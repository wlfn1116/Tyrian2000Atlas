namespace T2A.Tyrian;

public sealed class LevelListItem
{
    public int FileNum;
    public string Name = "";
    public bool BonusLevel;
    public bool GalagaMode;      // the script's ]g: Galaga-style mini-game rules apply
    /// <summary>Every way in is a warp ball — the level is off the campaign's normal route.
    /// Filled in once the episode's flow graph is built (GameData.GetGraph).</summary>
    public bool SecretLevel;
    /// <summary>One of the arenas Timed Battle offers off the title screen. Not exclusive with
    /// the campaign: Episode 1's #5 is arena 1 <em>and</em> the campaign's DELIANI. Filled in
    /// with <see cref="SecretLevel"/>.</summary>
    public bool TimedBattle;
    /// <summary>"Hard+" / "below Hard" when the difficulty you started on decides whether
    /// you ever see this level; empty when any difficulty reaches it.</summary>
    public string DifficultyGate = "";

    public string Display =>
        $"{FileNum:00}  {(string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name.Trim())}"
        + (TimedBattle ? "  [timed battle]" : "")
        + (SecretLevel ? "  [secret]" : "")
        + (DifficultyGate.Length > 0 ? $"  [{DifficultyGate.ToLowerInvariant()}]" : "")
        + (BonusLevel ? "  [bonus]" : "");
}

public sealed class EpisodeInfo
{
    public int Number;                 // 1..5
    public LevelContainer Container = null!;
    public List<LevelEntry> ScriptLevels = new();
    public List<LevelListItem> Levels = new();
    public EpisodeScriptFile? Script;  // the whole levels%d.dat, for the level-flow graph
}

/// <summary>
/// Root of the loaded Tyrian 2000 data set: locates the data directory,
/// loads the palette, scans the 5 episodes, and caches shape tables.
/// </summary>
public sealed class GameData
{
    public string DataDir { get; }
    public PaletteSet Palettes { get; }
    public readonly List<EpisodeInfo> Episodes = new();

    private readonly Dictionary<char, ShapeTable> _shapeCache = new();
    private readonly Dictionary<char, CompShapes?> _newshCache = new();
    private readonly Dictionary<int, EnemyData> _enemyCache = new();
    private readonly Dictionary<int, WeaponData> _weaponCache = new();
    private readonly Dictionary<int, ItemData> _itemCache = new();
    private readonly Dictionary<int, EpisodeGraph> _graphCache = new();
    private readonly Dictionary<int, List<DataCube>> _cubeCache = new();
    private readonly Dictionary<int, Sprite?[]> _standaloneCache = new();
    private List<string>? _planetNames;
    private string? _newshPresent;
    private bool[]? _standalonePresent;
    private MainShapes? _main;
    private MainShapes? _xmas;
    private bool _xmasTried;

    // shapeFile[] from lvlmast.c: enemy shape-bank (1-based) -> newsh file char.
    private static readonly char[] ShapeFile =
    {
        '2','4','7','8','A','B','C','D','E','F','G','H','I','J','K','L','M','N',
        'O','P','Q','R','S','T','U','5','#','V','0','@','3','^','5','9','\'','%'
    };

    /// <summary>How many enemy shape banks the engine knows (shapeFile[]'s length).</summary>
    public static int ShapeBankCount => ShapeFile.Length;

    /// <summary>The newsh file character a 1-based enemy shape bank loads from.</summary>
    public static char ShapeBankChar(int bank)
        => bank >= 1 && bank <= ShapeFile.Length ? ShapeFile[bank - 1] : '?';

    /// <summary>
    /// The two shape banks that never reach a newsh file at all: JE_makeEnemy (tyrian2.c:6325)
    /// answers 21 with tyrian.shp's coins/datacubes sheet and 26 with its powerups sheet before
    /// it so much as looks at the four banks event 5 loaded. shapeFile[] still carries an entry
    /// for each, and both are dead letters -- 21's names newshq.shp, which no release ships (so
    /// a level that did load bank 21 would take the real game down in dir_fopen_die), and 26's
    /// names newsh5.shp, which bank 33 is the real route to. No level loads either.
    /// </summary>
    public static bool IsHardCodedBank(int bank) => bank is 21 or 26;

    /// <summary>The tyrian.shp sub-table a hard-coded bank draws from, or -1 for a normal bank.</summary>
    public static int HardCodedBankSheet(int bank) => bank switch { 21 => 10, 26 => 9, _ => -1 };

