; 全能本地播放器 —— Inno Setup 6 脚本
;
; 由 tools\pack.ps1 编译（别手工双击这个文件）：脚本会被复制到临时目录再编译，
; 所以这里除了同目录的 ChineseSimplified.isl，其它路径一律通过 /D 传进来。
;
; 设计要点（每条都有理由，不是随手写的）：
;   1. 每用户装到 %LocalAppData%\Programs\...：不弹 UAC、不需要管理员；
;   2. 卸载**绝不碰** %AppData%\播放器 —— 设置 / 历史 / 歌单 / 设置方案 / 字体都在那儿，
;      卸载删数据是最招人恨的一类行为，哪怕"顺便清理"也不行；
;   3. 文件关联只把本程序加进常见格式的「打开方式」候选：只写
;      HKCU\Software\Classes\Applications\播放器.exe 这一支（我们自己的键），
;      不碰 .mp4 之类的键本身、不抢默认程序（Win10/11 也不允许安装程序静默抢默认），
;      每一项都带 uninsdeletekey，卸载即还原；
;   4. AppId 固定：升级是"替换"，不会在系统里留下第二份；
;   5. libvlc 的 425 个文件必须原样落盘（插件的目录结构不能塞进单文件），
;      所以这里是目录安装，而不是 PublishSingleFile。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #error pack.ps1 必须用 /DPublishDir=... 传入发布目录
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#ifndef AppIcon
  #define AppIcon "app.ico"
#endif

#define AppName "全能本地播放器"
#define AppExeName "播放器.exe"
#define AppId "{{F00D05E2-0C99-4D9F-9F73-4085D2B5EFBD}"
#define RepoUrl "https://github.com/non266/local-player"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=non266
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=全能本地播放器-{#AppVersion}-安装程序
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany=non266
VersionInfoDescription={#AppName} {#AppVersion} 安装程序
; 程序用的是 net8.0-windows + libvlc，最低按 Win10 要求
MinVersion=10.0
; 升级时如果程序正开着，让 Inno 提示关闭（不要偷偷重启它）
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文的官方翻译文件（取自 jrsoftware/issrc 的 Files\Languages，随仓库一起放着，
; 这样打包不需要联网）。Inno 6 自带的语言列表里没有中文。
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "把「{#AppName}」加进常见音视频格式的「打开方式」候选（不改默认程序，卸载时自动撤销）"; GroupDescription: "集成"; Flags: checkedonce

[Files]
; 整个发布目录原样落盘（含 libvlc\win-x64 的插件树）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; 这些行由 pack.ps1 按 Core\MediaFormats.cs 里的扩展名生成，
; 保证"程序认得的格式"和"注册成打开方式的格式"不会各写一份、各走各的。
#ifexist "fileassoc.generated.iss"
  #include "fileassoc.generated.iss"
#else
  #error 缺少 fileassoc.generated.iss：请用 tools\pack.ps1 打包（它会按 Core\MediaFormats.cs 生成）
#endif

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 在安装目录里跑过冒烟测试的话会留下这个隔离数据目录（正常使用不会有）
Type: filesandordirs; Name: "{app}\smoke-data"
