namespace WukongRunner.Core;

public sealed class Runner(AutomationOptions options, Action<string> log)
{
    public async Task<RunReport> RunAsync(IBenchmarkHost host, IReadOnlyList<Profile> profiles,
        string outputDirectory, CancellationToken cancellationToken)
    {
        options.Validate();
        if (profiles.Count != 2 || profiles[0].Name != "cpu" || profiles[1].Name != "gpu")
            throw new InvalidDataException("Exactly two profiles are required, ordered cpu then gpu.");
        var resolved = profiles.Select(p => p.Resolve(host.DesktopWidth, host.DesktopHeight)).ToArray();
        foreach (var profile in resolved) profile.Validate();
        var report = new RunReport
        {
            Synthetic = host.Synthetic, Machine = host.Machine, Automation = options,
            Passes = resolved.Select(p => new PassReport { Profile = p }).ToList()
        };
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException("Output directory must be empty. Each invocation needs its own directory.");
        SettingsBackup? backup = null;
        var stopped = true;
        try
        {
            // Validate both edits before touching the real file.
            var original = File.ReadAllBytes(host.SettingsPath);
            foreach (var profile in resolved) SettingsEditor.Apply(original, profile);
            backup = new SettingsBackup(host.SettingsPath, outputDirectory);
            foreach (var pass in report.Passes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pass.StartedUtc = DateTimeOffset.UtcNow;
                pass.Status = "running";
                log($"{pass.Profile.Name}: applying profile");
                try
                {
                    backup.Apply(pass.Profile);
                    AtomicFile.Write(Path.Combine(outputDirectory, pass.Profile.Name + "-requested.ini"), File.ReadAllBytes(host.SettingsPath));
                    var watcher = new ResultWatcher(host.ResultDirectories, options);
                    var baseline = watcher.CaptureBaseline();
                    stopped = false; // Start can fail after creating a process; cleanup still runs.
                    await host.StartAsync(pass.Profile, cancellationToken);
                    log($"{pass.Profile.Name}: benchmark launched; triggering configured menu sequence");
                    await host.TriggerAsync(cancellationToken);
                    var result = await watcher.WaitAsync(baseline, pass.Profile, host.CheckHealth, cancellationToken);
                    pass.RawResultFile = pass.Profile.Name + "-raw.json";
                    AtomicFile.Write(Path.Combine(outputDirectory, pass.RawResultFile), result.Bytes);
                    pass.RawSha256 = result.Sha256;
                    pass.SourcePath = result.SourcePath;
                    pass.Metrics = result.Result.Metrics;
                    pass.Settings = result.Result.Settings;
                    pass.AdditionalRawFields = result.Result.AdditionalRawFields;
                    pass.Status = pass.Settings.Any(s => s.Status == "mismatch") ? "settings-mismatch" :
                        pass.Settings.Any(s => s.Status == "unverified") ? "completed-settings-unverified" : "completed";
                    log($"{pass.Profile.Name}: average {pass.Metrics.AverageFps:F2} FPS; {pass.Status}");
                }
                catch (Exception e)
                {
                    pass.Status = e is OperationCanceledException ? "cancelled" : "failed";
                    pass.Error = e.Message;
                    throw;
                }
                finally
                {
                    pass.FinishedUtc = DateTimeOffset.UtcNow;
                    try
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(options.CleanupTimeoutSeconds));
                        await host.StopAsync(cleanup.Token);
                        stopped = true;
                    }
                    catch (Exception e) { pass.CleanupError = e.Message; log("Cleanup failed: " + e.Message); }
                }
                if (!stopped) throw new IOException("Process cleanup failed; the next pass cannot safely start.");
                if (pass.Status == "settings-mismatch") throw new InvalidDataException("Reported settings differ from the requested profile.");
            }
            report.Status = report.Passes.Any(p => p.Status == "completed-settings-unverified") ? "completed-settings-unverified" : "completed";
        }
        catch (Exception e)
        {
            report.Status = e is OperationCanceledException ? "cancelled" : "failed";
            report.Error = e.Message;
            log(report.Status + ": " + e.Message);
        }
        finally
        {
            if (backup != null)
            {
                try
                {
                    backup.Restore();
                    report.SettingsRestored = stopped;
                    if (!stopped) throw new IOException("Original bytes restored, but a remaining process may rewrite them. Stop the benchmark and use recover.");
                    log("Original settings restored.");
                }
                catch (Exception e)
                { report.RecoveryError = e.Message; report.Status = "failed"; log("Recovery required: " + e.Message); }
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            ReportWriter.Write(outputDirectory, report);
        }
        return report;
    }
}
