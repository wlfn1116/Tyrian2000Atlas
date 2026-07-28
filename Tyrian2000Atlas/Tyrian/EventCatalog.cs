namespace T2A.Tyrian;

/// <summary>What one dat field of an event means. Empty label = the engine never reads it.
/// A default instance (an event with no entry for that field) has null strings, so both
/// accessors go through null-tolerant views.</summary>
public readonly record struct EventField(string Label, string Hint = "")
{
    public bool Used => Label is { Length: > 0 };
    public string LabelText => Label ?? "";
    public string HintText => Hint ?? "";
}

/// <summary>Everything the editor knows about one event type.</summary>
public sealed class EventInfo
{
    public byte Type;
    public string Name = "";
    public EventGroup Group;
    public string Summary = "";
    // In on-disk field order: dat, dat2, dat3, dat5, dat6, dat4. Shown to the user in the
    // same dat1..dat6 order the engine's own names use.
    public EventField Dat, Dat2, Dat3, Dat4, Dat5, Dat6;

    public EventField Field(int i) => i switch
    {
        0 => Dat, 1 => Dat2, 2 => Dat3, 3 => Dat4, 4 => Dat5, _ => Dat6,
    };
}

public enum EventGroup
{
    Spawn,          // puts an object on the field
    Scroll,         // map movement and position
    Backdrop,       // starfield, draw order, filters, smoothies
    Enemies,        // commands to enemies already on the field
    Flow,           // jumps, skips, timers, level end
    Audio,          // music and sound
    Special,        // everything else
}

/// <summary>
/// The event vocabulary of JE_eventSystem (tyrian2.c), one entry per type the engine
/// handles, with per-field meanings for the editor's form. Types the engine ignores fall
/// back to a raw entry so nothing is uneditable.
/// </summary>
public static class EventCatalog
{
    public static readonly IReadOnlyList<EventInfo> All;
    private static readonly Dictionary<byte, EventInfo> ByType = new();

    public static EventInfo Get(byte type)
    {
        if (ByType.TryGetValue(type, out var e)) return e;
        return new EventInfo
        {
            Type = type, Name = $"Event {type}", Group = EventGroup.Special,
            Summary = "Not handled by JE_eventSystem — kept verbatim.",
            Dat = new("dat"), Dat2 = new("dat2"), Dat3 = new("dat3"),
            Dat4 = new("dat4"), Dat5 = new("dat5"), Dat6 = new("dat6"),
        };
    }

    public static string GroupName(EventGroup g) => g switch
    {
        EventGroup.Spawn => "Spawns",
        EventGroup.Scroll => "Map & scroll",
        EventGroup.Backdrop => "Backdrop & draw",
        EventGroup.Enemies => "Enemy commands",
        EventGroup.Flow => "Flow control",
        EventGroup.Audio => "Audio",
        _ => "Special",
    };

    /// <summary>The fields every ordinary spawn event shares (6/7/10/15/17/18/23).</summary>
    private static void SpawnFields(EventInfo e, bool bottom = false)
    {
        e.Dat = new("enemy id", "enemyDat entry to spawn (event 12: base id, spawns id..id+3)");
        e.Dat2 = new("x", "screen X. -99 = the entry's own start position, -200 = random 24..231");
        e.Dat3 = new("y-vel add", "added to the entry's Y velocity (eyc)");
        e.Dat4 = new("link", "link number stamped on the new enemy (0 = none)");
        e.Dat5 = bottom ? new("y offset", "added to the bottom-edge spawn Y")
                        : new("y offset", "added to the spawn Y (top edge is -28)");
        e.Dat6 = new("fixed y move", "fixedMoveY: extra Y px/frame tied to the scroll");
    }

    private static void LinkField(EventInfo e, string allValue = "0")
        => e.Dat4 = new("link", $"link number of the enemies addressed ({allValue} = every enemy)");

