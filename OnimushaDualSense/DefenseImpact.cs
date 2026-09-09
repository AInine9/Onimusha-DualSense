namespace OnimushaDualSense;

// Translate the recorded contact's body and ringing envelope, without adding
// parry friction or an arbitrary exponential tail to ordinary guard/deflect.
static class DefenseImpact
{
    internal static float[] Convert(float[] source, bool deflect)
    {
        if (source.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Non-finite defense source");
        if (source.Length == 0 || source.All(x => Math.Abs(x) < .00001f)) return new float[96];
        int onset = SoundHaptics.Onset(source);
        int count = Math.Min(source.Length, onset + (int)((deflect ? 3 : .85) * 48000));
        var output = new float[count * 2];
        double low40 = 0, low220 = 0, low1400 = 0, body = 0, ring = 0, dc = 0;
        static double A(double hz) => 1 - Math.Exp(-2 * Math.PI * hz / 48000);
        static double Envelope(double current, double value) => current + (Math.Abs(value) - current) * (Math.Abs(value) > current ? .035 : .0012);
        double a40 = A(40), a220 = A(220), a1400 = A(1400), a30 = A(30);
        for (int i = 0; i < count; i++)
        {
            double x = source[i]; low40 += a40 * (x - low40); low220 += a220 * (x - low220); low1400 += a1400 * (x - low1400);
            body = Envelope(body, low1400 - low40); ring = Envelope(ring, x - low1400);
            double t = Math.Max(0, i - onset) / 48000.0;
            double energy = body + ring;
            double gate = Math.Clamp((energy - .0001) / .0005, 0, 1);
            // A fixed bounded gain preserves differences between attack strengths.
            double y = 5 * gate * (.7 * (low220 - low40) + 1.1 * body * Math.Sin(2 * Math.PI * (deflect ? 170 : 115) * t)
                + (deflect ? 1.25 : .8) * ring * Math.Sin(2 * Math.PI * (deflect ? 270 : 220) * t));
            dc += a30 * (y - dc);
            double edge = Math.Clamp((i - onset) / 72.0, 0, 1) * Math.Min(1, (count - i - 1) / 720.0);
            output[2 * i] = output[2 * i + 1] = (float)(Math.Clamp(y - dc, -.88, .88) * edge);
        }
        return output;
    }
}
