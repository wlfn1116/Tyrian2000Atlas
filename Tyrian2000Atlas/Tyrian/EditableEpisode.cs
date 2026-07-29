using System.Text;

namespace T2A.Tyrian;

/// <summary>
/// One level section of a tyrian{N}.lvl, fully editable. The section is parsed completely —
/// header, random-enemy list, 11-byte event records, the three 128-slot shape tables and the
/// three tile grids — so a load/save round-trip is byte-for-byte identical.
/// </summary>
public sealed class EditableLevel
{
    // Engine limits (lvllib.h / lvlmast.h / varz.h).
    public const int MaxEvents = 2499;        // maxEvent must stay < EVENT_MAXIMUM (2500)
    public const int MaxLevelEnemies = 40;    // levelEnemy[40]

    public byte MapFileChar = (byte)'!';
    public char ShapeChar = 'w';
    public ushort MapX = 3, MapX2 = 3, MapX3 = 3;
    public List<ushort> LevelEnemy = new();
    public List<EventRec> Events = new();
    public readonly ushort[][] MapSh = { new ushort[128], new ushort[128], new ushort[128] };
    public byte[] Bg1 = new byte[Level.Bg1Cols * Level.Bg1Rows];
    public byte[] Bg2 = new byte[Level.Bg2Cols * Level.Bg2Rows];
    public byte[] Bg3 = new byte[Level.Bg3Cols * Level.Bg3Rows];

    /// <summary>Cells a layer's grid may hold. The engine hard-reserves BG2 cell 71 and BG3
    /// cells 70+ as empty, and reads no cell above 71 on any layer.</summary>
    public static int SlotLimit(int layer) => layer switch { 0 => 72, 1 => 71, _ => 70 };

    public byte[] Cells(int layer) => layer == 0 ? Bg1 : layer == 1 ? Bg2 : Bg3;

    /// <summary>A brand-new level: empty grids on the given tile set, minimal event script.</summary>
    public static EditableLevel CreateNew(char shapeChar)
    {
        var lv = new EditableLevel { ShapeChar = shapeChar };
        // Slot 0 maps to shape 0 (empty) on every layer; grids start all-empty.
        Array.Fill(lv.Bg2, (byte)71);
        Array.Fill(lv.Bg3, (byte)70);
        lv.Events.Add(new EventRec { Time = 30, Type = 1, Dat = 2 });          // starfield speed
        lv.Events.Add(new EventRec { Time = 30, Type = 2, Dat = 1, Dat2 = 2, Dat3 = 3 });
        lv.Events.Add(new EventRec { Time = 3000, Type = 36 });                // ready to end
        lv.Events.Add(new EventRec { Time = 3100, Type = 11 });                // end level
        return lv;
    }

    /// <summary>A deep copy (for Duplicate).</summary>
    public EditableLevel Clone()
    {
        var lv = new EditableLevel
        {
            MapFileChar = MapFileChar,
            ShapeChar = ShapeChar,
            MapX = MapX, MapX2 = MapX2, MapX3 = MapX3,
            LevelEnemy = LevelEnemy.ToList(),
            Events = Events.ToList(),
            Bg1 = (byte[])Bg1.Clone(),
            Bg2 = (byte[])Bg2.Clone(),
            Bg3 = (byte[])Bg3.Clone(),
        };
        for (int l = 0; l < 3; l++) Array.Copy(MapSh[l], lv.MapSh[l], 128);
        return lv;
    }

    /// <summary>Parse one section out of the container.</summary>
    public static EditableLevel FromContainer(LevelContainer c, int fileNum)
    {
        var parsed = Level.Parse(c, fileNum);
        var lv = new EditableLevel
        {
            MapFileChar = parsed.MapFileChar,
            ShapeChar = parsed.ShapeChar,
            MapX = parsed.MapX, MapX2 = parsed.MapX2, MapX3 = parsed.MapX3,
            LevelEnemy = parsed.LevelEnemy.ToList(),
            Events = parsed.Events.ToList(),
            Bg1 = (byte[])parsed.Bg1.Clone(),
            Bg2 = (byte[])parsed.Bg2.Clone(),
            Bg3 = (byte[])parsed.Bg3.Clone(),
        };
        for (int l = 0; l < 3; l++) Array.Copy(parsed.MapSh[l], lv.MapSh[l], 128);
        return lv;
    }

