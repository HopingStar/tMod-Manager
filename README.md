# TModManager（C# 版）

tModLoader 启动器（Python 版重构版）。**C# 版是唯一维护版本，Python 版已停更。**

- 技术栈：.NET 8 + WPF + WebView2 + 内嵌 ASP.NET Core（Kestrel）
- 界面复刻 Python 版（C# 版自带一份独立 `wwwroot/`，与 Python 版前端解耦，不再运行时生成）
- 自绘深色标题栏，通过 Host Objects 桥接 `window.pywebview.api.*`

## 与 Python 版的关系

| | Python 版（停更） | C# 版（本仓库） |
|---|---|---|
| 目录 | `tmod_manager/` | `tmod_manager_c/` |
| 数据文件 | `tmod_manager/data.json`、`mod_meta.json` | **本仓库根** `data.json`、`mod_meta.json`（独立） |
| 前端 | 仓库内 `templates/` + `static/` | 仓库内 `wwwroot/`（独立副本） |
| 后端 | Flask + pywebview | .NET 8 + WPF + Kestrel |

- 两个版本**完全独立**：C# 版首次启动从空开始（`data.json` 首次保存时创建），**不复制 Python 版任何数据或前端**。
- **⚠️ 不要同时运行两个版本。** 它们共享同一个真实 Mods 目录
  （默认 `D:\Documents\My Games\Terraria\tModLoader\Mods`，可用环境变量 `TMOD_DEFAULT_MODS_PATH` 覆盖），
  同时运行会互踩 `enabled.json` 与 `.tmod.disabled`，导致特殊加载错乱。
- C# 版有**单实例互斥**：重复启动会提示"已在运行"。

## 功能特性

一个管理 tModLoader 多版本启动与模组配置的桌面启动器。

### 版本管理
- 添加版本（指定启动脚本 `*.bat`）、重命名、删除
- 编辑名称 / tML 版本号（版本号自动检测并缓存）
- 收藏置顶排序
- **直接启动**：用当前 Mods 目录的 `enabled.json` 原样启动该版本，不做任何修改
- **还原**：恢复该版本启动前的禁用集合
- **右键版本卡片 → 打开版本文件夹**（bat 所在目录）

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
- **右键模组项**：打开本地 `.tmod` 所在文件夹 / 打开 Steam Workshop 页面 / 复制模组名 / 复制 Workshop ID

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
- 深色主题，自绘标题栏（拖动 / 最小化 / 最大化 / 关闭）
- 自定义右键菜单（替换 Chromium 默认菜单）
- 统计行 / 来源 / 标签 / 更新徽章显示开关
- 单实例互斥，文件日志轮转

### 模组图标加密
见下文「图标加密」——XOR + 哈希文件名混淆，防普通用户识别模组。

## 构建与运行

需要 .NET SDK 8（已装 8.0.404）。NuGet 已配置华为云镜像（`NuGet.config`），无需访问 nuget.org。

