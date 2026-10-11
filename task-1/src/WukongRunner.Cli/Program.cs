using WukongRunner.Core;
using WukongRunner.Cli;
using WukongRunner.Cli.Windows;

return await MainAsync(args);

static async Task<int> MainAsync(string[] arguments)
{
    if (arguments.Length == 0 || arguments[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            WukongRunner — two-pass Black Myth: Wukong Benchmark Tool automation

            WukongRunner run [--output DIR] [--config-dir DIR]
                [--install-dir DIR] [--steam-dir DIR] [--settings-file FILE] [--history-dir DIR]
            WukongRunner demo [--output DIR] [--config-dir DIR] [--fail-pass cpu|gpu]
            WukongRunner recover --manifest FILE

            run: Windows + Steam + initialized benchmark; desktop session must remain unlocked.
            demo: synthetic data on any supported OS; does not measure your computer.
            recover: restore original-settings.ini using its checksum-verified recovery manifest.
                     Close the benchmark first. This overwrites the recorded settings file.

            Exit codes: 0 completed; 1 failure; 2 usage/configuration/preflight error;
                        3 collected results, but applied settings could not be verified; 130 cancelled.
            """);
        return 0;
    }
    var command = arguments[0];
    try
    {
        var allowed = command switch
        {
            "run" => new[] { "--output", "--config-dir", "--install-dir", "--steam-dir", "--settings-file", "--history-dir" },
            "demo" => ["--output", "--config-dir", "--fail-pass"],
            "recover" => ["--manifest"],
            _ => throw new ArgumentException("Unknown command. Use --help.")
        };
        var values = new Dictionary<string, string>();
        for (var i = 1; i < arguments.Length; i += 2)
        {
            if (!allowed.Contains(arguments[i]) || i + 1 >= arguments.Length || arguments[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(arguments[i], arguments[i + 1]))
                throw new ArgumentException("Unknown, duplicate, or incomplete option: " + arguments[i]);
        }
        string? Get(string name) => values.GetValueOrDefault(name);
        if (command == "recover")
        {
            var manifest = Get("--manifest") ?? throw new ArgumentException("recover requires --manifest.");
            using var recoveryLock = OperatingSystem.IsWindows()
                ? new FileStream(Path.Combine(Path.GetTempPath(), "WukongRunner-3132990.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : null;
            if (OperatingSystem.IsWindows() && (System.Diagnostics.Process.GetProcessesByName("b1-Win64-Shipping").Length > 0 ||
                                               System.Diagnostics.Process.GetProcessesByName("b1_benchmark").Length > 0))
                throw new IOException("Close the benchmark before restoring its settings.");
            SettingsBackup.Recover(manifest);
            Console.WriteLine("Original settings restored and backup checksum verified.");
            return 0;
        }
        if (command == "run" && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Real benchmark execution requires Windows. Use demo for synthetic workflow checks on macOS/Linux.");
        if (Get("--fail-pass") is { } failure && failure is not ("cpu" or "gpu"))
            throw new ArgumentException("--fail-pass must be cpu or gpu.");
        var config = Path.GetFullPath(Get("--config-dir") ?? Path.Combine(AppContext.BaseDirectory, "config"));
        var options = JsonFiles.Read<AutomationOptions>(Path.Combine(config, "automation.json"));
        options.Validate();
        var profiles = new[] { JsonFiles.Read<Profile>(Path.Combine(config, "cpu.json")), JsonFiles.Read<Profile>(Path.Combine(config, "gpu.json")) };
        foreach (var profile in profiles) profile.Validate();
        var output = Path.GetFullPath(Get("--output") ?? Path.Combine("results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Output directory already contains files. Choose a new directory.");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            // File-share locking survives async thread changes and releases automatically after a crash.
            using var runLock = command == "run"
                ? new FileStream(Path.Combine(Path.GetTempPath(), "WukongRunner-3132990.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                : null;
            {
                IBenchmarkHost host;
                if (command == "demo")
                {
                    options = options with { PollIntervalMs = 50, ResultStableMs = 200, RunTimeoutSeconds = 10 };
                    host = new DemoHost(Get("--fail-pass"));
                    Console.WriteLine("SYNTHETIC DEMO: all hardware and FPS values are invented.");
                }
                else if (OperatingSystem.IsWindows())
                {
                    var installation = Installation.Find(Get("--install-dir"), Get("--settings-file"), Get("--steam-dir"));
                    host = new WindowsHost(installation, options, await Hardware.ReadAsync(cancellation.Token), Get("--history-dir"));
                    Console.WriteLine("Windows integration is unvalidated. Menu steps and history paths may need adjustment for the installed benchmark version.");
                }
                else throw new PlatformNotSupportedException();
                await using (host)
                {
                    Directory.CreateDirectory(output);
                    // The first log callback occurs after Runner has validated the empty output directory.
                    var messages = new List<string>();
                    void Log(string message)
                    {
                        var line = $"{DateTimeOffset.UtcNow:O} {message}";
                        messages.Add(line); Console.WriteLine(line);
                        File.AppendAllText(Path.Combine(output, "run.log"), line + Environment.NewLine);
                    }
                    RunReport report;
                    try { report = await new Runner(options, Log).RunAsync(host, profiles, output, cancellation.Token); }
                    finally { File.WriteAllLines(Path.Combine(output, "run.log"), messages); }
                    Console.WriteLine("Report: " + Path.Combine(output, "report.html"));
                    return report.Status switch { "completed" => 0, "completed-settings-unverified" => 3, "cancelled" => 130, _ => 1 };
                }
            }
        }
        finally { Console.CancelKeyPress -= handler; }
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
    catch (Exception e) when (e is not OutOfMemoryException)
    { Console.Error.WriteLine(e.Message); return 2; }
}
