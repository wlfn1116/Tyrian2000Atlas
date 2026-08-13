using System.Numerics;

namespace T2A.Tyrian;

/// <summary>
/// The flight path a spawn will fly, computed from the same kinematics GameSim ported out
/// of JE_moveEnemy: per-tick velocity, cyclic acceleration with its reversal dance, the
/// spawn event's velocity add and fixed Y move, and the player-chase acceleration (which is
/// RNG-gated in the engine; here it applies at its expected rate, so a chaser's path reads
/// as the curve it will statistically fly). Points are canvas-space offsets from the spawn
/// marker: map-band enemies ride the terrain (their tempBackMove cancels against the
/// scroll), sky-band enemies drift up the map at the scroll speed.
/// </summary>
public static class PathPreview
{
    public const int TicksPerPoint = 2;

    /// <summary>
    /// Offsets from the spawn anchor, one per <see cref="TicksPerPoint"/> ticks, until the
    /// enemy leaves the keep-alive band or <paramref name="maxTicks"/> runs out.
    /// </summary>
    public static List<Vector2> Compute(in EnemyDat dat, in EventRec ev, int band,
        int backMove, int maxTicks = 300)
    {
        var pts = new List<Vector2>(maxTicks / TicksPerPoint + 1);
        bool custom = ev.Type is >= 49 and <= 52;

        // ---- JE_makeEnemy's motion init (randomized start offsets treated as centred) ----
        int ex = dat.StartX + 1, ey0 = dat.StartY + 1;
        int exc = dat.XMove, eyc = dat.YMove;
        int excc = dat.XCAccel, eycc = dat.YCAccel;
        int exccw = Math.Abs(excc), exccwmax = exccw;
        int eyccw = Math.Abs(eycc), eyccwmax = eyccw;
        int exccadd = excc > 0 ? 1 : -1;
        int eyccadd = eycc > 0 ? 1 : -1;
        int exrev = dat.XRev == 0 ? 100 : dat.XRev == -99 ? 0 : dat.XRev;
        int eyrev = dat.YRev == 0 ? 100 : dat.YRev == -99 ? 0 : dat.YRev;
        int xaccel = dat.XAccel, yaccel = dat.YAccel;

        // The spawn event's own contributions (JE_createNewEventEnemy): dat3 adds to the Y
        // velocity, dat6 is the fixed Y move — except where those bytes mean other things.
        int fixedmovey = 0;
        if (!custom)
        {
            eyc += ev.Dat3;
            if (ev.Type != 12) fixedmovey = ev.Dat6;
        }

        int ey = ey0;
        // Chase accel at its expected rate: engine chance is P((xaccel-89) > rng%11).
        float chaseX = xaccel != 0 ? Math.Min(Math.Max(xaccel - 89, 0), 11) / 11f : 0f;
        float chaseY = yaccel != 0 ? Math.Min(Math.Max(yaccel - 89, 0), 11) / 11f : 0f;
        float accX = 0f, accY = 0f;
        const int playerX = 130, playerY = 155;   // the phantom the chase curves toward

        static sbyte S8(int v) => unchecked((sbyte)v);
        float dx = 0f, dy = 0f;

        for (int t = 0; t < maxTicks; t++)
        {
            // cyclic acceleration — verbatim from the sim
            if (excc != 0 && --exccw <= 0)
            {
                if (exc == exrev) { excc = S8(-excc); exrev = -exrev; exccadd = -exccadd; }
                else
                {
                    exc = S8(exc + exccadd);
                    exccw = exccwmax;
                    if (exc == exrev) { excc = S8(-excc); exrev = -exrev; exccadd = -exccadd; }
                }
            }
            if (eycc != 0 && --eyccw <= 0)
            {
                if (eyc == eyrev) { eycc = S8(-eycc); eyrev = -eyrev; eyccadd = -eyccadd; }
                else
                {
                    eyc = S8(eyc + eyccadd);
                    eyccw = eyccwmax;
                    if (eyc == eyrev) { eycc = S8(-eycc); eyrev = -eyrev; eyccadd = -eyccadd; }
                }
            }

            // player-chase acceleration at its expected rate
            if (chaseX > 0f)
            {
                accX += chaseX;
                while (accX >= 1f)
                {
                    accX -= 1f;
                    if (playerX > ex) { if (exc < xaccel - 89) exc++; }
                    else if (exc >= 0 || -exc < xaccel - 89) exc--;
                }
            }
            if (chaseY > 0f)
            {
                accY += chaseY;
                while (accY >= 1f)
                {
                    accY -= 1f;
                    if (playerY > ey) { if (eyc < yaccel - 89) eyc++; }
                    else if (eyc >= 0 || -eyc < yaccel - 89) eyc--;
                }
            }

            ey += fixedmovey;
            ex += exc;
            ey += eyc;
            dx += exc;
            dy += eyc + fixedmovey;
            // Sky-band enemies are screen-glued: the map slides down past them, so on the
            // canvas they climb at the scroll speed. Map bands ride the terrain (their
            // engine-side tempBackMove cancels against the scroll) and need nothing.
            if (band == 0) dy -= backMove;

            if (t % TicksPerPoint == 0) pts.Add(new Vector2(dx, dy));
            // The engine's keep-alive band, roughly: past it the enemy is culled.
            if (ex < -100 || ex > 380 || ey < -140 || ey > 210) break;
        }
        return pts;
    }
}
