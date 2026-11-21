---
slug: opensource-veloppack-repo-ci
spec: docs/specs/opensource-veloppack-updates.md
date: 2026-11-21
---

# 任务书 B：仓库开源文档 + CI 发布链路（spec 阶段二/三）

## 目标文件

LICENSE（新）、README.md（新）、.gitignore（追加）、docs/M3-status.md（一行脱敏）、.github/workflows/release.yml（新）。

## 期望行为

1. LICENSE 为 GPL-3.0 官方全文（gnu.org 下载，校验非空且头两行含「GNU GENERAL PUBLIC LICENSE」与「Version 3」）；
2. README 含功能/下载安装（SmartScreen 引导）/构建/许可证段；
3. .gitignore 追加 preview/ 与 Releases/；
4. docs/M3-status.md 的 C:\MentorGraphics 示例路径脱敏为 C:\Vendor（仅此子串）；
5. release.yml：推 v* tag 触发，windows-latest，publish → vpk download/pack/upload，仓库 github.com/FM1503/WinQuick。

## 验收标准与测试计划

| 验收标准 | 测试层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC4a LICENSE 头部正确 | 静态 | head -2 LICENSE | 含 GNU GENERAL PUBLIC LICENSE / Version 3 | 5s |
| AC4b workflow YAML 可解析 | 静态 | python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml', encoding='utf-8')); print('yaml ok')"（环境无 pyyaml 则注明跳过） | yaml ok | 10s |
| AC4c vpk 本地打包演练（不 upload） | 集成 | 见执行书第 3 步 | Releases\WinQuick-Setup.exe 与 releases.win-x64.json 生成 | 5min |
