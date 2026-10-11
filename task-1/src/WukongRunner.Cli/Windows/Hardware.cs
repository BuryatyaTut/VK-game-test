using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using WukongRunner.Core;

namespace WukongRunner.Cli.Windows;

[SupportedOSPlatform("windows")]
public static class Hardware
{
    public static async Task<MachineInfo> ReadAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference='Stop'
            $cpu=@(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name)
            $ram=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
            $gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
            @{Cpu=($cpu -join '; ');RamBytes=$ram;Gpus=$gpu} | ConvertTo-Json -Depth 4 -Compress
            """;
        var notes = new List<string> { "GPU list contains installed adapters; it does not prove which adapter rendered the benchmark." };
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(info) ?? throw new IOException("Could not query Windows hardware.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new IOException("Windows hardware query failed: " + await error);
                using var doc = JsonDocument.Parse(await output);
                var root = doc.RootElement;
                var gpus = root.GetProperty("Gpus").EnumerateArray().Select(g => new GpuInfo(g.GetProperty("Name").GetString() ?? "Unknown", g.GetProperty("DriverVersion").GetString())).ToArray();
                return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), root.GetProperty("Cpu").GetString() ?? "Unknown",
                    Environment.ProcessorCount, root.GetProperty("RamBytes").GetUInt64(), gpus, notes);
            }
            finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None); } }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
        {
            notes.Add("Hardware details unavailable: " + e.Message);
            return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), "Unavailable", Environment.ProcessorCount, null, [], notes);
        }
    }
}