    /// <summary>The exact on-disk byte size of this section (events are 11 bytes each).</summary>
    public int SectionSize => MapDataOffset + 768 + Bg1.Length + Bg2.Length + Bg3.Length;

    /// <summary>Where the map-shape tables start, relative to the section start — what the
    /// container's odd lvlPos entries point at.</summary>
    public int MapDataOffset => 12 + 2 * LevelEnemy.Count + 11 * Events.Count;

    public void WriteSection(BinaryWriter w)
    {
        w.Write(MapFileChar);
        w.Write((byte)ShapeChar);
        w.Write(MapX); w.Write(MapX2); w.Write(MapX3);
        w.Write((ushort)LevelEnemy.Count);
        foreach (ushort e in LevelEnemy) w.Write(e);
        w.Write((ushort)Events.Count);
        foreach (var ev in Events)
        {
            w.Write(ev.Time);
            w.Write(ev.Type);
            w.Write(ev.Dat);
            w.Write(ev.Dat2);
            w.Write(ev.Dat3);
            w.Write(ev.Dat5);   // on-disk order: dat3, dat5, dat6, dat4
            w.Write(ev.Dat6);
            w.Write(ev.Dat4);
        }
        for (int l = 0; l < 3; l++)
            for (int i = 0; i < 128; i++)
            {
                w.Write((byte)(MapSh[l][i] >> 8));   // big-endian, as the engine expects
                w.Write((byte)(MapSh[l][i] & 0xFF));
            }
        w.Write(Bg1); w.Write(Bg2); w.Write(Bg3);
    }

    /// <summary>A parsed-level view of this state, for the renderer and the simulation.</summary>
    public Level ToLevel(int fileNum)
    {
        var lv = new Level
        {
            FileNum = fileNum,
            MapFileChar = MapFileChar,
            ShapeChar = ShapeChar,
            MapX = MapX, MapX2 = MapX2, MapX3 = MapX3,
            LevelEnemy = LevelEnemy.ToArray(),
            Events = Events.ToArray(),
            Bg1 = (byte[])Bg1.Clone(),
            Bg2 = (byte[])Bg2.Clone(),
            Bg3 = (byte[])Bg3.Clone(),
        };
        for (int l = 0; l < 3; l++) Array.Copy(MapSh[l], lv.MapSh[l], 128);
        return lv;
    }

    /// <summary>
    /// The grid cell to paint for a 1-based shape id on a layer: an existing slot that
    /// already maps to it, else a free one claimed for it. -1 when the layer's slot table
    /// is full. Shape 0 asks for an empty cell and prefers the layer's reserved one.
    /// </summary>
    public int EnsureSlot(int layer, int shapeId)
    {
        int limit = SlotLimit(layer);
        if (shapeId == 0)
        {
            if (layer == 1) return 71;    // hard-reserved empties need no slot at all
            if (layer == 2) return 70;
            for (int i = 0; i < limit; i++) if (MapSh[layer][i] == 0) return i;
        }
        else
        {
            for (int i = 0; i < limit; i++) if (MapSh[layer][i] == shapeId) return i;
        }

        // Claim a slot no cell references (its current mapping is unreachable anyway).
        var used = new bool[128];
        foreach (byte cell in Cells(layer)) if (cell < 128) used[cell] = true;
        for (int i = 0; i < limit; i++)
            if (!used[i]) { MapSh[layer][i] = (ushort)shapeId; return i; }
        return -1;
    }

    /// <summary>How many of the layer's slots are taken (mapped or referenced).</summary>
    public int SlotsUsed(int layer)
    {
        int limit = SlotLimit(layer);
        var used = new bool[128];
        foreach (byte cell in Cells(layer)) if (cell < 128) used[cell] = true;
        int n = 0;
        for (int i = 0; i < limit; i++) if (used[i] || MapSh[layer][i] != 0) n++;
        return n;
    }
}

