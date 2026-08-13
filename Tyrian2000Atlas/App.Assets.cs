using System.Numerics;
using Hexa.NET.ImGui;
using T2A.Render;
using T2A.Tyrian;

namespace T2A;

/// <summary>Which store a sprite comes out of. The data set keeps four quite different ones.</summary>
public enum SpriteStore
{
    /// <summary>newsh%c.shp, addressed by the 1-based enemy shape bank the enemyDat names.</summary>
    Newsh,
    /// <summary>A CompShapes sub-table of tyrian.shp (7..12): ships, powerups, coins, gems.</summary>
    MainSheet,
    /// <summary>A Sprite_array sub-table of tyrian.shp (0..6): fonts, planets, faces, options, weapons.</summary>
    MainBank,
    /// <summary>shapes%c.dat -- the 600 terrain tiles a level's backgrounds are built from.</summary>
    Tiles,
    /// <summary>newsh%c.shp by its file character -- the five sheets the engine loads by name
    /// rather than through an enemy shape bank (the shop, explosions, Destruct, the ship
    /// editor's ships, and one loose copy of a tyrian.shp sheet).</summary>
    NewshFile,
    /// <summary>A shape file outside tyrian.shp: an index into
    /// <see cref="GameData.StandaloneShapes"/>.</summary>
    Standalone,
}

