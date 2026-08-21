# Repository Guidelines

## Project Overview

FH6 Open Assist is a Windows-only, unpackaged WinUI 3 desktop application for Forza Horizon 6. It targets .NET 10 and Windows x64 (`net10.0-windows10.0.26100.0`, minimum Windows build 19041) and is distributed as a self-contained portable ZIP and Inno Setup installer. The UI and user-facing text are in Brazilian Portuguese.

The application exposes four BOTs: Skill Points, Farm de CR, WheelSpin Mad Mike, and Gastar Wheelspins. Automation must fail closed: an unknown or conflicting screen must stop or recover through a bounded path, never authorize a blind input.

## Structure and Ownership

- `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, and `MainWindow.xaml.cs`: WinUI startup, resources, composition root, UI state, instructions, and graceful shutdown.
- `Core/`: lifecycle, settings, paths, resource tracking, logging, macro contracts, and `AutomationCoordinator`.
- `Workflows/`: one `IMacroWorkflow` per BOT plus shared game navigation. Keep game-specific state machines here.
- `Windows/`: game-window discovery, foreground/background input, ViGEm, global hotkeys, focus/process validation, and key-release guarantees.
- `Vision/`: Windows Graphics Capture, in-process Windows OCR, deterministic classical layout detection, OCR/context fusion, ONNX position inference, and CR sample collection.
- `Assets/`: shipped UI assets, vision assets, the ONNX model/metadata, and `automation.json`.
- `tools/cr-position-model/`: offline, deterministic model training/export. `tools/vision/` contains the optional OpenCV analysis utility.
- `scripts/`, `installer/`, and `.github/workflows/`: portable/installer packaging and CI/release automation.

`MainWindow.xaml.cs` currently creates the services and workflows. If a dependency is added to `AutomationContext`, update the composition root and dispose its resources deterministically.

## Runtime Contracts

- `F8` starts an armed BOT or cancels the active run; `F9` ends and disarms it. Cancellation and every failure path must release all keyboard, mouse, and virtual-controller inputs.
- Foreground mode validates the Forza HWND and focus before input. Background mode uses WGC plus a validated ViGEm Xbox 360 controller and must not foreground the game. The game may be covered but not minimized.
- Farm de CR requires ViGEm even in foreground because its final alignment uses analog throttle.
- `GameContextDetector` combines OCR and deterministic color/layout evidence from the same checkpoint frame. Stable menus, confirmation dialogs, and controller-disconnected screens belong to classical detection; conflicts resolve to `Unknown`.
- ONNX is reserved for variable visual problems, currently the car position between the CR plates. A position is authorized only by the conservative multi-frame policy in `CrPositionClassifier`; never replace it with any-frame acceptance or lower the hard 90% floor without new evidence.
- Capture and inference occur only at decision points. Keep the ONNX session persistent on CPU with one thread, avoid continuous polling, and release WGC sessions when idle.
- Only a root WheelSpin run may start the nested SP or CR workflows. The coordinator accepts SP only with the exact target of 999 and CR only with a positive bounded target, rejects crossed targets or duration, requires the root cancellation token, and reserves one nested depth atomically. Nested workflows are sequential, and their inputs and capture session must be released before control returns to WheelSpin.
- WheelSpin must return to the garage and reread exact SP and CR after every SP/CR handoff. SP refill may resume WheelSpin only after an exact reading of 999; CR refill targets at least 10,000,000 CR or the configured reserve plus one car, whichever is greater, and must also prove that one full cycle remains affordable. Missing progress, an unmet target, or an exhausted bounded retry budget stops the parent workflow.
- WheelSpin recovery and SP-refill intent are local, versioned checkpoints written through a temporary file followed by an atomic move. Validate version, age, cycle/vehicle identity, stage, attempt budget, and exact before/after resource invariants on every load. A malformed, stale, conflicting, missing, or unwritable checkpoint must stop before the next purchase, SP spend, car switch, or removal; logs and diagnostics never substitute for a checkpoint.
- SP tracked after a visually confirmed race is an estimate derived from `PointsPerRace`, capped at 999. Only the mastery-screen OCR consensus is an exact SP reading. Estimated SP may drive progress telemetry, but it must never authorize a WheelSpin purchase, prove the 999 target, or replace the exact post-handoff reread.
- Required cars are selected automatically through `RequiredCarSelector`: the SP workflow requires the Subaru Impreza 22B-STI Version, while Farm de CR requires the Nissan S-Cargo at S1 PI 800. Confirm manufacturer/model and, where required, PI through bounded OCR/CV navigation before starting the gameplay state machine; ambiguous selection fails closed.
- SP challenge selection requires the expected result-grid/card text, yellow card body, and all four lime borders from the same checkpoint, with stable multi-frame agreement and conflict vetoes. Acceleration begins only after the `TEMPO RESTANTE`/`ATUAL` HUD is confirmed in two of three frames with the newest frame positive. Classify race success from A/Continuar plus B/Tentar Novamente and failure from A/Tentar Novamente plus B/Sair using OCR and control-color evidence from the same checkpoint; require two of three without the opposite result, never count `Unknown`, and release W in a `finally` path.

## Local Data and Privacy

Installed builds store preferences, daily logs, diagnostics, and CR samples under `%LOCALAPPDATA%\FH6 Open Assist`. Portable builds store them beside the executable because of `portable.marker`.

Never commit `ExemplosPosition/`, `diagnostics/`, logs, `bin/`, `obj/`, `publish/`, `artifacts/`, credentials, personal data, or raw gameplay captures. CR samples can contain account/UI information. The training script reads only `ExemplosPosition/Dataset/Invalid` and `Valid`; `Pending` is not ground truth. Keep every frame from one attempt in the same group using `<attempt-id>__<frame-id>` and label from confirmed outcome or explicit human review, not from the model prediction.

`AGENTS.md` is publishable repository guidance. Keep it free of runtime logs, diagnostics, account details, machine-specific paths, captured frames, and other local-only evidence.

## Build and Development Commands

Use Windows PowerShell and the .NET 10 SDK:

```powershell
dotnet restore .\FH6OpenAssist.csproj -r win-x64
dotnet build .\FH6OpenAssist.csproj -c Release --no-restore
dotnet run -c Release --project .\FH6OpenAssist.csproj --no-restore
```

Generate both distribution artifacts from one publish staging with Inno Setup 6 installed:

```powershell
.\scripts\build-release.ps1 -Version 0.0.0-local
```

Train and validate the CR position model from a curated local dataset:

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\tools\cr-position-model\requirements.txt
.\.venv\Scripts\python.exe .\tools\cr-position-model\train.py
```

