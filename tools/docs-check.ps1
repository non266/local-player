<#
  docs-check.ps1 —— 核对"文档里说的现状"与源码里的事实。

  为什么要有它：README / docs/介绍.md 里写着 SDK 版本、目标框架、内核版本、格式数量这些
  「当前状态」。它们一定会漂——1.6.2 之前 README 还写着 .NET 8、装机 671 个文件、
  以及 libvlc 3.0.23 时代的旧结论。人记得住一次，记不住十次。

  它做两件事：
    1. 从源码现读事实（global.json / 播放器.csproj / Core\MediaFormats.cs），打印成一张表。
       发版时照着改文档，不用手抄、不用手算。
    2. 对「当前状态」文档做几条硬检查（README.md 与 docs\介绍.md）：
       该出现的必须出现，不该出现的必须没有。任一条不成立 → 退出码 1。

  它只读，不改任何文件。数行数一律用 [IO.File]::ReadAllLines：
  本机实测 Get-Content 会把无 BOM 的 UTF-8 按 GBK 读（README 2,506 行读成 1,345，中文全乱）。

  用法：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\docs-check.ps1

  退出码：0 = 全部一致；1 = 有漂移（每条一行 ✗ 文件:行 …）。

  变异验证（证明它抓得住）见 docs/维护手册.md 第 2 节与 CHANGELOG 的对应版本节。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Read-Text([string]$relative)
{
    $path = Join-Path $root $relative

    if (-not (Test-Path -LiteralPath $path))
    {
        throw ("找不到 " + $relative)
    }

    return [IO.File]::ReadAllText($path)
}

function Read-Lines([string]$relative)
{
    $path = Join-Path $root $relative

    if (-not (Test-Path -LiteralPath $path))
    {
        throw ("找不到 " + $relative)
    }

    return [IO.File]::ReadAllLines($path)
}

# 用 .Contains() 而不是 -like：路径里有 [ ] ? 时通配符会抛 WildcardPatternException。
function Find-Line([string[]]$lines, [string]$needle)
{
    for ($i = 0; $i -lt $lines.Count; $i++)
    {
        if ($lines[$i].Contains($needle)) { return ($i + 1) }
    }

    return 0
}

function Assert-Contains([string]$relative, [string[]]$lines, [string]$needle, [string]$why)
{
    $line = Find-Line $lines $needle

    if ($line -eq 0)
    {
        $message = $relative + " 里找不到「" + $needle + "」：" + $why
        $failures.Add($message)
        Write-Host ("  ✗ " + $message) -ForegroundColor Red
    }
    else
    {
        Write-Host ("  ✓ " + $relative + ":" + $line + " 有「" + $needle + "」") -ForegroundColor Green
    }
}

