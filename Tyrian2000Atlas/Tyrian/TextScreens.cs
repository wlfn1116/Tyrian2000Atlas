namespace T2A.Tyrian;

/// <summary>
/// tyrian.pic — the full-screen backdrops the episode script's ]P/]U/]V/]R commands load:
/// u16 count, s32 offsets, then per picture a 320x200 PCX-style RLE stream (top two bits
/// set = run of the following byte). See picload.c:JE_loadPic.
/// </summary>
public sealed class PicFile
{
    public const int W = 320, H = 200;

    private readonly byte[] _raw;
    private readonly int[] _pos;
    public int Count { get; }

    /// <summary>pcxpal (pcxmast.c:23): the palette each 1-based picture is drawn in;
    /// also what a &gt;900 ]P clears the screen to.</summary>
    public static readonly int[] PicPalette = { 0, 7, 5, 8, 10, 5, 18, 19, 19, 20, 21, 22, 5, 23 };

    public static int PaletteFor(int pic1Based) =>
        pic1Based >= 1 && pic1Based <= PicPalette.Length ? PicPalette[pic1Based - 1] : 0;

    private PicFile(byte[] raw, int[] pos, int count)
    {
        _raw = raw;
        _pos = pos;
        Count = count;
    }

    public static PicFile Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var r = new ByteReader(raw);
        int count = r.U16();
        var pos = new int[count + 1];
        for (int i = 0; i < count; i++) pos[i] = r.S32();
        pos[count] = raw.Length;
        return new PicFile(raw, pos, count);
    }

    /// <summary>Decode one 1-based picture into 320x200 palette indices, or null.</summary>
    public byte[]? Decode(int pic1Based)
    {
        int i = pic1Based - 1;
        if (i < 0 || i >= Count || _pos[i] < 0 || _pos[i] >= _pos[i + 1] || _pos[i + 1] > _raw.Length)
            return null;
        var img = new byte[W * H];
        int p = _pos[i], at = 0, end = _pos[i + 1];
        while (at < img.Length && p < end)
        {
            byte b = _raw[p++];
            if ((b & 0xC0) == 0xC0)
            {
                int run = b & 0x3F;
                if (p >= end) break;
                byte v = _raw[p++];
                for (int k = 0; k < run && at < img.Length; k++) img[at++] = v;
            }
            else img[at++] = b;
        }
        return img;
    }
}

/// <summary>
/// Renders episode-script text screens the way JE_displayText composes them — the
/// backdrop, the 10px-spaced glow text at x=10, the WARNING bars — into a 320x200
/// palette-indexed buffer plus the palette it should be shown in. Used by the editor to
/// preview story screens, warnings and the ending exactly as the engine will draw them.
/// </summary>
public static class TextScreenRender
{
    public const int W = PicFile.W, H = PicFile.H;

    /// <summary>font_ascii (fonthand.c:30): character to TINY_FONT sprite index.</summary>
    private static readonly int[] FontAscii = BuildFontAscii();

