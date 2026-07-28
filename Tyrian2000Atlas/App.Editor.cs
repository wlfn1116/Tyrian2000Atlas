using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The episode editor: levels (maps, events, settings), the episode script, and the enemy
/// table, edited in place and saved as byte-compatible tyrian{N}.lvl / levels{N}.dat /
/// tyrian.hdt files — the same files the game and the Engaged fork load, so anything made
/// here is playable there unchanged. The first save of each stock file keeps a pristine
/// *.t2abak beside it.
///
/// This file is the shell: window, band, the per-mode lists, saving and the playtest hook.
/// The map painter, event editor, script editor and enemy editor live in their own files.
/// </summary>
public sealed unsafe partial class App
{
    private static readonly uint AcEdit = Gfx.Rgba(150, 220, 90);

    private bool _showEditor;
    private EditableEpisode? _edEp;
    private int _edEpisodeNum = 1;          // episode slot being edited (1..5)
    private int _edMode;                    // 0 = levels, 1 = script, 2 = enemies
    private int _edLevelIdx;                // selected level (index into _edEp.Levels)
    private float _edListW = 240f;
    private readonly byte[] _edLevelFilter = new byte[48];
    private string _edStatus = "";
    private bool _edConfirmRevert;
    private bool _edConfirmBlank;
    private bool _editorFocused;            // the editor window holds the keyboard
    private int _evSelectOnce = -1;         // CLI-aimed event selection, applied after load

    /// <summary>The "--edtool N" entry point: arm a map tool (5+ = spawn tools, with their palette).</summary>
    public void EditorTool(int tool)
    {
        _emTool = Math.Clamp(tool, 0, EmSpawnSelect);
        if (_emTool >= EmSpawnPlace) { _emPalette = true; _emPaletteMode = 1; }
    }

    /// <summary>The "--edzoom F" entry point: set the map canvas zoom.</summary>
    public void EditorZoom(float zoom) => _emZoom = Math.Clamp(zoom, 0.25f, 4f);

    /// <summary>Playtest and land around an event time — the map canvas's P key. Event time
    /// and playback ticks advance together while the map scrolls at speed 1, so the seek is
    /// close (map stops push the moment later; the timeline is right there to correct).</summary>
    private void EditorPlaytestAt(int time)
    {
        EditorPlaytest();
        if (_playback == null) return;
        _playback.SeekTo(Math.Clamp(time, 1, _playback.Duration));
        _playing = true;
        _status += $"  ·  landed near t{time}";
    }

    /// <summary>The "--maximize id" entry point: open a reference window maximized.</summary>
    public void MaximizeWindow(string id) => _refMax.Add(id);

    /// <summary>The "--edplaytest [level]" entry point: press the editor's Playtest button
    /// without opening the window, so the shot shows the playback itself.</summary>
    public void EditorPlaytestCli(int levelIdx = -1)
    {
        if (levelIdx >= 0) _edLevelIdx = levelIdx;
        EditorPlaytest();
    }

    /// <summary>The "--showeditor [mode] [level] [tab]" entry point.</summary>
    public void ShowEditor(int mode = -1, int levelIdx = -1, int tab = -1)
    {
        _showEditor = true;
        if (mode >= 0) _edMode = Math.Clamp(mode, 0, 2);
        if (levelIdx >= 0) _edLevelIdx = levelIdx;
        if (tab >= 0) _edSelectTab = Math.Clamp(tab, 0, 2);
        if (tab == 1) _evSelectOnce = 0;
    }

    private EpisodeInfo? EditorEpisodeInfo =>
        _gd?.Episodes.FirstOrDefault(e => e.Number == _edEpisodeNum);

