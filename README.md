# ⚡ tModLoader 启动器（tModManager）

> 一个管理 **tModLoader 多版本启动**与**模组配置**的桌面启动器，为多版本玩家提供一站式管理。

![版本](https://img.shields.io/badge/版本-v2.0.0-blueviolet)
![许可证](https://img.shields.io/badge/License-MIT-green)

深色界面的桌面小工具，基于 .NET 8 构建，支持多版本一键切换、整合包管理与 Steam Workshop 模组信息展示。所有数据**仅保存在本地**，不会上传任何信息。

## 🔄 与 Python 版的关系

本仓库为 **C# 重构版**；早期版本基于 **Python（Flask + pywebview）**，现已**停更**，后续功能与维护全部在本仓库进行。

- **数据完全独立**：两个版本各自使用独立的 `data.json` / `mod_meta.json`，互不复制、互不影响
- **⚠️ 请勿同时运行两个版本**：它们共享同一个 tModLoader Mods 目录，同时运行会互相覆盖模组启用配置，导致加载状态错乱
- **C# 版（本仓库）为唯一维护版本**，请优先使用本版本

## 📥 下载与运行

1. 到 **[GitHub Releases](https://github.com/HopingStar/tMod-Manager/releases)** 下载最新版 `tModManager-release.zip`
2. 解压到任意目录，双击 `tModManager.exe` 即可运行（绿色版，免安装）
3. 首次启动自动在程序目录生成配置文件

> **运行要求**：Windows 系统，需安装 **.NET 8 桌面运行时与 ASP.NET Core 运行时**（装「.NET Desktop Runtime 8.x」+「ASP.NET Core Runtime 8.x」即可）。

## ✨ 功能特性

### 多版本管理
- 添加任意 tModLoader 版本（选择其启动脚本），支持重命名 / 删除 / 收藏置顶
- 自动检测并显示每个版本的 tML 版本号
- 直接启动当前配置 / 一键还原

### 整合包
- 为版本创建整合包，一键切换不同模组组合
- 自定义整合包名称与描述
- 启动整合包自动应用对应的模组配置

### 模组管理
- 自动合并 **本地 Mods + Steam Workshop + 整合包** 三处来源，冲突一目了然
- 搜索、标签筛选、来源徽章
- 显示模组本名、图标、版本号、热度、大小、更新时间、标签等元数据
- 悬浮提示（显示内容可配置）
- 右键模组可打开本地文件 / Workshop 页面 / 复制模组名 / 复制 ID

### 特殊加载
- 独立 / 全局 / 启动应用 三种模式
- 退出游戏后自动还原模组状态，不留残留

### 其他
- tModLoader 更新检测（有新版本时提示）
- 自动检测 tML 进程，可一键结束游戏进程
- 窗口缩放尺寸自动记忆
- 单实例运行保护

## 🚀 快速上手

1. 点击「添加版本」，选择你的 tModLoader 启动脚本（`*.bat`）
2. 为版本「添加整合包」，挑选要加载的模组组合
3. 点击整合包上的「启动」即可开始游戏；游戏退出后模组配置自动还原

## 📝 使用提示

- 版本 / 整合包 / 设置等全部保存在程序目录的 `data.json`，重装系统前记得备份
- 程序只在本机读写数据，**不会上传任何信息到服务器**

## ❓ 常见问题

**双击没有反应？**
请确认已安装 .NET 8 桌面运行时与 ASP.NET Core 运行时；或打开程序目录 `logs` 下的日志查看错误信息。

**提示「已在运行」？**
本程序同一时间只允许运行一个实例，关闭已打开的窗口即可。

**如何迁移到新电脑？**
把整个程序目录（含 `data.json`）拷贝过去，安装好运行时后即可直接使用。

## 🚀 构建与运行

需要 .NET SDK 8。NuGet 已配置华为云镜像（`NuGet.config`），无需访问 nuget.org。

```bash
# 开发运行（仓库根为 BaseDir，读仓库根 data.json / wwwroot）
dotnet run --project src/TModManager.App

# 运行测试（52 个用例：Core 单元 + 全路由集成 + 存储 / 模组定位，全部用临时目录，绝不碰真实数据）
dotnet test

# 发布（散装 zip 版；framework-dependent 不打包运行时，启动快，无解压开销）
dotnet publish src/TModManager.App -c Release -r win-x64 --self-contained false
```

把 `src/TModManager.App/bin/Release/net8.0-windows/win-x64/publish/` 整个文件夹压缩成 zip 分发即可（内容 = `tModManager.exe` + 依赖 dll + `wwwroot` + `runtimes`，必须整体一起分发）。

## 🏗️ 架构

**单进程一体**：WPF 承载窗口 + 内嵌 ASP.NET Core（Kestrel，`ListenLocalhost(0)` 随机端口）+ WebView2 渲染。

- 前端（`wwwroot/`）通过 `http://127.0.0.1:{随机端口}/` 加载，与后端通过 JSON API（`/api/*`，snake_case）解耦
- 窗口控制（拖动 / 最小化 / 最大化 / 关闭）通过 Host Objects 注入的 `window.pywebview.api.*` 桥接
- 前后端解耦 → 换皮肤 / 改 UI 不动后端；后端逻辑全部在 `TModManager.Core`（纯逻辑，可单测）

## 📁 目录结构

```
src/TModManager.Core/   纯逻辑类库（可单测）
  ├─ Parsing/    .tmod 文件解析
  ├─ Icons/      图标缓存 / ICO 解析
  ├─ Network/    Steam / GitHub / tML 更新检测
  ├─ Processes/  tML 进程检测 / 强杀
  ├─ Storage/    data.json / mod_meta.json 读写 + 首启预置
  ├─ Mods/       模组列表构建 / 特殊加载 / workshop 扫描
  └─ Versions/   tML 版本号解析
src/TModManager.App/   WPF + WebView2 + Kestrel 宿主
src/TModManager.Tests/ xUnit 测试
wwwroot/               前端（独立副本，直接服务）
```

## ℹ️ 关于

- **作者**：HopingStar
- **项目主页**：https://github.com/HopingStar/tMod-Manager
- **许可证**：MIT License（Copyright © 2026 HopingStar），详见 [`LICENSE`](LICENSE)

如果这个工具帮到了你，欢迎在 GitHub 上点个 ⭐ 支持一下～
