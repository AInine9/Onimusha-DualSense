using System.Text.Json.Nodes;

namespace OnimushaDualSense;

sealed record Configuration(string Game, float Gain, bool AdaptiveTriggers, bool AutoLaunchGame)
{
    public static Configuration Read()
    {
        var config = File.Exists(Files.At("config.json")) ? Files.Read(Files.At("config.json")) : new JsonObject();
        float gain = config["gain"]?.GetValue<float>() ?? 1;
        if (!float.IsFinite(gain) || gain < 0 || gain > 1) throw new InvalidDataException("gain must be between 0 and 1");
        bool adaptiveTriggers = config["adaptive_triggers"]?.GetValue<bool>() ?? true;
        bool autoLaunchGame = config["auto_launch_game"]?.GetValue<bool>() ?? true;
        return new(config["game"]?.GetValue<string>() ?? "", gain, adaptiveTriggers, autoLaunchGame);
    }
    public void Save() => Files.Save(Files.At("config.json"), new { game = Game, gain = Gain, adaptive_triggers = AdaptiveTriggers, auto_launch_game = AutoLaunchGame });
}
