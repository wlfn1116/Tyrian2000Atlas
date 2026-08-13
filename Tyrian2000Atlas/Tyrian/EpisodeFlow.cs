namespace T2A.Tyrian;

/// <summary>
/// One full-screen text/cutscene screen: the ]M / ]P / ]W command run the engine plays it
/// with, plus the text lines. Used for a stop's arrival story, its pre-level warning and
/// the episode ending. Engine limits (fonthand.c:55-57): 12 lines of 60 characters —
/// levelWarningText[12][61] is written with plain strcpy, so exceeding either corrupts
/// memory in the real game.
/// </summary>
public sealed class StoryScreen
{
    public const int MaxLines = 12;
    public const int MaxLineLen = 60;

    /// <summary>What happens before the screen: 0 nothing, 1 fade to black (]B),
    /// 2 white flash (]F), 3 fade + dark palette (]C).</summary>
    public int Fade;
    /// <summary>Song to start (]M), 1..41; 0 = keep whatever is playing.</summary>
    public int Music;
    /// <summary>Backdrop (]P/]U/]V/]R): -1 = keep the current screen, 0 = the ship-editor
    /// PCX, 1..14 = tyrian.pic pictures, 901..914 = clear to that palette.</summary>
    public int Picture = -1;
    /// <summary>How a 1..14 picture arrives: 0 = fade in (]P), 1 = wipe up (]U),
    /// 2 = wipe down (]V), 3 = wipe right (]R).</summary>
    public int Wipe;
    /// <summary>]Wy vs ]Wn: the flashing WARNING bars and siren.</summary>
    public bool WarningFrame;
    /// <summary>Tens digit of the ]W number: red-alert mode (palette 7, text at the top).</summary>
    public int Red;
    /// <summary>Ones digit: the per-character glow delay; 0 types instantly.</summary>
    public int Speed = 3;
    public List<string> Lines = new();

    public StoryScreen Clone() => new()
    {
        Fade = Fade, Music = Music, Picture = Picture, Wipe = Wipe,
        WarningFrame = WarningFrame, Red = Red, Speed = Speed, Lines = Lines.ToList(),
    };

    /// <summary>One line, clipped to what the engine's buffers can hold.</summary>
    public static string ClipLine(string s) => s.Length > MaxLineLen ? s[..MaxLineLen] : s;

    /// <summary>Normalize to the engine's hard limits.</summary>
    public static List<string> ClipLines(IEnumerable<string> lines) =>
        lines.Select(ClipLine).Take(MaxLines).ToList();
}

/// <summary>A Timed Battle arena: one of the five title-screen picks (]T).</summary>
public sealed class BattleArena
{
    public int LevelFile = 1;
    public string Name = "ARENA";
    public int Song = 1;

    public BattleArena Clone() => (BattleArena)MemberwiseClone();
}

/// <summary>
/// The end of the episode: optional ending animation, the endscreens, and the ]Q close —
/// score plus one of nine secret-hint blocks, then the engine hands over to the next
/// episode on its own (JE_nextEpisode).
/// </summary>
public sealed class EpisodeEnding
{
    public const int HintCount = 9;
    /// <summary>Hint blocks may add at most this many lines to the score header ]Q writes
    /// first (2 lines, 3 in two-player) without overflowing levelWarningText[12].</summary>
    public const int MaxHintLines = 9;

    /// <summary>Play tyrend.anm (]A) before the endscreens.</summary>
    public bool Anim;
    /// <summary>Song started before the animation; 0 = none.</summary>
    public int AnimMusic;
    public List<StoryScreen> Screens = new();
    /// <summary>Music/backdrop for the score + hint screen; 0 music = keep, -1 pic = keep.</summary>
    public int HintMusic = 31;
    public int HintPic = 5;
    /// <summary>The nine hint blocks ]Q picks from (3 groups of 3; each profile is locked to
    /// one group). Empty block = the screen shows just the score.</summary>
    public List<List<string>> Hints = NewHints();

