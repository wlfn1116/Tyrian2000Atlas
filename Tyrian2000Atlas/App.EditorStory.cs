using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The editor's text-screen workbench, shared by every place the flow shows full-screen
/// text: a stop's arrival story, its pre-level warning and the episode's endscreens. A
/// list of screens on the left, the selected screen's form in the middle, and a live
/// preview on the right rendered exactly the way JE_displayText composes the real thing —
/// backdrop, tiny font at x=10, WARNING bars and all.
/// </summary>
public sealed unsafe partial class App
{
    private int _essSel;                     // selected screen within the current context
    private string _essCtx = "";             // which screen list the state belongs to
    private readonly byte[] _essTextBuf = new byte[1024];
    private string _essTextFor = "";
    private int _essSerial;                  // bumped on any screen edit: preview + buffers
    private GameViewImage? _essPreview;
    private string _essPreviewKey = "";
    private nint _essPreviewRenderer;

    /// <summary>Reset the workbench when the selected stop / pane changes.</summary>
    private void StoryContext(string ctx)
    {
        if (_essCtx == ctx) return;
        _essCtx = ctx;
        _essSel = 0;
        _essTextFor = "";
    }

    private static string PicLabel(int pic) => pic switch
    {
        < 0 => "keep what is on screen",
        0 => "tshp2.pcx",
        <= 14 => $"picture {pic}  (palette {PicFile.PaletteFor(pic)})",
        > 900 => $"clear to palette {PicFile.PaletteFor(pic - 900)}",
        _ => $"picture {pic}",
    };

    private static readonly (int Pic, string Hint)[] PicChoices = BuildPicChoices();

    private static (int, string)[] BuildPicChoices()
    {
        var list = new List<(int, string)> { (-1, ""), (0, "") };
        for (int p = 1; p <= 14; p++) list.Add((p, ""));
        for (int p = 901; p <= 914; p++) list.Add((p, ""));
        return list.ToArray();
    }

    /// <summary>The backdrop the engine would still have on screen when it reaches
    /// <paramref name="index"/>: the screen's own picture, else the nearest earlier one.</summary>
    private static int EffectivePicture(List<StoryScreen> screens, int index)
    {
        for (int i = Math.Min(index, screens.Count - 1); i >= 0; i--)
        {
            if (screens[i].Picture >= 0) return screens[i].Picture;
            if (screens[i].Fade == 3) return 901 + 7;   // ]C clears to palette 7
        }
        return -1;
    }

