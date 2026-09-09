using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

static class HapticAudit
{
    public static void VerifyPrepared()
    {
        var loaded = PreparedWaves.Load();
        using var actual = loaded.Samples;
        using var expected = new SampleStore();
        var reference = new ExtendedEffects(expected);
        if (actual.Count != expected.Count) throw new InvalidDataException("Prepared sample count differs");
        var timings = new List<double>();
        foreach (string key in expected.Keys)
        {
            long start = Stopwatch.GetTimestamp(); var data = actual[key];
            timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (!System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()).SequenceEqual(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected[key].AsSpan())))
                throw new InvalidDataException("Prepared waveform differs: " + key);
        }
        void Same(string name, object a, object b)
        {
            if (JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(a), System.Text.Json.JsonSerializer.SerializeToNode(b))) return;
            Files.Save(Files.Data("routing-expected.json"), a); Files.Save(Files.Data("routing-actual.json"), b);
            throw new InvalidDataException("Prepared routing differs: " + name);
        }
        Same("Available", reference.Available, loaded.Effects.Available); Same("Variants", reference.Variants, loaded.Effects.Variants);
        Same("SoundChoices", reference.SoundChoices, loaded.Effects.SoundChoices); Same("SoundInfo", reference.SoundInfo, loaded.Effects.SoundInfo);
        Same("SoundEvents", reference.SoundEvents, loaded.Effects.SoundEvents); Same("CutSamples", reference.CutSamples, loaded.Effects.CutSamples);
        Same("ParrySamples", reference.ParrySamples, loaded.Effects.ParrySamples);
        timings.Sort();
        var index = PreparedWaves.Prepare();
        var reachable = reference.Available.Values.Where(e => !reference.Variants.ContainsKey(e.Id)).Select(e => e.SampleId).ToHashSet();
        reachable.UnionWith(reference.Variants.Values.SelectMany(v => v));
        reachable.UnionWith(reference.CutSamples.Where(p => reachable.Contains(p.Key)).Select(p => p.Value).ToArray());
        reachable.UnionWith(reference.ParrySamples.Where(p => reachable.Contains(p.Key)).Select(p => p.Value).ToArray());
        Files.Save(Files.Data("prepared-audit.json"), new { verified_samples = actual.Count, routing_identical = true,
            shared_wav_files = index.Waves.Values.Select(w => w.File).Distinct().Count(),
            wav_bytes = index.Waves.Values.DistinctBy(w => w.File).Sum(w => 56L + w.Length * 4L),
            reachable_samples = reachable.Count, unreferenced_samples = actual.Keys.Except(reachable).Count(),
            read_p95_ms = timings[(int)(timings.Count * .95)], read_max_ms = timings[^1] });
        Console.WriteLine($"PASS: {actual.Count} prepared waveforms are bit exact at unity gain; all routing metadata matches.");
    }
    public static void Run()
    {
        var watch = Stopwatch.StartNew();
        using var samples = new SampleStore();
        var effects = new ExtendedEffects(samples);
        double seconds = watch.Elapsed.TotalSeconds;
        SampleStore.FinishLoading();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var readMilliseconds = new List<double>();
        foreach (string key in samples.Keys)
        {
            long start = Stopwatch.GetTimestamp();
            var data = samples[key];
            readMilliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            hashes[key] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan())));
        }
        readMilliseconds.Sort();
        Files.Save(Files.Data("wave-hashes.json"), hashes);
        var rows = new List<object>();
        foreach (var source in SoundCatalog.Entries)
        {
            string id = source.Id + (source.Family == "footsteps" ? "_left" : "");
            if (!effects.Variants.TryGetValue(id, out var variants)) continue;
            foreach (string variant in variants)
            {
                var data = samples[variant];
                double peak = 0, sum = 0, dc = 0, step = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    if (!float.IsFinite(data[i])) throw new InvalidDataException("Non-finite rendered waveform");
                    peak = Math.Max(peak, Math.Abs(data[i])); sum += (double)data[i] * data[i]; dc += data[i];
                    if (i >= 2) step = Math.Max(step, Math.Abs(data[i] - data[i - 2]));
                }
                rows.Add(new { id = variant, family = source.Family, frames = data.Length / 2,
                    peak, rms = Math.Sqrt(sum / data.Length), dc = dc / data.Length, max_step = step });
            }
        }
        Files.Save(Files.Data("render-audit.json"), new { renderer = "transient-body-texture-v1", load_seconds = seconds,
            sound_events = effects.SoundEvents.Count, sample_count = samples.Count,
            sample_bytes = samples.StoredBytes, resident_sample_bytes = samples.ResidentBytes,
            read_p95_ms = readMilliseconds[(int)(readMilliseconds.Count * .95)], read_max_ms = readMilliseconds[^1], rows });
        Console.WriteLine($"Rendered {rows.Count} audible-event candidates; {effects.SoundEvents.Count} events; load {seconds:F2}s.");
    }
}
