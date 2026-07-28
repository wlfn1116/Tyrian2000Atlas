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
    /// <summary>0..4 are terrain tools; 5..8 are the spawn equivalents.</summary>
    private int _emTool;
    private const int EmSpawnPlace = 5;
    private const int EmSpawnErase = 6;
    private const int EmSpawnPick = 7;
    private const int EmSpawnSelect = 8;
    private int _emLastTerrainTool;       // restored when switching back to Terrain
    private int _emLastSpawnTool = EmSpawnPlace; // ... and to Spawns
    /// <summary>Spawns are a mode of their own, not the last chip in the tool row.</summary>
    private bool SpawnMode => _emTool >= EmSpawnPlace;
    private float _emZoom = 1f;
    private bool _emGrid = true;
    private bool _emDimOthers = true;
    /// <summary>Show full spawn sprites while a terrain tool is active. Spawn tools always
    /// show them; this independent overlay is the quick way to paint around encounters.</summary>
    private bool _emShowSpawns = true;
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
    private readonly record struct EmUndoStep(string Label, int Layer, byte[]? Cells,
        ushort[]? Slots, List<EventRec>? Events);
    private readonly List<EmUndoStep> _emUndo = new();
    private readonly List<EmUndoStep> _emRedo = new();

    // The brush: a stamp of 1-based tile ids, row-major; 1x1 for a plain tile. 0 entries
    // paint empty, so a stamp lifted off the map pastes its gaps too.
    private int _emStampW = 1, _emStampH = 1;
    private int[] _emStamp = { 1 };
    private (int C, int R) _emPickStart = (-1, -1);   // stamp grab in progress
    private bool _emScatter;              // paint random picks from the stamp pool
    private int _emScatterPct = 35;
    private bool _emPaintEmptyOnly;       // protect authored terrain while decorating
    private bool _emMirrorPaint;          // reflect paint across the active layer's centre
    private readonly Random _emRng = new(12345);

    // The spawn brush and the marker selection.
    private int _emSpawnEnemy = 25;
    private int _emSpawnBand;             // 0 auto, 1 sky, 2 ground, 3 top, 4 ground2
    private bool _emSpawnBottom;          // enter from the bottom edge instead of the top
    private bool _emBankOnly = true;      // palette lists only banks the level loads
    private bool _emSnap;                 // snap spawn X to a 6px lane grid
    private readonly byte[] _emSpawnFilter = new byte[48];
    private int _emSpawnPanel;            // 0 brush, 1 selection, 2 waves
    private int _emSelLink = 1;           // the link number the bulk-assign field holds
    private int _emSelStagger = 8;        // ticks per member for the stagger op

    /// <summary>A wave saved off a selection: relative times, absolute Xs shifted as one.</summary>
    private sealed class WaveStamp
    {
        public string Name = "";
        public List<EventRec> Events = new();   // Time = offset from the wave's start
        public float AnchorX;                   // canvas X of the earliest member at capture
        public int Enemy;                       // for the thumbnail
    }
    private readonly List<WaveStamp> _emWaves = new();
    private int _emWaveArmed = -1;              // wave the Place tool stamps (-1 = enemy brush)
    private int _emFormation;             // 0 single, 1 row, 2 column, 3 wedge
    private int _emFormCount = 4;
    private int _emFormSpacing = 28;
    private int _emFormStagger = 8;
    private static readonly string[] EmFormationNames =
        { "One", "Row", "Stream", "Vee", "Sweep", "Zigzag", "Pincer", "Arc" };
    private static readonly string[] EmFormationTips =
    {
        "A single spawn per click.",
        "A horizontal wall, optionally rippling in time.",
        "One lane, one attacker after another.",
        "The centre arrives first and the wings follow.",
        "A diagonal crossing of space and time.",
        "Attackers alternate between two lanes.",
        "Pairs close inward from both sides.",
        "A curved wave: the centre leads, the edges trail smoothly.",
    };
    private readonly record struct EmCue(int Time, string Name);
    private readonly Dictionary<EditableLevel, List<EmCue>> _emCues = new();
    private int _emCueSerial = 1;
    private int _emCursorTime = -1;
    private int _emSelEvent = -1;         // primary selected spawn (event index, -1 = none)
    private readonly HashSet<int> _emSelSet = new();   // the whole selection
    private int _emDragEvent = -1;        // marker drag in progress (the grabbed one)
    private Vector2 _emDragStartMouse;
    private Dictionary<int, (EventRec Ev, double Scroll)>? _emDragOrigs;  // per-member origins
    private bool _emPressEmpty;           // LMB went down on empty space: click places,
    private Vector2 _emPressPos;          //   a drag past the threshold opens a marquee
    private bool _emMarqueeLive;
    private bool _emSpawnEraseStroke;
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
        DrawPacingStrip(ep, lv);

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
            if (_emPaletteMode == 1) DrawSpawnPanelTabs(ep, lv);
            else if (_emSlots) DrawSlotEditor(ep, lv);
            else DrawTilePalette(ep, lv);
            WellEnd();
        }
    }

    /// <summary>The spawn workspace's side panel: the brush, the selection, the wave shelf.</summary>
    private void DrawSpawnPanelTabs(EditableEpisode ep, EditableLevel lv)
    {
        string selLabel = _emSelSet.Count > 0 ? $"Sel ({_emSelSet.Count})" : "Sel";
        SegBar("##emsptabs", ref _emSpawnPanel, AcEdit, ImGui.GetContentRegionAvail().X - 4f,
            ("Brush", "What a click places: enemy, band, entry edge, formation."),
            (selLabel, "The selected spawns, and the shaping operations that act on all\nof them at once."),
            ($"Waves ({_emWaves.Count})", "Selections saved as reusable waves - stamp them anywhere,\nin any level."));
        ImGui.Dummy(new Vector2(0, 2));
        switch (_emSpawnPanel)
        {
            case 1: DrawSelectionPanel(ep, lv); break;
            case 2: DrawWavesPanel(ep, lv); break;
            default: DrawSpawnPalette(ep, lv); break;
        }
    }

    private void DrawMapToolStrip(EditableEpisode ep, EditableLevel lv)
    {
        // A small contextual command deck: creation tools own the first row; navigation,
        // recovery and view state own the second. Everything stays visible in the detail pane
        // instead of the useful controls falling off its right edge.
        BandBegin("emband", AcEdit, 2);

        // What is being edited comes first: the terrain, or the spawns living on it.
        int mode = SpawnMode ? 1 : 0;
        if (SegBar("##emmode2", ref mode, AcEdit, 168f,
                ("Terrain", "Paint the three background layers with tiles and stamps."),
                ("Spawns", "Place, select and shape the enemies directly on the map.")))
        {
            if (mode == 1) { _emLastTerrainTool = Math.Clamp(_emTool, 0, 4); _emTool = _emLastSpawnTool; }
            else { _emLastSpawnTool = Math.Clamp(_emTool, EmSpawnPlace, EmSpawnSelect); _emTool = _emLastTerrainTool; }
        }

        BandDivider();
        if (!SpawnMode)
        {
            SegBar("##emlayer", ref _emLayer, AcEdit, 150f,
                ("BG1", "Ground layer: 14x300 cells, the gameplay length of the level.  (key 1)"),
                ("BG2", "Middle layer: 14x600 cells, scrolls faster, blended over BG1.  (key 2)"),
                ("BG3", "Cloud layer: 15x600 cells, fastest scroll.  (key 3)"));
            BandDivider();
            SegBar("##emtool", ref _emTool, AcEdit, 280f,
                ("Paint", "Left-drag paints the brush.  (B)\nRight-click picks a tile; right-DRAG grabs a multi-tile stamp."),
                ("Erase", "Left-drag clears cells to empty.  (E)"),
                ("Pick", "Left-click reads a tile into the brush; drag grabs a stamp.  (I)"),
                ("Fill", "Flood-fills the connected region of identical cells.  (G)"),
                ("Rect", "Left-drag fills a rectangle, tiling the current stamp.  (M)"));
        }
        else
        {
            int stool = _emTool - EmSpawnPlace;
            SegBar("##emstool", ref stool, AcEdit, 280f,
                ("Place", "Click drops the brush (or the armed wave).  (S)\n" +
                          "Markers still select and drag; right-click adds a level event."),
                ("Erase", "Click or drag across spawn markers to remove them.  (E)\n" +
                          "A whole drag is one undo step."),
                ("Pick", "Sample a spawn's enemy, band and entry edge into the brush.  (I)"),
                ("Select", "Click and box-select without ever placing.  (V)\n" +
                           "Drag moves the selection · Delete removes · Ctrl+D duplicates\n" +
                           "Alt+drag duplicates-and-drags · arrows nudge"));
            _emTool = EmSpawnPlace + Math.Clamp(stool, 0, EmSpawnSelect - EmSpawnPlace);
            _emLastSpawnTool = _emTool;
        }
        if (SpawnMode && _emPaletteMode != 1) { _emPalette = true; _emPaletteMode = 1; }
        if (!SpawnMode) _emPaletteMode = 0;

        if (UiButton("Undo", AcEdit,
                _emUndo.Count > 0 ? $"Undo {_emUndo[^1].Label}  (Ctrl+Z)" : "Nothing to undo.",
                58f, _emUndo.Count == 0))
            UndoMap(ep);
        ImGui.SameLine(0, 5);
        if (UiButton("Redo", AcEdit,
                _emRedo.Count > 0 ? $"Redo {_emRedo[^1].Label}  (Ctrl+Y / Ctrl+Shift+Z)" : "Nothing to redo.",
                58f, _emRedo.Count == 0))
            RedoMap(ep);

        BandDivider();
        BandLabel("zoom");
        ImGui.SetNextItemWidth(100);
        ImGui.SliderFloat("##emzoom", ref _emZoom, 0.25f, 4f, "%.2fx");
        SliderReset(ref _emZoom, 1f, "Ctrl+wheel over the map does this too.");

        BandDivider();
        UiToggle("spawns", ref _emShowSpawns, AcEdit,
            "Show placed enemy sprites while a terrain tool is active.\n" +
            "Spawn tools always show them so they remain editable.", 84f);
        ImGui.SameLine(0, 5);
        // The remaining view switches live behind one button: they are set-and-forget.
        if (UiButton("view...", AcEdit, "Grid, layer dimming, flight paths, the screen\nframe and the side panel."))
            ImGui.OpenPopup("##emview");
        if (ImGui.BeginPopup("##emview"))
        {
            ImGui.Checkbox("cell grid", ref _emGrid);
            ImGui.Checkbox("dim other layers", ref _emDimOthers);
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
        var cues = EditorCues(lv);
        if (UiButton(cues.Count == 0 ? "cues" : $"cues {cues.Count}", AcRoutes,
                "Session navigation cues for this level. Add them at the view centre\n" +
                "or press K with the mouse over the map. They also appear on the pacing strip."))
            ImGui.OpenPopup("##emcues");
        DrawCuePopup(lv, cues);

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
        string brush;
        if (SpawnMode)
        {
            brush = _emTool switch
            {
                EmSpawnErase => "erase spawns",
                EmSpawnPick => "pick spawn",
                EmSpawnSelect => _emSelSet.Count > 0 ? $"{_emSelSet.Count} selected" : "select",
                _ when _emWaveArmed >= 0 && _emWaveArmed < _emWaves.Count =>
                    $"wave: {_emWaves[_emWaveArmed].Name}",
                _ => $"enemy {_emSpawnEnemy}" + (_emFormation > 0 ? $" x{_emFormCount}" : "")
            };
            BandNote(brush, UiFaint);
        }
        else
        {
            brush = _emStampW * _emStampH > 1 ? $"stamp {_emStampW}x{_emStampH}" : $"tile {_emStamp[0]}";
            if (_emScatter && _emTool is 0 or 4) brush += $" · scatter {_emScatterPct}%";
            BandNote($"{brush}   ·   slots {lv.SlotsUsed(_emLayer)}/{EditableLevel.SlotLimit(_emLayer)}",
                UiFaint);
        }
        BandEnd();
    }

    // =====================================================================
    // Session cues: lightweight landmarks for composing a long level
    // =====================================================================

    private List<EmCue> EditorCues(EditableLevel lv)
    {
        if (!_emCues.TryGetValue(lv, out var cues))
        {
            cues = new List<EmCue>();
            _emCues[lv] = cues;
        }
        return cues;
    }

    private void AddEditorCue(EditableLevel lv, int time)
    {
        if (time < 0) return;
        var cues = EditorCues(lv);
        time = Math.Clamp(time, 1, 65499);
        int near = cues.FindIndex(c => Math.Abs(c.Time - time) <= 2);
        if (near >= 0)
        {
            JumpToCue(lv, cues[near]);
            _edStatus = $"{cues[near].Name} already marks t {cues[near].Time}";
            return;
        }
        var cue = new EmCue(time, $"Cue {_emCueSerial++}");
        cues.Add(cue);
        cues.Sort((a, b) => a.Time - b.Time);
        if (cues.Count > 16) cues.RemoveAt(0);
        _edStatus = $"{cue.Name} marked at t {cue.Time}";
    }

    private void JumpToCue(EditableLevel lv, in EmCue cue)
    {
        _emScrollToY = (float)(ObjectPlacer.YBase - TimeToScroll(lv, cue.Time));
        _edStatus = $"{cue.Name} · t {cue.Time}";
    }

    private void DrawCuePopup(EditableLevel lv, List<EmCue> cues)
    {
        if (!ImGui.BeginPopup("##emcues")) return;
        int centre = TimeForScroll(lv,
            ObjectPlacer.YBase - (_emViewTopCv + _emViewHCv * 0.5f));
        if (UiButton("+ cue at view centre", AcRoutes,
                "Drop a session landmark at the middle of the visible canvas.", -1f))
            AddEditorCue(lv, centre);
        ImGui.Separator();
        if (cues.Count == 0)
            ImGui.TextDisabled("No cues yet · hover the map and press K");
        int kill = -1;
        for (int i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (ImGui.Selectable($"{cue.Name}   t {cue.Time}")) JumpToCue(lv, cue);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("click to jump · right-click to remove");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) kill = i;
            }
        }
        if (kill >= 0) cues.RemoveAt(kill);
        if (cues.Count > 0)
        {
            ImGui.Separator();
            if (ImGui.Selectable("clear all cues")) cues.Clear();
        }
        ImGui.EndPopup();
    }

    // =====================================================================
    // Pacing strip: spawns over TIME, the level's rhythm at a glance
    // =====================================================================

    /// <summary>
    /// A horizontal histogram of spawns per moment across the whole level, with the current
    /// view marked — the pacing view a scrolling shooter lives or dies by. Click to jump.
    /// </summary>
    private void DrawPacingStrip(EditableEpisode ep, EditableLevel lv)
    {
        const float h = 30f;
        float w = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        bool pressed = ImGui.InvisibleButton("##empace", new Vector2(Math.Max(60, w), h));
        bool hot = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();

        FlatRect(dl, p, p + new Vector2(w, h),
            active ? Mix(Gfx.Rgba(14, 16, 21), AcEdit, 0.08f) : Gfx.Rgba(14, 16, 21),
            Mix(UiPanelHi, AcEdit, active ? 0.24f : 0.1f), 5f);
        int endTime = 100;
        foreach (var e in lv.Events) endTime = Math.Max(endTime, e.Time);

        const int bins = 140;
        Span<short> count = stackalloc short[bins];
        foreach (var e in lv.Events)
        {
            if (!EventCatalog.IsSpawnType(e.Type)) continue;
            int b = Math.Clamp(e.Time * bins / (endTime + 1), 0, bins - 1);
            if (count[b] < short.MaxValue) count[b]++;
        }
        int peak = 1;
        foreach (short c in count) peak = Math.Max(peak, c);

        float bw = (w - 8f) / bins;
        for (int b = 0; b < bins; b++)
        {
            if (count[b] == 0) continue;
            float bh = MathF.Max(2f, (h - 8f) * count[b] / peak);
            float x = p.X + 4f + b * bw;
            dl.AddRectFilled(new Vector2(x, p.Y + h - 4f - bh), new Vector2(x + MathF.Max(1f, bw - 1f), p.Y + h - 4f),
                Shade(AcEdit, 0.45f + 0.55f * count[b] / peak, 220));
        }

        // Flow moments as ticks along the top edge; the end in red.
        foreach (var e in lv.Events)
        {
            if (EventCatalog.FlowLabel(e) == null) continue;
            float x = Px(p.X + 4f + (w - 8f) * e.Time / (endTime + 1));
            dl.AddLine(new Vector2(x, p.Y + 2f), new Vector2(x, p.Y + (e.Type == 11 ? h - 2f : 8f)),
                Shade(e.Type == 11 ? AcEnemy : AcRoutes, 1f, e.Type == 11 ? (byte)220 : (byte)150));
        }

        // Session cues read like DAW markers: little flags above the pacing histogram.
        foreach (var cue in EditorCues(lv))
        {
            float x = Px(p.X + 4f + (w - 8f) * Math.Clamp(cue.Time, 0, endTime) / (endTime + 1));
            uint cc = Shade(AcRoutes, 1.15f, 230);
            dl.AddTriangleFilled(new Vector2(x, p.Y + 2f), new Vector2(x + 7f, p.Y + 2f),
                new Vector2(x, p.Y + 9f), cc);
            dl.AddLine(new Vector2(x, p.Y + 2f), new Vector2(x, p.Y + h - 3f),
                Shade(AcRoutes, 0.9f, 95));
        }

        // The slice of time the canvas is looking at.
        int tBot = TimeForScroll(lv, ObjectPlacer.YBase - (_emViewTopCv + _emViewHCv));
        int tTop = TimeForScroll(lv, ObjectPlacer.YBase - _emViewTopCv);
        float vx0 = p.X + 4f + (w - 8f) * Math.Clamp(tBot, 0, endTime) / (endTime + 1);
        float vx1 = p.X + 4f + (w - 8f) * Math.Clamp(tTop, 0, endTime) / (endTime + 1);
        dl.AddRect(new Vector2(Px(vx0), Px(p.Y + 1f)), new Vector2(Px(Math.Max(vx1, vx0 + 5f)), Px(p.Y + h - 1f)),
            Shade(AcPlayer, 0.95f, 210));

        if (hot || active)
        {
            float frac = Math.Clamp((ImGui.GetMousePos().X - p.X - 4f) / (w - 8f), 0f, 1f);
            int t = (int)(frac * endTime);
            float scrubX = Px(p.X + 4f + (w - 8f) * frac);
            dl.AddLine(new Vector2(scrubX, p.Y + 2f), new Vector2(scrubX, p.Y + h - 2f),
                Shade(AcPlayer, 1.15f, active ? (byte)245 : (byte)175), active ? 2f : 1f);
            var nearCue = EditorCues(lv).FirstOrDefault(c =>
                Math.Abs((p.X + 4f + (w - 8f) * c.Time / (endTime + 1)) - ImGui.GetMousePos().X) < 6f);
            string cueTip = nearCue.Name != null ? $"\n{nearCue.Name} · t {nearCue.Time}" : "";
            ImGui.SetTooltip($"t {t} - drag to scrub · right-click to add a cue{cueTip}");
            if (hot && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                AddEditorCue(lv, t);
            // IsItemActive stays true while the press is held, including outside the strip,
            // matching the vertical minimap's pleasant continuous scrub instead of jumping
            // only once when the click is released.
            if (active || pressed)
                _emScrollToY = (float)(ObjectPlacer.YBase - TimeToScroll(lv, t));
        }
        ImGui.Dummy(new Vector2(0, 2));
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
        DrawCueLines(lv, dl, origin, z);
        if (_emShowSpawns || SpawnMode) DrawSpawnMarkers(ep, lv, dl, origin, z);

        HandleMapMouse(ep, lv, origin, z, size);
        DrawAddEventMenu(ep, lv);

        var winPos = ImGui.GetWindowPos();
        UiHint(ImGui.GetForegroundDrawList(),
            new Vector2(winPos.X + 8, winPos.Y + size.Y - 26),
            _emTool == EmSpawnPlace
                ? "click place · drag move · drag empty = select box · alt+drag copy · right-click add event · P play here"
                : _emTool == EmSpawnErase
                ? "click / drag erase · ctrl+Z/Y history · right-click add event · P play here"
                : _emTool == EmSpawnPick
                ? "click a spawn to load it into the brush · P play here"
                : _emTool == EmSpawnSelect
                ? "click / box select · drag move · ctrl+D duplicate · Delete · arrows nudge · P play here"
                : "space+drag pan · shift+wheel sideways · ctrl+wheel zoom · right-drag stamp · K cue · P play here · ctrl+Z/Y history",
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
        bool interactive = SpawnMode && ImGui.IsWindowHovered();
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

    private void DrawCueLines(EditableLevel lv, ImDrawListPtr dl, Vector2 origin, float z)
    {
        var cues = EditorCues(lv);
        if (cues.Count == 0) return;
        var win = ImGui.GetWindowPos();
        float winBot = win.Y + ImGui.GetWindowSize().Y;
        float x0 = origin.X, x1 = origin.X + LevelRenderer.CanvasW * z;
        foreach (var cue in cues)
        {
            float y = origin.Y + (float)(ObjectPlacer.YBase - TimeToScroll(lv, cue.Time)) * z;
            if (y < win.Y - 20f || y > winBot + 20f) continue;
            float py = Px(y);
            uint col = Shade(AcRoutes, 1.05f, 115);
            dl.AddLine(new Vector2(x0, py), new Vector2(x1, py), col);
            BadgeAt(dl, new Vector2(x1 - 68f, py - 16f), cue.Name, AcRoutes, 0.82f);
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
                PushEventsUndo(lv, $"add {EventCatalog.Get(ev.Type).Name}");
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

        foreach (var cue in EditorCues(lv))
        {
            float cv = (float)(ObjectPlacer.YBase - TimeToScroll(lv, cue.Time));
            float cy = Px(p.Y + cv / LevelRenderer.CanvasH * h);
            if (cy < p.Y || cy > p.Y + h) continue;
            dl.AddLine(new Vector2(p.X + 1f, cy), new Vector2(p.X + w - 1f, cy),
                Shade(AcRoutes, 1.1f, 210), 2f);
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
        if (ImGui.IsItemHovered() || active) ImGui.SetTooltip("the whole level - drag to scrub");
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
        _emCursorTime = CanvasYToTime(lv, mouse.Y);
        if (!io.WantTextInput && !io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.K) &&
            _emCursorTime >= 0)
            AddEditorCue(lv, _emCursorTime);

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
            if (SpawnMode && _emSelSet.Count > 0)
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

        if (SpawnMode)
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
        if (_emShowSpawns && io.KeyCtrl && ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
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
                    if (_emTool == 1)
                    {
                        SetCell(ep, lv, _emLayer, cellC, cellR, 0);
                        if (_emMirrorPaint)
                        {
                            int mc = cols - 1 - cellC;
                            if (mc != cellC) SetCell(ep, lv, _emLayer, mc, cellR, 0);
                        }
                    }
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
                {
                    DrawCellRect(dl, origin, z, yOffL, _emRectStart,
                        (Math.Clamp(cellC, 0, cols - 1), Math.Clamp(cellR, 0, rows - 1)), AcEdit);
                    if (_emMirrorPaint)
                        DrawCellRect(dl, origin, z, yOffL,
                            (cols - 1 - _emRectStart.C, _emRectStart.R),
                            (cols - 1 - Math.Clamp(cellC, 0, cols - 1),
                                Math.Clamp(cellR, 0, rows - 1)), AcRoutes);
                }
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
                            int id = 0;
                            bool paint = true;
                            if (_emScatter)
                            {
                                paint = _emRng.Next(100) < _emScatterPct;
                                if (paint) id = ScatterPick();
                            }
                            else
                                id = _emStamp[((r - r0) % _emStampH) * _emStampW + (c - c0) % _emStampW];
                            if (paint)
                            {
                                SetCell(ep, lv, _emLayer, c, r, id);
                                if (_emMirrorPaint)
                                {
                                    int mc = cols - 1 - c;
                                    if (mc != c) SetCell(ep, lv, _emLayer, mc, r, id);
                                }
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
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (io.KeyShift) RedoMap(ep);
            else UndoMap(ep);
        }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y)) RedoMap(ep);
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D) && SpawnMode)
            DuplicateSelection(ep, lv);
        if (io.KeyCtrl) return;
        if (SpawnMode)
        {
            int before = _emTool;
            if (ImGui.IsKeyPressed(ImGuiKey.B) || ImGui.IsKeyPressed(ImGuiKey.S))
                _emTool = EmSpawnPlace;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _emTool = EmSpawnErase;
            if (ImGui.IsKeyPressed(ImGuiKey.I)) _emTool = EmSpawnPick;
            if (ImGui.IsKeyPressed(ImGuiKey.V) || ImGui.IsKeyPressed(ImGuiKey.M))
                _emTool = EmSpawnSelect;
            if (_emTool != before)
            {
                _emLastSpawnTool = _emTool;
                _emPalette = true;
                _emPaletteMode = 1;
            }
        }
        else
        {
            if (ImGui.IsKeyPressed(ImGuiKey.B)) _emTool = 0;
            if (ImGui.IsKeyPressed(ImGuiKey.E)) _emTool = 1;
            if (ImGui.IsKeyPressed(ImGuiKey.I)) _emTool = 2;
            if (ImGui.IsKeyPressed(ImGuiKey.G)) _emTool = 3;
            if (ImGui.IsKeyPressed(ImGuiKey.M)) _emTool = 4;
            if (ImGui.IsKeyPressed(ImGuiKey.S) || ImGui.IsKeyPressed(ImGuiKey.V))
            {
                _emTool = ImGui.IsKeyPressed(ImGuiKey.V) ? EmSpawnSelect : EmSpawnPlace;
                _emLastSpawnTool = _emTool;
                _emPalette = true;
                _emPaletteMode = 1;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Key1)) _emLayer = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.Key2)) _emLayer = 1;
        if (ImGui.IsKeyPressed(ImGuiKey.Key3)) _emLayer = 2;
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && SpawnMode && _emSelSet.Count > 0)
            DeleteSpawn(ep, lv);
    }

    /// <summary>The stamp, ghosted under the cursor so painting is aimed before it lands.</summary>
    private void DrawBrushGhost(ImDrawListPtr dl, EditableLevel lv, Vector2 origin, float z,
        int cellC, int cellR, int cols, int rows, float yOffL)
    {
        if (_emTool is 1 or 2)
        {
            void EraseBox(int c)
            {
                var a1 = origin + new Vector2(c * ShapeTable.TileW,
                    yOffL + cellR * ShapeTable.TileH) * z;
                dl.AddRect(a1, a1 + new Vector2(ShapeTable.TileW, ShapeTable.TileH) * z,
                    Shade(AcEdit, 1.1f, 230), 0, 0, 1.5f);
            }
            EraseBox(cellC);
            if (_emMirrorPaint && _emTool == 1)
            {
                int mc = cols - 1 - cellC;
                if (mc != cellC) EraseBox(mc);
            }
            return;
        }
        var atlas = Atlas(SpriteSource.Tiles(char.ToLowerInvariant(lv.ShapeChar)), _palette);
        int gw = _emScatter && _emTool == 0 ? 1 : _emStampW;
        int gh = _emScatter && _emTool == 0 ? 1 : _emStampH;
        void GhostAt(int atC, bool mirrored)
        {
            for (int sy = 0; sy < gh; sy++)
                for (int sx = 0; sx < gw; sx++)
                {
                    int c = atC + sx, r = cellR + sy;
                    if (c < 0 || c >= cols || r < 0 || r >= rows) continue;
                    var a = origin + new Vector2(c * ShapeTable.TileW,
                        yOffL + r * ShapeTable.TileH) * z;
                    var b = a + new Vector2(ShapeTable.TileW, ShapeTable.TileH) * z;
                    int sourceX = mirrored ? gw - 1 - sx : sx;
                    int id = _emScatter && _emTool == 0
                        ? _emStamp[0] : _emStamp[sy * _emStampW + sourceX];
                    if (id > 0 && atlas != null)
                        atlas.Draw(dl, id, a, z, Gfx.Rgba(255, 255, 255, 150));
                    dl.AddRect(a, b, Shade(AcEdit, 0.9f, 130));
                }
            var g0 = origin + new Vector2(atC * ShapeTable.TileW,
                yOffL + cellR * ShapeTable.TileH) * z;
            var g1 = origin + new Vector2(Math.Min(cols, atC + gw) * ShapeTable.TileW,
                yOffL + Math.Min(rows, cellR + gh) * ShapeTable.TileH) * z;
            dl.AddRect(g0, g1, Shade(AcEdit, 1.15f, 235), 0, 0, 1.5f);
        }

        GhostAt(cellC, mirrored: false);
        if (_emMirrorPaint)
        {
            int mc = cols - cellC - gw;
            if (mc != cellC) GhostAt(mc, mirrored: true);
        }
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
        int cols = Level.ColsFor(_emLayer), rows = Level.RowsFor(_emLayer);
        if (_emScatter)
        {
            if (_emRng.Next(100) < _emScatterPct)
            {
                int id = ScatterPick();
                SetCell(ep, lv, _emLayer, cellC, cellR, id);
                if (_emMirrorPaint)
                {
                    int mc = cols - 1 - cellC;
                    if (mc != cellC) SetCell(ep, lv, _emLayer, mc, cellR, id);
                }
            }
            return;
        }
        ApplyStampBlock(ep, lv, cellC, cellR, mirrorBrush: false);
        if (_emMirrorPaint)
        {
            int mc = cols - cellC - _emStampW;
            if (mc != cellC) ApplyStampBlock(ep, lv, mc, cellR, mirrorBrush: true);
        }
    }

    private void ApplyStampBlock(EditableEpisode ep, EditableLevel lv, int cellC, int cellR,
        bool mirrorBrush)
    {
        int cols = Level.ColsFor(_emLayer), rows = Level.RowsFor(_emLayer);
        for (int sy = 0; sy < _emStampH; sy++)
            for (int sx = 0; sx < _emStampW; sx++)
            {
                int c = cellC + sx, r = cellR + sy;
                if (c < 0 || c >= cols || r < 0 || r >= rows) continue;
                int sourceX = mirrorBrush ? _emStampW - 1 - sx : sx;
                SetCell(ep, lv, _emLayer, c, r, _emStamp[sy * _emStampW + sourceX]);
            }
    }

    private void TransformStamp(bool flipX = false, bool flipY = false, bool rotate = false)
    {
        int oldW = _emStampW, oldH = _emStampH;
        var next = new int[_emStamp.Length];
        if (rotate)
        {
            int newW = oldH, newH = oldW;
            for (int y = 0; y < oldH; y++)
                for (int x = 0; x < oldW; x++)
                    next[x * newW + (newW - 1 - y)] = _emStamp[y * oldW + x];
            _emStampW = newW;
            _emStampH = newH;
            _emStamp = next;
            _edStatus = $"brush rotated · {_emStampW}x{_emStampH}";
            return;
        }
        for (int y = 0; y < oldH; y++)
            for (int x = 0; x < oldW; x++)
            {
                int nx = flipX ? oldW - 1 - x : x;
                int ny = flipY ? oldH - 1 - y : y;
                next[ny * oldW + nx] = _emStamp[y * oldW + x];
            }
        _emStamp = next;
        _edStatus = flipX ? "brush flipped horizontally" : "brush flipped vertically";
    }

    // =====================================================================
    // Cell operations + undo
    // =====================================================================

    private void BeginStroke(EditableEpisode ep, EditableLevel lv, string label = "")
    {
        if (_emStroke) return;
        _emStroke = true;
        if (label.Length == 0)
            label = _emTool switch
            {
                1 => "erase terrain",
                3 => "fill terrain",
                4 => "paint rectangle",
                _ when _emScatter => "scatter terrain",
                _ => "paint terrain",
            };
        PushUndo(new EmUndoStep(label, _emLayer, (byte[])lv.Cells(_emLayer).Clone(),
            (ushort[])lv.MapSh[_emLayer].Clone(), null));
    }

    private void EndStroke() => _emStroke = false;

    /// <summary>Snapshot the event list before a spawn edit (place, drag, delete).</summary>
    private void PushEventsUndo(EditableLevel lv, string label = "edit spawns")
    {
        PushUndo(new EmUndoStep(label, 0, null, null, lv.Events.ToList()));
    }

    private void PushUndo(EmUndoStep step)
    {
        _emUndo.Add(step);
        if (_emUndo.Count > EmMaxUndo) _emUndo.RemoveAt(0);
        _emRedo.Clear(); // a new branch makes the former future meaningless
    }

    private static EmUndoStep CaptureHistoryState(EditableLevel lv, in EmUndoStep shape) =>
        shape.Events != null
            ? new EmUndoStep(shape.Label, 0, null, null, lv.Events.ToList())
            : new EmUndoStep(shape.Label, shape.Layer,
                (byte[])lv.Cells(shape.Layer).Clone(),
                (ushort[])lv.MapSh[shape.Layer].Clone(), null);

    private void ApplyHistory(EditableEpisode ep, List<EmUndoStep> from,
        List<EmUndoStep> to, string verb)
    {
        var lv = EditorLevel();
        if (lv == null || from.Count == 0) return;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(CaptureHistoryState(lv, step));
        if (to.Count > EmMaxUndo) to.RemoveAt(0);
        if (step.Events != null)
        {
            lv.Events.Clear();
            lv.Events.AddRange(step.Events);
            _emSelSet.Clear();
            _emSelEvent = -1;
            _evSelected = Math.Min(_evSelected, lv.Events.Count - 1);
            NoteEventsChanged(ep);
        }
        else
        {
            Array.Copy(step.Cells!, lv.Cells(step.Layer), step.Cells!.Length);
            Array.Copy(step.Slots!, lv.MapSh[step.Layer], step.Slots!.Length);
            ep.LevelsDirty = true;
        }
        _edStatus = $"{verb} {step.Label}";
    }

    private void UndoMap(EditableEpisode ep) => ApplyHistory(ep, _emUndo, _emRedo, "undid");
    private void RedoMap(EditableEpisode ep) => ApplyHistory(ep, _emRedo, _emUndo, "redid");

    /// <summary>Paint one cell with a tile id (0 = empty), claiming a slot as needed.</summary>
    private void SetCell(EditableEpisode ep, EditableLevel lv, int layer, int c, int r, int shapeId)
    {
        byte[] cells = lv.Cells(layer);
        int i = r * Level.ColsFor(layer) + c;
        if (shapeId > 0 && _emPaintEmptyOnly && ResolveEditShape(lv, layer, cells[i]) > 0)
            return;
        int slot = lv.EnsureSlot(layer, shapeId);
        if (slot < 0)
        {
            _edStatus = $"BG{layer + 1} has no free tile slots ({EditableLevel.SlotLimit(layer)} in use) - " +
                        "remap one in the slot table.";
            return;
        }
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
        bool spawnTool = SpawnMode;
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

            // Spawns are a genuine overlay, independent of which thing is being edited:
            // terrain mode uses a slightly softer alpha, while spawn mode keeps the selected
            // encounter fully lit. This is still the exact engine anchor and terrain zoom.
            DrawSpawnSpriteAt(dl, ep, lv, o, p, z,
                spawnTool ? sel ? (byte)255 : (byte)185 : (byte)145);
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
                    (spawnTool
                        ? _emTool == EmSpawnErase ? "\nclick or drag across to erase"
                        : _emTool == EmSpawnPick ? "\nclick to load this spawn into the brush"
                        : "\ndrag to move · Delete to remove · double-click to open"
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
        _emTool = EmSpawnSelect;   // arrive holding the thing, not a loaded brush
        _emLastSpawnTool = EmSpawnSelect;
        _emPalette = true;
        _emPaletteMode = 1;
        _emSpawnPanel = 1;
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

        if (_emTool is EmSpawnErase or EmSpawnPick)
        {
            _emPressEmpty = false;
            _emMarqueeLive = false;
        }

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

        // ---- a pending press on empty space ----
        // Place tool: drag = marquee, release in place = drop the brush/wave.
        // Select tool: drag = marquee, release in place = clear the selection.
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
                    {
                        _emSpawnPanel = 1;   // the Selection tab is now the working surface
                        _edStatus = $"{_emSelSet.Count} spawns selected - drag to move, " +
                                    "Delete removes, Ctrl+D duplicates, arrows nudge";
                    }
                }
                else if (_emTool == EmSpawnPlace)
                {
                    if (_emWaveArmed >= 0) PlaceWaveAt(ep, lv, _emPressPos);
                    else PlaceSpawnAt(ep, lv, _emPressPos);
                }
                else
                {
                    SelectOnly(-1);   // Select tool: a click on nothing empties the hand
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

        // The spawn workspace mirrors the terrain's direct erase and pick tools. Erasing is
        // a stroke (one undo snapshot no matter how many markers it crosses); picking samples
        // the authored event and returns to Place with that brush ready.
        if (_emTool == EmSpawnErase)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) _emSpawnEraseStroke = false;
            if (overMarker)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var hp = origin + new Vector2(hit.X, hit.Y) * z;
                dl.AddCircle(hp, 10f, Shade(AcEnemy, 1.15f, 235), 0, 2f);
                ImGui.SetTooltip("erase this spawn · drag across more · Ctrl+Z restores the stroke");
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    if (!_emSpawnEraseStroke)
                    {
                        PushEventsUndo(lv, "erase spawns");
                        _emSpawnEraseStroke = true;
                    }
                    RemoveSpawnEventAt(ep, lv, hit.EventIndex);
                }
            }
            return;
        }
        if (_emTool == EmSpawnPick)
        {
            if (overMarker)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var hp = origin + new Vector2(hit.X, hit.Y) * z;
                dl.AddCircle(hp, 10f, Shade(AcSim, 1.15f, 235), 0, 2f);
                ImGui.SetTooltip("load this enemy, band and entry edge into the spawn brush");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    PickSpawnIntoBrush(ep, lv, hit.EventIndex);
            }
            return;
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
                if (io.KeyAlt)
                {
                    // Alt+drag: leave the originals, take the copies with you.
                    DuplicateSelection(ep, lv, dtOffset: 0);
                    grabbed = _emSelEvent;
                }
                BeginMarkerDrag(lv, grabbed, mouse);
            }
            else
            {
                // Not yet a spawn: the release decides between placing and a marquee.
                if (_emTool is EmSpawnPlace or EmSpawnSelect)
                {
                    _emPressEmpty = true;
                    _emPressPos = mouse;
                    _emMarqueeLive = false;
                }
            }
            return;
        }

        // ---- idle: ghost what a click would do ----
        if (_emTool != EmSpawnPlace) return;
        if (!overMarker && _emWaveArmed >= 0 && _emWaveArmed < _emWaves.Count)
        {
            // The armed wave, member by member, hanging off the cursor.
            var wave = _emWaves[_emWaveArmed];
            var table2 = ep.Enemies;
            int bm = BackMoveAt(lv, Math.Max(1, CanvasYToTime(lv, mouse.Y)));
            foreach (var rel in wave.Events)
            {
                float gx = ImGui.GetMousePos().X + (rel.Dat2 is (-99) or (-200)
                    ? 0 : (rel.Dat2 - wave.Events[0].Dat2)) * z;
                float gy = ImGui.GetMousePos().Y - rel.Time * Math.Max(1, bm) * z;
                int id = rel.Type is >= 49 and <= 52 ? 0 : rel.Dat;
                if (id >= 0 && id < table2.Length)
                    EditorEnemyThumbTinted(dl, table2, id,
                        new Vector2(gx - 14, gy - 16), new Vector2(gx + 14, gy + 16), 150);
            }
            var pc = ImGui.GetMousePos();
            dl.AddLine(pc - new Vector2(10, 0), pc + new Vector2(10, 0), Shade(AcEdit, 1f, 200));
            dl.AddLine(pc - new Vector2(0, 10), pc + new Vector2(0, 10), Shade(AcEdit, 1f, 200));
            int wt = SpawnTimeForY(lv, mouse.Y, -28);
            ImGui.SetTooltip($"stamp {wave.Name} ({wave.Events.Count} spawns)  ·  t {(wt < 0 ? "?" : wt)}\n" +
                             "right-click the Waves tab entry to disarm");
            return;
        }
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

    /// <summary>Remove one spawn during an eraser stroke and keep every index-based
    /// selection pointing at the same surviving records.</summary>
    private void RemoveSpawnEventAt(EditableEpisode ep, EditableLevel lv, int eventIndex)
    {
        if (eventIndex < 0 || eventIndex >= lv.Events.Count ||
            !EventCatalog.IsSpawnType(lv.Events[eventIndex].Type)) return;

        lv.Events.RemoveAt(eventIndex);
        var keep = _emSelSet.Where(i => i != eventIndex)
            .Select(i => i > eventIndex ? i - 1 : i).ToArray();
        _emSelSet.Clear();
        foreach (int i in keep) _emSelSet.Add(i);
        _emSelEvent = _emSelEvent == eventIndex ? -1
            : _emSelEvent > eventIndex ? _emSelEvent - 1 : _emSelEvent;
        _evSelected = _evSelected == eventIndex ? -1
            : _evSelected > eventIndex ? _evSelected - 1 : _evSelected;
        NoteEventsChanged(ep);
        _edStatus = "spawn erased · Ctrl+Z restores the stroke";
    }

    /// <summary>Sample an ordinary spawn into the brush, including the authored layer band
    /// and entry edge, then return to Place just like terrain Pick returns to Paint.</summary>
    private void PickSpawnIntoBrush(EditableEpisode ep, EditableLevel lv, int eventIndex)
    {
        if (eventIndex < 0 || eventIndex >= lv.Events.Count) return;
        var ev = lv.Events[eventIndex];
        if (!EventCatalog.IsSpawnType(ev.Type)) return;
        if (ev.Type is >= 49 and <= 52)
        {
            _edStatus = "That spawn carries an inline sprite, not an enemy-table entry; edit it in Events.";
            return;
        }
        if (ep.Enemies.Length == 0)
        {
            _edStatus = "This episode has no enemy-table entries to load into the brush.";
            return;
        }

        _emSpawnEnemy = Math.Clamp((int)ev.Dat, 0, ep.Enemies.Length - 1);
        if (ObjectPlacer.IsSpawn(ev.Type, out int band, out int baseEy))
        {
            _emSpawnBand = band switch { 0 => 1, 25 => 2, 50 => 3, 75 => 4, _ => 0 };
            _emSpawnBottom = baseEy > 0;
        }
        _emFormation = 0;
        _emWaveArmed = -1;
        _emTool = EmSpawnPlace;
        _emLastSpawnTool = EmSpawnPlace;
        _emPalette = true;
        _emPaletteMode = 1;
        _emSpawnPanel = 0;
        NoteRecentEnemy(_emSpawnEnemy);
        _edStatus = $"spawn brush: enemy {_emSpawnEnemy} · {SpawnBandName()}" +
                    (_emSpawnBottom ? " · from bottom" : "");
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
        float half = Math.Max(0.5f, (n - 1) * 0.5f);
        for (int i = 0; i < n; i++)
        {
            float k = i - (n - 1) * 0.5f;
            switch (_emFormation)
            {
                case 1: // row
                    yield return (k * _emFormSpacing, i * _emFormStagger);
                    break;
                case 2: // stream
                    yield return (0, i * Math.Max(4, _emFormStagger));
                    break;
                case 3: // vee
                    yield return (k * _emFormSpacing,
                        (int)(Math.Abs(k) * Math.Max(4, _emFormStagger)));
                    break;
                case 4: // diagonal sweep
                    yield return (k * _emFormSpacing, i * Math.Max(2, _emFormStagger));
                    break;
                case 5: // alternating lanes
                    yield return (((i & 1) == 0 ? -1f : 1f) * _emFormSpacing,
                        i * Math.Max(3, _emFormStagger));
                    break;
                case 6: // paired pincer, closing toward the centre
                    int rank = i / 2;
                    int ranks = (n + 1) / 2;
                    float side = (i & 1) == 0 ? -1f : 1f;
                    yield return (side * (ranks - rank) * _emFormSpacing * 0.62f,
                        rank * Math.Max(4, _emFormStagger));
                    break;
                default: // smooth arc
                    float norm = Math.Abs(k) / half;
                    yield return (k * _emFormSpacing,
                        (int)(norm * norm * Math.Max(4, _emFormStagger) * half));
                    break;
            }
        }
    }

    private void DrawFormationPreview()
    {
        var points = FormationOffsets().ToList();
        if (points.Count == 0) return;
        const float h = 60f;
        float w = ImGui.GetContentRegionAvail().X;
        var p = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        FlatRect(dl, p, p + new Vector2(w, h), Gfx.Rgba(15, 17, 22),
            Mix(UiPanelHi, AcEdit, 0.12f), 5f);
        float maxX = Math.Max(1f, points.Max(o => Math.Abs(o.Dx)));
        int minT = points.Min(o => o.Dt), maxT = points.Max(o => o.Dt);
        float sx = (w * 0.42f) / maxX;
        float sy = maxT == minT ? 0f : (h - 18f) / (maxT - minT);
        var drawn = new List<Vector2>(points.Count);
        foreach (var pt in points)
            drawn.Add(new Vector2(p.X + w * 0.5f + pt.Dx * sx,
                sy == 0f ? p.Y + h * 0.5f : p.Y + 9f + (pt.Dt - minT) * sy));
        for (int i = 1; i < drawn.Count; i++)
            dl.AddLine(drawn[i - 1], drawn[i], Shade(AcEdit, 0.7f, 105), 1f);
        for (int i = 0; i < drawn.Count; i++)
        {
            dl.AddCircleFilled(drawn[i], i == 0 ? 5f : 4f,
                Shade(i == 0 ? AcGo : AcEdit, 1.05f, 235));
            dl.AddCircle(drawn[i], i == 0 ? 5f : 4f, Gfx.Rgba(9, 11, 15, 230));
        }
        dl.AddText(p + new Vector2(6f, h - 16f), UiFaint, "space × time");
        ImGui.Dummy(new Vector2(w, h + 3f));
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
        float baseX = _emSnap ? MathF.Round(canvasMouse.X / 6f) * 6f : canvasMouse.X;
        PushEventsUndo(lv, offsets.Count > 1
            ? $"place {EmFormationNames[_emFormation]} pattern" : "place spawn");
        int lastAt = -1;
        foreach (var (dx, dt) in offsets)
        {
            var ev = new EventRec
            {
                Time = (ushort)Math.Clamp(t0 + dt, 1, 65499),
                Type = SpawnEventType(band),
                Dat = (short)_emSpawnEnemy,
                Dat2 = SpawnXForCanvas(lv, baseX + dx, band),
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
        PushEventsUndo(lv, "move spawn selection"); // one step per grab
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
        if (_emSnap) delta.X = MathF.Round(delta.X / 6f) * 6f;
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
        PushEventsUndo(lv, "nudge spawn selection");
        MoveSpawns(ep, lv, origs, new Vector2(dx * step, dy * step));
        SortEvents(lv);
    }

    /// <summary>Ctrl+D (a touch later) or Alt+drag (in place): duplicate the selection and
    /// select the copies.</summary>
    private void DuplicateSelection(EditableEpisode ep, EditableLevel lv, int dtOffset = 12)
    {
        if (_emSelSet.Count == 0) return;
        if (lv.Events.Count + _emSelSet.Count > EditableLevel.MaxEvents)
        {
            _edStatus = $"event list is full ({EditableLevel.MaxEvents})";
            return;
        }
        PushEventsUndo(lv, "duplicate spawn selection");
        var copies = new List<EventRec>();
        foreach (int idx in _emSelSet.OrderBy(i => i))
            if (idx >= 0 && idx < lv.Events.Count)
            {
                var ev = lv.Events[idx];
                ev.Time = (ushort)Math.Min(65499, ev.Time + dtOffset);
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
        _edStatus = dtOffset > 0
            ? $"duplicated {copies.Count} spawns (+{dtOffset} ticks) - drag them into place"
            : $"duplicated {copies.Count} spawns - dragging the copies";
    }

    /// <summary>Stamp an armed wave at the cursor: relative times, the whole X pattern
    /// shifted as one to land the wave's anchor under the hand.</summary>
    private void PlaceWaveAt(EditableEpisode ep, EditableLevel lv, Vector2 canvasMouse)
    {
        if (_emWaveArmed < 0 || _emWaveArmed >= _emWaves.Count) return;
        var wave = _emWaves[_emWaveArmed];
        if (lv.Events.Count + wave.Events.Count > EditableLevel.MaxEvents)
        {
            _edStatus = $"event list is full ({EditableLevel.MaxEvents})";
            return;
        }
        int t0 = SpawnTimeForY(lv, canvasMouse.Y, -28);
        if (t0 < 0)
        {
            _edStatus = "that spot is above what the level ever scrolls in";
            return;
        }
        float snapX = _emSnap ? MathF.Round(canvasMouse.X / 6f) * 6f : canvasMouse.X;
        int dx = (int)MathF.Round(snapX - wave.AnchorX);
        PushEventsUndo(lv, $"place {wave.Name}");
        _emSelSet.Clear();
        foreach (var rel in wave.Events)
        {
            var ev = rel;
            ev.Time = (ushort)Math.Clamp(t0 + rel.Time, 1, 65499);
            if (ev.Dat2 is not (-99) and not (-200))
                ev.Dat2 = (short)Math.Clamp(ev.Dat2 + dx, short.MinValue, short.MaxValue);
            int at = 0;
            while (at < lv.Events.Count && lv.Events[at].Time <= ev.Time) at++;
            lv.Events.Insert(at, ev);
            var shifted = _emSelSet.Where(i => i >= at).OrderByDescending(i => i).ToList();
            foreach (int s in shifted) { _emSelSet.Remove(s); _emSelSet.Add(s + 1); }
            _emSelSet.Add(at);
        }
        _emSelEvent = _emSelSet.Count > 0 ? _emSelSet.Max() : -1;
        NoteEventsChanged(ep);
        _edStatus = $"stamped {wave.Name} ({wave.Events.Count} spawns) at t {t0}";
    }

    private void DeleteSpawn(EditableEpisode ep, EditableLevel lv)
    {
        if (_emSelSet.Count == 0) return;
        PushEventsUndo(lv, "delete spawn selection");
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

        UiSection("Brush lab", AcEdit, $"{_emStampW}x{_emStampH}");
        if (atlas != null && _emStampW * _emStampH > 1)
        {
            DrawStampPreview(atlas);
            float tw = (ImGui.GetContentRegionAvail().X - 10f) / 3f;
            if (UiButton("flip H", AcEdit, "Mirror the grabbed stamp left-to-right.", tw))
                TransformStamp(flipX: true);
            ImGui.SameLine(0, 5);
            if (UiButton("flip V", AcEdit, "Mirror the grabbed stamp top-to-bottom.", tw))
                TransformStamp(flipY: true);
            ImGui.SameLine(0, 5);
            if (UiButton("rotate", AcEdit, "Rotate the grabbed stamp 90° clockwise.", tw))
                TransformStamp(rotate: true);
        }
        UiToggle("empty only", ref _emPaintEmptyOnly, AcEdit,
            "Protect existing terrain: paint and scatter only land in empty cells.");
        ImGui.SameLine(0, 5);
        UiToggle("mirror X", ref _emMirrorPaint, AcEdit,
            "Paint a reflected partner across the active layer's centre.\n" +
            "The ghost shows both sides before they land.");
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
                        if (_emTool is 1 or 2 || SpawnMode) _emTool = 0;
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
                    if (_emTool is 1 or 2 || SpawnMode) _emTool = 0;
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawStampPreview(SpriteAtlas atlas)
    {
        float maxW = Math.Max(40f, ImGui.GetContentRegionAvail().X);
        float maxH = 72f;
        float z = Math.Min(1f, Math.Min(maxW / (_emStampW * ShapeTable.TileW),
            maxH / (_emStampH * ShapeTable.TileH)));
        float w = _emStampW * ShapeTable.TileW * z;
        float h = _emStampH * ShapeTable.TileH * z;
        var slot = ImGui.GetCursorScreenPos();
        var p = slot + new Vector2((maxW - w) * 0.5f, 3f);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p - new Vector2(4f), p + new Vector2(w, h) + new Vector2(4f),
            Gfx.Rgba(15, 17, 22), 4f);
        for (int y = 0; y < _emStampH; y++)
            for (int x = 0; x < _emStampW; x++)
            {
                int id = _emStamp[y * _emStampW + x];
                var at = p + new Vector2(x * ShapeTable.TileW, y * ShapeTable.TileH) * z;
                if (id > 0) atlas.Draw(dl, id, at, z);
                dl.AddRect(at, at + new Vector2(ShapeTable.TileW, ShapeTable.TileH) * z,
                    Gfx.Rgba(255, 255, 255, 18));
            }
        dl.AddRect(p - new Vector2(1f), p + new Vector2(w, h) + new Vector2(1f),
            Shade(AcEdit, 0.9f, 170));
        ImGui.Dummy(new Vector2(maxW, h + 8f));
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
                BeginStroke(ep, lv, "remap tile slot"); // cells + slots travel together
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
        ImGui.SameLine(0, 5);
        UiToggle("snap", ref _emSnap, AcEdit,
            "Snap placed and stamped Xs to a 6px lane grid - tidy columns\nwithout pixel-nudging.");

        UiSection("Pattern composer", AcEdit);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo("##emform", EmFormationNames[_emFormation]))
        {
            for (int i = 0; i < EmFormationNames.Length; i++)
            {
                if (ImGui.Selectable(EmFormationNames[i], i == _emFormation))
                    _emFormation = i;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(EmFormationTips[i]);
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled(EmFormationTips[_emFormation]);
        if (_emFormation > 0)
        {
            DrawFormationPreview();
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
                        _emTool = EmSpawnPlace;
                        _emLastSpawnTool = EmSpawnPlace;
                        _emWaveArmed = -1;
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
                _emTool = EmSpawnPlace;
                _emLastSpawnTool = EmSpawnPlace;
                _emWaveArmed = -1;   // picking an enemy loads the plain brush again
                NoteRecentEnemy(id);
            }
        }
        if (shown == 0)
            UiEmpty("no enemies match", _emBankOnly
                ? "this level loads few banks - untick 'banks' to see everything"
                : "clear the filter", AcEdit);
        ImGui.EndChild();
    }

    // =====================================================================
    // Selection panel: shaping operations over the whole selection
    // =====================================================================

    private void DrawSelectionPanel(EditableEpisode ep, EditableLevel lv)
    {
        if (_emSelSet.Count == 0)
        {
            UiEmpty("nothing selected",
                "box-select spawns on the map\n(drag on empty space)", AcEdit);
            return;
        }

        var idxs = _emSelSet.Where(i => i >= 0 && i < lv.Events.Count).OrderBy(i => i).ToList();
        var times = idxs.Select(i => (int)lv.Events[i].Time).OrderBy(t => t).ToList();
        UiSection("Selection", AcEdit, $"{idxs.Count} spawns");
        if (times.Count > 0)
            ImGui.TextDisabled($"t {times[0]} .. {times[^1]}  ({times[^1] - times[0]} ticks)");
        ImGui.Dummy(new Vector2(0, 2));

        float w2 = (ImGui.GetContentRegionAvail().X - 5f) / 2f;

        // ---- shape in time ----
        UiSection("Time", AcEdit);
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("##selstag", ref _emSelStagger);
        _emSelStagger = Math.Clamp(_emSelStagger, 0, 600);
        ImGui.SameLine(0, 4);
        if (UiButton("stagger", AcEdit,
                "First member keeps its time; each next one fires this many\nticks later - turns a clump into a stream.", 0f))
            BulkRetime(ep, lv, idxs, (order, count, t0, t1, torig) => t0 + order * _emSelStagger);
        ImGui.SameLine(0, 4);
        if (UiButton("spread", AcEdit,
                "Keep the first and last times, space everyone evenly between.", 0f,
                idxs.Count < 3))
            BulkRetime(ep, lv, idxs, (order, count, t0, t1, torig) =>
                t0 + (int)Math.Round((t1 - t0) * (double)order / (count - 1)));
        if (UiButton("reverse order", AcEdit,
                "Mirror the times inside the span: the last attacker becomes the first.", w2))
            BulkRetime(ep, lv, idxs, (order, count, t0, t1, torig) => t0 + t1 - torig);
        ImGui.SameLine(0, 5);
        if (UiButton("same time", AcEdit, "Everyone fires at the first member's time.", w2))
            BulkRetime(ep, lv, idxs, (order, count, t0, t1, torig) => t0);

        // ---- shape in space ----
        UiSection("Position", AcEdit);
        if (UiButton("mirror X", AcEdit,
                "Flip the whole pattern around the playfield's centre -\nthe left-hand wave becomes its right-hand twin.", w2))
            BulkMirrorX(ep, lv, idxs);
        ImGui.SameLine(0, 5);
        if (UiButton("align X", AcEdit, "Every member takes the first one's X.", w2))
            BulkAlignX(ep, lv, idxs);
        if (UiButton("center group", AcEdit,
                "Move the whole formation sideways until its bounds sit on the\nplayfield centre.", w2))
            BulkCenterX(ep, lv, idxs);
        ImGui.SameLine(0, 5);
        if (UiButton("spread field", AcEdit,
                "Fan the selection evenly across the playable width in time order.", w2,
                idxs.Count < 2))
            BulkSpreadX(ep, lv, idxs);

        // ---- identity ----
        UiSection("Identity", AcEdit);
        if (UiButton($"set enemy -> {_emSpawnEnemy}", AcEdit,
                "Every plain spawn in the selection becomes the brush enemy.", -1f))
            BulkSetEnemy(ep, lv, idxs, _emSpawnEnemy);
        ImGui.SetNextItemWidth(70);
        ImGui.InputInt("##sellink", ref _emSelLink);
        _emSelLink = Math.Clamp(_emSelLink, 0, 255);
        ImGui.SameLine(0, 4);
        if (UiButton("set link", AcEdit,
                "Stamp this link number on the whole selection - the handle\nevents 19/25/33/70... command groups by.", 0f))
            BulkSetLink(ep, lv, idxs, _emSelLink);
        ImGui.SameLine(0, 4);
        if (UiButton("free", AcEdit, "Find a link number nothing in this level uses yet.", 0f))
        {
            var used = new HashSet<int>();
            foreach (var e in lv.Events) if (e.Dat4 != 0) used.Add(e.Dat4);
            for (int l = 1; l < 200; l++)
                if (!used.Contains(l)) { _emSelLink = l; break; }
        }

        UiSection("Wave shelf", AcEdit);
        if (UiButton("save as wave", AcEdit,
                "Keep this pattern - relative times, the X layout - on the Waves\n" +
                "shelf, ready to stamp anywhere in any level.", -1f))
            SaveSelectionAsWave(ep, lv, idxs);

        // ---- the members, jump-to on click ----
        UiSection("Members", AcEdit);
        ImGui.BeginChild("emselrows");
        var dl = ImGui.GetWindowDrawList();
        foreach (int i in idxs.OrderBy(i => lv.Events[i].Time))
        {
            if (!RowVisible(30f)) continue;
            var ev = lv.Events[i];
            var box = UiRow($"##selm{i}", i == _emSelEvent, AcEdit, 30f);
            int id = ev.Type is >= 49 and <= 52 ? 0 : ev.Dat;
            EditorEnemyThumb(dl, ep.Enemies, id, box.Min + new Vector2(4, 1),
                new Vector2(box.Min.X + 32, box.Max.Y - 1));
            RowText(box, 40f, $"t {ev.Time}",
                $"{(EventCatalog.IsSpawnType(ev.Type) ? $"enemy {ev.Dat}" : EventCatalog.Get(ev.Type).Name)}" +
                (ev.Dat4 != 0 ? $" · link {ev.Dat4}" : ""), AcEdit, box.Selected);
            if (box.Clicked)
            {
                _emSelEvent = i;
                _emScrollToY = (float)(ObjectPlacer.YBase - TimeToScroll(lv, ev.Time));
            }
        }
        ImGui.EndChild();
    }

    /// <summary>Rewrite the selection's times through a shape function of (time-order,
    /// count, first, last, member's original time), then re-sort with selections following.
    /// Original times are snapshotted first, so shapes read stable inputs.</summary>
    private void BulkRetime(EditableEpisode ep, EditableLevel lv, List<int> idxs,
        Func<int, int, int, int, int, int> shape)
    {
        if (idxs.Count == 0) return;
        PushEventsUndo(lv, "reshape spawn timing");
        var byTime = idxs.OrderBy(i => lv.Events[i].Time).ToList();
        var orig = byTime.Select(i => (int)lv.Events[i].Time).ToArray();
        int t0 = orig[0], t1 = orig[^1];
        for (int order = 0; order < byTime.Count; order++)
        {
            var ev = lv.Events[byTime[order]];
            ev.Time = (ushort)Math.Clamp(shape(order, byTime.Count, t0, t1, orig[order]), 1, 65499);
            lv.Events[byTime[order]] = ev;
        }
        NoteEventsChanged(ep);
        SortEvents(lv);
    }

    private void BulkMirrorX(EditableEpisode ep, EditableLevel lv, List<int> idxs)
    {
        EnsureEditorObjects(ep, lv);
        if (_emObjects == null) return;
        float centre = 48f + (_engaged ? GameSim.EngagedViewW : GameSim.ViewW) * 0.5f;
        var xOf = new Dictionary<int, float>();
        foreach (var o in _emObjects)
            if (o.EventIndex >= 0 && !xOf.ContainsKey(o.EventIndex)) xOf[o.EventIndex] = o.X;
        PushEventsUndo(lv, "mirror spawn formation");
        foreach (int i in idxs)
        {
            var ev = lv.Events[i];
            if (!EventCatalog.IsSpawnType(ev.Type) || ev.Dat2 is (-99) or (-200) ||
                !xOf.TryGetValue(i, out float x)) continue;
            ev.Dat2 = (short)Math.Clamp(ev.Dat2 + (int)MathF.Round((2f * centre - x) - x),
                short.MinValue, short.MaxValue);
            lv.Events[i] = ev;
        }
        NoteEventsChanged(ep);
    }

    private void BulkAlignX(EditableEpisode ep, EditableLevel lv, List<int> idxs)
    {
        var byTime = idxs.OrderBy(i => lv.Events[i].Time).ToList();
        if (byTime.Count == 0) return;
        short x = lv.Events[byTime[0]].Dat2;
        if (x is (-99) or (-200)) return;
        PushEventsUndo(lv, "align spawn formation");
        foreach (int i in byTime)
        {
            var ev = lv.Events[i];
            if (!EventCatalog.IsSpawnType(ev.Type)) continue;
            ev.Dat2 = x;
            lv.Events[i] = ev;
        }
        NoteEventsChanged(ep);
    }

    private static int SpawnBandForEvent(byte type) => type switch
    {
        15 or 18 or 50 => 1,
        7 or 23 or 32 or 51 => 3,
        10 or 52 or 56 => 4,
        _ => 2,
    };

    private void BulkCenterX(EditableEpisode ep, EditableLevel lv, List<int> idxs)
    {
        EnsureEditorObjects(ep, lv);
        if (_emObjects == null) return;
        var xOf = new Dictionary<int, float>();
        foreach (var o in _emObjects)
            if (o.EventIndex >= 0 && idxs.Contains(o.EventIndex) && !xOf.ContainsKey(o.EventIndex))
                xOf[o.EventIndex] = o.X;
        if (xOf.Count == 0) return;
        float middle = (xOf.Values.Min() + xOf.Values.Max()) * 0.5f;
        float fieldCentre = 48f + (_engaged ? GameSim.EngagedViewW : GameSim.ViewW) * 0.5f;
        float dx = fieldCentre - middle;
        PushEventsUndo(lv, "center spawn formation");
        foreach (var (i, x) in xOf)
        {
            var ev = lv.Events[i];
            if (!EventCatalog.IsSpawnType(ev.Type)) continue;
            ev.Dat2 = SpawnXForCanvas(lv, x + dx, SpawnBandForEvent(ev.Type));
            lv.Events[i] = ev;
        }
        NoteEventsChanged(ep);
        _edStatus = $"centred {xOf.Count} spawns";
    }

    private void BulkSpreadX(EditableEpisode ep, EditableLevel lv, List<int> idxs)
    {
        var byTime = idxs.Where(i => i >= 0 && i < lv.Events.Count &&
                EventCatalog.IsSpawnType(lv.Events[i].Type))
            .OrderBy(i => lv.Events[i].Time).ToList();
        if (byTime.Count < 2) return;
        float left = 62f;
        float right = 48f + (_engaged ? GameSim.EngagedViewW : GameSim.ViewW) - 14f;
        PushEventsUndo(lv, "spread spawn formation");
        for (int order = 0; order < byTime.Count; order++)
        {
            int i = byTime[order];
            var ev = lv.Events[i];
            float x = left + (right - left) * order / (byTime.Count - 1f);
            ev.Dat2 = SpawnXForCanvas(lv, x, SpawnBandForEvent(ev.Type));
            lv.Events[i] = ev;
        }
        NoteEventsChanged(ep);
        _edStatus = $"spread {byTime.Count} spawns across the playfield";
    }

    private void BulkSetEnemy(EditableEpisode ep, EditableLevel lv, List<int> idxs, int enemy)
    {
        PushEventsUndo(lv, "change selected enemies");
        foreach (int i in idxs)
        {
            var ev = lv.Events[i];
            // Only plain spawns: a 49-52's dat is a sprite, a 12's block base is a choice.
            if (ev.Type is 6 or 7 or 10 or 15 or 17 or 18 or 23 or 32 or 56)
            {
                ev.Dat = (short)enemy;
                lv.Events[i] = ev;
            }
        }
        NoteEventsChanged(ep);
    }

    private void BulkSetLink(EditableEpisode ep, EditableLevel lv, List<int> idxs, int link)
    {
        PushEventsUndo(lv, "link spawn selection");
        foreach (int i in idxs)
        {
            var ev = lv.Events[i];
            if (!EventCatalog.IsSpawnType(ev.Type)) continue;
            ev.Dat4 = (byte)Math.Clamp(link, 0, 255);
            lv.Events[i] = ev;
        }
        NoteEventsChanged(ep);
        _edStatus = $"link {link} stamped on {idxs.Count} spawns";
    }

    private void SaveSelectionAsWave(EditableEpisode ep, EditableLevel lv, List<int> idxs)
    {
        var byTime = idxs.Where(i => EventCatalog.IsSpawnType(lv.Events[i].Type))
            .OrderBy(i => lv.Events[i].Time).ToList();
        if (byTime.Count == 0) return;
        EnsureEditorObjects(ep, lv);
        int t0 = lv.Events[byTime[0]].Time;
        var wave = new WaveStamp
        {
            Name = $"Wave {_emWaves.Count + 1}",
            Enemy = lv.Events[byTime[0]].Type is >= 49 and <= 52 ? 0 : lv.Events[byTime[0]].Dat,
            AnchorX = 180f,
        };
        if (_emObjects != null)
            foreach (var o in _emObjects)
                if (o.EventIndex == byTime[0]) { wave.AnchorX = o.X; break; }
        foreach (int i in byTime)
        {
            var ev = lv.Events[i];
            ev.Time = (ushort)(ev.Time - t0);
            wave.Events.Add(ev);
        }
        _emWaves.Add(wave);
        if (_emWaves.Count > 12) _emWaves.RemoveAt(0);
        _emSpawnPanel = 2;
        _edStatus = $"{wave.Name} shelved ({wave.Events.Count} spawns) - arm it and stamp away";
    }

    // =====================================================================
    // Waves panel: the reusable shelf
    // =====================================================================

    private void DrawWavesPanel(EditableEpisode ep, EditableLevel lv)
    {
        UiSection("Waves", AcEdit, "session shelf");
        ImGui.TextDisabled("Selections saved as stamps: relative\ntimes, the X pattern moved as one.\nThey work across levels.");
        ImGui.Dummy(new Vector2(0, 3));
        if (_emWaves.Count == 0)
        {
            UiEmpty("shelf is empty",
                "select spawns on the map, then\n'save as wave' in the Sel tab", AcEdit);
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        int kill = -1;
        for (int w = 0; w < _emWaves.Count; w++)
        {
            var wave = _emWaves[w];
            bool armed = w == _emWaveArmed;
            var box = UiRow($"##emwave{w}", armed, AcEdit, 40f);
            EditorEnemyThumb(dl, ep.Enemies, wave.Enemy, box.Min + new Vector2(6, 2),
                new Vector2(box.Min.X + 42, box.Max.Y - 2));
            int span = wave.Events.Count > 0 ? wave.Events[^1].Time : 0;
            RowText(box, 50f, wave.Name,
                $"{wave.Events.Count} spawns · {span} ticks", AcEdit, armed,
                reserve: 30f);
            RowTrail(box, armed ? "armed" : "", Shade(AcGo, 1f));
            if (box.Clicked)
            {
                _emWaveArmed = armed ? -1 : w;
                if (_emWaveArmed >= 0)
                {
                    _emTool = EmSpawnPlace;
                    _emLastSpawnTool = EmSpawnPlace;
                }
            }
            if (box.Hovered)
            {
                ImGui.SetTooltip(armed
                    ? "armed - click on the map to stamp it\nclick here to disarm · right-click deletes"
                    : "click to arm the Place tool with this wave\nright-click deletes");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) kill = w;
            }
        }
        if (kill >= 0)
        {
            _emWaves.RemoveAt(kill);
            if (_emWaveArmed == kill) _emWaveArmed = -1;
            else if (_emWaveArmed > kill) _emWaveArmed--;
        }
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
