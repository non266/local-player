# 打包：自包含发布 → 便携 zip → Inno Setup 安装程序
#
# 用法（在仓库根目录或任何地方都行）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack.ps1
#   powershell ... -File tools\pack.ps1 -SkipZip        只出安装程序
#   powershell ... -File tools\pack.ps1 -SkipPublish    复用上次的发布目录（调试 .iss 时快）
#
# 产物落在 dist\（已 gitignore）：
#   全能本地播放器-<版本>-安装程序.exe   每用户安装、免 UAC、带卸载器
#   全能本地播放器-<版本>-便携版.zip     解压即用，不写注册表
#
# 前置：
#   - .NET SDK（和平时构建用的是同一个）
#   - Inno Setup 6 的 ISCC.exe（默认装在 C:\Program Files (x86)\Inno Setup 6\）
#   - 7-Zip（便携 zip 用；没有就加 -SkipZip）
#
# 版本号只有 csproj 一个来源：这里读 <Version> 再通过 /D 传给 .iss，不在安装脚本里写死。

[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipSetup,
    [switch]$KeepTemp,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

# ---- 0. 定位与前置检查 -----------------------------------------------------

$scriptDir = $PSScriptRoot
$root = Split-Path -Parent $scriptDir
$csproj = Join-Path $root '播放器.csproj'
$icon = Join-Path $root 'app.ico'
$issSource = Join-Path $scriptDir 'installer\播放器.iss'
$islSource = Join-Path $scriptDir 'installer\ChineseSimplified.isl'

foreach ($need in @($csproj, $icon, $issSource, $islSource)) {
    if (-not (Test-Path -LiteralPath $need)) { throw "缺文件：$need" }
}

if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }

$version = ([regex]'(?m)<Version>\s*([^<\s]+)\s*</Version>').Match((Get-Content -LiteralPath $csproj -Raw -Encoding UTF8)).Groups[1].Value
if (-not $version) { throw "没能从 $csproj 里读出 <Version> —— 版本号是这里唯一的来源，读不到就不该继续" }

Write-Host "=== 打包 全能本地播放器 $version ===" -ForegroundColor Cyan
Write-Host "    仓库    : $root"
Write-Host "    产物目录: $OutputDir"

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $SkipSetup -and -not $iscc) {
    throw ("找不到 ISCC.exe（Inno Setup 6）。装法：" + [Environment]::NewLine +
           "  1) 从官方页 https://jrsoftware.org/isdl.php 下 innosetup-6.7.3.exe（官方直链，" + [Environment]::NewLine +
           "     GitHub Releases 上，签名者应为 Pyrsys B.V.）" + [Environment]::NewLine +
           "  2) 静默安装：innosetup-6.7.3.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" + [Environment]::NewLine +
           "  注意：Inno Setup 商用需要购买许可（个人/非商业免费），详见其下载页。")
}