/// <summary>
/// One addressable sprite bank. <see cref="Index"/> means whatever the store indexes by: a
/// shape bank (1..36), a tyrian.shp sub-table, or a tile-set file character.
/// <see cref="Xmas"/> reads the same sub-table out of tyrianc.shp instead, which is how
/// Christmas mode works — a whole alternate shape file, not a set of extra sprites.
/// </summary>
public readonly record struct SpriteSource(SpriteStore Store, int Index, bool Xmas = false)
{
    public static SpriteSource Newsh(int bank) => new(SpriteStore.Newsh, bank);
    public static SpriteSource MainSheet(int i, bool xmas = false) => new(SpriteStore.MainSheet, i, xmas);
    public static SpriteSource MainBank(int i, bool xmas = false) => new(SpriteStore.MainBank, i, xmas);
    public static SpriteSource Tiles(char c) => new(SpriteStore.Tiles, c);
    public static SpriteSource NewshFile(char c) => new(SpriteStore.NewshFile, c);
    public static SpriteSource Standalone(int i) => new(SpriteStore.Standalone, i);

    /// <summary>The sheet the shop and HUD draw every purchasable item's icon from.</summary>
    public static SpriteSource Shop => NewshFile('1');

    public char FileChar => (char)Index;

    /// <summary>Sprite numbering base: the Sprite_array forms (tyrian.shp's own banks, and the
    /// stand-alone files that use the same layout) are 0-based; the compiled sheets are 1-based.</summary>
    public int FirstIndex => Store is SpriteStore.MainBank or SpriteStore.Standalone ? 0 : 1;

    /// <summary>tyrian.shp, or its Christmas twin.</summary>
    private string ShapeFile => Xmas ? "tyrianc.shp" : "tyrian.shp";

    public string Title => Store switch
    {
        SpriteStore.Newsh => $"Bank {Index:00}  ·  newsh{char.ToLowerInvariant(GameData.ShapeBankChar(Index))}.shp",
        SpriteStore.NewshFile => $"newsh{char.ToLowerInvariant(FileChar)}.shp  ·  {NewshFileRole(FileChar)}",
        SpriteStore.MainSheet => $"{ShapeFile} #{Index}  ·  {MainSheetName(Index)}" +
                                 (BankNote.Length > 0 ? $"  ·  {BankNote}" : ""),
        SpriteStore.MainBank => $"{ShapeFile} #{Index}  ·  {MainBankName(Index)}",
        SpriteStore.Standalone => $"{StandaloneFile}  ·  {StandaloneName}  ·  {StandaloneRole}",
        _ => $"shapes{char.ToLowerInvariant(FileChar)}.dat  ·  terrain tiles",
    };

    /// <summary>Short label for a list row.</summary>
    public string ShortName => Store switch
    {
        SpriteStore.Newsh => $"{Index:00}  newsh{char.ToLowerInvariant(GameData.ShapeBankChar(Index))}",
        SpriteStore.NewshFile => $"newsh{char.ToLowerInvariant(FileChar)}",
        SpriteStore.MainSheet => MainSheetName(Index),
        SpriteStore.MainBank => MainBankName(Index),
        SpriteStore.Standalone => StandaloneName,
        _ => $"shapes{char.ToLowerInvariant(FileChar)}",
    };

    /// <summary>The two halves a two-line list row wants: what the bank IS, then where it
    /// lives on disk. <see cref="Title"/> runs them together for headings and tooltips.</summary>
    public string ListTitle => Store switch
    {
        SpriteStore.Newsh => $"Bank {Index:00}",
        SpriteStore.NewshFile => $"newsh{char.ToLowerInvariant(FileChar)}.shp",
        SpriteStore.MainSheet => MainSheetName(Index),
        SpriteStore.MainBank => MainBankName(Index),
        SpriteStore.Standalone => StandaloneName,
        _ => $"Tile set {char.ToUpperInvariant(FileChar)}",
    };

    public string ListNote => Store switch
    {
        SpriteStore.Newsh => $"newsh{char.ToLowerInvariant(GameData.ShapeBankChar(Index))}.shp",
        // The role, not the file name: the file name is this row's title, and what a sheet
        // nothing points a shape bank at is FOR is the thing the list cannot otherwise say.
        SpriteStore.NewshFile => NewshFileRole(FileChar),
        // The two hard-coded sheets say their bank instead of their sub-table number: the group
        // heading already says which file, and 210px of list row fits one or the other, not both.
        // The heading and tooltip (Title) carry the pair.
        SpriteStore.MainSheet => BankNote.Length > 0 ? BankNote : $"{ShapeFile} #{Index}",
        SpriteStore.MainBank => $"{ShapeFile} #{Index}",
        SpriteStore.Standalone => StandaloneFile,
        _ => $"shapes{char.ToLowerInvariant(FileChar)}.dat",
    };

    /// <summary>The file this bank is read out of, for a message that has to name it.</summary>
    public string DiskFile => Store switch
    {
        SpriteStore.Newsh => $"newsh{char.ToLowerInvariant(GameData.ShapeBankChar(Index))}.shp",
        SpriteStore.NewshFile => $"newsh{char.ToLowerInvariant(FileChar)}.shp",
        SpriteStore.MainSheet or SpriteStore.MainBank => ShapeFile,
        SpriteStore.Standalone => StandaloneFile,
        _ => $"shapes{char.ToLowerInvariant(FileChar)}.dat",
    };

    private string StandaloneFile => Index >= 0 && Index < GameData.StandaloneShapes.Length
        ? GameData.StandaloneShapes[Index].File : "?";

    private string StandaloneName => Index >= 0 && Index < GameData.StandaloneShapes.Length
        ? GameData.StandaloneShapes[Index].Name : "?";

    /// <summary>What reads the file, which for three of the four is "nothing" -- worth saying,
    /// since a viewer showing them otherwise implies the game draws them.</summary>
    private string StandaloneRole => Index >= 0 && Index < GameData.StandaloneShapes.Length
        ? GameData.StandaloneShapes[Index].Role : "?";

    /// <summary>
    /// The shape bank a tyrian.shp sheet answers to, for the two JE_makeEnemy hard-codes. Said
    /// on the sheet's own row because these are the rows that carry banks 21 and 26 -- the
    /// "Enemy banks" group cannot list them, since neither ever reaches a newsh file.
    /// </summary>
    private string BankNote => Index switch
    {
        9 when !Xmas => "shape bank 26",
        10 when !Xmas => "shape bank 21",
        _ => "",
    };

    /// <summary>What a newsh sheet that no shape bank names is loaded for.</summary>
    private static string NewshFileRole(char c) => char.ToLowerInvariant(c) switch
    {
        '1' => "shop, HUD & mouse pointer",
        '6' => "explosions & score popups",
        '~' => "Destruct minigame",
        '$' => "ship editor's extra ships",
        '(' => "loose copy of tyrian.shp #7",
        _ => "loaded by name, not by bank",
    };

    // sprite.h:29-36 -- the Sprite_array sub-tables.
    private static string MainBankName(int i) => i switch
    {
        0 => "Font (large)",
        1 => "Font (small)",
        2 => "Font (tiny)",
        3 => "Planets",
        4 => "Faces",
        5 => "Options / help",
        6 => "Weapons",
        _ => $"bank {i}",
    };

    // The CompShapes sub-tables, named for what JE_loadMainShapeTables (sprite.c:1111) calls
    // them as it reads them -- these follow the seven Sprite_array banks in the same file.
    private static string MainSheetName(int i) => i switch
    {
        7 => "Player shots",
        8 => "Player ships",
        9 => "Powerups",
        10 => "Coins / datacubes",
        11 => "Player shots (more)",
        12 => "Ships (T2000)",
        _ => $"sheet {i}",
    };
}

