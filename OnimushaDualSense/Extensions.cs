using System.Text.Json.Nodes;

namespace OnimushaDualSense;

record ExtraEffect(string Id, string Category, int Priority, double Cooldown, double Duration,
    double Frequency, double EndFrequency, float Amplitude, float Balance = 0, int Pulses = 1, string Family = "")
{
    public string SampleId => "ext:" + Id;
    public bool IsUi => Category == "ui";
    public string Kind => Family.Length == 0 ? Id : Family;
}
record SoundChoice(string SampleId, SoundSwitch[] Switches);
record SoundPreviewInfo(uint Media, uint Path, int Draw, int DrawCount, double PitchCents,
    double DelayMs, double VolumeDb, bool Randomized, bool DynamicRtpc, bool DynamicState);

sealed class ExtendedEffects
{
    public static readonly ExtraEffect[] Definitions =
    [
        new("foot_left", "footsteps", 1, .16, .028, 105, 65, .23f, -.72f),
        new("foot_right", "footsteps", 1, .16, .028, 105, 65, .23f, .72f),
        new("run_left", "footsteps", 1, .13, .035, 100, 55, .32f, -.65f),
        new("run_right", "footsteps", 1, .13, .035, 100, 55, .32f, .65f),
        new("attack", "combat", 4, .12, .065, 190, 260, .25f, .2f),
        new("hit", "combat", 6, .07, .055, 155, 55, .85f),
        new("damage", "defense", 8, .32, .110, 90, 42, .82f),
        new("guard", "defense", 6, .12, .040, 210, 125, .72f, -.25f),
        new("dodge", "movement", 3, .22, .095, 85, 145, .48f),
        new("perfect_dodge", "defense", 7, .30, .160, 125, 205, .70f, 0, 2),
        new("land", "movement", 4, .30, .115, 100, 45, .65f),
        new("heal", "recovery", 3, .60, .240, 100, 180, .48f, 0, 2),
        new("soul", "souls", 2, .24, .105, 95, 190, .45f, -.4f),
        new("pickup", "pickups", 3, .28, .140, 160, 210, .47f, 0, 2),
        new("lock_on", "combat", 2, .25, .045, 170, 170, .38f),
        new("power", "powers", 7, .65, .320, 55, 160, .80f, 0, 2),
        new("finisher", "powers", 7, .45, .220, 110, 42, .85f),
        new("ui_select", "ui", 2, .075, .016, 185, 185, .28f),
        new("ui_decide", "ui", 4, .12, .055, 160, 220, .42f),
        new("ui_cancel", "ui", 4, .12, .045, 180, 100, .35f)
    ];
    public Dictionary<string, string[]> Variants { get; } = [];
    public Dictionary<string, SoundChoice[]> SoundChoices { get; } = [];
    public Dictionary<string, string> ParrySamples { get; } = [];
    public Dictionary<string, string> CutSamples { get; } = [];
    public Dictionary<string, SoundPreviewInfo> SoundInfo { get; } = [];
    public Dictionary<uint, object> SoundEvents { get; } = [];
    public Dictionary<string, ExtraEffect> Available { get; } = [];
    public HashSet<string> PlayableSamples()
    {
        var result = Available.Values.Where(e => !Variants.ContainsKey(e.Id)).Select(e => e.SampleId).ToHashSet();
        result.UnionWith(Variants.Values.SelectMany(v => v));
        result.UnionWith(CutSamples.Where(p => result.Contains(p.Key)).Select(p => p.Value).ToArray());
        result.UnionWith(ParrySamples.Where(p => result.Contains(p.Key)).Select(p => p.Value).ToArray());
        return result;
    }
    internal ExtendedEffects() { }
    public ExtendedEffects(IDictionary<string, float[]> samples, bool loadSound = true)
    {
        foreach (var effect in Definitions)
        {
            Available[effect.Id] = effect;
            samples[effect.SampleId] = Synthesize(effect, 1);
        }
        if (loadSound && File.Exists(Files.Data("sound_haptics.json")))
        {
            var manifest = Files.Read(Files.Data("sound_haptics.json"));
            if (manifest["format"]?.GetValue<int>() != 2) throw new InvalidDataException("Run Setup.cmd to regenerate sound assets");
            var sourceCache = new Dictionary<string, float[]>();
            var renderCache = new Dictionary<string, string>();
            foreach (var source in SoundCatalog.Entries)
            {
                if (manifest[source.Event.ToString()] is not JsonNode node) continue;
                var files = node["variants"]!.AsArray();
                bool eventAudible = false;
                int priority = source.Family switch { "footsteps" => 1, "ui" => 4, "attack" => 4, "guard" => 10, "contact" => 9, _ => 7 };
                double cooldown = source.Family switch { "footsteps" => .13, "ui" => .07, "attack" => .12, _ => .065 };
                foreach (string side in source.Family == "footsteps" ? new[] { "_left", "_right" } : new[] { "" })
                {
                    string id = source.Id + side;
                    var effect = new ExtraEffect(id, source.Category, priority, cooldown, .23, 0, 0, 1, Family: source.Family);
                    var variants = new List<string>();
                    var choices = new List<SoundChoice>();
                    for (int i = 0; i < files.Count; i++)
                    {
                        var file = files[i]!;
                        float[] data;
                        string renderKey = string.Join('|', file["source_sha256"]!.ToString(), source.Family,
                            source.Category, side, file["pitch_cents"]!.ToJsonString(), file["delay_ms"]!.ToJsonString(), file["volume_db"]!.ToJsonString());
                        bool reused = renderCache.ContainsKey(renderKey);
                        if (reused) data = samples[renderCache[renderKey]];
                        else
                            data = SoundHaptics.Render(SoundHaptics.LoadShared(file, sourceCache), source.Family, SoundHaptics.Playback(file));
                        eventAudible |= data.Any(value => value != 0);
                        if (!reused) for (int n = 0; n < data.Length; n++)
                            data[n] *= ((side == "_left" && n % 2 == 1) || (side == "_right" && n % 2 == 0) ? .65f : 1);
                        string sampleId = effect.SampleId + "#" + i;
                        var switches = (file["switches"]?.AsArray() ?? []).Select(item => new SoundSwitch(
                            item!["group"]!.GetValue<uint>(), item["default"]!.GetValue<uint>(),
                            item["values"]!.AsArray().Select(value => value!.GetValue<uint>()).ToArray())).ToArray();
                        samples[sampleId] = data; if (!reused) renderCache[renderKey] = sampleId; variants.Add(sampleId); choices.Add(new(sampleId, switches));
                        SoundInfo[sampleId] = new(
                            file["source_media"]!.GetValue<uint>(), file["path_id"]!.GetValue<uint>(),
                            file["draw"]!.GetValue<int>(), file["draw_count"]!.GetValue<int>(),
                            file["pitch_cents"]!.GetValue<double>(), file["delay_ms"]!.GetValue<double>(),
                            file["volume_db"]!.GetValue<double>(), file["randomized"]!.GetValue<bool>(),
                            file["dynamic_rtpc"]!.GetValue<bool>(), file["dynamic_state"]!.GetValue<bool>());
                        if (source.Family is "attack" or "contact" or "special_motion")
                        {
                            string cutId = "ext:cut_" + effect.Id + "#" + i;
                            string cutKey = renderKey + "|cut";
                            float[] cutData;
                            if (!renderCache.TryGetValue(cutKey, out var cachedCut))
                            {
                                cutData = SoundHaptics.Render(SoundHaptics.LoadShared(file, sourceCache), "cut", SoundHaptics.Playback(file));
                                renderCache[cutKey] = cutId;
                            }
                            else cutData = samples[cachedCut];
                            samples[cutId] = cutData; CutSamples[sampleId] = cutId; SoundInfo[cutId] = SoundInfo[sampleId];
                        }
                    }
                    if (source.Family == "guard")
                    {
                        for (int i = 0; i < files.Count; i++)
                        {
                            var file = files[i]!;
                            string baseId = effect.SampleId + "#" + i;
                            string parryId = "ext:parry_" + effect.Id + "#" + i;
                            var data = SoundHaptics.Render(SoundHaptics.LoadShared(file, sourceCache), "parry", SoundHaptics.Playback(file));
                            samples[parryId] = data; ParrySamples[baseId] = parryId; SoundInfo[parryId] = SoundInfo[baseId];
                        }
                    }
                    if (variants.Count > 0 && eventAudible)
                    {
                        Available[id] = effect; Variants[id] = variants.ToArray(); SoundChoices[id] = choices.ToArray();
                    }
                }
                sourceCache.Clear();
                if (files.Count > 0 && eventAudible) SoundEvents[source.Event] = new { id = source.Id, family = source.Family, source = source.Source };
            }
            DefenseSounds.Load(this, samples);
        }
        var playable = PlayableSamples();
        foreach (string key in samples.Keys.Where(key => key.StartsWith("ext:") && !playable.Contains(key)).ToArray())
        {
            samples.Remove(key); SoundInfo.Remove(key);
        }
    }