    /// <summary>
    /// Every newsh%c.shp the game can hold, in the order its own sprite viewer walks them
    /// (mainint.c JE_spriteViewer). Past the shape banks these are loaded by name, one job each:
    /// '1' the shop, HUD and mouse pointer, '6' explosions, '~' the Destruct minigame, '$' the
    /// ship editor's extra ships. '(' is a loose copy of tyrian.shp's player-shot sheet that
    /// nothing loads.
    /// </summary>
    public const string NewshChars = "0123456789abcdefghijklmnopqrstuvwxyz#$%'(@^~";

    /// <summary>
    /// The 1-based shape banks whose shapeFile[] entry is this file, skipping the two the engine
    /// hard-codes away. Empty for a sheet that is only ever loaded by name.
    /// </summary>
    public static List<int> ShapeBanksFor(char fileChar)
    {
        char c = char.ToLowerInvariant(fileChar);
        var banks = new List<int>();
        for (int b = 1; b <= ShapeFile.Length; b++)
            if (!IsHardCodedBank(b) && char.ToLowerInvariant(ShapeFile[b - 1]) == c) banks.Add(b);
        return banks;
    }

    /// <summary>The terrain tile sets the levels draw from (shapes%c.dat).</summary>
    public static readonly char[] TileSetChars = { 'w', 'x', 'y', 'z', ')' };

    /// <summary>
    /// The shape files that live outside tyrian.shp, in the order the game's own viewer lists
    /// them. estsc/estpa are plain Sprite_array files; the ship-editor pair needs
    /// <see cref="ShipEditorCells"/> instead, which is why the format is carried here.
    ///
    /// estpa.shp is named from its contents, not from the sources: mainint.c calls it an unused
    /// ending file and the data dump's index repeats that, but 151 of its 152 sprites are pixel
    /// -identical to tyrian.shp's planets bank and the odd one out is an earlier, smaller "2000"
    /// logo -- so it is that bank's older twin, not ending art.
    /// </summary>
    public static readonly (string File, string Name, string Role, bool Cells)[] StandaloneShapes =
    {
        ("estsc.shp", "Ending & credits", "JE_playCredits", false),
        ("estpa.shp", "Planets & title logos", "older twin of tyrian.shp #3", false),
        ("user1.shp", "DOS ship editor 1", "12x14 cells, not read at runtime", true),
        ("user2.shp", "DOS ship editor 2", "12x14 cells, not read at runtime", true),
    };

    public GameData(string dataDir)
    {
        DataDir = dataDir;
        Palettes = PaletteSet.Load(Path.Combine(dataDir, "palette.dat"));

        for (int ep = 1; ep <= 5; ep++)
        {
            string lvlPath = Path.Combine(dataDir, $"tyrian{ep}.lvl");
            if (!File.Exists(lvlPath)) continue;

            var info = new EpisodeInfo { Number = ep, Container = new LevelContainer(lvlPath) };

            string scriptPath = Path.Combine(dataDir, $"levels{ep}.dat");
            if (File.Exists(scriptPath))
            {
                try
                {
                    info.ScriptLevels = EpisodeScript.ParseLevels(scriptPath);
                    info.Script = EpisodeScriptFile.Load(scriptPath);
                }
                catch { /* tolerate a malformed script */ }
            }

            // Map lvlFileNum -> first name/bonus seen in the script.
            var nameByFile = new Dictionary<int, LevelEntry>();
            foreach (var e in info.ScriptLevels)
                if (!nameByFile.ContainsKey(e.LvlFileNum))
                    nameByFile[e.LvlFileNum] = e;

            int sections = info.Container.SectionCount;
            for (int f = 1; f <= sections; f++)
            {
                var item = new LevelListItem { FileNum = f };
                if (nameByFile.TryGetValue(f, out var e))
                {
                    item.Name = e.Name;
                    item.BonusLevel = e.BonusLevel || e.NormalBonus;
                    item.GalagaMode = e.GalagaMode;
                }
                info.Levels.Add(item);
            }

            Episodes.Add(info);
        }

        // Resolve the flow graphs now (~30ms for all five): they are what marks the secret
        // levels in the level list, so leaving it lazy would make Display depend on whether
        // anything had opened the tree yet.
        foreach (var ep in Episodes)
        {
            try { GetGraph(ep); }
            catch { /* an episode whose data won't resolve simply gets no secret marks */ }
        }
    }

    public Level LoadLevel(EpisodeInfo ep, int fileNum) => Level.Parse(ep.Container, fileNum);

