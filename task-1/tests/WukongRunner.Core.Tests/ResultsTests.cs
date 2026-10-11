using System.Text;
using WukongRunner.Core;

namespace WukongRunner.Core.Tests;

public sealed class ResultsTests
{
    [Theory]
    [InlineData("{\"FPSAvg\":60,\"FPSMax\":90}")]
    [InlineData("{\"FPSAvg\":\"NaN\",\"FPSMin\":10,\"FPSMax\":90}")]
    [InlineData("{\"FPSAvg\":60,\"FPSMin\":70,\"FPSMax\":90}")]
    [InlineData("{\"FPSAvg\":60,\"FPSMin\":-1,\"FPSMax\":90}")]
    [InlineData("{\"FPSAvg\":0,\"FPSMin\":0,\"FPSMax\":0}")]
    [InlineData("{\"FPSAvg\":60,\"FPSAvg\":70,\"FPSMin\":10,\"FPSMax\":90}")]
    public void RejectsMissingInvalidAndAmbiguousMetrics(string json) =>
        Assert.Throws<InvalidDataException>(() => ResultParser.Parse(Encoding.UTF8.GetBytes(json), Fixtures.Cpu, Fixtures.Options));

    [Fact]
    public void MissingSettingsAreUnverifiedAndAdditionalFieldsKeepRawMeaning()
    {
        var parsed = ResultParser.Parse(Fixtures.Result(Fixtures.Cpu, settings: []), Fixtures.Cpu, Fixtures.Options);
        Assert.All(parsed.Settings, s => Assert.Equal("unverified", s.Status));
        Assert.Equal("71.2", parsed.AdditionalRawFields["FPS95"]);
        Assert.DoesNotContain("Records", parsed.AdditionalRawFields.Keys);
    }

    [Fact]
    public void ReportsSettingMismatch()
    {
        var settings = SettingsEditor.ExpectedSettings(Fixtures.Cpu);
        settings["InsertFrame"] = "1";
        var parsed = ResultParser.Parse(Fixtures.Result(Fixtures.Cpu, settings: settings), Fixtures.Cpu, Fixtures.Options);
        Assert.Equal("mismatch", Assert.Single(parsed.Settings, s => s.Key == "InsertFrame").Status);
    }

    [Fact]
    public void SupportsConfiguredResultObjectAndUtf8Bom()
    {
        var inner = Encoding.UTF8.GetString(Fixtures.Result(Fixtures.Cpu));
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"result\":" + inner + "}")).ToArray();
        var parsed = ResultParser.Parse(bytes, Fixtures.Cpu, Fixtures.Options with { ResultObjectPointer = "/result" });
        Assert.Equal(80, parsed.Metrics.AverageFps);
    }

    [Fact]
    public async Task RetriesAValidButIncompleteJsonUntilTheRequiredFieldsArrive()
    {
        using var workspace = new Workspace();
        var watcher = new ResultWatcher([workspace.Root], Fixtures.Options with { RunTimeoutSeconds = 3 });
        var baseline = watcher.CaptureBaseline();
        var path = workspace.File("result.json");
        File.WriteAllText(path, "{\"FPSAvg\":80}");
        var waiting = watcher.WaitAsync(baseline, Fixtures.Cpu, () => { }, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        File.WriteAllBytes(path, Fixtures.Result(Fixtures.Cpu));
        var result = await waiting;
        Assert.Equal(80, result.Result.Metrics.AverageFps);
    }

    [Fact]
    public async Task AcceptsAnOverwrittenResultButNotTheBaseline()
    {
        using var workspace = new Workspace();
        var path = workspace.File("result.json");
        File.WriteAllBytes(path, Fixtures.Result(Fixtures.Cpu, 60));
        var watcher = new ResultWatcher([workspace.Root], Fixtures.Options);
        var baseline = watcher.CaptureBaseline();
        File.WriteAllBytes(path, Fixtures.Result(Fixtures.Cpu, 120));
        var result = await watcher.WaitAsync(baseline, Fixtures.Cpu, () => { }, TestContext.Current.CancellationToken);
        Assert.Equal(120, result.Result.Metrics.AverageFps);
    }

    [Fact]
    public async Task RejectsAnOldResultCopiedUnderANewFilename()
    {
        using var workspace = new Workspace();
        File.WriteAllBytes(workspace.File("old.json"), Fixtures.Result(Fixtures.Cpu));
        var watcher = new ResultWatcher([workspace.Root], Fixtures.Options);
        var baseline = watcher.CaptureBaseline();
        File.Copy(workspace.File("old.json"), workspace.File("new.json"));
        await Assert.ThrowsAsync<TimeoutException>(() => watcher.WaitAsync(baseline, Fixtures.Cpu, () => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsMultipleDistinctFreshResults()
    {
        using var workspace = new Workspace();
        var watcher = new ResultWatcher([workspace.Root], Fixtures.Options);
        var baseline = watcher.CaptureBaseline();
        File.WriteAllBytes(workspace.File("a.json"), Fixtures.Result(Fixtures.Cpu, 60));
        File.WriteAllBytes(workspace.File("b.json"), Fixtures.Result(Fixtures.Cpu, 90));
        await Assert.ThrowsAsync<InvalidDataException>(() => watcher.WaitAsync(baseline, Fixtures.Cpu, () => { }, TestContext.Current.CancellationToken));
    }
}