```bash
# 开发运行（仓库根为 BaseDir，读仓库根 data.json / wwwroot）
dotnet run --project src/TModManager.App

# 运行测试（47 个用例：Core 单元 + 全路由集成 + 图标混淆 + 模组定位，全部用临时目录，绝不碰真实数据）
dotnet test

# 发布（散装，约 4.7MB；framework-dependent 不打包运行时，启动快，无解压开销）
# 注意：`-r win-x64` 不带 `--self-contained false` 时默认会打包运行时（80~190MB）！
dotnet publish src/TModManager.App/TModManager.App.csproj \
  -c Release -r win-x64 --self-contained false

# 可选：单文件（约 2.6MB，启动多一步解压、略慢；分发为一个 exe 更方便）
# dotnet publish src/TModManager.App/TModManager.App.csproj \
#   -c Release -r win-x64 --self-contained false \
#   -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### 发布后部署

把 `src/TModManager.App/bin/Release/net8.0-windows/win-x64/publish/` 整个文件夹拷到任意位置
（或直接压缩成 zip 分发），运行 `tModManager.exe`。此时 `BaseDir = exe 所在目录`，
`data.json` / `wwwroot` 都在 exe 旁。散装版 **exe 必须和旁边的 dll / runtimes / wwwroot 一起分发**。

**运行依赖**：目标机器需装 **.NET 8 桌面运行时 + ASP.NET Core 运行时**（装 .NET SDK 或
「.NET Desktop Runtime 8.x」+「ASP.NET Core Runtime 8.x」即可）。若分发对象没装 ASP.NET Core 运行时，
退回自包含打包（去掉 `--self-contained false`，exe 约 80MB，无任何依赖）。

### 安装程序版（可选）

用 Inno Setup 6 从 `tModManager.iss` 编译安装引导程序：

```bash
ISCC.exe tModManager.iss -ORelease   # 生成 Release/tModManager-Setup.exe
```

- 安装器为**散装**分发（内容 = publish/ 全部文件，LZMA2 压缩，约 3.9MB）
- 标准向导：默认「仅当前用户」（免 UAC），可选「为所有用户安装」（需管理员）
- 开始菜单 + 桌面快捷方式 + 应用图标
- **自带卸载程序**（`unins000.exe`），也可从「程序和功能」卸载

发布产物统一放 `Release/`：`tModManager-release.zip`（直装散装版）+ `tModManager-Setup.exe`（安装程序版）。

## 架构

**单进程一体**：WPF 承载窗口 + 内嵌 ASP.NET Core（Kestrel，`ListenLocalhost(0)` 随机端口）+ WebView2 渲染。

- 前端（`wwwroot/`）通过 `http://127.0.0.1:{随机端口}/` 加载，与后端通过 JSON API（`/api/*`，snake_case）解耦
- 窗口控制（拖动 / 最小化 / 最大化 / 关闭）通过 Host Objects 注入的 `window.pywebview.api.*` 桥接
- 前后端解耦 → 换皮肤 / 改 UI 不动后端；后端逻辑全部在 `TModManager.Core`（纯逻辑，可单测）
- 单实例互斥（同名 Mutex），重复启动提示「已在运行」
- 默认 `Mods` 目录可用环境变量 `TMOD_DEFAULT_MODS_PATH` 覆盖

## 目录结构

```
src/TModManager.Core/   纯逻辑类库（可单测）
  ├─ Parsing/    .tmod 二进制逆向（≈ Python tmod_parser）
  ├─ Icons/      图标 XOR 加密 / ICO 解析（≈ Python icon_util）
  ├─ Network/    Steam / GitHub / tML 更新检测（≈ Python net_fetch，拆 5 类）
  ├─ Processes/  tML 进程检测（WMI 命令行）/ 强杀
  ├─ Storage/    data.json / mod_meta.json 读写 + 首启预置
  ├─ Mods/       模组列表构建 / 特殊加载 / workshop 扫描
  └─ Versions/   tML 版本号解析
src/TModManager.App/   WPF + WebView2 + Kestrel 宿主
src/TModManager.Tests/ xUnit 测试
wwwroot/               前端（独立副本，直接服务）
```

## 日志

运行日志在 `BaseDir/logs/tmod_manager.log`（超 1MB 轮转为 `.old`）。排查问题先看这里。

## 图标加密

模组图标以「XOR 加密 + SHA256 哈希文件名」存储在 `wwwroot/static/mod_icons/`：

- 文件名为 16 位哈希（如 `0356258358e6da18.dat`），看不出对应哪个模组
- 内容经 XOR 加密（密钥与窗口图标 `tmod_icon.dat` 区分），看图软件打不开
- 前端通过 `/api/icon/{哈希}` 解密输出原始 PNG（响应带 `no-store`，避免浏览器缓存明文图片）
- 哈希严格校验（16 位 hex），`/api/icon` 防路径遍历
- 图标在本地逆向 `.tmod` 时自动生成；前端 `wwwroot` 为 C# 版自带，不与 Python 版混用

> ⚠️ 这只是**防普通用户的混淆**，密钥写死在本地程序里——抓本地流量或反编译 exe 仍可还原，
> 并非真正的加密安全。若需更强保护（不值得），应把数据放服务端而非本地渲染。

## 已知边界

- Steam / GitHub 请求走本地 HttpClient，证书校验失败时降级不校验（本地工具只读公开数据）。
- 测试与单文件部署均使用临时/独立目录，不会写入真实 `data.json` 或改动真实 Mods 目录（启动类操作除外，需人工验证）。

## License

本仓库以 **MIT License** 开源（Copyright © 2026 HopingStar），详见 [`LICENSE`](LICENSE)。
