using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's enemy pane: the full enemyDat table (both banks) with every field editable —
/// graphics, movement, turrets, launches, armor and worth — an animated preview that plays
/// the entry the way the engine animates it, and a per-frame sprite picker over the entry's
/// own shape bank. Also home to the thumbnail helpers the other editor panes borrow, which
/// read the EDITED table rather than the episode's cached one.
/// </summary>
public sealed unsafe partial class App
{
    private int _eeSelected = 25;           // enemyDat id being edited
    private readonly byte[] _eeFilter = new byte[48];
    private bool _eeOnlyUsed;               // list only ids the current level spawns
    private double _eeClock;                // preview animation clock (35 Hz)
    private int _eePickFrame = -1;          // EGraphic slot a sprite picker is open for
    private readonly byte[] _eeCopyBuf = new byte[16];
    private bool _eeScrollTo;

    // =====================================================================
    // Thumbnails over the edited table (shared with the other panes)
    // =====================================================================

    /// <summary>
    /// True when a row of this height at the cursor would be on screen; otherwise its space
    /// is consumed by a spacing-free dummy. The enemy lists hold hundreds of thumbnail rows
    /// across dozens of sprite banks — drawn unculled they blow straight through the atlas
    /// cache every frame, evicting sheets the same frame is still drawing from.
    /// </summary>
    private static bool RowVisible(float height)
    {
        float y = ImGui.GetCursorScreenPos().Y;
        var wp = ImGui.GetWindowPos();
        if (y + height >= wp.Y - 30 && y <= wp.Y + ImGui.GetWindowSize().Y + 30) return true;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
            new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        ImGui.Dummy(new Vector2(1, height));
        ImGui.PopStyleVar();
        return false;
    }

    /// <summary>
    /// One enemy list row — thumbnail, id, the facts — shared by the spawn palette and the
    /// event picker so every list in the editor reads identically. Skips itself (consuming
    /// its space) when scrolled out of view. Returns clicked.
    /// </summary>
    private bool EnemyListRow(EnemyDat[] table, int id, bool selected)
    {
        if (!RowVisible(38f)) return false;
        var d = table[id];
        var box = UiRow($"##enr{id}", selected, AcEdit, 38f);
        EditorEnemyThumb(ImGui.GetWindowDrawList(), table, id, box.Min + new Vector2(6, 2),
            new Vector2(box.Min.X + 42, box.Max.Y - 2));
        RowText(box, 50f, $"{id}",
            $"bank {d.ShapeBank} · armor {d.Armor} · value {d.Value}" +
            (d.Esize == 1 ? " · 2x2" : "") + (d.IsGround ? " · ground" : " · air"),
            AcEdit, selected);
        return box.Clicked;
    }

    /// <summary>One enemy of the edited table fitted into a box.</summary>
    private void EditorEnemyThumb(ImDrawListPtr dl, EnemyDat[] table, int id,
        Vector2 boxMin, Vector2 boxMax) =>
        EditorEnemyThumbTinted(dl, table, id, boxMin, boxMax, 255);