    public static List<List<string>> NewHints()
    {
        var h = new List<List<string>>();
        for (int i = 0; i < HintCount; i++) h.Add(new List<string>());
        return h;
    }

    public EpisodeEnding Clone()
    {
        var c = new EpisodeEnding
        {
            Anim = Anim, AnimMusic = AnimMusic, HintMusic = HintMusic, HintPic = HintPic,
            Screens = Screens.Select(s => s.Clone()).ToList(),
            Hints = Hints.Select(h => h.ToList()).ToList(),
        };
        while (c.Hints.Count < HintCount) c.Hints.Add(new List<string>());
        return c;
    }
}

/// <summary>One stop on the episode's route: a level, and everything wrapped around it.</summary>
public sealed class FlowStop
{
    public int LevelFile = 1;               // 1-based section of tyrian{N}.lvl
    public string Name = "NEW LEVEL";       // 9 chars max, the ]L name
    public int Song = 1;                    // 1..41
    public bool Bonus, NormalBonus;         // the ]L '$' flags
    public bool Galaga, Engage, Extra;      // ]g / ]e / ]x modes for this level
    public bool SavePoint;                  // ]s on the way in
    public bool SaveBackup;                 // ]b - write the LAST LEVEL backup save
    /// <summary>Story screens shown on arrival, before the outpost (or the level when
    /// there is none): the episode intro on stop 1, interludes later.</summary>
    public List<StoryScreen> Story = new();
    /// <summary>Warning text shown right before the level (]W block); empty = none.</summary>
    public List<string> Warning = new();
    public bool WarnFrame = true;           // ]Wy vs ]Wn
    public int WarnRed;                     // tens digit of the ]W number
    public int WarnSpeed = 1;               // ones digit (stock warnings use 1)
    /// <summary>An outpost (shop + datacubes + galaxy map hop) before the level.</summary>
    public bool Outpost;
    public int OutpostSong = 2;             // ]i
    public int MapPlanet = 1;               // the galaxy-map planet the hop shows
    public List<int> Cubes = new();         // ]? cubetxt indices
    public int CubesFree = 1;               // ]! — readable without pickups
    /// <summary>The shop's nine availability rows (]I), exactly as the engine reads them.</summary>
    public List<int>[] Shop = NewShop();

    public static List<int>[] NewShop()
    {
        var rows = new List<int>[EpisodeFlow.ShopRowCount];
        for (int i = 0; i < rows.Length; i++) rows[i] = new List<int>();
        return rows;
    }

    public FlowStop Clone()
    {
        var c = (FlowStop)MemberwiseClone();
        c.Story = Story.Select(s => s.Clone()).ToList();
        c.Warning = Warning.ToList();
        c.Cubes = Cubes.ToList();
        c.Shop = NewShop();
        for (int i = 0; i < Shop.Length; i++) c.Shop[i] = Shop[i].ToList();
        return c;
    }
}

/// <summary>
/// The episode's route as a plain ordered list of stops plus its ending, and the machinery
/// that turns it into a correct levels{N}.dat script — sections, ]L chains, outposts with
/// their ]G/]I/]?/]! blocks, story screens, ]W warnings, the ]Q ending with its nine hint
/// blocks and the optional ]T Timed Battle arenas — without a single hand-positioned
/// character. Scripts it generated carry a marker and re-import losslessly; foreign scripts
/// import best-effort along their main route.
/// </summary>
public sealed class EpisodeFlow
{
    public const string Marker = "T2A FLOW";
    public const int ShopRowCount = 9;
    /// <summary>itemAvail[9][10]: an 11th id on a row overruns the next row's memory.</summary>
    public const int ShopRowMax = 10;
    /// <summary>mapPlanet[5]/mapSection[5]: a ]G carries at most five destinations.</summary>
    public const int MaxMapDest = 5;
    public const int MaxArenas = 5;

