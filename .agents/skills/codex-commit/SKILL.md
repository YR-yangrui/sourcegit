---
name: codex-commit
description: Use when Codex is asked to create, split, stage, amend, or write messages for Git commits in a repository, especially before running git commit from local changes.
---

# Codex Commit

## Overview

Commit creation is a review boundary. Before creating or amending a commit, verify the change intent, obtain subagent review, keep commits atomic, and write messages that preserve the reasoning behind the patch.

## Hard Gate

Before running `git commit`, `git commit --amend`, or any equivalent commit-creating command:

- Do not treat staged files as approval or as the commit boundary.
- Do not use only a quick user confirmation as the review.
- Do not skip review because the change is small or the user is in a hurry.
- If subagents are unavailable, stop and tell the user that the required commit review cannot be performed.

## Workflow

1. Inspect the current state: `git status --short`, unstaged diff, staged diff, and recent commit style.
2. Identify one or more intended commits by user-facing purpose, not by staged state.
3. For each intended commit, define:
   - Goal: what the commit is meant to accomplish.
   - Rationale: why the implementation was chosen.
   - Scope: exact files or hunks that belong in the commit.
   - Validation: checks already run, checks still needed, or checks intentionally skipped.
4. Start a subagent review before committing. The reviewer must not edit files.
5. Resolve review feedback according to the feedback policy below.
6. Stage only the files or hunks for the current atomic commit.
7. Re-check the staged diff before committing.
8. Prepare the commit message and inspect the exact message file or complete message string that will be passed to `git commit -F`. If intended line breaks appear as literal `\n`, fix the message before committing.
9. Commit with a message that follows the message rules below.
10. Tell the user which review feedback was handled automatically and which checks were run.

## Subagent Review

Send the reviewer a concise prompt containing:

- The modification goal.
- Why this implementation was chosen.
- The intended commit boundary.
- A summary of the relevant staged and unstaged diffs.
- The validation already performed.
- Any known uncertainty or tradeoff.

Ask the reviewer to verify:

- Whether the current changes achieve the stated goal.
- Whether they introduce likely bugs or behavioral regressions.
- Whether they introduce performance problems.
- Whether there is a simpler or safer approach.
- Whether the proposed commit boundary is atomic.
- Whether the commit message needs a detailed Markdown body.

Use this prompt shape:

```text
Review this proposed commit before I create it.

Goal:
[one sentence]

Why this implementation:
[short rationale]

Intended commit scope:
[files/hunks and why they belong together]

Diff summary:
[relevant summary, plus commands/files the reviewer should inspect]

Validation:
[commands run and results, or not run with reason]

Please check goal fit, bugs, performance, simpler or safer alternatives, atomicity, and commit message needs. Do not edit files.
```

## Feedback Policy

- Automatically fix only simple, localized defects such as typos, obvious missed imports, broken tests from the current change, or small correctness fixes that preserve the approved design.
- After automatic fixes, re-run the relevant check and tell the user what review feedback was handled.
- If feedback requires design changes, behavior changes, scope expansion, a different architecture, or a non-trivial alternative, stop. Summarize the feedback, give a recommendation, and wait for user confirmation before editing.
- If feedback is not applicable, explain why before proceeding.
- If review finds that commit boundaries are wrong, re-split the commits and review the affected commit again.

## Atomic Commit Rules

A commit is atomic when it has one coherent purpose and can be understood or reverted on its own.

- Put unrelated features, bug fixes, formatting-only changes, dependency changes, and generated-output refreshes in separate commits unless one is required for the other to work.
- Keep implementation, tests, docs, localization, and resource updates together when they are all necessary for the same feature or fix.
- Split files by hunk when one file contains unrelated changes.
- Stop and ask the user when a mixed hunk cannot be separated safely.
- Before each commit, inspect `git diff --cached --stat` and `git diff --cached` to confirm the staged diff contains only that commit's scope.

## Commit Message Rules

- Always write a subject.
- Match the repository's existing style when clear; otherwise prefer concise conventional commit style such as `fix: ...`, `feat: ...`, `perf: ...`, or `docs: ...`.
- Always write the commit message bilingually in English and Chinese.
- Format the subject as `<type>: English subject(中文主题)`, with the English subject first and the Chinese subject inside ASCII parentheses immediately after it.
- If the commit contains any user-visible change, or more than one distinct change item, include a blank line after the subject and write an English Markdown bullet list, then a separator line containing exactly `----------------`, then a Chinese Markdown bullet list.
- The English and Chinese body sections should cover the same substantive details. Include validation when useful.
- Do not hide multiple changes behind a vague subject-only message.
- When writing body bullets, actively decide whether each bullet is user-visible:
  - User-visible changes are included in changelog by leaving the bullet unmarked. Feature changes, interaction changes, bug fixes, and large user-noticeable performance improvements usually stay unmarked.
  - Internal changes are excluded from changelog by appending `(NO CHANGELOG)` to the bullet. Refactors, internal CI mechanics, tests, dependency housekeeping, documentation/process maintenance, and small performance tuneups default to `(NO CHANGELOG)`.
  - Internal CI changes default to `(NO CHANGELOG)`, but if the release or update process change is explicitly user-visible, use judgment and leave the corresponding bullet unmarked.
- Keep English and Chinese bullets semantically paired in the same order. The Chinese bullet must express the same change as the English bullet, and the corresponding bullet in both sections must use the same `(NO CHANGELOG)` decision.
- Before creating the commit, inspect the actual message file or complete string used with `git commit -F`. The body must contain real line breaks, not a single-line message with literal `\n` where newlines were intended. Fix the file or string and re-check it before running `git commit`.

Message shape for a multi-item commit:

```text
perf: Subject in english(中文主题)

- Detail one and why it was needed.
- Detail two and how it supports the same goal.
----------------
- 细节一以及为什么需要它。
- 细节二以及它如何支持同一个目标。
```

## Red Flags

| Rationalization | Required response |
| --- | --- |
| "The user is in a hurry." | Still run subagent review before committing. |
| "The index is already staged." | Re-check logical commit boundaries. |
| "A quick confirmation is enough." | Confirmation is not review; request subagent review. |
| "The subject explains it." | Add a Markdown body when the commit has user-visible changes or multiple change items. |
| "This bullet is internal but harmless in changelog." | Mark internal bullets with `(NO CHANGELOG)`. |
| "The English and Chinese bullets are close enough." | Make paired bullets semantically equivalent and keep the same changelog marker. |
| "The message preview looked multiline." | Inspect the exact `git commit -F` file or string and fix literal `\n` before committing. |
| "The reviewer suggested a better design." | Stop and ask the user before making non-trivial design changes. |