    private static int[] BuildFontAscii()
    {
        var map = new int[256];
        Array.Fill(map, -1);
        void Run(int from, string chars, int start)
        {
            for (int i = 0; i < chars.Length; i++) map[chars[i]] = start + i;
            _ = from;
        }
        Run(0, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", 0);
        map['!'] = 26; map['?'] = 27; map['.'] = 28; map[','] = 29; map[';'] = 30;
        map[':'] = 31; map['\''] = 32; map['"'] = 33;
        Run(0, "abcdefghijklmnopqrstuvwxyz", 34);
        map['#'] = 60; map['$'] = 61; map['%'] = 62; map['*'] = 63; map['('] = 64;
        map[')'] = 65; map['{'] = 66; map['}'] = 67; map['['] = 68; map[']'] = 69;
        Run(0, "123456789", 70);             // '0' sits AFTER 1-9 in the sheet
        map['0'] = 79;
        map['/'] = 80; map['|'] = 81; map['\\'] = 82; map['-'] = 83; map['+'] = 84;
        map['='] = 85;
        for (int i = 0; i < 41; i++) map[128 + i] = 86 + i;   // the Latin-1 accents block
        return map;
    }

    /// <summary>Pixel width of a string in the tiny font, spaces and ~toggles included.</summary>
    public static int TextWidth(SpriteBank? font, string s)
    {
        int w = 0;
        foreach (char c in s)
        {
            if (c == ' ') { w += 6; continue; }
            if (c == '~') continue;
            int id = c < 256 ? FontAscii[c] : -1;
            var spr = id >= 0 ? font?.Get(id) : null;
            if (spr != null) w += spr.W + 1;
        }
        return w;
    }

    /// <summary>
    /// One font string at its settled glow state: PART_SHADE (a black copy at +1,+1)
    /// under hue|value pixels, '~' toggling +4 brightness — fonthand.c:JE_outText.
    /// </summary>
    public static void DrawText(byte[] screen, SpriteBank? font, int x, int y, string s,
        int colorBank, int brightness = 0)
    {
        DrawTextPass(screen, font, x + 1, y + 1, s, 0, -1);
        DrawTextPass(screen, font, x, y, s, colorBank, brightness);
    }

    private static void DrawTextPass(byte[] screen, SpriteBank? font, int x, int y, string s,
        int colorBank, int brightness)
    {
        if (font == null) return;
        int bright = 0;
        foreach (char c in s)
        {
            if (c == ' ') { x += 6; continue; }
            if (c == '~') { bright = bright == 0 ? 4 : 0; continue; }
            int id = c < 256 ? FontAscii[c] : -1;
            var spr = id >= 0 ? font.Get(id) : null;
            if (spr == null) continue;
            BlitHv(screen, spr, x, y, colorBank, brightness < 0 ? -1 : brightness + bright);
            x += spr.W + 1;
        }
    }

    /// <summary>sprite.c:blit_sprite_hv — value &lt; 0 means the dark (all-black) form.</summary>
    private static void BlitHv(byte[] screen, Sprite spr, int x, int y, int hue, int value)
    {
        for (int sy = 0; sy < spr.H; sy++)
        {
            int dy = y + sy;
            if (dy < 0 || dy >= H) continue;
            for (int sx = 0; sx < spr.W; sx++)
            {
                byte pix = spr.Pixels[sy * spr.W + sx];
                if (pix == 0) continue;
                int dx = x + sx;
                if (dx < 0 || dx >= W) continue;
                if (value < 0) { screen[dy * W + dx] = 0; continue; }
                int v = (pix & 0x0F) + value;
                if (v > 0xF) v = v >= 0x1F ? 0 : 0xF;
                screen[dy * W + dx] = (byte)((hue << 4) | v);
            }
        }
    }

    /// <summary>The pulsing WARNING bars (fonthand.c:JE_updateWarning), frozen mid-pulse.</summary>
    public static void DrawWarningBars(byte[] screen)
    {
        byte col = 14 * 16 + 7;
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < W; x++) screen[y * W + x] = col;
        for (int y = 194; y < 200; y++)
            for (int x = 0; x < W; x++) screen[y * W + x] = col;
    }

    /// <summary>
    /// Compose one text screen: the backdrop named by <paramref name="picture"/> (-1 = a
    /// plain dark screen), the lines the way JE_displayText lays them out, the bars when
    /// <paramref name="warningFrame"/>. Returns the indexed screen and the palette index
    /// it should be presented with.
    /// </summary>
    public static (byte[] Screen, int Palette) Compose(PicFile? pics, MainShapes shapes,
        int picture, IReadOnlyList<string> lines, bool warningFrame, bool red,
        string? prompt = null)
    {
        var screen = new byte[W * H];
        int palette = 7;                     // the ]C fallback: dark menu palette
        if (picture is >= 1 and <= 14 && pics != null)
        {
            var img = pics.Decode(picture);
            if (img != null)
            {
                Array.Copy(img, screen, screen.Length);
                palette = PicFile.PaletteFor(picture);
            }
        }
        else if (picture > 900)
        {
            palette = PicFile.PaletteFor(picture - 900);
        }

        var font = shapes.Banks[2];          // TINY_FONT
        int bank = red ? 7 : 14;             // JE_outCharGlow's colour bank
        int y = red ? 2 : 55;
        for (int i = 0; i < Math.Min(lines.Count, StoryScreen.MaxLines); i++)
        {
            DrawText(screen, font, 10, y, StoryScreen.ClipLine(lines[i]), bank);
            y += 10;
        }
        if (prompt != null)
        {
            int px = (W - TextWidth(font, prompt)) / 2;
            DrawText(screen, font, px, red ? 7 * 16 + 6 : 184, prompt, bank);
        }
        if (warningFrame) DrawWarningBars(screen);
        return (screen, palette);
    }
}
