namespace OnimushaDualSense;

// Only selected with an explicit PARRY action-state confirmation. Ordinary guard,
// BLOCK/deflect and EVADE never select this renderer from a sound ID alone.
static class ParryFriction
{
    public static float[] Convert(float[] source)
    {
        if (source.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite parry source");
        if (source.Length == 0 || source.All(v => Math.Abs(v) < .00001f)) return new float[96];
        int onset = SoundHaptics.Onset(source);
        int frames = Math.Min(source.Length, onset + 384000); // Source duration, bounded by game stop/action exit and 8s ceiling.
        var values = new double[frames];
        double dc = 0, low100 = 0, low700 = 0, low3500 = 0;
        double body = 0, scrape = 0, grain = 0, phase = 0, outputDc = 0, peak = 0;
        static double Coef(double hz) => 1 - Math.Exp(-2 * Math.PI * hz / 48000);
        static double Env(double current, double sample)
        {
            double target = Math.Abs(sample);
            return current + (target - current) * (target > current ? .015 : .001);
        }
        double a30 = Coef(30), a100 = Coef(100), a700 = Coef(700), a3500 = Coef(3500);
        for (int i = 0; i < frames; i++)
        {
            double x = source[i];
            dc += a30 * (x - dc); low100 += a100 * (x - low100);
            low700 += a700 * (x - low700); low3500 += a3500 * (x - low3500);
            body = Env(body, low700 - low100);
            scrape = Env(scrape, low3500 - low700);
            grain = Env(grain, x - low3500);
            double t = Math.Max(0, i - onset) / 48000.0;
            double energy = .7 * body + scrape + .35 * grain;
            // Modest compression lifts quieter sustained texture without normalizing
            // every tail to full strength. The gate prevents amplification of silence.
            double gate = Math.Clamp((energy - .00015) / .00085, 0, 1);
            double envelope = .06 * Math.Pow(energy / .06, .65) * gate;
            double brightness = (scrape + grain) / (body + scrape + grain + 1e-9);
            phase += 2 * Math.PI * (180 + 110 * brightness) / 48000;
            double carrier = .78 * Math.Sin(phase) + .22 * Math.Sin(phase * 1.37);
            double impact = (.5 * (low700 - dc) + body * Math.Sin(2 * Math.PI * 140 * t)) * Math.Exp(-t / .09);
            double friction = 1.9 * envelope * carrier * Math.Min(1, t / .020);
            double v = impact + friction;
            outputDc += a30 * (v - outputDc);
            double edge = Math.Clamp((i - onset) / 72.0, 0, 1);
            double release = Math.Min(1, (frames - 1 - i) / 720.0);
            values[i] = (v - outputDc) * edge * release;
            peak = Math.Max(peak, Math.Abs(values[i]));
        }
        double gain = peak > 0 ? Math.Min(7, .82 / peak) : 0;
        var stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++) stereo[2 * i] = stereo[2 * i + 1] = (float)(values[i] * gain);
        return stereo;
    }
}