    internal static bool Matches(SoundSwitch[] required, JsonNode? actual)
    {
        var values = actual as JsonObject;
        foreach (var item in required)
        {
            uint selected = item.Default;
            if (values != null && values.TryGetPropertyValue(item.Group.ToString(), out var node) && node != null)
                selected = node.GetValue<uint>();
            if (!item.Values.Contains(selected)) return false;
        }
        return true;
    }

    public static float[] Synthesize(ExtraEffect effect, float gain)
    {
        int frames = (int)Math.Round(effect.Duration * 48000);
        float[] output = new float[frames * 2];
        double pulseLength = effect.Duration / effect.Pulses;
        for (int i = 0; i < frames; i++)
        {
            double t = i / 48000.0, local = t % pulseLength;
            double p = local / pulseLength;
            double edge = Math.Sin(Math.PI * Math.Min(1, local / .0015) / 2);
            double release = Math.Min(1, (pulseLength - local) / .004);
            double phase = 2 * Math.PI * (effect.Frequency * local + .5 * (effect.EndFrequency - effect.Frequency) / pulseLength * local * local);
            double carrier, envelope;
            if (effect.Id is "attack" or "dodge")
            {
                // Broad, light sweep without an impact at its onset.
                envelope = Math.Pow(Math.Sin(Math.PI * p), 2);
                carrier = .55 * Math.Sin(phase) + .25 * Math.Sin(phase * 1.71) + .20 * Math.Sin(phase * 2.37);
            }
            else if (effect.Id is "guard" or "perfect_dodge")
            {
                envelope = edge * edge * release * Math.Exp(-5 * p);
                carrier = .55 * Math.Sin(2 * Math.PI * 195 * local) + .30 * Math.Sin(2 * Math.PI * 330 * local) + .15 * Math.Sin(2 * Math.PI * 470 * local);
            }
            else if (effect.Id is "heal" or "soul" or "power")
            {
                envelope = Math.Pow(Math.Sin(Math.PI * p), 1.5);
                carrier = Math.Sin(phase) * (.8 + .2 * Math.Sin(2 * Math.PI * 24 * local));
            }
            else if (effect.Id is "hit" or "damage" or "land" or "finisher")
            {
                envelope = edge * edge * release * Math.Exp(-4.5 * p);
                carrier = .78 * Math.Sin(phase) + .22 * Math.Sin(2 * Math.PI * 280 * local) * Math.Exp(-local / .008);
            }
            else
            {
                envelope = edge * edge * release * Math.Exp(-5.5 * p);
                carrier = Math.Sin(phase);
            }
            float value = (float)(carrier * envelope) * effect.Amplitude * gain;
            output[2 * i] = value * (effect.Balance > 0 ? 1 - effect.Balance : 1);
            output[2 * i + 1] = value * (effect.Balance < 0 ? 1 + effect.Balance : 1);
        }
        output[0] = output[1] = output[^1] = output[^2] = 0;
        return output;
    }
}

