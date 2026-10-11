# Task 1 — Wukong benchmark runner

A C# / .NET 10 console application that configures and runs **Black Myth: Wukong Benchmark Tool** from Steam twice: a CPU-oriented pass followed by a GPU-oriented pass. It collects the benchmark's own FPS results and writes a local HTML report, JSON report, and raw evidence.

**Validation status:** built and tested on macOS with synthetic inputs; cross-published for Windows x64. The Windows/Steam integration, menu sequence, game configuration mapping, and real result schema have **not** been tested against the benchmark. No real FPS measurements are included. See [validation](docs/validation.md) and [integration notes](docs/integration-notes.md).

## Run on Windows

Use a Windows x64 PC that meets the benchmark's requirements. Install Steam and **Black Myth: Wukong Benchmark Tool** (App ID `3132990`). Launch it once to finish initial setup/shader preparation, then exit it. Steam should be signed in. Keep the desktop unlocked and leave the benchmark window in the foreground during automation.

Unzip the Windows package into a writable directory. The executable includes .NET; the recipient does not need a C# compiler, SDK, or separately installed runtime.

```powershell
.\WukongRunner.exe run
```

The default output is a new directory below `results/`. To choose a location:

```powershell
.\WukongRunner.exe run --output .\results\my-pc
```

The output directory must be empty. Existing results are never overwritten.

**Menu integration:** `config/automation.json` currently waits 30 seconds for the menu, then sends Enter once. This is an **unverified starter sequence**, not a confirmed unattended interface. A person with the installed benchmark must check it and adjust the supported key/click sequence if that version's menu differs. A successful keystroke never counts as benchmark completion; a fresh, stable, valid result is required. See [configuration](config/README.md).

If discovery needs explicit paths:

```powershell
.\WukongRunner.exe run --install-dir "D:\SteamLibrary\steamapps\common\Black Myth Wukong Benchmark Tool" --settings-file "D:\SteamLibrary\steamapps\common\Black Myth Wukong Benchmark Tool\b1\Saved\Config\Windows\GameUserSettings.ini" --history-dir "$env:TEMP\b1\BenchMarkHistory\Tool"
```

`--steam-dir` overrides the Steam client directory. `--config-dir` selects a different set of the three JSON configuration files. Paths are passed as arguments, not evaluated as shell commands.

## Develop and demonstrate on macOS, Linux, or Windows

Install .NET SDK **10.0.401** (or a later patch in the same SDK feature band). Run these commands from `task-1`:

```sh
dotnet build WukongRunner.sln -c Release
dotnet test --project tests/WukongRunner.Core.Tests/WukongRunner.Core.Tests.csproj -c Release
dotnet run --project src/WukongRunner.Cli -c Release -- demo
```

Tests use xUnit with Microsoft Testing Platform; the first restore downloads the pinned test dependencies from NuGet. The application itself has no external package dependencies.

The demo creates an isolated temporary benchmark substitute, writes a partial JSON result, completes it, performs both passes, and restores its settings. Its CPU/GPU descriptions and FPS values are **invented**. Every report carries a synthetic-data notice. It does not launch Steam or measure the Mac.

To exercise recovery after an interrupted second pass:

```sh
dotnet run --project src/WukongRunner.Cli -c Release -- demo --fail-pass gpu
```

This intentionally returns exit code 1 and retains the first pass's result. Temporary demo input files are removed after the report is saved.

## How the runner works

1. Validate options, resolve the Steam installation and original settings, detect conflicting processes, and collect hardware information.
2. Validate both settings transformations before changing anything; save a byte-for-byte backup and a checksum-protected recovery manifest.
3. Apply the CPU profile from the original settings, record existing result files, and launch Steam with the benchmark App ID.
4. Identify a new process by its installation-specific executable path and start time. Wait for its window; send the configured menu sequence only to the verified foreground window.
5. Wait for a new or changed, stable JSON result. Reject old content, incomplete/invalid metrics, and multiple distinct candidates. Preserve the accepted bytes and their SHA-256 hash.
6. Close the tracked benchmark processes. Apply the GPU profile from the same original settings and repeat.
7. Restore original settings, including after cancellation/failure. Save results and diagnostic information.

The runner does not use elapsed time as evidence of completion. Missing FPS values cause failure; missing reported settings cause an explicit unverified status. A settings mismatch stops the workflow. A cleanup failure prevents the second pass and leaves recovery information.

