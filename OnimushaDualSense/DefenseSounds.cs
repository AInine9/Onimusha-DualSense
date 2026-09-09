using System.Text.Json.Nodes;

namespace OnimushaDualSense;

// These compound events preserve concurrent Wwise layers. Alternatives within
// each random container are selected once per event, never played all at once.
static class DefenseSounds
{
    internal static void Prepare(string extracted, string decoder)
    {
        if (!File.Exists(Files.Bundled("defense_haptics.json"))) return;
        var manifest = Files.Read(Files.Bundled("defense_haptics.json"));
        string bankName = manifest["bank"]!.GetValue<string>();
        if (Path.GetFileName(bankName) != bankName) throw new InvalidDataException("Invalid defense bank");
        string bankPath = Path.Combine(extracted, "natives/stm/sound/wwise", bankName);
        if (Files.Sha(bankPath) != manifest["bank_sha256"]!.GetValue<string>()) throw new InvalidDataException("Unsupported defense bank");
        var chunks = Setup.Chunks(File.ReadAllBytes(bankPath));
        var media = new Dictionary<uint, (int Offset, int Length)>();
        for (int i = 0; i < chunks["DIDX"].Length; i += 12)
            media[BitConverter.ToUInt32(chunks["DIDX"], i)] = (checked((int)BitConverter.ToUInt32(chunks["DIDX"], i + 4)), checked((int)BitConverter.ToUInt32(chunks["DIDX"], i + 8)));
        string scratch = Files.Data("defense-decode-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        Directory.CreateDirectory(Files.Data("sources"));
        var prepared = new HashSet<string>();
        try
        {
            foreach (var item in manifest["events"]!.AsObject())
                foreach (var variant in item.Value?["variants"]?.AsArray() ?? [])
                    foreach (var layer in variant!["layers"]!.AsArray())
                    {
                        string hash = layer!["source_sha256"]!.GetValue<string>();
                        if (!prepared.Add(hash)) continue;
                        string filename = layer["source_file"]!.GetValue<string>();
                        if (Path.GetFileName(filename) != filename) throw new InvalidDataException("Invalid defense source");
                        string destination = Files.Data("sources/" + filename);
                        if (File.Exists(destination) && Files.Sha(destination) == hash) continue;
                        var span = media[layer["source_media"]!.GetValue<uint>()];
                        string wem = Path.Combine(scratch, "source.wem"), wav = Path.Combine(scratch, "source.wav"), normalized = Path.Combine(scratch, "normalized.wav");
                        File.WriteAllBytes(wem, chunks["DATA"][span.Offset..checked(span.Offset + span.Length)]);
                        Setup.Exec(decoder, "-i", "-o", wav, wem);
                        SoundHaptics.Write(normalized, SoundHaptics.ReadSource(wav, true), precise: true);
                        if (Files.Sha(normalized) != hash) throw new InvalidDataException("Defense source regeneration checksum mismatch");
                        File.Copy(normalized, destination, true);
                    }
        }
        finally { Directory.Delete(scratch, true); }
        Console.WriteLine($"Verified {prepared.Count} defense source hashes.");
    }

    internal static float[] Render(JsonNode variant, string family, bool audible, Dictionary<string, float[]> cache)
    {
        var layers = variant["layers"]!.AsArray().Select(node =>
        {
            var source = SoundHaptics.LoadShared(node!, cache);
            var playback = SoundHaptics.Playback(node!);
            return audible
                ? SoundHaptics.AddDelay(SoundHaptics.ApplyPlayback(source, 2, playback), 2, playback.DelaySeconds)
                : SoundHaptics.Render(source, family == "parry_release" ? "deflect" : family, playback);
        }).ToArray();
        var result = new float[layers.Select(v => v.Length).DefaultIfEmpty(0).Max()];
        foreach (var layer in layers)
            for (int i = 0; i < layer.Length; i++) result[i] += layer[i];
        // Fixed soft limiting keeps relative source strength; it does not
        // normalize small and large defense sounds to the same peak.
        if (!audible) for (int i = 0; i < result.Length; i++) result[i] = (float)(.88 * Math.Tanh(result[i] / .88));
        return result;
    }

    internal static void Load(ExtendedEffects effects, IDictionary<string, float[]> samples)
    {
        if (!File.Exists(Files.Bundled("defense_haptics.json"))) return;
        var manifest = Files.Read(Files.Bundled("defense_haptics.json"));
        var cache = new Dictionary<string, float[]>();
        foreach (var entry in manifest["events"]!.AsObject())
        {
            uint ev = uint.Parse(entry.Key); var node = entry.Value!;
            string family = node["family"]!.GetValue<string>();
            string id = "defense_sound_" + ev;
            if (family == "parry_stop")
            {
                effects.SoundEvents[ev] = new { id, family, source = "parry_pos", stops = "defense_sound_" + node["stops"]!.ToString() };
                continue;
            }
            var variants = new List<string>();
            int i = 0;
            foreach (var variant in node["variants"]!.AsArray())
            {
                string sample = "ext:" + id + "#" + i++;
                samples[sample] = Render(variant!, family, false, cache);
                variants.Add(sample);
            }
            effects.Available[id] = new(id, "defense", 11, .04, 0, 0, 0, 1, Family: family);
            effects.Variants[id] = variants.ToArray();
            effects.SoundEvents[ev] = new { id, family, source = "parry_pos" };
        }
    }
}