/// <summary>
/// Shared asset plumbing for the reference browsers: the game's own fonts, and packed
/// atlases for any sprite bank in the data set. Atlases are keyed by (bank, palette) and
/// built on demand -- a browser can page through 36 banks without paying for the ones it
/// never shows, and a palette change simply misses the cache.
/// </summary>
public sealed unsafe partial class App
{
    /// <summary>Enough for every bank a window has on screen at once, several times over.</summary>
    private const int AtlasCacheMax = 28;

    // Keyed by renderer too: SDL textures belong to the renderer that made them, and the
    // same bank shown in the main window and the detached one needs a texture in each.
    private readonly Dictionary<(SpriteSource Src, int Palette, nint Renderer), SpriteAtlas> _atlases = new();
    private readonly List<(SpriteSource Src, int Palette, nint Renderer)> _atlasOrder = new();
    /// <summary>Atlases evicted mid-frame. Their textures may still sit in this frame's
    /// draw list, so destruction waits for the next frame's start — destroying at once
    /// blanked every sprite the frame had already queued from them.</summary>
    private readonly List<SpriteAtlas> _atlasGraveyard = new();

    /// <summary>Call once per frame, before any drawing, to bury last frame's evictions.</summary>
    private void PumpAtlasGraveyard()
    {
        foreach (var dead in _atlasGraveyard) dead.Dispose();
        _atlasGraveyard.Clear();
    }

    /// <summary>
    /// Release every texture made on one renderer — called by the host right before it
    /// destroys the detached window's renderer, while destroying them is still legal.
    /// </summary>
    public void DropRendererCaches(nint rendererHandle)
    {
        PumpAtlasGraveyard();   // both renderers are alive here; bury everything pending
        foreach (var key in _atlasOrder.Where(k => k.Renderer == rendererHandle).ToList())
        {
            _atlasOrder.Remove(key);
            if (_atlases.Remove(key, out var dead)) dead.Dispose();
        }
        if (_cubeFace.RendererHandle == rendererHandle)
        {
            _cubeFace.Dispose();
            _cubeFaceKey = (-1, -1, -1);
        }
    }

    private SpriteAtlas? Atlas(SpriteSource src, int palette)
    {
        if (_gd == null) return null;
        var key = (src, palette, (nint)_activeRenderer.Handle);
        if (_atlases.TryGetValue(key, out var hit))
        {
            _atlasOrder.Remove(key);
            _atlasOrder.Add(key);
            return hit;
        }

        Sprite?[] sprites;
        try { sprites = SpritesOf(_gd, src); }
        catch { return null; }
        if (sprites.Length == 0) return null;

        var atlas = new SpriteAtlas();
        try { atlas.Build(_activeRenderer, sprites, _gd.Palettes.Get(palette)); }
        catch { atlas.Dispose(); return null; }

        while (_atlasOrder.Count >= AtlasCacheMax)
        {
            var oldest = _atlasOrder[0];
            _atlasOrder.RemoveAt(0);
            if (_atlases.Remove(oldest, out var dead)) _atlasGraveyard.Add(dead);
        }
        _atlases[key] = atlas;
        _atlasOrder.Add(key);
        return atlas;
    }

