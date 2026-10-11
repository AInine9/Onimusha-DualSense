namespace OnimushaDualSense;

readonly record struct AudioTimingSnapshot(double MaxGapMs, double MaxRenderMs, double MaxLatencyMs);

// The callback only updates numbers: no logging, file I/O, or additional locks.
sealed class AudioTimingHistory
{
    double previousStart = double.NaN;
    double maxGap, maxRender, maxLatency, pendingGap, pendingRender;
    internal static void Maximum(ref double target, double value)
    {
        double old = Volatile.Read(ref target);
        while (value > old)
        {
            double seen = Interlocked.CompareExchange(ref target, value, old);
            if (seen == old) return;
            old = seen;
        }
    }
    public void Started(double now)
    {
        if (double.IsFinite(previousStart))
        {
            double gap = Math.Max(0, now - previousStart);
            Maximum(ref maxGap, gap); Maximum(ref pendingGap, gap);
        }
        previousStart = now;
    }
    public void Completed(double began, double ended, double latency)
    {
        double duration = Math.Max(0, ended - began);
        Maximum(ref maxRender, duration); Maximum(ref pendingRender, duration);
        if (double.IsFinite(latency)) Maximum(ref maxLatency, latency);
    }
    public (double Gap, double Render) Consume() =>
        (Interlocked.Exchange(ref pendingGap, 0), Interlocked.Exchange(ref pendingRender, 0));
    public AudioTimingSnapshot Snapshot => new(Volatile.Read(ref maxGap) * 1000,
        Volatile.Read(ref maxRender) * 1000, Volatile.Read(ref maxLatency) * 1000);
}
