# Configuration

All three JSON files are validated at startup. Unknown properties are rejected to catch spelling mistakes. The configuration directory defaults to `config/` next to the executable, so running from a different working directory does not break configuration discovery.

## Graphics profiles

`cpu.json` and `gpu.json` specify name, rationale, width/height, render percentage, quality 1–5, and optional `uiOverrides`.

- Both dimensions zero means the primary desktop's dimensions, recorded in the resolved report.
- Explicit dimensions must be within 640–7680 by 360–4320; those bounds are input validation, not a promise that the game/GPU supports the resolution.
- `quality`: 1 low through 5 cinematic; Unreal scalability groups receive the corresponding 0–4 values.
- `uiOverrides`: changes existing named pairs in `UISettingData`. Unknown/missing pairs fail validation instead of being invented. Use this only with values verified for the installed benchmark.
- Frame generation (`InsertFrame`), VSync (`Vsync`), FPS cap (`LockFrameRate`), and ray tracing (`Rtx`) must remain zero for these baseline profiles.

The runner changes resolution/scalability and quality/toggle representations; it cannot establish that additional game-specific resolution or upscaler fields were interpreted correctly without real output. A result that omits those settings remains unverified.

## Automation

The supplied single-Enter sequence is an unverified starting assumption. It may need a one-time adjustment to the actual main menu. It does not accept license/privacy agreements or automate first-time Steam login. Initialize the benchmark before using the runner.

Each `steps` item has `kind` (`key`, `click`, or `wait`) and `delayAfterMs`. Keys supported: Enter, Space, Escape, Up, Down, Left, Right, Tab. Clicks use `x`/`y` in the range 0–1 within the benchmark's client area. For example, a click at the window's center would be:

```json
{ "kind": "click", "x": 0.5, "y": 0.5, "delayAfterMs": 1000 }
```

This is a coordinate-format example, not a claimed location of the Start button. Input is refused if focus cannot be established or the target point is covered. The implementation uses a bounded configured sequence; it does not perform screen recognition. Keep the game visible, do not interact with the mouse/keyboard during that sequence, and use an unlocked desktop.

- `launchTimeoutSeconds`: maximum wait for a new matching process/window.
- `menuReadyDelaySeconds`: delay after a window exists; **not** proof of menu readiness.
- `runTimeoutSeconds`: maximum wait for a result after the input sequence.
- `cleanupTimeoutSeconds`: bounded shutdown/recovery preparation.
- `pollIntervalMs`: how often to inspect history-file metadata.
- `resultStableMs`: how long metadata must remain unchanged before reading/parsing.
- `maxResultBytes`: per-file limit, default 64 MiB. Up to 512 history JSON files are scanned recursively; reparse points are skipped.
- `resultObjectPointer`: JSON Pointer to the object containing `FPSAvg`, `FPSMin`, and `FPSMax`; empty means the root object.
- `resultSettingsPointer`: JSON Pointer **relative to the result object** to a flat settings object; default `/Settings`. Names are matched exactly to the keys shown in the requested-settings report. Missing keys are unverified. This candidate layout is not verified against authentic benchmark output.

Polling avoids repeatedly reading growing result files. Old content hashes are rejected, including copies saved under a new filename. A stable result must have all three finite FPS values with `0 <= minimum <= average <= maximum` and a positive average. Ambiguous fresh results fail instead of silently selecting one.

Paths can be overridden with CLI options. The default candidate history directory is `%TEMP%/b1/BenchMarkHistory/Tool`; discovery does not scan unrelated application data.
