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
/// One episode being edited: every level of its tyrian{N}.lvl, its levels{N}.dat script,
/// and the enemy table it plays with (tyrian.hdt for episodes 1-3 — shared by all three —
/// or the block embedded at the end of the .lvl for 4 and 5). Builds byte-exact files back;
/// an untouched load/save round-trips identically, which is what keeps the output loadable
/// by the game and the Engaged fork alike.
/// </summary>
public sealed class EditableEpisode
{
    /// <summary>lvlPos[43] caps lvlNum at 41 entries = 20 level sections.</summary>
    public const int MaxLevels = 20;

    public int Number;
    public readonly List<EditableLevel> Levels = new();
    public List<string> ScriptLines = new();

    /// <summary>The enemy table, editable. Index space is the engine's: 0..850 and 1001..1850.</summary>
    public EnemyDat[] Enemies = Array.Empty<EnemyDat>();

    /// <summary>Episodes 4/5: the whole item-data block embedded after the last section,
    /// verbatim; the enemy table lives inside it at <see cref="EnemyOffsetInBlock"/>.
    /// Episodes 1-3 keep this null and patch tyrian.hdt instead.</summary>
    public byte[]? ItemBlock;
    public int EnemyOffsetInBlock = -1;

    /// <summary>Episodes 1-3: where the shared enemy table sits inside tyrian.hdt.</summary>
    public int HdtEnemyOffset = -1;

    public bool LevelsDirty, ScriptDirty, EnemiesDirty;
    public bool Dirty => LevelsDirty || ScriptDirty || EnemiesDirty;

    public bool SharedEnemyTable => Number <= 3;

    public static EditableEpisode Load(GameData gd, EpisodeInfo ep)
    {
        var e = new EditableEpisode { Number = ep.Number };
        for (int f = 1; f <= ep.Container.SectionCount; f++)
            e.Levels.Add(EditableLevel.FromContainer(ep.Container, f));

        string scriptPath = Path.Combine(gd.DataDir, $"levels{ep.Number}.dat");
        if (File.Exists(scriptPath))
            e.ScriptLines = EpisodeScript.DecryptStrings(scriptPath);

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
    /// blank level and a minimal script (section 1 plays level 1, section 2 is the ]Q
    /// ending — its nine '#' blocks are what the engine's hint reader expects to skim).
    /// The enemy table and the embedded item block are inherited from what was loaded:
    /// the engine cannot run a level without them.
    /// </summary>
    public void StartBlank(char shapeChar)
    {
        Levels.Clear();
        Levels.Add(EditableLevel.CreateNew(shapeChar));
        ScriptLines = new List<string>
        {
            "*1 START",
            "]L[ 9999 002 NEW LEVEL 01 01",
            "",
            "*2 EPISODE COMPLETE",
            "]Q[",
        };
        for (int i = 0; i < 9; i++) ScriptLines.Add("#");
        ScriptLines.Add("");
        LevelsDirty = ScriptDirty = true;
    }

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

    /// <summary>Everything that would stop the engine loading the result. Empty = good.</summary>
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
            if (lv.Events.Count == 0)
                problems.Add($"level #{i + 1}: no events — it will never scroll or end");
            if (lv.LevelEnemy.Count > EditableLevel.MaxLevelEnemies)
                problems.Add($"level #{i + 1}: {lv.LevelEnemy.Count} random enemies (engine cap {EditableLevel.MaxLevelEnemies})");
        }
        for (int i = 0; i < ScriptLines.Count; i++)
            if (ScriptLines[i].Length > 255)
                problems.Add($"script line {i + 1} is over 255 characters and cannot be encoded");
        foreach (var (line, idx) in ScriptLines.Select((l, i) => (l, i)))
        {
            if (line.Length < 2 || line[0] != ']' || line[1] != 'L') continue;
            int file = EpisodeScript.AtoiAt(line, 25);
            if (file < 1 || file > Levels.Count)
                problems.Add($"script line {idx + 1}: ]L names level file {file}, but the episode has 1..{Levels.Count}");
        }
        return problems;
    }

    // =====================================================================
    // Saving
    // =====================================================================

    /// <summary>The files a save writes, with their target names.</summary>
    public string LvlFileName => $"tyrian{Number}.lvl";
    public string ScriptFileName => $"levels{Number}.dat";
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
        LevelsDirty = ScriptDirty = EnemiesDirty = false;
        return written;
    }

    /// <summary>The .t2abak files present for this episode in <paramref name="dir"/>.</summary>
    public List<string> BackupsIn(string dir)
    {
        var names = new List<string> { LvlFileName, ScriptFileName };
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