    /// <summary>The eight-character row labels the engine skips over in a ]I block, and the
    /// order the rows are read in (tyrian2.c:4913).</summary>
    public static readonly string[] ShopRowLabels =
        { "Ship", "WeapF", "WeapR", "Power", "Engine", "Opt1", "Opt2", "Armor", "Shield" };

    public readonly List<FlowStop> Stops = new();
    public EpisodeEnding Ending = new();
    /// <summary>The title screen's Timed Battle picks; empty = the mode has no arenas here.</summary>
    public readonly List<BattleArena> Arenas = new();
    /// <summary>True when the current script carries the marker: edits regenerate it live.</summary>
    public bool OwnsScript;

    // =====================================================================
    // Generation
    // =====================================================================

    /// <summary>
    /// Compose the whole script. Stop k's ]L chains to stop k+1's entry section (its
    /// arrival section when it has story screens or an outpost), and the last stop ends
    /// the episode via the ending section's ]Q. Timed Battle arenas hang off section 1's
    /// ]T and close with ]q.
    /// </summary>
    public List<string> Generate()
    {
        // Sections are numbered before anything is written: 1 is the bootstrap jump,
        // then each stop takes one or two, then the ending, then the arenas.
        var entry = new int[Stops.Count];      // the section a predecessor's ]L points at
        var levelSec = new int[Stops.Count];   // the section holding the stop's ]L
        int next = 2;
        for (int i = 0; i < Stops.Count; i++)
        {
            entry[i] = next;
            if (HasArrival(Stops[i])) next++;
            levelSec[i] = next++;
        }
        int endSec = next++;
        var arenaSec = new int[Arenas.Count];
        for (int i = 0; i < Arenas.Count; i++) arenaSec[i] = next++;
        int battleOverSec = Arenas.Count > 0 ? next : 0;

        var lines = new List<string>
        {
            $"*1 {Marker} - built by the Atlas editor's Flow tab; regenerate there rather than hand-editing",
        };
        if (Arenas.Count > 0)
        {
            // Five 3-wide fields; missing arenas repeat the last one, like stock data does.
            string t = "]T[";
            for (int k = 0; k < MaxArenas; k++)
                t += " " + D2(arenaSec[Math.Min(k, Arenas.Count - 1)]);
            lines.Add(t);
        }
        lines.Add($"]J {D3(2)}[");
        lines.Add("");

        for (int i = 0; i < Stops.Count; i++)
        {
            var s = Stops[i];
            if (HasArrival(s))
            {
                lines.Add($"*{entry[i]} ARRIVAL {i + 1} - {SafeName(s.Name)}");
                foreach (var screen in s.Story) WriteScreen(lines, screen);
                if (s.Outpost)
                {
                    if (s.OutpostSong > 0) lines.Add($"]i {D3(s.OutpostSong)}[");
                    if (s.Cubes.Count > 0)
                    {
                        string cubes = $"]?[ {D2(Math.Min(s.Cubes.Count, 4))}";
                        foreach (int c in s.Cubes.Take(4)) cubes += $" {D3(c)}";
                        lines.Add(cubes);
                        lines.Add($"]![ {D2(Math.Clamp(s.CubesFree, 0, 4))}");
                    }
                    lines.Add($"]G[ {D2(s.MapPlanet)} 1 {D2(s.MapPlanet)} {D3(levelSec[i])}");
                    lines.Add("]I[");
                    for (int r = 0; r < ShopRowCount; r++)
                    {
                        string row = " " + ShopRowLabels[r].PadRight(7);
                        foreach (int id in s.Shop[r].Take(ShopRowMax)) row += $" {id}";
                        lines.Add(row);
                    }
                }
                lines.Add("");
            }

            lines.Add($"*{levelSec[i]} STOP {i + 1} - {SafeName(s.Name)}");
            if (s.SavePoint) lines.Add("]s[");
            if (s.SaveBackup) lines.Add("]b[");
            if (s.Galaga) lines.Add("]g[");
            if (s.Engage) lines.Add("]e[");
            if (s.Extra) lines.Add("]x[");
            if (s.Warning.Count > 0)
            {
                lines.Add($"]W{(s.WarnFrame ? 'y' : 'n')} {WarnDigits(s.WarnRed, s.WarnSpeed)}[");
                foreach (string w in StoryScreen.ClipLines(s.Warning)) lines.Add(w);
                lines.Add("#");
            }
            int nextSec = i + 1 < Stops.Count ? entry[i + 1] : endSec;
            lines.Add(BuildLevelLine(nextSec, s.Name, s.Song, s.LevelFile, s.NormalBonus, s.Bonus));
            lines.Add("");
        }

        lines.Add($"*{endSec} EPISODE COMPLETE");
        if (Ending.Anim)
        {
            if (Ending.AnimMusic > 0) lines.Add($"]M {D3(Ending.AnimMusic)}[");
            lines.Add("]A[");
        }
        foreach (var screen in Ending.Screens) WriteScreen(lines, screen);
        lines.Add("]F[");
        if (Ending.HintMusic > 0) lines.Add($"]M {D3(Ending.HintMusic)}[");
        if (Ending.HintPic >= 0) lines.Add($"]P {D3(Ending.HintPic)}[");
        lines.Add("]Q[");
        for (int h = 0; h < EpisodeEnding.HintCount; h++)
        {
            var block = h < Ending.Hints.Count ? Ending.Hints[h] : null;
            if (block != null)
                foreach (string l in StoryScreen.ClipLines(block).Take(EpisodeEnding.MaxHintLines))
                    lines.Add(l);
            lines.Add("#");
        }
        lines.Add("");

        for (int i = 0; i < Arenas.Count; i++)
        {
            lines.Add($"*{arenaSec[i]} TIMED BATTLE {i + 1}");
            lines.Add(BuildLevelLine(battleOverSec, Arenas[i].Name, Arenas[i].Song,
                Arenas[i].LevelFile, false, false));
        }
        if (Arenas.Count > 0)
        {
            lines.Add($"*{battleOverSec} TIMED BATTLE OVER");
            lines.Add("]q[");
            lines.Add("");
        }
        return lines;
    }

