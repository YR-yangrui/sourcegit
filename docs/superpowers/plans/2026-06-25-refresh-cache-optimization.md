# Refresh Cache Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce repeated F5 refresh cost by sharing a lightweight ref snapshot and caching Branch, Tag, History, and ahead/behind results when their SHA-based inputs are unchanged.

**Architecture:** Add command-layer helpers for ref snapshots and batch ref detail queries. Keep repository-view caches in `ViewModels.Repository` so UI state is invalidated naturally when a tab closes, and keep `QueryTrackStatus` as a process-wide SHA-pair cache because its result depends only on two commit IDs.

**Tech Stack:** C# / .NET 10, existing `SourceGit.Commands.Command` Git runner, existing Repository refresh tasks, existing diagnostics.

---

### Task 1: Command Helpers

**Files:**
- Create: `src/Commands/QueryRefSnapshot.cs`
- Modify: `src/Commands/QueryBranches.cs`
- Modify: `src/Commands/QueryTags.cs`
- Modify: `src/Commands/QueryTrackStatus.cs`

- [x] Add `QueryRefSnapshot` that runs one `for-each-ref` over `refs/heads`, `refs/remotes`, and `refs/tags`, parses `refname`, `objectname`, `HEAD`, `upstream`, and `worktreepath`, and exposes branch/tag rows plus a stable refs fingerprint.
- [x] Add batch branch detail parsing in `QueryBranches` using `for-each-ref` without `%(upstream:trackshort)`.
- [x] Add batch tag detail parsing in `QueryTags` using `for-each-ref` for explicit tag refs.
- [x] Add SHA-pair cache to `QueryTrackStatus`, with diagnostic hit/miss fields.

### Task 2: Repository Caches

**Files:**
- Modify: `src/ViewModels/Repository.cs`

- [x] Add repository-view cache fields for branch keys, tag keys, and the single-slot history cache.
- [x] Add shared ref snapshot task creation so Branch, Tag, and History refreshes can reuse the same snapshot in a refresh batch.
- [x] Rebuild only branch/tag cache misses, using one batch Git query per type, and reuse unchanged model instances.
- [x] Use SHA-pair track status cache to fill ahead/behind after branch reuse or rebuild.
- [x] Use `historyArgs + headSha + refsFingerprint` as the single-slot history key.

### Task 3: Verification

**Files:**
- No production files unless verification exposes a bug.

- [x] Run `dotnet build SourceGit.slnx --no-restore -p:OutDir="$env:TEMP\sourcegit-codex-build\"`.
- [x] Run `git diff --check`.
- [x] Inspect diagnostics after build/code review for cache hit/miss field coverage.
