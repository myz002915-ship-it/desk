# 文档索引

## 发布

| 文档 | 内容 |
|---|---|
| [Release-notes-v1.5.0.md](Release-notes-v1.5.0.md) | 当前版本：启动速度、Dock 与启动台视觉 |
| [Release-notes-v1.4.0.md](Release-notes-v1.4.0.md) | 启动台文件夹、拖动体验、赞助入口 |
| [../packaging/RELEASE-NOTES.md](../packaging/RELEASE-NOTES.md) | 随安装包分发的那一份（与当前版本同步） |

安装包与资产在仓库的 [Releases](https://github.com/myz002915-ship-it/desk/releases) 发布；`packaging/release.json` 是版本与工具链的唯一来源。

## 授权与来源

| 文档 | 内容 |
|---|---|
| [LICENSING.md](LICENSING.md) | 非商业源码公开的范围：哪些内容沿用 Apache-2.0、哪些受 PolyForm Noncommercial 约束 |
| [licensing/FILE-LICENSES.json](licensing/FILE-LICENSES.json) | 逐文件授权记录 |
| [第三方声明.md](第三方声明.md) | 第三方组件与许可清单 |
| [依赖与来源记录.md](依赖与来源记录.md) | 依赖版本与来源追溯 |
| [../SOURCE-PROVENANCE.json](../SOURCE-PROVENANCE.json) | 上游项目与代码来源 |
| [third-party/](third-party) | 第三方许可原文（.NET 运行时、Android Liquid Glass、Inno Setup、Unicode、Windows SDK） |

## 验收证据

[evidence/](evidence) 保存每个发布候选版本的构建与验收记录，命名与内容说明见 [evidence/README.md](evidence/README.md)。这些文件是发布审计的一部分，请勿手工修改；新的运行会写入新的批次目录。