    /// <summary>
    /// The screen-list workbench. Returns true when anything changed (the caller marks the
    /// flow dirty and regenerates the script).
    /// </summary>
    private bool DrawStoryScreens(EditableEpisode ep, List<StoryScreen> screens, string ctx,
        string intro)
    {
        StoryContext(ctx);
        bool ch = false;
        var avail = ImGui.GetContentRegionAvail();

        // ---- the screen list ----
        float listW = 190f;
        WellBegin("esslist", new Vector2(listW, avail.Y), AcEdit, padX: 6f, padY: 6f);
        UiSection("Screens", AcEdit, screens.Count.ToString());
        float footer = ImGui.GetFrameHeight() * 2f + 12f;
        ImGui.BeginChild("essrows", new Vector2(0, -footer));
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            string first = s.Lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no text)";
            var row = UiRow($"##ess{i}", i == _essSel, AcEdit, 36f);
            string sub = (s.Picture >= 0 ? $"pic {s.Picture} " : "") +
                (s.Music > 0 ? $"song {s.Music} " : "") +
                (s.WarningFrame ? "warning" : "");
            RowText(row, 10f, $"{i + 1}  {first}", sub.Trim().Length > 0 ? sub.Trim() : "text only",
                AcEdit, row.Selected);
            if (row.Clicked) { _essSel = i; _essTextFor = ""; }
        }
        if (screens.Count == 0)
            UiEmpty("no screens", "add one below", AcEdit);
        ImGui.EndChild();

        float w2 = (ImGui.GetContentRegionAvail().X - 5f) * 0.5f;
        if (UiButton("+ Add", AcGo, "Append a text screen.", w2))
        {
            var made = new StoryScreen();
            if (screens.Count == 0) { made.Picture = 5; made.Fade = 3; }
            made.Lines.Add("");
            screens.Add(made);
            _essSel = screens.Count - 1;
            _essTextFor = "";
            ch = true;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Remove", AcEnemy, "", w2, _essSel >= screens.Count))
        {
            screens.RemoveAt(_essSel);
            _essSel = Math.Clamp(_essSel, 0, Math.Max(0, screens.Count - 1));
            _essTextFor = "";
            ch = true;
        }
        if (UiButton("Move up", AcEdit, "", w2, _essSel <= 0 || _essSel >= screens.Count))
        {
            (screens[_essSel - 1], screens[_essSel]) = (screens[_essSel], screens[_essSel - 1]);
            _essSel--;
            _essTextFor = "";
            ch = true;
        }
        ImGui.SameLine(0, 5);
        if (UiButton("Move down", AcEdit, "", w2, _essSel >= screens.Count - 1))
        {
            (screens[_essSel + 1], screens[_essSel]) = (screens[_essSel], screens[_essSel + 1]);
            _essSel++;
            _essTextFor = "";
            ch = true;
        }
        WellEnd();

        ImGui.SameLine(0, 8f);

        // ---- the selected screen's form + preview ----
        ImGui.BeginChild("essdetail", new Vector2(0, avail.Y));
        if (intro.Length > 0)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextColored(ColorOf(UiDim), intro);
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0, 3f));
        }
        if (_essSel >= screens.Count)
        {
            UiEmpty("no screen selected", "each screen is one full-screen page of text\nwith its own backdrop and music", AcEdit);
            ImGui.EndChild();
            return ch;
        }

        var scr = screens[_essSel];
        float formW = Math.Max(300f, ImGui.GetContentRegionAvail().X - 356f);
        ImGui.BeginChild("essform", new Vector2(formW, 0));
        ch |= DrawScreenForm(scr);
        ImGui.EndChild();
        ImGui.SameLine(0, 8f);
        ImGui.BeginChild("essprev", new Vector2(0, 0));
        DrawTextScreenPreview(EffectivePicture(screens, _essSel), scr.Lines,
            scr.WarningFrame, scr.Red > 0, $"{ctx}:{_essSel}");
        ImGui.EndChild();
        ImGui.EndChild();

        if (ch) _essSerial++;
        return ch;
    }

    /// <summary>Every field of one screen. Returns changed.</summary>
    private bool DrawScreenForm(StoryScreen scr)
    {
        bool ch = false;

        UiSection("Text", AcEdit, $"{scr.Lines.Count}/{StoryScreen.MaxLines} lines");
        string key = $"{_essCtx}:{_essSel}:{_essSerial}";
        if (_essTextFor != key)
        {
            int n = System.Text.Encoding.Latin1.GetBytes(
                string.Join('\n', scr.Lines), new Span<byte>(_essTextBuf, 0, _essTextBuf.Length - 1));
            _essTextBuf[n] = 0;
            _essTextFor = key;
        }
        float textH = ImGui.GetTextLineHeight() * 9f;
        fixed (byte* p = _essTextBuf)
            if (ImGui.InputTextMultiline("##esstext", p, (nuint)_essTextBuf.Length,
                    new Vector2(ImGui.GetContentRegionAvail().X - 4f, textH)))
            {
                scr.Lines = BufText(_essTextBuf).Split('\n').Select(StoryScreen.ClipLine)
                    .Take(StoryScreen.MaxLines).ToList();
                ch = true;
                // Keep the buffer as typed; it re-syncs when the selection moves.
                _essTextFor = $"{_essCtx}:{_essSel}:{_essSerial + 1}";
            }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Up to {StoryScreen.MaxLines} lines of {StoryScreen.MaxLineLen} characters -\n" +
                "the engine's own buffer. Blank lines are real spacing.\n" +
                "~word~ shows the word highlighted.");
        var over = scr.Lines.Select((l, i) => (l, i)).Where(t => t.l.Length > StoryScreen.MaxLineLen).ToList();
        if (over.Count > 0)
            ImGui.TextColored(ColorOf(Shade(AcEnemy, 1.1f)),
                $"line {over[0].i + 1} is over {StoryScreen.MaxLineLen} chars and will be clipped");

        UiSection("Backdrop", AcEdit);
        ImGui.SetNextItemWidth(210f);
        if (ImGui.BeginCombo("picture", PicLabel(scr.Picture)))
        {
            foreach (var (pic, _) in PicChoices)
                if (ImGui.Selectable(PicLabel(pic), pic == scr.Picture) && pic != scr.Picture)
                {
                    scr.Picture = pic;
                    ch = true;
                }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What ]P puts on screen before the text: one of tyrian.pic's\n" +
                "fourteen paintings, or a plain clear to a palette.");
        if (scr.Picture is >= 1 and <= 14)
        {
            ImGui.SetNextItemWidth(180f);
            string[] wipes = { "fade in  (]P)", "wipe up  (]U)", "wipe down  (]V)", "wipe right  (]R)" };
            int wipe = Math.Clamp(scr.Wipe, 0, 3);
            if (ImGui.Combo("arrives by", ref wipe, wipes, wipes.Length)) { scr.Wipe = wipe; ch = true; }
        }
        ImGui.SetNextItemWidth(180f);
        string[] fades = { "nothing", "fade to black  (]B)", "white flash  (]F)", "fade + dark palette  (]C)" };
        int fade = Math.Clamp(scr.Fade, 0, 3);
        if (ImGui.Combo("before it", ref fade, fades, fades.Length)) { scr.Fade = fade; ch = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The transition the engine runs before this screen's backdrop.");

        UiSection("Sound and pacing", AcEdit);
        ImGui.SetNextItemWidth(110f);
        int music = scr.Music;
        if (ImGui.InputInt("song (0 = keep)", ref music)) { scr.Music = Math.Clamp(music, 0, 41); ch = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("]M - starts a music.mus song before the text. 0 leaves the\ncurrent song playing.");
        ImGui.SetNextItemWidth(110f);
        int speed = scr.Speed;
        if (ImGui.InputInt("type-in speed", ref speed)) { scr.Speed = Math.Clamp(speed, 0, 9); ch = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Frames per character of the glow effect. 0 shows the whole\nscreen at once; stock cutscenes use 3.");

        UiSection("Warning dress", AcEdit);
        bool frame = scr.WarningFrame;
        if (UiToggle("flashing WARNING bars + siren", ref frame, AcEdit,
                "]Wy - the pulsing red bars and warning sound around the text."))
        {
            scr.WarningFrame = frame;
            ch = true;
        }
        ImGui.SetNextItemWidth(110f);
        int red = scr.Red;
        if (ImGui.InputInt("red alert (0-9)", ref red)) { scr.Red = Math.Clamp(red, 0, 9); ch = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Non-zero moves the text to the top and shows it in the red\ncolour bank - the classic pre-boss klaxon look.");
        return ch;
    }

    /// <summary>The live 320x200 preview, composed like the engine composes it.</summary>
    private void DrawTextScreenPreview(int effectivePic, List<string> lines, bool warningFrame,
        bool red, string key)
    {
        if (_gd == null) return;
        float availW = ImGui.GetContentRegionAvail().X;
        float scale = Math.Clamp(availW / TextScreenRender.W, 0.75f, 1.6f);

        string want = $"{key}:{_essSerial}:{effectivePic}:{warningFrame}:{red}:{string.Join("|", lines)}";
        nint rh = (nint)_activeRenderer.Handle;
        if (_essPreview == null || _essPreviewRenderer != rh)
        {
            _essPreview?.Dispose();
            _essPreview = new GameViewImage();
            _essPreviewRenderer = rh;
            _essPreviewKey = "";
        }
        if (_essPreviewKey != want)
        {
            var (screen, pal) = TextScreenRender.Compose(_gd.Pics, _gd.Main, effectivePic,
                lines, warningFrame, red);
            _essPreview.Update(_activeRenderer, screen, _gd.Palettes.Get(pal),
                0, 0, TextScreenRender.W, TextScreenRender.H, stride: TextScreenRender.W);
            _essPreviewKey = want;
        }

        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(TextScreenRender.W * scale, TextScreenRender.H * scale);
        dl.AddRectFilled(p - new Vector2(2, 2), p + size + new Vector2(2, 2), Gfx.Rgba(6, 7, 10), 3f);
        _essPreview.Draw(dl, p, scale);
        dl.AddRect(p - new Vector2(2, 2), p + size + new Vector2(2, 2), Shade(AcEdit, 0.5f, 140), 3f);
        ImGui.Dummy(size + new Vector2(4, 6));
        ImGui.TextColored(ColorOf(UiFaint), "as the game will draw it");
    }
}
