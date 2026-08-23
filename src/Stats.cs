using System.Diagnostics;
using System.Threading;

namespace CullExpiredFix
{
    public static class Stats
    {
        public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public static long Entered;
        public static long Skipped;
        public static long Scanned;
        public static long VanillaScans;
        public static long ScanTicks;
        public static long KeysWalked;
        public static long ChunksRemoved;
        public static long Fallbacks;
        public static long MaxScanTicks;

        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void AddScan(long startTicks, int keys, int removed, bool vanilla)
        {
            long d = Stopwatch.GetTimestamp() - startTicks;
            Interlocked.Increment(ref Scanned);
            if (vanilla)
            {
                Interlocked.Increment(ref VanillaScans);
            }
            Interlocked.Add(ref ScanTicks, d);
            Interlocked.Add(ref KeysWalked, keys);
            Interlocked.Add(ref ChunksRemoved, removed);

            long cur = Interlocked.Read(ref MaxScanTicks);
            while (d > cur)
            {
                long prev = Interlocked.CompareExchange(ref MaxScanTicks, d, cur);
                if (prev == cur)
                {
                    break;
                }
                cur = prev;
            }
        }

        public static double Ms(long ticks)
        {
            return ticks * TicksToMs;
        }

        public static string Describe()
        {
            long entered = Interlocked.Read(ref Entered);
            long skipped = Interlocked.Read(ref Skipped);
            long scanned = Interlocked.Read(ref Scanned);
            long vanilla = Interlocked.Read(ref VanillaScans);
            long ticks = Interlocked.Read(ref ScanTicks);
            long keys = Interlocked.Read(ref KeysWalked);
            long removed = Interlocked.Read(ref ChunksRemoved);
            long fallbacks = Interlocked.Read(ref Fallbacks);
            long max = Interlocked.Read(ref MaxScanTicks);

            double totalMs = Ms(ticks);
            double perScan = (scanned > 0L) ? totalMs / scanned : 0.0;
            double nsPerKey = (keys > 0L) ? totalMs * 1e6 / keys : 0.0;
            double skipPct = (entered > 0L) ? 100.0 * skipped / entered : 0.0;

            string removedText = (vanilla > 0L) ? (removed + " (fast path only)") : removed.ToString();

            return string.Format(
                "calls={0} skipped={1} ({2:F1}%) scans={3} vanillaScans={4} scanTotal={5:F0}ms mean={6:F2}ms max={7:F2}ms perKey={8:F0}ns removed={9} fallbacks={10}",
                entered, skipped, skipPct, scanned, vanilla, totalMs, perScan, Ms(max), nsPerKey, removedText, fallbacks);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref Entered, 0L);
            Interlocked.Exchange(ref Skipped, 0L);
            Interlocked.Exchange(ref Scanned, 0L);
            Interlocked.Exchange(ref VanillaScans, 0L);
            Interlocked.Exchange(ref ScanTicks, 0L);
            Interlocked.Exchange(ref KeysWalked, 0L);
            Interlocked.Exchange(ref ChunksRemoved, 0L);
            Interlocked.Exchange(ref Fallbacks, 0L);
            Interlocked.Exchange(ref MaxScanTicks, 0L);
        }
    }
}
