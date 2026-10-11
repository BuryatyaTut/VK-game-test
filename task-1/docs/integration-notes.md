# Integration implementation and open assumptions

No authentic benchmark configuration or result file was captured locally. All observations from execution in this repository use synthetic fixtures. The following assumptions are explicit so an integration problem produces a diagnosable result rather than a plausible-looking fabricated score.

| Area | Implemented behavior | Still requires target validation |
| --- | --- | --- |
| Steam | Read `libraryfolders.vdf`, match `appmanifest_3132990.acf`, launch `steam.exe -applaunch 3132990` | Steam startup/update dialogs and launch lifecycle |
| Installation | Explicit override or matching Steam manifest | Alternate benchmark layouts |
| Configuration | `b1/Saved/Config/Windows/GameUserSettings.ini`, with override | Actual saved location/version |
| INI structure | GSGameUserSettings section, `UISettingData` tuple pairs, ScalabilityGroups | Meaning of game-specific enum values and interactions with upscaling/resolution UI fields |
| Processes | New matching executable paths and start times; reject pre-existing benchmark; keep process handles | Launcher-to-renderer lifecycle; simultaneous manual launches must be avoided |
| Window | Win32 foreground checks, client-relative coordinates, keyboard/mouse input | Actual main-menu layout, focus behavior, readiness and default Enter action |
| Completion | Fresh stable JSON with valid FPSAvg/FPSMin/FPSMax; content hashes reject stale results | Actual output path and schema; last-frame/result-save timing |
| Reported settings | Configurable JSON pointer to a flat map; exact field names | Actual settings container/field naming; absent fields remain unverified |
| Hardware | Windows CIM CPU/RAM and installed GPU names/drivers, with unavailable fallback | Active rendering adapter is not inferred from the installed-adapter list |

The editor validates expected structure before changing a file. It preserves unrelated values, newline style, and UTF-8/UTF-16 BOM encoding. Recovery restores the original bytes exactly. It does not edit global driver settings or request administrative rights.

The default input sequence is one Enter after a 30-second delay. There is no established app-specific unattended flag, accessible-control integration, or screen-state recognition. Timing and input events are not evidence that a pass started or finished. If the sequence does not start a pass, the process/result timeout fails the run.

The game may use additional UI fields to override standard Unreal resolution and render scale. The profiles write the standard keys and known quality/toggle pairs; those writes are not proof of effective application. `cpu-requested.ini` / `gpu-requested.ini` intentionally describe requested settings. Output fields are needed for verification; missing fields produce exit code 3 after collecting both results.

FPSAvg, FPSMin and FPSMax are the only interpreted benchmark metrics. The parser supports finite JSON numbers and invariant numeric strings. Other scalar fields are preserved as raw values. In particular, FPS95 is not relabeled as 1% low/5th percentile; CPUAvg/GPUAvg are not assumed to be utilization percentages or frame times. Per-frame Records are preserved in the raw evidence file rather than used to invent unsupported metrics.

## Recovery boundaries

Handled errors and Ctrl+C execute cleanup and attempt settings restoration. If a tracked process cannot be stopped, the runner still restores the bytes but marks recovery as uncertain because that process could rewrite them later. The recovery manifest stays available.

Force termination or power loss cannot run a finally block. Close the benchmark and use the recovery command. Steam itself is never killed. A delayed Steam launch can also outlive a launch timeout; inspect/close any late benchmark window before recovery or another attempt.

Process association uses executable path, launch time and an exclusive per-user runner lock, not proof of a direct parent-child relationship through every Steam launcher. Do not manually launch the same benchmark during automation. When identification is ambiguous or inaccessible, the runner refuses input/cleanup rather than acting on unrelated processes.

## Benchmark-method limits

CPU/GPU profile names describe intended workloads. No bottleneck is established solely from low/high graphics settings or the report's labels. Two passes are not a statistical performance comparison. The baseline excludes ray tracing for hardware compatibility and uses native display resolution for the GPU pass, which may not fully load every GPU. The requested GPU maximum-load requirement therefore remains empirically unverified.
