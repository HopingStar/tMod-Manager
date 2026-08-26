# ⚡ tModLoader 启动器（tModManager）

> 一个管理 **tModLoader 多版本启动**与**模组配置**的桌面启动器，为多版本玩家提供一站式管理。

![版本](https://img.shields.io/badge/版本-v2.0.0-blueviolet)
![许可证](https://img.shields.io/badge/License-MIT-green)

深色界面的桌面小工具，基于 .NET 8 构建，支持多版本一键切换、整合包管理与 Steam Workshop 模组信息展示。所有数据**仅保存在本地**，不会上传任何信息。

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

## ℹ️ 关于

- **作者**：HopingStar
- **项目主页**：https://github.com/HopingStar/tMod-Manager
- **许可证**：MIT License（Copyright © 2026 HopingStar），详见 [`LICENSE`](LICENSE)

如果这个工具帮到了你，欢迎在 GitHub 上点个 ⭐ 支持一下～
