using System.Text.Json.Nodes;

namespace OnimushaDualSense;

sealed record Configuration(string Game, float Gain)
{
    public static Configuration Read()
    {
        var config = File.Exists(Files.At("config.json")) ? Files.Read(Files.At("config.json")) : new JsonObject();
        float gain = config["gain"]?.GetValue<float>() ?? 1;
        if (!float.IsFinite(gain) || gain < 0 || gain > 1) throw new InvalidDataException("gain must be between 0 and 1");
        return new(config["game"]?.GetValue<string>() ?? "", gain);
    }
    public void Save() => Files.Save(Files.At("config.json"), new { game = Game, gain = Gain });
}
