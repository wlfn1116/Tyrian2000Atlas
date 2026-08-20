namespace T2A.Tyrian;

/// <summary>
/// The BG1 scroll integrated over an event list, exactly as ObjectPlacer integrates it:
/// piecewise segments of (event time, accumulated px, px per time unit, raw backMove).
/// One walk serves the editor's ruler, the flow-line overlays in both canvases, and the
/// time-for-position inversions.
/// </summary>
public static class ScrollWalk
{
    public readonly record struct Seg(int Time, double Cum, double Rate, int Move);

    public static List<Seg> Build(IReadOnlyList<EventRec> events)
    {
        var segs = new List<Seg> { new(0, 0, 1, 1) };
        int backMove = 1;
        double cum = 0;
        int last = 0;
        bool ended = false;
        foreach (var e in events)
        {
            int dt = e.Time - last;
            if (dt > 0 && !ended)
            {
                cum += (backMove > 0 ? 1 : 0) * dt;
                last = e.Time;
            }
            if (e.Type == 11) ended = true;
            if (e.Type is 2 or 30)
            {
                backMove = e.Dat;
                segs.Add(new Seg(e.Time, cum, backMove > 0 ? 1 : 0, backMove));
            }
            else if (e.Type == 3)
            {
                backMove = 1;
                segs.Add(new Seg(e.Time, cum, 1, 1));
            }
        }
        return segs;
    }

    public static double ScrollAt(List<Seg> segs, int time)
    {
        double cum = 0; int lastT = 0; double rate = 1;
        foreach (var (t, c, r, _) in segs)
        {
            if (t > time) break;
            cum = c; lastT = t; rate = r;
        }
        return cum + rate * (time - lastT);
    }

    /// <summary>Earliest event time whose accumulated scroll reaches <paramref name="scroll"/>.</summary>
    public static int TimeFor(List<Seg> segs, double scroll)
    {
        if (scroll < 0) return 1;
        for (int i = segs.Count - 1; i >= 0; i--)
        {
            var (t, c, r, _) = segs[i];
            if (c <= scroll + 0.0001)
                return r > 0 ? (int)(t + (scroll - c) / r) : t;
        }
        return 1;
    }

    /// <summary>BG1 px per tick in effect at an event time.</summary>
    public static int MoveAt(List<Seg> segs, int time)
    {
        int move = 1;
        foreach (var (t, _, _, m) in segs)
        {
            if (t > time) break;
            move = m;
        }
        return Math.Max(0, move);
    }
}
