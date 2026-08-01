namespace T2A.Tyrian;

/// <summary>
/// A loose .pcx file from the data folder — ZSoft PCX, 8 bits a pixel, one plane, with the
/// 256-colour palette in the trailing 769-byte block. That is the only form the game ships
/// and the only one JE_loadPCX (pcxload.c:28) can read: it seeks -769 from the end for the
/// palette, skips the 128-byte header outright and unrolls exactly 320x200 pixels.
///
/// This honours the header rather than assuming it, so a file that is not 320x200 comes out
/// at its own size instead of as a torn 320-wide smear. The palette bytes are full 8-bit RGB,
/// <em>not</em> the 6-bit VGA values palette.dat holds — JE_loadPCX copies them into `colors`
/// with no expansion, so expanding them here would wash every one of these pictures out.
/// </summary>
public sealed class PcxImage
{
    public int W { get; }
    public int H { get; }

    /// <summary>W*H palette indices, row-major.</summary>
    public byte[] Pixels { get; }

    /// <summary>The file's own 256 colours, packed R,G,B,A the way <see cref="PaletteSet"/>
    /// packs palette.dat. Null when the file carries no trailing palette block.</summary>
    public uint[]? Palette { get; }

    private PcxImage(int w, int h, byte[] pixels, uint[]? palette)
    {
        W = w;
        H = h;
        Pixels = pixels;
        Palette = palette;
    }

    public static PcxImage Load(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 128)
            throw new InvalidDataException("too short to be a PCX");
        if (raw[0] != 0x0A)
            throw new InvalidDataException("not a PCX (manufacturer byte is not 10)");
        if (raw[3] != 8 || raw[65] != 1)
            throw new InvalidDataException(
                $"only 8-bit single-plane PCX is supported (this is {raw[3]}bpp, {raw[65]} planes)");

        int xmin = raw[4] | (raw[5] << 8), ymin = raw[6] | (raw[7] << 8);
        int xmax = raw[8] | (raw[9] << 8), ymax = raw[10] | (raw[11] << 8);
        int w = xmax - xmin + 1, h = ymax - ymin + 1;
        if (w <= 0 || h <= 0 || (long)w * h > 64_000_000L)
            throw new InvalidDataException($"implausible size {w}x{h}");

        // Rows are stored padded to bytesPerLine, which the spec makes even and so can run a
        // byte past the picture. Decode the whole row and keep the first w bytes of it.
        int stride = raw[66] | (raw[67] << 8);
        if (stride < w) stride = w;

        // A trailing palette sits in the last 769 bytes behind an 0x0C marker, so the RLE
        // stream has to stop short of it rather than decode the colours as pixels.
        uint[]? pal = null;
        int end = raw.Length;
        if (raw.Length >= 128 + 769 && raw[^769] == 0x0C)
        {
            end = raw.Length - 769;
            pal = new uint[PaletteSet.ColorsPerPalette];
            int o = raw.Length - 768;
            for (int i = 0; i < pal.Length; i++)
            {
                byte r = raw[o++], g = raw[o++], b = raw[o++];
                pal[i] = (uint)(r | (g << 8) | (b << 16) | (0xFFu << 24));
            }
        }

        var px = new byte[w * h];
        int p = 128;
        for (int y = 0; y < h && p < end; y++)
        {
            int x = 0;
            while (x < stride && p < end)
            {
                byte b = raw[p++];
                int run = 1;
                if ((b & 0xC0) == 0xC0)
                {
                    run = b & 0x3F;
                    if (p >= end) break;
                    b = raw[p++];
                }
                // The spec forbids a run crossing into the next row, so whatever lands past
                // the stride is padding and is dropped rather than wrapped.
                for (int k = 0; k < run && x < stride; k++, x++)
                    if (x < w) px[y * w + x] = b;
            }
        }
        return new PcxImage(w, h, px, pal);
    }
}
