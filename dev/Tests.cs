using System.Text.Json.Nodes;

namespace OnimushaDualSense;

static class Tests
{
    static int assertions;
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception("TEST FAILED: " + message); }
    public static int Run()
    {
        using (var store = new SampleStore(16))
        {
            float[] exact = [0f, -0f, float.Epsilon, -.12345679f, .9876543f, 1f];
            store["first"] = exact; store["alias"] = exact;
            Check(store.StoredBytes == exact.Length * 4, "aliases store exact float32 data only once");
            store["other"] = [.2f, -.2f];
            Check(store.ResidentBytes <= 16, "wave cache enforces its byte budget");
            Check(System.Runtime.InteropServices.MemoryMarshal.AsBytes(store["first"].AsSpan()).SequenceEqual(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(exact.AsSpan())), "evicted waveform reload is bit exact including signed zero");
            var diskMixer = new Mixer(store, 1); diskMixer.Play("first");
            store["evict"] = new float[100];
            float[] diskOutput = new float[12]; diskMixer.Fill(diskOutput, 3);
            Check(diskOutput[7] == exact[3] && diskOutput[10] == .85f, "cache eviction preserves active voice samples and output limiting");
        }
        var guard = SoundCatalog.Entries.Single(entry => entry.Event == 446347563u).Variants;
        var guardFull = guard.Single(item => item.Media == 623516683u).Fixed;
        var guardLow = guard.Single(item => item.Media == 693544742u).Fixed;
        Check(guardFull.PitchCents == 0 && Math.Abs(guardFull.DelaySeconds - .2) < 1e-6 && guardFull.VolumeDb == -3
            && guardLow.PitchCents == -200 && Math.Abs(guardLow.DelaySeconds - .2) < 1e-6 && guardLow.VolumeDb == -6,
            "guard catalog preserves verified Wwise pitch, action delay and branch volume");
        var attack = SoundCatalog.Entries.Single(entry => entry.Event == 1716219749u).Variants;
        Check(attack.Any(item => item.Media == 13821785u && item.Fixed == new SoundPlayback(0, 0, -7)
            && item.RandomMin.PitchCents == -50 && item.RandomMax.PitchCents == 50), "exact attack path is not contaminated by a global media-ID collision");
        var duplicateMediaPaths = SoundCatalog.Entries.Single(entry => entry.Event == 760667172u).Variants.Where(item => item.Media == 474528256u).ToArray();
        Check(duplicateMediaPaths.Select(item => item.Fixed.VolumeDb).Order().SequenceEqual(new[] { -96d, 0d }), "same media reached through distinct Wwise paths remains distinct");
        var randomAttack = attack.First(item => item.RandomMin.PitchCents == -50 && item.RandomMax.PitchCents == 50);
        var pitchDraws = Enumerable.Range(0, SoundHaptics.DrawCount(randomAttack)).Select(i => SoundHaptics.Realize(randomAttack, i, 5).PitchCents).Order().ToArray();
        Check(pitchDraws.SequenceEqual(new[] { -40d, -20d, 0d, 20d, 40d }), "five deterministic strata represent the authored continuous random pitch range");
        var playbackSource = new float[] { 1, .75f, .5f, .25f };
        var pitchOnly = SoundHaptics.ApplyPlayback(playbackSource, 1, new SoundPlayback(-1200, 0, -12), applyVolume: false);
        Check(pitchOnly.Length == playbackSource.Length * 2 && Math.Abs(pitchOnly.Max() - 1) < .0001f, "pitch rendering changes duration without applying authored gain before tactile normalization");
        var minus3 = SoundHaptics.Scale(new float[] { .5f }, -3)[0];
        var minus6 = SoundHaptics.Scale(new float[] { .5f }, -6)[0];
        Check(Math.Abs(minus3 / minus6 - Math.Pow(10, 3.0 / 20.0)) < .001, "post-conversion Wwise volume keeps relative branch loudness");
        var limited = SoundHaptics.Scale(new float[] { .5f, 1 }, 12);
        Check(Math.Abs(limited[1] - .98f) < .00001 && Math.Abs(limited[0] / limited[1] - .5f) < .00001, "positive Wwise gain limits the whole waveform without flat-topped clipping");
        var delayedPlayback = SoundHaptics.AddDelay(new float[] { .25f, -.25f }, 1, .002);
        Check(delayedPlayback.Length == 98 && delayedPlayback.Take(96).All(v => v == 0) && delayedPlayback[96] == .25f, "authored Wwise initial delay is represented at 48 kHz");
        var branch = new[] { new SoundSwitch(10, 20, [30u, 31u]) };
        Check(ExtendedEffects.Matches(branch, JsonNode.Parse("{\"10\":31}")) && !ExtendedEffects.Matches(branch, JsonNode.Parse("{\"10\":32}"))
            && !ExtendedEffects.Matches(branch, new JsonObject()), "runtime switch values select only the matching Wwise branch and otherwise use its default");
        var diagnostic = Inspector.Parse("2026-09-07 12:30:01.123 Extended haptic PLAY id=sound_446347563 event=9 frame=88 sample=ext:sound_446347563#2");
        Check(diagnostic?.label == "ガード音" && diagnostic.sample.EndsWith("#2") && diagnostic.frame == 88, "inspector identifies actual selected variant without guessing guard subtype");
        Check(Inspector.Parse("2026-09-07 12:30:01.124 Extended haptic PLAY id=ui_sound_1434875051 event=10 frame=89")?.sample == "", "legacy log does not invent a waveform variant");
        Check(Inspector.Parse("2026-09-07 12:30:01.125 Trigger=1; active=True")?.kind == "trigger" && Inspector.Parse("unrelated line") == null, "inspector separates trigger settings and ignores unrelated log lines");
        var previewSamples = new Dictionary<string, float[]> { ["ext:example#1"] = new float[960] };
        var request = new JsonObject { ["token"] = "one", ["timestamp"] = 1000L, ["sample"] = "ext:example#1" };
        Check(Audition.Valid(request, 1100, previewSamples) && !Audition.Valid(request, 7000, previewSamples), "audition accepts selected variant and rejects stale requests");
        request["sample"] = "../anything"; Check(!Audition.Valid(request, 1100, previewSamples), "audition rejects unknown samples and paths");
        request["sample"] = ""; Check(Audition.Valid(request, 1100, previewSamples), "audition accepts explicit stop");
        Check(Audition.Delays(.02, .03, 480) == (960, 0) && Audition.Delays(.04, .01, 0) == (0, 1440), "audio/haptic alignment accounts for source onset and device latency");
        var delayed = new Mixer(new Dictionary<string, float[]> { ["one"] = [.5f, .5f] }, 1);
        delayed.Play("one", 2); var delayOutput = new float[8]; delayed.Fill(delayOutput, 2);
        Check(delayOutput.All(v => v == 0) && delayed.Playing, "scheduled onset stays silent through delay frames");
        delayed.Fill(delayOutput, 2); Check(delayOutput[2] == .5f && delayOutput[3] == .5f && !delayed.Playing, "scheduled sample starts at the exact frame after delay");
        var life = new GameLifetime();
        Check(!life.ShouldExit(false, 0) && !life.ShouldExit(false, 100), "wait for first game");
        Check(!life.ShouldExit(true, 101) && !life.ShouldExit(false, 102) && !life.ShouldExit(false, 103.9) && life.ShouldExit(false, 104), "exit grace");
        life = new(); life.ShouldExit(true, 0); life.ShouldExit(false, 1);
        Check(!life.ShouldExit(null, 3) && !life.ShouldExit(false, 4) && !life.ShouldExit(true, 5), "process query failure and recovery");
        Check(Protocol.Feedback(new float[10]).SequenceEqual(Protocol.Off), "off zones");
        Check(Convert.ToHexString(Protocol.Feedback(Enumerable.Repeat(1f, 10).ToArray())) == "21FF03FFFFFF3F00000000", "all zones packed");
        Check(Protocol.Report(audio: false).Length == 48 && Protocol.Report(audio: false)[1] == 14 && Protocol.Report()[1] == 12, "USB reports preserve rumble amplitudes");
        var samples = new Dictionary<string, float[]> { ["a"] = [1, -1, .5f, -.5f] }; var mixer = new Mixer(samples, .5f); float[] output = new float[12];
        Check(mixer.Play("a") && !mixer.Play("a"), "deduplicate active sample"); mixer.Fill(output, 3);
        Check(output.SequenceEqual(new float[] { 0, 0, .5f, -.5f, 0, 0, .25f, -.25f, 0, 0, 0, 0 }) && !mixer.Playing, "stereo routed to haptic channels only");
        Check(mixer.Play("a"), "can replay finished sample"); mixer.Stop(); Check(!mixer.Playing, "stop clears voices");
        var inbox = new Inbox();
        var state = JsonNode.Parse("{\"version\":2,\"session\":1,\"seq\":1,\"frame\":20,\"enabled\":true,\"paused\":false,\"trigger\":0,\"events\":[{\"seq\":1,\"frame\":1},{\"seq\":2,\"frame\":20}]}")!;
        Check(inbox.Accept(state, 1)!.Events.Length == 1 && inbox.Accept(state, 2) == null, "stale events and duplicate state");
        state["seq"] = 2L; state["paused"] = true;
        var paused = inbox.Accept(state, 3)!; Check(!paused.Active && paused.Trigger == -1 && paused.Events.Length == 0, "paused output silent");
        state["session"] = 2; state["seq"] = 1L; state["paused"] = false;
        Check(inbox.Accept(state, 4)!.Events.Length == 1, "new game session");
        state = JsonNode.Parse("{\"version\":3,\"session\":3,\"seq\":1,\"frame\":20,\"enabled\":true,\"paused\":true,\"ui_allowed\":true,\"trigger\":0,\"events\":[{\"seq\":1,\"frame\":20,\"kind\":\"extended\",\"id\":\"attack\"},{\"seq\":2,\"frame\":20,\"kind\":\"extended\",\"id\":\"ui_select\"}]}")!;
        var menu = inbox.Accept(state, 5)!;
        Check(!menu.Active && menu.UiAllowed && menu.Events.Length == 1 && menu.Events[0]["id"]!.ToString() == "ui_select", "paused menu admits UI only, never attack");
        state["seq"] = 2L; state["enabled"] = false;
        Check(inbox.Accept(state, 6)!.Events.Length == 0, "master disable also disables menu haptics");
        var extensionSamples = new Dictionary<string, float[]>();
        var ext = new ExtendedEffects(extensionSamples, false);
        Check(ext.Available.Count == 20, "all optional extension patterns generated");
        foreach (var effect in ext.Available.Values)
        {
            var data = extensionSamples[effect.SampleId];
            Check(data.Length > 0 && data.Length % 2 == 0 && data.All(v => float.IsFinite(v) && Math.Abs(v) < 1) && data[0] == 0 && data[^1] == 0, "finite bounded stereo synthesis with silent boundaries: " + effect.Id);
        }
        var foot = extensionSamples["ext:foot_left"];
        Check(foot.Where((v, i) => i % 2 == 0).Sum(v => Math.Abs(v)) > foot.Where((v, i) => i % 2 == 1).Sum(v => Math.Abs(v)) * 2, "left step is stronger in left actuator");
        var extensionMixer = new Mixer(extensionSamples, .5f); var queue = new FeedbackQueue(extensionMixer, ext, _ => { });
        queue.Extended("foot_left", 1, 1, 0); queue.Dispatch(false, .01);
        Check(!extensionMixer.Playing && queue.HasWork, "no extension output before native suppression ack");
        queue.Dispatch(true, .02); Check(extensionMixer.Playing, "extension starts after ack");
        queue.Clear(); queue.Extended("damage", 4, 4, 1); queue.Dispatch(true, 1.01); queue.Extended("foot_right", 5, 5, 1.02); queue.Dispatch(true, 1.03);
        Check(queue.ExtraPlays.GetValueOrDefault("damage") == 1 && !queue.ExtraPlays.ContainsKey("foot_right"), "footstep never displaces a damage impact");
        queue.Clear(); queue.Extended("attack", 6, 6, 2); queue.Dispatch(true, 2.3);
        Check(!extensionMixer.Playing && !queue.HasWork, "stale extension request expires instead of playing late");
        queue.Extended("ui_select", 7, 7, 3); queue.SetActivity(false, true); queue.Dispatch(true, 3.01);
        Check(extensionMixer.Playing, "UI effect survives gameplay pause");
        queue.SetActivity(false, false); Check(!extensionMixer.Playing && !queue.HasWork, "focus or heartbeat loss cancels all output");
        queue.Extended("soul", 8, 8, 4); queue.Dispatch(true, 4.01); queue.Clear(); queue.Extended("soul", 9, 9, 4.05); queue.Dispatch(true, 4.06);
        Check(queue.ExtraPlays.GetValueOrDefault("soul") == 1, "soul bursts are rate-limited");
        queue.Clear(); queue.Extended("hit", 10, 10, 5); queue.Dispatch(true, 5.01);
        extensionMixer.Fill(new float[48000], 12000);
        queue.Extended("attack", 11, 11, 5.08); queue.Dispatch(true, 5.09);
        Check(!extensionMixer.Playing, "contact prevents a delayed swish after impact");
        queue.Extended("foot_right", 12, 12, 5.2); queue.Dispatch(true, 5.21);
        Check(!extensionMixer.Playing, "combat masks steps after the impact waveform ends");
        queue.Extended("foot_right", 13, 13, 5.5); queue.Dispatch(true, 5.51);
        Check(extensionMixer.Playing, "footsteps resume after combat settles");
        var holdMixer = new Mixer(extensionSamples, .5f);
        var holdQueue = new FeedbackQueue(holdMixer, ext, _ => { });
        holdQueue.Extended("hit", 1, 1, 10); holdQueue.Dispatch(true, 10.01);
        holdMixer.Fill(new float[48000 * 4], 48000);
        Check(!holdQueue.HasWork && holdQueue.RequiresSuppression(true, false, 10.5), "native rumble stays suppressed between combat pulses");
        Check(!holdQueue.RequiresSuppression(true, false, 10.81), "native rumble resumes after combat quiets for 800ms");
        Check(!holdQueue.RequiresSuppression(false, false, 10.5) && !holdQueue.RequiresSuppression(false, true, 10.5), "combat hold never suppresses a menu or unfocused game");
        holdQueue.SetActivity(false, true);
        Check(!holdQueue.CombatWindow(10.5), "pausing cancels combat suppression hold");
        double Energy(string id) => extensionSamples["ext:" + id].Sum(v => (double)v * v);
        Check(Energy("attack") < Energy("hit") * .4, "air sweep is materially lighter than physical impact");
        float[] recording = new float[12000];
        for (int i = 0; i < recording.Length; i++) recording[i] = (float)(Math.Sin(2 * Math.PI * 2400 * i / 48000) * Math.Exp(-i / 2100.0));
        var translated = SoundHaptics.Convert(recording, "contact");
        Check(translated.Length <= 48000 * .50 * 2 && translated.All(v => float.IsFinite(v) && Math.Abs(v) <= .971) && translated[0] == 0 && translated[^1] == 0, "sound conversion bounded and smoothly gated");
        Check(translated.Sum(v => (double)v * v) > 10, "high-frequency source retains substantial tactile energy");
        var silence = SoundHaptics.Convert(new float[4800], "ui");
        Check(silence.All(v => v == 0), "silent sound never becomes an invented vibration");
        double Rms(float[] values) => Math.Sqrt(values.Sum(v => (double)v * v) / Math.Max(1, values.Length));
        var quietInput = recording.Select(v => v * .001f).ToArray();
        Check(Rms(SoundHaptics.Convert(quietInput, "contact")) < Rms(translated) * .02, "very quiet recordings are not normalized into strong impacts");
        var leading = new float[4800 + recording.Length]; Array.Copy(recording, 0, leading, 4800, recording.Length);
        var aligned = SoundHaptics.Convert(leading, "contact");
        Check(aligned.Take(4800 * 2).All(v => v == 0) && aligned.Skip(4800 * 2).Any(v => v != 0), "source-leading silence survives tactile conversion");
        var longTone = Enumerable.Range(0, 24000).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 140 * i / 48000))).ToArray();
        var tail = SoundHaptics.Convert(longTone, "contact");
        Check(tail.Length == 48000 && Rms(tail.Skip(30000).Take(8000).ToArray()) < Rms(tail.Skip(4000).Take(8000).ToArray()), "impact tail extends beyond old cap with natural damping");
        Check(Rms(SoundHaptics.Convert(longTone, "ui")) < Rms(tail), "UI has less energy than contact");
        var stereoInput = recording.SelectMany(v => new[] { v, v }).ToArray();
        var rendered = SoundHaptics.Render(stereoInput, "contact", new(0, .05, -6));
        Check(rendered.Take(4800).All(v => v == 0) && Math.Abs(Rms(rendered.Skip(4800).ToArray()) / Rms(translated) - Math.Pow(10, -6.0 / 20)) < .001, "shared source rendering preserves Wwise delay and gain");
        Check(SoundHaptics.ApplyPlayback([], 2, new()).Length == 0, "empty source resampling is safe");
        var scrapeTone = Enumerable.Range(0, 72000).Select(i => (float)(.03 * Math.Sin(2 * Math.PI * 2400 * i / 48000))).ToArray();
        var friction = SoundHaptics.Convert(scrapeTone, "parry");
        Check(friction.Length == 144000 && Rms(friction.Skip(57600).Take(48000).ToArray()) > .01,
            "parry friction preserves sustained source texture after 600ms");
        var guardTexture = SoundHaptics.Convert(scrapeTone, "guard");
        Check(guardTexture.Length <= 81600 && Rms(guardTexture.Skip(48000).Take(24000).ToArray()) > .01, "ordinary guard preserves source body after the old 400ms cap");
        Check(Rms(SoundHaptics.Convert(scrapeTone.Select(v => v * .2f).ToArray(), "guard")) < Rms(guardTexture) * .3,
            "defense conversion preserves relative source strength rather than peak-normalizing");
        Check(SoundHaptics.Convert(new float[72000], "parry").All(v => v == 0), "silent parry source remains silent");
        var positionSamples = new Dictionary<string, float[]> { ["short"] = Enumerable.Repeat(.1f, 96000).ToArray(), ["long"] = Enumerable.Range(0, 144000).Select(i => i / 200000f).ToArray() };
        var positionMixer = new Mixer(positionSamples, 1); positionMixer.Play("short"); positionMixer.Fill(new float[4000], 1000);
        Check(positionMixer.Replace("short", "long"), "promotion replaces an existing voice");
        var positionOut = new float[4]; positionMixer.Fill(positionOut, 1);
        Check(Math.Abs(positionOut[2] - .01) < .000001, "promotion preserves elapsed sample position");
        positionMixer.FadeOut("long"); positionMixer.Fill(new float[1536], 384);
        Check(!positionMixer.Playing, "state exit finishes fade within 8ms");
        if (File.Exists(Files.Data("sound_haptics.json")))
        {
            var loaded = PreparedWaves.Load();
            using var converted = loaded.Samples;
            var sounds = loaded.Effects;
            if (Files.Read(Files.Data("sound_haptics.json"))["format"]?.GetValue<int>() == 2)
                Check(converted.StoredBytes < converted.Keys.Sum(key => (long)converted[key].Length * 4), "identical playback conditions share stored arrays without repeated gain");
            SampleStore.FinishLoading();
            var replayMixer = new Mixer(converted, 1);
            var replayLog = new List<string>();
            var replay = new FeedbackQueue(replayMixer, sounds, replayLog.Add);
            var recordedSwitches = JsonNode.Parse("{\"1572667004\":782826392,\"3070412364\":930712164,\"3688638200\":2185786256}");
            replay.Extended("sound_446347563", 261, 18456, 0, recordedSwitches);
            replay.Extended("sound_3022315518", 262, 18457, .017, recordedSwitches);
            replay.Dispatch(true, .018);
            Check(replay.ExtraPlays.GetValueOrDefault("sound_446347563") == 1 && !replay.ExtraPlays.ContainsKey("sound_3022315518"),
                "captured guard event 261 survives contact event 262 before suppression acknowledgment");
            replay.Extended("sound_3022315518", 400, 18462, .1, recordedSwitches);
            replay.Extended("perfect_dodge", 401, 18463, .11);
            replay.Dispatch(true, .12);
            Check(!replay.ExtraPlays.ContainsKey("sound_3022315518") && !replay.ExtraPlays.ContainsKey("perfect_dodge"), "guard remains protected through its 200ms source delay");
            var guardOutput = new float[48000 * 4]; replayMixer.Fill(guardOutput, 48000);
            Check(guardOutput.Any(v => Math.Abs(v) > .001), "captured guard request actually renders non-silent output after its source delay");
            Check(replay.Skipped.ContainsKey("sound_3022315518:higher_priority_active_or_pending"), "priority rejection has an explicit diagnostic reason");
            replay.Clear(); replay.Extended("perfect_dodge", 402, 19000, 2); replay.Dispatch(true, 2.01);
            replay.Extended("sound_3022315518", 403, 19001, 2.02, recordedSwitches); replay.Dispatch(true, 2.03);
            Check(replay.ExtraPlays.ContainsKey("sound_3022315518"), "source-derived combat contact preempts generic perfect-dodge synthesis");
            Check(sounds.SoundEvents.Count > 300 && sounds.Variants.Count > 300, "audible bank-verified sound references load with hashes");
            Check(sounds.ParrySamples.Count > 0 && sounds.ParrySamples.All(p => converted[p.Value].Length > converted[p.Key].Length), "actual guard sources have dedicated longer parry variants");
            foreach (string defense in new[] { "guard", "deflect", "dodge", "unknown", "none", "parry" })
            {
                var dm = new Mixer(converted, 1); var dq = new FeedbackQueue(dm, sounds, _ => { });
                dq.SetDefense(defense, 0); dq.Extended("sound_446347563", 1, 1, .01, recordedSwitches); dq.Dispatch(true, .02);
                Check(dq.ParryPlays == (defense == "parry" ? 1 : 0), "friction selection requires explicit parry: " + defense);
                if (defense == "parry")
                {
                    dm.Fill(new float[48000], 12000); dq.SetDefense("none", .27); dm.Fill(new float[1536], 384);
                    Check(!dm.Playing, "leaving actual parry stops sustained waveform");
                }
            }
            var lateMixer = new Mixer(converted, 1); var lateQueue = new FeedbackQueue(lateMixer, sounds, _ => { });
            lateQueue.Extended("sound_446347563", 1, 1, 0, recordedSwitches); lateQueue.Dispatch(true, .01);
            lateQueue.SetDefense("parry", .05); Check(lateQueue.ParryPlays == 1, "late state promotes recent guard sound");
            lateQueue.Clear(); lateQueue.Extended("sound_446347563", 2, 2, 1, recordedSwitches); lateQueue.Dispatch(true, 1.01);
            lateQueue.SetDefense("parry", 1.3); Check(lateQueue.ParryPlays == 1, "old guard sound cannot be promoted by later parry");
            lateQueue.Clear(); lateQueue.SetDefense("guard", 2); lateQueue.Extended("sound_446347563", 3, 3, 2.01, recordedSwitches); lateQueue.Dispatch(true, 2.02);
            lateQueue.SetDefense("parry", 2.05); Check(lateQueue.ParryPlays == 1, "confirmed normal guard cannot be promoted into parry");
            Check(sounds.SoundChoices.All(item => item.Value.Any(choice => converted[choice.SampleId].Any(value => value != 0))), "silent-only sound events cannot suppress native rumble");
            Check(converted.Values.All(v => v.All(x => float.IsFinite(x) && Math.Abs(x) <= 1)), "unity-gain source waveforms stay within normalized range before the mixer limiter");
            if (File.Exists(Files.Data("finisher-replay.json")))
            {
                var fm = new Mixer(converted, 1); var messages = new List<string>();
                var fq = new FeedbackQueue(fm, sounds, messages.Add);
                long previous = 0; int count = 0;
                foreach (var row in Files.Read(Files.Data("finisher-replay.json")).AsArray())
                {
                    long frame = row!["frame"]!.GetValue<long>();
                    int frames = (int)Math.Clamp((frame - previous) * 800, 0, 48000 * 3); previous = frame;
                    fm.Fill(new float[frames * 4], frames);
                    fq.Extended(row["id"]!.GetValue<string>(), row["seq"]!.GetValue<long>(), frame, frame / 60.0, row["switches"], row["technique"]!.GetValue<string>());
                    fq.Dispatch(true, frame / 60.0); count++;
                }
                Check(count >= 15 && fq.ExtraPlays.Values.Sum() == count && fq.Skipped.Count == 0, "captured issen and dead heat cuts have no lost sound events");
                var plays = messages.Where(m => m.StartsWith("Extended haptic PLAY")).ToArray();
                Check(plays.All(m => m.Contains("sample=ext:cut_")), "every captured special slash uses source-derived cut rendering");
                Check(plays.All(m => converted[m.Split("sample=")[1].Split(' ')[0]].Any(v => Math.Abs(v) > .001)), "all selected special-cut sources produce audible-strength tactile output");
                foreach (string label in new[] { "issen", "deadheat" })
                {
                    var line = plays.First(m => m.Contains("id=" + label + "_"));
                    Check(Inspector.Parse("2026-09-08 20:00:00.000 " + line)!.label.Contains(label == "issen" ? "崩し一閃" : "相子剣戟"), "inspector identifies special technique: " + label);
                }
                fq.Clear(); messages.Clear(); fq.Extended("sound_1716219749", 10000, 10000, 1000, recordedSwitches); fq.Dispatch(true, 1000);
                Check(messages.Any(m => m.Contains("sample=ext:sound_1716219749")), "ordinary slash retains its existing rendering");
                fq.Clear(); messages.Clear(); fq.Extended("sound_1530332381", 10001, 10001, 1001, recordedSwitches); fq.Dispatch(true, 1001);
                Check(!fm.Playing, "special motion source does not vibrate outside a technique");
                Console.WriteLine($"Special-attack recording: {count} source-based cuts replayed with no rejection.");
            }
            var pairMixer = new Mixer(converted, 1); var pairQueue = new FeedbackQueue(pairMixer, sounds, _ => { });
            pairQueue.SetDefense("guard", 0); pairQueue.Extended("sound_446347563", 1, 100, 0, recordedSwitches);
            pairQueue.Extended("sound_3022315518", 2, 101, .017, recordedSwitches); pairQueue.Dispatch(true, .018);
            Check(pairQueue.ExtraPlays.GetValueOrDefault("sound_446347563") == 1 && pairQueue.ExtraPlays.GetValueOrDefault("sound_3022315518") == 1,
                "confirmed guard layers paired collision without replacing the guard body");
            pairQueue.Extended("sound_3283242646", 3, 102, .025, recordedSwitches); pairQueue.Dispatch(true, .03);
            Check(!pairQueue.ExtraPlays.ContainsKey("sound_3283242646"), "only one nearby collision can layer per guard");
            if (File.Exists(Files.Bundled("defense_haptics.json")))
            {
                var manifest = Files.Read(Files.Bundled("defense_haptics.json"))["events"]!;
                foreach (string ev in new[] { "1466807829", "3469150555", "2024084845" })
                {
                    string id = "defense_sound_" + ev;
                    var dm = new Mixer(converted, 1); var dq = new FeedbackQueue(dm, sounds, _ => { });
                    dq.SetDefense("guard", 0); dq.Extended(id, 1, 1, 0); dq.Dispatch(true, .01);
                    Check(!dm.Playing, "dedicated parry sound cannot fire during normal guard: " + ev);
                    dq.SetDefense("parry", 1); dq.Extended(id, 2, 2, 1); dq.Dispatch(true, 1.01);
                    Check(dm.Playing && dq.ParryPlays == 1, "actual parry start is mapped: " + ev);
                    dm.Fill(new float[19200], 4800); dq.StopParry(id); dm.Fill(new float[1536], 384);
                    Check(!dm.Playing, "actual Wwise stop ends the corresponding friction: " + ev);
                    Check(manifest[ev]!["variants"]!.AsArray()[0]!["layers"]!.AsArray().Count == 3, "three audible Wwise layers retained: " + ev);
                }
                foreach (string ev in new[] { "250823922", "1644781178" })
                {
                    var dm = new Mixer(converted, 1); var dq = new FeedbackQueue(dm, sounds, _ => { });
                    dq.SetDefense("deflect", 0); dq.Extended("defense_sound_" + ev, 1, 1, 0); dq.Dispatch(true, .01);
                    Check(dm.Playing && dq.ParryPlays == 0, "deflect uses its own contact source, never friction: " + ev);
                    dq.SetDefense("guard", .2); dq.Extended("sound_446347563", 2, 20, .3, recordedSwitches); dq.Dispatch(true, .31);
                    Check(dq.ExtraPlays.ContainsKey("sound_446347563"), "a later guard can replace deflect ringing: " + ev);
                }
                var releaseMixer = new Mixer(converted, 1); var releaseQueue = new FeedbackQueue(releaseMixer, sounds, _ => { });
                releaseQueue.Extended("defense_sound_1902560855", 1, 1, 0); releaseQueue.StopParry("defense_sound_1466807829"); releaseQueue.Dispatch(true, .01);
                Check(releaseMixer.Playing && releaseQueue.ParryPlays == 0, "stop event preserves the separate release sound after action exit");
                var variant = manifest["1466807829"]!["variants"]!.AsArray()[0]!;
                var audible = DefenseSounds.Render(variant, "parry", true, new());
                Check(audible.Any(v => v != 0) && audible.Length >= 48000, "audition renders the same compound source layers");
                releaseQueue.SetDefense("guard", .2); releaseQueue.Extended("sound_446347563", 2, 20, .3, recordedSwitches); releaseQueue.Dispatch(true, .31);
                Check(releaseQueue.ExtraPlays.ContainsKey("sound_446347563"), "new guard contact can replace the previous parry release ringing");
                if (File.Exists(Files.Data("defense-replay.json")))
                {
                    var dm = new Mixer(converted, 1); var dq = new FeedbackQueue(dm, sounds, _ => { });
                    long previousFrame = 0; int expected = 0;
                    foreach (var record in Files.Read(Files.Data("defense-replay.json")).AsArray())
                    {
                        long frame = record!["frame"]!.GetValue<long>();
                        int elapsed = (int)Math.Clamp((frame - previousFrame) * 800, 0, 48000 * 9); previousFrame = frame;
                        dm.Fill(new float[elapsed * 4], elapsed);
                        long bits = record["state"]!.GetValue<long>();
                        string kind = (bits & 536870912) != 0 ? "dodge" : (bits & 134217728) != 0 ? "deflect" : (bits & 268435456) != 0 ? "parry" : (bits & 117440512) != 0 ? "guard" : "none";
                        double now = frame / 60.0; dq.SetDefense(kind, now);
                        if (record["event"] is JsonNode ev && manifest[ev.ToString()] is JsonNode source)
                        {
                            string family = source["family"]!.GetValue<string>();
                            if (family == "parry_stop") dq.StopParry("defense_sound_" + source["stops"]!.ToString());
                            else
                            {
                                dq.Extended("defense_sound_" + ev.ToString(), record["seq"]!.GetValue<long>(), frame, now);
                                expected++;
                            }
                        }
                        dq.Dispatch(true, now);
                    }
                    Check(expected > 20 && dq.ExtraPlays.Values.Sum() == expected, "captured playthrough replays every registered parry/deflect/release without loss");
                    Check(dq.Skipped.Count == 0, "captured defense sequence has no priority, state or cooldown rejection");
                    Console.WriteLine($"Defense recording: {expected} sound events replayed; {dq.ParryPlays} friction starts; no rejected events.");
                }
            }
        }
        string scratch = Files.Data("test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        string original = Files.Root;
        try
        {
            string file = Path.Combine(scratch, "ipc.json"); Files.Atomic(file, new { value = 1 });
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Check(!Files.Atomic(file, new { value = 2 }) && Files.Read(file)["value"]!.GetValue<int>() == 1, "Windows rename conflict retains complete old JSON");
            Check(Files.Atomic(file, new { value = 3 }) && Files.Read(file)["value"]!.GetValue<int>() == 3, "IPC retries recover after unlock");
            Files.Root = scratch; Directory.CreateDirectory(Files.Data(""));
            var firstIndex = PreparedWaves.Prepare();
            string waveIndex = Files.Data("waves/index.json");
            var indexTime = File.GetLastWriteTimeUtc(waveIndex);
            new Configuration("test-game", .5f).Save();
            var settings = Configuration.Read();
            Check(settings.Game == "test-game" && settings.Gain == .5f, "single gain configuration round trips");
            var configFields = Files.Read(Files.At("config.json")).AsObject();
            Check(configFields.Count == 2 && configFields.ContainsKey("game") && configFields.ContainsKey("gain"), "configuration contains only game and gain");
            var loadedAgain = PreparedWaves.Load();
            using (loadedAgain.Samples)
            {
                var singleGain = new Mixer(loadedAgain.Samples, settings.Gain);
                singleGain.Play("ext:attack"); var outputGain = new float[400]; singleGain.Fill(outputGain, 100);
                var expectedGain = loadedAgain.Samples["ext:attack"];
                Check(Enumerable.Range(0, 100).All(i => outputGain[i * 4 + 2] == expectedGain[i * 2] * .5f), "master gain is applied exactly once at output");
            }
            Check(File.GetLastWriteTimeUtc(waveIndex) == indexTime, "gain changes do not rebuild prepared WAVs");
            var start = AppHost.StartInfo("run", "argument with spaces");
            Check(start.ArgumentList.Count == 3 && start.ArgumentList[0].EndsWith("OnimushaDualSense.dll") && start.ArgumentList[1] == "run" && start.ArgumentList[2] == "argument with spaces", "DLL relaunch preserves assembly path and argument boundaries");
            var firstWave = firstIndex.Waves.Values.First();
            string firstPath = Files.Data("waves/" + firstWave.File);
            var wavBytes = File.ReadAllBytes(firstPath); wavBytes[^1] ^= 1; File.WriteAllBytes(firstPath, wavBytes);
            bool corrupt = false;
            try { PreparedWaves.ReadWave(firstPath, firstWave.Length, firstWave.Hash); }
            catch (InvalidDataException) { corrupt = true; }
            Check(corrupt, "corrupt persistent WAV is rejected before playback");
            PreparedWaves.Prepare(true);
            Check(PreparedWaves.ReadWave(firstPath, firstWave.Length, firstWave.Hash).Length == firstWave.Length, "forced prepare repairs corrupted WAV");
            File.Delete(firstPath); PreparedWaves.Prepare();
            Check(File.Exists(firstPath), "missing persistent WAV triggers regeneration");
            string oldFingerprint = PreparedWaves.Fingerprint();
            File.WriteAllText(Files.Data("sound_haptics.json"), "{\"format\":2}");
            Check(PreparedWaves.Fingerprint() != oldFingerprint, "source manifest changes invalidate prepared cache");
            string game = Path.Combine(scratch, "game"); Directory.CreateDirectory(Files.Bundled(""));
            File.WriteAllText(Files.Bundled("onimusha_dualsense_bridge.lua"), "-- Onimusha DualSense bridge\nnew");
            Directory.CreateDirectory(Path.Combine(game, "reframework/autorun")); string dest = Path.Combine(game, "reframework/autorun/onimusha_dualsense_bridge.lua");
            File.WriteAllText(dest, "-- Onimusha DualSense bridge\nold"); Setup.Install(game);
            Check(File.ReadAllText(Files.Data("backup.lua")).EndsWith("old") && File.ReadAllText(dest).EndsWith("new"), "installer backs up prior own Lua");
            File.WriteAllText(dest, "user change"); bool refused = false; try { Setup.Install(game); } catch (InvalidDataException) { refused = true; }
            Check(refused && File.ReadAllText(dest) == "user change", "modified Lua protected");
        }
        finally { Files.Root = original; Directory.Delete(scratch, true); }
        Console.WriteLine($"PASS: {assertions} assertions (protocol, mixer, state, lifecycle, Windows IPC, installer)"); return 0;
    }
}