    public MainShapes Main => _main ??= MainShapes.Load(Path.Combine(DataDir, "tyrian.shp"));

    private PicFile? _pics;
    private bool _picsTried;

    /// <summary>tyrian.pic, the ]P/]U backdrops; null when the file is absent or unreadable.</summary>
    public PicFile? Pics
    {
        get
        {
            if (_picsTried) return _pics;
            _picsTried = true;
            try { _pics = PicFile.Load(Path.Combine(DataDir, "tyrian.pic")); }
            catch { _pics = null; }
            return _pics;
        }
    }

    /// <summary>
    /// The Christmas shape file. Xmas mode is a wholesale swap of tyrian.shp for tyrianc.shp
    /// (opentyr.c:281) — same 13 sub-tables, different art — so it is the same structure read
    /// from a different file rather than a set of extra sprites. Null if the file is absent.
    /// </summary>
    public MainShapes? XmasMain
    {
        get
        {
            if (_xmasTried) return _xmas;
            _xmasTried = true;
            string path = Path.Combine(DataDir, "tyrianc.shp");
            try { _xmas = File.Exists(path) ? MainShapes.Load(path) : null; }
            catch { _xmas = null; }
            return _xmas;
        }
    }

    public EnemyData GetEnemyData(EpisodeInfo ep)
    {
        if (_enemyCache.TryGetValue(ep.Number, out var ed)) return ed;
        ed = EnemyData.Load(DataDir, ep);
        _enemyCache[ep.Number] = ed;
        return ed;
    }

    /// <summary>newsh file for a 1-based enemy shape bank (1..36), cached.</summary>
    public CompShapes? GetNewsh(int bank)
    {
        if (bank < 1 || bank > ShapeFile.Length) return null;
        return GetNewshChar(ShapeFile[bank - 1]);
    }

    /// <summary>newsh file by its literal file character (the engine's JE_loadCompShapes).</summary>
    public CompShapes? GetNewshChar(char fileChar)
    {
        char c = char.ToLowerInvariant(fileChar);
        if (_newshCache.TryGetValue(c, out var cs)) return cs;
        string path = Path.Combine(DataDir, $"newsh{c}.shp");
        cs = File.Exists(path) ? CompShapes.LoadFile(path) : null;
        _newshCache[c] = cs;
        return cs;
    }

    /// <summary>
    /// Which of <see cref="NewshChars"/> this folder actually holds, in that order. Asked of the
    /// folder rather than of shapeFile[], which names one file (newshq.shp) that no release has.
    /// </summary>
    public string NewshCharsPresent => _newshPresent ??= string.Concat(
        NewshChars.Where(c => File.Exists(Path.Combine(DataDir, $"newsh{c}.shp"))));

    /// <summary>Whether this folder holds one of <see cref="StandaloneShapes"/>.</summary>
    public bool HasStandalone(int i)
    {
        _standalonePresent ??= Array.ConvertAll(StandaloneShapes,
            s => File.Exists(Path.Combine(DataDir, s.File)));
        return i >= 0 && i < _standalonePresent.Length && _standalonePresent[i];
    }

    /// <summary>One of <see cref="StandaloneShapes"/>, decoded and cached; empty when the file is
    /// absent or will not parse.</summary>
    public Sprite?[] GetStandalone(int i)
    {
        if (i < 0 || i >= StandaloneShapes.Length) return Array.Empty<Sprite?>();
        if (_standaloneCache.TryGetValue(i, out var hit)) return hit;
        var (file, _, _, cells) = StandaloneShapes[i];
        Sprite?[] list = Array.Empty<Sprite?>();
        try
        {
            string path = Path.Combine(DataDir, file);
            if (File.Exists(path))
            {
                byte[] d = File.ReadAllBytes(path);
                list = cells ? ShipEditorCells.Parse(d) : SpriteBank.Parse(d, 0).ToArray();
            }
        }
        catch { list = Array.Empty<Sprite?>(); }
        _standaloneCache[i] = list;
        return list;
    }

    /// <summary>Galaxy-map planet names (1-based), shared by every episode.</summary>
    public List<string> PlanetNameList => _planetNames ??= PlanetNames.Load(DataDir);

