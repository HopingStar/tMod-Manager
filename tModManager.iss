; tModManager 安装脚本（Inno Setup 6）
; 编译：ISCC.exe tModManager.iss  → 生成 tModManager-Setup.exe
; 源文件：散装发布产物（publish/ 全部内容）

#define MyAppName "tModManager"
#define MyAppVersion "2.2.0"
#define MyAppExeName "tModManager.exe"
#define MyAppIcon "src\TModManager.App\app.ico"
#define MyPublishDir "src\TModManager.App\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{D4C3B2A1-1234-4A5B-8C6D-9E8F7A6B5C4D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=tModManager
DefaultDirName={autopf}\tModManager
DefaultGroupName=tModManager
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=tModManager-Setup
SetupIconFile={#MyAppIcon}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
; 默认「仅当前用户」免 UAC（装到 {userpf}），可选「所有用户」才弹管理员提升。
; 玩家装到自己的目录无需管理员权限，卸载同理。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; 中文向导需官方中文语言包（ChineseSimplified.isl），本机 Inno Setup 未带，暂用英文；如有中文包可加回：
; Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