$sevenZip = @(
    (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
    (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $SkipZip -and -not $sevenZip) {
    Write-Host "    没有 7-Zip：跳过便携 zip（要出 zip 就装个 7-Zip，或者去掉 -SkipZip）" -ForegroundColor Yellow
    $SkipZip = $true
}

# ---- 1. 文件关联清单：从 MediaFormats.cs 提取 ---------------------------------
# 注册成「打开方式」的格式必须和程序认得的格式是同一份清单。手抄一份到 .iss 里
# 迟早走偏（改了源码忘了改安装包，用户右键里就少了那种格式），所以这里直接读源码。

$mediaFormatsPath = Join-Path $root 'Core\MediaFormats.cs'
# ⚠ 必须显式 -Encoding UTF8：Windows PowerShell 5.1 的 Get-Content 默认按 ANSI(GBK) 解，
#   这个文件是 UTF-8，按 GBK 解会出现"中文注释的字节吃掉后面一个 ASCII 字符"这种隐患，
#   而我们要抽的正是 ASCII 的扩展名清单——静默错一两个扩展名最难查。
$mediaFormats = Get-Content -LiteralPath $mediaFormatsPath -Raw -Encoding UTF8

function Get-FormatExtensions {
    param([string]$arrayName)

    $match = [regex]::Match($mediaFormats, "(?s)$arrayName\s*=\s*\{(.*?)\};")
    if (-not $match.Success) {
        throw "在 Core\MediaFormats.cs 里找不到 $arrayName —— 源码写法变了，打包脚本里的提取规则要跟着改"
    }

    return [regex]::Matches($match.Groups[1].Value, '"(\.[^"]+)"') | ForEach-Object { $_.Groups[1].Value }
}

$extensions = @(
    (Get-FormatExtensions 'VideoExtensions') +
    (Get-FormatExtensions 'AudioExtensions') +
    (Get-FormatExtensions 'PlaylistExtensions')
) | Sort-Object -Unique

# 字幕那一组故意不注册：把 .txt / .lrc 也塞进"打开方式"只会添乱
if ($extensions.Count -lt 40) {
    throw "从 MediaFormats.cs 只提取到 $($extensions.Count) 个扩展名，明显不对（正常是 50 个上下）"
}

Write-Host "    文件关联: $($extensions.Count) 种格式（视频 + 音频 + 播放列表，来自 Core\MediaFormats.cs）"

# ---- 2. 备好编译目录（.iss 及其依赖会被复制过去） ----------------------------

$work = Join-Path $env:TEMP "播放器-pack-$version"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work | Out-Null

Copy-Item -LiteralPath $issSource -Destination $work
Copy-Item -LiteralPath $islSource -Destination $work

# 「打开方式」候选：只写 HKCU\Software\Classes\Applications\播放器.exe 这一支自己的键，
# 不碰 .mp4 之类的键本身。SupportedTypes 是 Windows 用来把程序列进"打开方式"的清单。
$association = New-Object System.Collections.Generic.List[string]
$association.Add('; 本文件由 tools\pack.ps1 生成（扩展名取自 Core\MediaFormats.cs），不要手改')
$association.Add('Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: fileassoc; Flags: uninsdeletekey')
$association.Add('Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: fileassoc; Flags: uninsdeletekey')
$association.Add('Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: fileassoc; Flags: uninsdeletekey')

foreach ($ext in $extensions) {
    $association.Add('Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: "' + $ext + '"; ValueData: ""; Tasks: fileassoc; Flags: uninsdeletekey')
}

Set-Content -LiteralPath (Join-Path $work 'fileassoc.generated.iss') -Value $association -Encoding utf8

# ---- 3. 自包含发布 -----------------------------------------------------------

$publish = Join-Path $work 'publish'

if (-not $SkipPublish) {
    Write-Host "=== dotnet publish（自包含 win-x64，首次会联网拉运行时包）===" -ForegroundColor Cyan

    # 说明两件实测过的事，免得下次有人重新怀疑一遍：
    #   1) 这一步会重写 obj\project.assets.json，写进去的图里多出 net8.0-windows10.0.19041.0/win-x64
    #      这个目标——**不影响**平时的 `dotnet build --no-restore`（实测：构建照常走完，
    #      两条目标的 assets 是合法的）。所以这里不做备份/还原那一套。
    #   2) publish 的输出目录在临时目录里，不碰 bin\Release，所以**程序正开着也能打包**。

    # 原生程序往 stderr 写进度，在 $ErrorActionPreference='Stop' 下会被当成致命错误，
    # 所以这一段显式放宽，改看退出码。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publish `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) {
        throw "dotnet publish 失败（退出码 $code）"
    }
}
elseif (-not (Test-Path -LiteralPath $publish)) {
    throw "-SkipPublish 要求上次的发布目录还在：$publish 不存在"
}

$payloadMb = [math]::Round((Get-ChildItem -LiteralPath $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "    发布目录: $publish（$payloadMb MB）"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ---- 4. 便携 zip -------------------------------------------------------------

if (-not $SkipZip) {
    Write-Host "=== 便携 zip（7-Zip，解压即用、不写注册表）===" -ForegroundColor Cyan

    $zip = Join-Path $OutputDir "全能本地播放器-$version-便携版.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        Push-Location $publish
        # 在发布目录里打包：zip 里就是"解压到一个文件夹即可运行"的结构，没有多余的外层目录
        & $sevenZip a -tzip -mx=9 -bso0 -bsp0 $zip '.\*'
        $code = $LASTEXITCODE
        Pop-Location
    }
    finally {
        $ErrorActionPreference = $previous
    }

    # 7-Zip：0 = 正常，1 = 有警告但压完了
    if ($code -gt 1) { throw "7-Zip 打包失败（退出码 $code）" }
    if ($code -eq 1) { Write-Host "    （7-Zip 报了警告，但 zip 已生成）" -ForegroundColor Yellow }
}

# ---- 5. 编译安装程序 ---------------------------------------------------------

if (-not $SkipSetup) {
    Write-Host "=== Inno Setup 编译安装程序 ===" -ForegroundColor Cyan

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$OutputDir" "/DAppIcon=$icon" (Join-Path $work '播放器.iss')
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) {
        Write-Host "ISCC 失败（退出码 $code）：编译目录留在 $work，可以进去手工重跑看详细报错" -ForegroundColor Red
        throw "Inno Setup 编译失败"
    }
}

# ---- 6. 收尾与摘要 -----------------------------------------------------------

if (-not $KeepTemp) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "=== 产物（$OutputDir）===" -ForegroundColor Green

Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Name -like "*$version*" } | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    Write-Host ("  {0}" -f $_.Name)
    Write-Host ("      {0} MB   SHA256 {1}" -f [math]::Round($_.Length / 1MB, 2), $hash)
}

Write-Host ""
Write-Host "安装程序会装到 %LocalAppData%\Programs\全能本地播放器（免 UAC）；" -ForegroundColor Gray
Write-Host "卸载不会删除 %AppData%\播放器 里的设置 / 历史 / 歌单 / 设置方案 / 字体。" -ForegroundColor Gray
