using WukongRunner.Core;

namespace WukongRunner.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ParsesSteamLibrariesWithEscapesAndComments()
    {
        var parsed = SteamVdf.Parse("""
            // Synthetic Steam metadata
            "libraryfolders" {
              "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "3132990" "123" } }
              "1" { "path" "D:\\Games" }
            }
            """);
        Assert.Equal(@"C:\Program Files (x86)\Steam", parsed.Children["libraryfolders"].Children["0"].Values["path"]);
        Assert.Equal(@"D:\Games", parsed.Children["libraryfolders"].Children["1"].Values["path"]);
    }
    [Theory]
    [InlineData("\"x\" { \"y\" \"z\"")]
    [InlineData("\"x\" \"a\" \"x\" \"b\"")]
    [InlineData("\"unfinished")]
    public void RejectsMalformedSteamMetadata(string text) => Assert.Throws<InvalidDataException>(() => SteamVdf.Parse(text));

    [Fact]
    public void RejectsInvalidSettingsBeforeLaunchingAnything()
    {
        Assert.Throws<InvalidDataException>(() => (Fixtures.Options with { PollIntervalMs = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fixtures.Options with { Steps = [new() { Kind = "click", X = double.NaN }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fixtures.Cpu with { UiOverrides = new() { ["InsertFrame"] = "1" } }).Validate());
        Assert.Throws<InvalidDataException>(() => (Fixtures.Cpu with { Width = 0 }).Validate());
    }
}
