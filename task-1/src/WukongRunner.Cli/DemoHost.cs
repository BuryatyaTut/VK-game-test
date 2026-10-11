using System.Runtime.InteropServices;
using System.Text;
using WukongRunner.Core;

namespace WukongRunner.Cli;

public sealed class DemoHost : IBenchmarkHost
{
    private readonly string root;
    private readonly string? failPass;
    private Task? writer;
    private CancellationTokenSource? lifetime;
    private Profile? active;
    public string SettingsPath { get; }
    public IReadOnlyList<string> ResultDirectories { get; }
    public int DesktopWidth => 1920;
    public int DesktopHeight => 1080;
    public bool Synthetic => true;
    public MachineInfo Machine { get; } = new("Synthetic demonstration", RuntimeInformation.ProcessArchitecture.ToString(),
        "DEMO CPU — not detected hardware", 8, 16UL * 1024 * 1024 * 1024,
        [new("DEMO GPU — not detected hardware", "synthetic")], ["All machine details and FPS values in this demo are synthetic."]);

    public DemoHost(string? failPass)
    {
        this.failPass = failPass;
        root = Path.Combine(Path.GetTempPath(), "WukongRunner-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SettingsPath = Path.Combine(root, "GameUserSettings.ini");
        ResultDirectories = [Path.Combine(root, "history")];
        Directory.CreateDirectory(ResultDirectories[0]);
        var profile = new Profile { Name = "cpu", Rationale = "Synthetic settings fixture", Width = 1280, Height = 720 };
        var pairs = string.Join(',', SettingsEditor.UiValues(profile).Select(p => $"(\"{p.Key}\", \"{p.Value}\")"));
        File.WriteAllText(SettingsPath, $"; SYNTHETIC fixture\n[{SettingsEditor.GameSection}]\nUISettingData=({pairs})\nUnrelatedSetting=keep-me\n[ScalabilityGroups]\nsg.ResolutionQuality=100\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(ResultDirectories[0], "old.json"), "{\"FPSAvg\":999,\"FPSMin\":998,\"FPSMax\":1000}");
    }

    public Task StartAsync(Profile profile, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); active = profile; lifetime = new(); return Task.CompletedTask; }
    public Task TriggerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = active ?? throw new InvalidOperationException("Demo is not started.");
        writer = WriteAsync(profile, lifetime!.Token);
        return Task.CompletedTask;
    }
    private async Task WriteAsync(Profile profile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(ResultDirectories[0], "current.json");
        await File.WriteAllTextAsync(path, "{\"FPSAvg\":", cancellationToken);
        await Task.Delay(700, cancellationToken);
        if (failPass == profile.Name) throw new IOException("Requested synthetic benchmark failure.");
        var avg = profile.Name == "cpu" ? 140d : 62d;
        JsonFiles.Write(path, new Dictionary<string, object>
        {
            ["FPSAvg"] = avg, ["FPSMin"] = avg - 20, ["FPSMax"] = avg + 18,
            ["Settings"] = SettingsEditor.ExpectedSettings(profile), ["Synthetic"] = true
        });
    }
    public void CheckHealth()
    { if (writer?.IsFaulted == true) throw new IOException("Synthetic benchmark process failed.", writer.Exception); }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (lifetime != null) await lifetime.CancelAsync();
        if (writer != null)
        {
            try { await writer.WaitAsync(cancellationToken); }
            catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
        lifetime?.Dispose(); lifetime = null; writer = null;
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        Directory.Delete(root, recursive: true); // Only the unique directory created by this demo.
    }
}
