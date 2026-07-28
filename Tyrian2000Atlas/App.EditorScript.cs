using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's script pane: levels{N}.dat as the engine reads it — a flat list of lines cut
/// into sections by '*' markers, walked with ']' commands. Sections on the left, the selected
/// section's lines on the right, every line freely editable; a ']L' line additionally gets a
/// structured form for its fixed-position fields, and a command reference sits one toggle
/// away so the whole language is discoverable.
/// </summary>
public sealed unsafe partial class App
{
    private int _esSection = 1;             // 1-based section shown
    private int _esLine = -1;               // absolute line index selected, -1 = none
    private bool _esReference;              // show the ] command reference
    private readonly byte[] _esLineBuf = new byte[256];
    private int _esLineBufFor = -1;         // which line the buffer holds
    private readonly byte[] _esNameBuf = new byte[16];
    private int _esNameBufFor = -1;

    /// <summary>Line index at which 1-based section N starts (index 0 = before any marker).</summary>
    private static List<int> ScriptSections(EditableEpisode ep)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < ep.ScriptLines.Count; i++)
            if (ep.ScriptLines[i].Length > 0 && ep.ScriptLines[i][0] == '*')
                starts.Add(i + 1);
        return starts;
    }

    private void DrawScriptSectionList(EditableEpisode ep)
    {
        var starts = ScriptSections(ep);
        ImGui.BeginChild("essecrows", new Vector2(0, -(ImGui.GetFrameHeight() + 10f)));
        for (int s = 1; s < starts.Count; s++)
        {
            int begin = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1] - 1 : ep.ScriptLines.Count;
            string title = begin > 0 ? ep.ScriptLines[begin - 1].Trim('*', ' ') : "";
            var loads = new List<string>();
            bool galaga = false, engage = false;
            for (int i = begin; i < end; i++)
            {
                string line = ep.ScriptLines[i];
                if (line.Length < 2 || line[0] != ']') continue;
                if (line[1] == 'L') loads.Add($"#{EpisodeScript.AtoiAt(line, 25)}");
                if (line[1] == 'g') galaga = true;
                if (line[1] == 'e') engage = true;
            }
            string sub = loads.Count > 0 ? "loads " + string.Join(" ", loads)
                : end - begin == 0 ? "(empty)" : $"{end - begin} lines";
            if (galaga) sub += " · galaga";
            if (engage) sub += " · engage";

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
            _esSection = starts.Count;
            _esLine = -1;
        }
    }

    private void DrawScriptDetail(EditableEpisode ep)
    {
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

    private void DrawScriptToolStrip(EditableEpisode ep, int begin, int end)
    {
        BandBegin("esband", AcEdit);
        bool haveSel = _esLine >= begin - 1 && _esLine < end;
        if (UiButton("+ line", AcEdit, "Insert an empty line after the selected one."))
        {
            int at = haveSel ? _esLine + 1 : end;
            ep.ScriptLines.Insert(at, "");
            ep.ScriptDirty = true;
            _esLine = at;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("+ level (]L)", AcEdit, "Insert a level-load line, ready to fill in."))
        {
            int at = haveSel ? _esLine + 1 : end;
            ep.ScriptLines.Insert(at, BuildLevelLine(9999, "NEW LEVEL", 1, 1, false, false));
            ep.ScriptDirty = true;
            _esLine = at;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Delete", AcEnemy, "Remove the selected line.", 0f, !haveSel))
        {
            ep.ScriptLines.RemoveAt(_esLine);
            ep.ScriptDirty = true;
            _esLine = -1;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Up", AcEdit, "", 0f, !haveSel || _esLine <= begin))
        {
            (ep.ScriptLines[_esLine - 1], ep.ScriptLines[_esLine]) =
                (ep.ScriptLines[_esLine], ep.ScriptLines[_esLine - 1]);
            ep.ScriptDirty = true;
            _esLine--;
            _esLineBufFor = -1;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Down", AcEdit, "", 0f, !haveSel || _esLine >= end - 1))
        {
            (ep.ScriptLines[_esLine + 1], ep.ScriptLines[_esLine]) =
                (ep.ScriptLines[_esLine], ep.ScriptLines[_esLine + 1]);
            ep.ScriptDirty = true;
            _esLine++;
            _esLineBufFor = -1;
        }

        BandDivider();
        UiToggle("command reference", ref _esReference, AcEdit,
            "What every ] command does and where its numbers sit.");
        BandDivider();
        BandNote($"section {_esSection} · lines {begin + 1}-{end} of {ep.ScriptLines.Count}", UiFaint);
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
        ImGui.SetNextItemWidth(110);
        ch |= ImGui.InputInt("song", ref song);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("1..41, music.mus index.");
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
        'J' => "]J n - jump to section n.",
        '2' => "]2 n - jump to section n in 2-player / one-player-action games.",
        'w' => "]w n - jump to section n if flying the Stalker 21.126.",
        't' => "]t n - jump to section n if the level timer ran out.",
        'l' => "]l n - jump to section n if a player died.",
        'H' => "]H n - jump to section n if difficulty is Hard or above.",
        'h' => "]h - stop reading this section if difficulty is below Hard.",
        's' => "]s - store a savepoint at this section.",
        'b' => "]b - save the game (LAST LEVEL slot).",
        'g' => "]g - Galaga mode for the next level in this section.",
        'e' => "]e - ENGAGE mode (Super Tyrian rules) for the next level.",
        'x' => "]x - extra game mode.",
        'i' => "]i n - set the song the upcoming shop plays.",
        'I' => "]I - shop: the NEXT 9 lines list item ids per shop row.",
        'G' => "]G - galaxy map: positional pairs of planet + section.",
        '?' => "]? - outpost datacubes: count, then cubetxt indices.",
        '!' => "]! n - set how many of those cubes are free to read.",
        '+' => "]+ n - raise the free-cube count (capped at 4).",
        'Q' => "]Q - secret-hint screen.",
        'A' => "]A - play the ending animation.",
        'W' => "]W - warning text block, until a '#' line.",
        'P' or 'U' or 'V' or 'R' or 'C' or 'B' or 'F' => "cutscene command (pictures / scrolling text).",
        'S' or 'n' or 'M' => "cutscene pacing command.",
        'T' => "]T - Timed Battle arena list.",
        'q' => "]q - Timed Battle over.",
        _ => "",
    };

    private void DrawScriptReference()
    {
        ImGui.TextDisabled("The ] commands, as JE_loadMap reads them (tyrian2.c). Numbers are read\n" +
            "with atoi at fixed character positions - keep columns as the stock lines have them.");
        ImGui.Dummy(new Vector2(0, 4));
        foreach (var (cmd, text) in new[]
        {
            ("]L", "load a level: ']L[ 9999 nnn NAMENAMEN ss ff' - nnn next section (col 10),\n" +
                   "9-char name (col 14), ss song (col 23), ff level file (col 26), then '$' flags."),
            ("]J n", "jump to section n. ]2/]w/]t/]l/]H are the conditional forms."),
            ("]g  ]e  ]x", "modes for the next level: Galaga, ENGAGE (Super Tyrian), extra game."),
            ("]s  ]b", "savepoint / save game."),
            ("]G + ]I", "an outpost: galaxy-map destinations, then the shop (]I eats 9 lines)."),
            ("]? ]! ]+", "the outpost's datacube shelf and how much of it is free."),
            ("]i n", "shop music."),
            ("]Q ]A ]W", "secret hints, the ending anim, warning text."),
            ("*", "a line starting with '*' begins the next section."),
        })
        {
            ImGui.TextColored(ColorOf(Shade(AcEdit, 1.05f)), cmd);
            ImGui.SameLine(0, 14);
            ImGui.TextDisabled(text);
            ImGui.Dummy(new Vector2(0, 2));
        }
    }
}