    /// <summary>The same at an alpha, for ghosted previews on the map canvas.</summary>
    private void EditorEnemyThumbTinted(ImDrawListPtr dl, EnemyDat[] table, int id,
        Vector2 boxMin, Vector2 boxMax, byte alpha)
    {
        if (id < 0 || id >= table.Length) return;
        var d = table[id];
        if (!d.Loaded || d.EGraphic == null || d.EGraphic[0] == 0 || d.EGraphic[0] == 999) return;
        var atlas = Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette);
        if (atlas == null) return;
        var (ox, oy, w, h) = EnemyFrameBox(atlas, d.EGraphic[0], d.Esize == 1);
        float scale = Math.Min(1.6f, Math.Min((boxMax.X - boxMin.X) / w, (boxMax.Y - boxMin.Y) / h));
        var anchor = new Vector2(
            MathF.Round((boxMin.X + boxMax.X) * 0.5f - (ox + w * 0.5f) * scale),
            MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - (oy + h * 0.5f) * scale));
        DrawEnemyFrame(dl, atlas, d.EGraphic[0], d.Esize == 1, anchor, scale,
            Gfx.Rgba(255, 255, 255, alpha));
    }

    /// <summary>What a spawn event will create, edited-table edition. Events 49-52 carry
    /// their own art; event 12 shows its 4-entry block base.</summary>
    private void EditorSpawnThumb(ImDrawListPtr dl, EnemyDat[] table, in EventRec ev,
        Vector2 boxMin, Vector2 boxMax)
    {
        if (ev.Type is >= 49 and <= 52)
        {
            var atlas = Atlas(EnemySpriteSource(Math.Max(0, (int)ev.Dat3)), AppSettings.GamePalette);
            if (atlas == null || ev.Dat <= 0) return;
            bool big = table.Length > 0 && table[0].Esize == 1;   // scratch entry 0 sets the form
            var (ox, oy, w, h) = EnemyFrameBox(atlas, ev.Dat, big);
            float scale = Math.Min(1.6f, Math.Min((boxMax.X - boxMin.X) / w, (boxMax.Y - boxMin.Y) / h));
            var anchor = new Vector2(
                MathF.Round((boxMin.X + boxMax.X) * 0.5f - (ox + w * 0.5f) * scale),
                MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - (oy + h * 0.5f) * scale));
            DrawEnemyFrame(dl, atlas, ev.Dat, big, anchor, scale);
            return;
        }
        EditorEnemyThumb(dl, table, ev.Dat, boxMin, boxMax);
    }

    // =====================================================================
    // List
    // =====================================================================

    private void DrawEnemyEditorList(EditableEpisode ep)
    {
        UiFilter("##eefilter", "id, bank, armor...", _eeFilter, ImGui.GetContentRegionAvail().X - 60f, AcEdit);
        ImGui.SameLine(0, 5);
        UiToggle("used", ref _eeOnlyUsed, AcEdit,
            "Only entries the current level's events actually spawn.");

        var usedIds = _eeOnlyUsed ? CollectUsedEnemyIds(ep) : null;
        string f = BufText(_eeFilter).Trim();
        var table = ep.Enemies;
        var dl = ImGui.GetWindowDrawList();

        ImGui.BeginChild("eerows");
        int bankBreak = -1;
        for (int id = 0; id < table.Length; id++)
        {
            if (id is > 850 and < 1001) continue;   // the gap between the two banks
            var d = table[id];
            bool empty = !d.Loaded || d.EGraphic == null ||
                         (d.EGraphic[0] == 0 && d.Armor == 0 && d.Value == 0);
            if (usedIds != null && !usedIds.Contains(id)) continue;
            if (f.Length > 0)
            {
                if (!Matches(f, id.ToString(), d.ShapeBank.ToString(), d.Armor.ToString(),
                        d.Value.ToString())) continue;
            }
            else if (empty && id != _eeSelected && usedIds == null)
                continue;   // unfiltered browsing skips the hundreds of blank slots

            int bank = id <= 850 ? 1 : 2;
            if (bank != bankBreak)
            {
                bankBreak = bank;
                UiSection(bank == 1 ? "Bank 1 · ids 0-850" : "Bank 2 · ids 1001-1850", AcEdit);
            }

            if (_eeScrollTo && id == _eeSelected)
            {
                ImGui.SetScrollHereY(0.4f);
                _eeScrollTo = false;
            }
            if (!RowVisible(38f)) continue;
            var box = UiRow($"##ee{id}", id == _eeSelected, AcEdit, 38f);
            EditorEnemyThumb(dl, table, id, box.Min + new Vector2(6, 2),
                new Vector2(box.Min.X + 42, box.Max.Y - 2));
            RowText(box, 50f, $"{id}",
                empty ? "(empty slot)"
                      : $"bank {d.ShapeBank} · armor {d.Armor} · value {d.Value}" +
                        (d.Esize == 1 ? " · 2x2" : ""),
                AcEdit, box.Selected);
            if (box.Clicked) _eeSelected = id;
        }
        ImGui.EndChild();
    }

    /// <summary>Every enemyDat id the current level's events reference.</summary>
    private HashSet<int> CollectUsedEnemyIds(EditableEpisode ep)
    {
        var ids = new HashSet<int>();
        var lv = EditorLevel();
        if (lv == null) return ids;
        foreach (var ev in lv.Events)
        {
            if (!EventCatalog.IsSpawnType(ev.Type) || ev.Type is >= 49 and <= 52) continue;
            if (ev.Type == 12) for (int k = 0; k < 4; k++) ids.Add(ev.Dat + k);
            else ids.Add(ev.Dat);
        }
        foreach (var e in lv.LevelEnemy) ids.Add(e);
        return ids;
    }

    // =====================================================================
    // Detail
    // =====================================================================

    private void DrawEnemyEditorDetail(EditableEpisode ep)
    {
        var table = ep.Enemies;
        if (_eeSelected < 0 || _eeSelected >= table.Length ||
            (_eeSelected is > 850 and < 1001))
            _eeSelected = 25;
        ref var d = ref table[_eeSelected];
        if (d.EGraphic == null)
        {
            // Claiming an empty slot: give it a frame table so it can be edited at all.
            d.EGraphic = new ushort[20];
            d.Loaded = true;
        }
        bool changed = false;

        UiTitle($"Enemy {_eeSelected}", AcEdit,
            ep.SharedEnemyTable
                ? "shared table: episodes 1-3 all read this entry"
                : $"episode {ep.Number}'s own table");

        // --- jump / copy strip ---
        ImGui.SetNextItemWidth(90);
        int jump = _eeSelected;
        if (ImGui.InputInt("id##eejump", ref jump, 0))
        {
            if (jump != _eeSelected && jump >= 0 && jump < table.Length &&
                jump is <= 850 or >= 1001)
            {
                _eeSelected = jump;
                _eeScrollTo = true;
            }
        }
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(70);
        FilterBox("##eecopy", "id", _eeCopyBuf, 70f);
        ImGui.SameLine(0, 4);
        if (UiButton("copy from", AcEdit, "Overwrite this entry with another id's fields."))
        {
            if (int.TryParse(BufText(_eeCopyBuf).Trim(), out int src) &&
                src >= 0 && src < table.Length && table[src].Loaded)
            {
                var copy = table[src];
                copy.EGraphic = copy.EGraphic == null ? new ushort[20] : (ushort[])copy.EGraphic.Clone();
                table[_eeSelected] = copy;
                changed = true;
            }
        }
        ImGui.SameLine(0, 8);
        if (UiButton("clear", AcEnemy, "Zero the whole entry."))
        {
            table[_eeSelected] = new EnemyDat { EGraphic = new ushort[20], Loaded = true };
            changed = true;
        }

        ImGui.Dummy(new Vector2(0, 4));

        // --- two columns: preview | fields ---
        float previewW = 236f;
        WellBegin("eeprev", new Vector2(previewW, ImGui.GetContentRegionAvail().Y), AcEdit);
        DrawEnemyPreview(ref d);
        WellEnd();
        ImGui.SameLine(0, 8);
        ImGui.BeginChild("eefields");
        changed |= DrawEnemyFields(ep, ref d);
        ImGui.EndChild();

        if (changed)
        {
            ep.EnemiesDirty = true;
            _emObjects = null;   // spawn thumbnails/markers may show this entry
        }
    }

    private void DrawEnemyPreview(ref EnemyDat d)
    {
        _eeClock += ImGui.GetIO().DeltaTime * 35.0;
        var avail = ImGui.GetContentRegionAvail();
        float stageH = 170f;
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(avail.X, stageH), Gfx.Rgba(9, 10, 14), 5f);

        var atlas = Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette);
        int frames = Math.Max(1, (int)d.Ani);
        int frame = d.Animate == 0 ? 0 : (int)(_eeClock / 2) % frames;
        int gr = d.EGraphic != null && frame < d.EGraphic.Length ? d.EGraphic[frame] : 0;
        if (atlas != null && gr > 0 && gr != 999)
            DrawEnemyFrameCentered(dl, atlas, gr, d.Esize == 1,
                p, p + new Vector2(avail.X, stageH), 3f);
        else
            ClipText(dl, p + new Vector2(12, stageH * 0.5f - 7), avail.X - 24, UiFaint,
                gr == 999 ? "invisible (999)" : "no sprite");
        ImGui.Dummy(new Vector2(avail.X, stageH + 4));

        KV("frame", $"{frame + 1}/{frames}", 0, 76f);
        KV("animate", d.Animate switch { 0 => "0 static", 1 => "1 loop", 2 => "2 on fire", _ => d.Animate.ToString() }, 0, 76f);
        KV("size", d.Esize == 1 ? "2x2 metasprite" : "single sprite", 0, 76f);
        KV("kind", d.IsGround ? "ground (explodes as ground)" : "air", 0, 76f);
        string worth = d.Value switch
        {
            1 => "datacube",
            >= 10000 => $"powerup code {d.Value}",
            0 => "0 (decoration)",
            _ => $"{d.Value} pts/cash",
        };
        KV("worth", worth, 0, 76f);
    }

    private bool DrawEnemyFields(EditableEpisode ep, ref EnemyDat d)
    {
        bool ch = false;

        UiSection("Appearance", AcEdit);
        ch |= EeByte("shape bank", ref d.ShapeBank, 0, 36,
            "Sprite bank the frames index into. 21 = coins/gems sheet,\n26 = powerups sheet, else newsh bank 1-36. The level must\nload the bank with event 5 for the engine to draw it.");
        ch |= EeByte("size", ref d.Esize, 0, 1, "0 = single 12px sprite, 1 = 24x28 2x2 metasprite.");
        ch |= EeByte("ani frames", ref d.Ani, 0, 20, "How many EGraphic frames the animation runs.");
        ch |= EeByte("animate", ref d.Animate, 0, 2, "0 = static, 1 = loop, 2 = animate when firing.");

        // The 20-frame table, each cell a click away from a picker over the bank.
        ImGui.TextDisabled("frames (EGraphic 1-20; 999 = invisible)");
        var atlas = Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette);
        var dl = ImGui.GetWindowDrawList();
        for (int i = 0; i < 20; i++)
        {
            if (i % 10 != 0) ImGui.SameLine(0, 3);
            var at = ImGui.GetCursorScreenPos();
            var cell = new Vector2(26, 30);
            bool clicked = ImGui.InvisibleButton($"##eegr{i}", cell);
            bool hover = ImGui.IsItemHovered();
            dl.AddRectFilled(at, at + cell, Gfx.Rgba(20, 22, 29), 3f);
            dl.AddRect(at, at + cell, hover ? Shade(AcEdit, 1f, 220) : UiLineSoft, 3f);
            int gr = d.EGraphic![i];
            if (atlas != null && gr > 0 && gr != 999)
                DrawEnemyFrameCentered(dl, atlas, gr, d.Esize == 1, at, at + cell, 1f);
            else if (gr == 999)
                dl.AddText(at + new Vector2(3, 8), UiFaint, "inv");
            if (hover) ImGui.SetTooltip($"frame {i + 1}: sprite {gr}\nclick to pick from bank {d.ShapeBank}");
            if (clicked) _eePickFrame = i;
        }
        ch |= DrawFramePickPopup(ref d);

        ch |= EeUShort("damaged gfx", ref d.Dgr,
            "EGraphic base swapped in once armor falls to the threshold (0 = none).");
        ch |= EeSByte("damaged at", ref d.DLevel, "Armor threshold for the damaged form (-1 = never).");
        ch |= EeSByte("damaged ani", ref d.DAni, "Animation frames of the damaged form.");
        ch |= EeByte("explosion", ref d.ExplosionType, 0, 255,
            "Explosion animation index. Bit 0 clear = ground object\n(draws in the ground band's explosion pass).");

        UiSection("Movement", AcEdit);
        ch |= EeSByte("x move", ref d.XMove, "Velocity, px per frame.");
        ch |= EeSByte("y move", ref d.YMove, "");
        ch |= EeSByte("x accel", ref d.XAccel, "Plain acceleration.");
        ch |= EeSByte("y accel", ref d.YAccel, "");
        ch |= EeSByte("x cyclic accel", ref d.XCAccel, "Cyclic (waving) acceleration.");
        ch |= EeSByte("y cyclic accel", ref d.YCAccel, "");
        ch |= EeSByte("x reversal", ref d.XRev, "Cyclic accel reversal point (0 = none, 100 = immediate).");
        ch |= EeSByte("y reversal", ref d.YRev, "");
        ch |= EeShort("start x", ref d.StartX, "Default spawn position, used when the event says x = -99.");
        ch |= EeShort("start y", ref d.StartY, "");
        ch |= EeSByte("start x random", ref d.StartXC, "Random half-range around start x.");
        ch |= EeSByte("start y random", ref d.StartYC, "");

        UiSection("Combat", AcEdit);
        ch |= EeByte("armor", ref d.Armor, 0, 255, "0 = not shootable (pickup or decoration).");
        ch |= EeByte("turret 1 weapon", ref d.Tur0, 0, 255,
            "Enemy weapon id fired by turret 1 (0 = none; 251-255 = specials:\n251 suck-o-magnet, 252 savara missile, 253 short-range magnet,\n254 special, 255 magneto repulse).");
        ch |= EeByte("turret 1 freq", ref d.Freq0, 0, 255, "Frames between shots.");
        ch |= EeByte("turret 2 weapon", ref d.Tur1, 0, 255, "");
        ch |= EeByte("turret 2 freq", ref d.Freq1, 0, 255, "");
        ch |= EeByte("turret 3 weapon", ref d.Tur2, 0, 255, "");
        ch |= EeByte("turret 3 freq", ref d.Freq2, 0, 255, "");
        ch |= EeUShort("launch type", ref d.ELaunchType,
            "enemyDat id this enemy launches (a carrier's fighters,\na turret's debris). 0 = nothing.");
        ch |= EeByte("launch freq", ref d.ELaunchFreq, 0, 255, "Frames between launches.");
        ch |= EeUShort("on death spawn", ref d.EEnemyDie,
            "enemyDat id spawned when this one dies (0 = nothing).");
        ch |= EeShort("value", ref d.Value,
            "Score/cash when destroyed. 1 = datacube pickup;\n>= 10000 = powerup pickup code; 0 with 0 armor = decoration.");

        return ch;
    }

    private bool DrawFramePickPopup(ref EnemyDat d)
    {
        if (_eePickFrame >= 0) ImGui.OpenPopup("pick a sprite");
        ImGui.SetNextWindowSize(new Vector2(420, 500), ImGuiCond.Appearing);
        bool changed = false;
        if (!ImGui.BeginPopup("pick a sprite")) { if (_eePickFrame >= 0) _eePickFrame = -1; return false; }

        int slot = _eePickFrame;
        ImGui.TextDisabled(slot >= 0 ? $"frame {slot + 1} · bank {d.ShapeBank} · click a sprite" : "");
        if (UiButton("none (0)", AcEdit, "", 90f)) { if (slot >= 0) { d.EGraphic![slot] = 0; changed = true; } ImGui.CloseCurrentPopup(); }
        ImGui.SameLine(0, 6);
        if (UiButton("invisible (999)", AcEdit, "An active enemy with no sprite.", 120f))
        { if (slot >= 0) { d.EGraphic![slot] = 999; changed = true; } ImGui.CloseCurrentPopup(); }
        ImGui.Separator();

        var atlas = Atlas(EnemySpriteSource(d.ShapeBank), AppSettings.GamePalette);
        if (atlas == null) { ImGui.TextDisabled("bank not found"); ImGui.EndPopup(); return changed; }
        ImGui.BeginChild("eepicksheet");
        var dl = ImGui.GetWindowDrawList();
        float availW = ImGui.GetContentRegionAvail().X;
        bool big = d.Esize == 1;
        float cw = big ? 26 : 14, chh = big ? 30 : 16;
        int perRow = Math.Max(1, (int)(availW / (cw + 4)));
        var top = ImGui.GetCursorScreenPos();
        int count = atlas.Count;
        ImGui.Dummy(new Vector2(availW, ((count + perRow - 1) / perRow) * (chh + 4)));
        var mouse = ImGui.GetMousePos();
        float scrollY = ImGui.GetScrollY(), viewH = ImGui.GetWindowSize().Y;
        for (int i = 1; i < count; i++)
        {
            float x = ((i - 1) % perRow) * (cw + 4);
            float y = ((i - 1) / perRow) * (chh + 4);
            if (y < scrollY - 40 || y > scrollY + viewH + 20) continue;
            var a = top + new Vector2(x, y);
            var b = a + new Vector2(cw, chh);
            bool hot = mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y &&
                       ImGui.IsWindowHovered();
            dl.AddRectFilled(a, b, Gfx.Rgba(20, 22, 29));
            DrawEnemyFrameCentered(dl, atlas, i, big, a, b, 1f);
            if (slot >= 0 && d.EGraphic![slot] == i)
                dl.AddRect(a - new Vector2(1, 1), b + new Vector2(1, 1), Shade(AcEdit, 1.2f), 0, 0, 2f);
            else if (hot) dl.AddRect(a, b, Shade(AcEdit, 0.9f, 220));
            if (!hot) continue;
            ImGui.SetTooltip(big ? $"sprite {i} (2x2 uses {i}, {i + 1}, {i + 19}, {i + 20})" : $"sprite {i}");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && slot >= 0)
            {
                d.EGraphic![slot] = (ushort)i;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
        if (changed) _eePickFrame = -1;
        return changed;
    }

    // ---- field helpers (InputInt with clamping and a hint) ----

    private bool EeByte(string label, ref byte v, int min, int max, string tip)
    {
        int i = v;
        ImGui.SetNextItemWidth(110);
        bool ch = ImGui.InputInt(label, ref i);
        if (ch) v = (byte)Math.Clamp(i, min, max);
        if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);
        return ch;
    }

    private bool EeSByte(string label, ref sbyte v, string tip)
    {
        int i = v;
        ImGui.SetNextItemWidth(110);
        bool ch = ImGui.InputInt(label, ref i);
        if (ch) v = (sbyte)Math.Clamp(i, sbyte.MinValue, sbyte.MaxValue);
        if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);
        return ch;
    }

    private bool EeShort(string label, ref short v, string tip)
    {
        int i = v;
        ImGui.SetNextItemWidth(110);
        bool ch = ImGui.InputInt(label, ref i);
        if (ch) v = (short)Math.Clamp(i, short.MinValue, short.MaxValue);
        if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);
        return ch;
    }

    private bool EeUShort(string label, ref ushort v, string tip)
    {
        int i = v;
        ImGui.SetNextItemWidth(110);
        bool ch = ImGui.InputInt(label, ref i);
        if (ch) v = (ushort)Math.Clamp(i, 0, ushort.MaxValue);
        if (ImGui.IsItemHovered() && tip.Length > 0) ImGui.SetTooltip(tip);
        return ch;
    }
}