    /// <summary>The episode's datacube readings, cached. Empty if cubetxt%d.dat is missing.</summary>
    public List<DataCube> GetCubes(EpisodeInfo ep)
    {
        if (_cubeCache.TryGetValue(ep.Number, out var c)) return c;
        string path = Path.Combine(DataDir, $"cubetxt{ep.Number}.dat");
        try { c = File.Exists(path) ? DataCubes.Load(path) : new List<DataCube>(); }
        catch { c = new List<DataCube>(); }
        _cubeCache[ep.Number] = c;
        return c;
    }

    /// <summary>
    /// The episode's level-flow graph, cached. Building it parses every level in the episode
    /// once, to find the secret-warp pickups that the script itself never mentions.
    /// </summary>
    public EpisodeGraph? GetGraph(EpisodeInfo ep)
    {
        if (_graphCache.TryGetValue(ep.Number, out var g)) return g;
        if (ep.Script == null) return null;

        var secrets = new Dictionary<int, List<int>>();
        var ed = GetEnemyData(ep);
        foreach (var item in ep.Levels)
        {
            try { secrets[item.FileNum] = EpisodeGraph.FindSecretTargets(LoadLevel(ep, item.FileNum), ed); }
            catch { /* a level that won't parse simply contributes no secret exits */ }
        }

        g = EpisodeGraph.Build(ep.Script, PlanetNameList,
            fileNum => secrets.TryGetValue(fileNum, out var t) ? t : Enumerable.Empty<int>());
        _graphCache[ep.Number] = g;

        foreach (var item in ep.Levels)
        {
            item.SecretLevel = g.IsSecretOnly(item.FileNum);
            item.TimedBattle = g.IsTimedBattleArena(item.FileNum);
            item.DifficultyGate = g.DifficultyGate(item.FileNum);
        }
        return g;
    }

    /// <summary>The episode's shop tables (ships, ports, sidekicks, shields, generators,
    /// specials), cached. Never null; check <see cref="ItemData.Loaded"/>.</summary>
    public ItemData GetItems(EpisodeInfo ep, bool fork = true)
    {
        // Keyed on the fork flag too: the two are different tables, and the browser flips
        // between them, so one cache slot per episode would keep handing back the wrong shop.
        int key = ep.Number * 2 + (fork ? 1 : 0);
        if (_itemCache.TryGetValue(key, out var it)) return it;
        try { it = ItemData.Load(DataDir, ep, fork); }
        catch { it = new ItemData(); }
        _itemCache[key] = it;
        return it;
    }

    public WeaponData GetWeapons(EpisodeInfo ep)
    {
        if (_weaponCache.TryGetValue(ep.Number, out var wd)) return wd;
        wd = WeaponData.Load(DataDir, ep);
        _weaponCache[ep.Number] = wd;
        return wd;
    }

    /// <summary>Resolve a shape bank to a sprite sheet given the 4 currently active event-5 banks.</summary>
    public CompShapes? ResolveBankSheet(int shapeBank, int[] activeBanks)
    {
        if (shapeBank == 21) return Main.CoinsGems;
        if (shapeBank == 26) return Main.PowerUps;
        for (int i = 0; i < activeBanks.Length; i++)
            if (activeBanks[i] == shapeBank)
                return GetNewsh(shapeBank);
        // Fall back: try loading directly (some levels reference a bank without a tracked slot).
        return GetNewsh(shapeBank);
    }

    public ShapeTable GetShapeTable(char shapeChar)
    {
        char key = char.ToLowerInvariant(shapeChar);
        if (_shapeCache.TryGetValue(key, out var t)) return t;
        string path = Path.Combine(DataDir, $"shapes{key}.dat");
        var table = ShapeTable.Load(path, key);
        _shapeCache[key] = table;
        return table;
    }

    /// <summary>Find a Tyrian folder from a user selection or the current application path.</summary>
    public static string? FindDataDir(string? startHint = null)
    {
        var candidates = new List<string>();
        void AddProbe(string? baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return;
            var d = new DirectoryInfo(baseDir);
            for (int up = 0; up < 8 && d != null; up++, d = d.Parent!)
                candidates.Add(d.FullName);
        }

        if (!string.IsNullOrEmpty(startHint) && Directory.Exists(startHint))
        {
            candidates.Add(startHint);
            try { candidates.AddRange(Directory.EnumerateDirectories(startHint)); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        AddProbe(Environment.CurrentDirectory);
        AddProbe(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "tyrian2000data"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "data"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tyrian2000data"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));

        foreach (var c in candidates)
            if (File.Exists(Path.Combine(c, "tyrian1.lvl")) && File.Exists(Path.Combine(c, "palette.dat")))
                return c;
        return null;
    }
}
