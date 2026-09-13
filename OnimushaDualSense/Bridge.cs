using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnimushaDualSense;

static class Bridge
{
    // A bounded polling interval that trims idle CPU/USB traffic while keeping companion latency low.
    const int CompanionLoopSleepMilliseconds = 8;
    // Keep a margin below the one-second freshness watchdog when a write is delayed.
    const double ControlHeartbeatSeconds = .5;
    const double ControlWatchdogSeconds = 1.0;

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
        var effects = profiles.ToDictionary(p => p.Key, p => Protocol.Feedback(
            p.Value["_PowerList"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray(), config.AdaptiveTriggerStrength));
        var mixer = new Mixer(samples, config.Gain);
        var queue = new FeedbackQueue(mixer, extensions, Files.Log);
        var inbox = new Inbox();
        var reader = new ChangedJsonReader();
        string routesToken = Guid.NewGuid().ToString("N");
        string routesPath = Path.Combine(config.Game, "reframework/data/onimusha_dualsense_routes.json");
        if (!Files.Atomic(routesPath, new { token = routesToken, sound_events = extensions.SoundEvents }))
            throw new IOException("Cannot publish sound routes");
        bool outputEnabled = false;
#if DEVELOPER
        var defenseTrace = new DefenseTrace();
#endif
        bool WriteControl(bool suppress)
        {
#if DEVELOPER
            // `trace_lua_session` scopes the trace ACK to the exact Lua
            // lifetime that produced the rows. A companion restart alone must
            // never acknowledge rows from a later script reload.
            return Files.Atomic(controlPath, new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), suppress_legacy = suppress, output_enabled = outputEnabled, adaptive_triggers = config.AdaptiveTriggers, routes_token = routesToken, ack_session = inbox.Session == null ? null : JsonNode.Parse(inbox.Session), ack_event = inbox.LastEvent, defense_trace = true, trace_session = defenseTrace.Session, trace_lua_session = defenseTrace.LuaSession, trace_ack = defenseTrace.LastSeq });
#else
            return Files.Atomic(controlPath, new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), suppress_legacy = suppress, output_enabled = outputEnabled, adaptive_triggers = config.AdaptiveTriggers, routes_token = routesToken, ack_session = inbox.Session == null ? null : JsonNode.Parse(inbox.Session), ack_event = inbox.LastEvent });
