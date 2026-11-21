---
slug: opensource-veloppack-updates-client
spec: docs/specs/opensource-veloppack-updates.md
date: 2026-11-21
---

# 任务书 A：客户端 Velopack 集成（spec 阶段一）

## 目标文件

WinQuick.App.csproj、Program.cs（新）、Updates/UpdateChecker.cs（新）、App.xaml 不改文件本体（csproj 改编译方式）、AppConfig.cs、ConfigStore.cs、SingleInstance.cs、ResidentHost.cs、OverlayController.cs、TrayHost.cs、SettingsPanel.xaml、SettingsPanel.xaml.cs、tests/WinQuick.Core.Tests/UpdateConfigTests.cs（新）。

## 期望行为

1. 入口：Program.Main 首行 VelopackApp.Build().Run()，再手动启动 WPF；App.xaml 按 Page 编译；OnStartup 逻辑不变；
2. UpdateChecker：GitHub 源 github.com/FM1503/WinQuick、仅正式版；Check/Download/ApplyAndRestart 全静默降级；开发目录运行 IsSupported=false；
3. 配置：新增 updates.autoCheck（默认 true），渲染模板含该段，旧配置缺段兼容；
4. 启动静默检查：延迟 15s、可被 updates.autoCheck 关闭、下载完成托盘气泡（点击直达「关于」分区）；绝不自动重启；
5. 设置面板新增「关于」分区（侧栏末项）：版本号、状态行、检查更新、立即更新并重启（仅就绪后可见）、自动检查开关；
6. 自启路径自愈：Run 项路径≠当前 exe 时启动改写。

## 验收标准与测试计划

| 验收标准 | 测试层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 已成功生成 0 错误 | 60s |
| AC2 UpdateConfig 三用例全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter UpdateConfig | Passed! 3 用例 | 60s |
| AC3 diff 范围仅任务书文件 | 静态 | git diff --stat | 仅上述 13 文件 + dotnet add package 产生的 csproj 行 | 10s |

## 许可（机械修正，仅限此两种情况，其余任何错误一律停报）

- 编译报 ResidentHost.cs / TrayHost.cs 缺 `using System;` 或 `using System.Threading.Tasks;` → 允许在该文件头部 using 区补对应一行；
- 除此外任何编译错误（含 Velopack API 签名不匹配）→ 停止并完整贴出错误行，不得猜测修改。