    /// <summary>The episode being edited, loading it on first use.</summary>
    private EditableEpisode? EditorEpisode()
    {
        if (_gd == null) return null;
        if (_edEp != null && _edEp.Number == _edEpisodeNum) return _edEp;
        var info = EditorEpisodeInfo ?? _gd.Episodes.FirstOrDefault();
        if (info == null) return null;
        _edEpisodeNum = info.Number;
        try
        {
            _edEp = EditableEpisode.Load(_gd, info);
            // Struct records share their EGraphic arrays with the cached table; the editor
            // writes frames in place, so give every record its own copy.
            for (int i = 0; i < _edEp.Enemies.Length; i++)
                if (_edEp.Enemies[i].EGraphic != null)
                    _edEp.Enemies[i].EGraphic = (ushort[])_edEp.Enemies[i].EGraphic.Clone();
        }
        catch (Exception ex)
        {
            _edStatus = "Load failed: " + ex.Message;
            return null;
        }
        // _edLevelIdx survives the load on purpose: the CLI aims the editor before the
        // first frame ever constructs the episode. EditorLevel() clamps it.
        ResetEditorCaches();
        return _edEp;
    }

    /// <summary>Drop everything derived from the edited episode's current state.</summary>
    private void ResetEditorCaches()
    {
        _emObjects = null;
        _emTimeRuler = null;
        _emHealth = null;
        _emUndo.Clear();
        _emRedo.Clear();
        _emCanvasScrolled = false;
        _emSelSet.Clear();
        _emSelEvent = -1;
        _evSelected = -1;
        _esSection = 1;
        _esLine = -1;
    }

    /// <summary>Throw the edits away and reload the episode from disk.</summary>
    private void EditorReload()
    {
        _edEp = null;
        EditorEpisode();
        _edStatus = "Reloaded from disk.";
    }

    private EditableLevel? EditorLevel()
    {
        var ep = EditorEpisode();
        if (ep == null || ep.Levels.Count == 0) return null;
        _edLevelIdx = Math.Clamp(_edLevelIdx, 0, ep.Levels.Count - 1);
        return ep.Levels[_edLevelIdx];
    }

    /// <summary>The name the episode script gives a level file, or "".</summary>
    private string EditorLevelName(int fileNum)
    {
        var ep = _edEp;
        if (ep == null) return "";
        foreach (var line in ep.ScriptLines)
        {
            if (line.Length < 2 || line[0] != ']' || line[1] != 'L') continue;
            if (EpisodeScript.AtoiAt(line, 25) != fileNum) continue;
            var e = EpisodeScript.ParseLevelLine(line, 0);
            if (e.Name.Trim().Length > 0) return e.Name.Trim();
        }
        return "";
    }

    // =====================================================================
    // Window
    // =====================================================================

    private void DrawEditorWindow()
    {
        _editorFocused = false;
        if (!_showEditor || _gd == null) return;
        if (!RefBegin("Editor", "editor", ref _showEditor, AcEdit,
                new Vector2(1240, 820), new Vector2(860, 520))) return;
        _editorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);

        var ep = EditorEpisode();
        if (ep == null)
        {
            UiEmpty("No episode to edit", "Load a Tyrian 2000 data folder first.", AcEdit);
            RefEnd(AcEdit);
            return;
        }

        DrawEditorBand(ep);

        float maxList = Math.Max(190f, ImGui.GetContentRegionAvail().X - 520f);
        _edListW = Math.Clamp(_edListW, 190f, maxList);

        WellBegin("edlist", new Vector2(_edListW, ImGui.GetContentRegionAvail().Y), AcEdit);
        switch (_edMode)
        {
            case 0: DrawEditorLevelList(ep); break;
            case 1: DrawScriptSectionList(ep); break;
            default: DrawEnemyEditorList(ep); break;
        }
        WellEnd();

        ImGui.SameLine(0, 3);
        VSplitter("##edsplit", ref _edListW, 190f, maxList);
        ImGui.SameLine(0, 3);

        ImGui.BeginChild("eddetail", new Vector2(0, 0));
        switch (_edMode)
        {
            case 0: DrawEditorLevelDetail(ep); break;
            case 1: DrawScriptDetail(ep); break;
            default: DrawEnemyEditorDetail(ep); break;
        }
        ImGui.EndChild();

