using System.Text;
using WukongRunner.Core;

namespace WukongRunner.Core.Tests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public void PreservesEncodingUnrelatedValuesAndRestoresExactOriginal(string format)
    {
        using var workspace = new Workspace();
        Encoding encoding = format switch
        {
            "utf8-bom" => new UTF8Encoding(true), "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true), _ => new UTF8Encoding(false)
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(Fixtures.Ini.Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        var settings = workspace.File("settings.ini");
        File.WriteAllBytes(settings, bytes);
        var backup = new SettingsBackup(settings, workspace.Root);
        backup.Apply(Fixtures.Cpu);
        var editedBytes = File.ReadAllBytes(settings);
        Assert.True(editedBytes.AsSpan().StartsWith(encoding.GetPreamble()));
        var edited = encoding.GetString(editedBytes);
        Assert.Contains("UnrelatedSetting=keep-me", edited);
        Assert.Contains("Привет", edited);
        Assert.Contains("(\"Unrelated\", \"keep\")", edited);
        Assert.Contains("sg.ShadowQuality=0", edited);
        Assert.Contains("sg.ResolutionQuality=50", edited);
        Assert.Contains("ResolutionSizeX=1280", edited);
        Assert.Contains("FrameRateLimit=0.000000 ; original comment", edited);
        Assert.DoesNotContain("\n", edited.Replace("\r\n", "", StringComparison.Ordinal));
        backup.Restore();
        Assert.Equal(bytes, File.ReadAllBytes(settings));
    }

    [Fact]
    public void RejectsMissingOrAmbiguousSettings()
    {
        Assert.Throws<InvalidDataException>(() => SettingsEditor.Apply(Encoding.UTF8.GetBytes("[ScalabilityGroups]\n"), Fixtures.Cpu));
        var duplicate = Fixtures.Ini + "\n[ScalabilityGroups]\n";
        Assert.Throws<InvalidDataException>(() => SettingsEditor.Apply(Encoding.UTF8.GetBytes(duplicate), Fixtures.Cpu));
        var missing = Fixtures.Ini.Replace("(\"InsertFrame\", \"1\"),", "", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => SettingsEditor.Apply(Encoding.UTF8.GetBytes(missing), Fixtures.Cpu));
    }

    [Fact]
    public void RejectsTamperedRecoveryBackupWithoutOverwritingSettings()
    {
        using var workspace = new Workspace();
        var path = workspace.File("settings.ini");
        File.WriteAllText(path, Fixtures.Ini);
        var backup = new SettingsBackup(path, workspace.Root);
        backup.Apply(Fixtures.Cpu);
        var edited = File.ReadAllBytes(path);
        File.AppendAllText(workspace.File("original-settings.ini"), "tampered");
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Recover(backup.ManifestPath));
        Assert.Equal(edited, File.ReadAllBytes(path));
    }

    [Fact]
    public void RecoveryManifestRestoresExactBytes()
    {
        using var workspace = new Workspace();
        var path = workspace.File("settings.ini");
        File.WriteAllText(path, Fixtures.Ini);
        var original = File.ReadAllBytes(path);
        var backup = new SettingsBackup(path, workspace.Root);
        backup.Apply(Fixtures.Cpu);
        SettingsBackup.Recover(backup.ManifestPath);
        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
