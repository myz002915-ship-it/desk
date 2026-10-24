# GlassDesk

Windows 11 x64 的桌面美化工具：液态玻璃 Dock、启动台、应用文件夹与统一设置中心。

当前版本 **1.5.0** · [下载安装包](https://github.com/myz002915-ship-it/desk/releases/latest) · [更新说明](docs/Release-notes-v1.5.0.md) · [参与贡献](CONTRIBUTING.md)

## 功能

- **Dock**：玻璃材质、指针放大、运行状态点、自动隐藏、拖拽重排与固定应用。
- **启动台**：网格视图、分页、搜索（中文/英文/拼音/别名）、文件夹创建与重命名。
- **外观**：深浅色主题、玻璃质感档位、Dock 尺寸与位置、桌面入口与登录启动。
- **更新**：启动时检查本仓库的 Release，校验 SHA-256 后引导安装。
- **赞助**：赞助完全自愿，全部功能免费，不赞助也可使用。

## 系统要求

- Windows 11 x64，Build 22000 及以上
- 安装包自带 .NET 10 运行时；从源码构建需要 .NET 10 SDK

## 安装、升级与卸载

到 [Releases](https://github.com/myz002915-ship-it/desk/releases/latest) 下载 `GlassDesk-<版本>-win-x64-Setup.exe`：

- 升级保留设置、应用库、文件夹、布局、自定义图标与登录启动配置
- 卸载同样保留 `Data` 目录，不会删除个人配置
- 安装包**未签名**，可用同页 `SHA256SUMS.txt` 核对下载文件
- 程序按用户会话保持单实例；运行开发版前请先从托盘退出已安装的实例

用户数据保存在程序目录旁的 `Data`，不随安装包分发。

## 从源码构建

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release
dotnet run --project src/GlassDesk.App/GlassDesk.App.csproj -c Release
```

`global.json` 指定 SDK 10.0.401（允许 latestFeature 滚动），目标 Windows SDK 为 10.0.26100.0。

运行测试：

```powershell
dotnet test tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj -c Release
dotnet test tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj -c Release
dotnet test tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj -c Release
```

或一次跑完构建与测试：

```powershell
powershell -ExecutionPolicy Bypass -File build/build.ps1
```

AppServices 测试依赖 Windows COM/外壳能力，Dock 测试会创建 WPF 窗口，二者都需要可交互的桌面会话。涉及真实鼠标键盘的验收工程默认拒绝运行，只在设置了 `GLASSDESK_ISOLATED_DESKTOP=1` 的隔离桌面（专用测试账号或虚拟机）中执行。

## 打包发布

`packaging/release.json` 是版本与工具链的唯一来源（产品版本、文件版本、包名、.NET SDK 与 Inno Setup 版本及其哈希）。改版本只改这一处，构建入口会核对它：

```powershell
powershell -ExecutionPolicy Bypass -File build/package.ps1 `
  -DotnetPath "D:\dotnet10\dotnet.exe" `
  -InnoCompilerPath "C:\Program Files\Inno Setup 7\ISCC.exe" `
  -InnoInstallerPath "C:\path\to\innosetup-7.1.0-x64.exe"
```

脚本会依次完成：自包含发布 → 许可与来源核对 → ZIP → Inno Setup 安装包 → 载荷与版本校验 → 静态验证，产物落在 `artifacts/package/<包名>-<runId>/`（含 `SHA256SUMS.json`、`payload-manifest.json`、`toolchain.json`、`build-report.json`），运行证据写入 `docs/evidence/`。

发布时把 `GlassDesk-<版本>-win-x64-Setup.exe` 与 `SHA256SUMS.txt` 上传到本仓库的 Release 即可，客户端的自动更新只认 `packaging/update-source.json` 指向的仓库、精确资产名和 SHA-256。

## 目录结构

```
src/
  GlassDesk.Core        模型、设置、存储、几何与序列化（无 UI，可单测）
  GlassDesk.Platform    Win32 互操作、显示器、安装位置、登录启动
  GlassDesk.UI          玻璃材质、主题、通用控件
  GlassDesk.AppServices 应用库、导入、启动、图标、更新
  GlassDesk.Dock        Dock 窗口、磁贴、放大、自动隐藏
  GlassDesk.Launcher    启动台窗口、分页、搜索、文件夹
  GlassDesk.Settings    设置窗口
  GlassDesk.App         唯一可执行程序，负责组装与生命周期
tests/                  单元测试与各阶段验收工程（见 docs/evidence）
build/                  构建、发布、打包、校验脚本
packaging/              发布清单、发布说明、Inno Setup 脚本、更新源配置
docs/                   发布说明、授权、第三方声明与验收证据
```

修改 `src/GlassDesk.UI/Effects` 下的着色器后，先执行 `build/compile-transition-shader.ps1` 再编译。

## 文档

- [文档索引](docs/README.md)
- [授权说明](docs/LICENSING.md) · [第三方声明](docs/第三方声明.md) · [依赖与来源记录](docs/依赖与来源记录.md)
- [源代码来源](SOURCE-PROVENANCE.json)

## 授权

本项目新增与修改的内容按 **PolyForm Noncommercial License 1.0.0** 提供：允许许可范围内的学习、修改、修复、Fork 与非商业分享；不授予售卖、商用或包装成收费软件的权利，标准许可对部分机构用途另有允许条款，以 [LICENSE](LICENSE) 英文原文为准。

历史 Apache-2.0 代码与第三方组件保留原有权利，不受本次限制追溯影响；沿用的范围见 [docs/LICENSING.md](docs/LICENSING.md)，逐文件记录见 `docs/licensing/FILE-LICENSES.json`，第三方声明见 [NOTICE](NOTICE) 与 [docs/third-party](docs/third-party)。
