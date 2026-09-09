using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

static class Files
{
    public static string Root = AppContext.BaseDirectory;
    public static string At(string name) => Path.Combine(Root, name);
    public static string Data(string name) => At(Path.Combine("data", name));
    public static string Bundled(string name) => At(Path.Combine("bin", name));
    public static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
    public static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    public static bool Atomic(string path, object value)
    {
        string temp = path + "." + Environment.ProcessId + ".tmp";
        for (int i = 0; i < 4; i++)
        {
            try { Save(temp, value); File.Move(temp, path, true); return true; }
            catch (IOException e) when ((e.HResult & 65535) is 5 or 32 or 33) { }
            catch (UnauthorizedAccessException) { }
            if (i < 3) Thread.Sleep(2 << i);
        }
        return false;
    }
    public static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Console.WriteLine(line);
        try { File.AppendAllText(Data("bridge.log"), line + Environment.NewLine); }
        catch (IOException) { } // A concurrent diagnostic process must not stop controller output.
    }
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static bool? GameRunning()
    {
        try { var processes = Process.GetProcessesByName("OnimushaWotS"); bool found = processes.Length > 0; foreach (var p in processes) p.Dispose(); return found; }
        catch { return null; }
    }
}

sealed class GameLifetime
{
    public bool Seen { get; private set; }
    double? absent;
    public bool ShouldExit(bool? running, double now)
    {
        if (running == true) { Seen = true; absent = null; }
        else if (running == false && Seen) { absent ??= now; return now - absent >= 2; }
        else if (running == null) absent = null;
        return false;
    }
}

static class Protocol
{
    public static readonly byte[] Off = [5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    public static byte[] Feedback(float[] powers)
    {
        if (powers.Length != 10) throw new InvalidDataException("Expected ten trigger zones");
        uint mask = 0, packed = 0;
        for (int i = 0; i < 10; i++)
        {
            float p = powers[i];
            if (!float.IsFinite(p) || p < 0 || p > 1) throw new InvalidDataException("Invalid trigger strength");
            int strength = Math.Min(8, (int)(p * 8 + .5));
            if (strength > 0) { mask |= 1u << i; packed |= (uint)(strength - 1) << (i * 3); }
        }
        if (mask == 0) return Off;
        byte[] result = new byte[11]; result[0] = 0x21;
        BitConverter.GetBytes((ushort)mask).CopyTo(result, 1); BitConverter.GetBytes(packed).CopyTo(result, 3);
        return result;
    }
    public static byte[] Report(byte[]? right = null, byte[]? left = null, bool audio = true)
    {
        byte[] result = new byte[48]; result[0] = 2; result[1] = audio ? (byte)0x0c : (byte)0x0e;
        (right ?? Off).CopyTo(result, 11); (left ?? Off).CopyTo(result, 22); return result;
    }
}

record Accepted(int Trigger, JsonNode[] Events, bool Active, bool UiAllowed = false);
sealed class Inbox
{
    public string? Session;
    public double Last;
    long seq = -1, lastEvent;
    public Accepted? Accept(JsonNode state, double now)
    {
        int version = state["version"]!.GetValue<int>();
        if (version is not (1 or 2 or 3)) throw new InvalidDataException("Unsupported bridge protocol");
        string session = state["session"]!.ToJsonString();
        long next = state["seq"]!.GetValue<long>();
        if (session != Session) { Session = session; seq = -1; lastEvent = 0; }
        if (next <= seq) return null;
        // Validate the complete message before committing its sequence.
        bool enabled = state["enabled"]!.GetValue<bool>();
        bool active = enabled && !state["paused"]!.GetValue<bool>();
        bool uiAllowed = enabled && version == 3 && state["ui_allowed"]?.GetValue<bool>() == true;
        long frame = state["frame"]!.GetValue<long>();
        var events = state["events"]?.AsArray().Where(e => e!["seq"]!.GetValue<long>() > lastEvent).Select(e => e!).ToArray() ?? [];
        var fresh = events.Where(e => frame - e["frame"]!.GetValue<long>() is >= 0 and <= 8 &&
            (active || (uiAllowed && e["kind"]?.ToString() == "extended" && e["id"]?.ToString().StartsWith("ui_", StringComparison.Ordinal) == true) || e["kind"]?.ToString() == "stop")).ToArray();
        int trigger = active ? state["trigger"]?.GetValue<int>() ?? -1 : -1;
        seq = next; Last = now;
        if (events.Length > 0) lastEvent = events.Max(e => e["seq"]!.GetValue<long>());
        return new(trigger, fresh, active && version >= 2, uiAllowed);
    }
}

static class Wave
{
    public static float[] Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Expected RIFF WAV");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Expected WAVE");
        bool format = false; byte[]? pcm = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string tag = new(reader.ReadChars(4)); int length = checked((int)reader.ReadUInt32());
            byte[] data = reader.ReadBytes(length); if (data.Length != length) throw new InvalidDataException("Truncated WAV");
            if (tag == "fmt ")
            {
                format = data.Length >= 16 && BitConverter.ToUInt16(data, 0) == 1 && BitConverter.ToUInt16(data, 2) == 2 && BitConverter.ToUInt32(data, 4) == 48000 && BitConverter.ToUInt16(data, 14) == 16;
            }
            if (tag == "data") pcm = data;
            if ((length & 1) != 0) reader.ReadByte();
        }
        if (!format || pcm == null || pcm.Length % 4 != 0) throw new InvalidDataException("Expected 48 kHz stereo PCM16");
        float[] result = new float[pcm.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return result;
    }

}

