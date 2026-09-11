using System.Text.Json.Nodes;
using System.Globalization;

namespace OnimushaDualSense;

sealed record Configuration(string Game, float Gain, bool AdaptiveTriggers, bool AutoLaunchGame, float AdaptiveTriggerStrength = 1)
{
    public static Configuration Read()
    {
        var config = File.Exists(Files.At("config.json")) ? Files.Read(Files.At("config.json")) : new JsonObject();
        float gain = config["gain"]?.GetValue<float>() ?? 1;
        if (!float.IsFinite(gain) || gain < 0 || gain > 1) throw new InvalidDataException("gain must be between 0 and 1");
        bool adaptiveTriggers = config["adaptive_triggers"]?.GetValue<bool>() ?? true;
        float adaptiveTriggerStrength = config["adaptive_trigger_strength"]?.GetValue<float>() ?? 1;
        if (!float.IsFinite(adaptiveTriggerStrength) || adaptiveTriggerStrength < 0 || adaptiveTriggerStrength > 1) throw new InvalidDataException("adaptive_trigger_strength must be between 0 and 1");
        bool autoLaunchGame = config["auto_launch_game"]?.GetValue<bool>() ?? true;
        return new(config["game"]?.GetValue<string>() ?? "", gain, adaptiveTriggers, autoLaunchGame, adaptiveTriggerStrength);
    }
    public void Save()
    {
        var config = new JsonObject
        {
            ["game"] = Game,
            ["gain"] = Number(Gain),
            ["adaptive_triggers"] = AdaptiveTriggers,
            ["adaptive_trigger_strength"] = Number(AdaptiveTriggerStrength),
            ["auto_launch_game"] = AutoLaunchGame
        };
        Files.Save(Files.At("config.json"), config);
    }

    static JsonNode Number(float value) => JsonNode.Parse(value.ToString("0.0#########", CultureInfo.InvariantCulture))!;
}
