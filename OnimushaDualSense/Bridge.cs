using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

static class Bridge
{
    public static int Run(string[] args)
    {
        using var mutex = new Mutex(false, @"Local\OnimushaDualSenseBridge", out bool created);
        if (!created) { Files.Log("Another companion is already running."); return 0; }
        File.Delete(Files.Data("stop.request"));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; File.WriteAllText(Files.Data("stop.request"), "stop"); };
        var config = Configuration.Read();
        if (!File.Exists(Path.Combine(config.Game, "OnimushaWotS.exe"))) throw new InvalidOperationException("Run Setup.cmd first");
        string statePath = Path.Combine(config.Game, "reframework/data/onimusha_dualsense_bridge.json");
        string controlPath = Path.Combine(config.Game, "reframework/data/onimusha_dualsense_control.json");
        double seconds = 0;
        int arg = Array.IndexOf(args, "--seconds"); if (arg >= 0) seconds = double.Parse(args[arg + 1], System.Globalization.CultureInfo.InvariantCulture);
        var prepared = PreparedWaves.Load();
        using var samples = prepared.Samples;
        var extensions = prepared.Effects; SampleStore.FinishLoading();
        var profiles = Files.Read(Files.Data("trigger_profiles.json"))["profiles"]!.AsArray().ToDictionary(p => p!["_Type"]!.GetValue<int>(), p => p!);
        var effects = profiles.ToDictionary(p => p.Key, p => Protocol.Feedback(p.Value["_PowerList"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray()));
        var mixer = new Mixer(samples, config.Gain);
        var queue = new FeedbackQueue(mixer, extensions, Files.Log);
        var routes = JsonSerializer.SerializeToElement(extensions.SoundEvents);
        bool outputEnabled = false;
        bool WriteControl(bool suppress) => Files.Atomic(controlPath, new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), suppress_legacy = suppress, output_enabled = outputEnabled, sound_events = routes });
        using var hid = new Hid();
        Audio? audio = null;
        try
        {
            audio = new Audio(mixer);
            Files.Log($"C# companion: USB HID and four-channel WASAPI open; {extensions.Available.Count} feedback patterns loaded.");
#if DEVELOPER
            using var audition = new Audition();
#endif
            var inbox = new Inbox(); var lifetime = new GameLifetime();
            JsonNode? state = null; Accepted? last = null;
            double began = Files.Now, nextProcess = 0, lastStatus = 0, lastControl = began, controlAttempt = double.NegativeInfinity;
            int lastTrigger = int.MinValue;
            bool? lastSuppress = null;
            while (!File.Exists(Files.Data("stop.request")))
            {
                double now = Files.Now;
                audio.CheckHealth();
#if DEVELOPER
                audition.Read(samples, queue, now);
                if (audition.Active)
                {
                    outputEnabled = false;
                    if (lastSuppress != true || now - lastControl > .2)
                        if (WriteControl(true)) { lastSuppress = true; lastControl = now; }
                    bool freshGame = (DateTime.UtcNow - File.GetLastWriteTimeUtc(statePath)).TotalSeconds < .75;
                    bool ack = false;
                    if (freshGame) try { ack = Files.Read(statePath)["legacy_suppressed"]?.GetValue<bool>() == true; } catch (Exception e) when (ReadableError(e)) { }
                    hid.Send(Protocol.Report());
                    audition.Tick(mixer, Focus.IsGame(), ack && now - lastControl < 1, freshGame, now, audio.OutputLatency);
                    Thread.Sleep(5); continue;
                }
#endif
                if (now >= nextProcess)
                {
                    if (lifetime.ShouldExit(Files.GameRunning(), now)) { Files.Log("Game process exited; shutting down."); break; }
                    nextProcess = now + 1;
                }
                bool focus = Focus.IsGame();
                try
                {
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(statePath)).TotalSeconds < .75)
                    {
                        var next = Files.Read(statePath); var accepted = inbox.Accept(next, now);
                        if (accepted != null)
                        {
                            state = next; last = accepted;
                            queue.SetDefense(focus && accepted.Active && state["gameplay_allowed"]?.GetValue<bool>() == true
                                ? state["defense_kind"]?.GetValue<string>() ?? "none" : "none", now);
                            if (focus && config.Gain > 0)
                                foreach (var ev in accepted.Events)
                                {
                                    string kind = ev["kind"]!.GetValue<string>();
                                    if (kind == "stop") queue.Clear();
                                    else if (kind == "defense_stop") queue.StopParry(ev["id"]!.ToString());
                                    else if (kind == "extended")
                                    {
                                        string id = ev["id"]!.ToString();
                                        bool allowed = id.StartsWith("ui_", StringComparison.Ordinal)
                                            ? accepted.UiAllowed : accepted.Active && state["gameplay_allowed"]?.GetValue<bool>() == true;
                                        if (allowed)
                                        {
                                            if (ev["defense_kind"] is JsonNode defense) queue.SetDefense(defense.GetValue<string>(), now);
                                            queue.Extended(id, ev["seq"]!.GetValue<long>(), ev["frame"]!.GetValue<long>(), now, ev["switches"], ev["technique"]?.GetValue<string>() ?? "");
                                        }
                                    }
                                }
                        }
                    }
                }
                catch (Exception e) when (ReadableError(e)) { }
                bool active = last != null && now - inbox.Last < .75 && last.Active && focus;
                bool uiAllowed = last != null && now - inbox.Last < .75 && last.UiAllowed && focus;
                bool gameplay = active && state?["gameplay_allowed"]?.GetValue<bool>() == true;
                int trigger = active ? last!.Trigger : -1;
                outputEnabled = config.Gain > 0 && (active || uiAllowed);
                queue.SetDefense(gameplay ? state?["defense_kind"]?.GetValue<string>() ?? "none" : "none", now);
                queue.SetActivity(config.Gain > 0 && active, config.Gain > 0 && uiAllowed);
                queue.Dispatch(state?["legacy_suppressed"]?.GetValue<bool>() == true, now);
                bool suppress = queue.RequiresSuppression(gameplay, uiAllowed, now);
                if ((suppress != lastSuppress || now - lastControl > .2) && now - controlAttempt >= .05)
                {
                    controlAttempt = now;
                    if (WriteControl(suppress))
                    {
                        if (suppress != lastSuppress) Files.Log($"Suppression request={suppress}");
                        lastControl = now; lastSuppress = suppress;
                    }
                }
                if (now - lastControl > 1) { queue.Clear(); active = false; trigger = -1; suppress = false; }
                if (trigger != lastTrigger) { Files.Log($"Trigger={trigger}; active={active}"); lastTrigger = trigger; }
                byte[] right = Protocol.Off, left = Protocol.Off;
                if (effects.TryGetValue(trigger, out var effect))
                {
                    int which = profiles[trigger]["_Which"]!.GetValue<int>();
                    if (which is 0 or 2) right = effect; if (which is 1 or 2) left = effect;
                }
                hid.Send(Protocol.Report(right, left, suppress));
                if (now - lastStatus > 2)
                    if (Files.Atomic(Files.Data("status.json"), new { running = true, active, trigger, extended_plays = queue.ExtraPlays,
                        extended_audio_active = mixer.Playing, defense_kind = queue.DefenseKind, parry_plays = queue.ParryPlays,
                        skipped_extensions = queue.Skipped, suppression_requested = suppress, sound_reference_events = extensions.SoundEvents.Count,
                        audio_underflows = audio.Underflows, session = inbox.Session, heartbeat_age = now - inbox.Last, lua_errors = state?["errors"]?.DeepClone() })) lastStatus = now;
                if (seconds > 0 && now - began >= seconds) break;
                Thread.Sleep(5);
            }
            return 0;
        }
        finally
        {
            mixer.Stop(); outputEnabled = false;
            try { audio?.Dispose(); }
            finally
            {
                try { WriteControl(false); }
                finally
                {
                    try { hid.Send(Protocol.Report(audio: false)); }
                    finally { Files.Atomic(Files.Data("status.json"), new { running = false }); Files.Log("Output stopped; both triggers released."); }
                }
            }
        }
    }
    static bool ReadableError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException or KeyNotFoundException or NullReferenceException or FormatException;
}