        RefEnd(AcEdit);
    }

    /// <summary>
    /// The editor's header, one line: the project (episode + disk operations), the
    /// workspace switch, the playtest launch, the status. The band's min-width machinery
    /// keeps a too-narrow window honest by widening it.
    /// </summary>
    private void DrawEditorBand(EditableEpisode ep)
    {
        BandBegin("edband", AcEdit);

        // Project and workspace are one compact cluster. Keep them adjacent (and explicitly
        // in this one-row band) so the episode picker never reads like a separate header.
        BandLabel("episode");
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("##edep", $"Episode {ep.Number}{(ep.Dirty ? " *" : "")}"))
        {
            foreach (var info in _gd!.Episodes)
            {
                bool sel = info.Number == _edEpisodeNum;
                string mark = _edEp != null && _edEp.Number == info.Number && _edEp.Dirty ? " *" : "";
                if (ImGui.Selectable($"Episode {info.Number}{mark}", sel) && !sel)
                {
                    // One episode is held in memory at a time; unsaved edits would be lost.
                    if (_edEp is { Dirty: true })
                        _edStatus = "Unsaved changes — save or reload before switching episodes.";
                    else
                    {
                        _edEpisodeNum = info.Number;
                        _edEp = null;
                    }
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which of the five episode slots is being edited.\n" +
                             "The engine knows exactly five; new content replaces one of them.");
        BandDivider();
        SegBar("##edmode", ref _edMode, AcEdit, 250f,
            ("Levels", "The tyrian{N}.lvl side: maps, events and per-level settings."),
            ("Script", "The levels{N}.dat side: level names, order, songs, shops and jumps."),
            ("Enemies", ep.SharedEnemyTable
                ? "The enemyDat table in tyrian.hdt - shared by episodes 1-3."
                : $"The enemyDat table embedded in tyrian{ep.Number}.lvl."));

        BandDivider();
        if (UiButton("New", AcEdit,
                "Start a from-scratch episode in this slot: one blank level and a\n" +
                "minimal script (play it, then episode complete). The enemy table\n" +
                "carries over - levels need enemies to spawn. Nothing is written\n" +
                "until you save."))
            _edConfirmBlank = true;
        ImGui.SameLine(0, 5);
        bool canPlaytest = EditorLevel() != null;
        if (UiButton("Playtest", AcGo, "Run the selected level in the playback simulation,\n" +
                "edits and all - nothing needs to be saved first.\n" +
                "Enemy-table edits ride along too.", 0f, !canPlaytest))
            EditorPlaytest();

        BandDivider();
        if (UiButton("Save", AcEdit,
                $"Write {ep.LvlFileName} + {ep.ScriptFileName}" +
                (ep.SharedEnemyTable ? " (+ tyrian.hdt if enemies changed)" : "") +
                $" into the data folder.\nFirst overwrite of a stock file keeps a pristine copy as *{EditableEpisode.BackupSuffix}.",
                0f, _gd == null))
            EditorSave();
        ImGui.SameLine(0, 5);
        if (UiButton("Export...", AcEdit,
                "Write the same files into a folder of your choice\n(for dropping into another install of the game or the fork)."))
            EditorExportPick();
        ImGui.SameLine(0, 5);
        bool anyBackup = _gd != null && ep.BackupsIn(_dataDir).Count > 0;
        if (UiButton("Revert", AcEdit,
                "Put the pristine *.t2abak files back and reload.", 0f, !anyBackup))
            _edConfirmRevert = true;
        ImGui.SameLine(0, 5);
        if (UiButton("Reload", AcEdit, "Throw the edits away and reload the episode from disk.",
                0f, !ep.Dirty))
            EditorReload();

        BandDivider();
        string note = _edStatus.Length > 0 ? _edStatus
            : ep.Dirty ? "unsaved changes" : "everything saved";
        BandNote(note, ep.Dirty && _edStatus.Length == 0 ? Shade(AcRoutes, 1f) : UiFaint);
        BandEnd();

        DrawRevertConfirm(ep);
        DrawBlankConfirm(ep);
    }

    private void DrawBlankConfirm(EditableEpisode ep)
    {
        if (_edConfirmBlank) { ImGui.OpenPopup("Start a blank episode?"); _edConfirmBlank = false; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Start a blank episode?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text($"Replace the in-memory episode {ep.Number} with a from-scratch one?");
        ImGui.TextDisabled("One empty level, a minimal script, the enemy table kept.\n" +
                           (ep.Dirty ? "Your unsaved edits here are lost.\n" : "") +
                           "The files on disk stay untouched until you save.");
        ImGui.Dummy(new Vector2(0, 4));
        if (UiButton("Start blank", AcGo, "", 120f))
        {
            ep.StartBlank(EditorLevel()?.ShapeChar ?? 'w');
            _edLevelIdx = 0;
            ResetEditorCaches();
            _edStatus = "Blank episode - paint level #1, then Save or Export.";
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8);
        if (UiButton("Cancel", AcEdit, "", 110f)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRevertConfirm(EditableEpisode ep)
    {
        if (_edConfirmRevert) { ImGui.OpenPopup("Revert to originals?"); _edConfirmRevert = false; }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Revert to originals?", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.Text("Restore these files from their pristine backups?");
        foreach (var f in ep.BackupsIn(_dataDir)) ImGui.BulletText(f);
        ImGui.TextDisabled("The current files and any unsaved edits are lost.");
        ImGui.Dummy(new Vector2(0, 4));
        if (UiButton("Revert", AcEnemy, "", 110f))
        {
            try
            {
                var restored = ep.RevertIn(_dataDir);
                _edStatus = restored.Count > 0
                    ? "Restored " + string.Join(", ", restored) + " - reloading."
                    : "No backups found.";
                _edEp = null;
                LoadData(_dataDir);   // the atlas itself must see the restored files
            }
            catch (Exception ex) { _edStatus = "Revert failed: " + ex.Message; }
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8);
        if (UiButton("Cancel", AcEdit, "", 110f)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    // =====================================================================
    // Saving
    // =====================================================================

    private void EditorSave()
    {
        var ep = _edEp;
        if (ep == null || _gd == null) return;
        var problems = ep.Validate();
        if (problems.Count > 0)
        {
            _edStatus = "Not saved: " + problems[0] +
                (problems.Count > 1 ? $" (+{problems.Count - 1} more)" : "");
            return;
        }
        try
        {
            var written = ep.SaveTo(_dataDir, backup: true);
            _edStatus = "Saved " + string.Join(", ", written) + " - reloading.";
            // Reload the data set so the level list, browsers and playback all read the
            // saved state; then come back to the same editor selection.
            int keepLevel = _edLevelIdx;
            LoadData(_dataDir);
            _edEp = null;
            EditorEpisode();
            _edLevelIdx = keepLevel;
        }
        catch (Exception ex)
        {
            _edStatus = "Save failed: " + ex.Message;
        }
    }

    private void EditorExportPick()
    {
        var ep = _edEp;
        if (ep == null) return;
        var problems = ep.Validate();
        if (problems.Count > 0)
        {
            _edStatus = "Not exported: " + problems[0] +
                (problems.Count > 1 ? $" (+{problems.Count - 1} more)" : "");
            return;
        }
        StartExportFolderPick();
    }

    /// <summary>Finish an export once the folder dialog has answered.</summary>
    private void EditorExportTo(string dir)
    {
        var ep = _edEp;
        if (ep == null) return;
        try
        {
            var written = ep.SaveTo(dir, backup: false);
            // Exporting must not mark the in-memory episode clean - the data folder still
            // holds the unsaved state. SaveTo cleared the flags; the next edit re-marks
            // them, but the safe statement is that the working copy may still differ.
            _edStatus = $"Exported {string.Join(", ", written)} to {dir}";
        }
        catch (Exception ex)
        {
            _edStatus = "Export failed: " + ex.Message;
        }
    }

    // =====================================================================
    // Playtest
    // =====================================================================

    /// <summary>
    /// Hand the edited level to the main viewport and run it: the same path the level list
    /// takes, but built from the in-memory editable state (map, events and enemy table
    /// included) rather than from the files.
    /// </summary>
    private void EditorPlaytest()
    {
        var ep = EditorEpisode();   // loads on demand: the CLI playtests without the window
        var lv = EditorLevel();
        var info = EditorEpisodeInfo;
        if (ep == null || lv == null || info == null || _gd == null) return;
        try
        {
            _episodeIdx = _gd.Episodes.IndexOf(info);
            _levelFileNum = _edLevelIdx + 1;
            RebuildBrowseList();
            _level = lv.ToLevel(_edLevelIdx + 1);
            _shapes = _gd.GetShapeTable(_level.ShapeChar);
            _enemyData = ep.ToEnemyData();
            _flowSegs = ScrollWalk.Build(_level.Events);
            _timeline = LevelTimeline.Build(_level);
            _layerScroll = new ObjectPlacer.LayerScroll();
            _objects = ObjectPlacer.Place(_gd, info, _level, _enemyData, null, _layerScroll);
            if (_gameLayerOrder)
                _layers = LayerStack.GameOrder(_layers, _level.ComputeStartFlags());
            _layerOrderFlags = null;
            _layerLiveSeenValid = false;
            _playback = null;
            _playbackMode = true;
            BuildPlayback();
            _playing = _playback != null;
            _composeDirty = true;
            _viewInitialized = false;
            string name = EditorLevelName(_edLevelIdx + 1);
            _status = $"PLAYTEST ep{ep.Number} #{_edLevelIdx + 1} {name} - unsaved editor state; " +
                      "pick any level in the list to leave it";
        }
        catch (Exception ex)
        {
            _edStatus = "Playtest failed: " + ex.Message;
        }
    }

    // =====================================================================
    // Level list (mode 0)
    // =====================================================================

    private void DrawEditorLevelList(EditableEpisode ep)
    {
        UiFilter("##edlvfilter", "find a level", _edLevelFilter,
            ImGui.GetContentRegionAvail().X, AcEdit);
        string filter = BufText(_edLevelFilter).Trim();
        float footer = ImGui.GetFrameHeight() * 2 + 14f;
        ImGui.BeginChild("edlvrows", new Vector2(0, -footer));
        int shown = 0;
        bool selectedShown = false;
        for (int i = 0; i < ep.Levels.Count; i++)
        {
            var lv = ep.Levels[i];
            string name = EditorLevelName(i + 1);
            if (filter.Length > 0 && !Matches(filter, name, (i + 1).ToString(),
                    lv.ShapeChar.ToString(), lv.Events.Count.ToString())) continue;
            shown++;
            if (i == _edLevelIdx) selectedShown = true;
            var row = UiRow($"##edlv{i}", i == _edLevelIdx, AcEdit, 40f);
            RowText(row, 12f, $"#{i + 1:00}  {(name.Length > 0 ? name : "(no script entry)")}",
                $"tiles {lv.ShapeChar}   events {lv.Events.Count}", AcEdit, row.Selected,
                reserve: 8f);
            if (row.Clicked && _edLevelIdx != i)
            {
                _edLevelIdx = i;
                _emObjects = null;
                _emTimeRuler = null;
                _emUndo.Clear();
                _emRedo.Clear();
                _evSelected = -1;
            }
        }
        if (shown == 0)
            UiEmpty("no levels match", "clear the filter to see the whole episode", AcEdit);
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0, 3));
        float w = (ImGui.GetContentRegionAvail().X - 10f) / 3f;
        bool full = ep.Levels.Count >= EditableEpisode.MaxLevels;
        if (UiButton("Add", AcEdit, full
                ? $"The engine caps an episode at {EditableEpisode.MaxLevels} levels."
                : "A fresh empty level at the end of the file.", w, full))
        {
            ep.Levels.Add(EditableLevel.CreateNew(EditorLevel()?.ShapeChar ?? 'w'));
            ep.LevelsDirty = true;
            _edLevelIdx = ep.Levels.Count - 1;
            ResetEditorCaches();
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Duplicate", AcEdit, "A copy of the selected level, at the end of the file.",
                w, full || EditorLevel() == null || !selectedShown))
        {
            var src = EditorLevel()!;
            ep.Levels.Add(src.Clone());
            ep.LevelsDirty = true;
            _edLevelIdx = ep.Levels.Count - 1;
            ResetEditorCaches();
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Delete", AcEnemy, "Remove the selected level. ]L lines pointing at later\n" +
                "levels are renumbered; lines pointing at this one go dead.", w,
                EditorLevel() == null || ep.Levels.Count <= 1 || !selectedShown))
        {
            ep.Levels.RemoveAt(_edLevelIdx);
            RenumberScriptLevels(ep, removedAt: _edLevelIdx + 1);
            ep.LevelsDirty = true;
            _edLevelIdx = Math.Clamp(_edLevelIdx, 0, ep.Levels.Count - 1);
            ResetEditorCaches();
        }

        float w2 = (ImGui.GetContentRegionAvail().X - 5f) / 2f;
        if (UiButton("Move up", AcEdit, "Swap with the level above; ]L lines follow.", w2,
                _edLevelIdx <= 0 || !selectedShown))
        {
            (ep.Levels[_edLevelIdx - 1], ep.Levels[_edLevelIdx]) =
                (ep.Levels[_edLevelIdx], ep.Levels[_edLevelIdx - 1]);
            SwapScriptLevels(ep, _edLevelIdx, _edLevelIdx + 1);
            ep.LevelsDirty = true;
            _edLevelIdx--;
            ResetEditorCaches();
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Move down", AcEdit, "Swap with the level below; ]L lines follow.", w2,
                EditorLevel() == null || _edLevelIdx >= ep.Levels.Count - 1 || !selectedShown))
        {
            (ep.Levels[_edLevelIdx + 1], ep.Levels[_edLevelIdx]) =
                (ep.Levels[_edLevelIdx], ep.Levels[_edLevelIdx + 1]);
            SwapScriptLevels(ep, _edLevelIdx + 2, _edLevelIdx + 1);
            ep.LevelsDirty = true;
            _edLevelIdx++;
            ResetEditorCaches();
        }
    }

    /// <summary>Rewrite the ]L file-number field (offset 25) across the script.</summary>
    private static void PatchScriptLevelNumbers(EditableEpisode ep, Func<int, int> map)
    {
        for (int i = 0; i < ep.ScriptLines.Count; i++)
        {
            string s = ep.ScriptLines[i];
            if (s.Length < 27 || s[0] != ']' || s[1] != 'L') continue;
            int file = EpisodeScript.AtoiAt(s, 25);
            int now = map(file);
            if (now == file) continue;
            string num = now.ToString("00");
            // Keep the fixed-position layout: two digits at 25..26.
            var chars = s.ToCharArray();
            if (chars.Length < 27) continue;
            chars[25] = num[^2]; chars[26] = num[^1];
            ep.ScriptLines[i] = new string(chars);
            ep.ScriptDirty = true;
        }
    }

    private static void RenumberScriptLevels(EditableEpisode ep, int removedAt) =>
        PatchScriptLevelNumbers(ep, f => f > removedAt ? f - 1 : f);

    private static void SwapScriptLevels(EditableEpisode ep, int a, int b) =>
        PatchScriptLevelNumbers(ep, f => f == a ? b : f == b ? a : f);

    // =====================================================================
    // Level detail (mode 0)
    // =====================================================================

    private void DrawEditorLevelDetail(EditableEpisode ep)
    {
        var lv = EditorLevel();
        if (lv == null)
        {
            UiEmpty("No level selected", "Add a level with the button under the list.", AcEdit);
            return;
        }

        if (ImGui.BeginTabBar("##edlvtabs"))
        {
            // TabItem (not BeginTabItem) so "open this event" / "show on map" jumps can
            // switch the tab programmatically via _edSelectTab.
            if (TabItem("Map", _edSelectTab == 0))
            {
                DrawMapEditor(ep, lv);
                ImGui.EndTabItem();
            }
            if (TabItem("Events", _edSelectTab == 1))
            {
                DrawEventEditor(ep, lv);
                ImGui.EndTabItem();
            }
            if (TabItem("Level settings", _edSelectTab == 2))
            {
                DrawLevelSettings(ep, lv);
                ImGui.EndTabItem();
            }
            _edSelectTab = -1;
            ImGui.EndTabBar();
        }
    }

    /// <summary>One-shot programmatic tab switch for the level detail pane.</summary>
    private int _edSelectTab = -1;

    private void DrawLevelSettings(EditableEpisode ep, EditableLevel lv)
    {
        ImGui.Dummy(new Vector2(0, 2));
        UiSection("Identity", AcEdit);
        string name = EditorLevelName(_edLevelIdx + 1);
        KV("level file", $"#{_edLevelIdx + 1} of {ep.Levels.Count}");
        KV("script name", name.Length > 0 ? name : "(no ]L line loads this level yet)");
        if (name.Length == 0 &&
            UiButton("Create script entry", AcEdit,
                "Adds a new script section with a ]L line loading this level.\n" +
                "Route to it in the Script tab: a jump (]J), a galaxy-map entry (]G),\n" +
                "or another level's 'next section'."))
        {
            int fileNum = _edLevelIdx + 1;
            var starts = ScriptSections(ep);
            ep.ScriptLines.Add($"*{starts.Count} LEVEL {fileNum}");
            ep.ScriptLines.Add(BuildLevelLine(9999, $"LEVEL {fileNum}", 1, fileNum, false, false));
            ep.ScriptLines.Add("");
            ep.ScriptDirty = true;
            _edStatus = $"Script section {starts.Count} now loads level #{fileNum} - wire a jump to it.";
        }
        ImGui.Dummy(new Vector2(0, 6));

        UiSection("Tile set", AcEdit);
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##edshapes", $"shapes{char.ToLowerInvariant(lv.ShapeChar)}.dat"))
        {
            foreach (char c in GameData.TileSetChars)
            {
                bool sel = char.ToLowerInvariant(lv.ShapeChar) == c;
                if (ImGui.Selectable($"shapes{c}.dat", sel) && !sel)
                {
                    lv.ShapeChar = c;
                    ep.LevelsDirty = true;
                    _emObjects = null;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.TextDisabled("The 600-tile terrain set all three map layers draw from.");
        ImGui.Dummy(new Vector2(0, 6));

        UiSection("Map registration", AcEdit);
        int mapX = lv.MapX, mapX2 = lv.MapX2, mapX3 = lv.MapX3;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("mapX (bg1/bg2)", ref mapX)) { lv.MapX = (ushort)Math.Clamp(mapX, 1, 14); ep.LevelsDirty = true; _emObjects = null; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "Leftmost visible bg1 column at level start (1-based).\n" +
            "Also anchors event X coordinates: screen x = event x - (mapX-1)*24.");
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("mapX2", ref mapX2)) { lv.MapX2 = (ushort)Math.Clamp(mapX2, 1, 14); ep.LevelsDirty = true; }
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("mapX3 (bg3)", ref mapX3)) { lv.MapX3 = (ushort)Math.Clamp(mapX3, 1, 15); ep.LevelsDirty = true; _emObjects = null; }
        int mfc = lv.MapFileChar;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("map file byte", ref mfc)) { lv.MapFileChar = (byte)Math.Clamp(mfc, 0, 255); ep.LevelsDirty = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Read by the engine but never used; kept for byte-compatibility.");
        ImGui.Dummy(new Vector2(0, 6));

        UiSection("Random enemies", AcEdit, $"{lv.LevelEnemy.Count}/{EditableLevel.MaxLevelEnemies}");
        ImGui.TextDisabled("The pool the engine spawns from on its own clock (events 13/14 gate it,\nevent 37 sets the rate). Ground-band spawns.");
        ImGui.Dummy(new Vector2(0, 2));
        int removeAt = -1;
        for (int i = 0; i < lv.LevelEnemy.Count; i++)
        {
            ImGui.PushID(i);
            var box = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            EditorEnemyThumb(dl, ep.Enemies, lv.LevelEnemy[i], box, box + new Vector2(34, 30));
            ImGui.Dummy(new Vector2(36, 30));
            ImGui.SameLine();
            int v = lv.LevelEnemy[i];
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("##rid", ref v))
            {
                lv.LevelEnemy[i] = (ushort)Math.Clamp(v, 0, 1850);
                ep.LevelsDirty = true;
            }
            ImGui.SameLine();
            if (UiButton("x", AcEnemy, "remove", 26f)) removeAt = i;
            ImGui.PopID();
        }
        if (removeAt >= 0) { lv.LevelEnemy.RemoveAt(removeAt); ep.LevelsDirty = true; }
        if (UiButton("Add random enemy", AcEdit, "",
                0f, lv.LevelEnemy.Count >= EditableLevel.MaxLevelEnemies))
        {
            lv.LevelEnemy.Add(25);
            ep.LevelsDirty = true;
        }
    }
}