/// <summary>
/// One datacube of cubetxt{N}.dat, editable with byte-exact round-tripping: the raw lines
/// are kept verbatim and only rewritten where an edit actually landed. The marker line's
/// number pair is cosmetic to the engine — load_cube finds cubes by counting '*' markers
/// and reads only the face number at offset 4 (game_menu.c:2899).
/// </summary>
public sealed class EditableCube
{
    public string Marker = "*01 01";
    public string Title = "";
    public string Header = "";
    public List<string> Body = new();
    /// <summary>Whether the file actually carried these lines: reserved slots are bare
    /// consecutive markers (Episode 4 opens with three), and writing empty title/header
    /// lines for them would break the byte-exact round-trip.</summary>
    public bool HasTitle, HasHeader;

    public bool WriteTitle => HasTitle || Title.Length > 0 || Header.Length > 0 || Body.Count > 0;
    public bool WriteHeader => HasHeader || Header.Length > 0 || Body.Count > 0;
    public bool IsEmpty => !WriteTitle && Body.Count == 0;

    /// <summary>The 1-based face-sprite number in the marker (0 = none drawn).</summary>
    public int Face
    {
        get => EpisodeScript.AtoiAt(Marker, 4);
        set
        {
            // Keep any trailing comment the stock marker carried.
            int paren = Marker.IndexOf('(');
            string comment = paren >= 0 ? "  " + Marker[paren..].TrimEnd() : "";
            int index = Marker.Length > 1 ? EpisodeScript.AtoiAt(Marker, 1) : 0;
            Marker = $"*{Math.Max(1, index):00} {Math.Clamp(value, 0, 99):00}{comment}";
        }
    }

    public EditableCube Clone() => new()
    {
        Marker = Marker, Title = Title, Header = Header, Body = Body.ToList(),
    };
}

/// <summary>
/// One episode being edited: every level of its tyrian{N}.lvl, its levels{N}.dat script,
/// its cubetxt{N}.dat datacube readings, and the enemy table it plays with (tyrian.hdt for
/// episodes 1-3 — shared by all three — or the block embedded at the end of the .lvl for 4
/// and 5). Builds byte-exact files back; an untouched load/save round-trips identically,
/// which is what keeps the output loadable by the game and the Engaged fork alike.
/// </summary>
public sealed class EditableEpisode
{
    /// <summary>lvlPos[43] caps lvlNum at 41 entries = 20 level sections.</summary>
    public const int MaxLevels = 20;

    public int Number;
    public readonly List<EditableLevel> Levels = new();
    public List<string> ScriptLines = new();
    /// <summary>The episode's datacube readings, in shelf order (]? indices are 1-based
    /// into this list).</summary>
    public List<EditableCube> Cubes = new();

    /// <summary>The enemy table, editable. Index space is the engine's: 0..850 and 1001..1850.</summary>
    public EnemyDat[] Enemies = Array.Empty<EnemyDat>();

    /// <summary>Episodes 4/5: the whole item-data block embedded after the last section,
    /// verbatim; the enemy table lives inside it at <see cref="EnemyOffsetInBlock"/>.
    /// Episodes 1-3 keep this null and patch tyrian.hdt instead.</summary>
    public byte[]? ItemBlock;
    public int EnemyOffsetInBlock = -1;

    /// <summary>Episodes 1-3: where the shared enemy table sits inside tyrian.hdt.</summary>
    public int HdtEnemyOffset = -1;

    public bool LevelsDirty, ScriptDirty, EnemiesDirty, CubesDirty;
    public bool Dirty => LevelsDirty || ScriptDirty || EnemiesDirty || CubesDirty;

    public bool SharedEnemyTable => Number <= 3;

    public static EditableEpisode Load(GameData gd, EpisodeInfo ep)
    {
        var e = new EditableEpisode { Number = ep.Number };
        for (int f = 1; f <= ep.Container.SectionCount; f++)
            e.Levels.Add(EditableLevel.FromContainer(ep.Container, f));

        string scriptPath = Path.Combine(gd.DataDir, $"levels{ep.Number}.dat");
        if (File.Exists(scriptPath))
            e.ScriptLines = EpisodeScript.DecryptStrings(scriptPath);

        string cubePath = Path.Combine(gd.DataDir, $"cubetxt{ep.Number}.dat");
        if (File.Exists(cubePath))
            e.Cubes = ParseCubes(EpisodeScript.DecryptStrings(cubePath));

        var ed = EnemyData.Load(gd.DataDir, ep);
        e.Enemies = (EnemyDat[])ed.Enemies.Clone();
        if (ep.Number >= 4)
        {
            int blockStart = ep.Container.LvlPos[ep.Container.LvlNum - 1];
            e.ItemBlock = ep.Container.Raw[blockStart..];
            e.EnemyOffsetInBlock = ed.EnemyOffset - blockStart;
        }
        else
        {
            e.HdtEnemyOffset = ed.EnemyOffset;
        }
        return e;
    }

