# Codex Commit Skill 设计

## 目标

新增一个仓库级 Codex skill，用来约束 Codex 在本仓库中准备和创建 commit 的流程。

## 背景

Codex 会从当前工作目录到仓库根目录之间的 `.agents/skills` 目录发现仓库级 skill。本仓库此前没有定义仓库级 skill，因此新 skill 放在 `.agents/skills/codex-commit/SKILL.md`。

## 需求

- 当 Codex 需要创建 commit 时，必须在提交前请求 subagent review。
- review 请求必须说明改动目标、为什么选择当前实现方式，以及预期提交范围。
- subagent 必须检查改动是否达成目标、是否引入 bug、是否有性能风险，以及是否存在更简单或更安全的方案。
- 如果 review 反馈需要非平凡的设计修改，必须先告知用户并等待确认。
- review 发现的简单 bug 可以自动修复，但 Codex 必须在之后告知用户处理了哪些反馈。
- commit 应保持原子性：无关功能不能放在同一个 commit；同一功能所需的相关改动可以放在一起。
- commit message 始终需要 subject。如果一个 commit 包含任何用户可感知改动，或包含多个改动项，body 必须使用 Markdown bullet 描述这些改动。
- commit body 的英文和中文 bullet 必须语义对应，并按每条 bullet 主动判断是否应进入 changelog。
- 面向用户的功能、交互、bug 修复和大性能改动默认不加标记；重构、内部 CI、测试、依赖整理、文档流程和小性能调优默认追加 `(NO CHANGELOG)`。
- 执行 `git commit` 前必须检查实际传给 `git commit -F` 的 message 文件或字符串，确认需要换行的位置不是字面量 `\n`。

## 设计

创建一个仅包含指令的 `codex-commit` skill。该流程依赖判断、review 处理和提交边界划分，而不是确定性的文件转换，因此不需要脚本。

skill 包含：

- 面向 commit 创建、commit message 编写、stage 决策和 commit 拆分的触发描述。
- 一个硬性的提交前 review gate，要求必须先进行 subagent review。
- review prompt checklist，覆盖目标、理由、diff 范围、验证结果和风险区域。
- 反馈处理策略，区分可自动修复的问题和需要用户确认的设计变更。
- 基于用户可感知目的和可回滚性的原子 commit 分组规则。
- 多改动项 commit 的 Markdown body、双语对应和 changelog 标记规则。
- 执行 `git commit` 前的最终检查。

## 验证

对 `.agents/skills/codex-commit` 运行 skill creator validator。再使用一个压力场景 forward-test：要求 agent 从多个无关改动中快速创建 commit，确认它仍会先 review 并拆分提交边界。

## 不在范围内

- 不增加 Git hooks，因为这里要求的是 agent 工作流，不是可机械强制的命令规则。
- 不修改项目配置，因为仓库级 skill discovery 已足够。
- 不增加自定义 subagent TOML，因为该流程可以直接使用已有 subagent 能力。