    /// <summary>
    /// Drop everything the reference browsers derived from the old data folder: the sprite
    /// atlases, and the per-episode work (assemblies, appearance index, level statistics)
    /// that is keyed by episode number and would otherwise survive into a different data set.
    /// </summary>
    private void ResetBrowserCaches()
    {
        foreach (var a in _atlases.Values) a.Dispose();
        _atlases.Clear();
        _atlasOrder.Clear();

        _assemblies = null;
        _assembliesFor = "";
        _appearances = null;
        _appearancesFor = -1;
        _itemSoldAt = null;
        _itemFoundIn = null;
        _episodeOutposts = null;
        _specialDropSites = null;
        _otherPickups = null;
        _statsCache.Clear();
        _peerRows.Clear();
        _peerKey = "";
        _enemySelected = -1;
        _sprSelected = -1;
        _usage = null;              // song/sound cross-references belong to the old data set
    }

    /// <summary>
    /// Lay a bank out as a dense array whose position IS the sprite's own number, so an
    /// atlas index and a game sprite index are the same thing everywhere downstream. The
    /// 1-based stores get a null at slot 0.
    /// </summary>
    internal static Sprite?[] SpritesOf(GameData gd, SpriteSource src)
    {
        switch (src.Store)
        {
            case SpriteStore.Standalone:
                return gd.GetStandalone(src.Index);

            case SpriteStore.Newsh:
            case SpriteStore.NewshFile:
            case SpriteStore.MainSheet:
            {
                var main = src.Xmas ? gd.XmasMain : gd.Main;
                CompShapes? cs = src.Store switch
                {
                    SpriteStore.Newsh => gd.GetNewsh(src.Index),
                    SpriteStore.NewshFile => gd.GetNewshChar(src.FileChar),
                    _ => main != null && src.Index >= 0 && src.Index < main.Sheets.Length
                        ? main.Sheets[src.Index] : null,
                };
                if (cs == null) return Array.Empty<Sprite?>();
                var list = new Sprite?[cs.Count + 1];
                for (int i = 1; i <= cs.Count; i++) list[i] = cs.Decode(i);
                return list;
            }
            case SpriteStore.MainBank:
            {
                var main = src.Xmas ? gd.XmasMain : gd.Main;
                var bank = main != null && src.Index >= 0 && src.Index < main.Banks.Length
                    ? main.Banks[src.Index] : null;
                if (bank == null) return Array.Empty<Sprite?>();
                var list = new Sprite?[bank.Count];
                for (int i = 0; i < bank.Count; i++) list[i] = bank.Get(i);
                return list;
            }
            default:
            {
                var table = gd.GetShapeTable(src.FileChar);
                var list = new Sprite?[ShapeTable.TileCount + 1];
                for (int i = 0; i < ShapeTable.TileCount; i++)
                {
                    var px = table.Tiles[i];
                    if (px == null) continue;
                    list[i + 1] = new Sprite
                    {
                        W = ShapeTable.TileW,
                        H = ShapeTable.TileH,
                        Pixels = px,
                    };
                }
                return list;
            }
        }
    }

    /// <summary>
    /// Where an enemyDat's frames live. Banks 21 and 26 are the two tyrian.shp sheets the
    /// engine hard-codes for coins/gems and powerups; everything else is a newsh bank.
    /// Mirrors GameData.ResolveBankSheet.
    /// </summary>
    private static SpriteSource EnemySpriteSource(int shapeBank) => shapeBank switch
    {
        21 => SpriteSource.MainSheet(10),
        26 => SpriteSource.MainSheet(9),
        _ => SpriteSource.Newsh(shapeBank),
    };

