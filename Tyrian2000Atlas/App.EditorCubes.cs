using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's datacube pane: cubetxt{N}.dat as an editable shelf of readings. Every cube
/// is a portrait, a title, a category header and the body text (with the game's '~'
/// emphasis), all written back byte-exact. The pane knows which outposts of the current
/// script actually offer each cube, and deleting one renumbers the script's ]? lists the
/// same way deleting a level renumbers ]L lines.
/// </summary>
public sealed unsafe partial class App
{
    private int _ecSelected;                 // 0-based index into ep.Cubes
    private readonly byte[] _ecFilter = new byte[48];
    private readonly byte[] _ecTitleBuf = new byte[256];
    private readonly byte[] _ecHeaderBuf = new byte[256];
    private readonly byte[] _ecBodyBuf = new byte[8192];
    private int _ecBufFor = -1;
    private SpriteImage? _ecFace;
    private (int Face, nint Renderer) _ecFaceKey = (-1, 0);
    private bool _ecScrollTo;

    /// <summary>The "--edcubes [idx]" entry point.</summary>
    public void ShowCubeEditor(int index = -1)
    {
        _showEditor = true;
        _edMode = 3;
        if (index >= 0) { _ecSelected = index; _ecScrollTo = true; }
    }

    /// <summary>1-based cube indices any ]? line of the current script offers.</summary>
    private static HashSet<int> ReferencedCubes(EditableEpisode ep)
    {
        var refs = new HashSet<int>();
        foreach (var s in ep.ScriptLines)
        {
            if (s.Length < 2 || s[0] != ']' || s[1] != '?') continue;
            int n = Math.Clamp(EpisodeScript.AtoiAt(s, 4), 0, 8);
            for (int c = 0; c < n; c++) refs.Add(EpisodeScript.AtoiAt(s, 3 + (c + 1) * 4));
        }
        return refs;
    }

    // =====================================================================
    // List (rail)
    // =====================================================================

