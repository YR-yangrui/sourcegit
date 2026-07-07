# Diagnostics Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add lightweight structured diagnostics, performance spans, and Perfetto export support.

**Architecture:** Create a dependency-free `SourceGit.Diagnostics` layer that records structured events to JSONL on a background worker, stores recent events in memory, and exports Chrome Trace Event JSON for Perfetto. Wire the layer into app startup, crash logging, Git command execution, and repository refresh paths.

**Tech Stack:** C#/.NET, Avalonia desktop app, manual `Utf8JsonWriter`, Chrome Trace Event JSON, Perfetto.

---

### Task 1: Diagnostics Core

**Files:**
- Create: `src/Diagnostics/DiagnosticEvent.cs`
- Create: `src/Diagnostics/DiagnosticScope.cs`
- Create: `src/Diagnostics/DiagnosticManager.cs`

- [x] Add a structured event model with level, category, name, timestamp, duration, thread, operation id, message, exception, and argument fields.
- [x] Add `DiagnosticScope` to emit duration spans on `Dispose`.
- [x] Add `DiagnosticManager` setup, shutdown, background JSONL writing, recent-event buffering, path hashing, and argument redaction.

### Task 2: Perfetto Export

**Files:**
- Create: `src/Diagnostics/PerfettoTraceExporter.cs`
- Modify: `src/Diagnostics/DiagnosticManager.cs`

- [x] Export recent events as Chrome Trace Event JSON.
- [x] Map spans to `X` duration events and logs to `i` instant events.
- [x] Write trace files under `Native.OS.DataDir/profiles`.

### Task 3: App And Crash Integration

**Files:**
- Modify: `src/App.axaml.cs`
- Modify: `src/Native/OS.cs`

- [x] Initialize diagnostics immediately after data-dir setup.
- [x] Log app startup, unhandled domain exceptions, unobserved task exceptions, and top-level startup failures.
- [x] Add recent diagnostic events and a Perfetto trace path to crash logs.
- [x] Flush diagnostics on normal app shutdown.

### Task 4: Git Command Instrumentation

**Files:**
- Modify: `src/Commands/Command.cs`
- Modify: `src/Commands/QueryCommits.cs`
- Modify: `src/Commands/QueryLocalChanges.cs`
- Modify: `src/Commands/Diff.cs`

- [x] Measure `ExecAsync`, `ReadToEnd`, and `ReadToEndAsync`.
- [x] Measure streaming hot-path commands that bypass the base read helpers.
- [x] Record command mode, sanitized args, repository hash, exit code, success, cancellation, and output sizes where available.

### Task 5: Repository Refresh Instrumentation

**Files:**
- Modify: `src/ViewModels/Repository.cs`

- [x] Measure each refresh task.
- [x] Record result counts for branches, remotes, tags, commits, submodules, worktrees, working-copy changes, and stashes.
- [x] Record cancellation state without changing existing refresh behavior.

### Task 6: Verification

**Files:**
- No source edits unless verification finds defects.

- [x] Run `dotnet build SourceGit.slnx --no-restore` to compare against the baseline failure.
- [x] Run any available narrower compile check if dependencies are present.
- [x] Inspect `git diff` to confirm no unrelated files changed.