    static EventCatalog()
    {
        var list = new List<EventInfo>
        {
            new()
            {
                Type = 1, Name = "Starfield speed", Group = EventGroup.Backdrop,
                Summary = "Sets the starfield scroll speed.",
                Dat = new("speed", "star speed (2 is typical; higher = faster)"),
            },
            new()
            {
                Type = 2, Name = "Scroll speeds", Group = EventGroup.Scroll,
                Summary = "Sets all three background speeds and resets the slow-scroll delays. " +
                          "BG1 speed 0 stops the map (and releases an armed map-stop).",
                Dat = new("bg1 speed", "px/frame for the ground layer (0 = stopped)"),
                Dat2 = new("bg2 speed", "px/frame for the middle layer"),
                Dat3 = new("bg3 speed", "px/frame for the cloud layer"),
            },
            new()
            {
                Type = 3, Name = "Slow scroll preset", Group = EventGroup.Scroll,
                Summary = "BG1 at 1px every 3 frames, BG2 every 2, BG3 every frame.",
            },
            new()
            {
                Type = 4, Name = "Map stop (enemy-gated)", Group = EventGroup.Scroll,
                Summary = "Stops the backgrounds until the armed band has no enemies left.",
                Dat = new("band", "0/1 = ground band arms the release, 2 = sky, 3 = top"),
            },
            new()
            {
                Type = 5, Name = "Load shape banks", Group = EventGroup.Spawn,
                Summary = "Loads up to four enemy sprite banks into the level's slots.",
                Dat = new("slot 1 bank", "enemy shape bank 1..36 (0 = keep current)"),
                Dat2 = new("slot 2 bank", "0 = keep current"),
                Dat3 = new("slot 3 bank", "0 = keep current"),
                Dat4 = new("slot 4 bank", "0 = keep current"),
            },
            new() { Type = 6, Name = "Spawn ground enemy", Group = EventGroup.Spawn,
                Summary = "Creates one enemy on the ground band (25)." },
            new() { Type = 7, Name = "Spawn top enemy", Group = EventGroup.Spawn,
                Summary = "Creates one enemy on the top/foreground band (50)." },
            new()
            {
                Type = 8, Name = "Starfield off", Group = EventGroup.Backdrop,
                Summary = "Hides the starfield.",
            },
            new()
            {
                Type = 9, Name = "Starfield on", Group = EventGroup.Backdrop,
                Summary = "Shows the starfield.",
            },
            new() { Type = 10, Name = "Spawn ground2 enemy", Group = EventGroup.Spawn,
                Summary = "Creates one enemy on the second ground band (75)." },
            new()
            {
                Type = 11, Name = "End level", Group = EventGroup.Flow,
                Summary = "Starts the level-end sequence.",
                Dat = new("instant", "1 = end immediately, no fly-off animation"),
            },
            new()
            {
                Type = 12, Name = "Spawn 4x4 block", Group = EventGroup.Spawn,
                Summary = "Spawns entries id..id+3 as one 48x56 block of four 2x2 metasprites.",
            },
            new() { Type = 13, Name = "Enemies off", Group = EventGroup.Spawn,
                Summary = "Random level-enemy generation stops." },
            new() { Type = 14, Name = "Enemies on", Group = EventGroup.Spawn,
                Summary = "Random level-enemy generation resumes." },
            new() { Type = 15, Name = "Spawn sky enemy", Group = EventGroup.Spawn,
                Summary = "Creates one enemy on the sky band (0)." },
            new()
            {
                Type = 16, Name = "Voice announcement", Group = EventGroup.Audio,
                Summary = "Plays one of the nine announcer lines.",
                Dat = new("line", "1..9: Enemy approaching / Large enemy / Boss / Warning ..."),
            },
            new() { Type = 17, Name = "Spawn ground enemy from bottom", Group = EventGroup.Spawn,
                Summary = "Ground-band enemy entering from the bottom edge (y 190)." },
            new() { Type = 18, Name = "Spawn sky enemy from bottom", Group = EventGroup.Spawn,
                Summary = "Sky-band enemy entering from the bottom edge (y 190)." },
            new()
            {
                Type = 19, Name = "Enemy move", Group = EventGroup.Enemies,
                Summary = "Rewrites velocity for enemies by link number or slot range.",
                Dat = new("x-vel", "-99 = keep"),
                Dat2 = new("y-vel", "-99 = keep"),
                Dat3 = new("range", "0 = by link; 2 = slots 0-24, 1 = 25-49, 3 = 50-74, 99 = all; 80..89 = link from PL slot"),
                Dat4 = new("link", "link number (with range 0)"),
                Dat5 = new("cycle", "> 0: set animation frame"),
                Dat6 = new("fixed y move", "0 = keep, -99 = clear"),
            },
            new()
            {
                Type = 20, Name = "Enemy accel (cyclic)", Group = EventGroup.Enemies,
                Summary = "Sets cyclic acceleration on enemies by link.",
                Dat = new("x-accel", "-99 = keep"),
                Dat2 = new("y-accel", "-99 = keep"),
                Dat3 = new("PL slot", "80..89 = read link from a stored pick (event 75)"),
                Dat5 = new("cycle", "> 0: set animation frame"),
                Dat6 = new("ani", "> 0: set animation speed and restart"),
            },
            new() { Type = 21, Name = "BG3 over sprites", Group = EventGroup.Backdrop,
                Summary = "Draw the cloud layer over everything (background3over = 1)." },
            new() { Type = 22, Name = "BG3 normal", Group = EventGroup.Backdrop,
                Summary = "Cloud layer back under the sprites (background3over = 0)." },
            new() { Type = 23, Name = "Spawn top enemy at bottom", Group = EventGroup.Spawn,
                Summary = "Top-band enemy entering from the bottom edge (y 180)." },
            new()
            {
                Type = 24, Name = "Enemy animate", Group = EventGroup.Enemies,
                Summary = "Starts or reshapes the animation of enemies by link.",
                Dat = new("ani speed", "> 0: frames of animation"),
                Dat2 = new("start frame", "> 0: start cycle (also the loop floor)"),
                Dat3 = new("mode", "1 = play once and stop, 2 = animate when firing"),
                Dat4 = new("link", "link number addressed"),
            },
            new()
            {
                Type = 25, Name = "Enemy armor", Group = EventGroup.Enemies,
                Summary = "Sets armor for enemies by link (Galaga scales by difficulty).",
                Dat = new("armor", "new armor value"),
            },
            new()
            {
                Type = 26, Name = "Small enemy adjust", Group = EventGroup.Spawn,
                Summary = "Offsets subsequent 1x1 spawns by (-10,-7), for small sprites.",
                Dat = new("on", "1 = on, 0 = off"),
            },
            new()
            {
                Type = 27, Name = "Enemy accel reversal", Group = EventGroup.Enemies,
                Summary = "Sets the cyclic-acceleration reversal points by link.",
                Dat = new("x-rev", "-99 = keep"),
                Dat2 = new("y-rev", "-99 = keep"),
                Dat3 = new("filter", "1..16: palette-filter the enemy; 80..89 = PL slot"),
                Dat4 = new("link", "0 = every enemy"),
            },
            new() { Type = 28, Name = "Top enemies normal", Group = EventGroup.Backdrop,
                Summary = "Top band drawn under an over-layered BG3 (topEnemyOver = false)." },
            new() { Type = 29, Name = "Top enemies over", Group = EventGroup.Backdrop,
                Summary = "Top band drawn over an over-layered BG3 (topEnemyOver = true)." },
            new()
            {
                Type = 30, Name = "Scroll speeds (keep stop)", Group = EventGroup.Scroll,
                Summary = "Like event 2 but does not release a pending map stop.",
                Dat = new("bg1 speed", "px/frame (0 = stopped)"),
                Dat2 = new("bg2 speed", "px/frame"),
                Dat3 = new("bg3 speed", "px/frame"),
            },
            new()
            {
                Type = 31, Name = "Enemy fire override", Group = EventGroup.Enemies,
                Summary = "Rewrites turret fire frequencies for enemies by link.",
                Dat = new("freq 1", "shots every N frames for turret 1"),
                Dat2 = new("freq 2", ""),
                Dat3 = new("freq 3", ""),
                Dat4 = new("link", "99 = every enemy"),
                Dat5 = new("launch freq", "launcher cadence, if the enemy has one"),
            },
            new() { Type = 32, Name = "Spawn top enemy at bottom (fixed)", Group = EventGroup.Spawn,
                Summary = "Top-band enemy at y 190; ignores the y-offset field." },
            new()
            {
                Type = 33, Name = "Enemy-die spawn", Group = EventGroup.Enemies,
                Summary = "When the linked enemies die, they turn into this entry.",
                Dat = new("enemy id", "entry spawned on death (533 = random powerup roll)"),
                Dat4 = new("link", "link number addressed"),
            },
            new() { Type = 34, Name = "Music fade", Group = EventGroup.Audio,
                Summary = "Fades the music out." },
            new()
            {
                Type = 35, Name = "Play song", Group = EventGroup.Audio,
                Summary = "Switches to another song.",
                Dat = new("song", "1..41 (music.mus index)"),
            },
            new() { Type = 36, Name = "Ready to end level", Group = EventGroup.Flow,
                Summary = "Arms the level end; event 11 or a jump finishes it." },
            new()
            {
                Type = 37, Name = "Random enemy rate", Group = EventGroup.Spawn,
                Summary = "How often the levelEnemy list spawns (smaller = denser).",
                Dat = new("frequency", "frames between random spawns (default 96)"),
            },
            new()
            {
                Type = 38, Name = "Jump time (loop)", Group = EventGroup.Flow,
                Summary = "Jumps the event clock, usually backwards to loop a bombardment.",
                Dat = new("target time", "event-time to jump to"),
            },
            new()
            {
                Type = 39, Name = "Change link number", Group = EventGroup.Enemies,
                Summary = "Renames every enemy on one link number to another.",
                Dat = new("from link", ""),
                Dat2 = new("to link", ""),
            },
            new() { Type = 40, Name = "Continual damage", Group = EventGroup.Enemies,
                Summary = "Enemies take a point of damage every other frame." },
            new()
            {
                Type = 41, Name = "Clear enemies", Group = EventGroup.Enemies,
                Summary = "Removes enemies without explosions.",
                Dat = new("which", "0 = every slot, 1 = ground slots 0..24 only"),
            },
            new() { Type = 42, Name = "BG3 under sky enemies", Group = EventGroup.Backdrop,
                Summary = "Cloud layer over ground but under sky enemies (background3over = 2)." },
            new()
            {
                Type = 43, Name = "BG2 draw mode", Group = EventGroup.Backdrop,
                Summary = "Where the middle layer sits in the stack.",
                Dat = new("mode", "0/3 = early, 1 = over ground band, 2 = frontmost; other = hidden"),
            },
            new()
            {
                Type = 44, Name = "Screen filter", Group = EventGroup.Backdrop,
                Summary = "Hue filter / brightness fade over the playfield.",
                Dat = new("mode", "0 = off, 1 = on, 2 = fade"),
                Dat2 = new("hue", "palette filter (-99 = none)"),
                Dat3 = new("brightness", "additive brightness"),
                Dat4 = new("new hue", "hue faded towards (-99 = none; stored signed)"),
                Dat5 = new("fade step", "brightness change per frame"),
                Dat6 = new("start", "0 = begin the fade immediately"),
            },
            new()
            {
                Type = 45, Name = "Enemy-die spawn (arcade)", Group = EventGroup.Enemies,
                Summary = "Like 33, but only in 2-player / arcade modes.",
                Dat = new("enemy id", ""),
                Dat4 = new("link", ""),
            },
            new()
            {
                Type = 46, Name = "Difficulty shift", Group = EventGroup.Flow,
                Summary = "Nudges the live difficulty level up or down.",
                Dat = new("delta", "added to difficulty (clamped 1..10)"),
                Dat2 = new("filter", "0 = always; 1 applies only in 1-player full-game"),
            },
            new()
            {
                Type = 47, Name = "Enemy armor (direct)", Group = EventGroup.Enemies,
                Summary = "Sets armor for enemies by link, no difficulty scaling.",
                Dat = new("armor", ""),
            },
            new() { Type = 48, Name = "BG2 opaque", Group = EventGroup.Backdrop,
                Summary = "Middle layer drawn without transparency blending." },
            new()
            {
                Type = 49, Name = "Spawn custom ground object", Group = EventGroup.Spawn,
                Summary = "Spawns scratch entry 0 with art, bank and armor from this event.",
                Dat = new("sprite", "sprite index in the bank"),
                Dat2 = new("x", "screen X (-99 = entry default, -200 = random)"),
                Dat3 = new("bank", "enemy shape bank"),
                Dat4 = new("link", "link number stamped"),
                Dat5 = new("y offset", ""),
                Dat6 = new("armor", "0 = decoration"),
            },
            new()
            {
                Type = 50, Name = "Spawn custom sky object", Group = EventGroup.Spawn,
                Summary = "Event-49 form on the sky band.",
                Dat = new("sprite", ""), Dat2 = new("x", ""), Dat3 = new("bank", ""),
                Dat4 = new("link", ""), Dat5 = new("y offset", ""), Dat6 = new("armor", ""),
            },
            new()
            {
                Type = 51, Name = "Spawn custom top object", Group = EventGroup.Spawn,
                Summary = "Event-49 form on the top band.",
                Dat = new("sprite", ""), Dat2 = new("x", ""), Dat3 = new("bank", ""),
                Dat4 = new("link", ""), Dat5 = new("y offset", ""), Dat6 = new("armor", ""),
            },
            new()
            {
                Type = 52, Name = "Spawn custom ground2 object", Group = EventGroup.Spawn,
                Summary = "Event-49 form on the second ground band.",
                Dat = new("sprite", ""), Dat2 = new("x", ""), Dat3 = new("bank", ""),
                Dat4 = new("link", ""), Dat5 = new("y offset", ""), Dat6 = new("armor", ""),
            },
            new()
            {
                Type = 53, Name = "Force events while stopped", Group = EventGroup.Flow,
                Summary = "Keeps the event clock running while the map is stopped.",
                Dat = new("mode", "99 = off, anything else = on"),
            },
            new()
            {
                Type = 54, Name = "Jump time", Group = EventGroup.Flow,
                Summary = "Jumps the event clock to a target time (boss loops jump backwards).",
                Dat = new("target time", ""),
            },
            new()
            {
                Type = 55, Name = "Enemy X/Y accel", Group = EventGroup.Enemies,
                Summary = "Sets plain acceleration on enemies by link.",
                Dat = new("x-accel", "-99 = keep"),
                Dat2 = new("y-accel", "-99 = keep"),
                Dat3 = new("PL slot", "80..89 = link from stored pick"),
                Dat4 = new("link", "0 = every enemy"),
            },
            new() { Type = 56, Name = "Spawn ground2 enemy at bottom", Group = EventGroup.Spawn,
                Summary = "Second-ground-band enemy at y 190; ignores the y offset." },
            new()
            {
                Type = 57, Name = "Super enemy 254 jump", Group = EventGroup.Flow,
                Summary = "Arms: when the link-254 enemy dies, jump to this time.",
                Dat = new("target time", ""),
            },
            new()
            {
                Type = 58, Name = "Set enemy launch", Group = EventGroup.Enemies,
                Summary = "Changes what the linked enemies launch.",
                Dat = new("launch type", "enemyDat id launched (or special 251..255)"),
                Dat4 = new("link", "99 = every enemy"),
            },
            new()
            {
                Type = 59, Name = "Replace enemy", Group = EventGroup.Enemies,
                Summary = "Linked enemies are replaced in place by another entry.",
                Dat = new("enemy id", "replacement entry"),
                Dat4 = new("link", "0 = every enemy"),
            },
            new()
            {
                Type = 60, Name = "Assign special flag", Group = EventGroup.Enemies,
                Summary = "The linked enemies set a global flag when killed.",
                Dat = new("flag", "1..10"),
                Dat2 = new("set to", "1 = set the flag true, 0 = false"),
                Dat4 = new("link", ""),
            },
            new()
            {
                Type = 61, Name = "Skip if flag", Group = EventGroup.Flow,
                Summary = "Skips events if a global flag has the given value.",
                Dat = new("flag", "1..10"),
                Dat2 = new("value", "1 = true, 0 = false"),
                Dat3 = new("skip", "events skipped when it matches"),
            },
            new()
            {
                Type = 62, Name = "Sound effect", Group = EventGroup.Audio,
                Summary = "Plays a sound sample.",
                Dat = new("sound", "1..31 (tyrian.snd index)"),
            },
            new()
            {
                Type = 63, Name = "Skip if 1-player", Group = EventGroup.Flow,
                Summary = "Skips events unless playing 2-player / arcade.",
                Dat = new("skip", "events skipped"),
            },
            new()
            {
                Type = 64, Name = "Smoothie", Group = EventGroup.Backdrop,
                Summary = "Water/lava/ice feedback filters and the screen flip.",
                Dat = new("which", "1..9 (5 shares data with 3; 9 = screen flip)"),
                Dat2 = new("mode", "on/off or variant, per filter"),
                Dat3 = new("data", "filter parameter"),
            },
            new()
            {
                Type = 65, Name = "BG3 follow BG1", Group = EventGroup.Backdrop,
                Summary = "Locks the cloud layer to the ground layer's scroll.",
                Dat = new("mode", "0 = locked, 1 = free"),
            },
            new()
            {
                Type = 66, Name = "Skip if difficulty <=", Group = EventGroup.Flow,
                Summary = "Skips events at or below a difficulty.",
                Dat = new("difficulty", "0..10"),
                Dat2 = new("skip", "events skipped"),
            },
            new()
            {
                Type = 67, Name = "Level timer", Group = EventGroup.Flow,
                Summary = "Starts/stops the countdown; expiry jumps the event clock.",
                Dat = new("on", "1 = start, 0 = stop"),
                Dat2 = new("target time", "event-time jumped to at zero"),
                Dat3 = new("seconds", "countdown length"),
            },
            new()
            {
                Type = 68, Name = "Replace enemy (any band)", Group = EventGroup.Enemies,
                Summary = "Same as 59; kept as its own type by the engine.",
                Dat = new("enemy id", ""),
                Dat4 = new("link", "0 = every enemy"),
            },
            new()
            {
                Type = 69, Name = "Invulnerability", Group = EventGroup.Special,
                Summary = "Gives every player this many invulnerable ticks.",
                Dat = new("ticks", ""),
            },
            new()
            {
                Type = 70, Name = "Jump if enemies gone", Group = EventGroup.Flow,
                Summary = "Jumps when no enemy with the named links is left (the boss gate).",
                Dat = new("target time", ""),
                Dat2 = new("link a", "0 = test links 1..19 instead"),
                Dat3 = new("link b", "0 = unused"),
                Dat4 = new("link c", "0 = unused"),
            },
            new()
            {
                Type = 71, Name = "Jump if map top", Group = EventGroup.Flow,
                Summary = "Jumps once the BG1 map position has scrolled far enough.",
                Dat = new("target time", ""),
                Dat2 = new("map pos", "threshold, map px * 2"),
            },
            new()
            {
                Type = 72, Name = "BG3 layout variant", Group = EventGroup.Backdrop,
                Summary = "Alternative BG3-locked registration (background3x1b).",
                Dat = new("on", "1 = on"),
            },
            new()
            {
                Type = 73, Name = "Sky enemies over all", Group = EventGroup.Backdrop,
                Summary = "Sky band drawn last of all.",
                Dat = new("on", "1 = on, 0 = off"),
            },
            new()
            {
                Type = 74, Name = "Enemy bounce params", Group = EventGroup.Enemies,
                Summary = "Sets the bounce box for enemies by link.",
                Dat = new("x max", "-99 = keep"),
                Dat2 = new("y max", "-99 = keep"),
                Dat4 = new("link", "0 = every enemy"),
                Dat5 = new("x min", "-99 = keep"),
                Dat6 = new("y min", "-99 = keep"),
            },
            new()
            {
                Type = 75, Name = "Random link pick", Group = EventGroup.Flow,
                Summary = "Stores a random live link from a range into a PL slot (80..89).",
                Dat = new("link from", ""),
                Dat2 = new("link to", ""),
                Dat3 = new("slot", "80..89"),
                Dat4 = new("fail skip", "> 0: events skipped when no candidate is alive"),
            },
            new() { Type = 76, Name = "Return active", Group = EventGroup.Flow,
                Summary = "Arms the engine's return-jump (SQUADRON-style route loop)." },
            new()
            {
                Type = 77, Name = "Set map position", Group = EventGroup.Scroll,
                Summary = "Teleports the BG1/BG2 map positions.",
                Dat = new("bg1 pos", "map px * 2"),
                Dat2 = new("bg2 pos", "map px * 2 (0 = same as bg1)"),
            },
            new() { Type = 78, Name = "Galaga fire chance up", Group = EventGroup.Special,
                Summary = "Raises the Galaga-mode fire chance a step (capped)." },
            new()
            {
                Type = 79, Name = "Boss health bars", Group = EventGroup.Special,
                Summary = "Shows armor bars for up to two link numbers.",
                Dat = new("link a", "0 = none"),
                Dat2 = new("link b", "0 = none"),
            },
            new()
            {
                Type = 80, Name = "Skip if 2-player", Group = EventGroup.Flow,
                Summary = "Skips events when playing 2-player.",
                Dat = new("skip", "events skipped"),
            },
            new()
            {
                Type = 81, Name = "BG2 wrap", Group = EventGroup.Scroll,
                Summary = "Loops a strip of the BG2 map (the conveyor effect).",
                Dat = new("wrap at", "map px * 2"),
                Dat2 = new("wrap to", "map px * 2"),
            },
            new()
            {
                Type = 82, Name = "Give special weapon", Group = EventGroup.Special,
                Summary = "Hands the player a special weapon.",
                Dat = new("special id", ""),
            },
            new()
            {
                Type = 83, Name = "Map stop (T2000)", Group = EventGroup.Scroll,
                Summary = "Tyrian 2000's copy of the enemy-gated map stop.",
                Dat = new("band", "0/1 = ground, 2 = sky, 3 = top"),
            },
            new()
            {
                Type = 84, Name = "Timed battle: timer", Group = EventGroup.Flow,
                Summary = "Event 67, applied only in Timed Battle mode.",
                Dat = new("on", "1 = start"),
                Dat2 = new("target time", ""),
                Dat3 = new("seconds", ""),
            },
            new()
            {
                Type = 85, Name = "Timed battle: die spawn", Group = EventGroup.Enemies,
                Summary = "Event 33, applied only in Timed Battle mode.",
                Dat = new("enemy id", ""),
                Dat4 = new("link", ""),
            },
            new()
            {
                Type = 99, Name = "Random explosions", Group = EventGroup.Backdrop,
                Summary = "Ambient explosions over the playfield.",
                Dat = new("on", "1 = on, 0 = off"),
            },
        };

        foreach (var e in list)
        {
            switch (e.Type)
            {
                case 6 or 7 or 10 or 15: SpawnFields(e); break;
                case 17 or 18 or 23: SpawnFields(e, bottom: true); break;
                case 32 or 56:
                    SpawnFields(e, bottom: true);
                    e.Dat5 = default;   // these two ignore the offset
                    break;
                case 12:
                    SpawnFields(e);
                    e.Dat6 = new("band", "2 = sky, 3 = top, 4 = ground2, else ground");
                    break;
                case 25 or 47: LinkField(e); break;
                case 19 or 20: e.Dat4 = new("link", "link number (see range)"); break;
            }
            ByType[e.Type] = e;
        }
        All = list;
    }