    private void DrawCubeEditorList(EditableEpisode ep)
    {
        UiFilter("##ecfilter", "find a reading", _ecFilter,
            ImGui.GetContentRegionAvail().X, AcEdit);
        string filter = BufText(_ecFilter).Trim();
        var offered = ReferencedCubes(ep);

        float footer = ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.Y * 3f + 8f;
        ImGui.BeginChild("ecrows", new Vector2(0, -footer), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollbar);
        int shown = 0;
        for (int i = 0; i < ep.Cubes.Count; i++)
        {
            var cube = ep.Cubes[i];
            string title = cube.Title.Replace("~", "").Trim();
            if (filter.Length > 0 && !Matches(filter, (i + 1).ToString(), title, cube.Header))
                continue;
            shown++;
            if (_ecScrollTo && i == _ecSelected)
            {
                ImGui.SetScrollHereY(0.4f);
                _ecScrollTo = false;
            }
            var row = UiRow($"##ec{i}", i == _ecSelected, AcEdit, 40f);
            string sub = cube.IsEmpty ? "(empty slot)"
                : $"{cube.Body.Count} lines" + (offered.Contains(i + 1) ? " - on a shelf" : " - never offered");
            RowText(row, 12f, $"{i + 1:000}  {(title.Length > 0 ? title : "(untitled)")}",
                sub, AcEdit, row.Selected, reserve: 8f);
            if (row.Clicked && _ecSelected != i)
            {
                _ecSelected = i;
                _ecBufFor = -1;
            }
        }
        if (shown == 0)
            UiEmpty(ep.Cubes.Count == 0 ? "no cube file" : "no readings match",
                ep.Cubes.Count == 0 ? $"cubetxt{ep.Number}.dat was not found" : "clear the filter",
                AcEdit);
        ImGui.EndChild();

        ImGui.Dummy(new Vector2(0, 3));
        float w = (ImGui.GetContentRegionAvail().X - 10f) / 3f;
        if (UiButton("New", AcEdit, "A blank reading at the end of the file.", w))
        {
            ep.Cubes.Add(new EditableCube
            {
                Marker = $"*{ep.Cubes.Count + 1:00} 01",
                Title = "NEW READING",
                Header = "Data",
                HasTitle = true,
                HasHeader = true,
            });
            ep.CubesDirty = true;
            _ecSelected = ep.Cubes.Count - 1;
            _ecBufFor = -1;
            _ecScrollTo = true;
        }
        ImGui.SameLine(0, 5);
        bool haveSel = _ecSelected >= 0 && _ecSelected < ep.Cubes.Count;
        if (UiButton("Duplicate", AcEdit, "Copy this reading to the end of the file.", w, !haveSel))
        {
            ep.Cubes.Add(ep.Cubes[_ecSelected].Clone());
            ep.CubesDirty = true;
            _ecSelected = ep.Cubes.Count - 1;
            _ecBufFor = -1;
            _ecScrollTo = true;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Delete", AcEnemy,
                "Remove this reading. ]? lists in the script are renumbered;\n" +
                "entries offering this cube are dropped from their shelves.", w, !haveSel))
        {
            CaptureEditorSnapshot(ep, $"Before deleting cube {_ecSelected + 1}");
            ep.Cubes.RemoveAt(_ecSelected);
            RenumberScriptCubes(ep, removedAt: _ecSelected + 1);
            ep.CubesDirty = true;
            _ecSelected = Math.Clamp(_ecSelected, 0, Math.Max(0, ep.Cubes.Count - 1));
            _ecBufFor = -1;
        }
        ImGui.TextColored(ColorOf(UiFaint), $"cubetxt{ep.Number}.dat - the outposts' readings");
    }

    /// <summary>Rewrite every ]? list after a cube was removed: references above it shift
    /// down, references to it disappear. Lines are regenerated in the canonical layout the
    /// engine reads (count at col 4, 4-wide entries from col 7).</summary>
    internal static void RenumberScriptCubes(EditableEpisode ep, int removedAt)
    {
        for (int i = 0; i < ep.ScriptLines.Count; i++)
        {
            string s = ep.ScriptLines[i];
            if (s.Length < 2 || s[0] != ']' || s[1] != '?') continue;
            int n = Math.Clamp(EpisodeScript.AtoiAt(s, 4), 0, 8);
            var cubes = new List<int>();
            for (int c = 0; c < n; c++)
            {
                int cube = EpisodeScript.AtoiAt(s, 3 + (c + 1) * 4);
                if (cube == removedAt) continue;
                cubes.Add(cube > removedAt ? cube - 1 : cube);
            }
            string line = $"]?[ {Math.Min(cubes.Count, 4):00}";
            foreach (int c in cubes.Take(4)) line += $" {Math.Clamp(c, 0, 999):000}";
            ep.ScriptLines[i] = line;
            ep.ScriptDirty = true;
        }
    }

    // =====================================================================
    // Detail
    // =====================================================================

    private void DrawCubeEditorDetail(EditableEpisode ep)
    {
        if (_ecSelected < 0 || _ecSelected >= ep.Cubes.Count)
        {
            UiEmpty("no reading selected",
                ep.Cubes.Count == 0
                    ? "this episode has no cubetxt file to edit"
                    : "pick a reading on the left, or add one", AcEdit);
            return;
        }
        var cube = ep.Cubes[_ecSelected];
        bool ch = false;

        BandBegin("ecband", AcEdit);
        BandNote($"cube {_ecSelected + 1:000} of {ep.Cubes.Count}", UiDim);
        BandDivider();
        var offered = ReferencedCubes(ep);
        BandNote(offered.Contains(_ecSelected + 1)
            ? "offered by an outpost's ]? shelf"
            : "not on any outpost shelf yet - add it in the Script workspace's outpost tab",
            offered.Contains(_ecSelected + 1) ? UiDim : Shade(AcSim, 1.05f));
        BandEnd();

        if (_ecBufFor != _ecSelected)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                cube.Title.Length > 254 ? cube.Title[..254] : cube.Title, _ecTitleBuf);
            _ecTitleBuf[n] = 0;
            n = System.Text.Encoding.Latin1.GetBytes(
                cube.Header.Length > 254 ? cube.Header[..254] : cube.Header, _ecHeaderBuf);
            _ecHeaderBuf[n] = 0;
            n = System.Text.Encoding.Latin1.GetBytes(
                string.Join('\n', cube.Body), new Span<byte>(_ecBodyBuf, 0, _ecBodyBuf.Length - 1));
            _ecBodyBuf[n] = 0;
            _ecBufFor = _ecSelected;
        }

        var avail = ImGui.GetContentRegionAvail();
        float formW = Math.Max(340f, avail.X * 0.46f);
        ImGui.BeginChild("ecform", new Vector2(formW, avail.Y));

        UiSection("Identity", AcEdit);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 10f);
        fixed (byte* p = _ecTitleBuf)
            if (ImGui.InputText("##ectitle", p, (nuint)_ecTitleBuf.Length))
            {
                cube.Title = BufText(_ecTitleBuf);
                cube.HasTitle = true;
                ch = true;
            }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The heading over the reading. ~word~ highlights, exactly\nas the game's own titles do.");
        ImGui.SetNextItemWidth(190f);
        fixed (byte* p = _ecHeaderBuf)
            if (ImGui.InputText("category", p, (nuint)_ecHeaderBuf.Length))
            {
                cube.Header = BufText(_ecHeaderBuf);
                cube.HasHeader = true;
                ch = true;
            }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shown under the portrait: Data, Tech, Historical...");

        ImGui.SameLine(0, 14f);
        ImGui.SetNextItemWidth(90f);
        int face = cube.Face;
        int faceCount = _gd?.Main.Faces?.Count ?? 0;
        if (ImGui.InputInt("portrait", ref face))
        {
            cube.Face = Math.Clamp(face, 0, Math.Max(0, faceCount));
            ch = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"1-based face sprite (1..{Math.Max(1, faceCount)}); 0 = none.\nThe portrait preview sits by the reading.");

        UiSection("Body", AcEdit, $"{cube.Body.Count} lines");
        float bodyH = Math.Max(140f, ImGui.GetContentRegionAvail().Y - 8f);
        fixed (byte* p = _ecBodyBuf)
            if (ImGui.InputTextMultiline("##ecbody", p, (nuint)_ecBodyBuf.Length,
                    new Vector2(ImGui.GetContentRegionAvail().X - 4f, bodyH)))
            {
                cube.Body = BufText(_ecBodyBuf).Split('\n').ToList();
                while (cube.Body.Count > 0 && cube.Body[^1].Length == 0)
                    cube.Body.RemoveAt(cube.Body.Count - 1);
                ch = true;
            }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The reading itself. The game wraps lines to its reader on its\n" +
                "own; blank lines separate paragraphs; ~word~ highlights.\n" +
                "Keep lines under 255 characters (the file format's limit).");
        ImGui.EndChild();

        ImGui.SameLine(0, 10f);

        // ---- the reading, set like the datacube reader sets it ----
        ImGui.BeginChild("ecpreview", new Vector2(0, avail.Y));
        DrawCubePreview(cube);
        ImGui.EndChild();

        if (ch) ep.CubesDirty = true;
    }

    private void DrawCubePreview(EditableCube cube)
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;

        // Portrait bezel, exactly the reader's framing.
        int faceSprite = cube.Face - 1;
        var face = faceSprite >= 0 ? _gd?.Main.Faces?.Get(faceSprite) : null;
        if (face != null && _gd != null)
        {
            const float scale = 2f;
            nint rh = (nint)_activeRenderer.Handle;
            if (_ecFaceKey != (faceSprite, rh) || _ecFace == null)
            {
                _ecFace ??= new SpriteImage();
                _ecFace.Update(_activeRenderer, face,
                    _gd.Palettes.Get(DataCubes.PaletteFor(faceSprite)));
                _ecFaceKey = (faceSprite, rh);
            }
            var bez = start;
            var bezMax = start + new Vector2(face.W * scale + 10f, face.H * scale + 10f);
            FlatRect(dl, bez, bezMax, Mix(UiPanel, AcEdit, 0.14f), Mix(UiPanelHi, AcEdit, 0.32f), 6f);
            var inner = bez + new Vector2(5f, 5f);
            dl.AddRectFilled(inner, inner + new Vector2(face.W * scale, face.H * scale), Gfx.Rgba(8, 8, 13), 2f);
            _ecFace.Draw(dl, inner, scale);
            ImGui.Dummy(new Vector2(face.W * scale + 10f, face.H * scale + 10f));
            ImGui.SameLine(0, 12f);
        }

        ImGui.BeginGroup();
        DrawCubeSpans(SpansOf(cube.Title));
        var rule = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(rule, new Vector2(rule.X + 96f, rule.Y + 1.5f), Shade(AcEdit, 1f, 150));
        ImGui.Dummy(new Vector2(96f, 5f));
        ImGui.TextColored(ColorOf(UiDim), cube.Header);
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, 6f));

        // The reading column: dark well, wrapped spans with the '~' emphasis coloured.
        var avail = ImGui.GetContentRegionAvail();
        var bp = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(bp, bp + new Vector2(w, avail.Y), Gfx.Rgba(15, 14, 22), 6f);
        dl.AddRect(bp, bp + new Vector2(w, avail.Y), Shade(AcEdit, 0.35f, 90), 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 3f));
        ImGui.BeginChild("ecreading", new Vector2(w, avail.Y), ImGuiChildFlags.AlwaysUseWindowPadding);
        foreach (var line in cube.Body)
        {
            if (line.Trim().Length == 0)
            {
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * 0.55f));
                continue;
            }
            DrawCubeSpans(SpansOf(line));
        }
        if (cube.Body.Count == 0)
            ImGui.TextColored(ColorOf(UiFaint), "(the reading is empty)");
        ImGui.Dummy(new Vector2(0, 10f));
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }
}
