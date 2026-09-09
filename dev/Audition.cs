using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

sealed class Audition : IDisposable
{
    string last = "", request = "", sample = "";
    double began;
    bool playing, withAudio;
    int onset;
    readonly Dictionary<string, float[]> audible = [];
    readonly Mixer audibleMixer;
    Audio? output;
    public Audition() { audibleMixer = new Mixer(audible, 1); }
    public void Dispose() { audibleMixer.Stop(); output?.Dispose(); }
    internal static (int Haptic, int Sound) Delays(double hapticLatency, double soundLatency, int onsetFrames)
    {
        double difference = soundLatency + onsetFrames / 48000.0 - hapticLatency;
        return ((int)Math.Round(Math.Max(0, difference) * 48000), (int)Math.Round(Math.Max(0, -difference) * 48000));
    }
    void PrepareAudio(string id, float volume)
    {
        withAudio = false; audibleMixer.Stop();
        var defense = Regex.Match(id, @"^ext:defense_sound_(\d+)#(\d+)$");
        if (defense.Success)
        {
            var defenseManifest = Files.Read(Files.Bundled("defense_haptics.json"));
            var defenseNode = defenseManifest["events"]![defense.Groups[1].Value]!;
            var variant = defenseNode["variants"]!.AsArray()[int.Parse(defense.Groups[2].Value)]!;
            audible["preview"] = DefenseSounds.Render(variant, defenseNode["family"]!.GetValue<string>(), true, new())
                .Take(48000 * 2 * 8).Select(v => v * volume).ToArray();
            onset = 0; output ??= new Audio(audibleMixer, true);
            output.CheckHealth(); withAudio = true; return;
        }
        var match = Regex.Match(id, @"^ext:(?:ui_|parry_|cut_|issen_|deadheat_)?sound_(\d+)(?:_(?:left|right))?#(\d+)$");
        if (!match.Success) return;
        var manifest = Files.Read(Files.Data("sound_haptics.json"));
        var node = manifest[match.Groups[1].Value]?["variants"]?.AsArray()[int.Parse(match.Groups[2].Value)];
        if (node?["source_file"] is not JsonNode file) return;
        string filename = file.GetValue<string>();
        if (Path.GetFileName(filename) != filename) throw new InvalidDataException("Invalid source path");
        string path = Files.Data("sources/" + filename);
        if (Files.Sha(path) != node["source_sha256"]!.GetValue<string>()) throw new InvalidDataException("Source checksum mismatch");
        var sound = Wave.Read(path);
        if (manifest["format"]?.GetValue<int>() == 2)
        {
            var playback = SoundHaptics.Playback(node);
            sound = SoundHaptics.AddDelay(SoundHaptics.ApplyPlayback(sound, 2, playback), 2, playback.DelaySeconds);
        }
        audible["preview"] = sound.Take(48000 * 2 * 8).Select(v => v * volume).ToArray();
        onset = node["onset_frames"]?.GetValue<int>() ?? 0;
        output ??= new Audio(audibleMixer, true);
        output.CheckHealth(); withAudio = true;
    }
    public bool Active => sample.Length > 0;
    internal static bool Valid(JsonNode node, long now, IDictionary<string, float[]> samples) =>
        node["token"]?.GetValue<string>() is { Length: > 0 } &&
        now - (node["timestamp"]?.GetValue<long>() ?? 0) is >= 0 and < 5000 &&
        (node["sample"]?.GetValue<string>() == "" || samples.ContainsKey(node["sample"]?.GetValue<string>() ?? ""));
    public void Read(IDictionary<string, float[]> samples, FeedbackQueue queue, double now)
    {
        try
        {
            var node = Files.Read(Files.Data("audition-request.json"));
            string token = node["token"]?.GetValue<string>() ?? "";
            if (token == last || !Valid(node, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), samples)) return;
            last = request = token; sample = node["sample"]!.GetValue<string>(); began = now; playing = false;
            queue.Clear(); audibleMixer.Stop(); withAudio = false;
            if (Active && node["audio"]?.GetValue<bool>() == true)
            {
                try
                {
                    float volume = node["volume"]?.GetValue<float>() ?? .5f;
                    if (!float.IsFinite(volume) || volume < 0 || volume > 1) throw new InvalidDataException("Invalid audio volume");
                    PrepareAudio(sample, volume);
                }
                catch (Exception e)
                {
                    sample = ""; Files.Atomic(Files.Data("audition-status.json"), new { token, state = "failed", message = e.Message }); return;
                }
            }
            Files.Atomic(Files.Data("audition-status.json"), new { token, state = Active ? "waiting" : "stopped" });
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
    }
    public void Tick(Mixer mixer, bool gameFocused, bool acknowledged, bool freshGame, double now, double hapticLatency = 0)
    {
        if (!Active) return;
        if (gameFocused || now - began > 10 || (playing && !mixer.Playing && (!withAudio || !audibleMixer.Playing)))
        {
            mixer.Stop(); audibleMixer.Stop(); sample = "";
            Files.Atomic(Files.Data("audition-status.json"), new { token = request, state = gameFocused ? "game_focused" : playing ? "finished" : "timeout" });
            return;
        }
        if (!playing && (acknowledged || !freshGame))
        {
            var delays = withAudio ? Delays(hapticLatency, output!.OutputLatency, onset) : (Haptic: 0, Sound: 0);
            if (withAudio) audibleMixer.Play("preview", delays.Sound);
            playing = mixer.Play(sample, delays.Haptic);
            Files.Atomic(Files.Data("audition-status.json"), new { token = request, state = playing ? "playing" : "failed", sample, audio = withAudio, haptic_delay_frames = delays.Haptic, sound_delay_frames = delays.Sound });
            Files.Log("Inspector audition sample=" + sample);
        }
    }
}
