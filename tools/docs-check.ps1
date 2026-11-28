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

# 两句话的先后顺序：用来挡"提示块插进表格中间"这类 Markdown 结构错误。
# 只查首次出现的位置；顺序不成立 / 有一句找不到都算红。
function Assert-Order([string]$relative, [string[]]$lines, [string]$firstNeedle, [string]$secondNeedle, [string]$why)
{
    $first = Find-Line $lines $firstNeedle
    $second = Find-Line $lines $secondNeedle

    if ($first -eq 0 -or $second -eq 0 -or $first -ge $second)
    {
        $message = $relative + " 里「" + $firstNeedle + "」（第 " + $first + " 行）应该排在「" + $secondNeedle + "」（第 " + $second + " 行）之前：" + $why
        $failures.Add($message)
        Write-Host ("  ✗ " + $message) -ForegroundColor Red
    }
    else
    {
        Write-Host ("  ✓ " + $relative + ":" + $first + "「" + $firstNeedle + "」在「" + $secondNeedle + "」之前") -ForegroundColor Green
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
Write-Host "=== 检查「当前状态」文档（README.md / docs/介绍.md / docs/ARCHITECTURE.md） ===" -ForegroundColor Cyan

$readme = Read-Lines "README.md"
$intro = Read-Lines "docs\介绍.md"
$architecture = Read-Lines "docs\ARCHITECTURE.md"
$reference = Read-Lines "docs\参考手册.md"

Assert-Contains "README.md" $readme (".NET " + $sdkMajor + " SDK") ("环境要求里的 SDK 要跟 global.json 一致（现在是 " + $sdk + "）")
Assert-Contains "README.md" $readme $sdk "环境要求里要写出 global.json 钉住的那个版本号"
Assert-Contains "README.md" $readme $tfm "「构建与运行」里的输出路径要与目标框架一致"
Assert-Contains "README.md" $readme $libvlc "README 里要写当前内核版本"
Assert-Contains "docs\介绍.md" $intro $libvlc "介绍的技术栈里要写当前内核版本"
Assert-Contains "README.md" $readme ($extensionTotal.ToString() + " 种") "「打开方式」注册的格式数要与 Core\MediaFormats.cs 一致"

# 规模数字（1.9.0 起）：以前这些数只打印、不检查，于是"忘了更新"只能靠人记得。
# 断言写的是文档里的原话片段，改文档时照抄「事实」表即可。
$productCountText = $productSources.Count.ToString()
$productLinesText = $productLines.ToString("N0")
$smokeCountText = $smokeSources.Count.ToString()
$smokeLinesText = $smokeLines.ToString("N0")

Assert-Contains "docs\介绍.md" $intro ("主程序 **" + $productLinesText + " 行 / " + $productCountText + " 个文件**") ("介绍里的主程序规模要与源码一致（现在是 " + $productLinesText + " 行 / " + $productCountText + " 个文件）")
Assert-Contains "docs\介绍.md" $intro ("测试 **" + $smokeLinesText + " 行 / " + $smokeCountText + " 个文件**") ("介绍里的测试规模要与源码一致（现在是 " + $smokeLinesText + " 行 / " + $smokeCountText + " 个文件）")
Assert-Contains "docs\ARCHITECTURE.md" $architecture ($productLinesText + " 行 / **" + $productCountText + " 个文件**") ("ARCHITECTURE 第 1 节的规模要与源码一致（现在是 " + $productLinesText + " 行 / " + $productCountText + " 个文件）")
Assert-Contains "docs\ARCHITECTURE.md" $architecture ($smokeLinesText + " 行 / " + $smokeCountText + " 个文件") ("ARCHITECTURE 第 1 节的冒烟规模要与源码一致（现在是 " + $smokeLinesText + " 行 / " + $smokeCountText + " 个文件）")

Assert-Absent "README.md" $readme ".NET 8 SDK" ("SDK 已经换成 " + $sdkMajor + "（global.json）")
Assert-Absent "README.md" $readme ".NET 8 Desktop Runtime" ("运行时要写 .NET " + $sdkMajor + " Desktop Runtime，或者写明发行包是自包含的")
Assert-Absent "README.md" $readme "本项目用 8.0 编译" "构建基线已经换了"
Assert-Absent "README.md" $readme "全能本地播放器-1." "发行资产名统一是 ASCII 的 local-player-<版本>-setup.exe / -portable.zip"
Assert-Absent "docs\介绍.md" $intro ".NET 8 + Windows Forms" ("技术栈已经换成 .NET " + $sdkMajor)
Assert-Absent "docs\介绍.md" $intro ".NET 8 Desktop Runtime" ("运行时要写 .NET " + $sdkMajor + " Desktop Runtime，或者写明发行包是自包含的")

# 冒烟口径（1.13.0 起）：步骤数来自 Program.cs 里的 SmokeStep 列表，
# 检查编号上限来自 StepCount；四个文档必须用同一句话，避免再出现"24 步 / 20 项"各说各的。
$program = Read-Text "tools\SmokeTest\Program.cs"
$checkCount = [regex]::Match($program, 'private const int StepCount = (\d+);').Groups[1].Value
$runnerCount = ([regex]::Matches($program, 'new SmokeStep\(')).Count
$manual = Read-Lines "docs\维护手册.md"
$smokePhrase = "$runnerCount 个运行步骤 / $checkCount 项检查"

if ($checkCount -eq "" -or $runnerCount -eq 0)
{
    throw "没能从 tools\SmokeTest\Program.cs 读出步骤数 / 检查数（是不是改了 const 的名字？）"
}

Assert-Contains "README.md" $readme $smokePhrase "冒烟口径要以 Program.cs 为准（现在是 $runnerCount 个运行步骤 / $checkCount 项检查）"
Assert-Contains "docs\介绍.md" $intro $smokePhrase "冒烟口径要以 Program.cs 为准（现在是 $runnerCount 个运行步骤 / $checkCount 项检查）"
Assert-Contains "docs\ARCHITECTURE.md" $architecture $smokePhrase "冒烟口径要以 Program.cs 为准（现在是 $runnerCount 个运行步骤 / $checkCount 项检查）"
Assert-Contains "docs\维护手册.md" $manual $smokePhrase "冒烟口径要以 Program.cs 为准（现在是 $runnerCount 个运行步骤 / $checkCount 项检查）"

# 1.13.0 之前四份文档各说各的；把当时那几句**具体旧话**钉成"不许回来"，
# 否则只查"新句子在不在"仍可能同一份文档里新旧并存。
Assert-Absent "README.md" $readme "全量 24 步冒烟" "1.13.0 已统一为 21 个运行步骤 / 25 项检查，1.14.0 是 22 / 26"
Assert-Absent "docs\ARCHITECTURE.md" $architecture "第 18～20 步" "1.13.0 已改成第 22～24 项检查 / 第 18～20 个运行步骤"
Assert-Absent "docs\维护手册.md" $manual "全量 21 步冒烟" "1.14.0 统一为 22 个运行步骤 / 26 项检查"
Assert-Absent "docs\介绍.md" $intro "一共 20 项检查" "1.13.0 已统一为 21 个运行步骤 / 25 项检查，1.14.0 是 22 / 26"

# 1.14.0 起网络目录与网络 m3u 都走后台。README 里这几句是当时的"当前状态"，
# 已经不对了；钉成"不许回来"，免得下次改文档又抄回旧结论。
Assert-Absent "README.md" $readme 'IsOpenable` 仍是同步的' "1.14.0 起网络位置不在界面线程查存在性，这句是 1.13.0 之前的旧说法"
Assert-Absent "README.md" $readme "网络目录的拖入暂不支持" "1.14.0 已支持网络目录（判目录 / 枚举都在后台）"
Assert-Absent "README.md" $readme "第 25 步" "筛跑示例里写的是检查编号，要写「第 25 项检查」（或「第 21 个运行步骤」）"

# 1.30.0 把逐帧 / 延迟 / 歌词偏移收进命令目录之后，下面的旧说法必须消失；
# README 的快捷键表也不能再被提示块从中间截断（否则后 4 行渲染成第二张表）。
Assert-Absent "docs\参考手册.md" $reference '逐帧 `E` / 延迟' "1.30.0 起逐帧 / 延迟 / 歌词偏移都可以改，这句是旧口径"
Assert-Contains "docs\参考手册.md" $reference "实测（1.32.0）装出来" "安装体积那一行要写当前版本（1.32.0 实测）"
Assert-Absent "docs\介绍.md" $intro "按名称排序约 4 秒" "1.18.0 实测 50,000 条按名称排序是 1,566 ms，旧数字是 1.15.0 之前的"
Assert-Contains "docs\介绍.md" $intro "按名称排序约 1.6 秒" "排序数字要跟 1.18.0 的实测一致"
Assert-Contains "docs\介绍.md" $intro "1.32.0 实测" "程序体积那一行要写当前版本（1.32.0 实测）"
Assert-Order "README.md" $readme '| `Ctrl+]` / `Ctrl+[` |' "> 1.30.0 起可以改" "快捷键表的所有行必须在提示块之前，否则 Markdown 会断成两张表"

# 1.32.0：播放列表整批移动 / 撤销移除；缩略图早已落盘，不能再写"只在内存缓存"。
Assert-Absent "docs\介绍.md" $intro "只在内存缓存" "1.28.0 起缩略图有 128 MB 落盘缓存，这句是旧口径"
Assert-Contains "docs\介绍.md" $intro "落盘缓存 128 MB" "缩略图那一行要写落盘缓存与上限"
Assert-Contains "README.md" $readme "整批上移下移与拖动" "播放列表多选要写清整批移动"
Assert-Contains "README.md" $readme "Ctrl+Z" "撤销移除的固定键要写出来"
Assert-Absent "README.md" $readme "拖动换位仍只对一项" "1.32.0 起多选可以整批上移 / 下移 / 拖动"
Assert-Absent "docs\参考手册.md" $reference "上移、下移与拖动换位仍只对" "1.32.0 起多选可以整批移动，旧限制句不能留着"
Assert-Contains "docs\参考手册.md" $reference "撤销最近一次移除" "参考手册要写 `Ctrl+Z` 撤销的口径"

# 1.33.0：清空也纳入撤销；歌单库多选整批拖动；批量添加会标「未保存」。
Assert-Contains "README.md" $readme "撤销清空" "README 要写 `Ctrl+Z` 也能撤销清空"
Assert-Contains "README.md" $readme "多选按块拖动排序" "README 的歌单库要写多选整批拖动"
Assert-Contains "docs\介绍.md" $intro "撤销最近一次移除或清空" "介绍要写清空撤销的口径"
Assert-Contains "docs\介绍.md" $intro "歌单窗口多选整批拖动" "介绍要写歌单库整批拖动"
Assert-Contains "docs\参考手册.md" $reference "撤销清空（恢复 N 项）" "参考手册要写清空撤销的菜单文案"
Assert-Contains "docs\参考手册.md" $reference "歌单窗口里的拖拽换位支持多选按块移动" "参考手册要写歌单库整批拖动"
Assert-Absent "docs\参考手册.md" $reference "清空播放列表」仍走确认框，不提供撤销" "1.33.0 起清空可以撤销，旧限制句不能留着"
Assert-Absent "docs\参考手册.md" $reference "拖拽换位只认" "1.33.0 起歌单库多选可以整批拖动，旧限制句不能留着"

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
