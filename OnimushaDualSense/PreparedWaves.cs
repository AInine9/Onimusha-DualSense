using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

// Generated once at unity gain. The mixer applies the configured output gain;
// cached arrays and the stored WAVs remain unchanged.
static class PreparedWaves
{
    internal record WaveEntry(string File, int Length, string Hash);
    internal record Index(string Fingerprint, Dictionary<string, WaveEntry> Waves,
        Dictionary<string, ExtraEffect> Available, Dictionary<string, string[]> Variants,
        Dictionary<string, SoundChoice[]> Choices, Dictionary<string, string> Parry,
        Dictionary<string, string> Cut, Dictionary<string, SoundPreviewInfo> Info,
        Dictionary<uint, JsonElement> Events);
    static string Folder => Files.Data("waves");
    static string IndexPath => Path.Combine(Folder, "index.json");
    internal static string Fingerprint()
    {
        string text = "float32-wav-v1|" + Files.Sha(typeof(PreparedWaves).Assembly.Location);
        foreach (string name in new[] { Files.Data("sound_haptics.json"), Files.Bundled("defense_haptics.json") })
            text += "|" + Path.GetFileName(name) + ":" + (File.Exists(name) ? Files.Sha(name) : "absent");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    static string PathFor(WaveEntry entry)
    {
        if (entry.File != entry.Hash + ".wav" || entry.Hash.Length != 64 || !entry.Hash.All(Uri.IsHexDigit)
            || entry.Length < 0 || entry.Length % 2 != 0) throw new InvalidDataException("Invalid prepared waveform entry");
        return Path.Combine(Folder, entry.File);
    }
    static Index? Existing(string fingerprint)
    {
        try
        {
            if (!File.Exists(IndexPath)) return null;
            var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath));
            if (index == null || index.Fingerprint != fingerprint) return null;
            foreach (var wave in index.Waves.Values.DistinctBy(w => w.File))
                if (new FileInfo(PathFor(wave)).Length != 56L + wave.Length * 4L) return null;
            return index;
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { return null; }
    }
    public static Index Prepare(bool force = false)
    {
        string fingerprint = Fingerprint();
        if (!force && Existing(fingerprint) is { } current) return current;
        string rootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Files.Root).TrimEnd('\\', '/').ToUpperInvariant())));
        using var mutex = new Mutex(false, @"Local\OnimushaWavePreparation_" + rootHash);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            if (!force && Existing(fingerprint) is { } ready) return ready;
            Directory.CreateDirectory(Folder);
            Console.WriteLine("Preparing reusable float32 haptic WAVs...");
            using var samples = new SampleStore();
            var effects = new ExtendedEffects(samples);
            var waves = new Dictionary<string, WaveEntry>();
            var written = new HashSet<string>();
            foreach (string key in effects.PlayableSamples())
            {
                var data = samples[key];
                string hash = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan())));
                var entry = new WaveEntry(hash + ".wav", data.Length, hash);
                if (written.Add(hash)) WriteWave(PathFor(entry), data);
                waves[key] = entry;
            }
            var result = new Index(fingerprint, waves, effects.Available, effects.Variants, effects.SoundChoices,
                effects.ParrySamples, effects.CutSamples, effects.SoundInfo,
                effects.SoundEvents.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)));
            if (!Files.Atomic(IndexPath, result)) throw new IOException("Cannot commit prepared waveform index");
            Console.WriteLine($"Prepared {waves.Count} sample registrations in {written.Count} shared WAV files.");
            return result;
        }
        finally { mutex.ReleaseMutex(); }
    }
    public static (SampleStore Samples, ExtendedEffects Effects) Load()
    {
        var index = Prepare();
        var samples = new SampleStore(); var effects = new ExtendedEffects();
        foreach (var (key, wave) in index.Waves) samples.RegisterWave(key, PathFor(wave), wave.Length, wave.Hash);
        foreach (var (key, value) in index.Available) effects.Available[key] = value;
        foreach (var (key, value) in index.Variants) effects.Variants[key] = value;
        foreach (var (key, value) in index.Choices) effects.SoundChoices[key] = value;
        foreach (var (key, value) in index.Parry) effects.ParrySamples[key] = value;
        foreach (var (key, value) in index.Cut) effects.CutSamples[key] = value;
        foreach (var (key, value) in index.Info) effects.SoundInfo[key] = value;
        foreach (var (key, value) in index.Events) effects.SoundEvents[key] = value;
        return (samples, effects);
    }
    internal static void WriteWave(string path, float[] data)
    {
        if (File.Exists(path))
        {
            try { ReadWave(path, data.Length, Path.GetFileNameWithoutExtension(path)); return; }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new BinaryWriter(File.Create(temp)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(checked(48 + data.Length * 4));
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((ushort)3); writer.Write((ushort)2); writer.Write(48000); writer.Write(384000);
                writer.Write((ushort)8); writer.Write((ushort)32);
                writer.Write(Encoding.ASCII.GetBytes("fact")); writer.Write(4); writer.Write(data.Length / 2);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(checked(data.Length * 4));
                writer.Write(MemoryMarshal.AsBytes(data.AsSpan()));
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static float[] ReadWave(string path, int length, string hash)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        bool Tag(string text) => Encoding.ASCII.GetString(reader.ReadBytes(4)) == text;
        if (stream.Length != 56L + length * 4L || !Tag("RIFF") || reader.ReadInt32() != 48L + length * 4L
            || !Tag("WAVE") || !Tag("fmt ") || reader.ReadInt32() != 16 || reader.ReadUInt16() != 3
            || reader.ReadUInt16() != 2 || reader.ReadInt32() != 48000 || reader.ReadInt32() != 384000
            || reader.ReadUInt16() != 8 || reader.ReadUInt16() != 32 || !Tag("fact") || reader.ReadInt32() != 4
            || reader.ReadInt32() != length / 2 || !Tag("data") || reader.ReadInt32() != length * 4L)
            throw new InvalidDataException("Invalid prepared float32 waveform; run prepare-waves --force");
        var data = new float[length]; stream.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan()));
        if (Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(data.AsSpan()))) != hash)
            throw new InvalidDataException("Prepared waveform checksum mismatch; run prepare-waves --force");
        return data;
    }
}