    private static bool HasArrival(FlowStop s) => s.Outpost || s.Story.Count > 0;

    /// <summary>The ]W number: tens = red-alert mode, ones = glow speed.</summary>
    private static string WarnDigits(int red, int speed) =>
        $"{Math.Clamp(red, 0, 9)}{Math.Clamp(speed, 0, 9)}";

    private static void WriteScreen(List<string> lines, StoryScreen s)
    {
        switch (s.Fade)
        {
            case 1: lines.Add("]B["); break;
            case 2: lines.Add("]F["); break;
            case 3: lines.Add("]C["); break;
        }
        if (s.Music > 0) lines.Add($"]M {D3(s.Music)}[");
        if (s.Picture >= 0)
        {
            char cmd = s.Picture is >= 1 and <= 14
                ? s.Wipe switch { 1 => 'U', 2 => 'V', 3 => 'R', _ => 'P' }
                : 'P';   // palette clears and the PCX only exist on the plain form
            lines.Add($"]{cmd} {D3(s.Picture)}[");
        }
        lines.Add($"]W{(s.WarningFrame ? 'y' : 'n')} {WarnDigits(s.Red, s.Speed)}[");
        foreach (string l in StoryScreen.ClipLines(s.Lines)) lines.Add(l);
        lines.Add("#");
    }

    /// <summary>A ]L with every field on its engine-read position (see tyrian2.c:4945).</summary>
    public static string BuildLevelLine(int next, string name, int song, int file,
        bool normalBonus, bool bonus)
    {
        name = SafeName(name).PadRight(9);
        return "]L[ 9999 " + D3(next) + " " + name + D2(song) + " " + D2(file) +
               (normalBonus ? "$" : bonus ? " " : "") + (bonus ? "$" : "");
    }

