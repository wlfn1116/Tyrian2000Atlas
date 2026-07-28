using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's map painter: the three background grids drawn 1:1 in the same 360x16800
/// bottom-aligned canvas space the atlas renders levels in, with free panning past every
/// edge — spawns live off-map too. Terrain is painted with tiles, multi-tile stamps grabbed
/// off the map, or a scatter brush; spawns are placed singly or as formations, dragged and
/// deleted directly on the canvas, and drawn at the exact zoom the terrain uses. A minimap
/// strip navigates the 16800px length; a screen-frame guide shows what the player will
/// actually see; a time ruler keeps the map-row/event-time arithmetic visible throughout.
/// </summary>
public sealed unsafe partial class App
{
    private int _emLayer;                 // 0 = BG1 (ground), 1 = BG2, 2 = BG3
    private int _emTool;                  // 0 paint, 1 erase, 2 pick, 3 fill, 4 rect, 5 spawn
    private float _emZoom = 1f;
    private bool _emGrid = true;
    private bool _emDimOthers = true;
    private bool _emMarkers = true;
    private bool _emGuide = true;         // screen-span lines + cursor screen frame
    private bool _emPalette = true;
    private int _emPaletteMode;           // 0 = tiles, 1 = spawns
    private bool _emSlots;
    private (int C, int R) _emRectStart = (-1, -1);
    private bool _emStroke;
    private List<PlacedObject>? _emObjects;
    private ObjectPlacer.LayerScroll? _emScrollInfo;
    private List<ScrollWalk.Seg>? _emTimeRuler;    // scroll segments
    private int _emHoverFlow = -1;                 // flow line under the cursor (event index)
    private int _emCtxTime = -1;                   // time an "add level event" menu opened at
    private bool _emCtxRequest;                    // open that menu this frame
    private bool _emZoomPending;                   // re-anchor the view after a zoom change
    private Vector2 _emZoomAnchorCanvas, _emZoomAnchorScreen;
    private float _emScrollToY = -1f;     // one-shot scroll request (canvas px)
    private bool _emCanvasScrolled;
    private float _emViewTopCv, _emViewHCv = 1f;   // the canvas view window, in canvas px

    /// <summary>How far past the map the canvas pans, in canvas px: enemies are routinely
    /// authored to enter from beyond the side edges, and a creator wants slack on every
    /// side, so the pannable space reaches well past the map.</summary>
    private const float EmMarginX = 520f, EmMarginY = 380f;

    /// <summary>One undo step: either a layer's cells+slots, or the whole event list —
    /// so Ctrl+Z walks back through painting and spawn edits alike, in order.</summary>
    private readonly record struct EmUndoStep(int Layer, byte[]? Cells, ushort[]? Slots,
        List<EventRec>? Events);
    private readonly List<EmUndoStep> _emUndo = new();

    // The brush: a stamp of 1-based tile ids, row-major; 1x1 for a plain tile. 0 entries
    // paint empty, so a stamp lifted off the map pastes its gaps too.
    private int _emStampW = 1, _emStampH = 1;
    private int[] _emStamp = { 1 };
    private (int C, int R) _emPickStart = (-1, -1);   // stamp grab in progress
    private bool _emScatter;              // paint random picks from the stamp pool
    private int _emScatterPct = 35;
    private readonly Random _emRng = new(12345);

    // The spawn brush and the marker selection.
    private int _emSpawnEnemy = 25;
    private int _emSpawnBand;             // 0 auto, 1 sky, 2 ground, 3 top, 4 ground2
    private bool _emSpawnBottom;          // enter from the bottom edge instead of the top
    private bool _emBankOnly = true;      // palette lists only banks the level loads
    private readonly byte[] _emSpawnFilter = new byte[48];
    private int _emFormation;             // 0 single, 1 row, 2 column, 3 wedge
    private int _emFormCount = 4;
    private int _emFormSpacing = 28;
    private int _emFormStagger = 8;
    private int _emSelEvent = -1;         // primary selected spawn (event index, -1 = none)
    private readonly HashSet<int> _emSelSet = new();   // the whole selection
    private int _emDragEvent = -1;        // marker drag in progress (the grabbed one)
    private Vector2 _emDragStartMouse;
    private Dictionary<int, (EventRec Ev, double Scroll)>? _emDragOrigs;  // per-member origins
    private bool _emPressEmpty;           // LMB went down on empty space: click places,
    private Vector2 _emPressPos;          //   a drag past the threshold opens a marquee
    private bool _emMarqueeLive;
    private bool _emPaths = true;         // draw flight-path previews
    private List<(string Text, int EventIndex)>? _emHealth;   // level warnings (null = stale)
    private readonly List<int> _emRecentTiles = new();
    private readonly List<int> _emRecentEnemies = new();
    private float _emLastCanvasW = 800f;

    private const int EmMaxUndo = 48;

    private void SelectOnly(int eventIndex)
    {
        _emSelSet.Clear();
        if (eventIndex >= 0) _emSelSet.Add(eventIndex);
        _emSelEvent = eventIndex;
    }

    private void NoteRecentTile(int id)
    {
        _emRecentTiles.Remove(id);
        _emRecentTiles.Insert(0, id);
        if (_emRecentTiles.Count > 8) _emRecentTiles.RemoveAt(_emRecentTiles.Count - 1);
    }

    private void NoteRecentEnemy(int id)
    {
        _emRecentEnemies.Remove(id);
        _emRecentEnemies.Insert(0, id);
        if (_emRecentEnemies.Count > 6) _emRecentEnemies.RemoveAt(_emRecentEnemies.Count - 1);
    }

    // =====================================================================
    // Entry
    // =====================================================================

    private void DrawMapEditor(EditableEpisode ep, EditableLevel lv)
    {
        DrawMapToolStrip(ep, lv);

        float paletteW = _emPalette ? 252f : 0f;
        const float miniW = 58f;
        var avail = ImGui.GetContentRegionAvail();
        float canvasW = avail.X - paletteW - (paletteW > 0 ? 6f : 0f) - miniW - 6f;
        DrawMapCanvas(ep, lv, new Vector2(canvasW, avail.Y));
        ImGui.SameLine(0, 6);
        DrawMiniMap(ep, lv, new Vector2(miniW, avail.Y));
        if (_emPalette)
        {
            ImGui.SameLine(0, 6);
            WellBegin("empal", new Vector2(paletteW, avail.Y), AcEdit);
            if (_emPaletteMode == 1) DrawSpawnPalette(ep, lv);
            else if (_emSlots) DrawSlotEditor(ep, lv);
            else DrawTilePalette(ep, lv);
            WellEnd();
        }
    }

