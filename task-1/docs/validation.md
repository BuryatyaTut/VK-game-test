# Validation record

Date: 11 October 2026. Development host: macOS 15.7, Apple Silicon (arm64). SDK: .NET 10.0.401; runtime: 10.0.12.

## Completed checks

| Check | Result |
| --- | --- |
| Release build of Core, CLI and test projects | Passed; zero warnings and zero errors |
| xUnit / Microsoft Testing Platform tests | 33 passed, 0 failed, 0 skipped |
| Project-level and solution-level test discovery | Both discovered and passed all 33 cases |
| Successful synthetic CLI demo | CPU then GPU; both complete; settings restored; exit 0 |
| Synthetic GPU failure demo | CPU result retained, GPU failed, original settings restored; exit 1 |
| Real `run` command on macOS | Refused with an explanatory message; exit 2 |
| Windows x64 self-contained publish | Succeeded; output identified as a Windows PE32+ x86-64 console executable |
| HTML report data and links | Synthetic labeling, expected pass status, existing local evidence links, no script tags |
| HTML escaping | Regression test rejects literal injected script markup in the generated report |

Tests cover UTF-8/UTF-16 BOM preservation, CRLF preservation, unrelated settings, exact restoration, backup tampering, missing/invalid FPS, duplicate result fields, unverified/mismatched settings, configurable JSON roots, partial writes, overwritten output, stale copied results, ambiguous fresh output, cancellation, failed launch/trigger/cleanup, preserving the successful first pass, and Steam VDF parsing.

Reproduce from `task-1`:

```sh
dotnet restore WukongRunner.sln --locked-mode
dotnet build WukongRunner.sln -c Release --no-restore
dotnet test --project tests/WukongRunner.Core.Tests/WukongRunner.Core.Tests.csproj -c Release --no-build --no-restore
dotnet run --project src/WukongRunner.Cli -c Release -- demo
```

The test inputs are explicitly synthetic, assembled under temporary directories. They are not recordings from Black Myth: Wukong Benchmark Tool. Passing these tests validates behavior against those inputs, not compatibility with an unknown real result/configuration format.

## Not verified

- Steam launch and process identification on Windows.
- The benchmark's actual main-menu Enter action, focus handling, timing, and input delivery.
- Real configuration paths, enum values, and effective resolution/render scale/quality.
- Real result path, schema, reported-settings field layout and completeness.
- Hardware collection through Windows CIM, active rendering GPU and real FPS.
- CPU/GPU limiting components, maximum GPU load, repeatability and warm-up effects.
- Windows execution of the published executable, installer/security UI behavior, or code signing.
- Browser-rendered visual layout: the in-app browser blocked local-file URLs. Report content and links were checked from local files without bypassing that restriction.

The Windows integration and profiles therefore remain unvalidated. The result is an implemented and locally tested runner with explicit integration assumptions, not a claim of a successful real benchmark run. No fabricated demonstration values are presented as hardware measurements.

## First target-machine verification

A person with Windows and the initialized benchmark should check the actual saved INI, menu sequence and output JSON against `config/` and the integration notes. Confirm that a manual result and an automated result match, that the reported settings reflect each profile, that existing settings are restored, and that an interrupted second pass preserves the first result. Resolve missing field mappings before treating exit code 3 as fully verified output. Check bottlenecks separately if making CPU/GPU performance claims.
