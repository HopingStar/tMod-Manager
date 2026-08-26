# ⚡ tModLoader 启动器（tModManager）

> 一个管理 **tModLoader 多版本启动**与**模组配置**的桌面启动器。
> **C# 版（本仓库）是唯一维护版本。**

![版本](https://img.shields.io/badge/版本-v2.0-blueviolet)
![.NET](https://img.shields.io/badge/.NET-8-blue)
![许可证](https://img.shields.io/badge/License-MIT-green)

基于 **.NET 8 + WPF + WebView2 + 内嵌 ASP.NET Core（Kestrel）** 构建的深色桌面程序，自绘标题栏，界面复刻早期 Python 版并完全解耦。

## 📥 下载

- 到 **[GitHub Releases](https://github.com/HopingStar/tMod-Manager/releases)** 下载最新版 `tModManager-release.zip`
- 解压后双击 `tModManager.exe` 即可运行（**绿色版，免安装**），首次启动自动在 exe 旁生成数据文件
- **运行依赖**：需安装 **.NET 8 桌面运行时 + ASP.NET Core 运行时**（「.NET Desktop Runtime 8.x」+「ASP.NET Core Runtime 8.x」）

> 官方分发为 **zip 绿色版**；仓库内的 `tModManager.iss`（Inno Setup）仅供想自行制作安装包的开发者使用，官方不发布安装包。

## ✨ 功能特性

### 版本管理
- 添加版本（指定启动脚本 `*.bat`）、重命名、删除
- 编辑名称 / tML 版本号（版本号自动检测并缓存）
- 收藏置顶排序
- **直接启动**：用当前 Mods 目录的 `enabled.json` 原样启动该版本，不做任何修改
- **还原**：恢复该版本启动前的禁用集合
- 右键版本卡片 → 打开版本文件夹（bat 所在目录）

### 整合包管理
- 为版本添加 / 移除整合包；整合包增删、重命名、自定义描述
- **启动整合包**：覆盖 `enabled.json` 并应用特殊加载后启动
- 检测源可切换：整合包文件夹 / Workshop / 本地 Mods

### 模组管理
- **三来源合并列表**：本地 Mods + Steam Workshop + 整合包文件夹（来源徽章、冲突标注）
- 搜索框 + 标签筛选
- 每版本 / 每整合包独立模组列表弹窗（对照实际加载的 `enabled.json`）
- **模组元数据**：本地逆向 `.tmod`（图标 / Info）优先，Steam Workshop 补全（本名 / 作者 / 版本 / 订阅热度 / 更新时间 / 大小 / 标签）
- 悬浮 tooltip（模组名 / 版本 / 热度 / 标签，显示开关可配置）
- 右键模组项：打开本地 `.tmod` 所在文件夹 / 打开 Steam Workshop 页面 / 复制模组名 / 复制 Workshop ID

### 特殊加载
- 独立 / 全局 / 启动应用 三种模式
- 游戏进程退出时**自动还原**禁用状态（防止残留 `.tmod.disabled` 影响下次加载）

### 启动与进程
- 启动整合包 / 启动当前 / 直接启动
- tML 进程检测（WMI 命令行匹配）+ 一键强杀
- 退出监控：游戏结束后自动还原配置

### 联网
- Steam Workshop 元数据查询（分批、本地缓存）
- tModLoader 更新检测（GitHub releases）+ 版本号对比（「有新版本」徽章）
- 离线检测降级（本地元数据兜底）

### UI / 交互
- 深色主题，自绘标题栏（拖动 / 最小化 / 最大化 / 关闭），窗口缩放尺寸自动记忆
- 自定义右键菜单（替换 Chromium 默认菜单）
- 全局设置弹窗分板块管理（更新检测 / 模组列表显示 / Tooltip / 全局特殊配置 / 关于）
- 单实例互斥，文件日志轮转

## 🚀 构建与运行

需要 .NET SDK 8。NuGet 已配置华为云镜像（`NuGet.config`），无需访问 nuget.org。

```bash
# 开发运行（仓库根为 BaseDir，读仓库根 data.json / wwwroot）
dotnet run --project src/TModManager.App

# 运行测试（52 个用例：Core 单元 + 全路由集成 + 图标混淆 + 模组定位，全部用临时目录，绝不碰真实数据）
dotnet test

# 发布（散装 zip 版；framework-dependent 不打包运行时，启动快，无解压开销）
dotnet publish src/TModManager.App -c Release -r win-x64 --self-contained false
```

把 `src/TModManager.App/bin/Release/net8.0-windows/win-x64/publish/` 整个文件夹压缩成 zip 分发即可（内容 = `tModManager.exe` + 依赖 dll + `wwwroot` + `runtimes`，必须整体一起分发）。

## 🗂️ 数据文件

- 首次启动在**程序所在目录**自动创建 `data.json`（版本 / 整合包 / 设置 / 收藏）与 `mod_meta.json`（Steam 元数据缓存），均为个人数据，不入库、不上传
- 运行日志在 `logs/tmod_manager.log`（超 1MB 轮转为 `.old`），排查问题先看这里
- 默认 Mods 目录可用环境变量 `TMOD_DEFAULT_MODS_PATH` 覆盖
- **单实例互斥**：重复启动会提示「已在运行」

## 🏗️ 架构

**单进程一体**：WPF 承载窗口 + 内嵌 ASP.NET Core（Kestrel，`ListenLocalhost(0)` 随机端口）+ WebView2 渲染。

- 前端（`wwwroot/`）通过 `http://127.0.0.1:{随机端口}/` 加载，与后端通过 JSON API（`/api/*`，snake_case）解耦
- 窗口控制（拖动 / 最小化 / 最大化 / 关闭）通过 Host Objects 注入的 `window.pywebview.api.*` 桥接
- 前后端解耦 → 换皮肤 / 改 UI 不动后端；后端逻辑全部在 `TModManager.Core`（纯逻辑，可单测）

## 📁 目录结构

```
src/TModManager.Core/   纯逻辑类库（可单测）
  ├─ Parsing/    .tmod 二进制逆向
  ├─ Icons/      图标 XOR 加密 / ICO 解析
  ├─ Network/    Steam / GitHub / tML 更新检测
  ├─ Processes/  tML 进程检测（WMI 命令行）/ 强杀
  ├─ Storage/    data.json / mod_meta.json 读写 + 首启预置
  ├─ Mods/       模组列表构建 / 特殊加载 / workshop 扫描
  └─ Versions/   tML 版本号解析
src/TModManager.App/   WPF + WebView2 + Kestrel 宿主
src/TModManager.Tests/ xUnit 测试
wwwroot/               前端（独立副本，直接服务）
```

## 🔒 图标加密说明

模组图标以「XOR 加密 + SHA256 哈希文件名」存储在 `wwwroot/static/mod_icons/`：

- 文件名为 16 位哈希，看不出对应哪个模组；内容经 XOR 加密，看图软件打不开
- 前端通过 `/api/icon/{哈希}` 解密输出原始 PNG（响应带 `no-store`，避免浏览器缓存明文）
- 哈希严格校验（16 位 hex），`/api/icon` 防路径遍历

> ⚠️ 这只是**防普通用户的混淆**，密钥写死在本地程序里——抓本地流量或反编译 exe 仍可还原，并非真正的加密安全。

## ℹ️ 关于

- **作者**：HopingStar
- **项目主页**：https://github.com/HopingStar/tMod-Manager
- **技术栈**：.NET 8 + WPF + WebView2 + ASP.NET Core（Kestrel）
- **许可证**：MIT License（Copyright © 2026 HopingStar），详见 [`LICENSE`](LICENSE)