sealed class Mixer(IDictionary<string, float[]> samples, float gain)
{
    readonly object gate = new();
    readonly List<(string Id, float[] Data, int Pos, float Level)> voices = [];
    public bool Playing { get { lock (gate) return voices.Count > 0; } }
    public bool Play(string id, int delayFrames = 0, float level = 1)
    {
        if (!samples.TryGetValue(id, out var data)) return false;
        lock (gate)
        {
            if (voices.Any(v => v.Id == id)) return false;
            if (voices.Count == 16) voices.RemoveAt(0);
            voices.Add((id, data, -Math.Max(0, delayFrames) * 2, level)); return true;
        }
    }
    public void Stop() { lock (gate) voices.Clear(); }
    public bool Replace(string id, string replacement)
    {
        if (!samples.TryGetValue(replacement, out var data)) return false;
        lock (gate)
        {
            int index = voices.FindIndex(v => v.Id == id);
            if (index < 0) return false;
            var voice = voices[index];
            if (voice.Pos >= data.Length) return false;
            voices[index] = (replacement, data, voice.Pos, voice.Level); return true;
        }
    }
    public void FadeOut(string id)
    {
        lock (gate)
        {
            int index = voices.FindIndex(v => v.Id == id);
            if (index < 0) return;
            var voice = voices[index];
            if (voice.Pos < 0) { voices.RemoveAt(index); return; }
            int count = Math.Min(768, voice.Data.Length - voice.Pos);
            var tail = new float[count];
            for (int i = 0; i < count; i++) tail[i] = voice.Data[voice.Pos + i] * (1 - (i / 2) / (float)Math.Max(1, count / 2 - 1));
            voices[index] = (id, tail, 0, voice.Level);
        }
    }
    public void StopGameplay() { lock (gate) voices.RemoveAll(v => !v.Id.StartsWith("ext:ui_", StringComparison.Ordinal)); }
    public void Fill(float[] output, int frames)
    {
        Array.Clear(output);
        lock (gate)
        {
            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var v = voices[i];
                int start = Math.Min(frames, Math.Max(0, -v.Pos / 2)), pos = Math.Max(0, v.Pos);
                int n = Math.Min(frames - start, (v.Data.Length - pos) / 2);
                for (int f = 0; f < n; f++) { output[(f + start) * 4 + 2] += v.Data[pos + f * 2] * gain * v.Level; output[(f + start) * 4 + 3] += v.Data[pos + f * 2 + 1] * gain * v.Level; }
                int next = v.Pos + frames * 2;
                if (next >= v.Data.Length) voices.RemoveAt(i); else voices[i] = (v.Id, v.Data, next, v.Level);
            }
        }
        for (int i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i], -.85f, .85f);
    }
}
