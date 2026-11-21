# 执行书：opensource-veloppack-final-polish（reviewer P3 修复 + README 补链）

## R1 README.md 补开发文档链接
old:
```
## 许可证
```
new:
```
## 开发过程文档

设计决策、实测数据与各里程碑结论见 [docs/](docs/)（分层窗口与 z 序、内存实测、换肤规格、粒子调研等）。

## 许可证
```
（该锚点在 README 中唯一；不唯一则以文件末尾那处为准——先 Read 全文核对。）

## R2 ResidentHost.cs 注释脱位修复（reviewer P3）
背景：任务书 A 的 P7e 把 CheckForUpdatesQuietly 插在了 OnSettingsChanged 的 doc 注释与其方法声明之间，导致那段注释与方法分离。
操作：先 Read src/WinQuick.App/Hosting/ResidentHost.cs 的 720-760 行；把「CheckForUpdatesQuietly 的 doc 注释 + 方法体」整块剪出，移动到「OnSettingsChanged 的 doc 注释块」之前。代码语义零变化（纯块位置调整），移动后 OnSettingsChanged 的 doc 注释必须紧贴其方法声明。

## 验证
1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 0 error；
2. `dotnet test tests/WinQuick.Core.Tests --nologo` → 全绿（312 用例，基线不回退）；
3. Read 调整后的 ResidentHost.cs 相关行段，确认 OnSettingsChanged 注释与方法已相邻。
