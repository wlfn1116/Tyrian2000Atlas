using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's event pane: the level's whole script as a virtualized table, and a form for
/// the selected record that names every dat field for what the engine actually reads out of
/// it (see <see cref="EventCatalog"/>). Spawn events get an enemy picker with live thumbnails
/// from the edited table, and a jump back onto the map where the spawn lands.
/// </summary>
public sealed unsafe partial class App
{
    private int _evSelected = -1;
    private bool _evScrollTo;
    private int _evGroupFilter = -1;        // -1 = all, else (int)EventGroup
    private readonly byte[] _evFilter = new byte[48];
    private bool _evPickEnemy;              // enemy-picker popup requested this frame
    private readonly byte[] _evPickFilter = new byte[48];

    private const float EvRowH = 34f;

    private void DrawEventEditor(EditableEpisode ep, EditableLevel lv)
    {
        if (_evSelectOnce >= 0 && lv.Events.Count > 0)
        {
            _evSelected = Math.Min(_evSelectOnce, lv.Events.Count - 1);
            _evSelectOnce = -1;
            _evScrollTo = true;
        }
        DrawEventToolStrip(ep, lv);

        float formH = 300f;
        var avail = ImGui.GetContentRegionAvail();
        WellBegin("evtable", new Vector2(avail.X, Math.Max(120f, avail.Y - formH - 8f)), AcEdit);
        DrawEventTable(ep, lv);
        WellEnd();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginChild("evform", new Vector2(0, 0));
        DrawEventForm(ep, lv);
        ImGui.EndChild();
    }