    private static string SafeName(string name)
    {
        name = name.Replace('[', ' ').Replace(']', ' ');
        return name.Length > 9 ? name[..9] : name;
    }

    private static string D2(int v) => Math.Clamp(v, 0, 99).ToString("00");
    private static string D3(int v) => Math.Clamp(v, 0, 999).ToString("000");

    // =====================================================================
    // Import
    // =====================================================================

    /// <summary>
    /// Read a script back into stops. A marker script re-imports exactly; anything else is
    /// walked best-effort — every ]L becomes a stop, an immediately preceding section with
    /// a ]I becomes its outpost, story/cutscene screens ride along to the stop they lead
    /// into, and the ]Q section becomes the ending — which is enough to take a stock
    /// episode over and keep its spine, if not its conditional branches.
    /// </summary>
    public static EpisodeFlow FromScript(List<string> lines, int levelCount = 0)
    {
        var flow = new EpisodeFlow();
        flow.OwnsScript = lines.Count > 0 && lines[0].Contains(Marker, StringComparison.Ordinal);

        // Cut the lines into sections.
        var starts = new List<int> { 0 };
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Length > 0 && lines[i][0] == '*') starts.Add(i + 1);

        (int Begin, int End) Section(int s) => (starts[s],
            s + 1 < starts.Count ? starts[s + 1] - 1 : lines.Count);

        // ]T in section 1 names the arena sections. Together with the ]q section they
        // bound the Timed Battle zone: every level in it is an arena, not a stop (stock
        // data pads the five title-screen slots with filler sections the ]T line never
        // actually names).
        var arenaSections = new List<int>();
        int qSection = 0;
        for (int s = 1; s < starts.Count && qSection == 0; s++)
        {
            var (b, e) = Section(s);
            for (int i = b; i < e; i++)
                if (lines[i].Length >= 2 && lines[i][0] == ']' && lines[i][1] == 'q')
                {
                    qSection = s;
                    break;
                }
        }
        if (starts.Count > 1)
        {
            var (b1, e1) = Section(1);
            for (int i = b1; i < e1; i++)
            {
                if (lines[i].Length < 2 || lines[i][0] != ']' || lines[i][1] != 'T') continue;
                for (int k = 1; k <= MaxArenas; k++)
                {
                    int sec = EpisodeScript.AtoiAt(lines[i], k * 3);
                    if (sec <= 0) continue;
                    if (!arenaSections.Contains(sec)) arenaSections.Add(sec);
                }
                break;
            }
        }
        bool InArenaZone(int s) => arenaSections.Contains(s) ||
            (arenaSections.Count > 0 && qSection > 0 &&
             s >= arenaSections.Min() && s < qSection);

        // Carried from section to section until a stop claims them.
        FlowStop? pendingOutpost = null;
        var pendingStory = new List<StoryScreen>();

