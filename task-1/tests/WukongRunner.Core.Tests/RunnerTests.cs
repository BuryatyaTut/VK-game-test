using WukongRunner.Core;

namespace WukongRunner.Core.Tests;

public sealed class RunnerTests
{
    [Fact]
    public async Task RunsExactlyCpuThenGpuRestoresSettingsAndPreservesEvidence()
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace); host.Initialize();
        var original = File.ReadAllBytes(host.SettingsPath);
        var report = await new Runner(Fixtures.Options, _ => { }).RunAsync(host, [Fixtures.Cpu, Fixtures.Gpu], workspace.File("out"), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "cpu", "gpu" }, host.Started);
        Assert.Equal(2, host.Stops);
        Assert.Equal("completed", report.Status);
        Assert.True(report.SettingsRestored);
        Assert.Equal(original, File.ReadAllBytes(host.SettingsPath));
        Assert.All(report.Passes, p => Assert.True(File.Exists(Path.Combine(workspace.File("out"), p.RawResultFile!))));
        Assert.Contains("SYNTHETIC DEMO", File.ReadAllText(workspace.File("out/report.html")));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("trigger")]
    [InlineData("stop")]
    public async Task FailureAlwaysAttemptsRecoveryAndDoesNotStartGpu(string failure)
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace); host.Initialize();
        var original = File.ReadAllBytes(host.SettingsPath);
        host.OnStart = failure == "start" ? (_, _) => throw new IOException("Synthetic start failure") : null;
        host.FailTrigger = failure == "trigger";
        host.FailStop = failure == "stop";
        var report = await new Runner(Fixtures.Options, _ => { }).RunAsync(host, [Fixtures.Cpu, Fixtures.Gpu], workspace.File("out"), TestContext.Current.CancellationToken);
        Assert.Equal("failed", report.Status);
        Assert.Equal("cpu", Assert.Single(host.Started));
        Assert.Equal(1, host.Stops);
        Assert.Equal(original, File.ReadAllBytes(host.SettingsPath));
        Assert.Equal(failure != "stop", report.SettingsRestored);
        Assert.True(File.Exists(workspace.File("out/recovery.json")));
    }

    [Fact]
    public async Task CancellationRestoresSettingsAndKeepsCompletedCpuResult()
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace); host.Initialize();
        using var cancellation = new CancellationTokenSource();
        host.OnStart = (profile, token) =>
        { if (profile.Name == "gpu") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); } return Task.CompletedTask; };
        var report = await new Runner(Fixtures.Options, _ => { }).RunAsync(host, [Fixtures.Cpu, Fixtures.Gpu], workspace.File("out"), cancellation.Token);
        Assert.Equal("cancelled", report.Status);
        Assert.True(report.SettingsRestored);
        Assert.NotNull(report.Passes[0].Metrics);
        Assert.Equal("cancelled", report.Passes[1].Status);
    }

    [Fact]
    public async Task SettingMismatchFailsInsteadOfClaimingSuccess()
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace) { Mismatch = true }; host.Initialize();
        var report = await new Runner(Fixtures.Options, _ => { }).RunAsync(host, [Fixtures.Cpu, Fixtures.Gpu], workspace.File("out"), TestContext.Current.CancellationToken);
        Assert.Equal("failed", report.Status);
        Assert.Equal("settings-mismatch", report.Passes[0].Status);
        Assert.NotNull(report.Passes[0].Metrics);
        Assert.Single(host.Started);
    }

    [Fact]
    public async Task MissingSettingsLeaveBothMeasuredPassesExplicitlyUnverified()
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace) { OmitSettings = true }; host.Initialize();
        var report = await new Runner(Fixtures.Options, _ => { }).RunAsync(host, [Fixtures.Cpu, Fixtures.Gpu], workspace.File("out"), TestContext.Current.CancellationToken);
        Assert.Equal("completed-settings-unverified", report.Status);
        Assert.All(report.Passes, p => Assert.NotNull(p.Metrics));
    }

    [Fact]
    public async Task HtmlEscapesUntrustedText()
    {
        using var workspace = new Workspace();
        var host = new FakeHost(workspace); host.Initialize();
        await new Runner(Fixtures.Options, _ => { }).RunAsync(host,
            [Fixtures.Cpu with { Rationale = "<script>alert('x')</script>" }, Fixtures.Gpu], workspace.File("out"), TestContext.Current.CancellationToken);
        var html = File.ReadAllText(workspace.File("out/report.html"));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
