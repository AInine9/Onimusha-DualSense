using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OnimushaDualSense;

static class Setup
{
    const string Script = "onimusha_dualsense_bridge.lua";
    internal static readonly Dictionary<string, string> Resources = new()
    {
        ["natives/stm/gamedesign/system/adaptivetrigger/adaptivetriggersettingdata.user.3"] = "5a3299e925662dc5bb9c41905c82b90d50d0a340cef8eb8e5bc2129c3b52528b"
    };
    static Setup() { foreach (var item in SoundCatalog.Banks) Resources.Add(item.Key, item.Value); }
    static readonly Dictionary<string, (string Url, string Hash)> Tools = new()
    {
        ["pak"] = ("https://github.com/eigeen/ree-pak-rs/releases/download/v0.7.2/ree-pak-cli.exe", "bc4e561194a74faa3dbac06bd4ca0039eff85d047a8075c67895047212723101"),
        ["decoder"] = ("https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip", "6c4a8a3813864fefed081bbd337dbc0ad93bf88e0b92f5db98d7ab258b22dc6c")
    };
    public static void Exec(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start " + exe);
        process.WaitForExit(); if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(exe)} failed ({process.ExitCode})");
    }
    public static string? Discover()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, key, name) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") })
        {
            using var handle = hive.OpenSubKey(key);
            if (handle?.GetValue(name) is string path) libraries.Add(path);
        }
        foreach (string root in libraries.ToArray())
        {
            string file = Path.Combine(root, "steamapps/libraryfolders.vdf");
            if (File.Exists(file)) foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraries)
        {
            string file = Path.Combine(library, "steamapps/appmanifest_2638890.acf");
            if (!File.Exists(file)) continue;
            var match = Regex.Match(File.ReadAllText(file), "\"installdir\"\\s+\"([^\"]+)\"");
            if (!match.Success) continue;
            string game = Path.GetFullPath(Path.Combine(library, "steamapps/common", match.Groups[1].Value));
            if (File.Exists(Path.Combine(game, "OnimushaWotS.exe"))) found.Add(game);
        }
        return found.Count == 1 ? found.First() : null;
    }
    static string Download(string kind)
    {
        var tool = Tools[kind]; Directory.CreateDirectory(Files.Data("tools"));
        string path = Files.Data("tools/" + Path.GetFileName(new Uri(tool.Url).AbsolutePath));
        if (!File.Exists(path) || Files.Sha(path) != tool.Hash)
        {
            Console.WriteLine("Downloading official tool: " + tool.Url);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Onimusha-DualSense-Setup/1.1");
            string temp = path + ".download";
            using (var response = client.GetAsync(tool.Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var input = response.Content.ReadAsStream(); using var output = File.Create(temp); input.CopyTo(output);
            }
            if (Files.Sha(temp) != tool.Hash) { File.Delete(temp); throw new InvalidDataException("Download checksum mismatch; nothing was executed"); }
            File.Move(temp, path, true);
        }
        if (kind == "pak") return path;
        string folder = Files.Data("tools/vgmstream-r2117"); Directory.CreateDirectory(folder);
        ZipFile.ExtractToDirectory(path, folder, true); // BCL rejects directory traversal entries.
        return Path.Combine(folder, "vgmstream-cli.exe");
    }
    internal static Dictionary<string, byte[]> Chunks(byte[] data)
    {
        var result = new Dictionary<string, byte[]>(); int pos = 0;
        while (pos < data.Length)
        {
            if (pos + 8 > data.Length) throw new InvalidDataException("Truncated bank");
            string tag = System.Text.Encoding.ASCII.GetString(data, pos, 4); int length = checked((int)BitConverter.ToUInt32(data, pos + 4));
            pos += 8; if (length > data.Length - pos) throw new InvalidDataException("Truncated chunk");
            result[tag] = data[pos..(pos + length)]; pos += length;
        }
        return result;
    }
    internal static void PrepareTriggers(string extracted)
    {
        const string resource = "natives/stm/gamedesign/system/adaptivetrigger/adaptivetriggersettingdata.user.3";
        string path = Path.Combine(extracted, resource);
        if (Files.Sha(path) != Resources[resource]) throw new InvalidDataException("Unsupported adaptive trigger data");
        byte[] data = File.ReadAllBytes(path); var profiles = new List<object>();
        foreach (int offset in new[] { 0x90, 0xdc })
        {
            int kind = BitConverter.ToInt32(data, offset), which = BitConverter.ToInt32(data, offset + 4);
            if (BitConverter.ToUInt32(data, offset + 16) != 10 || which is < 0 or > 2) throw new InvalidDataException("Unsupported trigger profile");
            float[] powers = Enumerable.Range(0, 10).Select(i => BitConverter.ToSingle(data, offset + 20 + 4 * i)).ToArray(); Protocol.Feedback(powers);
            profiles.Add(new { _Type = kind, _Which = which, _Frequency = BitConverter.ToSingle(data, offset + 8), _IsMultiPosition = BitConverter.ToUInt32(data, offset + 12) != 0, _PowerList = powers, _Power = BitConverter.ToSingle(data, offset + 60), _StartPressPosition = BitConverter.ToSingle(data, offset + 64), _EndPressPosition = BitConverter.ToSingle(data, offset + 68), _EndPower = BitConverter.ToSingle(data, offset + 72) });
        }
        Files.Save(Files.Data("trigger_profiles.json"), new { source = "local game assets", profiles });
    }
    public static void Install(string game)
    {
        Directory.CreateDirectory(Path.Combine(game, "reframework/data"));
        string destination = Path.Combine(game, "reframework/autorun", Script); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string recordPath = Files.Data("install-record.json"), backup = Files.Data("backup.lua");
        var previous = File.Exists(recordPath) ? Files.Read(recordPath) : null;
        if (previous != null && !Path.GetFullPath(previous["game"]!.GetValue<string>()).Equals(game, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This folder belongs to another game installation; uninstall first");
        if (File.Exists(destination))
        {
            if (previous != null) { if (Files.Sha(destination) != previous["installed_sha256"]!.GetValue<string>()) throw new InvalidDataException("Installed Lua was modified; it was not replaced"); }
            else
            {
                if (!File.ReadAllText(destination).StartsWith("-- Onimusha DualSense bridge")) throw new InvalidDataException("A different file occupies the MOD filename");
                File.Copy(destination, backup, true);
            }
        }
        string source = Files.Bundled(Script);
        var record = new { game, installed_sha256 = Files.Sha(source), backup_sha256 = File.Exists(backup) ? Files.Sha(backup) : null };
        string temp = destination + ".installing"; File.Copy(source, temp, true); File.Move(temp, destination, true);
        Files.Save(recordPath, record);
    }
    public static void Uninstall()
    {
        if (Files.GameRunning() != false) throw new InvalidOperationException("Close Onimusha before uninstalling");
        string recordPath = Files.Data("install-record.json"); var record = Files.Read(recordPath);
        File.WriteAllText(Files.Data("stop.request"), "stop");
        // Same named mutex as both the C# and Python editions.
        for (int i = 0; ; i++)
        {
            using var mutex = new Mutex(false, @"Local\OnimushaDualSenseBridge", out bool created);
            if (created) break;
            if (i >= 30) throw new IOException("Companion is still running; installation was left unchanged");
            Thread.Sleep(100);
        }
        string target = Path.Combine(record["game"]!.GetValue<string>(), "reframework/autorun", Script);
        if (File.Exists(target))
        {
            if (Files.Sha(target) != record["installed_sha256"]!.GetValue<string>()) throw new InvalidDataException("Lua was modified; it was not removed");
            if (record["backup_sha256"] is JsonNode hash)
            {
                string backup = Files.Data("backup.lua"); if (!File.Exists(backup) || Files.Sha(backup) != hash.GetValue<string>()) throw new InvalidDataException("Backup mismatch"); File.Copy(backup, target, true);
            }
            else File.Delete(target);
        }
        File.Delete(recordPath); Console.WriteLine("MOD Lua removed or previous version restored.");
    }
    public static void Run(string[] args)
    {
        string? Option(string key) { int i = Array.IndexOf(args, key); return i < 0 ? null : args[i + 1]; }
        bool prepare = args.Contains("--prepare-only");
        if (!prepare && Files.GameRunning() != false) throw new InvalidOperationException("Close Onimusha before setup");
        var config = Configuration.Read();
        string? game = Option("--game") ?? (string.IsNullOrWhiteSpace(config.Game) ? Discover() : config.Game);
        if (game == null) { Console.Write("Onimusha game folder (containing OnimushaWotS.exe): "); game = Console.ReadLine()?.Trim().Trim('"'); }
        game = Path.GetFullPath(game ?? throw new InvalidOperationException("Game folder is required"));
        if (!File.Exists(Path.Combine(game, "OnimushaWotS.exe"))) throw new InvalidDataException("OnimushaWotS.exe not found");
        if (!prepare && !File.Exists(Path.Combine(game, "dinput8.dll"))) throw new InvalidOperationException("Install a compatible REFramework build first; see README");
        string pak = Path.GetFullPath(Option("--pak-tool") ?? Download("pak")), decoder = Path.GetFullPath(Option("--decoder") ?? Download("decoder"));
        string scratch = Files.Data("extract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            string paths = Path.Combine(scratch, "paths.list"), extracted = Path.Combine(scratch, "extracted");
            File.WriteAllText(paths, string.Join('\n', Resources.Keys) + "\n");
            string[] packs = [Path.Combine(game, "re_chunk_000.pak"), .. Directory.GetFiles(game, "re_chunk_000.pak.patch_*.pak").Order(StringComparer.Ordinal)];
            foreach (string pack in packs) Exec(pak, "unpack", "-p", paths, "-i", pack, "-o", extracted, "--skip-unknown", "--override");
            PrepareTriggers(extracted);
            SoundHaptics.Prepare(extracted, decoder);
            DefenseSounds.Prepare(extracted, decoder);
        }
        finally { Directory.Delete(scratch, true); }
        (config with { Game = game }).Save();
        PreparedWaves.Prepare();
        if (!prepare) Install(game);
        Console.WriteLine(prepare ? "Assets prepared. No game files changed." : "Setup complete. Use Start-Mod.cmd.");
    }
}
