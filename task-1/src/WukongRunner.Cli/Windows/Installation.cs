using Microsoft.Win32;
using System.Runtime.Versioning;
using WukongRunner.Core;

namespace WukongRunner.Cli.Windows;

[SupportedOSPlatform("windows")]
public sealed record Installation(string SteamExe, string Directory, string SettingsFile, IReadOnlyList<string> Executables)
{
    public const string AppId = "3132990";
    public static Installation Find(string? installOverride, string? settingsOverride, string? steamOverride)
    {
        var steamDirectory = steamOverride ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var steamExe = Path.Combine(steamDirectory, "steam.exe");
        if (!File.Exists(steamExe)) throw new FileNotFoundException("Steam was not found. Use --steam-dir.", steamExe);
        var install = installOverride;
        if (install == null)
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamDirectory };
            var libraryFile = Path.Combine(steamDirectory, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                var root = SteamVdf.Parse(File.ReadAllText(libraryFile));
                if (root.Children.TryGetValue("libraryfolders", out var folders))
                {
                    foreach (var node in folders.Children.Values)
                        if (node.Values.TryGetValue("path", out var path)) libraries.Add(path);
                    foreach (var pair in folders.Values)
                        if (int.TryParse(pair.Key, out _)) libraries.Add(pair.Value);
                }
            }
            foreach (var library in libraries)
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
                if (!File.Exists(manifest)) continue;
                var root = SteamVdf.Parse(File.ReadAllText(manifest));
                if (!root.Children.TryGetValue("AppState", out var state) ||
                    !state.Values.TryGetValue("appid", out var id) || id != AppId ||
                    !state.Values.TryGetValue("installdir", out var directory)) continue;
                if (directory.IndexOfAny(['/', '\\']) >= 0 || directory is "." or "..")
                    throw new InvalidDataException("Unexpected installation directory in Steam manifest.");
                var candidate = Path.Combine(library, "steamapps", "common", directory);
                if (System.IO.Directory.Exists(candidate)) { install = candidate; break; }
            }
        }
        if (install == null) throw new DirectoryNotFoundException("Install Black Myth: Wukong Benchmark Tool in Steam, or specify --install-dir.");
        install = Path.GetFullPath(install);
        var executables = new[] { Path.Combine(install, "b1_benchmark.exe"), Path.Combine(install, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe") }
            .Where(File.Exists).ToArray();
        if (executables.Length == 0) throw new FileNotFoundException("Expected benchmark executables were not found in the installation directory.");
        var settings = settingsOverride ?? Path.Combine(install, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
        if (!File.Exists(settings)) throw new FileNotFoundException("Settings are absent. Run the benchmark once, finish initial setup, exit, and retry; alternatively use --settings-file.", settings);
        return new(Path.GetFullPath(steamExe), install, Path.GetFullPath(settings), executables);
    }
}
