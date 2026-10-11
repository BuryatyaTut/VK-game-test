using System.Globalization;
using System.Net;
using System.Text;

namespace WukongRunner.Core;

public static class ReportWriter
{
    private static string E(object? value) => WebUtility.HtmlEncode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Unavailable");
    public static void Write(string directory, RunReport report)
    {
        JsonFiles.Write(Path.Combine(directory, "report.json"), report);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <title>Wukong benchmark report</title><style>
            body{font:16px/1.55 system-ui,sans-serif;color:#17212f;background:#f4f6f8;margin:0;padding:32px}
            main{max-width:1060px;margin:auto}section{background:white;padding:24px;margin:20px 0;border:1px solid #d7dde5;border-radius:10px}
            h1,h2{line-height:1.2}table{border-collapse:collapse;width:100%;margin:16px 0}th,td{text-align:left;border-bottom:1px solid #dde2e8;padding:9px;overflow-wrap:anywhere}
            .notice{background:#fff1ce;padding:16px;border-left:5px solid #9b6500}.muted{color:#526071}code{overflow-wrap:anywhere}a{color:#1554a2}
            @media(max-width:600px){body{padding:12px}section{padding:12px}th,td{padding:5px;font-size:13px}}
            </style><main><h1>Black Myth: Wukong benchmark</h1>
            """);
        if (report.Synthetic) html.Append("<p class=notice><strong>SYNTHETIC DEMO — invented input values, not measurements of this computer.</strong></p>");
        html.Append($"<p>Status: <strong>{E(report.Status)}</strong> · Original settings restored: {E(report.SettingsRestored)}</p>");
        html.Append($"<p class=muted>Run {E(report.RunId)} · {E(report.StartedUtc.ToString("O"))}</p>");
        if (report.Error != null) html.Append($"<p class=notice>{E(report.Error)}</p>");
        if (report.RecoveryError != null) html.Append($"<p class=notice>Recovery: {E(report.RecoveryError)}</p>");
        html.Append($"<section><h2>Computer</h2><table><tr><th>CPU</th><td>{E(report.Machine.Cpu)}</td></tr><tr><th>Logical processors</th><td>{E(report.Machine.LogicalProcessors)}</td></tr><tr><th>RAM</th><td>{E(report.Machine.RamBytes is ulong ram ? $"{ram / 1073741824d:F1} GiB" : null)}</td></tr><tr><th>OS / architecture</th><td>{E(report.Machine.OperatingSystem)} / {E(report.Machine.Architecture)}</td></tr>");
        foreach (var gpu in report.Machine.Gpus) html.Append($"<tr><th>GPU</th><td>{E(gpu.Name)} · Driver: {E(gpu.DriverVersion)}</td></tr>");
        html.Append("</table>");
        foreach (var note in report.Machine.Notes) html.Append($"<p>{E(note)}</p>");
        html.Append("</section>");
        foreach (var pass in report.Passes)
        {
            html.Append($"<section><h2>{E(pass.Profile.Name.ToUpperInvariant())} pass</h2><p>Status: {E(pass.Status)}</p><p>{E(pass.Profile.Rationale)}</p>");
            html.Append($"<p>Requested: {pass.Profile.Width} × {pass.Profile.Height}, render scale {pass.Profile.RenderPercent}%, quality {pass.Profile.Quality}/5.</p>");
            if (pass.Metrics is { } metrics)
                html.Append($"<table><tr><th>Average FPS</th><th>Minimum FPS</th><th>Maximum FPS</th></tr><tr><td>{metrics.AverageFps.ToString("F2", CultureInfo.InvariantCulture)}</td><td>{metrics.MinimumFps.ToString("F2", CultureInfo.InvariantCulture)}</td><td>{metrics.MaximumFps.ToString("F2", CultureInfo.InvariantCulture)}</td></tr></table>");
            if (pass.Error != null) html.Append($"<p class=notice>{E(pass.Error)}</p>");
            if (pass.CleanupError != null) html.Append($"<p class=notice>Cleanup: {E(pass.CleanupError)}</p>");
            if (pass.RawResultFile != null)
                html.Append($"<p><a href=\"{E(pass.RawResultFile)}\">Original result JSON</a> · SHA-256: <code>{E(pass.RawSha256)}</code></p>");
            html.Append("<details><summary>Requested and reported settings</summary><p>Unverified means the benchmark did not expose a matching field; requested values are not evidence that the engine used them.</p><table><tr><th>Setting</th><th>Requested</th><th>Reported</th><th>Check</th></tr>");
            foreach (var check in pass.Settings) html.Append($"<tr><td>{E(check.Key)}</td><td>{E(check.Requested)}</td><td>{E(check.Reported)}</td><td>{E(check.Status)}</td></tr>");
            html.Append("</table></details>");
            if (pass.AdditionalRawFields.Count > 0)
            {
                html.Append("<details><summary>Additional raw fields</summary><p>Names and values preserved without interpreting units or percentile definitions.</p><table>");
                foreach (var pair in pass.AdditionalRawFields) html.Append($"<tr><th>{E(pair.Key)}</th><td>{E(pair.Value)}</td></tr>");
                html.Append("</table></details>");
            }
            html.Append("</section>");
        }
        html.Append($"<p class=muted>{E(report.IntegrationStatus)} Two passes are individual observations; they do not establish repeatability or prove a CPU/GPU bottleneck.</p><p><a href=report.json>Machine-readable report</a></p></main></html>");
        AtomicFile.Write(Path.Combine(directory, "report.html"), Encoding.UTF8.GetBytes(html.ToString()));
    }
}
