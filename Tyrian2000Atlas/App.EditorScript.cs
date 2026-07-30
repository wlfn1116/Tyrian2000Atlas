using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's script pane. The Flow side is the episode as a route: stops chained in
/// order — each with its arrival story, outpost and pre-level warning — then the episode
/// ending (endscreens, the ]Q hints) and the Timed Battle arenas, all written into a
/// correct levels{N}.dat without a hand-positioned character. The Raw side is the same
/// file as the engine reads it: a flat list of lines cut into sections by '*' markers,
/// every line freely editable, with a command reference one toggle away.
/// </summary>
public sealed unsafe partial class App
{
    private int _esSection = 1;             // 1-based section shown (raw mode)
    private int _esLine = -1;               // absolute line index selected, -1 = none
    private bool _esReference;              // show the ] command reference
    private readonly byte[] _esLineBuf = new byte[256];
    private int _esLineBufFor = -1;         // which line the buffer holds
    private readonly byte[] _esNameBuf = new byte[16];
    private int _esNameBufFor = -1;

    // ---- the Flow builder ----
    private const int EsSelEnding = -2;     // the stop list's ENDING row
    private const int EsSelBattle = -3;     // ... and its TIMED BATTLE row
    private int _esMode;                    // 0 = flow builder, 1 = raw script
    private EpisodeFlow? _esFlow;
    private bool _esFlowStale;              // the raw lines changed under the builder
    private int _esStop;                    // selected stop (or a pseudo-row sentinel)
    private int _esStopTab;                 // 0 level & story, 1 outpost, 2 warning
    private int _esEndTab;                  // 0 endscreens, 1 secret hints
    private readonly byte[] _esStopName = new byte[16];
    private int _esStopNameFor = -1;
    private readonly byte[] _esWarnBuf = new byte[1024];
    private int _esWarnBufFor = -1;
    private readonly byte[] _esHintBuf = new byte[1024];
    private int _esHintFor = -1;
    private int _esHintSel;
    private readonly byte[] _esArenaBuf = new byte[16];
    private int _esArenaBufFor = -1;
    private int _esShopPickRow = -1;        // shop row a picker popup is open for
    private bool _esCubePick;
    private int _esShopNum = 1;             // the numeric-add field in the shop picker
    private bool _esConfirmTakeover;
    private SpriteImage? _esPlanetThumb;
    private (int Planet, nint Renderer) _esPlanetKey = (-1, 0);

    /// <summary>PGR (varz.c:89): 1-based planet id to PLANET_SHAPES sprite; PAni marks the
    /// fifteen-frame spinners.</summary>
    private static readonly int[] PlanetSprite =
        { 4, 1, 2, 3, 20, 36, 52, 68, 84, 100, 116, 132, 151, 151, 151, 151, 52, 52, 1, 2, 4 };

    /// <summary>The flow model for the current script, imported on demand.</summary>
    private EpisodeFlow EnsureFlow(EditableEpisode ep)
    {
        if (_esFlow == null || _esFlowStale)
        {
            _esFlow = EpisodeFlow.FromScript(ep.ScriptLines, ep.Levels.Count);
            _esFlowStale = false;
            if (_esStop >= 0)
                _esStop = Math.Clamp(_esStop, 0, Math.Max(0, _esFlow.Stops.Count - 1));
        }
        return _esFlow;
    }

    /// <summary>The raw script changed outside the builder; re-import before showing it.</summary>
    private void NoteScriptChangedExternally() => _esFlowStale = true;

    /// <summary>A flow edit happened: rewrite the script if the builder owns it.</summary>
    private void FlowChanged(EditableEpisode ep)
    {
        if (_esFlow == null) return;
        if (_esFlow.OwnsScript)
        {
            ep.ScriptLines = _esFlow.Generate();
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
        }
        else
        {
            _edStatus = "Flow edited - press 'Rewrite the script from this flow' to apply it.";
        }
    }

