using System.Diagnostics;

namespace OnimushaDualSense;

static class Program
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string command = args.FirstOrDefault() ?? "help";
        try
        {
            AppHost.Initialize();
            switch (command)
            {
                case "setup": Setup.Run(args); return 0;
                case "uninstall": Setup.Uninstall(); return 0;
                case "run": return Bridge.Run(args);
                case "stop": File.WriteAllText(Files.Data("stop.request"), "stop"); return 0;
                case "launch":
                    var config = Configuration.Read();
                    if (string.IsNullOrWhiteSpace(config.Game)) throw new InvalidOperationException("Run Setup.cmd first");
                    using (AppHost.Launch("run")) { }
                    if (ShouldAutoLaunchGame(config, args)) Process.Start(new ProcessStartInfo("steam://rungameid/2638890") { UseShellExecute = true });
                    return 0;
                case "prepare-waves": PreparedWaves.Prepare(args.Contains("--force")); return 0;
                case "diagnose":
                    Console.WriteLine($"Onimusha DualSense 1.1.1 / {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                    Console.WriteLine("Game running: " + Files.GameRunning());
                    Console.WriteLine("USB DualSense HID devices: " + Hid.Find().Count);
                    Console.WriteLine("Steam game: " + Setup.Discover()); return 0;
#if DEVELOPER
                case "inspect": Inspector.Launch(); return 0;
                case "inspect-server": return Inspector.Run(!args.Contains("--no-open"));
                case "test": return Tests.Run();
                case "audit-haptics": HapticAudit.Run(); return 0;
                case "verify-prepared": HapticAudit.VerifyPrepared(); return 0;
                case "prepare-sounds":
                    if (args.Length != 3) throw new ArgumentException("prepare-sounds <extracted-root> <decoder-exe>");
                    SoundHaptics.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0;
                case "prepare-defense":
                    if (args.Length != 3) throw new ArgumentException("prepare-defense <extracted-root> <decoder-exe>");
                    DefenseSounds.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])); return 0;
#endif
                default:
                    Console.WriteLine("Onimusha DualSense 1.1.1\nCommands: setup, launch, run, stop, uninstall, diagnose, prepare-waves");
                    return command == "help" ? 0 : 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            try { Files.Log(e.ToString()); } catch { }
            return 1;
        }
    }

    internal static bool ShouldAutoLaunchGame(Configuration config, string[] args) =>
        config.AutoLaunchGame && !args.Contains("--no-game");
}