function Assert-Absent([string]$relative, [string[]]$lines, [string]$needle, [string]$why)
{
    $line = Find-Line $lines $needle

    if ($line -ne 0)
    {
        $message = $relative + ":" + $line + " 还写着「" + $needle + "」：" + $why
        $failures.Add($message)
        Write-Host ("  ✗ " + $message) -ForegroundColor Red
    }
    else
    {
        Write-Host ("  ✓ " + $relative + " 没有「" + $needle + "」") -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 事实

Write-Host ""
Write-Host "=== 从源码现读的事实（改文档照这个抄） ===" -ForegroundColor Cyan

$sdkJson = Read-Text "global.json" | ConvertFrom-Json
$sdk = $sdkJson.sdk.version
$sdkMajor = ($sdk -split '\.')[0]

$csproj = Read-Text "播放器.csproj"
$tfm = [regex]::Match($csproj, '<TargetFramework>([^<]+)</TargetFramework>').Groups[1].Value
$version = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
$libvlc = [regex]::Match($csproj, 'VideoLAN\.LibVLC\.Windows" Version="([^"]+)"').Groups[1].Value
$libvlcsharp = [regex]::Match($csproj, 'LibVLCSharp" Version="([^"]+)"').Groups[1].Value

# 格式数：只数「打开方式」会注册的三组（字幕那组刻意不注册，见 tools/installer/播放器.iss）。
$media = Read-Text "Core\MediaFormats.cs"

function Get-ExtensionCount([string]$text, [string]$name)
{
    $start = $text.IndexOf("string[] " + $name)
    if ($start -lt 0) { return -1 }

    $end = $text.IndexOf('};', $start)
    if ($end -lt 0) { return -1 }

    return ([regex]::Matches($text.Substring($start, $end - $start), '"\.[a-z0-9]+"')).Count
}

$videoCount = Get-ExtensionCount $media "VideoExtensions"
$audioCount = Get-ExtensionCount $media "AudioExtensions"
$playlistCount = Get-ExtensionCount $media "PlaylistExtensions"
$subtitleCount = Get-ExtensionCount $media "SubtitleExtensions"

if ($videoCount -lt 0 -or $audioCount -lt 0 -or $playlistCount -lt 0)
{
    throw "Core\MediaFormats.cs 里的扩展名数组没找到（是不是改过字段名？）"
}

$extensionTotal = $videoCount + $audioCount + $playlistCount

$allSources = @(Get-ChildItem -LiteralPath $root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|dist|\.git)\\' })
$productSources = @($allSources | Where-Object { $_.FullName -notmatch '\\tools\\' })
$smokeSources = @($allSources | Where-Object { $_.FullName -match '\\tools\\SmokeTest\\' })

function Get-LineTotal([object[]]$files)
{
    $total = 0
    foreach ($file in $files) { $total += [IO.File]::ReadAllLines($file.FullName).Count }
    return $total
}

$productLines = Get-LineTotal $productSources
$smokeLines = Get-LineTotal $smokeSources

$coreSources = @($productSources | Where-Object { $_.FullName -match '\\Core\\' })
$uiSources = @($productSources | Where-Object { $_.FullName -match '\\Ui\\' })
$mainFormSources = @($productSources | Where-Object { $_.Name -like 'MainForm*.cs' })

Write-Host ("  SDK          " + $sdk + "（仓库根的 global.json）")
Write-Host ("  目标框架     " + $tfm)
Write-Host ("  内核         libvlc " + $libvlc + " / LibVLCSharp " + $libvlcsharp)
Write-Host ("  版本         播放器.csproj 里是 " + $version)
Write-Host ("  注册格式     " + $extensionTotal + " 种（视频 " + $videoCount + " + 音频 " + $audioCount + " + 播放列表 " + $playlistCount + "；字幕另有 " + $subtitleCount + " 种，不注册）")
Write-Host ("  主程序       " + $productSources.Count + " 个文件 / " + $productLines.ToString("N0") + " 行")
Write-Host ("    Core       " + $coreSources.Count + " / " + (Get-LineTotal $coreSources).ToString("N0"))
Write-Host ("    Ui         " + $uiSources.Count + " / " + (Get-LineTotal $uiSources).ToString("N0"))
Write-Host ("    MainForm   " + $mainFormSources.Count + " / " + (Get-LineTotal $mainFormSources).ToString("N0"))
Write-Host ("  冒烟测试     " + $smokeSources.Count + " 个文件 / " + $smokeLines.ToString("N0") + " 行")

$dist = Join-Path $root "dist"

if (Test-Path -LiteralPath $dist)
{
    foreach ($artifact in Get-ChildItem -LiteralPath $dist -File | Sort-Object Name)
    {
        Write-Host ("  dist 产物    " + $artifact.Name + "  " + [math]::Round($artifact.Length / 1MB, 2) + " MB")
    }
}

$installed = Join-Path $env:LOCALAPPDATA "Programs\全能本地播放器"

if (Test-Path -LiteralPath $installed)
{
    $installedFiles = @(Get-ChildItem -LiteralPath $installed -Recurse -File)
    $installedMb = [math]::Round((($installedFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ("  本机装机     " + $installedFiles.Count + " 个文件 / " + $installedMb + " MB（" + $installed + "）")
}

# ---------------------------------------------------------------- 检查

Write-Host ""
Write-Host "=== 检查「当前状态」文档（README.md / docs/介绍.md） ===" -ForegroundColor Cyan

$readme = Read-Lines "README.md"
$intro = Read-Lines "docs\介绍.md"

Assert-Contains "README.md" $readme (".NET " + $sdkMajor + " SDK") ("环境要求里的 SDK 要跟 global.json 一致（现在是 " + $sdk + "）")
Assert-Contains "README.md" $readme $sdk "环境要求里要写出 global.json 钉住的那个版本号"
Assert-Contains "README.md" $readme $tfm "「构建与运行」里的输出路径要与目标框架一致"
Assert-Contains "README.md" $readme $libvlc "README 里要写当前内核版本"
Assert-Contains "docs\介绍.md" $intro $libvlc "介绍的技术栈里要写当前内核版本"
Assert-Contains "README.md" $readme ($extensionTotal.ToString() + " 种") "「打开方式」注册的格式数要与 Core\MediaFormats.cs 一致"

Assert-Absent "README.md" $readme ".NET 8 SDK" ("SDK 已经换成 " + $sdkMajor + "（global.json）")
Assert-Absent "README.md" $readme ".NET 8 Desktop Runtime" ("运行时要写 .NET " + $sdkMajor + " Desktop Runtime，或者写明发行包是自包含的")
Assert-Absent "README.md" $readme "本项目用 8.0 编译" "构建基线已经换了"
Assert-Absent "README.md" $readme "全能本地播放器-1." "发行资产名统一是 ASCII 的 local-player-<版本>-setup.exe / -portable.zip"
Assert-Absent "docs\介绍.md" $intro ".NET 8 + Windows Forms" ("技术栈已经换成 .NET " + $sdkMajor)
Assert-Absent "docs\介绍.md" $intro ".NET 8 Desktop Runtime" ("运行时要写 .NET " + $sdkMajor + " Desktop Runtime，或者写明发行包是自包含的")

# ---------------------------------------------------------------- 摘要

Write-Host ""

if ($failures.Count -eq 0)
{
    Write-Host "  文档与源码一致（上面每条 ✓ 都是一项检查）" -ForegroundColor Green
    Write-Host "  改文档时请照抄上面「事实」表里的数字，不要手算——发版前这条要一直是绿的。"
    exit 0
}

Write-Host ("  发现 " + $failures.Count + " 处漂移：") -ForegroundColor Red

foreach ($failure in $failures)
{
    Write-Host ("    - " + $failure) -ForegroundColor Red
}

Write-Host ""
Write-Host "  改法：照上面「事实」表把文档里的旧数字/旧版本换掉（只改「当前状态」，历史章节不要改）。"
exit 1
