using System.Text;
using System.Text.Json;
using WukongRunner.Core;

namespace WukongRunner.Core.Tests;

internal sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WukongRunner-test-" + Guid.NewGuid().ToString("N"));
    public string File(string name) => Path.Combine(Root, name);
    public Workspace() => Directory.CreateDirectory(Root);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal static class Fixtures
{
    // Deliberately synthetic; this is not a captured game configuration.
    public const string Ini = """
        ; Synthetic fixture — unrelated text must survive: Привет
        [/Script/GSGameSettings.GSGameUserSettings]
        UISettingData=(("ViewDistance", "3"),("AntiAliasing", "3"),("ShadowQuality", "3"),("GlobalIllumination", "3"),("ReflectionQuality", "3"),("PostProcessing", "3"),("TextureQuality", "3"),("FxQuality", "3"),("VegetationQuality", "3"),("MaterialQuality", "3"),("Vsync", "1"),("ScreenMode", "1"),("LockFrameRate", "2"),("InsertFrame", "1"),("Rtx", "1"),("MotionBlur", "1"),("QualityLevel", "3"),("Unrelated", "keep"))
        ResolutionSizeX=1920
        FrameRateLimit=60 ; original comment
        UnrelatedSetting=keep-me
        [ScalabilityGroups]
        sg.ResolutionQuality=100
        sg.ShadowQuality=2
        """;
    public static Profile Cpu => new() { Name = "cpu", Rationale = "Synthetic CPU profile", Width = 1280, Height = 720, RenderPercent = 50, Quality = 1 };
    public static Profile Gpu => new() { Name = "gpu", Rationale = "Synthetic GPU profile", Width = 1920, Height = 1080, Quality = 5 };
    public static AutomationOptions Options => new() { PollIntervalMs = 10, ResultStableMs = 30, RunTimeoutSeconds = 1 };
    public static byte[] Result(Profile profile, double average = 80, Dictionary<string, string>? settings = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["FPSAvg"] = average, ["FPSMin"] = average - 20, ["FPSMax"] = average + 20,
            ["Settings"] = settings ?? SettingsEditor.ExpectedSettings(profile), ["Synthetic"] = true,
            ["FPS95"] = 71.2, ["Records"] = new[] { new { CPU = 1, GPU = 2 } }
        });
}

internal sealed class FakeHost(Workspace workspace) : IBenchmarkHost
{
    public string SettingsPath { get; } = workspace.File("settings.ini");
    public IReadOnlyList<string> ResultDirectories { get; } = [workspace.File("history")];
    public MachineInfo Machine => new("Synthetic", "Synthetic", "Synthetic CPU", 8, 1024, [], []);
    public int DesktopWidth => 2560;
    public int DesktopHeight => 1440;
    public bool Synthetic => true;
    public List<string> Started { get; } = [];
    public int Stops { get; private set; }
    public Func<Profile, CancellationToken, Task>? OnStart { get; set; }
    public bool FailStop { get; set; }
    public bool FailTrigger { get; set; }
    public bool Mismatch { get; set; }
    public bool OmitSettings { get; set; }
    private Profile? current;
    public void Initialize()
    {
        System.IO.File.WriteAllText(SettingsPath, Fixtures.Ini, new UTF8Encoding(false));
        Directory.CreateDirectory(ResultDirectories[0]);
    }
    public async Task StartAsync(Profile profile, CancellationToken cancellationToken)
    {
        Started.Add(profile.Name); current = profile;
        if (OnStart != null) await OnStart(profile, cancellationToken);
    }
    public Task TriggerAsync(CancellationToken cancellationToken)
    {
        if (FailTrigger) throw new IOException("Synthetic trigger failure.");
        var values = SettingsEditor.ExpectedSettings(current!);
        if (Mismatch) values["InsertFrame"] = "1";
        if (OmitSettings) values.Clear();
        System.IO.File.WriteAllBytes(Path.Combine(ResultDirectories[0], "result.json"), Fixtures.Result(current!, current!.Name == "cpu" ? 120 : 70, values));
        return Task.CompletedTask;
    }
    public void CheckHealth() { }
    public Task StopAsync(CancellationToken cancellationToken)
    { Stops++; return FailStop ? Task.FromException(new IOException("Synthetic stop failure.")) : Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
