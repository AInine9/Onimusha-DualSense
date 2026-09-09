using System.Text.Json.Nodes;

namespace OnimushaDualSense;

// Source files are decoded from the user's own banks during setup. No audio is embedded.
static class SoundHaptics
{
    // Format 2 stores one decoded source per content hash, never rendered variants.
    // Rendering happens once at load time, outside the device callback.
    public static void Prepare(string extracted, string decoder)
    {
        string scratch = Files.Data("sound-decode-" + Guid.NewGuid().ToString("N"));
        string output = Files.Data("sources");
        Directory.CreateDirectory(scratch); Directory.CreateDirectory(output);
        var manifest = new JsonObject { ["format"] = 2, ["renderer"] = "transient-body-texture-v1" };
        var stored = new HashSet<string>();
        try
        {
            foreach (var bankGroup in SoundCatalog.Entries.GroupBy(e => e.Bank))
            {
                string bankPath = Path.Combine(extracted, "natives/stm/sound/wwise", bankGroup.Key);
                string resource = "natives/stm/sound/wwise/" + bankGroup.Key;
                if (Files.Sha(bankPath) != SoundCatalog.Banks[resource]) throw new InvalidDataException("Unsupported sound bank: " + resource);
                var chunks = Setup.Chunks(File.ReadAllBytes(bankPath));
                byte[] index = chunks["DIDX"];
                if (index.Length % 12 != 0) throw new InvalidDataException("Invalid media index");
                var media = new Dictionary<uint, (int Offset, int Size)>();
                for (int i = 0; i < index.Length; i += 12)
                    media[BitConverter.ToUInt32(index, i)] = (checked((int)BitConverter.ToUInt32(index, i + 4)), checked((int)BitConverter.ToUInt32(index, i + 8)));
                var decoded = new Dictionary<uint, string>();
                foreach (var reference in bankGroup)
                {
                    var variants = new JsonArray();
                    foreach (var variant in reference.Variants)
                    {
                        uint id = variant.Media;
                        if (!decoded.TryGetValue(id, out string? hash))
                        {
                            var (offset, size) = media[id];
                            string wem = Path.Combine(scratch, id + ".wem"), wav = Path.Combine(scratch, id + ".wav");
                            File.WriteAllBytes(wem, chunks["DATA"][offset..checked(offset + size)]);
                            Setup.Exec(decoder, "-i", "-o", wav, wem);
                            string normalized = Path.Combine(scratch, "normalized.wav");
                            Write(normalized, ReadSource(wav, true));
                            hash = Files.Sha(normalized);
                            if (stored.Add(hash)) File.Copy(normalized, Path.Combine(output, hash + ".wav"), true);
                            decoded[id] = hash;
                        }
                        int draws = DrawCount(variant);
                        for (int draw = 0; draw < draws; draw++)
                        {
                            SoundPlayback playback = Realize(variant, draw, draws);
                            var switches = new JsonArray(variant.Switches.Select(item => (JsonNode)new JsonObject
                            {
                                ["group"] = item.Group, ["default"] = item.Default,
                                ["values"] = new JsonArray(item.Values.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray())
                            }).ToArray());
                            variants.Add(new JsonObject
                            {
                                ["source_file"] = hash + ".wav", ["source_sha256"] = hash,
                                ["onset_frames"] = 0, // source and haptics retain the same leading silence
                                ["source_media"] = id, ["path_id"] = variant.Path,
                                ["draw"] = draw, ["draw_count"] = draws,
                                ["pitch_cents"] = playback.PitchCents, ["delay_ms"] = playback.DelaySeconds * 1000,
                                ["volume_db"] = playback.VolumeDb, ["randomized"] = draws > 1,
                                ["dynamic_rtpc"] = variant.DynamicRtpc, ["dynamic_state"] = variant.DynamicState,
                                ["switches"] = switches
                            });
                        }
                    }
                    manifest[reference.Event.ToString()] = new JsonObject { ["variants"] = variants };
                }
                Console.WriteLine("Prepared shared sources: " + bankGroup.Key);
            }
            Files.Save(Files.Data("sound_haptics.json"), manifest);
            Console.WriteLine("Shared source WAVs: " + stored.Count + "; generated tactile WAVs: 0");
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static SoundPlayback Playback(JsonNode node) => new(
        node["pitch_cents"]!.GetValue<double>(), node["delay_ms"]!.GetValue<double>() / 1000,
        node["volume_db"]!.GetValue<double>());

    internal static float[] Render(float[] stereo, string family, SoundPlayback playback)
    {
        var mono = new float[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = (stereo[2 * i] + stereo[2 * i + 1]) * .5f;
        var tactile = Scale(Convert(ApplyPlayback(mono, 1, playback, false), family), playback.VolumeDb);
        // Match PCM16's silence floor, so inaudible branches cannot suppress rumble.
        for (int i = 0; i < tactile.Length; i++) tactile[i] = (float)Math.Round(tactile[i] * 32767) / 32767f;
        return AddDelay(tactile, 2, playback.DelaySeconds);
    }

    internal static float[] LoadShared(JsonNode node, Dictionary<string, float[]> cache)
    {
        string filename = node["source_file"]!.GetValue<string>();
        string hash = node["source_sha256"]!.GetValue<string>();
        if (Path.GetFileName(filename) != filename) throw new InvalidDataException("Invalid source path");
        // Include both fields so a changed manifest hash cannot bypass verification.
        string key = filename + ":" + hash;
        if (!cache.TryGetValue(key, out var data))
        {
            string path = Files.Data("sources/" + filename);
            if (Files.Sha(path) != hash) throw new InvalidDataException("Source checksum mismatch");
            cache[key] = data = Wave.Read(path);
        }
        return data;
    }
    internal static float[] ReadSource(string path, bool stereo = false)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Source must be RIFF");
        reader.ReadUInt32(); if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Source must be WAVE");
        int channels = 0, rate = 0; byte[]? pcm = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string tag = new(reader.ReadChars(4)); int length = checked((int)reader.ReadUInt32());
            byte[] chunk = reader.ReadBytes(length); if (chunk.Length != length) throw new InvalidDataException("Truncated source");
            if (tag == "fmt ")
            {
                if (chunk.Length < 16 || BitConverter.ToUInt16(chunk) != 1 || BitConverter.ToUInt16(chunk, 14) != 16) throw new InvalidDataException("Source must be PCM16");
                channels = BitConverter.ToUInt16(chunk, 2); rate = checked((int)BitConverter.ToUInt32(chunk, 4));
            }
            if (tag == "data") pcm = chunk;
            if ((length & 1) != 0) reader.ReadByte();
        }
        if (channels is < 1 or > 8 || rate is < 8000 or > 192000 || pcm == null || pcm.Length % (channels * 2) != 0) throw new InvalidDataException("Invalid source format");
        int inputFrames = pcm.Length / (channels * 2);
        var mono = new float[inputFrames];
        for (int f = 0; f < inputFrames; f++)
            for (int c = 0; c < channels; c++) mono[f] += BitConverter.ToInt16(pcm, (f * channels + c) * 2) / (32768f * channels);
        int outputChannels = stereo ? 2 : 1;
        var result = new float[(int)((long)inputFrames * 48000 / rate) * outputChannels];
        for (int i = 0; i < result.Length / outputChannels; i++)
        {
            double t = i * (double)rate / 48000.0; int a = (int)t, b = Math.Min(a + 1, mono.Length - 1);
            for (int c = 0; c < outputChannels; c++)
            {
                float x = stereo && channels == 2 ? BitConverter.ToInt16(pcm, (a * 2 + c) * 2) / 32768f : mono[a];
                float y = stereo && channels == 2 ? BitConverter.ToInt16(pcm, (b * 2 + c) * 2) / 32768f : mono[b];
                result[i * outputChannels + c] = x + (y - x) * (float)(t - a);
            }
        }
        return result;
    }

    internal static int Onset(float[] source)
    {
        float peak = source.Select(Math.Abs).DefaultIfEmpty(0).Max();
        return Math.Max(0, Array.FindIndex(source, v => Math.Abs(v) >= peak * .018f) - 96);
    }
    internal static int DrawCount(SoundVariant variant) => Variable(variant.RandomMin.PitchCents, variant.RandomMax.PitchCents)
        || Variable(variant.RandomMin.DelaySeconds, variant.RandomMax.DelaySeconds)
        || Variable(variant.RandomMin.VolumeDb, variant.RandomMax.VolumeDb) ? 5 : 1;
    static bool Variable(double low, double high) => Math.Abs(high - low) > 1e-9;
    internal static SoundPlayback Realize(SoundVariant variant, int draw, int count)
    {
        if (count < 1 || draw < 0 || draw >= count) throw new ArgumentOutOfRangeException(nameof(draw));
        double Sample(double low, double high, uint salt, int stride)
        {
            if (!Variable(low, high)) return (low + high) * .5;
            uint seed = variant.Path ^ salt;
            int slot = (int)(((uint)(draw * stride) + seed % (uint)count) % (uint)count);
            return low + (high - low) * ((slot + .5) / count);
        }
        return new(
            variant.Fixed.PitchCents + Sample(variant.RandomMin.PitchCents, variant.RandomMax.PitchCents, 0x9e3779b9, 2),
            Math.Max(0, variant.Fixed.DelaySeconds + Sample(variant.RandomMin.DelaySeconds, variant.RandomMax.DelaySeconds, 0x85ebca6b, 3)),
            variant.Fixed.VolumeDb + Sample(variant.RandomMin.VolumeDb, variant.RandomMax.VolumeDb, 0xc2b2ae35, 4));
    }
    internal static float[] ApplyPlayback(float[] source, int channels, SoundPlayback playback, bool applyVolume = true)
    {
        if (channels < 1 || source.Length % channels != 0) throw new ArgumentOutOfRangeException(nameof(channels));
        double rate = Math.Pow(2, playback.PitchCents / 1200.0);
        int inputFrames = source.Length / channels;
        if (inputFrames == 0) return [];
        int outputFrames = Math.Max(1, (int)Math.Ceiling(inputFrames / rate));
        float gain = applyVolume ? (float)Math.Pow(10, playback.VolumeDb / 20.0) : 1;
        var result = new float[outputFrames * channels];
        for (int f = 0; f < outputFrames; f++)
        {
            double p = Math.Min(inputFrames - 1, f * rate); int a = (int)p, b = Math.Min(a + 1, inputFrames - 1); float mix = (float)(p - a);
            for (int c = 0; c < channels; c++) result[f * channels + c] = (source[a * channels + c] + (source[b * channels + c] - source[a * channels + c]) * mix) * gain;
        }
        return result;
    }
    internal static float[] Scale(float[] source, double volumeDb)
    {
        float gain = (float)Math.Pow(10, volumeDb / 20.0);
        float peak = source.Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (peak > 0) gain = Math.Min(gain, .98f / peak); // preserve shape instead of hard clipping positive Wwise gain
        if (gain == 1) return source;
        var result = new float[source.Length];
        for (int i = 0; i < source.Length; i++) result[i] = source[i] * gain;
        return result;
    }
    internal static float[] AddDelay(float[] source, int channels, double seconds)
    {
        int delay = Math.Max(0, (int)Math.Round(seconds * 48000)) * channels;
        if (delay == 0) return source;
        var result = new float[delay + source.Length]; Array.Copy(source, 0, result, delay, source.Length); return result;
    }
    internal static float[] Convert(float[] source, string family)
    {
        if (family == "parry") return ParryFriction.Convert(source);
        if (family is "guard" or "deflect") return DefenseImpact.Convert(source, family == "deflect");
        // These are design tunings, not a claimed calibrated DualSense transfer function.
        // Keep impact, low-frequency body and high-frequency texture independently timed.
        (double Max, double Level, double Body, double Texture, double Decay) design = family switch
        {
            "ui" => (.10, .40, .45, .55, .055),
            "footsteps" => (.18, .55, 1.0, .40, .09),
            "attack" => (.32, .62, .65, .65, .16),
            "cut" => (1.2, .94, 1.1, 1.1, double.PositiveInfinity),
            "guard" => (.40, .88, .70, 1.0, .20),
            _ => (.50, .94, 1.0, .75, .24)
        };
        if (source.Length == 0 || source.All(v => Math.Abs(v) < .00001f)) return new float[96];
        if (source.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite source");
        // Preserve source-leading silence; onset-relative truncation formerly played early.
        int onset = Onset(source);
        int frames = Math.Min(source.Length, onset + (int)(design.Max * 48000));
        var tactile = new double[frames];
        double low90 = 0, low320 = 0, low1200 = 0, dc = 0;
        double envLow = 0, envMid = 0, envHigh = 0, slow = 0, outputDc = 0;
        double Coef(double hz) => 1 - Math.Exp(-2 * Math.PI * hz / 48000);
        double a90 = Coef(90), a320 = Coef(320), a1200 = Coef(1200), adc = Coef(30);
        static double Envelope(double current, double sample) => current + (Math.Abs(sample) - current) * (Math.Abs(sample) > current ? .045 : .0015);
        double max = 0;
        for (int i = 0; i < frames; i++)
        {
            double x = source[i];
            low90 += a90 * (x - low90); low320 += a320 * (x - low320);
            low1200 += a1200 * (x - low1200); dc += adc * (x - dc);
            envLow = Envelope(envLow, low90 - dc);
            envMid = Envelope(envMid, low1200 - low90);
            envHigh = Envelope(envHigh, x - low1200);
            double fast = envMid + envHigh;
            slow += .00065 * (fast - slow);
            double transient = Math.Max(0, fast - slow);
            double t = Math.Max(0, i - onset) / 48000.0;
            double texture = envHigh * .35 + transient * .85;
            double v = .45 * (low320 - dc)
                + design.Body * envLow * Math.Sin(2 * Math.PI * 72 * t)
                + .85 * envMid * Math.Sin(2 * Math.PI * 145 * t)
                + design.Texture * texture * (Math.Sin(2 * Math.PI * 235 * t) + .20 * Math.Sin(2 * Math.PI * 310 * t));
            // Soft late damping keeps the first hit intact while letting short tails decay.
            double damping = Math.Exp(-Math.Max(0, t - .06) / design.Decay);
            outputDc += adc * (v - outputDc);
            double edge = Math.Min(1, Math.Max(0, i - onset) / 72.0);
            double release = Math.Min(1, (frames - 1 - i) / 720.0);
            tactile[i] = (v - outputDc) * edge * release * damping;
            max = Math.Max(max, Math.Abs(tactile[i]));
        }
        // Bounded gain: quiet recordings remain quiet; never peak-normalize tiny noise.
        double scale = max > 0 ? design.Level * Math.Min(4.0, 1.0 / max) : 0;
        var stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++) stereo[2 * i] = stereo[2 * i + 1] = (float)(tactile[i] * scale);
        return stereo;
    }
    internal static void Write(string path, float[] stereo, bool precise = false)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + stereo.Length * 2); w.Write("WAVEfmt "u8); w.Write(16);
        w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(192000); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(stereo.Length * 2);
        foreach (float v in stereo) w.Write((short)Math.Clamp((int)Math.Round(precise ? (double)v * 32767 : v * 32767), -32767, 32767));
    }
}
