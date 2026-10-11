using System.Text.Json;
using System.Text.Json.Serialization;

namespace WukongRunner.Core;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty configuration: {path}");

    public static void Write<T>(string path, T value) => AtomicFile.Write(path,
        JsonSerializer.SerializeToUtf8Bytes(value, Options));
}

public sealed record Profile
{
    public required string Name { get; init; }
    public required string Rationale { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int RenderPercent { get; init; } = 100;
    public int Quality { get; init; } = 5;
    public Dictionary<string, string> UiOverrides { get; init; } = [];

    public void Validate()
    {
        if (Name is not ("cpu" or "gpu")) throw new InvalidDataException("Profile name must be cpu or gpu.");
        if (string.IsNullOrWhiteSpace(Rationale)) throw new InvalidDataException("A profile rationale is required.");
        if (!((Width == 0 && Height == 0) || (Width is >= 640 and <= 7680 && Height is >= 360 and <= 4320)))
            throw new InvalidDataException("Use width/height 0 for the desktop, or a supported explicit resolution.");
        if (RenderPercent is < 25 or > 100 || Quality is < 1 or > 5)
            throw new InvalidDataException("RenderPercent must be 25–100; Quality must be 1–5.");
        if (UiOverrides.Any(p => p.Key.IndexOfAny(['"', '\r', '\n']) >= 0 ||
                                p.Value.IndexOfAny(['"', '\r', '\n']) >= 0))
            throw new InvalidDataException("UI overrides must be single-line values without quotes.");
        foreach (var key in new[] { "InsertFrame", "Vsync", "LockFrameRate", "Rtx" })
            if (UiOverrides.TryGetValue(key, out var value) && value != "0")
                throw new InvalidDataException($"{key} must remain disabled for these profiles.");
    }

    public Profile Resolve(int desktopWidth, int desktopHeight) => Width == 0
        ? this with { Width = desktopWidth, Height = desktopHeight } : this;
}

public sealed record InputStep
{
    public string Kind { get; init; } = "key";
    public string Key { get; init; } = "Enter";
    public double X { get; init; }
    public double Y { get; init; }
    public int DelayAfterMs { get; init; } = 1000;
}

public sealed record AutomationOptions
{
    public int LaunchTimeoutSeconds { get; init; } = 120;
    public int MenuReadyDelaySeconds { get; init; } = 30;
    public int RunTimeoutSeconds { get; init; } = 900;
    public int CleanupTimeoutSeconds { get; init; } = 15;
    public int PollIntervalMs { get; init; } = 500;
    public int ResultStableMs { get; init; } = 2000;
    public long MaxResultBytes { get; init; } = 64 * 1024 * 1024;
    public string ResultObjectPointer { get; init; } = "";
    public string ResultSettingsPointer { get; init; } = "/Settings";
    public List<InputStep> Steps { get; init; } = [new()];

    public void Validate()
    {
        if (LaunchTimeoutSeconds is < 1 or > 600 || MenuReadyDelaySeconds is < 0 or > 600 ||
            RunTimeoutSeconds is < 1 or > 7200 || CleanupTimeoutSeconds is < 1 or > 120 ||
            PollIntervalMs is < 10 or > 10000 || ResultStableMs < PollIntervalMs || ResultStableMs > 60000 ||
            MaxResultBytes is < 1024 or > 256 * 1024 * 1024)
            throw new InvalidDataException("Automation limits are outside the supported ranges.");
        foreach (var pointer in new[] { ResultObjectPointer, ResultSettingsPointer })
            if (pointer != "" && !pointer.StartsWith('/')) throw new InvalidDataException("JSON pointers must start with / or be empty.");
        if (Steps.Count is < 1 or > 20) throw new InvalidDataException("Specify 1–20 menu steps.");
        foreach (var step in Steps)
        {
            if (step.Kind is not ("key" or "click" or "wait") || step.DelayAfterMs is < 0 or > 120000)
                throw new InvalidDataException("Invalid menu step or delay.");
            if (step.Kind == "click" && (!double.IsFinite(step.X) || !double.IsFinite(step.Y) ||
                    step.X is < 0 or > 1 || step.Y is < 0 or > 1)) throw new InvalidDataException("Click coordinates must be between 0 and 1.");
            if (step.Kind == "key" && step.Key is not ("Enter" or "Space" or "Escape" or "Up" or "Down" or "Left" or "Right" or "Tab"))
                throw new InvalidDataException($"Unsupported key: {step.Key}");
        }
    }
}

public sealed record MachineInfo(string OperatingSystem, string Architecture, string Cpu,
    int LogicalProcessors, ulong? RamBytes, IReadOnlyList<GpuInfo> Gpus, IReadOnlyList<string> Notes);
public sealed record GpuInfo(string Name, string? DriverVersion);
public sealed record SettingCheck(string Key, string Requested, string? Reported, string Status);
public sealed record BenchmarkMetrics(double AverageFps, double MinimumFps, double MaximumFps);
public sealed record ParsedResult(BenchmarkMetrics Metrics, IReadOnlyList<SettingCheck> Settings,
    IReadOnlyDictionary<string, string> AdditionalRawFields);
public sealed record ResultSnapshot(string SourcePath, byte[] Bytes, string Sha256, ParsedResult Result);

public sealed class PassReport
{
    public required Profile Profile { get; init; }
    public string Status { get; set; } = "not-started";
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
    public BenchmarkMetrics? Metrics { get; set; }
    public IReadOnlyList<SettingCheck> Settings { get; set; } = [];
    public IReadOnlyDictionary<string, string> AdditionalRawFields { get; set; } = new Dictionary<string, string>();
    public string? SourcePath { get; set; }
    public string? RawResultFile { get; set; }
    public string? RawSha256 { get; set; }
}

public sealed class RunReport
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public required bool Synthetic { get; init; }
    public required MachineInfo Machine { get; init; }
    public required AutomationOptions Automation { get; init; }
    public required List<PassReport> Passes { get; init; }
    public string Status { get; set; } = "running";
    public bool SettingsRestored { get; set; }
    public string? Error { get; set; }
    public string? RecoveryError { get; set; }
    public string IntegrationStatus { get; init; } = "Windows benchmark integration and profile bottlenecks are not empirically validated.";
}

public interface IBenchmarkHost : IAsyncDisposable
{
    string SettingsPath { get; }
    IReadOnlyList<string> ResultDirectories { get; }
    MachineInfo Machine { get; }
    int DesktopWidth { get; }
    int DesktopHeight { get; }
    bool Synthetic { get; }
    Task StartAsync(Profile profile, CancellationToken cancellationToken);
    Task TriggerAsync(CancellationToken cancellationToken);
    void CheckHealth();
    Task StopAsync(CancellationToken cancellationToken);
}