#endif
        }
        using var hid = new HidRecovery(() => new Hid(), Files.Log);
        Audio? audio = null;
        BluetoothHaptics? wireless = null;
        try
        {
            Hid.Device? detected = null;
            try
            {
                var devices = Hid.Find();
                if (devices.Count == 1) detected = devices[0];
                else Files.Log($"C# companion: HID preflight found {devices.Count} supported DualSense devices; using the audio path until one device is available.");
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Files.Log($"C# companion: HID preflight failed with native error {e.NativeErrorCode}; using the audio path.");
            }
            bool useBluetoothHaptics = detected != null
                && detected.Transport == HidTransport.Bluetooth
                && detected.ReportLength >= BluetoothHapticsProtocol.ReportLength;
            if (useBluetoothHaptics)
            {
                wireless = new BluetoothHaptics(mixer, hid, Files.Log);
                Files.Log($"C# companion: Bluetooth HID haptics stream active; direct PCM output report selected ({detected!.ReportLength} bytes); {extensions.Available.Count} feedback patterns loaded.");
            }
            else
            {
                try
                {
                    audio = new Audio(mixer);
                    Files.Log($"C# companion: DualSense HID recovery active; four-channel WASAPI open; {extensions.Available.Count} feedback patterns loaded.");
                }
                catch (InvalidOperationException e) when (detected == null)
                {
                    // A paired controller can appear after the companion starts. If there is
                    // no audio endpoint to use while it is absent, keep the HID worker alive
                    // so the next Bluetooth enumeration can open the direct haptics path.
                    Files.Log($"C# companion: four-channel WASAPI unavailable ({e.Message}); waiting for a Bluetooth HID haptics device.");
                    wireless = new BluetoothHaptics(mixer, hid, Files.Log);
                }
            }
#if DEVELOPER
            using var audition = new Audition();
#endif
            var lifetime = new GameLifetime();
            JsonNode? state = null; Accepted? last = null;
            double began = Files.Now, nextProcess = 0, lastStatus = 0, lastControl = began, controlAttempt = double.NegativeInfinity;
            int lastTrigger = int.MinValue;
            bool? lastSuppress = null;
            while (!File.Exists(Files.Data("stop.request")))
            {
                double now = Files.Now;
                audio?.CheckHealth();
#if DEVELOPER
                audition.Read(samples, queue, now);
                if (audition.Active)
                {
                    outputEnabled = false;
                    if (lastSuppress != true || now - lastControl > ControlHeartbeatSeconds)
                        if (WriteControl(true)) { lastSuppress = true; lastControl = now; }
                    bool freshGame = (DateTime.UtcNow - File.GetLastWriteTimeUtc(statePath)).TotalSeconds < .75;
                    bool ack = false;
                    if (freshGame) try { ack = Files.Read(statePath)["legacy_suppressed"]?.GetValue<bool>() == true; } catch (Exception e) when (ReadableError(e)) { }
                    hid.TrySend(Protocol.Report(), now);
                    audition.Tick(mixer, Focus.IsGame(), ack && now - lastControl < 1, freshGame, now, audio?.OutputLatency ?? 0);
                    Thread.Sleep(CompanionLoopSleepMilliseconds); continue;
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
                        var next = reader.ReadChanged(statePath); var accepted = next == null ? null : inbox.Accept(next, now);
                        if (accepted != null)
                        {
                            state = next!; last = accepted;
#if DEVELOPER
                            defenseTrace.Consume(state);
#endif
                            queue.SetNativeBow(focus && accepted.NativeBow);
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
                                            queue.Extended(id, ev["seq"]!.GetValue<long>(), ev["frame"]!.GetValue<long>(), now, ev["switches"], ev["technique"]?.GetValue<string>() ?? "", ev["defense_kind"]?.GetValue<string>());
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
                queue.SetNativeBow(gameplay && last!.NativeBow);
                int trigger = active ? last!.Trigger : -1;
                outputEnabled = config.Gain > 0 && (active || uiAllowed);
                queue.SetDefense(gameplay ? state?["defense_kind"]?.GetValue<string>() ?? "none" : "none", now);
                queue.SetActivity(config.Gain > 0 && gameplay, config.Gain > 0 && uiAllowed);
                queue.Dispatch(state?["legacy_suppressed"]?.GetValue<bool>() == true, now);
                bool suppress = queue.RequiresSuppression(gameplay, uiAllowed, now);
                if ((suppress != lastSuppress || now - lastControl > ControlHeartbeatSeconds) && now - controlAttempt >= .05)
                {
                    controlAttempt = now;
                    if (WriteControl(suppress))
                    {
                        if (suppress != lastSuppress) Files.Log($"Suppression request={suppress}");
                        lastControl = now; lastSuppress = suppress;
                    }
                }
                if (now - lastControl > ControlWatchdogSeconds) { queue.Clear(); active = false; trigger = -1; suppress = false; }
                if (trigger != lastTrigger) { Files.Log($"Trigger={trigger}; active={active}"); lastTrigger = trigger; }
                byte[] right = Protocol.Off, left = Protocol.Off;
                if (config.AdaptiveTriggers && effects.TryGetValue(trigger, out var effect))
                {
                    int which = profiles[trigger]["_Which"]!.GetValue<int>();
                    if (which is 0 or 2) right = effect; if (which is 1 or 2) left = effect;
                }
                hid.TrySend(Protocol.Report(right, left, suppress), now);
                if (now - lastStatus > 2)
                    if (Files.Atomic(Files.Data("status.json"), new { running = true, active, trigger, extended_plays = queue.ExtraPlays,
                        extended_audio_active = mixer.Playing, defense_kind = queue.DefenseKind, parry_plays = queue.ParryPlays,
                        skipped_extensions = queue.Skipped, suppression_requested = suppress, sound_reference_events = extensions.SoundEvents.Count,
                        audio_underflows = audio?.Underflows ?? 0, session = inbox.Session, heartbeat_age = now - inbox.Last, lua_errors = state?["errors"]?.DeepClone() })) lastStatus = now;
                if (seconds > 0 && now - began >= seconds) break;
                Thread.Sleep(CompanionLoopSleepMilliseconds);
            }
            return 0;
        }
        finally
        {
            mixer.Stop(); outputEnabled = false;
            try { wireless?.Dispose(); }
            finally
            {
                try { audio?.Dispose(); }
                finally
                {
                    try { WriteControl(false); }
                    finally
                    {
                        try { hid.Release(); }
                        finally { Files.Atomic(Files.Data("status.json"), new { running = false }); Files.Log("Output stopped; both triggers released."); }
                    }
                }
            }
        }
    }
    static bool ReadableError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException or KeyNotFoundException or NullReferenceException or FormatException;
}