    /// <summary>
    /// One enemy frame at its engine anchor (the enemy's own ex/ey). esize 1 is the four-quad
    /// metasprite -- sprite, +1, +19, +20 at (-6,-7), (+6,-7), (-6,+7), (+6,+7), so the anchor
    /// is the block's CENTRE; esize 0 is a single 12px sprite whose anchor is its TOP-LEFT.
    /// That asymmetry is the engine's (tyrian2.c JE_drawEnemy) and matters as soon as two
    /// parts of a boss are placed against each other.
    /// </summary>
    private static void DrawEnemyFrame(ImDrawListPtr dl, SpriteAtlas atlas, int gr, bool big,
        Vector2 anchor, float scale, uint tint = 0xFFFFFFFF)
    {
        if (gr <= 0 || gr == 999) return;
        if (!big) { atlas.Draw(dl, gr, anchor, scale, tint); return; }

        var tl = anchor - new Vector2(6f * scale, 7f * scale);
        atlas.Draw(dl, gr, tl, scale, tint);
        atlas.Draw(dl, gr + 1, tl + new Vector2(12f * scale, 0), scale, tint);
        atlas.Draw(dl, gr + 19, tl + new Vector2(0, 14f * scale), scale, tint);
        atlas.Draw(dl, gr + 20, tl + new Vector2(12f * scale, 14f * scale), scale, tint);
    }

    /// <summary>
    /// The engine's blit_sprite2x2 (sprite.c:866): four 12x14 sub-sprites at index, +1, +19
    /// and +20, with <paramref name="topLeft"/> the 24x28 block's top-left corner. Use this
    /// where the engine positions by corner (shop icons); <see cref="DrawEnemyFrame"/> is the
    /// enemy form, which positions by the anchor the enemy carries instead.
    /// </summary>
    private static void Draw2x2(ImDrawListPtr dl, SpriteAtlas atlas, int gr, Vector2 topLeft,
        float scale, uint tint = 0xFFFFFFFF)
    {
        if (gr <= 0) return;
        atlas.Draw(dl, gr, topLeft, scale, tint);
        atlas.Draw(dl, gr + 1, topLeft + new Vector2(12f * scale, 0), scale, tint);
        atlas.Draw(dl, gr + 19, topLeft + new Vector2(0, 14f * scale), scale, tint);
        atlas.Draw(dl, gr + 20, topLeft + new Vector2(12f * scale, 14f * scale), scale, tint);
    }

    /// <summary>The frame's footprint relative to its anchor, in game pixels.</summary>
    private static (float X, float Y, float W, float H) EnemyFrameBox(SpriteAtlas atlas, int gr, bool big)
    {
        if (big) return (-6f, -7f, 24f, 28f);
        var (w, h) = atlas.SizeOf(gr);
        return (0f, 0f, w == 0 ? 12f : w, h == 0 ? 14f : h);
    }

