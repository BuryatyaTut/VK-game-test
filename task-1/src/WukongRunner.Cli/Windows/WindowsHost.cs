using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using WukongRunner.Core;

namespace WukongRunner.Cli.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsHost : IBenchmarkHost
{
    private readonly Installation installation;
    private readonly AutomationOptions options;
    private readonly List<Process> owned = [];
    private Process? benchmark;
    private DateTime? launchUtc;
    private DateTime? exitedUtc;
    public string SettingsPath => installation.SettingsFile;
    public IReadOnlyList<string> ResultDirectories { get; }
    public MachineInfo Machine { get; }
    public int DesktopWidth { get; }
    public int DesktopHeight { get; }
    public bool Synthetic => false;

    public WindowsHost(Installation installation, AutomationOptions options, MachineInfo machine, string? historyOverride)
    {
        this.installation = installation; this.options = options; Machine = machine;
        ResultDirectories = [Path.GetFullPath(historyOverride ?? Path.Combine(Path.GetTempPath(), "b1", "BenchMarkHistory", "Tool"))];
        WindowInput.EnableDpiAwareness();
        (DesktopWidth, DesktopHeight) = WindowInput.Desktop;
        var existing = MatchingProcesses();
        if (existing.Count > 0)
        {
            foreach (var process in existing) process.Dispose();
            throw new IOException("The benchmark is already running. Close it before starting this tool.");
        }
    }

    public async Task StartAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (owned.Count > 0) throw new InvalidOperationException("Previous benchmark processes have not been cleaned up.");
        launchUtc = DateTime.UtcNow; exitedUtc = null;
        var start = new ProcessStartInfo(installation.SteamExe) { UseShellExecute = false };
        start.ArgumentList.Add("-applaunch"); start.ArgumentList.Add(Installation.AppId);
        // A returned Steam process is not the benchmark and must never be terminated by this runner.
        using var steamInvocation = Process.Start(start) ?? throw new IOException("Steam launch failed.");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(options.LaunchTimeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TrackNewProcesses();
            var windows = owned.Where(p => !p.HasExited).Where(p => { p.Refresh(); return WindowInput.IsUsable(p.MainWindowHandle); }).ToArray();
            // A launcher can briefly coexist with the actual rendering process; prefer Shipping when present.
            var renderers = windows.Where(p => p.ProcessName.Equals("b1-Win64-Shipping", StringComparison.OrdinalIgnoreCase)).ToArray();
            var candidates = renderers.Length > 0 ? renderers : windows;
            if (candidates.Length > 1) throw new IOException("Multiple benchmark windows are ambiguous; refusing to send input.");
            if (candidates.Length == 1) { benchmark = candidates[0]; return; }
            await Task.Delay(options.PollIntervalMs, cancellationToken);
        }
        throw new TimeoutException("Steam did not produce a usable benchmark window within the launch timeout.");
    }

    public async Task TriggerAsync(CancellationToken cancellationToken)
    {
        var process = benchmark ?? throw new InvalidOperationException("No benchmark window.");
        await Task.Delay(TimeSpan.FromSeconds(options.MenuReadyDelaySeconds), cancellationToken);
        foreach (var step in options.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("Benchmark exited before the menu sequence finished.");
            process.Refresh();
            await WindowInput.SendAsync(process.MainWindowHandle, process.Id, step, cancellationToken);
        }
    }

    public void CheckHealth()
    {
        if (benchmark == null) throw new IOException("No benchmark process is tracked.");
        if (!benchmark.HasExited) return;
        exitedUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - exitedUtc > TimeSpan.FromMilliseconds(options.ResultStableMs * 2 + options.PollIntervalMs))
            throw new IOException($"Benchmark exited with code {benchmark.ExitCode} without an accepted result.");
    }

    private List<Process> MatchingProcesses()
    {
        var result = new List<Process>();
        foreach (var name in installation.Executables.Select(Path.GetFileNameWithoutExtension).Distinct())
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file != null && installation.Executables.Contains(Path.GetFullPath(file), StringComparer.OrdinalIgnoreCase)) result.Add(process);
                    else process.Dispose();
                }
                catch (Win32Exception)
                {
                    process.Dispose();
                    foreach (var found in result) found.Dispose();
                    throw new IOException("Cannot identify an existing benchmark process. Use the same non-administrator account for Steam and the runner.");
                }
                catch (InvalidOperationException) { process.Dispose(); }
            }
        }
        return result;
    }

    private void TrackNewProcesses()
    {
        foreach (var process in MatchingProcesses())
        {
            try
            {
                if (owned.Any(p => p.Id == process.Id) || process.StartTime.ToUniversalTime() < launchUtc)
                    process.Dispose();
                else owned.Add(process);
            }
            catch (InvalidOperationException) { process.Dispose(); }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (launchUtc != null) TrackNewProcesses(); // Catch a renderer that appeared just before launch timeout/cancellation.
        foreach (var process in owned.Where(p => !p.HasExited)) process.CloseMainWindow();
        var graceful = Stopwatch.StartNew();
        while (owned.Any(p => !p.HasExited) && graceful.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(100, cancellationToken);
        foreach (var process in owned)
        {
            if (!process.HasExited)
            {
                var path = process.MainModule?.FileName;
                if (path == null || !installation.Executables.Contains(path, StringComparer.OrdinalIgnoreCase))
                    throw new IOException("Tracked process identity changed; refusing to terminate it.");
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        foreach (var process in owned) process.Dispose();
        owned.Clear(); benchmark = null; launchUtc = null;
    }

    public ValueTask DisposeAsync()
    {
        // Runner owns cleanup and records its result. Disposal must not hide or replace recovery errors.
        foreach (var process in owned) process.Dispose();
        owned.Clear();
        return ValueTask.CompletedTask;
    }
}