    private void DrawEventToolStrip(EditableEpisode ep, EditableLevel lv)
    {
        BandBegin("evband", AcEdit);
        if (UiButton("+ Add", AcEdit, "Insert a new event after the selected one\n(same time, so it runs in the same batch).",
                0f, lv.Events.Count >= EditableLevel.MaxEvents))
        {
            var t = _evSelected >= 0 && _evSelected < lv.Events.Count
                ? lv.Events[_evSelected].Time : (ushort)(lv.Events.Count > 0 ? lv.Events[^1].Time : 30);
            int at = _evSelected >= 0 ? _evSelected + 1 : lv.Events.Count;
            lv.Events.Insert(at, new EventRec { Time = t, Type = 6, Dat = 25, Dat2 = -99 });
            _evSelected = at;
            _evScrollTo = true;
            NoteEventsChanged(ep);
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Duplicate", AcEdit, "", 0f, _evSelected < 0))
        {
            lv.Events.Insert(_evSelected + 1, lv.Events[_evSelected]);
            _evSelected++;
            _evScrollTo = true;
            NoteEventsChanged(ep);
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Delete", AcEnemy, "", 0f, _evSelected < 0))
        {
            lv.Events.RemoveAt(_evSelected);
            _evSelected = Math.Min(_evSelected, lv.Events.Count - 1);
            NoteEventsChanged(ep);
        }

        BandDivider();
        BandLabel("show");
        ImGui.SetNextItemWidth(150);
        string groupLabel = _evGroupFilter < 0 ? "every group" : EventCatalog.GroupName((EventGroup)_evGroupFilter);
        if (ImGui.BeginCombo("##evgroup", groupLabel))
        {
            if (ImGui.Selectable("every group", _evGroupFilter < 0)) _evGroupFilter = -1;
            foreach (EventGroup g in Enum.GetValues<EventGroup>())
                if (ImGui.Selectable(EventCatalog.GroupName(g), _evGroupFilter == (int)g))
                    _evGroupFilter = (int)g;
            ImGui.EndCombo();
        }
        ImGui.SameLine(0, 6);
        UiFilter("##evfilter", "time, type or text", _evFilter, 170f, AcEdit);

        BandDivider();
        BandNote($"{lv.Events.Count} events (engine cap {EditableLevel.MaxEvents})", UiFaint);
        BandEnd();
    }

    /// <summary>Everything that follows from the event list changing, in one place.</summary>
    private void NoteEventsChanged(EditableEpisode ep)
    {
        ep.LevelsDirty = true;
        _emObjects = null;
        _emTimeRuler = null;
        _emHealth = null;
    }

    /// <summary>Keep the list in the ascending-time order the engine's walker requires,
    /// without disturbing the relative order of same-time records. Both selections — the
    /// event tab's and the map canvas's — follow their record to its new index.</summary>
    private void SortEvents(EditableLevel lv)
    {
        bool sorted = true;
        for (int i = 1; i < lv.Events.Count && sorted; i++)
            sorted = lv.Events[i - 1].Time <= lv.Events[i].Time;
        if (sorted) return;

        var indexed = lv.Events.Select((e, i) => (e, i)).ToList();
        indexed.Sort((a, b) => a.e.Time != b.e.Time ? a.e.Time - b.e.Time : a.i - b.i);
        // old index -> new index, so every selection follows its record.
        var newOf = new int[indexed.Count];
        for (int i = 0; i < indexed.Count; i++) newOf[indexed[i].i] = i;
        for (int i = 0; i < indexed.Count; i++) lv.Events[i] = indexed[i].e;
        if (_evSelected >= 0 && _evSelected < newOf.Length)
        {
            _evSelected = newOf[_evSelected];
            _evScrollTo = true;
        }
        if (_emSelEvent >= 0 && _emSelEvent < newOf.Length) _emSelEvent = newOf[_emSelEvent];
        var remapped = _emSelSet.Where(i => i >= 0 && i < newOf.Length)
            .Select(i => newOf[i]).ToList();
        _emSelSet.Clear();
        foreach (int i in remapped) _emSelSet.Add(i);
    }

    private bool EventPassesFilter(EditableLevel lv, int i)
    {
        var ev = lv.Events[i];
        var info = EventCatalog.Get(ev.Type);
        if (_evGroupFilter >= 0 && (int)info.Group != _evGroupFilter) return false;
        string f = BufText(_evFilter).Trim();
        if (f.Length == 0) return true;
        return Matches(f, ev.Time.ToString(), ev.Type.ToString(), info.Name,
            EventCatalog.IsSpawnType(ev.Type) ? ev.Dat.ToString() : null);
    }

    private void DrawEventTable(EditableEpisode ep, EditableLevel lv)
    {
        // The filtered view keeps original indices so selection stays a list index.
        var shown = new List<int>(lv.Events.Count);
        for (int i = 0; i < lv.Events.Count; i++)
            if (EventPassesFilter(lv, i)) shown.Add(i);

        var edTable = _edEp!.Enemies;
        float viewH = ImGui.GetContentRegionAvail().Y;
        float scrollY = ImGui.GetScrollY();
        var top = ImGui.GetCursorScreenPos();

        if (_evScrollTo && _evSelected >= 0)
        {
            int at = shown.IndexOf(_evSelected);
            if (at >= 0) ImGui.SetScrollY(Math.Max(0f, at * EvRowH - viewH * 0.4f));
            _evScrollTo = false;
        }

        int first = Math.Max(0, (int)(scrollY / EvRowH) - 1);
        int last = Math.Min(shown.Count - 1, (int)((scrollY + viewH) / EvRowH) + 1);
        ImGui.Dummy(new Vector2(1, shown.Count * EvRowH));
        var dl = ImGui.GetWindowDrawList();

        var mousePos = ImGui.GetMousePos();
        for (int row = first; row <= last; row++)
        {
            int i = shown[row];
            var ev = lv.Events[i];
            var info = EventCatalog.Get(ev.Type);
            ImGui.SetCursorScreenPos(new Vector2(top.X, top.Y + row * EvRowH));
            var box = UiRow($"##ev{i}", i == _evSelected, AcEdit, EvRowH);

            // Every row carries a "map" chip at its right edge: one click lands on the Map
            // tab with the event centred. It shares the row's Selectable, so the hit is
            // resolved by where the click fell.
            var chipA = new Vector2(box.Max.X - 46f, box.Min.Y + (EvRowH - 3f) * 0.5f - 9f);
            var chipB = chipA + new Vector2(38f, 18f);
            bool chipHot = box.Hovered && mousePos.X >= chipA.X && mousePos.X < chipB.X &&
                           mousePos.Y >= chipA.Y && mousePos.Y < chipB.Y;
            dl.AddRectFilled(chipA, chipB, chipHot ? Shade(AcEdit, 0.55f, 235) : Gfx.Rgba(34, 38, 49, 200), 3f);
            dl.AddRect(chipA, chipB, chipHot ? Shade(AcEdit, 1f, 230) : UiLineSoft, 3f);
            dl.AddText(new Vector2(chipA.X + 8f, chipA.Y + 2f),
                chipHot ? Gfx.Rgba(250, 252, 255) : UiDim, "map");
            if (chipHot) ImGui.SetTooltip("show on the map, centred");

            if (box.Clicked)
            {
                if (chipHot) ShowEventOnMap(ep, lv, i);
                else _evSelected = i;
            }

            float x = box.Min.X + 10f;
            dl.AddText(new Vector2(x, box.Min.Y + (EvRowH - ImGui.GetTextLineHeight()) * 0.5f - 8f),
                UiDim, $"t {ev.Time}");
            dl.AddText(new Vector2(x, box.Min.Y + (EvRowH - ImGui.GetTextLineHeight()) * 0.5f + 5f),
                UiFaint, $"#{i + 1}");

            // A spawn row carries its enemy's face; every row carries name + a one-liner.
            float tx = x + 62f;
            if (EventCatalog.IsSpawnType(ev.Type))
            {
                var min = new Vector2(x + 54f, box.Min.Y + 2f);
                EditorSpawnThumb(dl, edTable, ev, min, min + new Vector2(30f, EvRowH - 7f));
                tx += 36f;
            }
            ClipText(dl, new Vector2(tx, box.Min.Y + 3f), box.Max.X - tx - 60f,
                i == _evSelected ? Gfx.Rgba(250, 252, 255) : UiText, $"{ev.Type}  {info.Name}");
            ClipText(dl, new Vector2(tx, box.Min.Y + 3f + ImGui.GetTextLineHeight() + 1f),
                box.Max.X - tx - 110f, Shade(AcEdit, 1f, 185),
                EventCatalog.IsSpawnType(ev.Type) ? SpawnRowNote(edTable, ev) : info.Summary);
            if (ev.Dat4 != 0 && !EventCatalog.IsSpawnType(ev.Type))
                ClipTextRight(dl, box.Max.X - 52f,
                    (box.Min.Y + box.Max.Y) * 0.5f - ImGui.GetTextLineHeight() * 0.5f,
                    70f, UiFaint, $"link {ev.Dat4}");
        }
        ImGui.SetCursorScreenPos(new Vector2(top.X, top.Y + shown.Count * EvRowH));
        if (shown.Count == 0)
            UiEmpty("no events match", "clear the filter, or add an event", AcEdit);
    }

    private static string SpawnRowNote(EnemyDat[] table, in EventRec ev)
    {
        string x = ev.Dat2 == -99 ? "default x" : ev.Dat2 == -200 ? "random x" : $"x {ev.Dat2}";
        string what;
        if (ev.Type is >= 49 and <= 52) what = $"sprite {ev.Dat} bank {ev.Dat3}";
        else if (ev.Type == 12) what = $"block {ev.Dat}..{ev.Dat + 3}";
        else what = $"enemy {ev.Dat}";
        return ev.Dat4 != 0 ? $"{what} · {x} · link {ev.Dat4}" : $"{what} · {x}";
    }

    // =====================================================================
    // Form
    // =====================================================================

    private void DrawEventForm(EditableEpisode ep, EditableLevel lv)
    {
        if (_evSelected < 0 || _evSelected >= lv.Events.Count)
        {
            UiEmpty("no event selected", "click a row above, or Ctrl+click a spawn marker on the map", AcEdit);
            return;
        }
        var ev = lv.Events[_evSelected];
        var info = EventCatalog.Get(ev.Type);
        bool changed = false;

        UiSection($"Event {_evSelected + 1}", AcEdit, EventCatalog.GroupName(info.Group));

        // --- time + type ---
        int time = ev.Time;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("time", ref time)) { ev.Time = (ushort)Math.Clamp(time, 0, 65499); changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "When the event fires, in event-time units.\nWhile the map scrolls at speed 1, one unit = one map pixel.");
        bool timeEditDone = ImGui.IsItemDeactivatedAfterEdit();

        ImGui.SameLine(0, 14);
        ImGui.SetNextItemWidth(280);
        if (ImGui.BeginCombo("type", $"{ev.Type}  {info.Name}"))
        {
            EventGroup? lastGroup = null;
            foreach (var e in EventCatalog.All)
            {
                if (e.Group != lastGroup)
                {
                    lastGroup = e.Group;
                    ImGui.SeparatorText(EventCatalog.GroupName(e.Group));
                }
                if (ImGui.Selectable($"{e.Type}  {e.Name}", e.Type == ev.Type))
                {
                    ev.Type = e.Type;
                    changed = true;
                }
                if (ImGui.IsItemHovered() && e.Summary.Length > 0) ImGui.SetTooltip(e.Summary);
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine(0, 12);
        if (UiButton("show on map", AcEdit,
                "Jump to the Map tab with this event centred and selected -\n" +
                "spawns at their marker, level-wide events at their line."))
            ShowEventOnMap(ep, lv, _evSelected);

        ImGui.TextDisabled(info.Summary);
        ImGui.Dummy(new Vector2(0, 4));

        // --- the six dat fields, named ---
        changed |= DatField("dat", info.Dat, ref ev.Dat);
        SpawnHelpers(ep, lv, ref ev, ref changed);
        changed |= DatField("dat2", info.Dat2, ref ev.Dat2);
        changed |= DatFieldS8("dat3", info.Dat3, ref ev.Dat3);
        changed |= DatFieldU8("dat4", info.Dat4, ref ev.Dat4);
        changed |= DatFieldS8("dat5", info.Dat5, ref ev.Dat5);
        changed |= DatFieldS8("dat6", info.Dat6, ref ev.Dat6);

        if (changed)
        {
            lv.Events[_evSelected] = ev;
            NoteEventsChanged(ep);
        }
        if (timeEditDone) SortEvents(lv);

        DrawEnemyPickPopup(ep, lv);
    }

    private bool DatField(string raw, EventField f, ref short value)
    {
        int v = value;
        ImGui.SetNextItemWidth(120);
        bool ch = ImGui.InputInt(f.Used ? $"{f.LabelText}##{raw}" : $"{raw} (unused)", ref v);
        if (ch) value = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        if (ImGui.IsItemHovered() && f.HintText.Length > 0) ImGui.SetTooltip(f.HintText);
        return ch;
    }

    private bool DatFieldS8(string raw, EventField f, ref sbyte value)
    {
        int v = value;
        ImGui.SetNextItemWidth(120);
        bool ch = ImGui.InputInt(f.Used ? $"{f.LabelText}##{raw}" : $"{raw} (unused)", ref v);
        if (ch) value = (sbyte)Math.Clamp(v, sbyte.MinValue, sbyte.MaxValue);
        if (ImGui.IsItemHovered() && f.HintText.Length > 0) ImGui.SetTooltip(f.HintText);
        return ch;
    }

    private bool DatFieldU8(string raw, EventField f, ref byte value)
    {
        int v = value;
        ImGui.SetNextItemWidth(120);
        bool ch = ImGui.InputInt(f.Used ? $"{f.LabelText}##{raw}" : $"{raw} (unused)", ref v);
        if (ch) value = (byte)Math.Clamp(v, 0, 255);
        if (ImGui.IsItemHovered() && f.HintText.Length > 0) ImGui.SetTooltip(f.HintText);
        return ch;
    }

    /// <summary>The conveniences a spawn event earns: enemy picker, X presets, map jump.</summary>
    private void SpawnHelpers(EditableEpisode ep, EditableLevel lv, ref EventRec ev, ref bool changed)
    {
        if (!EventCatalog.IsSpawnType(ev.Type)) return;

        ImGui.SameLine(0, 10);
        bool custom = ev.Type is >= 49 and <= 52;
        if (!custom && UiButton("pick enemy...", AcEdit,
                "Browse the enemy table with thumbnails.", 0f))
            _evPickEnemy = true;

        // The thumbnail of what this spawn creates, drawn from the edited table.
        ImGui.SameLine(0, 10);
        var at = ImGui.GetCursorScreenPos();
        EditorSpawnThumb(ImGui.GetWindowDrawList(), _edEp!.Enemies, ev,
            at, at + new Vector2(44, ImGui.GetFrameHeight() + 6));
        ImGui.Dummy(new Vector2(46, ImGui.GetFrameHeight()));

        ImGui.SameLine(0, 10);
        bool defX = ev.Dat2 == -99, rndX = ev.Dat2 == -200;
        if (UiToggle("default x", ref defX, AcEdit,
                "-99: use the enemy entry's own start position."))
        {
            ev.Dat2 = (short)(defX ? -99 : 120);
            changed = true;
        }
        ImGui.SameLine(0, 4);
        if (UiToggle("random x", ref rndX, AcEdit, "-200: one random X in 24..231."))
        {
            ev.Dat2 = (short)(rndX ? -200 : 120);
            changed = true;
        }
    }

    // =====================================================================
    // Enemy picker popup
    // =====================================================================

    private void DrawEnemyPickPopup(EditableEpisode ep, EditableLevel lv)
    {
        if (_evPickEnemy) { ImGui.OpenPopup("pick an enemy"); _evPickEnemy = false; }
        ImGui.SetNextWindowSize(new Vector2(430, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("pick an enemy")) return;

        UiFilter("##evpickf", "id, bank, armor...", _evPickFilter, 240f, AcEdit, focus: false);
        ImGui.SameLine(0, 8);
        ImGui.TextDisabled("click a row to use it");
        ImGui.Separator();

        ImGui.BeginChild("evpicklist");
        string f = BufText(_evPickFilter).Trim();
        var table = ep.Enemies;
        foreach (int id in EnumerateEnemyIds(table, f, null))
        {
            if (!EnemyListRow(table, id, selected: false)) continue;
            if (_evSelected >= 0 && _evSelected < lv.Events.Count)
            {
                var ev = lv.Events[_evSelected];
                ev.Dat = (short)id;
                lv.Events[_evSelected] = ev;
                NoteEventsChanged(ep);
            }
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }
}