    /// <summary>Draw a frame centred in a box -- list thumbnails and the large preview.</summary>
    private static void DrawEnemyFrameCentered(ImDrawListPtr dl, SpriteAtlas atlas, int gr, bool big,
        Vector2 boxMin, Vector2 boxMax, float scale, uint tint = 0xFFFFFFFF)
    {
        if (gr <= 0 || gr == 999) return;
        var (ox, oy, w, h) = EnemyFrameBox(atlas, gr, big);
        var anchor = new Vector2(
            MathF.Round((boxMin.X + boxMax.X) * 0.5f - (ox + w * 0.5f) * scale),
            MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - (oy + h * 0.5f) * scale));
        DrawEnemyFrame(dl, atlas, gr, big, anchor, scale, tint);
    }

    /// <summary>One piece of a thumbnail: a frame, and where it sits relative to the group's
    /// anchor in game pixels.</summary>
    private readonly record struct ThumbPart(int Bank, int Sprite, bool Big, float X, float Y);

    /// <summary>
    /// An enemy as it is really drawn, fitted into a row-sized box: a single 12px frame, the 24x28
    /// metasprite, or the 48x56 object an event 12 tiles from four consecutive entries. A fixed
    /// scale would either clip the big ones or leave the small ones lost in the box, so the group's
    /// own footprint sets the scale -- never above <paramref name="maxScale"/>, so a small enemy is
    /// still drawn at its true size rather than blown up.
    /// </summary>
    /// <param name="blockBase">Base id of an event-12 block; 0 for an ordinary spawn.</param>
    /// <param name="customSprite">With <paramref name="enemyId"/> 0: the art an event 49-52 carries
    /// itself, which has no table entry to read.</param>
    private void DrawEnemyThumb(ImDrawListPtr dl, int episodeIdx, int enemyId,
        Vector2 boxMin, Vector2 boxMax, int blockBase = 0,
        int customSprite = 0, int customBank = 0, float maxScale = 1f)
    {
        var parts = new List<ThumbPart>();
        EnemyData? ed = null;
        try { ed = _gd?.GetEnemyData(_gd.Episodes[episodeIdx]); } catch { /* no table for it */ }

        void AddEntry(int id, float x, float y)
        {
            var d = ed?.Get(id) ?? default;
            if (d.Loaded && d.EGraphic is { Length: > 0 })
                parts.Add(new ThumbPart(d.ShapeBank, d.EGraphic[0], d.Esize == 1, x, y));
        }

        if (enemyId == 0 && customSprite > 0)
        {
            // Events 49-52 spawn scratch entry 0, so its esize -- not the event's -- sets the form.
            parts.Add(new ThumbPart(customBank, customSprite, (ed?.Get(0).Esize ?? 0) == 1, 0, 0));
        }
        else if (blockBase > 0)
        {
            for (int k = 0; k < 4; k++) AddEntry(blockBase + k, (k % 2) * 24f, -(k / 2) * 28f);
        }
        if (parts.Count == 0) AddEntry(enemyId, 0, 0);
        if (parts.Count == 0) return;

        // Union of every piece's own box, so the group is measured as the player sees it.
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        var atlases = new SpriteAtlas?[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            atlases[i] = Atlas(EnemySpriteSource(parts[i].Bank), AppSettings.GamePalette);
            if (atlases[i] is not { } a) continue;
            var (ox, oy, w, h) = EnemyFrameBox(a, parts[i].Sprite, parts[i].Big);
            x0 = Math.Min(x0, parts[i].X + ox); y0 = Math.Min(y0, parts[i].Y + oy);
            x1 = Math.Max(x1, parts[i].X + ox + w); y1 = Math.Max(y1, parts[i].Y + oy + h);
        }
        if (x1 <= x0 || y1 <= y0) return;

        float bw = x1 - x0, bh = y1 - y0;
        float scale = Math.Min(maxScale,
            Math.Min((boxMax.X - boxMin.X) / bw, (boxMax.Y - boxMin.Y) / bh));
        var origin = new Vector2(
            MathF.Round((boxMin.X + boxMax.X) * 0.5f - (x0 + bw * 0.5f) * scale),
            MathF.Round((boxMin.Y + boxMax.Y) * 0.5f - (y0 + bh * 0.5f) * scale));

        for (int i = 0; i < parts.Count; i++)
            if (atlases[i] is { } a)
                DrawEnemyFrame(dl, a, parts[i].Sprite, parts[i].Big,
                    origin + new Vector2(parts[i].X, parts[i].Y) * scale, scale);
    }

    /// <summary>
    /// A filter box over a fixed byte buffer. The ImGui binding takes raw pointers, so the
    /// browsers share one wrapper rather than repeating the fixed blocks.
    /// </summary>
    private bool FilterBox(string id, string hint, byte[] buf, float width)
    {
        Span<byte> idb = stackalloc byte[64];
        // Clipped rather than sized exactly: GetBytes throws when the destination is short, so a
        // hint that outgrew its buffer took the whole frame down with it instead of losing a
        // word off the end of a line ImGui was going to clip anyway.
        Span<byte> hb = stackalloc byte[160];
        int n = System.Text.Encoding.ASCII.GetBytes(id, idb); idb[n] = 0;
        int m = System.Text.Encoding.ASCII.GetBytes(
            hint.Length > hb.Length - 1 ? hint[..(hb.Length - 1)] : hint, hb); hb[m] = 0;
        ImGui.SetNextItemWidth(width);
        fixed (byte* ip = idb)
        fixed (byte* hp = hb)
        fixed (byte* p = buf)
            return ImGui.InputTextWithHint(ip, hp, p, (nuint)buf.Length);
    }

    /// <summary>
    /// A tab that can also be selected programmatically. Only the raw-pointer overload takes
    /// flags, and handing it a p_open would put a close button on every tab, so the label is
    /// marshalled here and p_open stays null.
    /// </summary>
    private bool TabItem(string label, bool select)
    {
        Span<byte> buf = stackalloc byte[64];
        int n = System.Text.Encoding.UTF8.GetBytes(label, buf);
        buf[n] = 0;
        fixed (byte* p = buf)
            return ImGui.BeginTabItem(p, (bool*)null,
                select ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
    }

    /// <summary>The text a <see cref="FilterBox"/> buffer currently holds.</summary>
    private static string BufText(byte[] buf)
    {
        int n = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
    }

    /// <summary>Case-insensitive "does this row survive the filter box".</summary>
    private static bool Matches(string filter, params string?[] fields)
    {
        if (filter.Length == 0) return true;
        foreach (var f in fields)
            if (f != null && f.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private List<SpriteSource> AllSpriteSources() => SpriteSourcesFor(_gd);

    /// <summary>
    /// Every bank the browser can show, in the order it lists them -- which is every shape file
    /// the data folder holds, and nothing it does not. The optional groups (Christmas, the
    /// stand-alone files) are offered only when their files are actually there.
    ///
    /// Static and GameData-driven so the "--checksprites" self-check can enumerate the same list
    /// without a window or a renderer.
    /// </summary>
    internal static List<SpriteSource> SpriteSourcesFor(GameData? gd)
    {
        var list = new List<SpriteSource>();
        // Banks 21 and 26 are left out deliberately. Neither ever loads a newsh file -- the
        // engine answers both from tyrian.shp, which the sheet rows below say -- and 21's
        // shapeFile[] entry names newshq.shp, a file no release ships. Listing them by that
        // table put a row in the browser that could never be read.
        for (int i = 1; i <= GameData.ShapeBankCount; i++)
            if (!GameData.IsHardCodedBank(i)) list.Add(SpriteSource.Newsh(i));
        // The rest of the newsh sheets on disk: the ones the engine loads by name. Driven by the
        // folder, so a data set with more or fewer of them lists exactly what it has.
        if (gd != null)
            foreach (char c in gd.NewshCharsPresent)
                if (GameData.ShapeBanksFor(c).Count == 0) list.Add(SpriteSource.NewshFile(c));
        for (int i = 7; i <= 12; i++) list.Add(SpriteSource.MainSheet(i));
        for (int i = 0; i <= 6; i++) list.Add(SpriteSource.MainBank(i));
        if (gd?.XmasMain != null)
        {
            for (int i = 7; i <= 12; i++) list.Add(SpriteSource.MainSheet(i, xmas: true));
            for (int i = 0; i <= 6; i++) list.Add(SpriteSource.MainBank(i, xmas: true));
        }
        if (gd != null)
            for (int i = 0; i < GameData.StandaloneShapes.Length; i++)
                if (gd.HasStandalone(i)) list.Add(SpriteSource.Standalone(i));
        foreach (char c in GameData.TileSetChars) list.Add(SpriteSource.Tiles(c));
        return list;
    }
}
