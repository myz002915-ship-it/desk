# 验收证据目录

每次执行 `build/package.ps1`（以及带验收开关的 `build/build.ps1`）都会新建一个批次目录，记录这次构建与验收的原始输出。这些文件是发布审计材料：**只追加，不改写**。

```
阶段7/
  batch-d-package-<runId>/
    dotnet-version.log         选中的 SDK 版本（需与 packaging/release.json 一致）
    dotnet-runtimes.log        该 SDK 携带的运行时清单（含 10.0.12）
    publish.log                自包含发布与打包内自检的输出
    inno-compile.log           Inno Setup 编译输出
    verify-package.log         静态验证器对产物、载荷与许可的核对结果
    package-run-report.json    本次运行的汇总：状态、提交、工具链、产物哈希、计数
    acceptance-<id>/           打包内应用自检（`--selftest`）的原始文本
```

- `<runId>` 是脚本生成的 GUID，和 `artifacts/package/GlassDesk-<版本>-win-x64-<runId>/` 一一对应。
- 失败的运行同样保留，`package-run-report.json` 里 `status` 为 `Failed`，`error` 写明原因——失败记录比通过记录更值得留档。
- 目录中可能出现构建机的绝对路径、SDK 与运行时版本号；不含用户数据、应用库或截图。
- 历史批次对应各自的产品版本，未随版本升级重写。