sealed class FeedbackQueue(Mixer mixer, ExtendedEffects effects, Action<string> log)
{
    record Request(string Id, long Sequence, long Frame, double Time, ExtraEffect Extra, bool Layer = false, string Technique = "");
    readonly List<Request> pending = [];
    readonly Dictionary<string, double> cooldowns = [];
    readonly Dictionary<string, int> nextVariant = [];
    int activePriority;
    string activeKind = "";
    double defenseContactTime = double.NegativeInfinity;
    long defenseContactFrame = -1000;
    bool defenseLayerUsed;
    double cutTime = double.NegativeInfinity;
    int cutLayers;
    double combatUntil;
    double contactUntil;
    double suppressUntil;
    string? parryVoice;
    Request? recentGuard;
    public string DefenseKind { get; private set; } = "none";
    public int ParryPlays { get; private set; }
    public void StopParry(string id)
    {
        pending.RemoveAll(p => p.Extra.Kind == "parry" && p.Extra.Id == id);
        if (parryVoice?.StartsWith("ext:" + id + "#", StringComparison.Ordinal) == true)
        {
            mixer.FadeOut(parryVoice); parryVoice = null; activePriority = 0;
            log($"Defense sound STOP id={id}");
        }
    }
    public void SetDefense(string kind, double now)
    {
        if (kind == DefenseKind) return;
        string previous = DefenseKind; DefenseKind = kind;
        if (previous == "deflect" && kind != "deflect") activePriority = Math.Min(activePriority, 9);
        log($"Defense state={kind} previous={previous}");
        if (kind != "parry")
        {
            pending.RemoveAll(p => p.Extra.Kind == "parry");
            if (parryVoice != null) mixer.FadeOut(parryVoice);
            if (parryVoice != null) activePriority = 0;
            parryVoice = null;
            // Explicit alternative defenses must never be promoted on a later parry.
            if (kind is "guard" or "deflect" or "dodge" || previous == "parry") recentGuard = null;
            return;
        }
        if (recentGuard is { } p && now - p.Time <= .25 && effects.ParrySamples.TryGetValue(p.Id, out var replacement)
            && mixer.Replace(p.Id, replacement))
        {
            parryVoice = replacement; ParryPlays++;
            log($"Extended haptic PLAY id=parry_{p.Extra.Id} event={p.Sequence} frame={p.Frame} sample={replacement} promoted=true");
        }
        recentGuard = null;
    }
    public Dictionary<string, int> Skipped { get; } = [];
    public bool CombatWindow(double now) => now < suppressUntil;
    public bool RequiresSuppression(bool gameplay, bool ui, double now) =>
        ((gameplay || ui) && HasWork) || (gameplay && CombatWindow(now));
    void Skip(string id, string reason, long sequence, long frame)
    {
        string key = id + ":" + reason; Skipped[key] = Skipped.GetValueOrDefault(key) + 1;
        log($"Extended haptic SKIP id={id} reason={reason} event={sequence} frame={frame}");
    }
    public Dictionary<string, int> ExtraPlays { get; } = [];
    public bool HasWork => pending.Count > 0 || mixer.Playing;
    public void Clear() { pending.Clear(); mixer.Stop(); activePriority = 0; suppressUntil = 0; parryVoice = null; recentGuard = null; DefenseKind = "none"; defenseContactTime = double.NegativeInfinity; cutTime = double.NegativeInfinity; cutLayers = 0; }
    public void SetActivity(bool gameplay, bool ui)
    {
        if (!gameplay && !ui) { Clear(); return; }
        if (!gameplay)
        {
            pending.RemoveAll(p => p.Extra.IsUi != true); mixer.StopGameplay(); suppressUntil = 0;
            parryVoice = null; recentGuard = null; DefenseKind = "none"; defenseContactTime = double.NegativeInfinity;
        }
        if (!ui) pending.RemoveAll(p => p.Extra.IsUi == true);
    }
    public void Extended(string id, long sequence, long frame, double now, JsonNode? switches = null, string technique = "")
    {
        if (!effects.Available.TryGetValue(id, out var effect)) { Skip(id, "unavailable", sequence, frame); return; }
        bool cut = technique is "issen" or "deadheat" && effect.Kind is "attack" or "contact" or "special_motion";
        if (effect.Kind == "special_motion" && !cut) { Skip(id, "not_special_attack", sequence, frame); return; }
        if (cut) effect = effect with { Priority = 13, Family = "cut", Cooldown = .035 };
        if (effect.Kind == "parry" && DefenseKind != "parry") { Skip(id, "not_parry", sequence, frame); return; }
        if (effect.Kind == "deflect" && DefenseKind != "deflect") { Skip(id, "not_deflect", sequence, frame); return; }
        SoundChoice[]? compatible = null;
        if (effects.SoundChoices.TryGetValue(id, out var choices))
        {
            compatible = choices.Where(choice => ExtendedEffects.Matches(choice.Switches, switches)).ToArray();
            if (compatible.Length == 0) { Skip(id, "switch_mismatch", sequence, frame); return; }
        }
        if (effect.Kind is "cut" or "attack" or "hit" or "guard" or "parry" or "parry_release" or "deflect" or "damage" or "finisher" or "contact" or "perfect_dodge" or "power") suppressUntil = now + .8;
        if (effect.Category == "footsteps" && now < combatUntil) { Skip(id, "combat_masks_step", sequence, frame); return; }
        if (effect.Kind == "attack" && now < contactUntil) { Skip(id, "contact_masks_attack", sequence, frame); return; }
        if (effect.Kind is "hit" or "guard" or "damage" or "finisher" or "contact") contactUntil = now + .12;
        if (effect.Kind is "attack" or "hit" or "guard" or "damage" or "finisher" or "contact") combatUntil = now + .4;
        string cooldownKey = cut ? "cut:" + id : effect.Family.Length > 0 ? effect.Family : id;
        if (now - cooldowns.GetValueOrDefault(cooldownKey, double.NegativeInfinity) < effect.Cooldown) { Skip(id, "cooldown", sequence, frame); return; }
        bool layer = effect.Kind == "contact" && DefenseKind is "guard" or "parry" or "deflect"
            && !defenseLayerUsed && now - defenseContactTime <= .08 && Math.Abs(frame - defenseContactFrame) <= 3;
        if (cut)
        {
            if (now - cutTime > .035) cutLayers = 0;
            layer = cutLayers is > 0 and < 3;
            if (cutLayers >= 3) { Skip(id, "cut_layer_limit", sequence, frame); return; }
            cutTime = now; cutLayers++;
        }
        int blockingPriority = effect.Kind == "guard" && activeKind is "deflect" or "parry_release" ? 9 : activePriority;
        if (!layer && ((mixer.Playing && blockingPriority > effect.Priority) || pending.Any(p => p.Extra.Priority > effect.Priority))) { Skip(id, "higher_priority_active_or_pending", sequence, frame); return; }
        cooldowns[cooldownKey] = now;
        if (layer) defenseLayerUsed = true;
        else
        {
            foreach (var displaced in pending) Skip(displaced.Extra.Id, "replaced_pending", displaced.Sequence, displaced.Frame);
            pending.Clear();
        }
        if (effect.Kind is "guard" or "parry" or "deflect")
        {
            defenseContactTime = now; defenseContactFrame = frame; defenseLayerUsed = false;
        }
        string sample = effect.SampleId;
        if (compatible != null)
        {
            int index = nextVariant.GetValueOrDefault(id);
            sample = compatible[index % compatible.Length].SampleId; nextVariant[id] = index + 1;
        }
        else if (effects.Variants.TryGetValue(id, out var variants))
        {
            int index = nextVariant.GetValueOrDefault(id);
            sample = variants[index % variants.Length]; nextVariant[id] = index + 1;
        }
        if (cut && effects.CutSamples.TryGetValue(sample, out var cutSample)) sample = cutSample;
        pending.Add(new(sample, sequence, frame, now, effect, layer, cut ? technique : ""));
    }
    public void Dispatch(bool acknowledged, double now)
    {
        foreach (var expired in pending.Where(p => now - p.Time >= .20)) Skip(expired.Extra.Id, "expired_waiting_ack", expired.Sequence, expired.Frame);
        pending.RemoveAll(p => now - p.Time >= .20);
        if (!acknowledged) return;
        foreach (var p in pending)
        {
            if (!p.Layer) { mixer.Stop(); activePriority = p.Extra.Priority; activeKind = p.Extra.Kind; }
            string selected = p.Id;
            if (!p.Layer)
            {
                parryVoice = null; recentGuard = null;
                if (p.Extra.Kind == "parry") parryVoice = selected;
                if (effects.ParrySamples.TryGetValue(p.Id, out var parry))
                {
                    if (DefenseKind == "parry") { selected = parry; parryVoice = parry; }
                    else if (DefenseKind is "none" or "unknown") recentGuard = p;
                }
            }
            if (!mixer.Play(selected, level: p.Layer ? .6f : 1)) continue;
            if (parryVoice == selected) ParryPlays++;
            { ExtraPlays[p.Extra.Id] = ExtraPlays.GetValueOrDefault(p.Extra.Id) + 1; log($"Extended haptic PLAY id={(p.Technique.Length > 0 ? p.Technique + "_" : parryVoice == selected && !p.Extra.Id.StartsWith("defense_") ? "parry_" : "")}{p.Extra.Id} event={p.Sequence} frame={p.Frame} sample={selected} layer={p.Layer}"); }
        }
        pending.Clear();
    }
}
