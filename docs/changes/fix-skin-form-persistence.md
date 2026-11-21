---
slug: fix-skin-form-persistence
tier: S
status: approved
date: 2026-11-21
code-files: src/WinQuick.App/Config/ConfigStore.cs, src/WinQuick.App/Radial/SettingsPanel.xaml.cs
review: quick
---

# 变更单：皮肤/形态选择重启后丢失（恢复默认）

## 根因（两条写入路径都丢字段，解析端无辜）

- 写入路径 1：`ConfigStore.RenderConfig` 模板（约 387-397 行）`appearance` 段只渲染 dimOpacity/accentColor/recentAccentColors，**缺 skin 与 form**。换肤面板的选择经 `OnSkinChanged` → `PersistSettings` → `SaveConfig` 落盘时被模板丢弃 → config.json 无此二键 → 重启读回默认（用户实测：2026-11-21 config.json 的 appearance 段确无 skin/form）。
- 写入路径 2：`SettingsPanel.BuildConfig`（约 1369-1374 行）构造 `AppearanceConfig` 缺 `Skin`/`Form` 赋值 → 从设置面板保存任何设置（如拖滑块）都会把皮肤/形态以 new 的默认 null 写回 —— 打开一次设置保存，皮肤就被静默重置。
- 解析端 `AppConfig.Parse` 用 `PropertyNameCaseInsensitive = true`，camelCase 的 skin/form 可直接映射到 `Skin`/`Form` 属性，无需改动。

## 改动

1. `ConfigStore.cs` RenderConfig 模板 appearance 段补渲染 `"skin"` 与 `"form"`（Str() 助手已处理 null）。
2. `SettingsPanel.xaml.cs` BuildConfig 的 AppearanceConfig 构造补 `Skin = _original.Appearance.Skin, Form = _original.Appearance.Form`（与同方法 Behavior 段「沿用 _original」同一惯例）。

## 已知限制（留档）

设置面板开着的同时在皮肤面板换装，BuildConfig 用的是面板打开时的快照，保存会以快照皮肤写回（罕见操作顺序；如需精确可改读 TokenStore 运行时值，本轮不做）。

## 验收标准（用户实测项）

1. 换肤 → config.json 的 appearance 段出现 `"skin": "…"` 且值正确；
2. 换肤后再改任意设置并保存 → 重启（进程/电脑）后皮肤与形态保留；
3. 皮肤设为默认（null）时 config.json 渲染 `"skin": null` 不报错。

## 验证

- `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` 0 error；
- reviewer 快审：docs/changes/fix-skin-form-persistence.review-report.json。
