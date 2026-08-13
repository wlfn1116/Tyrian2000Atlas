using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Tyrian;
using SdlNs = Hexa.NET.SDL2;

namespace T2A.Render;

/// <summary>
/// A whole sprite bank packed into one SDL texture on a fixed grid, so a browser can put
/// hundreds of sprites on screen without a texture per sprite. The cell is the bank's
/// largest sprite; every sprite sits at its cell's top-left, and <see cref="Draw"/> emits
/// only that sprite's own rectangle, so nothing is padded or stretched.
///
/// Index 0 is the sprite formats' transparent colour, so it uploads as transparent rather
/// than as palette entry 0 -- same rule as <see cref="SpriteImage"/>.
/// </summary>
public sealed unsafe class SpriteAtlas : IDisposable
{
    /// <summary>Keep a row inside anything a GPU will accept; banks are far smaller in practice.</summary>
    private const int MaxTexDim = 4096;

    /// <summary>
    /// Empty texels around every cell, with each sprite's edge pixels replicated into its
    /// ring. Sampling at a cell's exact edge lands on the replicated pixel instead of the
    /// neighbouring sprite — without this, fractional zooms showed dashes of whatever
    /// happened to sit next door in the atlas, exactly along tile boundaries.
    /// </summary>
    private const int Gutter = 1;

    public int Count { get; private set; }
    public int CellW { get; private set; }
    public int CellH { get; private set; }

    private int _cols, _texW, _texH;
    private (int W, int H)[] _size = Array.Empty<(int, int)>();
    private SdlNs.SDLTexturePtr _tex;
    private bool _created;

    /// <summary>The sprite's real size, which is what a caller centres and hit-tests against.</summary>
    public (int W, int H) SizeOf(int index)
        => index >= 0 && index < _size.Length ? _size[index] : (0, 0);

    public bool Has(int index) => index >= 0 && index < _size.Length && _size[index].W > 0;

    /// <summary>
    /// Pack <paramref name="sprites"/> (index-aligned; nulls become empty cells) through
    /// <paramref name="palette"/>. Rebuild whenever either changes -- callers key a cache on
    /// (bank, palette) and only call this on a miss.
    /// </summary>
    public void Build(SdlNs.SDLRendererPtr renderer, IReadOnlyList<Sprite?> sprites, uint[] palette)
    {
        Dispose();
        Count = sprites.Count;
        _size = new (int, int)[Count];
        if (Count == 0) return;

        int cw = 1, ch = 1;
        for (int i = 0; i < Count; i++)
        {
            var s = sprites[i];
            if (s == null || s.W <= 0 || s.H <= 0) continue;
            _size[i] = (s.W, s.H);
            if (s.W > cw) cw = s.W;
            if (s.H > ch) ch = s.H;
        }
        CellW = cw; CellH = ch;
        int strideW = cw + Gutter * 2, strideH = ch + Gutter * 2;

        // Roughly square, but never wider than a texture allows.
        _cols = Math.Max(1, Math.Min((int)Math.Ceiling(Math.Sqrt(Count)), MaxTexDim / strideW));
        int rows = (Count + _cols - 1) / _cols;
        // A bank tall enough to overflow would have to be enormous; clamp rather than fail,
        // and the cells past the cut simply report size 0 and draw nothing.
        int maxRows = Math.Max(1, MaxTexDim / strideH);
        if (rows > maxRows)
        {
            rows = maxRows;
            for (int i = _cols * rows; i < Count; i++) _size[i] = (0, 0);
        }
        _texW = _cols * strideW;
        _texH = rows * strideH;

        var rgba = new uint[_texW * _texH];
        for (int i = 0; i < Count; i++)
        {
            var s = sprites[i];
            if (s == null || _size[i].W == 0) continue;
            int cellX = (i % _cols) * strideW + Gutter, cellY = (i / _cols) * strideH + Gutter;
            if (cellY + s.H > _texH) continue;
            for (int y = 0; y < s.H; y++)
            {
                int src = y * s.W, dst = (cellY + y) * _texW + cellX;
                for (int x = 0; x < s.W; x++)
                {
                    byte c = s.Pixels[src + x];
                    rgba[dst + x] = c == 0 ? 0u : palette[c] | 0xFF000000u;
                }
            }
            // Replicate the sprite's edges into its gutter ring, so a sample that lands a
            // hair outside the rect still reads this sprite's own pixels.
            for (int y = 0; y < s.H; y++)
            {
                int row = (cellY + y) * _texW;
                rgba[row + cellX - 1] = rgba[row + cellX];
                rgba[row + cellX + s.W] = rgba[row + cellX + s.W - 1];
            }
            int top = (cellY - 1) * _texW, first = cellY * _texW;
            int bot = (cellY + s.H) * _texW, last = (cellY + s.H - 1) * _texW;
            for (int x = -1; x <= s.W; x++)
            {
                rgba[top + cellX + x] = rgba[first + cellX + Math.Clamp(x, 0, s.W - 1)];
                rgba[bot + cellX + x] = rgba[last + cellX + Math.Clamp(x, 0, s.W - 1)];
            }
        }

        _tex = SdlNs.SDL.CreateTexture(renderer, Gfx.SDL_PIXELFORMAT_ABGR8888,
            Gfx.SDL_TEXTUREACCESS_STATIC, _texW, _texH);
        SdlNs.SDL.SetTextureBlendMode(_tex, SdlNs.SDLBlendMode.Blend);
        // Pixel art scales as pixels: no interpolation, no cross-cell sampling.
        SdlNs.SDL.SetTextureScaleMode(_tex, SdlNs.SDLScaleMode.Nearest);
        fixed (uint* p = rgba)
            SdlNs.SDL.UpdateTexture(_tex, default, (nint)p, _texW * 4);
        _created = true;
    }

    /// <summary>Draw one sprite with its top-left at <paramref name="pos"/>.</summary>
    public void Draw(ImDrawListPtr dl, int index, Vector2 pos, float scale, uint tint = 0xFFFFFFFF)
    {
        if (!_created || index < 0 || index >= Count) return;
        var (w, h) = _size[index];
        if (w == 0) return;

        float u0 = ((index % _cols) * (CellW + Gutter * 2) + Gutter) / (float)_texW;
        float v0 = ((index / _cols) * (CellH + Gutter * 2) + Gutter) / (float)_texH;
        dl.AddImage(Gfx.TexRef((nint)_tex.Handle), pos, pos + new Vector2(w * scale, h * scale),
            new Vector2(u0, v0), new Vector2(u0 + (float)w / _texW, v0 + (float)h / _texH), tint);
    }

    /// <summary>Draw one sprite centred in a box -- how a list row or a grid cell shows it.</summary>
    public void DrawCentered(ImDrawListPtr dl, int index, Vector2 boxMin, Vector2 boxMax,
        float scale, uint tint = 0xFFFFFFFF)
    {
        var (w, h) = SizeOf(index);
        if (w == 0) return;
        var at = new Vector2(
            MathF.Round((boxMin.X + boxMax.X - w * scale) * 0.5f),
            MathF.Round((boxMin.Y + boxMax.Y - h * scale) * 0.5f));
        Draw(dl, index, at, scale, tint);
    }

    public void Dispose()
    {
        if (_created) SdlNs.SDL.DestroyTexture(_tex);
        _created = false;
        Count = 0;
        _size = Array.Empty<(int, int)>();
    }
}
