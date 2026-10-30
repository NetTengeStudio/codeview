using System;
using System.Threading;

namespace ScpItdMod.Weather.Material;

// Splits a loop over independent indices across the caller and a few helper threads. The result never depends on how the work lands on threads (every index is written by exactly one range),
// and a call costs no allocation. A helper spins for a moment after its work, because the next phase of a substep follows within microseconds, and leaves for good after a quiet second, so nothing outlives the snow it served.
internal sealed class SnowFork
{
    internal delegate void Range(int begin, int end);
    private const int SpinCount = 1500, RetireMilliseconds = 1000;
    private sealed class Helper
    {
        internal readonly ManualResetEventSlim Start = new(false, SpinCount);
        internal int Alive, Index;
    }
    private readonly Helper[] helpers;
    private readonly ManualResetEventSlim done = new(false, SpinCount);
    private Range body; private int count, tasks, pending; private Exception failure;
    internal SnowFork(int count)
    {
        helpers = new Helper[Math.Max(0, count)];
        for (int i = 0; i < helpers.Length; i++) helpers[i] = new Helper { Index = i + 1 };
    }
    internal static int DefaultHelpers => Math.Clamp(Environment.ProcessorCount - 1, 0, 7);
    internal int Helpers => helpers.Length;
    private void Loop(object state)
    {
        Helper helper = (Helper)state;
        while (true)
        {
            if (!helper.Start.Wait(RetireMilliseconds))
            {
                Volatile.Write(ref helper.Alive, 0);
                if (!helper.Start.IsSet || Interlocked.CompareExchange(ref helper.Alive, 1, 0) != 0) return;
            }
            helper.Start.Reset();
            try { body(count * helper.Index / tasks, count * (helper.Index + 1) / tasks); }
            catch (Exception exception) { Interlocked.CompareExchange(ref failure, exception, null); }
            if (Interlocked.Decrement(ref pending) == 0) done.Set();
        }
    }
    private void Wake(Helper helper)
    {
        helper.Start.Set();
        if (Volatile.Read(ref helper.Alive) == 0 && Interlocked.CompareExchange(ref helper.Alive, 1, 0) == 0)
            new Thread(Loop) { IsBackground = true, Name = "Snow solver helper" }.Start(helper);
    }
    // Runs range(begin, end) over [0, total) in pieces of at least grain indices; with too little work for a second piece it runs inline.
    internal void Run(int total, int grain, Range range)
    {
        int share = Math.Min(helpers.Length + 1, total / Math.Max(1, grain));
        if (share <= 1) { range(0, total); return; }
        body = range; count = total; tasks = share; failure = null; pending = share - 1;
        for (int i = 1; i < share; i++) Wake(helpers[i - 1]);
        // The event is always consumed here, even when the caller's own range throws: a helper sets it after its last write, so a call never returns while a helper still runs and never leaves a stale signal for the next one.
        try { range(0, total / share); }
        finally { done.Wait(); done.Reset(); body = null; }
        if (failure != null) { Exception thrown = failure; failure = null; throw new InvalidOperationException("Snow solver range failed", thrown); }
    }
}
