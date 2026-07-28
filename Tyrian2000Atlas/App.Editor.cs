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
/// This file is the shell: workspace rail, creation/safety workflows, per-mode lists, saving
/// and the playtest hook.
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
    private float _edListW = 268f;
    private readonly byte[] _edLevelFilter = new byte[48];
    private string _edStatus = "";
    private bool _edConfirmRevert;
    private bool _edConfirmBlank;
    private bool _edNewLevelRequest;
    private int _edNewTemplate;
    private int _edNewShape;
    private int _edNewDuration = 3100;
    private int _edNewPace = 1;
    private int _edNewSong = 1;
    private bool _edNewStarfield = true;
    private bool _edNewScriptEntry = true;
    private readonly byte[] _edNewNameBuf = new byte[16];
    private bool _edRenameRequest;
    private int _edRenameFile;
    private readonly byte[] _edRenameBuf = new byte[16];
    private bool _editorFocused;            // the editor window holds the keyboard
    private int _evSelectOnce = -1;         // CLI-aimed event selection, applied after load

    /// <summary>
    /// A deliberately in-memory checkpoint of everything creators can edit. It is cheap
    /// enough to capture before an experiment, broad enough to cover script/enemy work as
    /// well as maps, and never confused with a saved file on disk.
    /// </summary>
    private sealed class EditorSnapshot
    {
        public int EpisodeNumber;
        public string Name = "";
        public DateTime Created;
        public List<EditableLevel> Levels = new();
        public List<string> ScriptLines = new();
        public EnemyDat[] Enemies = Array.Empty<EnemyDat>();
    }

    private readonly List<EditorSnapshot> _edSnapshots = new();
    private bool _edSnapshotPopup;
    private int _edSnapshotSerial = 1;

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

    /// <summary>The "--ednew" visual-test entry point.</summary>
    public void ShowNewLevelStudio()
    {
        _showEditor = true;
        _edMode = 0;
        var ep = EditorEpisode();
        if (ep != null) OpenNewLevelStudio(ep);
    }

    /// <summary>The "--edsnapshots" visual-test entry point.</summary>
    public void ShowSnapshotShelf()
    {
        _showEditor = true;
        var ep = EditorEpisode();
        if (ep == null) return;
        CaptureEditorSnapshot(ep, "Layout experiment");
        _edSnapshotPopup = true;
    }

    /// <summary>The "--edselect" visual-test entry point.</summary>
    public void SelectFirstEditorSpawn()
    {
        _showEditor = true;
        _edMode = 0;
        _edSelectTab = 0;
        var lv = EditorLevel();
        if (lv == null) return;
        int index = lv.Events.FindIndex(e => EventCatalog.IsSpawnType(e.Type));
        if (index >= 0) SelectOnly(index);
        _emTool = _emLastSpawnTool = EmSpawnSelect;
        _emPalette = true;
        _emPaletteMode = 1;
        _emSpawnPanel = 1;
    }

    /// <summary>The "--edrepeat" encounter-repeater smoke-test entry point.</summary>
    public void RepeatFirstEditorSpawn()
    {
        SelectFirstEditorSpawn();
        var ep = EditorEpisode();
        var lv = EditorLevel();
        if (ep == null || lv == null || _emSelSet.Count == 0) return;
        RepeatSelection(ep, lv, _emSelSet.OrderBy(i => i).ToList());
    }

    /// <summary>The "--edrename" visual-test entry point.</summary>
    public void ShowLevelRename()
    {
        _showEditor = true;
        _edMode = 0;
        var ep = EditorEpisode();
        if (ep != null) OpenLevelRename(ep, _edLevelIdx + 1);
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
        bool preserved = _edEp is { Dirty: true };
        if (preserved) CaptureEditorSnapshot(_edEp!, "Before reload");
        _edEp = null;
        EditorEpisode();
        _edStatus = preserved
            ? "Reloaded from disk; the discarded working copy is in Session snapshots."
            : "Reloaded from disk.";
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

        HandleEditorShortcuts(ep);

        float maxList = Math.Max(230f, ImGui.GetContentRegionAvail().X - 540f);
        _edListW = Math.Clamp(_edListW, 230f, Math.Min(340f, maxList));
        float bodyH = ImGui.GetContentRegionAvail().Y;

        DrawEditorRail(ep, new Vector2(_edListW, bodyH));

        ImGui.SameLine(0, 3);
        VSplitter("##edsplit", ref _edListW, 230f, Math.Min(340f, maxList));
        ImGui.SameLine(0, 3);

        ImGui.BeginChild("eddetail", new Vector2(0, bodyH));
        switch (_edMode)
        {
            case 0: DrawEditorLevelDetail(ep); break;
            case 1: DrawScriptDetail(ep); break;
            default: DrawEnemyEditorDetail(ep); break;
        }
        ImGui.EndChild();

        DrawRevertConfirm(ep);
        DrawBlankConfirm(ep);
        DrawNewLevelStudio(ep);
        DrawLevelRenamePopup(ep);
        DrawEditorSnapshotPopup(ep);
        RefEnd(AcEdit);
    }

    /// <summary>Application-level editor shortcuts, available in every workspace.</summary>
    private void HandleEditorShortcuts(EditableEpisode ep)
    {
        if (!_editorFocused) return;
        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S))
        {
            if (io.KeyShift) EditorExportPick();
            else EditorSave();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.F6)) EditorPlaytest();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.N) &&
            _edMode == 0 && ep.Levels.Count < EditableEpisode.MaxLevels)
            OpenNewLevelStudio(ep);
        if (_edMode == 0 && ImGui.IsKeyPressed(ImGuiKey.F2))
            OpenLevelRename(ep, _edLevelIdx + 1);
        if (_edMode == 0 && _edSelectTab == 1 && io.KeyCtrl &&
            ImGui.IsKeyPressed(ImGuiKey.Z))
        {
            if (io.KeyShift) RedoMap(ep);
            else UndoMap(ep);
        }
        if (_edMode == 0 && _edSelectTab == 1 && io.KeyCtrl &&
            ImGui.IsKeyPressed(ImGuiKey.Y))
            RedoMap(ep);
    }

    /// <summary>
    /// The editor is a workspace rather than another reference browser: project identity,
    /// navigation and file operations stay in a persistent rail while the active tool owns
    /// the entire remaining surface.
    /// </summary>
    private void DrawEditorRail(EditableEpisode ep, Vector2 size)
    {
        WellBegin("edrail", size, AcEdit, padX: 9f, padY: 8f,
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);

        string state = ep.Dirty ? "UNSAVED" : "SAVED";
        UiSection("Project", ep.Dirty ? AcRoutes : AcEdit, state);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
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
        string files = $"{ep.LvlFileName}  +  {ep.ScriptFileName}" +
            (ep.SharedEnemyTable ? "  +  tyrian.hdt" : "");
        UiTextClip(files, UiFaint, ImGui.GetContentRegionAvail().X);
        ImGui.Dummy(new Vector2(0, 3f));

        SegBar("##edmode", ref _edMode, AcEdit, ImGui.GetContentRegionAvail().X,
            ("Levels", "The tyrian{N}.lvl side: maps, events and per-level settings."),
            ("Script", "The levels{N}.dat side: level names, order, songs, shops and jumps."),
            ("Enemies", ep.SharedEnemyTable
                ? "The enemyDat table in tyrian.hdt - shared by episodes 1-3."
                : $"The enemyDat table embedded in tyrian{ep.Number}.lvl."));

        string navTitle;
        string navCount;
        if (_edMode == 0)
        {
            navTitle = "Levels";
            navCount = ep.Levels.Count.ToString();
        }
        else if (_edMode == 1)
        {
            navTitle = "Script sections";
            navCount = Math.Max(0, ScriptSections(ep).Count - 1).ToString();
        }
        else
        {
            navTitle = "Enemy table";
            navCount = ep.Enemies.Count(e => e.Loaded).ToString();
        }
        UiSection(navTitle, AcEdit, navCount);

        float actionH = ImGui.GetFrameHeight() * 3f + ImGui.GetTextLineHeight() * 2f + 45f;
        float navH = Math.Max(100f, ImGui.GetContentRegionAvail().Y - actionH);
        // The navigator's lists own their scrolling. Letting this outer layout child scroll
        // produces a second full-height bar and clips the list footer by a few pixels.
        ImGui.BeginChild("ednavigator", new Vector2(0, navH), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        switch (_edMode)
        {
            case 0: DrawEditorLevelList(ep); break;
            case 1: DrawScriptSectionList(ep); break;
            default: DrawEnemyEditorList(ep); break;
        }
        ImGui.EndChild();

        UiSection("Project actions", AcEdit);
        float w2 = (ImGui.GetContentRegionAvail().X - 5f) * 0.5f;
        bool canPlaytest = EditorLevel() != null;
        if (UiButton("Playtest", AcGo, "Run the selected level in the playback simulation,\n" +
                "edits and all - nothing needs to be saved first.\n" +
                "Enemy-table edits ride along too.  (F6)", w2, !canPlaytest))
            EditorPlaytest();
        ImGui.SameLine(0, 5);
        if (UiButton(ep.Dirty ? "Save *" : "Save", AcEdit,
                $"Write {ep.LvlFileName} + {ep.ScriptFileName}" +
                (ep.SharedEnemyTable ? " (+ tyrian.hdt if enemies changed)" : "") +
                $" into the data folder.\nFirst overwrite of a stock file keeps a pristine copy as *{EditableEpisode.BackupSuffix}.\n\nCtrl+S",
                w2, _gd == null))
            EditorSave();

        float w4 = (ImGui.GetContentRegionAvail().X - 15f) * 0.25f;
        if (UiButton("New", AcEdit,
                "Start a from-scratch episode in this slot: one blank level and a\n" +
                "minimal script (play it, then episode complete). The enemy table\n" +
                "carries over - levels need enemies to spawn. Nothing is written\n" +
                "until you save.", w4))
            _edConfirmBlank = true;
        ImGui.SameLine(0, 5);
        if (UiButton("Export", AcEdit,
                "Write the same files into a folder of your choice\n" +
                "(for dropping into another install of the game or the fork).\n\nCtrl+Shift+S", w4))
            EditorExportPick();
        ImGui.SameLine(0, 5);
        if (UiButton("Reload", AcEdit, "Throw the edits away and reload the episode from disk.",
                w4, !ep.Dirty))
            EditorReload();
        ImGui.SameLine(0, 5);
        bool anyBackup = _gd != null && ep.BackupsIn(_dataDir).Count > 0;
        if (UiButton("Revert", AcEdit,
                "Put the pristine *.t2abak files back and reload.", w4, !anyBackup))
            _edConfirmRevert = true;

        int snapshotCount = _edSnapshots.Count(s => s.EpisodeNumber == ep.Number);
        if (UiButton(snapshotCount == 0 ? "Session snapshots"
                : $"Session snapshots ({snapshotCount})", AcRoutes,
                "Capture the whole in-memory episode before an experiment: every level,\n" +
                "script line and enemy entry. Restore without touching files on disk.",
                ImGui.GetContentRegionAvail().X))
            _edSnapshotPopup = true;

        string note = _edStatus.Length > 0 ? _edStatus
            : ep.Dirty ? "unsaved changes" : "everything saved";
        uint noteCol = ep.Dirty && _edStatus.Length == 0 ? Shade(AcRoutes, 1f) : UiFaint;
        UiTextClip(note, noteCol, ImGui.GetContentRegionAvail().X);
        if (ImGui.IsItemHovered() && note.Length > 0) ImGui.SetTooltip(note);
        WellEnd();
    }

    private static EnemyDat[] CloneEnemyTable(EnemyDat[] source)
    {
        var copy = (EnemyDat[])source.Clone();
        for (int i = 0; i < copy.Length; i++)
            if (copy[i].EGraphic != null)
                copy[i].EGraphic = (ushort[])copy[i].EGraphic.Clone();
        return copy;
    }

    private EditorSnapshot CaptureEditorSnapshot(EditableEpisode ep, string? name = null)
    {
        var snap = new EditorSnapshot
        {
            EpisodeNumber = ep.Number,
            Name = name ?? $"Snapshot {_edSnapshotSerial++}",
            Created = DateTime.Now,
            ScriptLines = ep.ScriptLines.ToList(),
            Enemies = CloneEnemyTable(ep.Enemies),
        };
        foreach (var lv in ep.Levels) snap.Levels.Add(lv.Clone());
        _edSnapshots.Add(snap);
        if (_edSnapshots.Count(s => s.EpisodeNumber == ep.Number) > 8)
            _edSnapshots.RemoveAt(_edSnapshots.FindIndex(s => s.EpisodeNumber == ep.Number));
        return snap;
    }

    private void RestoreEditorSnapshot(EditableEpisode ep, EditorSnapshot snap)
    {
        ep.Levels.Clear();
        foreach (var lv in snap.Levels) ep.Levels.Add(lv.Clone());
        ep.ScriptLines = snap.ScriptLines.ToList();
        ep.Enemies = CloneEnemyTable(snap.Enemies);
        ep.LevelsDirty = ep.ScriptDirty = ep.EnemiesDirty = true;
        _edLevelIdx = Math.Clamp(_edLevelIdx, 0, Math.Max(0, ep.Levels.Count - 1));
        ResetEditorCaches();
        _esLineBufFor = _esNameBufFor = -1;
        _eeScrollTo = true;
        _edStatus = $"Restored {snap.Name}; the restored state is unsaved.";
    }

    private void DrawEditorSnapshotPopup(EditableEpisode ep)
    {
        if (_edSnapshotPopup)
        {
            ImGui.OpenPopup("Session snapshots");
            _edSnapshotPopup = false;
        }
        ImGui.SetNextWindowSize(new Vector2(470f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("Session snapshots")) return;

        UiTitle("Session snapshots", AcRoutes,
            "whole-episode safety nets; memory only, cleared when the app closes", maxW: 440f);
        ImGui.TextWrapped("Capture before trying a new layout, rewriting the script, or tuning " +
            "the enemy table. Restoring first captures the current state automatically, so it is reversible.");
        ImGui.Dummy(new Vector2(0, 4f));
        if (UiButton("Capture everything now", AcRoutes,
                "Levels, maps, events, script and enemy table.", ImGui.GetContentRegionAvail().X))
        {
            var snap = CaptureEditorSnapshot(ep);
            _edStatus = $"{snap.Name} captured.";
        }

        if (!_edSnapshots.Any(s => s.EpisodeNumber == ep.Number))
        {
            UiEmpty("no snapshots yet", "capture one before your next big experiment", AcRoutes);
            ImGui.EndPopup();
            return;
        }

        ImGui.Dummy(new Vector2(0, 4f));
        int restoreAt = -1, removeAt = -1;
        for (int i = _edSnapshots.Count - 1; i >= 0; i--)
        {
            var snap = _edSnapshots[i];
            if (snap.EpisodeNumber != ep.Number) continue;
            UiSection(snap.Name, AcRoutes, snap.Created.ToString("HH:mm:ss"));
            ImGui.TextDisabled($"{snap.Levels.Count} levels  ·  {snap.ScriptLines.Count} script lines  ·  " +
                               $"{snap.Enemies.Count(e => e.Loaded)} enemies");
            float restoreW = ImGui.GetContentRegionAvail().X - 39f;
            if (UiButton("Restore", AcRoutes,
                    "The current state is captured first, then this snapshot replaces the working copy.",
                    restoreW))
                restoreAt = i;
            ImGui.SameLine(0, 5f);
            if (UiButton("x", AcEnemy, "remove this in-memory snapshot", 34f))
                removeAt = i;
        }

        if (removeAt >= 0) _edSnapshots.RemoveAt(removeAt);
        if (restoreAt >= 0 && restoreAt < _edSnapshots.Count)
        {
            var wanted = _edSnapshots[restoreAt];
            CaptureEditorSnapshot(ep, "Before restore");
            RestoreEditorSnapshot(ep, wanted);
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
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
            CaptureEditorSnapshot(ep, "Before blank episode");
            ep.StartBlank(EditorLevel()?.ShapeChar ?? 'w');
            _edLevelIdx = 0;
            ResetEditorCaches();
            _edStatus = "Blank episode started; the previous episode is in Session snapshots.";
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
                CaptureEditorSnapshot(ep, "Before reverting to originals");
                var restored = ep.RevertIn(_dataDir);
                _edStatus = restored.Count > 0
                    ? "Restored " + string.Join(", ", restored) +
                      "; the previous working copy is in Session snapshots."
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
        // Two button rows plus the real ImGui gaps surrounding them. The old magic 14px
        // reserve was slightly too small, so the footer overflowed the navigator while its
        // list sat above it with unused space.
        float footer = ImGui.GetFrameHeight() * 2f +
            ImGui.GetStyle().ItemSpacing.Y * 3f + 8f;
        // The level rail remains wheel-scrollable when it overflows, but the permanent
        // scrollbar was needless visual chrome (especially in a maximized workspace).
        ImGui.BeginChild("edlvrows", new Vector2(0, -footer), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar);
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
        if (UiButton("New...", AcEdit, full
                ? $"The engine caps an episode at {EditableEpisode.MaxLevels} levels."
                : "Open the playable-level studio: blank scaffold, terrain copy, or full duplicate.  (Ctrl+N)",
                w, full))
            OpenNewLevelStudio(ep);
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
                "levels are renumbered; lines pointing at this one go dead.\n" +
                "A Session snapshot is captured first.", w,
                EditorLevel() == null || ep.Levels.Count <= 1 || !selectedShown))
        {
            CaptureEditorSnapshot(ep, $"Before deleting level #{_edLevelIdx + 1}");
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

    private void OpenNewLevelStudio(EditableEpisode ep)
    {
        if (ep.Levels.Count >= EditableEpisode.MaxLevels) return;
        _edNewTemplate = 0;
        _edNewDuration = 3100;
        _edNewPace = 1;
        _edNewSong = 1;
        _edNewStarfield = true;
        _edNewScriptEntry = true;
        char currentShape = EditorLevel()?.ShapeChar ?? 'w';
        _edNewShape = Array.IndexOf(GameData.TileSetChars, char.ToLowerInvariant(currentShape));
        if (_edNewShape < 0) _edNewShape = 0;
        Array.Clear(_edNewNameBuf);
        string suggested = $"LEVEL {ep.Levels.Count + 1}";
        int n = System.Text.Encoding.Latin1.GetBytes(
            suggested.AsSpan(0, Math.Min(9, suggested.Length)), _edNewNameBuf);
        _edNewNameBuf[n] = 0;
        _edNewLevelRequest = true;
    }

    private static void CopyTerrainState(EditableLevel source, EditableLevel target)
    {
        target.MapFileChar = source.MapFileChar;
        target.ShapeChar = source.ShapeChar;
        target.MapX = source.MapX;
        target.MapX2 = source.MapX2;
        target.MapX3 = source.MapX3;
        for (int layer = 0; layer < 3; layer++)
            Array.Copy(source.MapSh[layer], target.MapSh[layer], source.MapSh[layer].Length);
        target.Bg1 = (byte[])source.Bg1.Clone();
        target.Bg2 = (byte[])source.Bg2.Clone();
        target.Bg3 = (byte[])source.Bg3.Clone();
    }

    private void ConfigureNewLevelScaffold(EditableLevel lv)
    {
        var scroll = lv.Events.First(e => e.Type == 2);
        (scroll.Dat, scroll.Dat2, scroll.Dat3) = _edNewPace switch
        {
            0 => ((short)1, (short)1, (sbyte)1),
            2 => ((short)2, (short)3, (sbyte)4),
            _ => ((short)1, (short)2, (sbyte)3),
        };
        int scrollAt = lv.Events.FindIndex(e => e.Type == 2);
        lv.Events[scrollAt] = scroll;
        if (!_edNewStarfield) lv.Events.RemoveAll(e => e.Type == 1);

        int end = Math.Clamp(_edNewDuration, 400, 30000);
        int readyAt = Math.Max(30, end - 100);
        int ready = lv.Events.FindIndex(e => e.Type == 36);
        int finish = lv.Events.FindIndex(e => e.Type == 11);
        if (ready >= 0)
        {
            var e = lv.Events[ready];
            e.Time = (ushort)readyAt;
            lv.Events[ready] = e;
        }
        if (finish >= 0)
        {
            var e = lv.Events[finish];
            e.Time = (ushort)end;
            lv.Events[finish] = e;
        }
    }

    private void CreateLevelFromStudio(EditableEpisode ep)
    {
        var source = EditorLevel();
        EditableLevel made;
        if (_edNewTemplate == 2 && source != null)
            made = source.Clone();
        else
        {
            char shape = GameData.TileSetChars[Math.Clamp(_edNewShape, 0, GameData.TileSetChars.Length - 1)];
            made = EditableLevel.CreateNew(shape);
            if (_edNewTemplate == 1 && source != null) CopyTerrainState(source, made);
            ConfigureNewLevelScaffold(made);
        }

        ep.Levels.Add(made);
        ep.LevelsDirty = true;
        _edLevelIdx = ep.Levels.Count - 1;
        string levelName = BufText(_edNewNameBuf).Trim();
        if (levelName.Length == 0) levelName = $"LEVEL {_edLevelIdx + 1}";
        if (_edNewScriptEntry)
        {
            int section = ScriptSections(ep).Count;
            ep.ScriptLines.Add($"*{section} {levelName}");
            ep.ScriptLines.Add(BuildLevelLine(9999, levelName,
                Math.Clamp(_edNewSong, 1, 41), _edLevelIdx + 1, false, false));
            ep.ScriptLines.Add("");
            ep.ScriptDirty = true;
        }
        ResetEditorCaches();
        _edSelectTab = 0;
        _edStatus = _edNewScriptEntry
            ? $"Created #{_edLevelIdx + 1} {levelName} with a script entry; route a jump to its new section."
            : $"Created #{_edLevelIdx + 1} {levelName}.";
    }

    private void DrawNewLevelStudio(EditableEpisode ep)
    {
        if (_edNewLevelRequest)
        {
            ImGui.OpenPopup("New level studio");
            _edNewLevelRequest = false;
        }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(650f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("New level studio", ImGuiWindowFlags.AlwaysAutoResize)) return;

        UiTitle("New level studio", AcEdit,
            "start playable, then make it yours", maxW: 620f);
        ImGui.TextWrapped("Choose how much of the current level should become your starting point. " +
            "Every option stays fully editable and nothing is written until Save.");
        ImGui.Dummy(new Vector2(0, 5f));

        SegBar("##ednewtemplate", ref _edNewTemplate, AcEdit,
            ImGui.GetContentRegionAvail().X,
            ("Playable blank", "Empty terrain plus start flow, starfield, ready-to-end and end-level events."),
            ("Copy terrain", "Keep all three painted backgrounds and registration; reset encounters and flow."),
            ("Full duplicate", "Copy terrain, events, random enemies and settings exactly."));
        ImGui.Dummy(new Vector2(0, 5f));

        string templateNote = _edNewTemplate switch
        {
            1 => "A clean encounter canvas over the current level's complete terrain.",
            2 => "A complete independent copy: ideal for a remix, alternate route, or difficulty variant.",
            _ => "A safe playable skeleton with empty maps and a known ending.",
        };
        ImGui.TextColored(ColorOf(Shade(AcEdit, 1.05f)), templateNote);

        UiSection("Identity", AcEdit);
        ImGui.SetNextItemWidth(190f);
        fixed (byte* p = _edNewNameBuf)
            ImGui.InputText("name (9 chars)", p, 10);
        ImGui.SameLine(0, 14f);
        ImGui.SetNextItemWidth(110f);
        ImGui.InputInt("song", ref _edNewSong);
        _edNewSong = Math.Clamp(_edNewSong, 1, 41);
        ImGui.SameLine(0, 14f);
        ImGui.Checkbox("create script entry", ref _edNewScriptEntry);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds a new section with a ]L line for this file.\n" +
                             "You can route to it later from the Script workspace.");

        if (_edNewTemplate == 0)
        {
            ImGui.SetNextItemWidth(220f);
            string shapeLabel = $"shapes{GameData.TileSetChars[_edNewShape]}.dat";
            if (ImGui.BeginCombo("tile set", shapeLabel))
            {
                for (int i = 0; i < GameData.TileSetChars.Length; i++)
                    if (ImGui.Selectable($"shapes{GameData.TileSetChars[i]}.dat", i == _edNewShape))
                        _edNewShape = i;
                ImGui.EndCombo();
            }
        }
        else
        {
            char inherited = EditorLevel()?.ShapeChar ?? 'w';
            KV("tile set", $"shapes{char.ToLowerInvariant(inherited)}.dat  (from current level)");
        }

        if (_edNewTemplate != 2)
        {
            UiSection("Playable scaffold", AcEdit);
            ImGui.SetNextItemWidth(180f);
            ImGui.SliderInt("length", ref _edNewDuration, 400, 12000, "%d time units");
            SliderReset(ref _edNewDuration, 3100,
                "The ready-to-end event lands 100 units before this.");
            ImGui.SameLine(0, 14f);
            ImGui.TextDisabled($"about {_edNewDuration / 35f:0} seconds at speed 1");

            SegBar("##ednewpace", ref _edNewPace, AcEdit, 310f,
                ("Calm", "BG1/BG2/BG3 start at 1/1/1."),
                ("Classic", "The familiar 1/2/3 parallax start."),
                ("Fast", "A brisk 2/3/4 opening."));
            ImGui.SameLine(0, 12f);
            UiToggle("starfield", ref _edNewStarfield, AcEdit,
                "Include the standard starfield-speed event at time 30.");
        }

        ImGui.Dummy(new Vector2(0, 8f));
        float w = (ImGui.GetContentRegionAvail().X - 8f) * 0.5f;
        if (UiButton("Create level", AcGo,
                "Add this level to the in-memory episode and open it on the map.", w))
        {
            CreateLevelFromStudio(ep);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8f);
        if (UiButton("Cancel", AcEdit, "", w)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void OpenLevelRename(EditableEpisode ep, int fileNum)
    {
        if (fileNum < 1 || fileNum > ep.Levels.Count) return;
        _edRenameFile = fileNum;
        Array.Clear(_edRenameBuf);
        string current = EditorLevelName(fileNum);
        if (current.Length == 0) current = $"LEVEL {fileNum}";
        int n = System.Text.Encoding.Latin1.GetBytes(
            current.AsSpan(0, Math.Min(9, current.Length)), _edRenameBuf);
        _edRenameBuf[n] = 0;
        _edRenameRequest = true;
    }

    private void ApplyLevelRename(EditableEpisode ep)
    {
        string name = BufText(_edRenameBuf).Trim();
        if (name.Length == 0 || _edRenameFile < 1 || _edRenameFile > ep.Levels.Count) return;

        int changed = RenameLevelScriptEntries(ep, _edRenameFile, name);
        _esLineBufFor = _esNameBufFor = -1;
        _edStatus = changed > 0
            ? $"Renamed level #{_edRenameFile} to {name} in {changed} script " +
              (changed == 1 ? "entry." : "entries.")
            : $"Named level #{_edRenameFile} {name} and created its script entry.";
    }

    /// <summary>
    /// Rename every route that loads one level file without altering the route itself. A
    /// freshly-created, unreferenced level receives a minimal playable script section.
    /// Kept separate from the popup so the fixed-position rewrite can be regression-tested.
    /// </summary>
    internal static int RenameLevelScriptEntries(EditableEpisode ep, int fileNum, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("A level name cannot be empty.", nameof(name));
        if (fileNum < 1 || fileNum > ep.Levels.Count)
            throw new ArgumentOutOfRangeException(nameof(fileNum));

        int changed = 0;
        for (int i = 0; i < ep.ScriptLines.Count; i++)
        {
            string line = ep.ScriptLines[i];
            if (line.Length < 27 || line[0] != ']' || line[1] != 'L' ||
                EpisodeScript.AtoiAt(line, 25) != fileNum)
                continue;
            var entry = EpisodeScript.ParseLevelLine(line, 0);
            ep.ScriptLines[i] = BuildLevelLine(entry.NextLevel, name, entry.Song,
                entry.LvlFileNum, entry.NormalBonus, entry.BonusLevel);
            changed++;
        }

        if (changed == 0)
        {
            int section = ScriptSections(ep).Count;
            ep.ScriptLines.Add($"*{section} {name}");
            ep.ScriptLines.Add(BuildLevelLine(9999, name, 1, fileNum, false, false));
            ep.ScriptLines.Add("");
        }
        ep.ScriptDirty = true;
        return changed;
    }

    private void DrawLevelRenamePopup(EditableEpisode ep)
    {
        if (_edRenameRequest)
        {
            ImGui.OpenPopup("Rename level");
            _edRenameRequest = false;
        }
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Rename level", ImGuiWindowFlags.AlwaysAutoResize)) return;

        UiTitle($"Rename level #{_edRenameFile}", AcEdit,
            "the game stores level names in the episode script", maxW: 430f);
        ImGui.TextWrapped("All ]L entries that load this level will receive the new name. " +
            "If none exists yet, the editor creates a script section for it automatically.");
        ImGui.Dummy(new Vector2(0, 6f));
        ImGui.SetNextItemWidth(220f);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        fixed (byte* p = _edRenameBuf)
            ImGui.InputText("name (9 chars)", p, 10);

        string name = BufText(_edRenameBuf).Trim();
        ImGui.Dummy(new Vector2(0, 8f));
        if (UiButton("Rename", AcGo, "", 120f, name.Length == 0))
        {
            ApplyLevelRename(ep);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8f);
        if (UiButton("Cancel", AcEdit, "", 110f)) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
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

        if (_edSelectTab < 0 || _edSelectTab > 2) _edSelectTab = 0;
        BandBegin("edlvworkspace", AcEdit);
        SegBar("##edlvtabs", ref _edSelectTab, AcEdit, 330f,
            ("Map", "Paint terrain, place spawns and shape encounter pacing."),
            ("Events", "Edit the level's complete event stream and raw fields."),
            ("Settings", "Level identity, tile set, map registration and random enemies."));
        BandDivider();
        if (UiButton("Rename", AcEdit,
                "Change this level's 9-character in-game name.  (F2)"))
            OpenLevelRename(ep, _edLevelIdx + 1);
        BandDivider();
        string name = EditorLevelName(_edLevelIdx + 1);
        string here = $"#{_edLevelIdx + 1:00}  {(name.Length > 0 ? name : "(unnamed)")}" +
            $"  /  tiles {lv.ShapeChar}  /  {lv.Events.Count} events";
        BandNote(here, UiDim);
        BandEnd();

        switch (_edSelectTab)
        {
            case 0: DrawMapEditor(ep, lv); break;
            case 1: DrawEventEditor(ep, lv); break;
            default: DrawLevelSettings(ep, lv); break;
        }
    }

    /// <summary>Active level workspace; map/event cross-links switch this directly.</summary>
    private int _edSelectTab;

    private void DrawLevelSettings(EditableEpisode ep, EditableLevel lv)
    {
        var avail = ImGui.GetContentRegionAvail();
        bool columns = avail.X >= 760f;
        if (!columns)
        {
            DrawLevelCoreSettings(ep, lv);
            ImGui.Dummy(new Vector2(0, 5f));
            DrawRandomEnemySettings(ep, lv);
            return;
        }

        float leftW = Math.Clamp(avail.X * 0.43f, 350f, 440f);
        WellBegin("edsettingscore", new Vector2(leftW, avail.Y), AcEdit,
            padX: 11f, padY: 9f);
        DrawLevelCoreSettings(ep, lv);
        WellEnd();
        ImGui.SameLine(0, 8f);
        WellBegin("edsettingsrandom", new Vector2(Math.Max(260f, avail.X - leftW - 8f), avail.Y), AcEdit,
            padX: 11f, padY: 9f);
        DrawRandomEnemySettings(ep, lv);
        WellEnd();
    }

    private void DrawLevelCoreSettings(EditableEpisode ep, EditableLevel lv)
    {
        UiSection("Identity", AcEdit);
        string name = EditorLevelName(_edLevelIdx + 1);
        KV("level file", $"#{_edLevelIdx + 1} of {ep.Levels.Count}");
        KV("script name", name.Length > 0 ? name : "(no ]L line loads this level yet)");
        if (UiButton(name.Length > 0 ? "Rename level..." : "Name this level...", AcEdit,
                name.Length > 0
                    ? "Change every script entry that loads this level.  (F2)"
                    : "Give the level a name and create the missing ]L script entry.  (F2)"))
            OpenLevelRename(ep, _edLevelIdx + 1);
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
    }

    private void DrawRandomEnemySettings(EditableEpisode ep, EditableLevel lv)
    {
        UiSection("Random enemies", AcEdit, $"{lv.LevelEnemy.Count}/{EditableLevel.MaxLevelEnemies}");
        ImGui.TextWrapped("The pool the engine spawns from on its own clock. Events 13/14 gate it; " +
            "event 37 sets the rate. These are ground-band spawns.");
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
