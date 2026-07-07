---
name: sourcegit-design-docs
description: Use when writing or updating SourceGit design documents, specs, implementation plans, docs/superpowers specs, architecture notes, or release/update design docs in this repository.
---

# SourceGit Design Docs

## 核心规则

SourceGit 仓库内的设计文档、规格说明和实现计划默认使用中文编写。

## 适用范围

该规则适用于：

- `docs/superpowers/specs/*.md`
- `docs/superpowers/plans/*.md`
- 架构设计、发布设计、更新机制设计、实现方案和需求澄清文档
- 用户明确要求“写设计文档”“写设计方案”“写 spec”“写 plan”的任务

## 写作要求

- 标题、段落、说明性列表和结论使用中文。
- 保留代码标识符、命令、文件路径、配置项、API 字段名、版本号格式、正则表达式和协议名称的原始英文。
- 示例中如果业务规则本身要求双语内容，可以保留英文示例，并补充中文解释。
- 文件名可以沿用仓库既有的日期加英文 kebab-case 命名方式，正文必须中文。
- 完成前快速扫描文档，确认没有大段英文说明性文字遗留；只允许技术字面量、引用内容和必要示例保留英文。

## 常见边界

- changelog 或 commit message 规范可以展示英文和中文双语示例，但解释规范的文字仍使用中文。
- 外部 API 名称、GitLab 字段、JSON key、C# 类型名和 shell 命令不要翻译。
- 如果上游模板要求英文小标题，优先改成中文；只有机器解析依赖该文本时才保留英文。