    /// <summary>An EnemyData over the edited table, for the renderer and the simulation.</summary>
    public EnemyData ToEnemyData() => EnemyData.FromRecords(Enemies);

    /// <summary>
    /// Throw the loaded content away and start a from-scratch episode in this slot: one
    /// blank level and a Flow-owned script (an outpost, the level, then the ]Q ending with
    /// its nine '#' blocks — everything the engine's readers expect). The enemy table, the
    /// embedded item block and the datacube file are inherited from what was loaded: the
    /// engine cannot run a level without them.
    /// </summary>
    public void StartBlank(char shapeChar)
    {
        Levels.Clear();
        Levels.Add(EditableLevel.CreateNew(shapeChar));
        var flow = new EpisodeFlow { OwnsScript = true };
        var stop = new FlowStop { LevelFile = 1, Name = "NEW LEVEL", Song = 1, Outpost = true };
        stop.Shop[0].Add(1);                       // a ship on the shelf keeps the shop sane
        stop.Shop[3].Add(1); stop.Shop[4].Add(1);
        stop.Shop[7].Add(1); stop.Shop[8].Add(1);
        flow.Stops.Add(stop);
        ScriptLines = flow.Generate();
        LevelsDirty = ScriptDirty = true;
    }

    // =====================================================================
    // cubetxt{N}.dat
    // =====================================================================

    /// <summary>Cut the decrypted cube file into cubes at its '*' markers.</summary>
    public static List<EditableCube> ParseCubes(List<string> lines)
    {
        var cubes = new List<EditableCube>();
        EditableCube? cur = null;
        int field = 0;
        foreach (var s in lines)
        {
            if (s.Length > 0 && s[0] == '*')
            {
                cur = new EditableCube { Marker = s };
                cubes.Add(cur);
                field = 0;
                continue;
            }
            if (cur == null) continue;
            if (field == 0) { cur.Title = s; cur.HasTitle = true; field++; }
            else if (field == 1) { cur.Header = s; cur.HasHeader = true; field++; }
            else cur.Body.Add(s);
        }
        return cubes;
    }

    /// <summary>The cube file's line list — the exact inverse of <see cref="ParseCubes"/>.</summary>
    public List<string> CubeLines()
    {
        var lines = new List<string>();
        foreach (var cube in Cubes)
        {
            lines.Add(cube.Marker);
            if (cube.WriteTitle) lines.Add(cube.Title);
            if (cube.WriteHeader) lines.Add(cube.Header);
            lines.AddRange(cube.Body);
        }
        return lines;
    }

    public byte[] BuildCubeBytes() => EpisodeScript.EncryptStrings(CubeLines());

    // =====================================================================
    // tyrian{N}.lvl
    // =====================================================================

