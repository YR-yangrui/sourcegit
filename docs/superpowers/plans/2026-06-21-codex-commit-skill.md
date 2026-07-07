# Codex Commit Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a repository-scoped Codex skill that enforces reviewed, atomic, well-described commits.

**Architecture:** Use a single instruction-only skill under `.agents/skills/codex-commit`. Keep UI metadata in `agents/openai.yaml`; keep design and execution notes under `docs/superpowers`.

**Tech Stack:** Codex agent skills, Markdown, YAML, Git.

---

### Task 1: Create The Repo Skill

**Files:**
- Create: `.agents/skills/codex-commit/SKILL.md`
- Create: `.agents/skills/codex-commit/agents/openai.yaml`

- [ ] **Step 1: Scaffold the skill**

Run:

```powershell
python 'C:\Users\Admin\.codex\skills\.system\skill-creator\scripts\init_skill.py' codex-commit --path 'G:\work\sourcegit\.agents\skills'
```

Expected: the command creates `.agents/skills/codex-commit/SKILL.md` and `.agents/skills/codex-commit/agents/openai.yaml`.

- [ ] **Step 2: Replace the generated SKILL.md template**

Write a concise workflow skill with:

- frontmatter `name: codex-commit`
- a trigger-focused `description`
- a pre-commit subagent review gate
- review prompt requirements
- feedback handling rules
- atomic commit grouping rules
- Markdown commit body rules for multi-item commits
- final checks before running `git commit`

- [ ] **Step 3: Fix UI metadata**

Set `agents/openai.yaml` to:

```yaml
interface:
  display_name: "Codex Commit"
  short_description: "Review and compose atomic commits"
  default_prompt: "Use $codex-commit when preparing repository changes for commit."
```

### Task 2: Validate The Skill

**Files:**
- Verify: `.agents/skills/codex-commit/SKILL.md`
- Verify: `.agents/skills/codex-commit/agents/openai.yaml`

- [ ] **Step 1: Run structural validation**

Run:

```powershell
python 'C:\Users\Admin\.codex\skills\.system\skill-creator\scripts\quick_validate.py' 'G:\work\sourcegit\.agents\skills\codex-commit'
```

Expected: validation succeeds with no frontmatter or naming errors.

- [ ] **Step 2: Run a pressure-scenario forward test**

Ask a subagent to use `$codex-commit` at `.agents/skills/codex-commit` while preparing commits from staged and unstaged changes across two unrelated features.

Expected: the subagent requires review before committing, splits unrelated work into separate commits, and uses a Markdown body when a commit contains multiple change items.

- [ ] **Step 3: Inspect the final diff**

Run:

```powershell
git diff -- .agents docs/superpowers
```

Expected: the diff only contains the new repo skill and supporting design or plan documents.