## Why these profiles

| Setting | CPU-oriented | GPU-oriented |
| --- | --- | --- |
| Output resolution | 1280 × 720 | Primary desktop resolution, resolved at runtime |
| Internal render scale | 50% | 100% |
| Graphics quality | Low | Cinematic |
| VSync / FPS cap | Off | Off |
| Frame generation | Off | Off |
| Ray tracing | Off | Off |
| Display mode | Windowed | Windowed |

The CPU profile reduces GPU rendering work. The GPU profile increases pixel and shading work while using the display's existing resolution. Frame generation is disabled so generated frames do not inflate the intended rendering comparison; an FPS cap would conceal available throughput. Ray tracing remains disabled to keep the baseline usable on graphics cards without DXR support. Windowed mode makes input coordinates relative to the application's client area and permits the low-resolution CPU pass.

These are **workload intentions**, not proven bottleneck classifications. Native resolution on a low-resolution screen may still be insufficient to saturate a fast GPU. The selected profiles do not dynamically calibrate CPU/GPU saturation. For a particular test machine, supported resolution and quality values can be adjusted in the profiles; record any such change. Two passes satisfy the requested workflow but do not establish run-to-run variance. Warm-up and repeated measurements would be separate experiments.

The editor updates standard Unreal resolution/scalability keys and known `UISettingData` quality/toggle fields. The game may override resolution/upscaling using additional UI-specific fields. That mapping remains unverified, so the report separates requested settings from any settings exposed in the result. It never treats the requested INI as proof of the effective rendering workload.

## Output and failure recovery

Each run directory contains:

- `report.html`: readable, self-contained report without remote assets or scripts.
- `report.json`: hardware, resolved profiles, metrics, settings verification, run status, and configuration.
- `cpu-raw.json` / `gpu-raw.json`: accepted original result bytes when available.
- `cpu-requested.ini` / `gpu-requested.ini`: requested configurations, not evidence of effective game settings.
- `original-settings.ini` and `recovery.json`: original configuration and its recovery manifest.
- `run.log`: timestamped progress and errors.

Only FPS average/minimum/maximum receive a defined interpretation. Additional scalar fields (including `FPS95`, when present) are retained under their original names without guessing units or percentile definitions. Raw per-frame records remain in the original JSON.

Ctrl+C requests cancellation and recovery. Force-killing the runner or losing power cannot execute cleanup. In that situation, close the benchmark and restore the backup:

```powershell
.\WukongRunner.exe recover --manifest .\results\my-pc\recovery.json
```

Recovery checks the backup hash and restores the recorded settings path. Keep recovery files local; they contain original machine-specific settings. Never run two copies or change benchmark settings manually during a run.

| Exit code | Meaning |
| --- | --- |
| 0 | Both passes completed and all configured setting comparisons matched |
| 1 | Workflow failed, including a settings mismatch or recovery problem |
| 2 | Invalid command, configuration, or preflight environment |
| 3 | Both results collected, but some effective settings could not be verified |
| 130 | Cancelled |

A synthetic demo can return 0; the report still explicitly identifies it as synthetic. A zero exit code does not establish CPU/GPU saturation.

## Build the Windows package

Cross-publishing works from the Mac SDK; it downloads Microsoft's Windows runtime pack. From `task-1`:

```sh
dotnet publish src/WukongRunner.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

Distribute `WukongRunner.exe` together with its `config/` directory and usage documentation. The generated `artifacts/WukongRunner-win-x64.zip` also includes the runtime license notices. The executable is unsigned and has not been executed on Windows; publishing it is not an integration test.

## Source layout

```text
task-1/
  WukongRunner.sln
  global.json
  Directory.Build.props
  config/                     CPU/GPU profiles and menu/timing configuration
  src/WukongRunner.Core/       Configuration, INI recovery, parsing, watching, workflow, reports
  src/WukongRunner.Cli/        Commands, synthetic demo, and Windows adapter
  tests/WukongRunner.Core.Tests/  Synthetic fixtures and xUnit regression tests
  docs/                       Integration assumptions and performed validation
  artifacts/                  Ignored build packages, demo report, and test results
```

Core has no Windows API dependencies. The Windows adapter is platform-guarded. The only PowerShell use at runtime is a fixed, read-only Windows CIM query for CPU, RAM, GPU names, and driver versions. Installed GPUs are reported as a list; that list does not identify the active renderer. Python is not required to build or run the solution.