    public byte[] BuildLvlBytes()
    {
        int lvlNum = Levels.Count * 2 + 1;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)lvlNum);

        int at = 2 + 4 * lvlNum;
        var pos = new int[lvlNum];
        for (int i = 0; i < Levels.Count; i++)
        {
            pos[i * 2] = at;
            pos[i * 2 + 1] = at + Levels[i].MapDataOffset;
            at += Levels[i].SectionSize;
        }
        // The final entry: the embedded item block (episodes 4/5), or end-of-file (1-3) —
        // exactly what the stock files hold there.
        pos[lvlNum - 1] = at;
        foreach (int p in pos) w.Write(p);

        foreach (var lv in Levels) lv.WriteSection(w);

        if (ItemBlock != null)
        {
            var block = (byte[])ItemBlock.Clone();
            if (EnemyOffsetInBlock >= 0)
                EnemyData.WriteTable(Enemies, block, EnemyOffsetInBlock);
            w.Write(block);
        }
        return ms.ToArray();
    }

    // =====================================================================
    // levels{N}.dat
    // =====================================================================

    public byte[] BuildScriptBytes() => EpisodeScript.EncryptStrings(ScriptLines);

    // =====================================================================
    // Validation
    // =====================================================================

    /// <summary>
    /// Only what would break WRITING the files, the engine LOADING them, or — for the raw
    /// text-screen limits — the engine's unchecked string buffers. Empty = safe to save.
    /// States the engine merely tolerates must not appear here: stock data itself is not
    /// pristine (episode 1 ships a vestigial ]L line pointing at level file 20 of 18), and
    /// a save gate that stock files cannot pass locks every creator out of the episode.
    /// Those live in <see cref="Advisories"/> instead.
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (Levels.Count == 0) problems.Add("the episode has no levels");
        if (Levels.Count > MaxLevels)
            problems.Add($"{Levels.Count} levels — the engine's offset table caps an episode at {MaxLevels}");
        for (int i = 0; i < Levels.Count; i++)
        {
            var lv = Levels[i];
            if (lv.Events.Count > EditableLevel.MaxEvents)
                problems.Add($"level #{i + 1}: {lv.Events.Count} events (engine cap {EditableLevel.MaxEvents})");
            if (lv.LevelEnemy.Count > EditableLevel.MaxLevelEnemies)
                problems.Add($"level #{i + 1}: {lv.LevelEnemy.Count} random enemies (engine cap {EditableLevel.MaxLevelEnemies})");
        }
        for (int i = 0; i < ScriptLines.Count; i++)
            if (ScriptLines[i].Length > 255)
                problems.Add($"script line {i + 1} is over 255 characters and cannot be encoded");
        for (int i = 0; i < Cubes.Count; i++)
        {
            var cube = Cubes[i];
            if (cube.Marker.Length > 255 || cube.Title.Length > 255 ||
                cube.Header.Length > 255 || cube.Body.Any(l => l.Length > 255))
                problems.Add($"datacube {i + 1}: a line is over 255 characters and cannot be encoded");
        }
        ScanTextBlocks(problems, null);
        return problems;
    }

    /// <summary>
    /// Walk every ]W and ]Q text block the way the engine streams them, and report what
    /// would overrun its fixed buffers (fonthand.c: levelWarningText[12][61], written with
    /// plain strcpy — an overrun corrupts the real game, so these BLOCK a save) into
    /// <paramref name="problems"/>, and the soft structural notes into
    /// <paramref name="notes"/>. Either list may be null to skip that half.
    /// </summary>
    private void ScanTextBlocks(List<string>? problems, List<string>? notes)
    {
        for (int i = 0; i < ScriptLines.Count; i++)
        {
            string s = ScriptLines[i];
            if (s.Length < 2 || s[0] != ']') continue;
            if (s[1] == 'W')
            {
                int lines = 0;
                bool closed = false;
                int j = i + 1;
                for (; j < ScriptLines.Count; j++)
                {
                    if (ScriptLines[j].StartsWith('#')) { closed = true; break; }
                    lines++;
                    if (ScriptLines[j].Length > StoryScreen.MaxLineLen)
                        notes?.Add($"script line {j + 1}: text-screen line is " +
                            $"{ScriptLines[j].Length} characters — the engine's rows hold " +
                            $"{StoryScreen.MaxLineLen} and one byte tramples the next line " +
                            "(stock episode 5 ships one of these)");
                }
                if (lines > StoryScreen.MaxLines)
                    problems?.Add($"script line {i + 1}: ]W block has {lines} lines — the engine " +
                        $"holds {StoryScreen.MaxLines} and overruns beyond them");
                if (!closed)
                    problems?.Add($"script line {i + 1}: ]W block never reaches a '#' line — " +
                        "the engine would stream the rest of the file into it");
                i = Math.Min(j, ScriptLines.Count - 1);
            }
            else if (s[1] == 'Q')
            {
                int block = 0, lines = 0;
                int j = i + 1;
                for (; j < ScriptLines.Count && block < EpisodeEnding.HintCount; j++)
                {
                    if (ScriptLines[j].StartsWith('#')) { block++; lines = 0; continue; }
                    lines++;
                    if (ScriptLines[j].Length > StoryScreen.MaxLineLen)
                        notes?.Add($"script line {j + 1}: hint line is " +
                            $"{ScriptLines[j].Length} characters — the engine's rows hold " +
                            $"{StoryScreen.MaxLineLen}");
                    if (lines == EpisodeEnding.MaxHintLines + 1)
                        problems?.Add($"script line {i + 1}: hint block {block + 1} is over " +
                            $"{EpisodeEnding.MaxHintLines} lines — with the score header it " +
                            "overruns the engine's 12-line text buffer");
                }
                if (block < EpisodeEnding.HintCount)
                    problems?.Add($"script line {i + 1}: ]Q needs {EpisodeEnding.HintCount} " +
                        $"'#'-terminated hint blocks after it but only {block} exist — the engine " +
                        "reads a random one of nine and would run off the end of the file");
                i = Math.Min(j, ScriptLines.Count - 1);
            }
            else if (s[1] == 'G')
            {
                int destinations = EpisodeScript.AtoiAt(s, 7);
                if (destinations > EpisodeFlow.MaxMapDest)
                    problems?.Add($"script line {i + 1}: ]G lists {destinations} destinations — " +
                        $"the engine's map holds {EpisodeFlow.MaxMapDest} and overruns beyond them");
            }
            else if (s[1] == 'I')
            {
                for (int r = 0; r < EpisodeFlow.ShopRowCount && i + 1 + r < ScriptLines.Count; r++)
                {
                    string row = ScriptLines[i + 1 + r];
                    int ids = CountShopIds(row);
                    if (ids > EpisodeFlow.ShopRowMax)
                        problems?.Add($"script line {i + 2 + r}: shop row lists {ids} items — " +
                            $"the engine's rows hold {EpisodeFlow.ShopRowMax} and overrun beyond them");
                }
                i += EpisodeFlow.ShopRowCount;
            }
        }
    }

    private static int CountShopIds(string row)
    {
        int n = 0, p = Math.Min(8, row.Length);
        while (p < row.Length)
        {
            while (p < row.Length && !char.IsDigit(row[p]) && row[p] != '-') p++;
            if (p >= row.Length) break;
            n++;
            while (p < row.Length && (char.IsDigit(row[p]) || row[p] == '-')) p++;
        }
        return n;
    }

    /// <summary>States a save survives but a creator should know about: dead script routes,
    /// levels that cannot end, and a route that never reaches the ending. Stock data ships
    /// some, so these inform, never block.</summary>
    public List<string> Advisories()
    {
        var notes = new List<string>();
        ScanTextBlocks(null, notes);
        for (int i = 0; i < Levels.Count; i++)
        {
            if (Levels[i].Events.Count == 0)
                notes.Add($"level #{i + 1} has no events — it will never scroll or end");
            else if (!Levels[i].Events.Any(e => e.Type == 11))
                notes.Add($"level #{i + 1} has no End level event (11) — it will never finish");
        }
        foreach (var (line, idx) in ScriptLines.Select((l, i) => (l, i)))
        {
            if (line.Length < 2 || line[0] != ']' || line[1] != 'L') continue;
            int file = EpisodeScript.AtoiAt(line, 25);
            if (file < 1 || file > Levels.Count)
                notes.Add($"script line {idx + 1}: ]L points at level file {file}, which does not " +
                          $"exist (a dead entry — stock episode 1 ships one of these)");
        }

        // The route itself, resolved the way the engine resolves it.
        try
        {
            var script = new EpisodeScriptFile();
            foreach (var l in ScriptLines)
            {
                script.Lines.Add(l);
                if (l.Length > 0 && l[0] == '*') script.SectionStart.Add(script.Lines.Count);
            }
            var graph = EpisodeGraph.Build(script, new List<string> { "" });
            if (!graph.Nodes.Any(n => n.Kind == GraphNodeKind.NextEpisode))
                notes.Add("no route reaches a ]Q — the episode never ends; the last level's " +
                          "chain just stops");
            var unreachable = graph.Nodes.Where(n => n.Kind == GraphNodeKind.Level &&
                n.In.All(ei => graph.Edges[ei].Kind == EdgeKind.Start &&
                               graph.Edges[ei].Label == "no route in")).ToList();
            foreach (var n in unreachable)
                notes.Add($"level entry {n.Title} (file #{n.LvlFileNum}) has no route " +
                          "leading to it — players can never reach it");

            int cubeCount = Cubes.Count;
            for (int i = 0; i < ScriptLines.Count; i++)
            {
                string s = ScriptLines[i];
                if (s.Length < 2 || s[0] != ']' || s[1] != '?') continue;
                int n = Math.Clamp(EpisodeScript.AtoiAt(s, 4), 0, 8);
                for (int c = 0; c < n; c++)
                {
                    int cube = EpisodeScript.AtoiAt(s, 3 + (c + 1) * 4);
                    if (cube > cubeCount)
                        notes.Add($"script line {i + 1}: ]? offers datacube {cube} but " +
                                  $"cubetxt{Number}.dat holds only {cubeCount}");
                }
            }
        }
        catch
        {
            // The advisory walker must never take the save dialog down with it.
        }
        return notes;
    }

    // =====================================================================
    // Saving
    // =====================================================================

    /// <summary>The files a save writes, with their target names.</summary>
    public string LvlFileName => $"tyrian{Number}.lvl";
    public string ScriptFileName => $"levels{Number}.dat";
    public string CubeFileName => $"cubetxt{Number}.dat";
    public const string HdtFileName = "tyrian.hdt";
    public const string BackupSuffix = ".t2abak";

    /// <summary>
    /// Write the episode into <paramref name="dir"/>. The first time a stock file is
    /// overwritten a pristine copy is kept beside it as *.t2abak, so a save is always
    /// reversible. Returns the files written.
    /// </summary>
    public List<string> SaveTo(string dir, bool backup)
    {
        var written = new List<string>();
        void Put(string name, byte[] bytes)
        {
            string path = Path.Combine(dir, name);
            if (backup && File.Exists(path) && !File.Exists(path + BackupSuffix))
                File.Copy(path, path + BackupSuffix);
            File.WriteAllBytes(path, bytes);
            written.Add(name);
        }

        Put(LvlFileName, BuildLvlBytes());
        Put(ScriptFileName, BuildScriptBytes());
        if (Cubes.Count > 0)
        {
            // Only touch cubetxt when it actually changed: most sessions never edit it.
            byte[] cubes = BuildCubeBytes();
            string cubePath = Path.Combine(dir, CubeFileName);
            if (!File.Exists(cubePath) || !File.ReadAllBytes(cubePath).AsSpan().SequenceEqual(cubes))
                Put(CubeFileName, cubes);
        }
        if (SharedEnemyTable && HdtEnemyOffset >= 0)
        {
            string hdtPath = Path.Combine(dir, HdtFileName);
            if (File.Exists(hdtPath))
            {
                byte[] hdt = File.ReadAllBytes(hdtPath);
                var patched = (byte[])hdt.Clone();
                EnemyData.WriteTable(Enemies, patched, HdtEnemyOffset);
                if (!patched.AsSpan().SequenceEqual(hdt))
                    Put(HdtFileName, patched);
            }
        }
        LevelsDirty = ScriptDirty = EnemiesDirty = CubesDirty = false;
        return written;
    }

    /// <summary>The .t2abak files present for this episode in <paramref name="dir"/>.</summary>
    public List<string> BackupsIn(string dir)
    {
        var names = new List<string> { LvlFileName, ScriptFileName, CubeFileName };
        if (SharedEnemyTable) names.Add(HdtFileName);
        return names.Where(n => File.Exists(Path.Combine(dir, n + BackupSuffix))).ToList();
    }

    /// <summary>Put the pristine files back. Returns the files restored.</summary>
    public List<string> RevertIn(string dir)
    {
        var restored = new List<string>();
        foreach (string name in BackupsIn(dir))
        {
            File.Copy(Path.Combine(dir, name + BackupSuffix), Path.Combine(dir, name), overwrite: true);
            restored.Add(name);
        }
        return restored;
    }
}