The trainer must preserve grouped holdout, validate PyTorch/ONNX equivalence, benchmark CPU inference, and write both `Assets/Vision/cr-position.onnx` and `cr-position-model.json`. Do not describe resubstitution as generalization.

## Validation

There is no automated test project. Use the smallest validation proportional to the change:

- Documentation/configuration: inspect rendered Markdown/YAML, check commands/paths, and run `git diff --check`.
- C#/XAML/runtime assets: run the Release restore/build above.
- Packaging: run `build-release.ps1` and verify exactly the portable ZIP and setup EXE; `portable.marker` belongs only in the ZIP.
- Gameplay changes: report BOT, initial screen, input mode, resolution/FPS, relevant timing, and outcome logs. Verify F8/F9 and failures release inputs; background validation must confirm the game was not foregrounded.
- Vision/model changes: keep attempts grouped, audit false positives separately from false negatives, validate on independent groups, confirm success through the CR delta, and document an unequivocal persistent `EventMenu` as operational `Invalid` rather than success.

## Style, Git, and Reviews

Use four-space C# indentation, file-scoped namespaces, nullable reference types, implicit usings, `PascalCase` for public members, `_camelCase` for private fields, and `Async` suffixes with propagated `CancellationToken`. Match existing XAML formatting and use UTF-8/pt-BR for user-visible text.

Work on a focused `feat/` or `fix/` branch. Preserve unrelated user changes. Use Conventional Commit subjects and author commits as `Gabriel Ramos <gabriel14fev@gmail.com>`. PRs target `main`, explain validation and gameplay evidence, keep CI green, and never attach unreviewed screenshots or local datasets.

Report vulnerabilities privately through GitHub Security.