    /// <summary>Types that create an object (for the editor's spawn niceties).</summary>
    public static bool IsSpawnType(byte type) => ObjectPlacer.IsSpawn(type, out _, out _);

    /// <summary>
    /// The short label a level-wide event carries when drawn as a line across the map —
    /// the moments that change what the whole level is doing. Null = not a flow event.
    /// </summary>
    public static string? FlowLabel(in EventRec ev) => ev.Type switch
    {
        2 or 30 => $"scroll {ev.Dat}/{ev.Dat2}/{ev.Dat3}",
        3 => "slow scroll",
        4 or 83 => "map stop" + (ev.Dat == 2 ? " (sky)" : ev.Dat == 3 ? " (top)" : ""),
        8 => "starfield off",
        9 => "starfield on",
        11 => "END LEVEL",
        36 => "ready to end",
        34 => "music fade",
        35 => $"song {ev.Dat}",
        38 => $"loop -> t{unchecked((ushort)ev.Dat)}",
        54 => $"jump -> t{unchecked((ushort)ev.Dat)}",
        44 => ev.Dat == 0 ? "filter off" : "filter",
        53 => ev.Dat == 99 ? "forced events off" : "forced events",
        67 => ev.Dat == 1 ? $"timer {ev.Dat3}s -> t{unchecked((ushort)ev.Dat2)}" : "timer off",
        77 => "map position set",
        _ => null,
    };

    /// <summary>A one-line description of an event for list rows.</summary>
    public static string Describe(in EventRec ev, EnemyData? ed)
    {
        var info = Get(ev.Type);
        if (IsSpawnType(ev.Type) && ed != null)
        {
            var s = ObjectPlacer.ResolveSpawn(ev, ed);
            string what = ev.Type is >= 49 and <= 52
                ? $"sprite {s.Sprite} bank {s.ShapeBank}"
                : ev.Type == 12 ? $"block {ev.Dat}..{ev.Dat + 3}" : $"enemy {s.EnemyId}";
            string x = ev.Dat2 == -99 ? "default x" : ev.Dat2 == -200 ? "random x" : $"x {ev.Dat2}";
            return ev.Dat4 != 0 ? $"{what}, {x}, link {ev.Dat4}" : $"{what}, {x}";
        }
        return info.Summary;
    }
}