        for (int s = 1; s < starts.Count; s++)
        {
            var (begin, end) = Section(s);
            bool isArena = InArenaZone(s);
            bool hasShop = false, hasQ = false;
            var outpost = new FlowStop { Outpost = true, OutpostSong = 0, CubesFree = 0 };
            FlowStop? levelStop = null;
            bool save = false, saveBackup = false, galaga = false, engage = false, extra = false;
            var warning = new List<string>();
            bool warnFrame = true;
            int warnRed = 0, warnSpeed = 1;
            var screens = new List<StoryScreen>();
            // ]B/]F/]C and ]M/]P seen since the last ]W: they belong to the NEXT screen.
            var partial = new StoryScreen();
            bool partialUsed = false;
            bool sawAnim = false;
            int animMusic = 0;

            void FlushTextBlock(bool frame, int digits, List<string> body)
            {
                var screen = partial;
                partial = new StoryScreen();
                partialUsed = false;
                screen.WarningFrame = frame;
                screen.Red = digits / 10;
                screen.Speed = digits % 10;
                screen.Lines = StoryScreen.ClipLines(body);
                screens.Add(screen);
            }

            for (int i = begin; i < end; i++)
            {
                string line = lines[i];
                if (line.Length < 2 || line[0] != ']')
                    continue;
                switch (line[1])
                {
                    case 's': save = true; break;
                    case 'b': saveBackup = true; break;
                    case 'g': galaga = true; break;
                    case 'e': engage = true; break;
                    case 'x': extra = true; break;
                    case 'i': outpost.OutpostSong = EpisodeScript.AtoiAt(line, 3); break;
                    case 'A': sawAnim = true; animMusic = partial.Music; partial.Music = 0; break;
                    case 'B': partial.Fade = 1; partialUsed = true; break;
                    case 'F': partial.Fade = 2; partialUsed = true; break;
                    case 'C': partial.Fade = 3; partialUsed = true; break;
                    case 'M': partial.Music = EpisodeScript.AtoiAt(line, 3); partialUsed = true; break;
                    case 'P': partial.Picture = EpisodeScript.AtoiAt(line, 3); partial.Wipe = 0; partialUsed = true; break;
                    case 'U': partial.Picture = EpisodeScript.AtoiAt(line, 3); partial.Wipe = 1; partialUsed = true; break;
                    case 'V': partial.Picture = EpisodeScript.AtoiAt(line, 3); partial.Wipe = 2; partialUsed = true; break;
                    case 'R': partial.Picture = EpisodeScript.AtoiAt(line, 3); partial.Wipe = 3; partialUsed = true; break;
                    case '?':
                    {
                        int n = Math.Clamp(EpisodeScript.AtoiAt(line, 4), 0, 4);
                        for (int c = 0; c < n; c++)
                            outpost.Cubes.Add(EpisodeScript.AtoiAt(line, 3 + (c + 1) * 4));
                        break;
                    }
                    case '!': outpost.CubesFree = EpisodeScript.AtoiAt(line, 4); break;
                    case 'G':
                        outpost.MapPlanet = Math.Max(1, EpisodeScript.AtoiAt(line, 4));
                        break;
                    case 'W':
                    {
                        bool frame = line.Length > 2 && line[2] == 'y';
                        int digits = EpisodeScript.AtoiAt(line, 4);
                        var body = new List<string>();
                        int w = i + 1;
                        for (; w < end; w++)
                        {
                            if (lines[w].StartsWith('#')) break;
                            body.Add(lines[w]);
                        }
                        i = Math.Min(w, end - 1);
                        FlushTextBlock(frame, digits, body);
                        break;
                    }
                    case 'Q':
                    {
                        hasQ = true;
                        // The nine hint blocks follow immediately.
                        var hints = EpisodeEnding.NewHints();
                        int h = 0;
                        int w = i + 1;
                        var block = new List<string>();
                        for (; w < end && h < EpisodeEnding.HintCount; w++)
                        {
                            if (lines[w].StartsWith('#'))
                            {
                                hints[h++] = block;
                                block = new List<string>();
                            }
                            else block.Add(lines[w]);
                        }
                        i = Math.Min(Math.Max(w - 1, i), end - 1);

                        flow.Ending.Anim = sawAnim;
                        flow.Ending.AnimMusic = animMusic;
                        // The screen fragments before ]Q carry its music/backdrop.
                        flow.Ending.HintMusic = partial.Music;
                        flow.Ending.HintPic = partial.Picture >= 0 ? partial.Picture : -1;
                        flow.Ending.Screens = pendingStory.Concat(screens).Select(sc => sc.Clone()).ToList();
                        flow.Ending.Hints = hints;
                        pendingStory.Clear();
                        screens.Clear();
                        partial = new StoryScreen();
                        partialUsed = false;
                        break;
                    }
                    case 'I':
                        hasShop = true;
                        for (int r = 0; r < ShopRowCount && i + 1 + r < end; r++)
                        {
                            string row = lines[i + 1 + r];
                            var ids = new List<int>();
                            int p = Math.Min(8, row.Length);
                            while (p < row.Length)
                            {
                                while (p < row.Length && !char.IsDigit(row[p]) && row[p] != '-') p++;
                                if (p >= row.Length) break;
                                int v = EpisodeScript.AtoiAt(row, p);
                                ids.Add(v);
                                while (p < row.Length && (char.IsDigit(row[p]) || row[p] == '-')) p++;
                            }
                            outpost.Shop[r] = ids;
                        }
                        i += ShopRowCount;
                        break;
                    case 'L':
                    {
                        var e = EpisodeScript.ParseLevelLine(line, s);
                        // Stock scripts carry vestigial ]L lines pointing past the .lvl's
                        // sections (Episode 1 has a file-20 leftover); those are not stops.
                        if (e.LvlFileNum <= 0 || (levelCount > 0 && e.LvlFileNum > levelCount))
                            break;
                        if (isArena)
                        {
                            flow.Arenas.Add(new BattleArena
                            {
                                LevelFile = e.LvlFileNum,
                                Name = e.Name.TrimEnd(),
                                Song = e.Song,
                            });
                            break;
                        }
                        levelStop ??= new FlowStop
                        {
                            LevelFile = e.LvlFileNum,
                            Name = e.Name.TrimEnd(),
                            Song = e.Song,
                            Bonus = e.BonusLevel,
                            NormalBonus = e.NormalBonus,
                        };
                        break;
                    }
                }
            }

            if (isArena) { pendingOutpost = null; pendingStory.Clear(); continue; }
            if (levelStop != null)
            {
                levelStop.SavePoint = save;
                levelStop.SaveBackup = saveBackup;
                levelStop.Galaga = galaga;
                levelStop.Engage = engage;
                levelStop.Extra = extra;
                // Text blocks found in the level's own section: the first WARNING-framed one
                // (or the only one) is the classic pre-level warning; the rest is story.
                int warnAt = screens.FindIndex(sc => sc.WarningFrame && sc.Picture < 0 && sc.Music == 0);
                if (warnAt < 0 && screens.Count == 1 && screens[0].Picture < 0 && screens[0].Music == 0)
                    warnAt = 0;
                if (warnAt >= 0)
                {
                    var w = screens[warnAt];
                    warning = w.Lines;
                    warnFrame = w.WarningFrame;
                    warnRed = w.Red;
                    warnSpeed = w.Speed;
                    screens.RemoveAt(warnAt);
                }
                levelStop.Warning = StoryScreen.ClipLines(warning);
                levelStop.WarnFrame = warnFrame;
                levelStop.WarnRed = warnRed;
                levelStop.WarnSpeed = warnSpeed;
                levelStop.Story = pendingStory.Concat(screens).Select(sc => sc.Clone()).ToList();
                pendingStory.Clear();
                if (pendingOutpost != null)
                {
                    levelStop.Outpost = true;
                    levelStop.OutpostSong = pendingOutpost.OutpostSong;
                    levelStop.MapPlanet = pendingOutpost.MapPlanet;
                    levelStop.Cubes = pendingOutpost.Cubes;
                    levelStop.CubesFree = pendingOutpost.CubesFree;
                    levelStop.Shop = pendingOutpost.Shop;
                }
                flow.Stops.Add(levelStop);
                pendingOutpost = null;
            }
            else if (hasShop)
            {
                pendingOutpost = outpost;
                pendingStory.AddRange(screens);
            }
            else if (hasQ)
            {
                // The ending section: everything before it was consumed into the ending.
                pendingOutpost = null;
                pendingStory.Clear();
            }
            else
            {
                // A pure text/cutscene or routing section: its screens ride to the next stop.
                pendingOutpost = null;
                pendingStory.AddRange(screens);
                if (partialUsed && screens.Count == 0)
                {
                    // ]M/]P with no ]W (a music change on a routing section): keep it as a
                    // silent screen so a regenerate still plays the song.
                    if (partial.Music > 0 || partial.Picture >= 0)
                        pendingStory.Add(partial);
                }
            }
        }
        return flow;
    }
}