    /// <summary>Line index at which 1-based section N starts (index 0 = before any marker).</summary>
    private static List<int> ScriptSections(EditableEpisode ep)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < ep.ScriptLines.Count; i++)
            if (ep.ScriptLines[i].Length > 0 && ep.ScriptLines[i][0] == '*')
                starts.Add(i + 1);
        return starts;
    }

    // =====================================================================
    // Rail list
    // =====================================================================

    private void DrawScriptSectionList(EditableEpisode ep)
    {
        if (_esMode == 0) { DrawFlowStopList(ep); return; }
        var starts = ScriptSections(ep);
        ImGui.BeginChild("essecrows", new Vector2(0, -(ImGui.GetFrameHeight() + 10f)));
        for (int s = 1; s < starts.Count; s++)
        {
            int begin = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1] - 1 : ep.ScriptLines.Count;
            string title = begin > 0 ? ep.ScriptLines[begin - 1].Trim('*', ' ') : "";
            var loads = new List<string>();
            string marks = "";
            for (int i = begin; i < end; i++)
            {
                string line = ep.ScriptLines[i];
                if (line.Length < 2 || line[0] != ']') continue;
                switch (line[1])
                {
                    case 'L': loads.Add($"#{EpisodeScript.AtoiAt(line, 25)}"); break;
                    case 'g': marks += " - galaga"; break;
                    case 'e': marks += " - engage"; break;
                    case 'I': marks += " - shop"; break;
                    case 'W': marks += " - text"; break;
                    case 'Q': marks += " - ENDING"; break;
                    case 'q': marks += " - battle over"; break;
                }
            }
            string sub = (loads.Count > 0 ? "loads " + string.Join(" ", loads)
                : end - begin == 0 ? "(empty)" : $"{end - begin} lines") + marks;

            var row = UiRow($"##essec{s}", s == _esSection, AcEdit, 40f);
            RowText(row, 12f, $"{s:00}  {(title.Length > 0 ? title : "(untitled)")}", sub,
                AcEdit, row.Selected);
            if (row.Clicked) { _esSection = s; _esLine = -1; }
        }
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0, 3));
        if (UiButton("Add section", AcEdit,
                "A new '*' marker at the end of the file. Jumps (]J n) address\nsections by their number.", -1f))
        {
            ep.ScriptLines.Add($"*SECTION {starts.Count}");
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esSection = starts.Count;
            _esLine = -1;
        }
    }

    private void DrawFlowStopList(EditableEpisode ep)
    {
        var flow = EnsureFlow(ep);
        // The two fixed rows (ending, arenas) stay pinned under the scrolling stops.
        float footer = ImGui.GetFrameHeight() * 2 + 14f + 2 * 42f;
        ImGui.BeginChild("esstops", new Vector2(0, -footer));
        for (int i = 0; i < flow.Stops.Count; i++)
        {
            var s = flow.Stops[i];
            var row = UiRow($"##esstop{i}", i == _esStop, AcEdit, 40f);
            string extras = (s.Story.Count > 0 ? "story - " : "") +
                (s.Outpost ? "outpost - " : "") + (s.SavePoint || s.SaveBackup ? "save - " : "") +
                (s.Warning.Count > 0 ? "warning - " : "") +
                (s.Galaga ? "galaga - " : "") + (s.Engage ? "engage - " : "");
            RowText(row, 12f, $"{i + 1:00}  {s.Name}",
                $"level #{s.LevelFile} - song {s.Song}" +
                (extras.Length > 0 ? " - " + extras.TrimEnd(' ', '-') : ""),
                AcEdit, row.Selected);
            if (row.Clicked && _esStop != i)
            {
                _esStop = i;
                _esStopNameFor = _esWarnBufFor = -1;
            }
        }
        if (flow.Stops.Count == 0)
            UiEmpty("no stops yet", "Add one below - each stop is a level\nwith everything around it.", AcEdit);
        ImGui.EndChild();

        // The two rows every episode has: how it ends, and the title-screen arenas.
        {
            var row = UiRow("##esend", _esStop == EsSelEnding, AcRoutes, 40f);
            int filled = flow.Ending.Hints.Count(h => h.Count > 0);
            RowText(row, 12f, "EPISODE ENDING",
                (flow.Ending.Anim ? "animation - " : "") +
                $"{flow.Ending.Screens.Count} endscreens - {filled}/9 hints",
                AcRoutes, row.Selected);
            if (row.Clicked) _esStop = EsSelEnding;

            row = UiRow("##esbattle", _esStop == EsSelBattle, AcRoutes, 40f);
            RowText(row, 12f, "TIMED BATTLE",
                flow.Arenas.Count > 0 ? $"{flow.Arenas.Count} title-screen arenas" : "no arenas",
                AcRoutes, row.Selected);
            if (row.Clicked) _esStop = EsSelBattle;
        }

        ImGui.Dummy(new Vector2(0, 3));
        bool stopSel = _esStop >= 0 && _esStop < flow.Stops.Count;
        float w = (ImGui.GetContentRegionAvail().X - 10f) / 3f;
        if (UiButton("Add stop", AcEdit, "Appends a stop; the ]L chain re-links itself.", w))
        {
            var used = flow.Stops.Select(s => s.LevelFile).ToHashSet();
            int file = 1;
            for (int f = 1; f <= ep.Levels.Count; f++)
                if (!used.Contains(f)) { file = f; break; }
            string name = EditorLevelName(file);
            flow.Stops.Add(new FlowStop
            {
                LevelFile = file,
                Name = name.Length > 0 ? name : $"LEVEL {file}",
            });
            _esStop = flow.Stops.Count - 1;
            _esStopNameFor = _esWarnBufFor = -1;
            FlowChanged(ep);
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Duplicate", AcEdit, "Copy this stop, story, outpost and all.", w, !stopSel))
        {
            flow.Stops.Insert(_esStop + 1, flow.Stops[_esStop].Clone());
            _esStop++;
            FlowChanged(ep);
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Remove", AcEnemy, "", w, !stopSel))
        {
            flow.Stops.RemoveAt(_esStop);
            _esStop = Math.Clamp(_esStop, 0, Math.Max(0, flow.Stops.Count - 1));
            FlowChanged(ep);
        }
        float w2 = (ImGui.GetContentRegionAvail().X - 5f) / 2f;
        if (UiButton("Move up", AcEdit, "", w2, !stopSel || _esStop <= 0))
        {
            (flow.Stops[_esStop - 1], flow.Stops[_esStop]) = (flow.Stops[_esStop], flow.Stops[_esStop - 1]);
            _esStop--;
            FlowChanged(ep);
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Move down", AcEdit, "", w2, !stopSel || _esStop >= flow.Stops.Count - 1))
        {
            (flow.Stops[_esStop + 1], flow.Stops[_esStop]) = (flow.Stops[_esStop], flow.Stops[_esStop + 1]);
            _esStop++;
            FlowChanged(ep);
        }
    }

    // =====================================================================
    // Detail
    // =====================================================================

    private void DrawScriptDetail(EditableEpisode ep)
    {
        int modeBefore = _esMode;
        SegBar("##esmode", ref _esMode, AcEdit, 260f,
            ("Flow", "The episode as a route of stops: levels chained in order, each\n" +
                     "with its story screens, outpost, shop, datacubes and warning -\n" +
                     "plus the ending and the Timed Battle arenas.\n" +
                     "Writes the whole script for you."),
            ("Raw script", "Every line of levels{N}.dat, hand-editable - the full ] language."));
        if (_esMode == 0 && modeBefore != 0) _esFlowStale = true;
        ImGui.Dummy(new Vector2(0, 2));
        if (_esMode == 0)
        {
            DrawFlowDetail(ep);
            return;
        }

        var starts = ScriptSections(ep);
        _esSection = Math.Clamp(_esSection, 1, Math.Max(1, starts.Count - 1));
        if (starts.Count <= 1)
        {
            UiEmpty("empty script", "add a section to begin", AcEdit);
            return;
        }
        int begin = starts[_esSection];
        int end = _esSection + 1 < starts.Count ? starts[_esSection + 1] - 1 : ep.ScriptLines.Count;

        DrawScriptToolStrip(ep, begin, end);

        float editorH = 190f;
        var avail = ImGui.GetContentRegionAvail();
        WellBegin("eslines", new Vector2(avail.X, Math.Max(120f, avail.Y - editorH - 8f)), AcEdit,
            padX: 4f, padY: 4f);
        DrawScriptLines(ep, begin, end);
        WellEnd();
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.BeginChild("eslineedit");
        if (_esReference) DrawScriptReference();
        else DrawScriptLineEditor(ep);
        ImGui.EndChild();
    }

    private void DrawFlowDetail(EditableEpisode ep)
    {
        var flow = EnsureFlow(ep);

        if (!flow.OwnsScript)
        {
            // A stock or hand-written script: the builder shows what it understood of it,
            // and takes over only on an explicit, confirmed rewrite.
            var p = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();
            FlatRect(dl, p, p + new Vector2(w, 46), Mix(UiPanel, AcSim, 0.10f),
                Mix(UiPanelHi, AcSim, 0.2f), 6f);
            dl.AddText(p + new Vector2(10, 6), Shade(AcSim, 1.05f),
                "This script was written by hand (or shipped with the game).");
            dl.AddText(p + new Vector2(10, 22), UiDim,
                $"The builder read {flow.Stops.Count} stops, {flow.Ending.Screens.Count} endscreens and " +
                $"{flow.Arenas.Count} arenas off its main route. Rewriting replaces branches.");
            ImGui.Dummy(new Vector2(w, 50));
            if (UiButton("Rewrite the script from this flow", AcGo,
                    "levels" + ep.Number + ".dat is regenerated from the stops, ending and\n" +
                    "arenas below. Conditional branches (difficulty, 2-player, timer and\n" +
                    "death routes) are dropped; story text rides along on its main route.\n" +
                    "Reload (or Revert) brings the old script back until you save."))
                _esConfirmTakeover = true;
            DrawTakeoverConfirm(ep, flow);
            ImGui.Dummy(new Vector2(0, 4));
        }

        if (_esStop == EsSelEnding) { DrawEndingEditor(ep, flow); return; }
        if (_esStop == EsSelBattle) { DrawBattleEditor(ep, flow); return; }
        if (flow.Stops.Count == 0 || _esStop >= flow.Stops.Count || _esStop < 0)
        {
            UiEmpty("no stop selected", "add a stop in the list on the left, or edit\nthe EPISODE ENDING and TIMED BATTLE rows", AcEdit);
            return;
        }

        ImGui.BeginChild("esflowdetail");
        DrawFlowStopEditor(ep, flow, flow.Stops[_esStop]);
        ImGui.EndChild();
    }

    private void DrawTakeoverConfirm(EditableEpisode ep, EpisodeFlow flow)
    {
        if (_esConfirmTakeover) { ImGui.OpenPopup("Rewrite the script?"); _esConfirmTakeover = false; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Rewrite the script?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text($"Replace levels{ep.Number}.dat's script with the {flow.Stops.Count}-stop flow?");
        ImGui.TextDisabled("Story screens, the ending and the arenas come along. Conditional\n" +
                           "jumps (difficulty, 2-player, timer, death routes) are dropped.\n" +
                           "Nothing touches disk until you save.");
        ImGui.Dummy(new Vector2(0, 4));
        if (UiButton("Rewrite", AcGo, "", 110f))
        {
            flow.OwnsScript = true;
            FlowChanged(ep);
            _edStatus = $"Script rewritten from the flow ({flow.Stops.Count} stops).";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8);
        if (UiButton("Cancel", AcEdit, "", 110f)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // =====================================================================
    // Stop editor
    // =====================================================================

    private void DrawFlowStopEditor(EditableEpisode ep, EpisodeFlow flow, FlowStop s)
    {
        bool ch = false;

        UiSection($"Stop {_esStop + 1}", AcEdit,
            _esStop + 1 < flow.Stops.Count ? $"then stop {_esStop + 2}" : "then the episode ending");

        // ---- the level itself ----
        ImGui.SetNextItemWidth(200);
        string cur = $"#{s.LevelFile}  {EditorLevelName(s.LevelFile)}";
        if (ImGui.BeginCombo("level", cur))
        {
            for (int f = 1; f <= ep.Levels.Count; f++)
            {
                string label = $"#{f}  {EditorLevelName(f)}";
                if (ImGui.Selectable(label, f == s.LevelFile) && f != s.LevelFile)
                {
                    s.LevelFile = f;
                    string nm = EditorLevelName(f);
                    if (nm.Length > 0) { s.Name = nm; _esStopNameFor = -1; }
                    ch = true;
                }
            }
            ImGui.EndCombo();
        }

        if (_esStopNameFor != _esStop)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                s.Name.Length > 9 ? s.Name[..9] : s.Name, _esStopName);
            _esStopName[n] = 0;
            _esStopNameFor = _esStop;
        }
        ImGui.SameLine(0, 14);
        ImGui.SetNextItemWidth(120);
        fixed (byte* p = _esStopName)
            if (ImGui.InputText("name (9 chars)", p, 10))
            {
                s.Name = BufText(_esStopName);
                ch = true;
            }
        ImGui.SameLine(0, 14);
        int song = s.Song;
        if (SongCombo("song", ref song, 220f, "]L song field - 1..41, music.mus index."))
        {
            s.Song = Math.Clamp(song, 1, 41);
            ch = true;
        }

        ch |= ImGui.Checkbox("save point", ref s.SavePoint);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("]s - dying after this sends the player back here.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("backup save", ref s.SaveBackup);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("]b - writes the LAST LEVEL save slot on arrival.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("galaga", ref s.Galaga);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("]g - Galaga mini-game rules for this level.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("engage", ref s.Engage);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("]e - Super Tyrian rules: Stalker 21.126, Atomic\nRailGun, no cash. Used for the mini-games.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("extra", ref s.Extra);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("]x - extra-game mode flag.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("bonus", ref s.Bonus);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("'$' - a bonus stage: dying still counts as clearing it.");
        ImGui.SameLine(0, 10);
        ch |= ImGui.Checkbox("normal bonus", ref s.NormalBonus);

        ImGui.Dummy(new Vector2(0, 4));
        SegBar("##esstoptabs", ref _esStopTab, AcEdit, 430f,
            ("Story screens", _esStop == 0
                ? "Text screens shown when the episode starts - the intro."
                : "Text screens shown on arrival, before the outpost."),
            ("Outpost", "The shop, datacube shelf and galaxy-map hop before the level."),
            ("Warning", "The classic flashing WARNING screen right before the level."));
        ImGui.Dummy(new Vector2(0, 4));

        switch (_esStopTab)
        {
            case 0:
                ImGui.BeginChild("esstorytab");
                ch |= DrawStoryScreens(ep, s.Story, $"stop{_esStop}",
                    _esStop == 0
                        ? "Shown when the episode starts, before anything else - this is the episode intro."
                        : "Shown when the player arrives at this stop, before its outpost opens.");
                ImGui.EndChild();
                break;
            case 1:
                ImGui.BeginChild("esoutposttab");
                ch |= DrawOutpostEditor(ep, flow, s);
                ImGui.EndChild();
                break;
            default:
                ImGui.BeginChild("eswarntab");
                ch |= DrawWarningEditor(s);
                ImGui.EndChild();
                break;
        }

        if (ch) FlowChanged(ep);
    }

    private bool DrawWarningEditor(FlowStop s)
    {
        bool ch = false;
        ImGui.TextColored(ColorOf(UiDim),
            "Shown right before the level starts (after the outpost). Empty text = no warning.");
        ImGui.Dummy(new Vector2(0, 2));

        float formW = Math.Max(300f, ImGui.GetContentRegionAvail().X - 356f);
        ImGui.BeginChild("eswarnform", new Vector2(formW, 0));
        UiSection("Warning text", AcEdit,
            s.Warning.Count > 0 ? $"{s.Warning.Count}/{StoryScreen.MaxLines} lines" : "off");
        if (_esWarnBufFor != _esStop)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                string.Join('\n', s.Warning), new Span<byte>(_esWarnBuf, 0, _esWarnBuf.Length - 1));
            _esWarnBuf[n] = 0;
            _esWarnBufFor = _esStop;
        }
        fixed (byte* p = _esWarnBuf)
            if (ImGui.InputTextMultiline("##eswarn", p, (nuint)_esWarnBuf.Length,
                    new Vector2(ImGui.GetContentRegionAvail().X - 10, ImGui.GetTextLineHeight() * 8f)))
            {
                s.Warning = BufText(_esWarnBuf)
                    .Split('\n').Select(StoryScreen.ClipLine)
                    .Take(StoryScreen.MaxLines).ToList();
                while (s.Warning.Count > 0 && s.Warning[^1].Length == 0)
                    s.Warning.RemoveAt(s.Warning.Count - 1);
                ch = true;
            }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stock warnings open with a line like 'Warning:' and leave\nblank lines between paragraphs.");

        bool frame = s.WarnFrame;
        if (UiToggle("flashing WARNING bars + siren", ref frame, AcEdit,
                "]Wy - the pulsing bars and the warning sound."))
        {
            s.WarnFrame = frame;
            ch = true;
        }
        ImGui.SetNextItemWidth(110);
        int red = s.WarnRed;
        if (ImGui.InputInt("red alert (0-9)", ref red)) { s.WarnRed = Math.Clamp(red, 0, 9); ch = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Non-zero: red colour bank, text at the very top.\nStock warnings keep this 0.");
        ImGui.SetNextItemWidth(110);
        int speed = s.WarnSpeed;
        if (ImGui.InputInt("type-in speed", ref speed)) { s.WarnSpeed = Math.Clamp(speed, 0, 9); ch = true; }
        ImGui.EndChild();

        ImGui.SameLine(0, 8f);
        ImGui.BeginChild("eswarnprev");
        if (ch) _essSerial++;
        DrawTextScreenPreview(-1, s.Warning, s.WarnFrame, s.WarnRed > 0, $"warn{_esStop}");
        ImGui.EndChild();
        return ch;
    }

    // =====================================================================
    // Outpost
    // =====================================================================

    private bool DrawOutpostEditor(EditableEpisode ep, EpisodeFlow flow, FlowStop s)
    {
        bool ch = false;
        bool outpost = s.Outpost;
        if (UiToggle("outpost before this level (shop + datacubes + map hop)", ref outpost, AcEdit,
                "The between-levels screen: buy equipment, read datacubes,\nthen launch at the level from the galaxy map."))
        {
            s.Outpost = outpost;
            ch = true;
        }
        if (!s.Outpost)
        {
            ImGui.TextColored(ColorOf(UiFaint),
                "No outpost: finishing the previous level goes straight to this one" +
                (s.Warning.Count > 0 ? " (after the warning)." : "."));
            return ch;
        }

        int osong = s.OutpostSong;
        if (SongCombo("shop song", ref osong, 220f,
                "]i - the music the shop plays. Stock outposts use 2.", zeroLabel: "(keep current)"))
        {
            s.OutpostSong = Math.Clamp(osong, 0, 41);
            ch = true;
        }
        ImGui.SameLine(0, 14);
        ch |= DrawPlanetPicker(s);

        if (_esStop > 0 && flow.Stops[_esStop - 1].Outpost &&
            UiButton("copy shop + cubes from the previous outpost", AcEdit, "", -1f))
        {
            var prev = flow.Stops[_esStop - 1];
            s.Shop = FlowStop.NewShop();
            for (int r = 0; r < s.Shop.Length; r++) s.Shop[r] = prev.Shop[r].ToList();
            s.Cubes = prev.Cubes.ToList();
            s.CubesFree = prev.CubesFree;
            s.OutpostSong = prev.OutpostSong;
            ch = true;
        }

        ch |= DrawFlowCubes(ep, s);
        ch |= DrawFlowShop(ep, s);
        return ch;
    }

    /// <summary>The galaxy-map planet, picked by name with its sprite beside the combo.</summary>
    private bool DrawPlanetPicker(FlowStop s)
    {
        bool ch = false;
        var names = _gd?.PlanetNameList ?? new List<string> { "" };
        string NameOf(int pl)
        {
            string n = PlanetNames.Get(names, pl);
            return n.Length > 0 ? $"{pl:00}  {n}" : $"{pl:00}  planet {pl}";
        }

        // The selected planet's face, drawn from the PLANET_SHAPES bank.
        var bank = _gd?.Main.Banks[3];
        int spriteId = s.MapPlanet >= 1 && s.MapPlanet <= PlanetSprite.Length
            ? PlanetSprite[s.MapPlanet - 1] - 1 : -1;
        var spr = spriteId >= 0 ? bank?.Get(spriteId) : null;
        if (spr != null && _gd != null)
        {
            nint rh = (nint)_activeRenderer.Handle;
            if (_esPlanetKey != (s.MapPlanet, rh) || _esPlanetThumb == null)
            {
                _esPlanetThumb ??= new SpriteImage();
                _esPlanetThumb.Update(_activeRenderer, spr, _gd.Palettes.Get(AppSettings.GamePalette));
                _esPlanetKey = (s.MapPlanet, rh);
            }
            var at = ImGui.GetCursorScreenPos();
            float box = ImGui.GetFrameHeight();
            float sc = Math.Min(box / spr.W, box / spr.H);
            _esPlanetThumb.Draw(ImGui.GetWindowDrawList(),
                at + new Vector2((box - spr.W * sc) * 0.5f, (box - spr.H * sc) * 0.5f), sc);
            ImGui.Dummy(new Vector2(box, box));
            ImGui.SameLine(0, 5);
        }
        ImGui.SetNextItemWidth(190);
        if (ImGui.BeginCombo("map planet", NameOf(s.MapPlanet)))
        {
            for (int pl = 1; pl <= 21; pl++)
                if (ImGui.Selectable(NameOf(pl), pl == s.MapPlanet) && pl != s.MapPlanet)
                {
                    s.MapPlanet = pl;
                    ch = true;
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("]G - the planet the outpost's departure map centres on for\n" +
                             "the hop to this level. Names come from tyrian.hdt; planets\n" +
                             "1-11 are always drawn on the map.");
        return ch;
    }

    private bool DrawFlowCubes(EditableEpisode ep, FlowStop s)
    {
        bool ch = false;
        UiSection("Datacubes", AcEdit, s.Cubes.Count > 0 ? $"{s.Cubes.Count}/4 on the shelf" : "none");
        int kill = -1;
        for (int i = 0; i < s.Cubes.Count; i++)
        {
            var cube = s.Cubes[i] >= 1 && s.Cubes[i] <= ep.Cubes.Count ? ep.Cubes[s.Cubes[i] - 1] : null;
            ImGui.PushID(i);
            if (UiButton("x", AcEnemy, "remove", 24f)) kill = i;
            ImGui.PopID();
            ImGui.SameLine(0, 6);
            ImGui.AlignTextToFramePadding();
            string title = cube == null ? "(no such cube)"
                : cube.Title.Replace("~", "").Trim();
            if (title.Length > 46) title = title[..46];
            ImGui.Text($"{s.Cubes[i]:000}  {title}" + (i < s.CubesFree ? "" : "   [needs pickups]"));
        }
        if (kill >= 0) { s.Cubes.RemoveAt(kill); ch = true; }
        if (s.Cubes.Count < 4 && UiButton("+ add datacube", AcEdit,
                "The outpost's reading shelf; the first 'free' ones need no\ndatacube pickups in the level before."))
            _esCubePick = true;
        if (s.Cubes.Count > 0)
        {
            ImGui.SameLine(0, 10);
            ImGui.SetNextItemWidth(90);
            int free = s.CubesFree;
            if (ImGui.InputInt("free to read", ref free))
            {
                s.CubesFree = Math.Clamp(free, 0, s.Cubes.Count);
                ch = true;
            }
        }
        if (UiButton("write the readings themselves...", AcEdit,
                $"Open the Cubes workspace: cubetxt{ep.Number}.dat, where the actual\n" +
                "text of every datacube lives - fully editable."))
        {
            _edMode = 3;
            if (s.Cubes.Count > 0) _ecSelected = s.Cubes[0] - 1;
        }

        if (_esCubePick) { ImGui.OpenPopup("##escubepick"); _esCubePick = false; }
        ImGui.SetNextWindowSize(new Vector2(430, 420), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##escubepick"))
        {
            for (int idx = 0; idx < ep.Cubes.Count; idx++)
            {
                var cube = ep.Cubes[idx];
                if (cube.IsEmpty) continue;
                if (ImGui.Selectable($"{idx + 1:000}  {cube.Title.Replace("~", "").Trim()}"))
                {
                    if (s.Cubes.Count < 4) { s.Cubes.Add(idx + 1); ch = true; }
                    ImGui.CloseCurrentPopup();
                }
            }
            if (ep.Cubes.Count == 0)
                ImGui.TextDisabled("no cubetxt file loaded for this episode");
            ImGui.EndPopup();
        }
        return ch;
    }

    private bool DrawFlowShop(EditableEpisode ep, FlowStop s)
    {
        bool ch = false;
        UiSection("Shop stock", AcEdit);
        ImGui.TextDisabled("What each upgrade row offers (10 per row at most). Click an item to remove it.");
        var items = _gd != null && EditorEpisodeInfo != null
            ? _gd.GetItems(EditorEpisodeInfo, _itemFork) : null;

        for (int r = 0; r < EpisodeFlow.ShopRowCount; r++)
        {
            ImGui.PushID(r);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ColorOf(UiFaint), EpisodeFlow.ShopRowLabels[r].PadRight(6));
            ImGui.SameLine(0, 6);
            int kill = -1;
            for (int i = 0; i < s.Shop[r].Count; i++)
            {
                if (i > 0) ImGui.SameLine(0, 4);
                ImGui.PushID(i);   // UiButton draws its label verbatim: uniqueness comes
                if (UiButton($"{s.Shop[r][i]} {FlowItemName(items, r, s.Shop[r][i])}".TrimEnd(),
                        AcEdit, "click to remove"))
                    kill = i;
                ImGui.PopID();
            }
            if (kill >= 0) { s.Shop[r].RemoveAt(kill); ch = true; }
            if (s.Shop[r].Count > 0) ImGui.SameLine(0, 4);
            bool full = s.Shop[r].Count >= EpisodeFlow.ShopRowMax;
            ImGui.PushID("add");
            if (UiButton("+", AcGo, full
                    ? $"the engine's rows hold {EpisodeFlow.ShopRowMax} items"
                    : "add an item to this row", 24f, full))
            {
                _esShopPickRow = r;
                _esShopNum = 1;
            }
            ImGui.PopID();
            if (r is 4 or 7)
            {
                ImGui.SameLine(0, 8);
                ImGui.TextColored(ColorOf(UiFaint), "(read by the engine, never sold)");
            }
            ImGui.PopID();
        }

        if (_esShopPickRow >= 0) ImGui.OpenPopup("##esshoppick");
        ImGui.SetNextWindowSize(new Vector2(360, 430), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("##esshoppick"))
        {
            int r = Math.Clamp(_esShopPickRow, 0, EpisodeFlow.ShopRowCount - 1);
            ImGui.TextDisabled($"{EpisodeFlow.ShopRowLabels[r]} row");
            ImGui.Separator();
            bool added = false;
            void Offer(int id, string name)
            {
                if (!ImGui.Selectable($"{id}  {name}")) return;
                var flow = EnsureFlow(ep);
                if (_esStop >= 0 && _esStop < flow.Stops.Count)
                {
                    var stop = flow.Stops[_esStop];
                    if (!stop.Shop[r].Contains(id) && stop.Shop[r].Count < EpisodeFlow.ShopRowMax)
                        stop.Shop[r].Add(id);
                }
                added = true;
            }
            if (items != null && items.Loaded)
            {
                switch (r)
                {
                    case 0:
                        for (int i = 1; i < items.Ships.Length; i++)
                            if (items.Ships[i]?.Name.Trim().Length > 0) Offer(i, items.Ships[i]!.Name.Trim());
                        break;
                    case 1 or 2:
                        Offer(0, "(none)");
                        for (int i = 1; i < items.Ports.Length; i++)
                            if (items.Ports[i]?.Name.Trim().Length > 0) Offer(i, items.Ports[i]!.Name.Trim());
                        break;
                    case 3:
                        for (int i = 1; i < items.Powers.Length; i++)
                            if (items.Powers[i]?.Name.Trim().Length > 0) Offer(i, items.Powers[i]!.Name.Trim());
                        break;
                    case 5 or 6:
                        Offer(0, "(none)");
                        for (int i = 1; i < items.Options.Length; i++)
                            if (items.Options[i]?.Name.Trim().Length > 0) Offer(i, items.Options[i]!.Name.Trim());
                        break;
                    case 8:
                        for (int i = 1; i < items.Shields.Length; i++)
                            if (items.Shields[i]?.Name.Trim().Length > 0) Offer(i, items.Shields[i]!.Name.Trim());
                        break;
                    default:
                    {
                        // Engine and Armor rows are plain upgrade levels, not table items.
                        ImGui.SetNextItemWidth(90);
                        ImGui.InputInt("level", ref _esShopNum);
                        _esShopNum = Math.Clamp(_esShopNum, 1, 30);
                        ImGui.SameLine(0, 6);
                        if (UiButton("add", AcGo, "", 60f)) Offer(_esShopNum, "");
                        for (int i = 1; i <= 5; i++) Offer(i, $"level {i}");
                        break;
                    }
                }
            }
            if (added)
            {
                ch = true;
                _esShopPickRow = -1;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        else _esShopPickRow = -1;
        return ch;
    }

    private static string FlowItemName(ItemData? items, int row, int id)
    {
        if (items == null || !items.Loaded || id <= 0) return id == 0 ? "(none)" : "";
        string? name = row switch
        {
            0 => id < items.Ships.Length ? items.Ships[id]?.Name : null,
            1 or 2 => id < items.Ports.Length ? items.Ports[id]?.Name : null,
            3 => id < items.Powers.Length ? items.Powers[id]?.Name : null,
            5 or 6 => id < items.Options.Length ? items.Options[id]?.Name : null,
            8 => id < items.Shields.Length ? items.Shields[id]?.Name : null,
            _ => null,
        };
        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? "" : name.Length > 16 ? name[..16] : name;
    }

    // =====================================================================
    // Ending editor
    // =====================================================================

    private void DrawEndingEditor(EditableEpisode ep, EpisodeFlow flow)
    {
        bool ch = false;
        var end = flow.Ending;

        UiSection("Episode ending", AcRoutes,
            flow.Stops.Count > 0 ? $"after stop {flow.Stops.Count}" : "nothing leads here yet");
        ImGui.TextColored(ColorOf(UiDim),
            "Finishing the last stop plays this: the endscreens, then the score + secret-hint\n" +
            "screen (]Q), and the game moves on to the next episode by itself.");
        ImGui.Dummy(new Vector2(0, 2));

        bool anim = end.Anim;
        if (UiToggle("play the ending animation first (tyrend.anm)", ref anim, AcRoutes,
                "]A - the fleet-destruction animation episode 3 ends on.\nOne animation exists; every episode may play it."))
        {
            end.Anim = anim;
            ch = true;
        }
        if (end.Anim)
        {
            ImGui.SameLine(0, 12);
            int am = end.AnimMusic;
            if (SongCombo("anim song", ref am, 220f,
                    "Started just before the animation. Stock uses 9.", zeroLabel: "(keep playing)"))
            {
                end.AnimMusic = Math.Clamp(am, 0, 41);
                ch = true;
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        SegBar("##esendtabs", ref _esEndTab, AcRoutes, 320f,
            ("Endscreens", "The story screens the episode goes out on."),
            ("Score + hints", "The ]Q screen: total score plus one of nine secret hints."));
        ImGui.Dummy(new Vector2(0, 4));

        if (_esEndTab == 0)
        {
            ImGui.BeginChild("esendscreens");
            ch |= DrawStoryScreens(ep, end.Screens, "ending",
                "Played when the last stop's level is cleared, before the score screen.");
            ImGui.EndChild();
        }
        else
        {
            ImGui.BeginChild("esendhints");
            ch |= DrawHintsEditor(end);
            ImGui.EndChild();
        }

        if (ch) FlowChanged(ep);
    }

    private bool DrawHintsEditor(EpisodeEnding end)
    {
        bool ch = false;
        float listW = 190f;
        var avail = ImGui.GetContentRegionAvail();
        WellBegin("eshintlist", new Vector2(listW, avail.Y), AcRoutes, padX: 6f, padY: 6f);
        UiSection("Hint blocks", AcRoutes, $"{end.Hints.Count(h => h.Count > 0)}/9");
        ImGui.TextColored(ColorOf(UiFaint), "3 groups of 3; each save\nprofile is locked to one\ngroup and sees a random\nhint of it per ending.");
        ImGui.Dummy(new Vector2(0, 3));
        for (int i = 0; i < EpisodeEnding.HintCount; i++)
        {
            if (i % 3 == 0)
                UiSection($"Group {i / 3 + 1}", AcRoutes);
            var block = end.Hints[i];
            string first = block.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(empty)";
            if (first.Length > 18) first = first[..18];
            var row = UiRow($"##eshint{i}", i == _esHintSel, AcRoutes, 26f);
            RowText(row, 8f, $"{i / 3 + 1}-{i % 3 + 1}  {first}", "", AcRoutes, row.Selected);
            if (row.Clicked) { _esHintSel = i; _esHintFor = -1; }
        }
        WellEnd();

        ImGui.SameLine(0, 8f);
        ImGui.BeginChild("eshintdetail");
        _esHintSel = Math.Clamp(_esHintSel, 0, EpisodeEnding.HintCount - 1);
        UiSection($"Hint {_esHintSel / 3 + 1}-{_esHintSel % 3 + 1}", AcRoutes,
            $"up to {EpisodeEnding.MaxHintLines} lines");
        ImGui.TextColored(ColorOf(UiDim),
            "Shown under the player's score. Empty = the screen shows just the score.\n" +
            "Stock hints open with a title line, a blank line, then the tip.");
        if (_esHintFor != _esHintSel)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                string.Join('\n', end.Hints[_esHintSel]),
                new Span<byte>(_esHintBuf, 0, _esHintBuf.Length - 1));
            _esHintBuf[n] = 0;
            _esHintFor = _esHintSel;
        }
        fixed (byte* p = _esHintBuf)
            if (ImGui.InputTextMultiline("##eshinttext", p, (nuint)_esHintBuf.Length,
                    new Vector2(ImGui.GetContentRegionAvail().X - 8f, ImGui.GetTextLineHeight() * 8f)))
            {
                end.Hints[_esHintSel] = BufText(_esHintBuf)
                    .Split('\n').Select(StoryScreen.ClipLine)
                    .Take(EpisodeEnding.MaxHintLines).ToList();
                while (end.Hints[_esHintSel].Count > 0 && end.Hints[_esHintSel][^1].Length == 0)
                    end.Hints[_esHintSel].RemoveAt(end.Hints[_esHintSel].Count - 1);
                ch = true;
            }

        UiSection("The score screen's dress", AcRoutes);
        int hm = end.HintMusic;
        if (SongCombo("song", ref hm, 220f, "Stock endings play 31 here.",
                zeroLabel: "(keep playing)"))
        {
            end.HintMusic = Math.Clamp(hm, 0, 41);
            ch = true;
        }
        ImGui.SameLine(0, 12);
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("backdrop", PicLabel(end.HintPic)))
        {
            foreach (var (pic, _) in PicChoices)
                if (ImGui.Selectable(PicLabel(pic), pic == end.HintPic) && pic != end.HintPic)
                {
                    end.HintPic = pic;
                    ch = true;
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stock endings use picture 5.");

        if (ch) _essSerial++;
        var preview = new List<string> { "Your score:  1234567", "" };
        preview.AddRange(end.Hints[_esHintSel]);
        DrawTextScreenPreview(end.HintPic, preview, warningFrame: false, red: false,
            $"hint{_esHintSel}");
        ImGui.EndChild();
        return ch;
    }

    // =====================================================================
    // Timed battle editor
    // =====================================================================

    private void DrawBattleEditor(EditableEpisode ep, EpisodeFlow flow)
    {
        bool ch = false;
        UiSection("Timed Battle arenas", AcRoutes, $"{flow.Arenas.Count}/{EpisodeFlow.MaxArenas}");
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColorOf(UiDim),
            "The title screen's Timed Battle mode offers five arena slots per episode (]T). " +
            "Each is one level played against the clock for a high score - separate from the " +
            "campaign route. With no arenas, the mode simply has nothing here.");
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(0, 4));

        int kill = -1;
        for (int i = 0; i < flow.Arenas.Count; i++)
        {
            var a = flow.Arenas[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ColorOf(UiFaint), $"slot {i + 1}");
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(190);
            string cur = $"#{a.LevelFile}  {EditorLevelName(a.LevelFile)}";
            if (ImGui.BeginCombo("##arlv", cur))
            {
                for (int f = 1; f <= ep.Levels.Count; f++)
                    if (ImGui.Selectable($"#{f}  {EditorLevelName(f)}", f == a.LevelFile) && f != a.LevelFile)
                    {
                        a.LevelFile = f;
                        string nm = EditorLevelName(f);
                        if (nm.Length > 0) { a.Name = nm; _esArenaBufFor = -1; }
                        ch = true;
                    }
                ImGui.EndCombo();
            }
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(110);
            // One shared buffer serves the row being typed in; other rows draw read-only.
            if (_esArenaBufFor == i)
            {
                fixed (byte* p = _esArenaBuf)
                    if (ImGui.InputText("##arname", p, 10))
                    {
                        a.Name = BufText(_esArenaBuf);
                        ch = true;
                    }
            }
            else
            {
                string shown = a.Name.Length > 0 ? a.Name : "(name)";
                if (ImGui.Button($"{shown}##arnb", new Vector2(110, 0)))
                {
                    int n = System.Text.Encoding.Latin1.GetBytes(
                        a.Name.Length > 9 ? a.Name[..9] : a.Name, _esArenaBuf);
                    _esArenaBuf[n] = 0;
                    _esArenaBufFor = i;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("click to edit the arena's 9-char name");
            }
            ImGui.SameLine(0, 8);
            int song = a.Song;
            if (SongCombo("song", ref song, 200f)) { a.Song = Math.Clamp(song, 1, 41); ch = true; }
            ImGui.SameLine(0, 8);
            if (UiButton("x", AcEnemy, "remove this arena", 26f)) kill = i;
            ImGui.PopID();
        }
        if (kill >= 0)
        {
            flow.Arenas.RemoveAt(kill);
            _esArenaBufFor = -1;
            ch = true;
        }
        if (flow.Arenas.Count < EpisodeFlow.MaxArenas &&
            UiButton("+ add arena", AcGo, "Empty title-screen slots repeat the last arena, like stock data."))
        {
            var a = new BattleArena { LevelFile = 1, Name = EditorLevelName(1), Song = 1 };
            if (a.Name.Length == 0) a.Name = "ARENA";
            flow.Arenas.Add(a);
            ch = true;
        }
        if (flow.Arenas.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.TextColored(ColorOf(UiFaint),
                "Arenas play the level as authored; the clock and scoring come from the mode itself.");
        }

        if (ch) FlowChanged(ep);
    }

    // =====================================================================
    // Raw script side
    // =====================================================================

    private void DrawScriptToolStrip(EditableEpisode ep, int begin, int end)
    {
        BandBegin("esband", AcEdit);
        bool haveSel = _esLine >= begin - 1 && _esLine < end;
        if (UiButton("+ line", AcEdit, "Insert an empty line after the selected one."))
        {
            int at = haveSel ? _esLine + 1 : end;
            ep.ScriptLines.Insert(at, "");
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine = at;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("+ level (]L)", AcEdit, "Insert a level-load line, ready to fill in."))
        {
            int at = haveSel ? _esLine + 1 : end;
            ep.ScriptLines.Insert(at, BuildLevelLine(9999, "NEW LEVEL", 1, 1, false, false));
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine = at;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("+ text (]W)", AcEdit, "Insert a text-screen block: ]Wn, two lines, and its closing '#'."))
        {
            int at = haveSel ? _esLine + 1 : end;
            ep.ScriptLines.InsertRange(at, new[] { "]Wn 03[", "Text line.", "", "#" });
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine = at;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Delete", AcEnemy, "Remove the selected line.", 0f, !haveSel))
        {
            ep.ScriptLines.RemoveAt(_esLine);
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine = -1;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Up", AcEdit, "", 0f, !haveSel || _esLine <= begin))
        {
            (ep.ScriptLines[_esLine - 1], ep.ScriptLines[_esLine]) =
                (ep.ScriptLines[_esLine], ep.ScriptLines[_esLine - 1]);
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine--;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Down", AcEdit, "", 0f, !haveSel || _esLine >= end - 1))
        {
            (ep.ScriptLines[_esLine + 1], ep.ScriptLines[_esLine]) =
                (ep.ScriptLines[_esLine], ep.ScriptLines[_esLine + 1]);
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLine++;
            _esLineBufFor = -1;
        }

        BandDivider();
        UiToggle("command reference", ref _esReference, AcEdit,
            "What every ] command does and where its numbers sit.");
        BandDivider();
        BandNote($"section {_esSection} - lines {begin + 1}-{end} of {ep.ScriptLines.Count}", UiFaint);
        BandEnd();
    }

    private void DrawScriptLines(EditableEpisode ep, int begin, int end)
    {
        var dl = ImGui.GetWindowDrawList();
        // The '*' marker itself, read-only context above the section's lines.
        if (begin > 0)
            ImGui.TextColored(ColorOf(Shade(AcEdit, 0.8f)), ep.ScriptLines[begin - 1]);

        for (int i = begin; i < end; i++)
        {
            string line = ep.ScriptLines[i];
            bool sel = i == _esLine;
            var p = ImGui.GetCursorScreenPos();
            float w = ImGui.GetContentRegionAvail().X;
            float h = ImGui.GetTextLineHeight() + 6f;
            if (ImGui.InvisibleButton($"##esline{i}", new Vector2(Math.Max(40f, w), h)))
            {
                _esLine = i;
                _esLineBufFor = -1;
            }
            bool hot = ImGui.IsItemHovered();
            if (sel) dl.AddRectFilled(p, p + new Vector2(w, h), Shade(AcEdit, 0.28f, 130), 3f);
            else if (hot) dl.AddRectFilled(p, p + new Vector2(w, h), Gfx.Rgba(255, 255, 255, 12), 3f);
            uint col = line.Length == 0 ? UiFaint
                : line.StartsWith("]L") ? Shade(AcGo, 1.05f)
                : line.StartsWith("]Q") || line.StartsWith("]A") ? Shade(AcRoutes, 1.1f)
                : line.StartsWith("]") ? UiText
                : UiDim;
            ClipText(dl, p + new Vector2(6, 3), w - 10, col, line.Length > 0 ? line : "(empty line)");
        }
    }

    private void DrawScriptLineEditor(EditableEpisode ep)
    {
        if (_esLine < 0 || _esLine >= ep.ScriptLines.Count)
        {
            UiEmpty("no line selected", "click a line above to edit it", AcEdit);
            return;
        }
        string line = ep.ScriptLines[_esLine];

        // Raw text first: the whole language is editable even where no form exists.
        if (_esLineBufFor != _esLine)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                line.Length > 254 ? line[..254] : line, _esLineBuf);
            _esLineBuf[n] = 0;
            _esLineBufFor = _esLine;
        }
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 10f);
        fixed (byte* p = _esLineBuf)
        {
            if (ImGui.InputText("##esraw", p, (nuint)_esLineBuf.Length))
            {
                ep.ScriptLines[_esLine] = BufText(_esLineBuf);
                ep.ScriptDirty = true;
                NoteScriptChangedExternally();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The raw line, exactly as the engine will read it.\n" +
                             "] commands use fixed character positions - see the reference.");

        // The ]L structured form writes the positional fields back into the same line.
        line = ep.ScriptLines[_esLine];
        if (line.Length >= 2 && line[0] == ']' && line[1] == 'L')
            DrawLevelLineForm(ep, line);
        else if (line.Length >= 2 && line[0] == ']')
        {
            var known = ScriptCommandHelp(line[1]);
            if (known.Length > 0) { ImGui.Dummy(new Vector2(0, 4)); ImGui.TextDisabled(known); }
        }
    }

    private void DrawLevelLineForm(EditableEpisode ep, string line)
    {
        ImGui.Dummy(new Vector2(0, 4));
        UiSection("Level entry", AcEdit);
        var e = EpisodeScript.ParseLevelLine(line, 0);
        bool ch = false;

        if (_esNameBufFor != _esLine)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(e.Name.TrimEnd(), _esNameBuf);
            _esNameBuf[n] = 0;
            _esNameBufFor = _esLine;
        }
        ImGui.SetNextItemWidth(120);
        fixed (byte* p = _esNameBuf)
            ch |= ImGui.InputText("name (9 chars)", p, 10);   // 9 + terminator, engine-hard limit

        int file = e.LvlFileNum, song = e.Song, next = e.NextLevel;
        bool normal = e.NormalBonus, bonus = e.BonusLevel;
        ImGui.SetNextItemWidth(110);
        ch |= ImGui.InputInt("level file", ref file);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"1-based section of tyrian{ep.Number}.lvl - the Levels tab's numbering.");
        ch |= SongCombo("song", ref song, 250f, "1..41, music.mus index.");
        ImGui.SetNextItemWidth(110);
        ch |= ImGui.InputInt("next section", ref next);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Script section entered when the level is finished\n(9999 = fall through; 0 = the section after this one).");
        ch |= ImGui.Checkbox("normal bonus", ref normal);
        ImGui.SameLine(0, 12);
        ch |= ImGui.Checkbox("bonus level", ref bonus);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("'$' flags: bonus levels don't kick you back to a save on death.");

        if (ch)
        {
            ep.ScriptLines[_esLine] = BuildLevelLine(
                Math.Clamp(next, 0, 9999), BufText(_esNameBuf),
                Math.Clamp(song, 0, 99), Math.Clamp(file, 1, 99), normal, bonus);
            ep.ScriptDirty = true;
            NoteScriptChangedExternally();
            _esLineBufFor = -1;   // the raw box re-reads the rebuilt line
        }
    }

    /// <summary>Compose a ]L line with every field on its engine-read position.</summary>
    private static string BuildLevelLine(int next, string name, int song, int file,
        bool normalBonus, bool bonus)
    {
        name = (name.Length > 9 ? name[..9] : name).PadRight(9);
        return "]L[ 9999 " + next.ToString("000") + " " + name +
               song.ToString("00") + " " + file.ToString("00") +
               (normalBonus ? "$" : bonus ? " " : "") + (bonus ? "$" : "");
    }

    private static string ScriptCommandHelp(char c) => c switch
    {
        'J' => "]J n - jump to section n (number at column 4).",
        '2' => "]2 n - jump to section n in 2-player / one-player-action games.",
        'w' => "]w n - jump to section n if flying the Stalker 21.126.",
        't' => "]t n - jump to section n if the level timer ran out.",
        'l' => "]l n - jump to section n if a player died.",
        'H' => "]H n - jump to section n if difficulty is BELOW Hard.",
        'h' => "]h - on Hard or above, skip the next line (stock uses it to swap in a harder ]L).",
        's' => "]s - store a savepoint at this section (death returns here).",
        'b' => "]b - write the LAST LEVEL backup save slot.",
        'g' => "]g - Galaga mode for the next level in this section.",
        'e' => "]e - ENGAGE mode (Super Tyrian rules: Stalker + railgun, no cash).",
        'x' => "]x - extra game mode flag.",
        'i' => "]i n - set the song the upcoming shop plays (1-based).",
        'I' => "]I - open the shop: the NEXT 9 lines list item ids per row (10 max each).",
        'G' => "]G - galaxy map: origin, count, then planet/section pairs (5 max).",
        '?' => "]? - outpost datacubes: count, then cubetxt indices (4 slots).",
        '!' => "]! n - set how many of those cubes are free to read.",
        '+' => "]+ n - raise the free-cube count (capped at 4).",
        'Q' => "]Q - END OF EPISODE: shows score + one of the NINE '#'-blocks that must follow, then hands over to the next episode.",
        'A' => "]A - play the ending animation (tyrend.anm).",
        'W' => "]W(y/n) RS - text screen until a '#' line: y = WARNING bars, R = red alert digit, S = type-in speed. Max 12 lines of 60 chars.",
        'P' => "]P n - backdrop: 0 = ship-editor PCX, 1..14 = tyrian.pic picture (fade in), 901+ = clear to a palette.",
        'U' => "]U n - picture n wipes in upward.",
        'V' => "]V n - picture n wipes in downward.",
        'R' => "]R n - picture n wipes in rightward.",
        'C' => "]C - fade out, clear, switch to the dark palette 7.",
        'B' => "]B - fade to black.",
        'F' => "]F - white flash, then black, then clear.",
        'M' => "]M n - play song n (1-based music.mus index).",
        'n' => "]n - re-enable text screens after the player pressed ESC to skip.",
        'S' => "]S - network-game text sync point.",
        '@' => "]@ - toggle the alternate text colour bank for following screens.",
        'T' => "]T - Timed Battle arena list: five 3-wide section numbers.",
        'q' => "]q - Timed Battle over (high-score check, back to title).",
        _ => "",
    };

    private void DrawScriptReference()
    {
        ImGui.TextDisabled("The ] commands, as JE_loadMap reads them (tyrian2.c). Numbers are read\n" +
            "with atoi at fixed character positions - keep columns as the stock lines have them.");
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginChild("esrefscroll");
        foreach (var (cmd, text) in new[]
        {
            ("]L", "load a level: ']L[ 9999 nnn NAMENAMEN ss ff' - nnn next section (col 10),\n" +
                   "9-char name (col 14), ss song (col 23), ff level file (col 26), then '$' flags."),
            ("]J n", "jump to section n. ]2/]w/]t/]l are the conditional forms\n" +
                     "(2-player / Stalker / timer ran out / a player died)."),
            ("]H n  ]h", "difficulty routing: ]H jumps when BELOW Hard; ]h skips the\nnext line on Hard and above."),
            ("]g  ]e  ]x", "modes for the next level: Galaga, ENGAGE (Super Tyrian), extra game."),
            ("]s  ]b", "savepoint / write the LAST LEVEL backup save."),
            ("]G + ]I", "an outpost: galaxy-map destinations (origin, count, planet/section\n" +
                        "pairs; 5 max), then the shop - ]I eats the NEXT 9 lines, one per\n" +
                        "upgrade row, 10 item ids each at most."),
            ("]? ]! ]+", "the outpost's datacube shelf (indices into cubetxt{N}.dat) and\nhow much of it is free."),
            ("]i n", "shop music (1-based)."),
            ("]W(y/n) RS", "a text screen, lines until a '#'. y = flashing WARNING bars and\n" +
                        "siren; R (tens) = red-alert mode; S (ones) = per-character glow\n" +
                        "delay. The engine holds 12 lines of 60 characters."),
            ("]P ]U ]V ]R", "backdrops from tyrian.pic: ]P fades picture n in (0 = ship-editor\n" +
                        "PCX, 901+ = clear to palette n-900); ]U/]V/]R wipe it in\nup / down / rightward."),
            ("]C ]B ]F", "transitions: fade + dark palette / fade to black / white flash."),
            ("]M n", "play song n. ]@ toggles the alternate text colour bank."),
            ("]n", "re-enable text screens after an ESC skip."),
            ("]Q", "END OF EPISODE: score + one random hint of the NINE '#'-terminated\n" +
                   "blocks that must follow, then the game advances episodes itself."),
            ("]A", "play the ending animation (tyrend.anm)."),
            ("]T + ]q", "Timed Battle: five 3-wide arena section numbers on ]T (title\nscreen); ]q ends the battle."),
            ("*", "a line starting with '*' begins the next section."),
        })
        {
            ImGui.TextColored(ColorOf(Shade(AcEdit, 1.05f)), cmd);
            ImGui.SameLine(0, 14);
            ImGui.TextDisabled(text);
            ImGui.Dummy(new Vector2(0, 2));
        }
        ImGui.EndChild();
    }
}
