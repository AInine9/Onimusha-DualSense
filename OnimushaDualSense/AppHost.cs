using System.Diagnostics;

namespace OnimushaDualSense;

static class AppHost
{
    public static void Initialize()
    {
        // The distributable has one bin directory and a user-editable root config.
        Files.Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Directory.CreateDirectory(Files.Data(""));
    }
    internal static ProcessStartInfo StartInfo(params string[] args)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the .NET host");
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = Files.Root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(typeof(AppHost).Assembly.Location);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    public static Process Launch(params string[] args) => Process.Start(StartInfo(args)) ?? throw new IOException("Cannot start the MOD");
}