    private void DrawMapToolStrip(EditableEpisode ep, EditableLevel lv)
    {
        BandBegin("emband", AcEdit);
        SegBar("##emlayer", ref _emLayer, AcEdit, 172f,
            ("BG1", "Ground layer: 14x300 cells, the gameplay length of the level.  (key 1)"),
            ("BG2", "Middle layer: 14x600 cells, scrolls faster, blended over BG1.  (key 2)"),
            ("BG3", "Cloud layer: 15x600 cells, fastest scroll.  (key 3)"));

        BandDivider();
        int toolBefore = _emTool;
        SegBar("##emtool", ref _emTool, AcEdit, 344f,
            ("Paint", "Left-drag paints the brush.  (B)\nRight-click picks a tile; right-DRAG grabs a multi-tile stamp."),
            ("Erase", "Left-drag clears cells to empty.  (E)"),
            ("Pick", "Left-click reads a tile into the brush; drag grabs a stamp.  (I)"),
            ("Fill", "Flood-fills the connected region of identical cells.  (G)"),
            ("Rect", "Left-drag fills a rectangle, tiling the current stamp.  (M)"),
            ("Spawn", "Place and move enemies directly on the map.  (S)\n" +
                      "Click = place the spawn brush · drag a marker = move it\n" +
                      "Delete = remove · double-click = open in the event tab"));
        if (_emTool == 5 && toolBefore != 5) { _emPalette = true; _emPaletteMode = 1; }
        if (_emTool != 5 && toolBefore == 5) _emPaletteMode = 0;

        BandDivider();
        BandLabel("zoom");
        ImGui.SetNextItemWidth(100);
        ImGui.SliderFloat("##emzoom", ref _emZoom, 0.25f, 4f, "%.2fx");
        SliderReset(ref _emZoom, 1f, "Ctrl+wheel over the map does this too.");

        BandDivider();
        // The five view switches live behind one button: they are set-and-forget, and five
        // chips were the widest thing on the strip.
        if (UiButton("view...", AcEdit, "Grid, layer dimming, spawn markers, the screen\nframe and the side panel."))
            ImGui.OpenPopup("##emview");
        if (ImGui.BeginPopup("##emview"))
        {
            ImGui.Checkbox("cell grid", ref _emGrid);
            ImGui.Checkbox("dim other layers", ref _emDimOthers);
            if (ImGui.Checkbox("spawn markers", ref _emMarkers)) _emObjects = null;
            ImGui.Checkbox("flight paths", ref _emPaths);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The curve a selected or hovered spawn will fly, computed\n" +
                                 "from the engine's own movement rules - chasers curve, wavers\n" +
                                 "wave, sky drifters slide up the map. Dots mark seconds.");
            ImGui.Checkbox("screen frame", ref _emGuide);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The on-screen span lines, plus a screen-sized frame under\n" +
                                 "the cursor - what the player sees when that row is at the top.");
            ImGui.Checkbox("side panel", ref _emPalette);
            ImGui.EndPopup();
        }

        BandDivider();
        var health = EnsureHealth(ep, lv);
        if (UiButton(health.Count == 0 ? "check: ok" : $"check: {health.Count}",
                health.Count == 0 ? AcGo : AcSim,
                "Live level checks: does it end, will every spawn be visible in game,\n" +
                "is anything unreachable. Click a finding to jump to it."))
            ImGui.OpenPopup("##emhealth");
        if (ImGui.BeginPopup("##emhealth"))
        {
            if (health.Count == 0) ImGui.TextDisabled("nothing to complain about");
            foreach (var (text, evIdx) in health)
            {
                if (ImGui.Selectable(text) && evIdx >= 0) OpenEventInTab(evIdx);
                if (evIdx >= 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip("click: open the first offending event");
            }
            ImGui.EndPopup();
        }

        BandDivider();
        string brush = _emTool == 5
            ? $"spawn: enemy {_emSpawnEnemy}" + (_emFormation > 0 ? $" x{_emFormCount}" : "")
            : _emStampW * _emStampH > 1 ? $"stamp {_emStampW}x{_emStampH}"
            : $"tile {_emStamp[0]}";
        if (_emScatter && _emTool is 0 or 4) brush += $" · scatter {_emScatterPct}%";
        BandNote($"{brush}   ·   slots {lv.SlotsUsed(_emLayer)}/{EditableLevel.SlotLimit(_emLayer)}",
            UiFaint);
        BandEnd();
    }

    // =====================================================================
    // Canvas
    // =====================================================================

    private static int EmLayerYOff(int layer) =>
        LevelRenderer.CanvasH - Level.RowsFor(layer) * ShapeTable.TileH;

    /// <summary>Snap to the pixel grid's half-offsets, where a 1px line is 1px. Hairlines
    /// at fractional screen coordinates get feathered across two pixels by the AA rasterizer
    /// — the "distorted lines" a zoomed, scrolled canvas showed everywhere.</summary>
    private static float Px(float v) => MathF.Floor(v) + 0.5f;
    private static Vector2 Px(Vector2 v) => new(MathF.Floor(v.X) + 0.5f, MathF.Floor(v.Y) + 0.5f);

    private void DrawMapCanvas(EditableEpisode ep, EditableLevel lv, Vector2 size)
    {
        float z = _emZoom;
        var content = new Vector2((LevelRenderer.CanvasW + EmMarginX * 2) * z,
                                  (LevelRenderer.CanvasH + EmMarginY * 2) * z);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, UiSunken);
        ImGui.BeginChild("emcanvas", size, ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        if (_emScrollToY >= 0f)
        {
            ImGui.SetScrollY(Math.Max(0f, (_emScrollToY + EmMarginY) * z - size.Y * 0.5f));
            _emScrollToY = -1f;
        }
        else if (!_emCanvasScrolled && ImGui.GetScrollMaxY() > 0f)
        {
            // First sight of a level: open on its bottom edge, where play begins — the map's
            // bottom at the view's bottom, not the margin below it — and centered sideways.
            ImGui.SetScrollY(Math.Max(0f, (LevelRenderer.CanvasH + EmMarginY) * z - size.Y + 8f));
            ImGui.SetScrollX(Math.Max(0f, (content.X - size.X) * 0.5f));
            _emCanvasScrolled = true;
        }

        _emLastCanvasW = size.X;
        var topLeft = ImGui.GetCursorScreenPos();
        // A zoom changed last frame: put the anchored canvas point back under the anchored
        // screen point. Deferred one frame on purpose — a SetScroll issued the moment the
        // zoom changes is clamped against the OLD zoom's content size, which is what used
        // to fling the view somewhere else entirely.
        if (_emZoomPending)
        {
            _emZoomPending = false;
            var desired = _emZoomAnchorScreen -
                (_emZoomAnchorCanvas + new Vector2(EmMarginX, EmMarginY)) * z;
            ImGui.SetScrollX(ImGui.GetScrollX() + (topLeft.X - desired.X));
            ImGui.SetScrollY(ImGui.GetScrollY() + (topLeft.Y - desired.Y));
            topLeft = desired;
        }
        ImGui.Dummy(content);
        var origin = topLeft + new Vector2(EmMarginX, EmMarginY) * z;   // canvas (0,0)
        var dl = ImGui.GetWindowDrawList();

        float viewTop = ImGui.GetScrollY();
        float viewH = size.Y;
        _emViewTopCv = viewTop / z - EmMarginY;
        _emViewHCv = viewH / z;

        // The map's own footprint, set apart from the off-map margin space.
        dl.AddRectFilled(origin,
            origin + new Vector2(LevelRenderer.CanvasW, LevelRenderer.CanvasH) * z,
            Gfx.Rgba(16, 18, 24));

        // ---- layers, inactive first (dimmed), active last and full ----
        var atlas = Atlas(SpriteSource.Tiles(char.ToLowerInvariant(lv.ShapeChar)), _palette);
        if (atlas != null)
        {
            Span<int> order = stackalloc int[3];
            int n = 0;
            for (int l = 2; l >= 0; l--) if (l != _emLayer) order[n++] = l;
            order[n] = _emLayer;
            for (int k = 0; k < 3; k++)
            {
                int layer = order[k];
                uint tint = layer == _emLayer ? 0xFFFFFFFFu
                    : _emDimOthers ? Gfx.Rgba(255, 255, 255, 70) : 0xFFFFFFFFu;
                DrawMapLayer(dl, lv, atlas, layer, origin, z, viewTop - (origin.Y - topLeft.Y),
                    viewH, tint);
            }
        }

        // ---- layer extent outline + grid on the active layer ----
        int cols = Level.ColsFor(_emLayer), rows = Level.RowsFor(_emLayer);
        float yOff = EmLayerYOff(_emLayer) * z;
        var extMin = Px(origin + new Vector2(0, yOff));
        var extMax = Px(origin + new Vector2(cols * ShapeTable.TileW * z,
            yOff + rows * ShapeTable.TileH * z));
        dl.AddRect(extMin, extMax, Shade(AcEdit, 0.65f, 140));
        if (_emGrid && z >= 0.7f)
            DrawMapGrid(dl, origin, cols, rows, yOff, z,
                viewTop - (origin.Y - topLeft.Y), viewH);

        if (_emGuide) DrawScreenGuide(dl, lv, origin, z, viewTop, viewH);
        DrawTimeRuler(dl, lv, origin, z, viewTop, viewH);
        DrawFlowLines(lv, dl, origin, z);
        if (_emMarkers || _emTool == 5) DrawSpawnMarkers(ep, lv, dl, origin, z);

        HandleMapMouse(ep, lv, origin, z, size);
        DrawAddEventMenu(ep, lv);

        var winPos = ImGui.GetWindowPos();
        UiHint(ImGui.GetForegroundDrawList(),
            new Vector2(winPos.X + 8, winPos.Y + size.Y - 26),
            _emTool == 5
                ? "click place · drag move · drag empty = select box · ctrl+D duplicate · Delete · P play here"
                : "space+drag pan · shift+wheel sideways · ctrl+wheel zoom · right-drag stamp · P play here · ctrl+Z undo",
            AcEdit);

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Level-wide events — scroll speeds, map stops, the end, jumps, songs, filters — drawn
    /// as labelled lines across the map at the moment they fire (where the screen top is at
    /// that time). In the Spawn tool they are live: click selects (so Delete and the arrows
    /// work), drag re-times, double-click opens the record.
    /// </summary>
    private void DrawFlowLines(EditableLevel lv, ImDrawListPtr dl, Vector2 origin, float z)
    {
        _emHoverFlow = -1;
        var win = ImGui.GetWindowPos();
        float winBot = win.Y + ImGui.GetWindowSize().Y;
        var mouse = ImGui.GetMousePos();
        bool interactive = _emTool == 5 && ImGui.IsWindowHovered();
        float x0 = origin.X, x1 = origin.X + LevelRenderer.CanvasW * z;
        float lastLabelY = float.MinValue, labelX = 0f;

        for (int i = 0; i < lv.Events.Count; i++)
        {
            var ev = lv.Events[i];
            string? label = EventCatalog.FlowLabel(ev);
            if (label == null) continue;
            float y = origin.Y + (float)(ObjectPlacer.YBase - TimeToScroll(lv, ev.Time)) * z;
            if (y < win.Y - 24 || y > winBot + 24) continue;

            bool sel = _emSelSet.Contains(i);
            bool hot = interactive && _emDragEvent < 0 && !_emPressEmpty &&
                       Math.Abs(mouse.Y - y) < 5f && mouse.X >= x0 && mouse.X <= x1;
            if (hot && _emHoverFlow < 0) _emHoverFlow = i;

            uint col = ev.Type == 11 ? AcEnemy : AcRoutes;
            byte alpha = sel ? (byte)235 : hot ? (byte)205 : (byte)120;
            float py = Px(y);
            dl.AddLine(new Vector2(x0, py), new Vector2(x1, py), Shade(col, 1f, alpha),
                sel || hot ? 2f : 1f);

            // Labels stack sideways when several events share a row.
            if (Math.Abs(y - lastLabelY) > 3f) { lastLabelY = y; labelX = x0 + 4f; }
            var sz = BadgeAt(dl, new Vector2(labelX, py - 17f), label, col, sel || hot ? 1f : 0.72f);
            labelX += sz.X + 4f;

            if (hot)
                ImGui.SetTooltip($"t {ev.Time}  ·  {EventCatalog.Get(ev.Type).Name}\n" +
                    "click select · drag re-time · double-click open · Delete removes");
        }
    }

    /// <summary>Right-click on the map (Spawn tool): drop a level-wide event at that time.</summary>
    private void DrawAddEventMenu(EditableEpisode ep, EditableLevel lv)
    {
        if (_emCtxRequest) { ImGui.OpenPopup("##emaddflow"); _emCtxRequest = false; }
        if (!ImGui.BeginPopup("##emaddflow")) return;

        ImGui.TextDisabled($"add at t {_emCtxTime}");
        ImGui.Separator();
        void Add(string label, EventRec ev, string tip = "")
        {
            if (ImGui.Selectable(label))
            {
                PushEventsUndo(lv);
                ev.Time = (ushort)Math.Clamp(_emCtxTime, 1, 65499);
                int at = 0;
                while (at < lv.Events.Count && lv.Events[at].Time <= ev.Time) at++;
                lv.Events.Insert(at, ev);
                SelectOnly(at);
                _evSelected = at;
                NoteEventsChanged(ep);
                _edStatus = $"added {EventCatalog.Get(ev.Type).Name} at t {ev.Time}";
                _emCtxTime = -1;
            }
            if (tip.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }

        Add("Scroll speeds...", new EventRec { Type = 2, Dat = 1, Dat2 = 2, Dat3 = 3 },
            "Set BG1/BG2/BG3 speeds (drag the line, edit values in the event tab).");
        Add("Slow scroll", new EventRec { Type = 3 });
        Add("Map stop (wait for enemies)", new EventRec { Type = 4, Dat = 1 },
            "Stops the map until the armed band has no enemies left.");
        Add("Starfield off", new EventRec { Type = 8 });
        Add("Starfield on", new EventRec { Type = 9 });
        Add("Play song...", new EventRec { Type = 35, Dat = 1 });
        Add("Screen filter...", new EventRec { Type = 44, Dat = 1, Dat2 = -99, Dat3 = 0 });
        Add("Loop back...", new EventRec { Type = 38, Dat = (short)Math.Max(1, _emCtxTime - 400) },
            "Jump the event clock backwards - the bombardment loop.");
        ImGui.Separator();
        Add("Ready to end", new EventRec { Type = 36 },
            "Arms the ending: the level finishes once the screen is clear.");
        Add("END LEVEL", new EventRec { Type = 11 });
        ImGui.EndPopup();
    }

    /// <summary>The playfield the player sees: span lines down the level, and a full screen
    /// frame anchored where the cursor row would be the top of the screen.</summary>
    private void DrawScreenGuide(ImDrawListPtr dl, EditableLevel lv, Vector2 origin, float z,
        float viewTop, float viewH)
    {
        int pw = _engaged ? GameSim.EngagedViewW : GameSim.ViewW;
        float x0 = Px(origin.X + 48f * z), x1 = Px(origin.X + (48f + pw) * z);
        var win = ImGui.GetWindowPos();
        float yA = win.Y, yB = win.Y + viewH;
        uint col = Shade(AcPlayer, 0.75f, 70);
        dl.AddLine(new Vector2(x0, yA), new Vector2(x0, yB), col);
        dl.AddLine(new Vector2(x1, yA), new Vector2(x1, yB), col);

        if (!ImGui.IsWindowHovered()) return;
        var mouse = (ImGui.GetMousePos() - origin) / z;
        var a = new Vector2(x0, Px(ImGui.GetMousePos().Y));
        var b = new Vector2(x1, Px(a.Y + GameSim.ViewH * z));
        dl.AddRect(a, b, Shade(AcPlayer, 0.9f, 150));
        dl.AddRectFilled(a, b, Shade(AcPlayer, 0.4f, 14));
        int t = CanvasYToTime(lv, mouse.Y);
        if (t >= 0)
            dl.AddText(new Vector2(x0 + 4, a.Y + 3), Shade(AcPlayer, 1.05f, 190),
                $"screen at t{t}");
    }

    private void DrawMapLayer(ImDrawListPtr dl, EditableLevel lv, SpriteAtlas atlas, int layer,
        Vector2 origin, float z, float viewTopLocal, float viewH, uint tint)
    {
        int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
        float yOff = EmLayerYOff(layer) * z;
        float cellH = ShapeTable.TileH * z, cellW = ShapeTable.TileW * z;
        int r0 = Math.Max(0, (int)((viewTopLocal - yOff) / cellH) - 1);
        int r1 = Math.Min(rows - 1, (int)((viewTopLocal + viewH - yOff) / cellH) + 1);
        byte[] cells = lv.Cells(layer);
        for (int r = r0; r <= r1; r++)
        {
            int rowBase = r * cols;
            float y = yOff + r * cellH;
            for (int c = 0; c < cols; c++)
            {
                int sid = ResolveEditShape(lv, layer, cells[rowBase + c]);
                if (sid <= 0) continue;
                atlas.Draw(dl, sid, origin + new Vector2(c * cellW, y), z, tint);
            }
        }
    }

    /// <summary>The engine's reserved-cell rules, over the editable grids.</summary>
    private static int ResolveEditShape(EditableLevel lv, int layer, byte cell)
    {
        if (layer == 1 && cell == 71) return 0;
        if (layer == 2 && cell >= 70) return 0;
        if (cell > 71) return 0;
        return lv.MapSh[layer][cell];
    }

    private static void DrawMapGrid(ImDrawListPtr dl, Vector2 origin, int cols, int rows,
        float yOff, float z, float viewTopLocal, float viewH)
    {
        uint col = Gfx.Rgba(255, 255, 255, 14);
        float cw = ShapeTable.TileW * z, ch = ShapeTable.TileH * z;
        float y0 = Math.Max(yOff, viewTopLocal), y1 = Math.Min(yOff + rows * ch, viewTopLocal + viewH);
        for (int c = 0; c <= cols; c++)
        {
            float x = Px(origin.X + c * cw);
            dl.AddLine(new Vector2(x, origin.Y + y0), new Vector2(x, origin.Y + y1), col);
        }
        int r0 = Math.Max(0, (int)((viewTopLocal - yOff) / ch));
        int r1 = Math.Min(rows, (int)((viewTopLocal + viewH - yOff) / ch) + 1);
        for (int r = r0; r <= r1; r++)
        {
            float y = Px(origin.Y + yOff + r * ch);
            dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + cols * cw, y), col);
        }
    }

    // =====================================================================
    // Minimap
    // =====================================================================

    /// <summary>The whole 16800px level as a strip: BG1 fill, spawn dots and the view
    /// window. Click or drag to jump the canvas.</summary>
    private void DrawMiniMap(EditableEpisode ep, EditableLevel lv, Vector2 size)
    {
        WellBegin("emmini", size, AcEdit, padX: 2f, padY: 2f,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float h = ImGui.GetContentRegionAvail().Y;
        var dl = ImGui.GetWindowDrawList();

        bool held = ImGui.InvisibleButton("##emminibtn", new Vector2(Math.Max(8, w), Math.Max(8, h)));
        bool active = ImGui.IsItemActive();

        // BG1 fill density per strip row (300 map rows onto h pixels).
        byte[] cells = lv.Bg1;
        float mapTop = EmLayerYOff(0) / (float)LevelRenderer.CanvasH;   // bg1 starts halfway
        for (int py = 0; py < (int)h; py++)
        {
            float cv = py / h;                       // 0..1 down the canvas
            float rowF = (cv - mapTop) / (1f - mapTop) * Level.Bg1Rows;
            if (rowF < 0) continue;
            int row = Math.Min(Level.Bg1Rows - 1, (int)rowF);
            int fill = 0;
            int rb = row * Level.Bg1Cols;
            for (int c = 0; c < Level.Bg1Cols; c++)
                if (ResolveEditShape(lv, 0, cells[rb + c]) > 0) fill++;
            if (fill == 0) continue;
            float frac = fill / (float)Level.Bg1Cols;
            float ly = Px(p.Y + py);
            dl.AddLine(new Vector2(Px(p.X + 1), ly),
                new Vector2(Px(p.X + 1 + (w - 2) * frac), ly), Shade(AcEdit, 0.55f, 130));
        }

        // Spawn dots at their true positions.
        EnsureEditorObjects(ep, lv);
        if (_emObjects != null)
            foreach (var o in _emObjects)
            {
                float mx = p.X + Math.Clamp(o.X / LevelRenderer.CanvasW, -0.15f, 1.15f) * (w - 2) + 1;
                float my = p.Y + o.Y / LevelRenderer.CanvasH * h;
                if (my < p.Y || my > p.Y + h) continue;
                dl.AddRectFilled(new Vector2(mx - 1, my - 1), new Vector2(mx + 1, my + 1),
                    ObjectPlacer.CategoryColor(o.Cat));
            }

        // The view window.
        float vy0 = Px(p.Y + Math.Clamp(_emViewTopCv / LevelRenderer.CanvasH, 0f, 1f) * h);
        float vy1 = Px(p.Y + Math.Clamp((_emViewTopCv + _emViewHCv) / LevelRenderer.CanvasH, 0f, 1f) * h);
        dl.AddRect(new Vector2(Px(p.X), vy0), new Vector2(Px(p.X + w), Math.Max(vy1, vy0 + 4)),
            Shade(AcPlayer, 0.95f, 200));

        if (active || held)
        {
            float cv = (ImGui.GetMousePos().Y - p.Y) / h * LevelRenderer.CanvasH;
            _emScrollToY = cv;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("the whole level - click to jump");
        WellEnd();
    }

    // =====================================================================
    // Mouse + keys
    // =====================================================================

    private void HandleMapMouse(EditableEpisode ep, EditableLevel lv, Vector2 origin, float z,
        Vector2 size)
    {
        bool hovered = ImGui.IsWindowHovered();
        if (!hovered && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndStroke();
        if (!hovered && _emDragEvent >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            EndMarkerDrag(lv);
        if (!hovered) return;

        var io = ImGui.GetIO();
        var mouse = (ImGui.GetMousePos() - origin) / z;   // canvas px (may be off-map)

        // Zoom around the cursor; wheel alone scrolls vertically, with Shift sideways
        // (the child has NoScrollWithMouse).
        if (io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                float before = _emZoom;
                _emZoom = Math.Clamp(_emZoom * (io.MouseWheel > 0 ? 1.25f : 0.8f), 0.25f, 4f);
                if (Math.Abs(_emZoom - before) > 0.0001f)
                {
                    // The canvas point under the cursor stays under the cursor; applied
                    // next frame, when the content has its new size to clamp against.
                    _emZoomAnchorCanvas = mouse;
                    _emZoomAnchorScreen = ImGui.GetMousePos();
                    _emZoomPending = true;
                }
            }
            else if (io.KeyShift)
                ImGui.SetScrollX(ImGui.GetScrollX() - io.MouseWheel * 90f);
            else ImGui.SetScrollY(ImGui.GetScrollY() - io.MouseWheel * 90f);
        }
        if (io.MouseWheelH != 0) ImGui.SetScrollX(ImGui.GetScrollX() + io.MouseWheelH * 60f);

        // Panning, three ways: middle-drag, Space+left-drag (the hand every art tool has),
        // and the arrow keys (the editor owns them while it is focused).
        bool spacePan = ImGui.IsKeyDown(ImGuiKey.Space) && !io.WantTextInput;
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) ||
            (spacePan && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f)))
        {
            ImGui.SetScrollX(ImGui.GetScrollX() - io.MouseDelta.X);
            ImGui.SetScrollY(ImGui.GetScrollY() - io.MouseDelta.Y);
        }
        if (!io.WantTextInput)
        {
            // With spawns selected the arrows NUDGE them; otherwise they pan the view.
            if (_emTool == 5 && _emSelSet.Count > 0)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true)) NudgeSelection(ep, lv, -1, 0, io.KeyShift);
                if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true)) NudgeSelection(ep, lv, 1, 0, io.KeyShift);
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) NudgeSelection(ep, lv, 0, -1, io.KeyShift);
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) NudgeSelection(ep, lv, 0, 1, io.KeyShift);
            }
            else
            {
                float step = io.KeyShift ? 260f : 90f;
                if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true)) ImGui.SetScrollX(ImGui.GetScrollX() - step);
                if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true)) ImGui.SetScrollX(ImGui.GetScrollX() + step);
                if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true)) ImGui.SetScrollY(ImGui.GetScrollY() - step);
                if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true)) ImGui.SetScrollY(ImGui.GetScrollY() + step);
            }

            // Play from here: run the level in the playback and land where the cursor is.
            if (ImGui.IsKeyPressed(ImGuiKey.P))
            {
                int t = CanvasYToTime(lv, mouse.Y);
                if (t >= 0) EditorPlaytestAt(t);
            }
            // Zoom presets: fit the map's width / back to 100%, keeping the view centre.
            if ((ImGui.IsKeyPressed(ImGuiKey.F) && !io.KeyCtrl) || ImGui.IsKeyPressed(ImGuiKey.Key0))
            {
                var win = ImGui.GetWindowPos();
                var centreScreen = win + ImGui.GetWindowSize() * 0.5f;
                _emZoomAnchorCanvas = (centreScreen - origin) / z;
                _emZoomAnchorScreen = centreScreen;
                _emZoomPending = true;
                _emZoom = ImGui.IsKeyPressed(ImGuiKey.Key0) ? 1f
                    : Math.Clamp((_emLastCanvasW - 24f) / (LevelRenderer.CanvasW + 60f), 0.25f, 4f);
            }
        }
        if (spacePan)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            return;   // the hand owns the mouse: no painting underneath
        }

        HandleMapKeys(ep, lv, io);

        int cols = Level.ColsFor(_emLayer), rows = Level.RowsFor(_emLayer);
        float yOffL = EmLayerYOff(_emLayer);
        int cellC = (int)MathF.Floor(mouse.X / ShapeTable.TileW);
        int cellR = (int)MathF.Floor((mouse.Y - yOffL) / ShapeTable.TileH);
        bool inGrid = cellC >= 0 && cellC < cols && cellR >= 0 && cellR < rows;
        var dl = ImGui.GetWindowDrawList();

        if (_emTool == 5)
        {
            HandleSpawnTool(ep, lv, origin, z, mouse, dl);
            return;
        }

        if (inGrid)
        {
            DrawBrushGhost(dl, lv, origin, z, cellC, cellR, cols, rows, yOffL);
            byte cur = lv.Cells(_emLayer)[cellR * cols + cellC];
            int sid = ResolveEditShape(lv, _emLayer, cur);
            int time = CanvasYToTime(lv, mouse.Y);
            ImGui.SetTooltip($"col {cellC}  row {cellR}   cell {cur} -> tile {(sid > 0 ? sid.ToString() : "empty")}" +
                (time >= 0 ? $"\non screen around time {time}" : ""));
        }

        // Ctrl+click near a marker opens its event, from any terrain tool.
        if (_emMarkers && io.KeyCtrl && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            TryPickMarker(mouse, z, out var picked))
        {
            OpenEventInTab(picked.EventIndex);
            return;
        }

        // Right-drag grabs a stamp off the map; a short right-click picks one tile.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && inGrid)
            _emPickStart = (cellC, cellR);
        if (_emPickStart.C >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Right))
            DrawCellRect(dl, origin, z, yOffL, _emPickStart,
                (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)), AcSim);
        if (_emPickStart.C >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            GrabStamp(lv, _emPickStart, (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)));
            _emPickStart = (-1, -1);
        }

        bool lmb = ImGui.IsMouseDown(ImGuiMouseButton.Left) && !io.KeyCtrl;
        switch (_emTool)
        {
            case 0 or 1:   // paint / erase
                if (lmb && inGrid)
                {
                    BeginStroke(ep, lv);
                    if (_emTool == 1) SetCell(ep, lv, _emLayer, cellC, cellR, 0);
                    else ApplyStamp(ep, lv, cellC, cellR);
                }
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) EndStroke();
                break;

            case 2:        // pick (left-drag also grabs a stamp)
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inGrid)
                    _emPickStart = (cellC, cellR);
                if (_emPickStart.C >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    DrawCellRect(dl, origin, z, yOffL, _emPickStart,
                        (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)), AcSim);
                if (_emPickStart.C >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    GrabStamp(lv, _emPickStart, (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)));
                    _emPickStart = (-1, -1);
                }
                break;

            case 3:        // fill
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inGrid)
                {
                    BeginStroke(ep, lv);
                    FloodFill(ep, lv, _emLayer, cellC, cellR, _emStamp[0]);
                    EndStroke();
                }
                break;

            case 4:        // rect (tiles the stamp; scatter scatters)
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inGrid)
                    _emRectStart = (cellC, cellR);
                if (_emRectStart.C >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    DrawCellRect(dl, origin, z, yOffL, _emRectStart,
                        (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)), AcEdit);
                if (_emRectStart.C >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    BeginStroke(ep, lv);
                    int c0 = Math.Min(_emRectStart.C, Math.Clamp(cellC, 0, cols - 1));
                    int c1 = Math.Max(_emRectStart.C, Math.Clamp(cellC, 0, cols - 1));
                    int r0 = Math.Min(_emRectStart.R, Math.Clamp(cellR, 0, rows - 1));
                    int r1 = Math.Max(_emRectStart.R, Math.Clamp(cellR, 0, rows - 1));
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                        {
                            if (_emScatter)
                            {
                                if (_emRng.Next(100) < _emScatterPct)
                                    SetCell(ep, lv, _emLayer, c, r, ScatterPick());
                            }
                            else
                            {
                                int id = _emStamp[((r - r0) % _emStampH) * _emStampW + (c - c0) % _emStampW];
                                SetCell(ep, lv, _emLayer, c, r, id);
                            }
                        }
                    EndStroke();
                    _emRectStart = (-1, -1);
                }
                break;
        }
    }

    /// <summary>Tool and layer hotkeys while the canvas is hovered (never while typing).</summary>
    private void HandleMapKeys(EditableEpisode ep, EditableLevel lv, ImGuiIOPtr io)
    {
        if (io.WantTextInput) return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z)) UndoMap(ep);
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && _emTool == 5)
            DuplicateSelection(ep, lv);
        if (io.KeyCtrl) return;
        if (ImGui.IsKeyPressed(ImGuiKey.B)) _emTool = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.E)) _emTool = 1;
        if (ImGui.IsKeyPressed(ImGuiKey.I)) _emTool = 2;
        if (ImGui.IsKeyPressed(ImGuiKey.G)) _emTool = 3;
        if (ImGui.IsKeyPressed(ImGuiKey.M)) _emTool = 4;
        if (ImGui.IsKeyPressed(ImGuiKey.S)) { _emTool = 5; _emPalette = true; _emPaletteMode = 1; }
        if (ImGui.IsKeyPressed(ImGuiKey.Key1)) _emLayer = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.Key2)) _emLayer = 1;
        if (ImGui.IsKeyPressed(ImGuiKey.Key3)) _emLayer = 2;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && _emTool == 5 && _emSelSet.Count > 0)
            DeleteSpawn(ep, lv);
    }

    /// <summary>The stamp, ghosted under the cursor so painting is aimed before it lands.</summary>
    private void DrawBrushGhost(ImDrawListPtr dl, EditableLevel lv, Vector2 origin, float z,
        int cellC, int cellR, int cols, int rows, float yOffL)
    {
        if (_emTool is 1 or 2)
        {
            var a1 = origin + new Vector2(cellC * ShapeTable.TileW, yOffL + cellR * ShapeTable.TileH) * z;
            dl.AddRect(a1, a1 + new Vector2(ShapeTable.TileW, ShapeTable.TileH) * z,
                Shade(AcEdit, 1.1f, 230), 0, 0, 1.5f);
            return;
        }
        var atlas = Atlas(SpriteSource.Tiles(char.ToLowerInvariant(lv.ShapeChar)), _palette);
        int gw = _emScatter && _emTool == 0 ? 1 : _emStampW;
        int gh = _emScatter && _emTool == 0 ? 1 : _emStampH;
        for (int sy = 0; sy < gh; sy++)
            for (int sx = 0; sx < gw; sx++)
            {
                int c = cellC + sx, r = cellR + sy;
                if (c >= cols || r >= rows) continue;
                var a = origin + new Vector2(c * ShapeTable.TileW, yOffL + r * ShapeTable.TileH) * z;
                var b = a + new Vector2(ShapeTable.TileW, ShapeTable.TileH) * z;
                int id = _emScatter && _emTool == 0 ? _emStamp[0] : _emStamp[sy * _emStampW + sx];
                if (id > 0 && atlas != null) atlas.Draw(dl, id, a, z, Gfx.Rgba(255, 255, 255, 150));
                dl.AddRect(a, b, Shade(AcEdit, 0.9f, 130));
            }
        var g0 = origin + new Vector2(cellC * ShapeTable.TileW, yOffL + cellR * ShapeTable.TileH) * z;
        var g1 = origin + new Vector2(Math.Min(cols, cellC + gw) * ShapeTable.TileW,
            yOffL + Math.Min(rows, cellR + gh) * ShapeTable.TileH) * z;
        dl.AddRect(g0, g1, Shade(AcEdit, 1.15f, 235), 0, 0, 1.5f);
    }

    private static void DrawCellRect(ImDrawListPtr dl, Vector2 origin, float z, float yOffL,
        (int C, int R) a, (int C, int R) b, uint accent)
    {
        int c0 = Math.Min(a.C, b.C), c1 = Math.Max(a.C, b.C);
        int r0 = Math.Min(a.R, b.R), r1 = Math.Max(a.R, b.R);
        var p = Px(origin + new Vector2(c0 * ShapeTable.TileW, yOffL + r0 * ShapeTable.TileH) * z);
        var q = Px(origin + new Vector2((c1 + 1) * ShapeTable.TileW, yOffL + (r1 + 1) * ShapeTable.TileH) * z);
        dl.AddRectFilled(p, q, Shade(accent, 0.5f, 60));
        dl.AddRect(p, q, Shade(accent, 1f, 220));
    }

    /// <summary>Copy a rectangle of the active layer's resolved tiles into the brush.</summary>
    private void GrabStamp(EditableLevel lv, (int C, int R) a, (int C, int R) b)
    {
        int c0 = Math.Min(a.C, b.C), c1 = Math.Max(a.C, b.C);
        int r0 = Math.Min(a.R, b.R), r1 = Math.Max(a.R, b.R);
        int w = c1 - c0 + 1, h = r1 - r0 + 1;
        var ids = new int[w * h];
        int cols = Level.ColsFor(_emLayer);
        byte[] cells = lv.Cells(_emLayer);
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
                ids[r * w + c] = ResolveEditShape(lv, _emLayer, cells[(r0 + r) * cols + c0 + c]);
        _emStampW = w; _emStampH = h; _emStamp = ids;
        if (w * h == 1 && ids[0] > 0) NoteRecentTile(ids[0]);
        if (_emTool is 1 or 2) _emTool = 0;
        _edStatus = w * h == 1 ? $"brush: tile {ids[0]}" : $"brush: {w}x{h} stamp";
    }

    /// <summary>A random non-empty tile from the stamp pool (the scatter brush's palette).</summary>
    private int ScatterPick()
    {
        Span<int> pool = stackalloc int[_emStamp.Length];
        int n = 0;
        foreach (int id in _emStamp) if (id > 0) pool[n++] = id;
        return n == 0 ? 0 : pool[_emRng.Next(n)];
    }

    private void ApplyStamp(EditableEpisode ep, EditableLevel lv, int cellC, int cellR)
    {
        if (_emScatter)
        {
            if (_emRng.Next(100) < _emScatterPct)
                SetCell(ep, lv, _emLayer, cellC, cellR, ScatterPick());
            return;
        }
        int cols = Level.ColsFor(_emLayer), rows = Level.RowsFor(_emLayer);
        for (int sy = 0; sy < _emStampH; sy++)
            for (int sx = 0; sx < _emStampW; sx++)
            {
                int c = cellC + sx, r = cellR + sy;
                if (c >= cols || r >= rows) continue;
                SetCell(ep, lv, _emLayer, c, r, _emStamp[sy * _emStampW + sx]);
            }
    }

    // =====================================================================
    // Cell operations + undo
    // =====================================================================

    private void BeginStroke(EditableEpisode ep, EditableLevel lv)
    {
        if (_emStroke) return;
        _emStroke = true;
        _emUndo.Add(new EmUndoStep(_emLayer, (byte[])lv.Cells(_emLayer).Clone(),
            (ushort[])lv.MapSh[_emLayer].Clone(), null));
        if (_emUndo.Count > EmMaxUndo) _emUndo.RemoveAt(0);
    }

    private void EndStroke() => _emStroke = false;

    /// <summary>Snapshot the event list before a spawn edit (place, drag, delete).</summary>
    private void PushEventsUndo(EditableLevel lv)
    {
        _emUndo.Add(new EmUndoStep(0, null, null, lv.Events.ToList()));
        if (_emUndo.Count > EmMaxUndo) _emUndo.RemoveAt(0);
    }

    private void UndoMap(EditableEpisode ep)
    {
        var lv = EditorLevel();
        if (lv == null || _emUndo.Count == 0) return;
        var step = _emUndo[^1];
        _emUndo.RemoveAt(_emUndo.Count - 1);
        if (step.Events != null)
        {
            lv.Events.Clear();
            lv.Events.AddRange(step.Events);
            _emSelEvent = -1;
            _evSelected = Math.Min(_evSelected, lv.Events.Count - 1);
            NoteEventsChanged(ep);
            return;
        }
        Array.Copy(step.Cells!, lv.Cells(step.Layer), step.Cells!.Length);
        Array.Copy(step.Slots!, lv.MapSh[step.Layer], step.Slots!.Length);
        ep.LevelsDirty = true;
    }

    /// <summary>Paint one cell with a tile id (0 = empty), claiming a slot as needed.</summary>
    private void SetCell(EditableEpisode ep, EditableLevel lv, int layer, int c, int r, int shapeId)
    {
        int slot = lv.EnsureSlot(layer, shapeId);
        if (slot < 0)
        {
            _edStatus = $"BG{layer + 1} has no free tile slots ({EditableLevel.SlotLimit(layer)} in use) - " +
                        "remap one in the slot table.";
            return;
        }
        byte[] cells = lv.Cells(layer);
        int i = r * Level.ColsFor(layer) + c;
        if (cells[i] == (byte)slot) return;
        cells[i] = (byte)slot;
        ep.LevelsDirty = true;
    }

    private void FloodFill(EditableEpisode ep, EditableLevel lv, int layer, int c, int r, int shapeId)
    {
        int cols = Level.ColsFor(layer), rows = Level.RowsFor(layer);
        byte[] cells = lv.Cells(layer);
        byte from = cells[r * cols + c];
        int slot = lv.EnsureSlot(layer, shapeId);
        if (slot < 0 || from == (byte)slot) return;
        var stack = new Stack<(int C, int R)>();
        stack.Push((c, r));
        while (stack.Count > 0)
        {
            var (cc, cr) = stack.Pop();
            if (cc < 0 || cc >= cols || cr < 0 || cr >= rows) continue;
            int i = cr * cols + cc;
            if (cells[i] != from) continue;
            cells[i] = (byte)slot;
            stack.Push((cc + 1, cr)); stack.Push((cc - 1, cr));
            stack.Push((cc, cr + 1)); stack.Push((cc, cr - 1));
        }
        ep.LevelsDirty = true;
    }

    // =====================================================================
    // Spawn markers + the Spawn tool
    // =====================================================================

    private void EnsureEditorObjects(EditableEpisode ep, EditableLevel lv)
    {
        if (_emObjects != null || _gd == null) return;
        var info = EditorEpisodeInfo;
        if (info == null) return;
        try
        {
            _emScrollInfo = new ObjectPlacer.LayerScroll();
            _emObjects = ObjectPlacer.Place(_gd, info, lv.ToLevel(_edLevelIdx + 1),
                ep.ToEnemyData(), null, _emScrollInfo);
        }
        catch
        {
            _emObjects = new List<PlacedObject>();
        }
    }

    private void DrawSpawnMarkers(EditableEpisode ep, EditableLevel lv, ImDrawListPtr dl,
        Vector2 origin, float z)
    {
        EnsureEditorObjects(ep, lv);
        if (_emObjects == null) return;
        bool spawnTool = _emTool == 5;
        var mouse = ImGui.GetMousePos();
        var winTop = ImGui.GetWindowPos().Y;
        float winBot = winTop + ImGui.GetWindowSize().Y;
        float cullPad = 80f * Math.Max(1f, z);
        int pathsDrawn = 0;
        foreach (var o in _emObjects)
        {
            var p = origin + new Vector2(o.X, o.Y) * z;
            if (p.Y < winTop - cullPad || p.Y > winBot + cullPad) continue;
            uint col = ObjectPlacer.CategoryColor(o.Cat);
            bool sel = spawnTool && o.EventIndex >= 0 && _emSelSet.Contains(o.EventIndex);
            bool hot = _emDragEvent < 0 && Vector2.DistanceSquared(mouse, p) < 42f;

            // The spawn tool shows what will appear where it appears, at the terrain's own
            // zoom — the sprite drawn exactly as the engine anchors it — and, for what is
            // selected or hovered, the path it will actually fly.
            if (spawnTool)
                DrawSpawnSpriteAt(dl, ep, lv, o, p, z, sel ? (byte)255 : (byte)185);
            if (spawnTool && _emPaths && (sel || hot) && pathsDrawn < 24 &&
                o.EventIndex >= 0 && o.EventIndex < lv.Events.Count &&
                o.EnemyId >= 0 && o.EnemyId < ep.Enemies.Length)
            {
                var evp = lv.Events[o.EventIndex];
                var datp = ep.Enemies[evp.Type is >= 49 and <= 52 ? 0 : o.EnemyId];
                if (datp.Loaded)
                {
                    DrawFlightPath(dl, p, z,
                        PathPreview.Compute(datp, evp, o.Band, BackMoveAt(lv, o.Time)), col);
                    pathsDrawn++;
                }
            }
            dl.AddCircleFilled(p, sel ? 4.5f : 3.5f, Alpha(col, 235));
            dl.AddCircle(p, sel ? 4.5f : 3.5f, Gfx.Rgba(10, 12, 16, 220));
            if (sel) dl.AddCircle(p, 7.5f, Shade(AcEdit, 1.2f, 240), 0, 2f);

            if (hot)
            {
                var ev = o.EventIndex >= 0 && o.EventIndex < lv.Events.Count
                    ? lv.Events[o.EventIndex] : default;
                ImGui.SetTooltip($"t {o.Time}  {ObjectPlacer.CategoryName(o.Cat)}\n" +
                    (o.EventIndex >= 0 ? $"event {o.EventIndex + 1}: {EventCatalog.Get(ev.Type).Name}\n" : "") +
                    $"enemy {o.EnemyId}" + (o.ApproxX ? "   (x approximate)" : "") +
                    (spawnTool ? "\ndrag to move · Delete to remove · double-click to open"
                               : "\nCtrl+click: open in the event editor"));
            }
        }
    }

    /// <summary>
    /// A placed object's sprite at the canvas zoom, anchored the way JE_drawEnemy anchors it
    /// (2x2 metasprites centre on the anchor; single sprites hang from their top-left) — so
    /// the ghost sits exactly where the engine will draw the enemy, at terrain scale.
    /// </summary>
    private void DrawSpawnSpriteAt(ImDrawListPtr dl, EditableEpisode ep, EditableLevel lv,
        in PlacedObject o, Vector2 anchor, float z, byte alpha)
    {
        if (o.SpriteIndex <= 0 || o.SpriteIndex == 999) return;
        int bank;
        if (o.EventIndex >= 0 && o.EventIndex < lv.Events.Count &&
            lv.Events[o.EventIndex].Type is >= 49 and <= 52)
            bank = Math.Max(0, (int)lv.Events[o.EventIndex].Dat3);
        else if (o.EnemyId >= 0 && o.EnemyId < ep.Enemies.Length)
            bank = ep.Enemies[o.EnemyId].ShapeBank;
        else return;
        var atlas = Atlas(EnemySpriteSource(bank), AppSettings.GamePalette);
        if (atlas == null) return;
        DrawEnemyFrame(dl, atlas, o.SpriteIndex, o.Esize == 1, anchor, z,
            Gfx.Rgba(255, 255, 255, alpha));
    }

    private bool TryPickMarker(Vector2 canvasMouse, float z, out PlacedObject picked)
    {
        picked = default;
        if (_emObjects == null) return false;
        float best = MathF.Max(10f, 10f / z);
        best *= best;
        bool found = false;
        foreach (var o in _emObjects)
        {
            if (o.EventIndex < 0) continue;
            float d = Vector2.DistanceSquared(canvasMouse, new Vector2(o.X, o.Y));
            if (d < best) { best = d; picked = o; found = true; }
        }
        return found;
    }

    private void OpenEventInTab(int eventIndex)
    {
        if (eventIndex < 0) return;
        _edSelectTab = 1;
        _evSelected = eventIndex;
        _evScrollTo = true;
    }

    /// <summary>
    /// The other direction: land on the Map tab with this event centred and selected —
    /// spawns at their marker, level-wide events at their line.
    /// </summary>
    private void ShowEventOnMap(EditableEpisode ep, EditableLevel lv, int idx)
    {
        if (idx < 0 || idx >= lv.Events.Count) return;
        _edSelectTab = 0;
        SelectOnly(idx);
        _evSelected = idx;
        _emTool = 5;
        _emPalette = true;
        _emPaletteMode = 1;
        float y = (float)(ObjectPlacer.YBase - TimeToScroll(lv, lv.Events[idx].Time));
        EnsureEditorObjects(ep, lv);
        if (_emObjects != null)
            foreach (var o in _emObjects)
                if (o.EventIndex == idx) { y = o.Y; break; }
        _emScrollToY = y;
    }

    private void HandleSpawnTool(EditableEpisode ep, EditableLevel lv, Vector2 origin, float z,
        Vector2 mouse, ImDrawListPtr dl)
    {
        EnsureEditorObjects(ep, lv);
        var io = ImGui.GetIO();

        // ---- an active group drag first: it owns the mouse until release ----
        if (_emDragEvent >= 0)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                UpdateMarkerDrag(ep, lv, mouse);
                if (_emDragEvent < lv.Events.Count)
                {
                    var evd = lv.Events[_emDragEvent];
                    ImGui.SetTooltip($"t {evd.Time}   x {(evd.Dat2 == -99 ? "default" : evd.Dat2 == -200 ? "random" : evd.Dat2.ToString())}" +
                        (_emSelSet.Count > 1 ? $"   ({_emSelSet.Count} spawns)" : ""));
                }
            }
            else EndMarkerDrag(lv);
            return;
        }

        // ---- a pending press on empty space: drag = marquee, release in place = spawn ----
        if (_emPressEmpty)
        {
            if (Vector2.Distance(mouse, _emPressPos) * z > 6f) _emMarqueeLive = true;
            if (_emMarqueeLive)
            {
                var a = origin + Vector2.Min(_emPressPos, mouse) * z;
                var b = origin + Vector2.Max(_emPressPos, mouse) * z;
                dl.AddRectFilled(a, b, Shade(AcEdit, 0.5f, 40));
                dl.AddRect(Px(a), Px(b), Shade(AcEdit, 1f, 220));
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_emMarqueeLive)
                {
                    if (!io.KeyCtrl) _emSelSet.Clear();
                    var lo = Vector2.Min(_emPressPos, mouse);
                    var hi = Vector2.Max(_emPressPos, mouse);
                    if (_emObjects != null)
                        foreach (var o in _emObjects)
                            if (o.EventIndex >= 0 && o.X >= lo.X && o.X <= hi.X &&
                                o.Y >= lo.Y && o.Y <= hi.Y)
                                _emSelSet.Add(o.EventIndex);
                    _emSelEvent = _emSelSet.Count > 0 ? _emSelSet.Max() : -1;
                    if (_emSelSet.Count > 0)
                        _edStatus = $"{_emSelSet.Count} spawns selected - drag to move, " +
                                    "Delete removes, Ctrl+D duplicates, arrows nudge";
                }
                else
                {
                    PlaceSpawnAt(ep, lv, _emPressPos);
                }
                _emPressEmpty = false;
                _emMarqueeLive = false;
            }
            return;
        }

        bool overMarker = TryPickMarker(mouse, z, out var hit);

        // Right-click on open ground: the add-a-level-event menu.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !overMarker)
        {
            int ct = CanvasYToTime(lv, mouse.Y);
            if (ct >= 0)
            {
                _emCtxTime = Math.Max(1, ct);
                _emCtxRequest = true;
            }
        }

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && overMarker)
        {
            OpenEventInTab(hit.EventIndex);
            return;
        }
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _emHoverFlow >= 0)
        {
            OpenEventInTab(_emHoverFlow);
            return;
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            int grabbed = overMarker ? hit.EventIndex : _emHoverFlow;
            if (grabbed >= 0)
            {
                if (io.KeyCtrl)
                {
                    // Ctrl+click edits the selection without dragging.
                    if (!_emSelSet.Remove(grabbed)) _emSelSet.Add(grabbed);
                    _emSelEvent = _emSelSet.Count > 0 ? _emSelSet.Max() : -1;
                    return;
                }
                if (!_emSelSet.Contains(grabbed)) SelectOnly(grabbed);
                _emSelEvent = grabbed;
                BeginMarkerDrag(lv, grabbed, mouse);
            }
            else
            {
                // Not yet a spawn: the release decides between placing and a marquee.
                _emPressEmpty = true;
                _emPressPos = mouse;
                _emMarqueeLive = false;
            }
            return;
        }

        // ---- idle: ghost the spawn brush (and its formation, and its flight) ----
        if (!overMarker)
        {
            var table = ep.Enemies;
            var atlasBank = _emSpawnEnemy >= 0 && _emSpawnEnemy < table.Length
                ? table[_emSpawnEnemy].ShapeBank : -1;
            var atlas = atlasBank >= 0 ? Atlas(EnemySpriteSource(atlasBank), AppSettings.GamePalette) : null;
            var d = _emSpawnEnemy >= 0 && _emSpawnEnemy < table.Length ? table[_emSpawnEnemy] : default;
            int t = SpawnTimeForY(lv, mouse.Y, SpawnBaseEy());
            foreach (var (dx, dt) in FormationOffsets())
            {
                var at = ImGui.GetMousePos() + new Vector2(dx, dt) * z;   // dt shown as map px
                if (atlas != null && d.EGraphic != null && d.EGraphic[0] is > 0 and not 999)
                    DrawEnemyFrame(dl, atlas, d.EGraphic[0], d.Esize == 1, at,
                        z, Gfx.Rgba(255, 255, 255, 150));
            }
            if (_emPaths && d.Loaded && t >= 0)
            {
                var synth = new EventRec { Type = SpawnEventType(SpawnBandCode(table)) };
                DrawFlightPath(dl, ImGui.GetMousePos(), z,
                    PathPreview.Compute(d, synth, SpawnBandCode(table) == 1 ? 0 : 25,
                        BackMoveAt(lv, t)),
                    d.IsGround ? ObjectPlacer.CategoryColor(ObjCategory.EnemyGround)
                               : ObjectPlacer.CategoryColor(ObjCategory.EnemyAir));
            }
            var p = ImGui.GetMousePos();
            dl.AddLine(p - new Vector2(10, 0), p + new Vector2(10, 0), Shade(AcEdit, 1f, 200));
            dl.AddLine(p - new Vector2(0, 10), p + new Vector2(0, 10), Shade(AcEdit, 1f, 200));
            bool bankLoaded = SpawnBankLoaded(lv, table);
            ImGui.SetTooltip($"place enemy {_emSpawnEnemy}" +
                (_emFormation > 0 ? $" x{_emFormCount}" : "") + $"  ·  t {(t < 0 ? "?" : t)}\n" +
                $"{SpawnBandName()}{(_emSpawnBottom ? " · from bottom" : "")}" +
                (bankLoaded ? "" : "\nWARNING: bank not in the level's event-5 loads - invisible in game"));
        }
    }

    /// <summary>A polyline of canvas offsets from an anchor: where the enemy will fly.
    /// Fades along its length; a brighter dot marks every second of game time.</summary>
    private static void DrawFlightPath(ImDrawListPtr dl, Vector2 anchor, float z,
        List<Vector2> path, uint color)
    {
        if (path.Count < 2) return;
        var prev = anchor;
        int perSecond = 35 / PathPreview.TicksPerPoint;
        for (int i = 0; i < path.Count; i++)
        {
            var at = anchor + path[i] * z;
            byte a = (byte)(200 - Math.Min(150, i * 150 / path.Count));
            dl.AddLine(prev, at, Alpha(color, a), 1.5f);
            if (i > 0 && i % perSecond == 0) dl.AddCircleFilled(at, 2.5f, Alpha(color, a));
            prev = at;
        }
    }

    /// <summary>The brush's spawn pattern as (dx map px, dt ticks) per enemy.</summary>
    private IEnumerable<(float Dx, int Dt)> FormationOffsets()
    {
        if (_emFormation == 0) { yield return (0, 0); yield break; }
        int n = Math.Max(2, _emFormCount);
        for (int i = 0; i < n; i++)
        {
            float k = i - (n - 1) * 0.5f;
            switch (_emFormation)
            {
                case 1: yield return (k * _emFormSpacing, i * _emFormStagger); break;          // row
                case 2: yield return (0, i * Math.Max(4, _emFormStagger)); break;              // column
                default: yield return (k * _emFormSpacing,                                     // wedge
                    (int)(Math.Abs(k) * Math.Max(4, _emFormStagger))); break;
            }
        }
    }

    /// <summary>Whether the brush enemy's bank is one the level ever loads (event 5).</summary>
    private bool SpawnBankLoaded(EditableLevel lv, EnemyDat[] table)
    {
        if (_emSpawnEnemy < 0 || _emSpawnEnemy >= table.Length) return true;
        int bank = table[_emSpawnEnemy].ShapeBank;
        return bank is 21 or 26 || LevelBanks(lv).Contains(bank);
    }

    /// <summary>
    /// The live level checklist: the mistakes that survive to the real game silently. Cached
    /// until the events or the enemy table change.
    /// </summary>
    private List<(string Text, int EventIndex)> EnsureHealth(EditableEpisode ep, EditableLevel lv)
    {
        if (_emHealth != null) return _emHealth;
        var list = new List<(string, int)>();
        bool hasEnd = lv.Events.Any(e => e.Type == 11);
        if (!hasEnd) list.Add(("no End level (event 11) - the level never finishes", -1));
        var banks = LevelBanks(lv);
        int badBank = 0, badBankFirst = -1, ghost = 0, ghostFirst = -1;
        for (int i = 0; i < lv.Events.Count; i++)
        {
            var evn = lv.Events[i];
            if (!EventCatalog.IsSpawnType(evn.Type) || evn.Type is >= 49 and <= 52) continue;
            int id = evn.Dat;
            if (id < 0 || id >= ep.Enemies.Length) continue;
            var d = ep.Enemies[id];
            if (!d.Loaded || d.EGraphic == null || d.EGraphic[0] == 0)
            {
                ghost++;
                if (ghostFirst < 0) ghostFirst = i;
                continue;
            }
            if (d.ShapeBank is not 21 and not 26 && !banks.Contains(d.ShapeBank))
            {
                badBank++;
                if (badBankFirst < 0) badBankFirst = i;
            }
        }
        if (badBank > 0)
            list.Add(($"{badBank} spawns use banks no event 5 loads - invisible in game", badBankFirst));
        if (ghost > 0)
            list.Add(($"{ghost} spawns reference empty enemy entries", ghostFirst));
        if (hasEnd)
        {
            int endT = lv.Events.Where(e => e.Type == 11).Max(e => e.Time);
            int after = lv.Events.Count(e => e.Time > endT);
            if (after > 0)
                list.Add(($"{after} events sit after the end (t {endT}) and never run", -1));
        }
        _emHealth = list;
        return list;
    }

    /// <summary>Every sprite bank the level's event-5 records load, any time.</summary>
    private static HashSet<int> LevelBanks(EditableLevel lv)
    {
        var banks = new HashSet<int>();
        foreach (var e in lv.Events)
        {
            if (e.Type != 5) continue;
            if (e.Dat > 0) banks.Add(e.Dat);
            if (e.Dat2 > 0) banks.Add(e.Dat2);
            if (e.Dat3 > 0) banks.Add(e.Dat3);
            if (e.Dat4 > 0) banks.Add(e.Dat4);
        }
        return banks;
    }

    /// <summary>The band the spawn brush will use (auto = by the enemy's ground bit).</summary>
    private int SpawnBandCode(EnemyDat[] table)
    {
        if (_emSpawnBand != 0) return _emSpawnBand;
        bool ground = _emSpawnEnemy >= 0 && _emSpawnEnemy < table.Length &&
                      table[_emSpawnEnemy].IsGround;
        return ground ? 2 : 1;
    }

    private string SpawnBandName() => _emSpawnBand switch
    {
        0 => "auto band", 1 => "sky band", 2 => "ground band", 3 => "top band", _ => "ground2 band",
    };

    /// <summary>Event type for the brush's band + entry edge.</summary>
    private byte SpawnEventType(int bandCode) => (bandCode, _emSpawnBottom) switch
    {
        (1, false) => 15, (1, true) => 18,
        (2, false) => 6, (2, true) => 17,
        (3, false) => 7, (3, true) => 23,
        (4, false) => 10, (4, true) => 56,
        _ => 6,
    };

    private int SpawnBaseEy() => _emSpawnBottom
        ? (_emSpawnBand == 3 ? 180 : 190)
        : -28;

    /// <summary>Event time at which a spawn with this base offset lands on canvas Y.</summary>
    private int SpawnTimeForY(EditableLevel lv, float canvasY, int baseEy)
    {
        double scroll = ObjectPlacer.YBase + baseEy - canvasY;
        if (scroll < 0) return -1;
        return TimeForScroll(lv, scroll);
    }

    /// <summary>The event X (dat2) that puts a spawn of this band at canvas X.</summary>
    private short SpawnXForCanvas(EditableLevel lv, float canvasX, int bandCode)
    {
        int x = (int)MathF.Round(canvasX);
        return (short)(bandCode switch
        {
            1 => x - 48 + (lv.MapX - 1) * 24,           // sky
            3 => x - 72 + lv.MapX3 * 24 + 42,           // top over its own BG3 frame
            _ => x - 48 + (lv.MapX - 1) * 24 + 12,      // ground bands
        });
    }

    private void PlaceSpawnAt(EditableEpisode ep, EditableLevel lv, Vector2 canvasMouse)
    {
        var table = ep.Enemies;
        int band = SpawnBandCode(table);
        var offsets = FormationOffsets().ToList();
        if (lv.Events.Count + offsets.Count > EditableLevel.MaxEvents)
        {
            _edStatus = $"event list is full ({EditableLevel.MaxEvents})";
            return;
        }
        int t0 = SpawnTimeForY(lv, canvasMouse.Y, SpawnBaseEy());
        if (t0 < 0)
        {
            _edStatus = "that spot is above what the level ever scrolls in";
            return;
        }
        PushEventsUndo(lv);
        int lastAt = -1;
        foreach (var (dx, dt) in offsets)
        {
            var ev = new EventRec
            {
                Time = (ushort)Math.Clamp(t0 + dt, 1, 65499),
                Type = SpawnEventType(band),
                Dat = (short)_emSpawnEnemy,
                Dat2 = SpawnXForCanvas(lv, canvasMouse.X + dx, band),
            };
            int at = 0;
            while (at < lv.Events.Count && lv.Events[at].Time <= ev.Time) at++;
            lv.Events.Insert(at, ev);
            lastAt = at;
        }
        SelectOnly(lastAt);
        _evSelected = lastAt;
        NoteRecentEnemy(_emSpawnEnemy);
        NoteEventsChanged(ep);
        string what = offsets.Count > 1 ? $"{offsets.Count} x enemy {_emSpawnEnemy}" : $"enemy {_emSpawnEnemy}";
        _edStatus = $"placed {what} at t {t0}" +
            (SpawnBankLoaded(lv, table) ? "" : "  -  WARNING: bank not loaded by event 5, invisible in game");
    }

    private void BeginMarkerDrag(EditableLevel lv, int grabbed, Vector2 mouse)
    {
        _emDragEvent = grabbed;
        _emDragStartMouse = mouse;
        _emDragOrigs = new Dictionary<int, (EventRec, double)>();
        foreach (int idx in _emSelSet)
            if (idx >= 0 && idx < lv.Events.Count)
                _emDragOrigs[idx] = (lv.Events[idx], TimeToScroll(lv, lv.Events[idx].Time));
        PushEventsUndo(lv);   // one step per grab, however far the group moves
    }

    /// <summary>
    /// Move the whole selection by the mouse delta: X is one-for-one (every band's screen X
    /// is dat2 plus a constant), time comes from the scroll each member's drag crossed — so
    /// the group follows the hand whatever the level's speed events are doing.
    /// </summary>
    private void UpdateMarkerDrag(EditableEpisode ep, EditableLevel lv, Vector2 canvasMouse)
    {
        if (_emDragOrigs == null) { _emDragEvent = -1; return; }
        var delta = canvasMouse - _emDragStartMouse;
        MoveSpawns(ep, lv, _emDragOrigs, delta);
    }

    /// <summary>Apply a canvas-space delta to a set of events from their drag origins.</summary>
    private void MoveSpawns(EditableEpisode ep, EditableLevel lv,
        Dictionary<int, (EventRec Ev, double Scroll)> origs, Vector2 delta)
    {
        foreach (var (idx, (orig, scroll0)) in origs)
        {
            if (idx < 0 || idx >= lv.Events.Count) continue;
            var ev = orig;
            // Only spawns move sideways: a flow event's dat2 is a PARAMETER (a scroll
            // speed, a jump target), and a drag must never rewrite it.
            if (EventCatalog.IsSpawnType(ev.Type))
            {
                if (ev.Dat2 is not (-99) and not (-200))
                    ev.Dat2 = (short)Math.Clamp(orig.Dat2 + (int)MathF.Round(delta.X),
                        short.MinValue, short.MaxValue);
                else if (Math.Abs(delta.X) > 6f)
                {
                    // A default/random X becomes concrete the moment it is dragged sideways.
                    int band = ev.Type switch
                    {
                        15 or 18 or 50 => 1,
                        7 or 23 or 32 or 51 => 3,
                        _ => 2,
                    };
                    ev.Dat2 = SpawnXForCanvas(lv, _emDragStartMouse.X + delta.X, band);
                }
            }
            int t = TimeForScroll(lv, scroll0 - delta.Y);   // dragging down = earlier
            ev.Time = (ushort)Math.Clamp(t, 1, 65499);
            lv.Events[idx] = ev;
        }
        NoteEventsChanged(ep);
    }

    private void EndMarkerDrag(EditableLevel lv)
    {
        if (_emDragEvent < 0) return;
        _emDragEvent = -1;
        _emDragOrigs = null;
        SortEvents(lv);   // remaps every selection to the records' new indices
    }

    /// <summary>Arrow-key nudging of the selection: X in pixels, Y through the same
    /// scroll-to-time conversion a drag uses. Shift = big steps.</summary>
    private void NudgeSelection(EditableEpisode ep, EditableLevel lv, int dx, int dy, bool big)
    {
        if (_emSelSet.Count == 0) return;
        int step = big ? 8 : 1;
        var origs = new Dictionary<int, (EventRec, double)>();
        foreach (int idx in _emSelSet)
            if (idx >= 0 && idx < lv.Events.Count)
                origs[idx] = (lv.Events[idx], TimeToScroll(lv, lv.Events[idx].Time));
        PushEventsUndo(lv);
        MoveSpawns(ep, lv, origs, new Vector2(dx * step, dy * step));
        SortEvents(lv);
    }

    /// <summary>Ctrl+D: duplicate the selection a touch later, and select the copies.</summary>
    private void DuplicateSelection(EditableEpisode ep, EditableLevel lv)
    {
        if (_emSelSet.Count == 0) return;
        if (lv.Events.Count + _emSelSet.Count > EditableLevel.MaxEvents)
        {
            _edStatus = $"event list is full ({EditableLevel.MaxEvents})";
            return;
        }
        PushEventsUndo(lv);
        var copies = new List<EventRec>();
        foreach (int idx in _emSelSet.OrderBy(i => i))
            if (idx >= 0 && idx < lv.Events.Count)
            {
                var ev = lv.Events[idx];
                ev.Time = (ushort)Math.Min(65499, ev.Time + 12);
                copies.Add(ev);
            }
        _emSelSet.Clear();
        foreach (var ev in copies)
        {
            int at = 0;
            while (at < lv.Events.Count && lv.Events[at].Time <= ev.Time) at++;
            lv.Events.Insert(at, ev);
            // Earlier-inserted copies shift when a later one lands before them; reindex.
            var shifted = _emSelSet.Where(i => i >= at).OrderByDescending(i => i).ToList();
            foreach (int s in shifted) { _emSelSet.Remove(s); _emSelSet.Add(s + 1); }
            _emSelSet.Add(at);
        }
        _emSelEvent = _emSelSet.Count > 0 ? _emSelSet.Max() : -1;
        NoteEventsChanged(ep);
        _edStatus = $"duplicated {copies.Count} spawns (+12 ticks) - drag them into place";
    }

    private void DeleteSpawn(EditableEpisode ep, EditableLevel lv)
    {
        if (_emSelSet.Count == 0) return;
        PushEventsUndo(lv);
        int removed = 0;
        foreach (int idx in _emSelSet.OrderByDescending(i => i))
        {
            if (idx < 0 || idx >= lv.Events.Count) continue;
            lv.Events.RemoveAt(idx);
            removed++;
            if (_evSelected == idx) _evSelected = -1;
            else if (_evSelected > idx) _evSelected--;
        }
        _emSelSet.Clear();
        _emSelEvent = -1;
        NoteEventsChanged(ep);
        _edStatus = removed == 1 ? "removed 1 spawn" : $"removed {removed} spawns";
    }

    // =====================================================================
    // Time ruler
    // =====================================================================

    /// <summary>
    /// The BG1 scroll integrated over the event list, exactly as ObjectPlacer does it:
    /// segments of (time, accumulated px, px per time unit). What turns "map row" into
    /// "event time" for the ruler, the hover readout and the spawn tool.
    /// </summary>
    private List<ScrollWalk.Seg> EnsureTimeRuler(EditableLevel lv)
        => _emTimeRuler ??= ScrollWalk.Build(lv.Events);

    private double TimeToScroll(EditableLevel lv, int time)
        => ScrollWalk.ScrollAt(EnsureTimeRuler(lv), time);

    /// <summary>BG1 px per tick in effect at an event time (the scroll-speed the path
    /// preview and the sky-band drift run at).</summary>
    private int BackMoveAt(EditableLevel lv, int time)
        => ScrollWalk.MoveAt(EnsureTimeRuler(lv), time);

    /// <summary>Earliest event time whose accumulated scroll reaches <paramref name="scroll"/>.</summary>
    private int TimeForScroll(EditableLevel lv, double scroll)
        => ScrollWalk.TimeFor(EnsureTimeRuler(lv), scroll);

    /// <summary>Event time at which a canvas Y is at the screen top; -1 above the map start.</summary>
    private int CanvasYToTime(EditableLevel lv, float canvasY)
    {
        double scroll = ObjectPlacer.YBase - canvasY;
        if (scroll < 0) return -1;
        return TimeForScroll(lv, scroll);
    }

    private void DrawTimeRuler(ImDrawListPtr dl, EditableLevel lv, Vector2 origin, float z,
        float viewTop, float viewH)
    {
        int endTime = lv.Events.Count > 0 ? lv.Events[^1].Time : 0;
        var winPos = ImGui.GetWindowPos();
        float xPin = winPos.X + 4;

        int step = z >= 2f ? 100 : z >= 0.9f ? 250 : 500;
        for (int t = 0; t <= endTime + step; t += step)
        {
            double y = (ObjectPlacer.YBase - TimeToScroll(lv, t)) * z;
            float sy = origin.Y + (float)y;
            if (sy < winPos.Y - 20 || sy > winPos.Y + viewH + 20) continue;
            if (y < 0) break;   // above the map top: the level has scrolled everything in
            float py = Px(sy);
            dl.AddLine(new Vector2(Px(origin.X), py), new Vector2(Px(origin.X + 6 * z), py),
                Shade(AcRoutes, 0.9f, 150));
            dl.AddText(new Vector2(xPin, MathF.Floor(sy) - 6), Shade(AcRoutes, 1f, 185), $"t{t}");
        }
    }

    // =====================================================================
    // Tile palette
    // =====================================================================

    private void DrawTilePalette(EditableEpisode ep, EditableLevel lv)
    {
        var atlas = Atlas(SpriteSource.Tiles(char.ToLowerInvariant(lv.ShapeChar)), _palette);
        UiSection("Tiles", AcEdit, $"shapes{char.ToLowerInvariant(lv.ShapeChar)}");
        if (UiButton("Erase brush", AcEdit, "Paint empty cells (same as the Erase tool).", 0f))
            _emTool = 1;
        ImGui.SameLine(0, 5);
        if (UiButton("Slot table...", AcEdit,
                "The 72-entry indirection each layer's cells go through.\n" +
                "Remap a slot to re-skin every cell using it at once.", 0f))
            _emSlots = true;
        UiToggle("scatter", ref _emScatter, AcEdit,
            "Paint random tiles from the current stamp's pool - debris fields,\n" +
            "broken ground, stars. Grab a few tiles as a stamp first.");
        if (_emScatter)
        {
            ImGui.SameLine(0, 6);
            ImGui.SetNextItemWidth(90);
            ImGui.SliderInt("##emscpct", ref _emScatterPct, 5, 100, "%d%%");
            SliderReset(ref _emScatterPct, 35);
        }
        ImGui.TextDisabled("right-drag ON THE MAP grabs a\nmulti-tile stamp to paint with");
        if (atlas != null && _emRecentTiles.Count > 0)
        {
            // The last few tiles used, one click away.
            var rdl = ImGui.GetWindowDrawList();
            var rtop = ImGui.GetCursorScreenPos();
            var rmouse = ImGui.GetMousePos();
            ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, ShapeTable.TileH + 4));
            for (int k = 0; k < _emRecentTiles.Count; k++)
            {
                int id = _emRecentTiles[k];
                var a = rtop + new Vector2(k * (ShapeTable.TileW + 4), 2);
                var b = a + new Vector2(ShapeTable.TileW, ShapeTable.TileH);
                rdl.AddRectFilled(a, b, Gfx.Rgba(20, 22, 29));
                atlas.Draw(rdl, id, a, 1f);
                bool hot = rmouse.X >= a.X && rmouse.X < b.X && rmouse.Y >= a.Y && rmouse.Y < b.Y &&
                           ImGui.IsWindowHovered();
                rdl.AddRect(a, b, hot ? Shade(AcEdit, 1f, 220) : UiLineSoft);
                if (hot)
                {
                    ImGui.SetTooltip($"recent: tile {id}");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        _emStampW = _emStampH = 1;
                        _emStamp = new[] { id };
                        if (_emTool is 1 or 2 or 5) _emTool = 0;
                    }
                }
            }
        }
        ImGui.Dummy(new Vector2(0, 3));
        if (atlas == null) { ImGui.TextDisabled("tile set not found"); return; }

        ImGui.BeginChild("empaltiles");
        var dl = ImGui.GetWindowDrawList();
        float availW = ImGui.GetContentRegionAvail().X;
        float viewH = ImGui.GetContentRegionAvail().Y;   // read before the Dummy consumes it
        int perRow = Math.Max(1, (int)(availW / (ShapeTable.TileW + 6)));
        var top = ImGui.GetCursorScreenPos();
        int rowsUsed = (ShapeTable.TileCount + perRow - 1) / perRow;
        ImGui.Dummy(new Vector2(availW, rowsUsed * (ShapeTable.TileH + 6)));
        var mouse = ImGui.GetMousePos();
        float scrollY = ImGui.GetScrollY();
        bool single = _emStampW * _emStampH == 1;
        for (int i = 1; i <= ShapeTable.TileCount; i++)
        {
            int cell = i - 1;
            float x = (cell % perRow) * (ShapeTable.TileW + 6);
            float y = (cell / perRow) * (ShapeTable.TileH + 6);
            if (y < scrollY - 40 || y > scrollY + viewH + 20) continue;
            var a = top + new Vector2(x, y);
            var b = a + new Vector2(ShapeTable.TileW, ShapeTable.TileH);
            bool hot = mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y &&
                       ImGui.IsWindowHovered();
            dl.AddRectFilled(a, b, Gfx.Rgba(20, 22, 29));
            atlas.Draw(dl, i, a, 1f);
            if (single && i == _emStamp[0] && _emTool != 1)
                dl.AddRect(a - new Vector2(1, 1), b + new Vector2(1, 1), Shade(AcEdit, 1.2f), 0, 0, 2f);
            else if (hot) dl.AddRect(a, b, Shade(AcEdit, 0.9f, 200));
            if (hot)
            {
                ImGui.SetTooltip($"tile {i}");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _emStampW = _emStampH = 1;
                    _emStamp = new[] { i };
                    NoteRecentTile(i);
                    if (_emTool is 1 or 2 or 5) _emTool = 0;
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawSlotEditor(EditableEpisode ep, EditableLevel lv)
    {
        UiSection($"BG{_emLayer + 1} slot table", AcEdit,
            $"{lv.SlotsUsed(_emLayer)}/{EditableLevel.SlotLimit(_emLayer)}");
        if (UiButton("< back to tiles", AcEdit, "", 0f)) _emSlots = false;
        ImGui.TextDisabled("Cells store a slot index; slots name a tile.\n" +
            "Click a slot to point it at the brush tile -\n" +
            "every cell using that slot re-skins at once.");
        ImGui.Dummy(new Vector2(0, 3));

        var atlas = Atlas(SpriteSource.Tiles(char.ToLowerInvariant(lv.ShapeChar)), _palette);
        if (atlas == null) { ImGui.TextDisabled("tile set not found"); return; }

        // How many cells reference each slot, so remapping is done with open eyes.
        var counts = new int[128];
        foreach (byte cell in lv.Cells(_emLayer)) if (cell < 128) counts[cell]++;

        ImGui.BeginChild("emslotgrid");
        var dl = ImGui.GetWindowDrawList();
        float availW = ImGui.GetContentRegionAvail().X;
        int perRow = Math.Max(1, (int)(availW / (ShapeTable.TileW + 22)));
        var top = ImGui.GetCursorScreenPos();
        int limit = EditableLevel.SlotLimit(_emLayer);
        ImGui.Dummy(new Vector2(availW, (limit / perRow + 1) * (ShapeTable.TileH + 18)));
        var mouse = ImGui.GetMousePos();
        for (int s = 0; s < limit; s++)
        {
            float x = (s % perRow) * (ShapeTable.TileW + 22);
            float y = (s / perRow) * (ShapeTable.TileH + 18);
            var a = top + new Vector2(x, y);
            var b = a + new Vector2(ShapeTable.TileW, ShapeTable.TileH);
            dl.AddRectFilled(a, b, Gfx.Rgba(20, 22, 29));
            int sid = lv.MapSh[_emLayer][s];
            if (sid > 0) atlas.Draw(dl, sid, a, 1f);
            dl.AddRect(a, b, counts[s] > 0 ? Shade(AcEdit, 0.7f, 160) : UiLineSoft);
            dl.AddText(new Vector2(a.X, b.Y + 1), counts[s] > 0 ? UiDim : UiFaint, $"{s}");
            bool hot = mouse.X >= a.X && mouse.X < b.X && mouse.Y >= a.Y && mouse.Y < b.Y &&
                       ImGui.IsWindowHovered();
            if (!hot) continue;
            ImGui.SetTooltip($"slot {s} -> tile {sid}   used by {counts[s]} cells\n" +
                             $"click: point it at tile {_emStamp[0]}");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                BeginStroke(ep, lv);   // snapshots cells AND the slot table, so Ctrl+Z undoes this
                EndStroke();
                lv.MapSh[_emLayer][s] = (ushort)_emStamp[0];
                ep.LevelsDirty = true;
                _edStatus = $"BG{_emLayer + 1} slot {s} -> tile {_emStamp[0]}.";
            }
        }
        ImGui.EndChild();
    }

    // =====================================================================
    // Spawn palette (the enemy brush)
    // =====================================================================

    private void DrawSpawnPalette(EditableEpisode ep, EditableLevel lv)
    {
        UiSection("Spawn brush", AcEdit);
        SegBar("##emband2", ref _emSpawnBand, AcEdit, ImGui.GetContentRegionAvail().X - 4f,
            ("Auto", "Ground band for ground entries (explosion bit), sky for the rest."),
            ("Sky", "Band 0: over the terrain, in front of BG1/BG2."),
            ("Grnd", "Band 25: the main enemy band."),
            ("Top", "Band 50: the foreground band, over BG3."),
            ("Gr2", "Band 75: the second ground band."));
        UiToggle("enter from bottom", ref _emSpawnBottom, AcEdit,
            "Spawn at the bottom edge (y 190) instead of the top (-28) -\n" +
            "the engine's chaser/escort entry.");

        UiSection("Formation", AcEdit);
        SegBar("##emform", ref _emFormation, AcEdit, ImGui.GetContentRegionAvail().X - 4f,
            ("One", "A single spawn per click."),
            ("Row", "N spawns spread sideways, optionally staggered in time."),
            ("Col", "N spawns at one X, one after another - a stream."),
            ("Vee", "A wedge: the middle first, wings later and wider."));
        if (_emFormation > 0)
        {
            ImGui.SetNextItemWidth(84);
            ImGui.SliderInt("count", ref _emFormCount, 2, 10);
            SliderReset(ref _emFormCount, 4);
            ImGui.SetNextItemWidth(84);
            ImGui.SliderInt("spacing", ref _emFormSpacing, 12, 72, "%d px");
            SliderReset(ref _emFormSpacing, 28);
            ImGui.SetNextItemWidth(84);
            ImGui.SliderInt("stagger", ref _emFormStagger, 0, 60, "%d t");
            SliderReset(ref _emFormStagger, 8, "Ticks between members.");
        }

        if (_emRecentEnemies.Count > 0)
        {
            // The last few enemies placed, one click away.
            var rdl = ImGui.GetWindowDrawList();
            var rtop = ImGui.GetCursorScreenPos();
            var rmouse = ImGui.GetMousePos();
            ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 36));
            for (int k = 0; k < _emRecentEnemies.Count; k++)
            {
                int id = _emRecentEnemies[k];
                var a = rtop + new Vector2(k * 38, 1);
                var b = a + new Vector2(34, 34);
                rdl.AddRectFilled(a, b, Gfx.Rgba(20, 22, 29), 3f);
                EditorEnemyThumb(rdl, ep.Enemies, id, a, b);
                bool hot = rmouse.X >= a.X && rmouse.X < b.X && rmouse.Y >= a.Y && rmouse.Y < b.Y &&
                           ImGui.IsWindowHovered();
                rdl.AddRect(a, b, id == _emSpawnEnemy ? Shade(AcEdit, 1.2f, 240)
                    : hot ? Shade(AcEdit, 1f, 200) : UiLineSoft, 3f);
                if (hot)
                {
                    ImGui.SetTooltip($"recent: enemy {id}");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        _emSpawnEnemy = id;
                        _emTool = 5;
                    }
                }
            }
        }
        ImGui.Dummy(new Vector2(0, 2));
        UiFilter("##emspawnf", "id, bank, armor...", _emSpawnFilter,
            ImGui.GetContentRegionAvail().X - 66f, AcEdit);
        ImGui.SameLine(0, 5);
        UiToggle("banks", ref _emBankOnly, AcEdit,
            "Only enemies whose sprite bank this level loads (event 5) -\n" +
            "anything else would be INVISIBLE in the real game. Coins and\npowerups always count.");

        string f = BufText(_emSpawnFilter).Trim();
        var table = ep.Enemies;
        var banks = _emBankOnly ? LevelBanks(lv) : null;
        ImGui.BeginChild("emspawnlist");
        int shown = 0;
        foreach (int id in EnumerateEnemyIds(table, f, null))
        {
            var d = table[id];
            if (banks != null && d.ShapeBank is not 21 and not 26 && !banks.Contains(d.ShapeBank))
                continue;
            shown++;
            if (EnemyListRow(table, id, id == _emSpawnEnemy))
            {
                _emSpawnEnemy = id;
                _emTool = 5;
                NoteRecentEnemy(id);
            }
        }
        if (shown == 0)
            UiEmpty("no enemies match", _emBankOnly
                ? "this level loads few banks - untick 'banks' to see everything"
                : "clear the filter", AcEdit);
        ImGui.EndChild();
    }

    /// <summary>Loaded, non-blank enemy ids passing a filter (shared by the pickers).</summary>
    private static IEnumerable<int> EnumerateEnemyIds(EnemyDat[] table, string filter,
        HashSet<int>? restrictTo)
    {
        for (int id = 0; id < table.Length; id++)
        {
            if (id is > 850 and < 1001) continue;
            var d = table[id];
            if (!d.Loaded || d.EGraphic == null || d.EGraphic[0] == 0) continue;
            if (restrictTo != null && !restrictTo.Contains(id)) continue;
            if (filter.Length > 0 && !Matches(filter, id.ToString(), d.ShapeBank.ToString(),
                    d.Armor.ToString(), d.Value.ToString())) continue;
            yield return id;
        }
    }
}
