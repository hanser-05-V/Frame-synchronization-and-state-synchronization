# Repository Guidelines

## Project Structure & Module Organization

This repository contains a Unity 2022.3.62f2 project under `Project/Frame Synchronization/`. Runtime C# code is in `Assets/Scripts/` and uses the `FrameSyncDemo` namespace. Keep deterministic frame logic, fixed-point math, snapshots, prediction, and buffering in `Assets/Scripts/FrameSync/`; socket transport and connection settings belong in `Assets/Scripts/Network/`; Unity editor extensions belong in `Assets/Scripts/Editor/`. Top-level controllers and debug UI remain directly under `Assets/Scripts/`. Materials and imported art live under `Assets/Material/` and `Assets/New Folder/`.

`Packages/` and `ProjectSettings/` are versioned Unity configuration. Do not commit generated `Library/`, `Temp/`, `Logs/`, `obj/`, `.idea/`, solution, or project files. `Assets/Scenes/` is currently ignored; coordinate any scene changes with maintainers before changing that rule.

## Build, Test, and Development Commands

- Open the folder through Unity Hub with Unity `2022.3.62f2`, load `Assets/Scenes/SampleScene.unity`, and press Play for normal development.
- `& "<UnityEditorPath>\Unity.exe" -projectPath "$PWD" -batchmode -quit -logFile -` imports assets and verifies that scripts compile in CI or a terminal.
- `& "<UnityEditorPath>\Unity.exe" -projectPath "$PWD" -batchmode -runTests -testPlatform EditMode -testResults TestResults.xml -quit` runs EditMode tests once test assemblies exist.

Always inspect the Unity Console after compilation and exercise both local and network modes when changing frame timing, prediction, or rollback.

## Coding Style & Naming Conventions

Use four-space indentation, Allman braces, and one public type per file. Use `PascalCase` for types, methods, properties, and events; use `_camelCase` for private fields; keep serialized fields private with `[SerializeField]`. Preserve deterministic simulation: prefer `FixedInt` and integer frame IDs inside synchronized state, and isolate floating-point or wall-clock values to presentation and scheduling code. Keep comments concise and update stale comments when behavior changes.

## Testing Guidelines

No repository test suite exists yet. Add NUnit Unity tests under `Assets/Tests/EditMode/` or `Assets/Tests/PlayMode/`, with matching `.asmdef` files. Name fixtures `<TypeName>Tests` and methods `Method_Scenario_ExpectedResult`. Prioritize fixed-point arithmetic, buffer boundaries, snapshot restore, frame alignment, prediction mismatch, and rollback replay. Include a regression test with every bug fix where practical.

## Commit & Pull Request Guidelines

Follow the existing `<type>: <summary>` style, such as `feat:`, `fix:`, `docs:`, `notes:`, or `chore:`. Keep commits focused and explain frame-sync consequences in the body. Pull requests should summarize behavior, list verification performed, link the relevant issue or task, and attach Console logs or screenshots for visible/debug-panel changes. Never force-push; repository pushes are performed manually by the maintainer.
