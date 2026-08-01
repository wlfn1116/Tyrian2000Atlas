using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>
/// The other half of the sprite browser: the full-screen pictures, which are PCX rather than
/// sprite banks. Everything the game draws that is not a sprite is one of these — the shop
/// wall, the in-game frame, the title screen, the intro logos, every cutscene backdrop the
/// episode scripts fade in — packed fourteen to a file in tyrian.pic, plus the loose .pcx
/// files sitting beside it.
///
/// They share the Sprites window rather than getting one of their own: the same list picks
/// them, and picking one swaps the grid for the picture. What they cannot share is the grid
/// itself. A sprite bank is hundreds of 12x14 stamps with colour 0 meaning "transparent"; a
/// picture is one opaque 320x200 painting in a palette of its own, and pushing it through the
/// sprite atlas would have punched a checkerboard through every black pixel in it.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>Which picture the window is showing, as a position in
    /// <see cref="AllPictureSources"/>; -1 means a sprite bank is showing instead.</summary>
    private int _picSelected = -1;
    /// <summary>0 fits the picture to the pane; 1..4 is a whole-number blow-up.</summary>
    private int _picZoom;
    /// <summary>Decode through the palette the game uses for this picture, rather than the
    /// one the band's slider selects. On by default — the point of the pane is what the
    /// picture actually looks like in play.</summary>
    private bool _picOwnPalette = true;

    private GameViewImage? _picImage;
    private string _picKey = "";
    private nint _picRenderer;

    // The last picture unpacked, kept because the pane re-reads it every frame for its size
    // and its texture key. tyrian.pic hands back a fresh 64,000-byte buffer per call and
    // re-runs the RLE to fill it, which is not something to do sixty times a second; the
    // loose .pcx files are already cached whole by GameData.
    private PictureSource _picUnpackedFor = new(-1);
    private byte[] _picPixels = Array.Empty<byte>();
    private int _picW, _picH;
    private string _picWhy = "";

    private void DropPictureImage()
    {
        _picImage?.Dispose();
        _picImage = null;
        _picRenderer = 0;
        _picKey = "";
        _picUnpackedFor = new PictureSource(-1);
        _picPixels = Array.Empty<byte>();
    }

    /// <summary>The picture's indexed pixels and its size, cached for the shown one.</summary>
    private bool PicturePixels(PictureSource src, out byte[] px, out int w, out int h, out string why)
    {
        if (src != _picUnpackedFor)
        {
            _picUnpackedFor = src;
            _picPixels = Array.Empty<byte>();
            _picW = _picH = 0;
            _picWhy = "";
            if (_gd == null) _picWhy = "No data folder is loaded.";
            else if (src.InPicFile)
            {
                if (_gd.Pics is not { } pics) _picWhy = "tyrian.pic is not in this data folder.";
                else if (pics.Decode(src.Pic) is not { } img) _picWhy = $"tyrian.pic has no picture {src.Pic}.";
                else { _picPixels = img; _picW = PicFile.W; _picH = PicFile.H; }
            }
            else if (_gd.GetPcx(src.File) is not { } pcx)
                _picWhy = $"{src.File} is not an 8-bit PCX this can read.";
            else { _picPixels = pcx.Pixels; _picW = pcx.W; _picH = pcx.H; }
        }
        px = _picPixels;
        w = _picW;
        h = _picH;
        why = _picWhy;
        return px.Length > 0;
    }

    /// <summary>
    /// The palette a picture is drawn through: pcxpal for a tyrian.pic entry (pcxmast.c:23,
    /// applied by JE_loadPic), and the file's own trailing block for a loose .pcx — JE_loadPCX
    /// reads the colours out of the file and never touches palette.dat, which is why these two
    /// cannot share one palette rule. Turning "game palette" off substitutes the band's.
    /// </summary>
    private void PicturePalette(PictureSource src, out uint[] pal, out string note)
    {
        var own = src.InPicFile ? null : _gd?.GetPcx(src.File)?.Palette;
        if (_picOwnPalette && src.InPicFile)
        {
            int p = PicFile.PaletteFor(src.Pic);
            pal = _gd!.Palettes.Get(p);
            note = $"palette {p}";
        }
        else if (_picOwnPalette && own != null)
        {
            pal = own;
            note = "the file's own palette";
        }
        else
        {
            pal = _gd!.Palettes.Get(_sprPalette);
            // A file carrying no palette block has to borrow one; say so, rather than showing
            // it in palette.dat colours as if that were what it holds.
            note = _picOwnPalette ? $"no palette in the file - showing palette {_sprPalette}"
                                  : $"palette {_sprPalette}";
        }
    }

    /// <summary>The pictures' own section of the bank list. Returns true if any row survived
    /// the filter, so the list can tell "nothing matches" from "nothing here".</summary>
    private bool DrawPictureList(string filter)
    {
        var all = AllPictureSources();
        var items = new List<(int Index, PictureSource Src)>();
        for (int i = 0; i < all.Count; i++)
            if (filter.Length == 0 || Matches(filter, all[i].Title, all[i].ListTitle))
                items.Add((i, all[i]));
        if (items.Count == 0) return false;

        UiSection("Full-screen pictures", AcSprite, items.Count.ToString());
        foreach (var (index, src) in items)
        {
            bool sel = index == _picSelected;
            var box = UiRow($"##pic{index}", sel, AcSprite, 30f);
            if (box.Clicked) { _picSelected = index; _sprSelected = -1; }
            if (box.Hovered) ImGui.SetTooltip(src.Title);
            RowText(box, 11f, src.ListTitle, src.ListNote, AcSprite, sel);
            if (sel && _sprScrollBankList) ImGui.SetScrollHereY(0.5f);
        }
        return true;
    }

    /// <summary>The picture pane: what it is, how it is being decoded, and the picture.</summary>
    private void DrawPicturePane()
    {
        var all = AllPictureSources();
        if (_picSelected < 0 || _picSelected >= all.Count)
        {
            _picSelected = -1;
            return;
        }
        var src = all[_picSelected];

        if (!PicturePixels(src, out var px, out int w, out int h, out string why))
        {
            UiTitle(src.ListTitle.ToUpperInvariant(), AcSprite, src.Title);
            UiEmpty("This picture could not be read", why, AcSprite);
            return;
        }
        PicturePalette(src, out var pal, out string palNote);

        UiTitle(src.ListTitle.ToUpperInvariant(), AcSprite, src.Title);
        Badge($"{w}x{h}", AcSprite);
        ImGui.SameLine(0, 5f);
        Badge(palNote, Gfx.Rgba(150, 162, 185));
        ImGui.SameLine(0, 5f);
        Badge(src.InPicFile ? "tyrian.pic" : "PCX file", Gfx.Rgba(150, 162, 185));

        ImGui.Dummy(new Vector2(0, 4f));
        DrawPictureControls(src, w, h);

        WellBegin("picview", ImGui.GetContentRegionAvail(), AcSprite, 7f, 7f,
            ImGuiWindowFlags.HorizontalScrollbar);
        DrawPictureImage(src, px, w, h, pal);
        WellEnd();
    }

    private void DrawPictureControls(PictureSource src, int w, int h)
    {
        BandBegin("picband", AcSprite);
        BandLabel("zoom");
        ImGui.SetNextItemWidth(112);
        ImGui.SliderInt("##piczoom", ref _picZoom, 0, 4, _picZoom == 0 ? "fit" : "%dx");
        SliderReset(ref _picZoom, 0,
            "How far the picture is blown up. \"fit\" uses the largest whole\n" +
            "multiple the pane holds, so the pixels stay square.", "fit");

        BandDivider(9f);
        UiToggle("game palette", ref _picOwnPalette, AcSprite,
            src.InPicFile
                ? "Decode through the palette the game loads with this picture\n" +
                  "(pcxpal). Off uses the palette the band above selects."
                : "Decode through the palette stored in the .pcx itself, which is\n" +
                  "what JE_loadPCX does. Off uses the palette the band above selects.");

        BandDivider(9f);
        bool windows = OperatingSystem.IsWindows();
        if (UiButton("export PNG", AcSprite,
                $"Save this picture as a {w}x{h} PNG at 1:1, fully opaque, in the\n" +
                "palette it is shown in here.",
                0f, SpriteExportBusy || !windows) && windows)
            ExportPicture(src);
        BandEnd();
    }

    private void DrawPictureImage(PictureSource src, byte[] px, int w, int h, uint[] pal)
    {
        nint rh = (nint)_activeRenderer.Handle;
        if (_picImage == null || _picRenderer != rh)
        {
            _picImage?.Dispose();
            _picImage = new GameViewImage();
            _picRenderer = rh;
            _picKey = "";
        }
        // The palette is in the key by index rather than by content: two palettes can hold
        // the same colours, and re-uploading 64,000 pixels a frame to be sure would cost more
        // than the whole rest of the window.
        string want = $"{src.Pic}|{src.File}|{(_picOwnPalette ? -1 : _sprPalette)}";
        if (_picKey != want)
        {
            _picImage.Update(_activeRenderer, px, pal, 0, 0, w, h, stride: w);
            _picKey = want;
        }

        var avail = ImGui.GetContentRegionAvail();
        float scale;
        if (_picZoom > 0) scale = _picZoom;
        else
        {
            // Whole multiples once there is room for one, so a 320x200 painting is never
            // resampled onto a half-pixel grid; below 1:1 the fractional fit is the only way
            // to show the whole thing at all.
            float fit = Math.Min(avail.X / Math.Max(1, w), avail.Y / Math.Max(1, h));
            scale = fit >= 1f ? MathF.Floor(fit) : Math.Max(0.05f, fit);
        }

        var size = new Vector2(w * scale, h * scale);
        // Centred in whatever room is left over, so a picture smaller than the pane does not
        // sit in a corner; the Dummy is the full extent so the scrollbars still find it.
        var origin = ImGui.GetCursorScreenPos() + new Vector2(
            MathF.Round(Math.Max(0f, (avail.X - size.X) * 0.5f)),
            MathF.Round(Math.Max(0f, (avail.Y - size.Y) * 0.5f)));

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(origin - new Vector2(2, 2), origin + size + new Vector2(2, 2),
            Gfx.Rgba(6, 7, 10), 3f);
        _picImage.Draw(dl, origin, scale);
        dl.AddRect(origin - new Vector2(2, 2), origin + size + new Vector2(2, 2),
            Shade(AcSprite, 0.5f, 140), 3f);
        ImGui.Dummy(new Vector2(Math.Max(avail.X, size.X), Math.Max(avail.Y, size.Y)));
    }

    /// <summary>The picture at 1:1 and fully opaque — colour 0 is a real colour in a
    /// backdrop, not the sprite formats' transparency.</summary>
    private bool BuildPictureRgba(PictureSource src, out int w, out int h, out uint[] rgba,
        out string why)
    {
        rgba = Array.Empty<uint>();
        if (!PicturePixels(src, out var px, out w, out h, out why)) return false;
        PicturePalette(src, out var pal, out _);
        rgba = new uint[w * h];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = pal[px[i]] | 0xFF000000u;
        return true;
    }

    /// <summary>What an exported picture is called: where it came from, and which palette it
    /// was actually decoded through, so two exports of the same picture never collide and the
    /// name never claims a palette the file does not carry.</summary>
    private string PictureFileName(PictureSource src)
    {
        string pal;
        if (!_picOwnPalette) pal = $"pal{_sprPalette}";
        else if (src.InPicFile) pal = $"pal{PicFile.PaletteFor(src.Pic)}";
        else pal = _gd?.GetPcx(src.File)?.Palette != null ? "ownpal" : $"pal{_sprPalette}";
        return $"{src.FileStem}_{pal}.png";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ExportPicture(PictureSource src)
    {
        if (_gd == null || SpriteExportBusy) return;
        if (!BuildPictureRgba(src, out int w, out int h, out var rgba, out string why))
        { _status = why; return; }
        StartSpritePngExport(PictureFileName(src), w, h, rgba);
    }
}
