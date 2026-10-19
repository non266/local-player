<#
  release-check.ps1 —— 一条命令跑完本项目的验收仪式。

  做四件事：
    1. 核对「当前状态」文档里的数字（tools\docs-check.ps1，只读；可 -SkipDocs 跳过）
    2. 构建主工程（Debug；默认还构建 Release）
    3. 构建冒烟测试工程
    4. 从输出目录跑全量 20 步冒烟，打印摘要与失败原因

  零警告是硬条件：出现「N 个警告」且 N > 0 就判失败（这个项目一直按 0 警告 0 错误要求自己）。

  用法（本机默认策略禁止直接跑 .ps1，所以走 -ExecutionPolicy Bypass）：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -Filter 歌单     # 只跑名字含「歌单」的检查（连带依赖的前置）
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -SkipSmoke       # 只构建（改文档时用）
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -SkipRelease     # 只 Debug
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -SkipDocs        # 正在改文档时跳过数字核对

  退出码：0 = 全绿；1 = 有失败（摘要里列出每一步的失败原因）。

  变异验证不在这个脚本里：那要临时改源码，属于"人来做"的一步，见 docs/维护手册.md 第 2 节。
#>
[CmdletBinding()]
param(
    [string]$Filter = "",
    [switch]$SkipSmoke,
    [switch]$SkipRelease,
    [switch]$SkipDocs
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "播放器.csproj"
$testProject = Join-Path $root "tools\SmokeTest\SmokeTest.csproj"
$outputDirectory = Join-Path $root "bin\Debug\net10.0-windows10.0.19041.0"

$failures = New-Object System.Collections.Generic.List[string]
$startedAt = Get-Date

function Write-Section([string]$title)
{
    Write-Host ""
    Write-Host ("=== " + $title + " ===") -ForegroundColor Cyan
}

function Write-Failure([string]$message)
{
    $failures.Add($message)
    Write-Host ("  ✗ " + $message) -ForegroundColor Red
}

# 本地化的构建输出里找「N 个警告 / N 个错误」，用来把"零警告"变成硬判据
function Get-Count([string[]]$lines, [string]$pattern)
{
    foreach ($line in $lines)
    {
        $match = [regex]::Match($line, $pattern)
        if ($match.Success) { return [int]$match.Groups[1].Value }
    }

    return -1
}

function Invoke-Build([string]$path, [string]$configuration, [string]$label)
{
    Write-Section ("构建 " + $label)

    # 原生命令往 stderr 写东西时会被 PowerShell 变成 ErrorRecord，
    # 而 $ErrorActionPreference='Stop' 会把它当"终止错误"——那样一条普通警告就能中断脚本。
    # 所以调用 dotnet 的这一小段临时放回 Continue，自己看退出码与「N 个警告」。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try
    {
        $lines = & dotnet build $path -c $configuration --no-restore 2>&1 | ForEach-Object { $_.ToString() }
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previous
    }

    $lines | Select-Object -Last 6 | ForEach-Object { Write-Host ("  " + $_) }

    if ($lines -match "NETSDK1112")
    {
        Write-Host "  ! obj\project.assets.json 可能是别的 SDK 留下的：先跑一次 dotnet restore。" -ForegroundColor Yellow
    }

    if ($lines -match "NETSDK1127")
    {
        Write-Host "  ! 目标包缺失：当前 SDK 找不到目标框架对应的引用包。换过 SDK 或目标框架之后先跑一次 dotnet restore。" -ForegroundColor Yellow
        Write-Host "    离线补齐的办法见 docs/ARCHITECTURE.md 第 16.1 节。" -ForegroundColor Yellow
    }

    if ($lines -match "NETSDK1004|Assets file")
    {
        Write-Host "  ! 还没还原过：先跑一次 dotnet restore（首次需要联网）。" -ForegroundColor Yellow
    }

    if ($exitCode -ne 0)
    {
        Write-Failure ($label + " 构建失败（退出码 " + $exitCode + "）")
        return $false
    }

    $warnings = Get-Count $lines "(\d+)\s*个警告"
    $errors = Get-Count $lines "(\d+)\s*个错误"

    if ($warnings -gt 0) { Write-Failure ($label + " 有 " + $warnings + " 个警告（本项目要求 0）") }
    if ($errors -gt 0) { Write-Failure ($label + " 有 " + $errors + " 个错误") }

    if ($warnings -eq 0 -and $errors -eq 0)
    {
        Write-Host ("  ✓ " + $label + "：0 警告 0 错误") -ForegroundColor Green
    }

    return ($warnings -le 0 -and $errors -le 0)
}

# ---------------------------------------------------------------- 环境

Write-Section "环境"
Write-Host ("  仓库根目录  " + $root)
Write-Host ("  dotnet      " + (dotnet --version))

if (-not (Test-Path -LiteralPath $project))
{
    Write-Failure ("找不到 " + $project + "（这个脚本要放在 tools\ 目录下）")
}

# 构建基线由仓库根目录的 global.json 钉在 SDK 10.0.402（目标框架 net10.0-windows）；
# 装了更高的 SDK 也能构建，所以这里不按版本挑刺，只在明显低于 8 时提醒一句。
$sdkVersion = (dotnet --version)
if ($sdkVersion -notmatch '^(8|9|10|11|12)\.')
{
    Write-Host ("  ! 当前 SDK 是 " + $sdkVersion + "，本工程基线是 .NET 10（global.json 钉在 10.0.402）。") -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 文档

# 只读核对：README / docs\介绍.md 里写的 SDK、目标框架、内核版本、格式数必须与源码一致。
# 漂移了就在这里红，免得"文档说的事实"又悄悄过时（正是 R6 这一轮发现的问题）。
if (-not $SkipDocs -and $failures.Count -eq 0)
{
    Write-Section "文档数字核对"
    $docsCheck = Join-Path $PSScriptRoot "docs-check.ps1"

    if (-not (Test-Path -LiteralPath $docsCheck))
    {
        Write-Failure ("找不到 " + $docsCheck + "（文档数字核对跑不了）")
    }
    else
    {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'

        try
        {
            & $docsCheck
            $docsExit = $LASTEXITCODE
        }
        catch
        {
            $docsExit = 1
            Write-Host ("  ! 文档数字核对崩了：" + $_.Exception.Message) -ForegroundColor Yellow
        }
        finally
        {
            $ErrorActionPreference = $previous
        }

        if ($docsExit -ne 0)
        {
            Write-Failure "文档数字核对没通过（上面每条 ✗ 都指出了要改的文件与行）"
        }
    }
}

# ---------------------------------------------------------------- 构建

if ($failures.Count -eq 0)
{
    Invoke-Build $project "Debug" "主工程 Debug" | Out-Null
    Invoke-Build $testProject "Debug" "冒烟测试 Debug" | Out-Null

    if (-not $SkipRelease)
    {
        Invoke-Build $project "Release" "主工程 Release" | Out-Null
    }
}

# ---------------------------------------------------------------- 冒烟

if (-not $SkipSmoke -and $failures.Count -eq 0)
{
    $title = if ($Filter.Length -gt 0) { "冒烟测试（筛选：" + $Filter + "）" } else { "冒烟测试（全量 20 步）" }
    Write-Section $title

    Push-Location $outputDirectory
    try
    {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'

        try
        {
            $smokeLines = if ($Filter.Length -gt 0)
            {
                & dotnet SmokeTest.dll $Filter 2>&1 | ForEach-Object { $_.ToString() }
            }
            else
            {
                & dotnet SmokeTest.dll 2>&1 | ForEach-Object { $_.ToString() }
            }

            $smokeExit = $LASTEXITCODE
        }
        finally
        {
            $ErrorActionPreference = $previous
        }
    }
    finally
    {
        Pop-Location
    }

    $smokeLines | Select-Object -Last 8 | ForEach-Object { Write-Host ("  " + $_) }

    if ($smokeExit -ne 0 -or -not ($smokeLines -match "SMOKE TEST PASSED"))
    {
        $logPath = Join-Path $env:TEMP ("release-check-smoke-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")
        $smokeLines | Set-Content -LiteralPath $logPath -Encoding UTF8

        Write-Failure ("冒烟测试没通过（退出码 " + $smokeExit + "），完整输出：" + $logPath)

        # 原生崩（M8 那种 0xC0000005）会把 Console 里没刷出去的东西一起带走，
        # 所以冒烟自己每一步都往这个日志里写一行（写完立刻落盘）。
        # 崩了就把最后几行打出来：一眼能看出"当时在做什么"。
        $journalPath = Join-Path $outputDirectory "smoke-journal.txt"

        if (Test-Path -LiteralPath $journalPath)
        {
            Write-Host "  --- smoke-journal.txt 最后 12 行（崩在哪一步看这里） ---" -ForegroundColor Yellow
            Get-Content -LiteralPath $journalPath -Encoding UTF8 -Tail 12 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
        }
        else
        {
            Write-Host "  （没有 smoke-journal.txt：可能是构建阶段就失败了）" -ForegroundColor Yellow
        }
    }
    else
    {
        Write-Host "  ✓ 冒烟测试全绿" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- 摘要

Write-Section "摘要"

$elapsed = [int](((Get-Date) - $startedAt).TotalSeconds)

if ($failures.Count -eq 0)
{
    Write-Host ("  全绿（用时 " + $elapsed + " 秒）") -ForegroundColor Green
    Write-Host "  别忘了：新加的断言要做一个'改坏了就会红'的变异（docs/维护手册.md 第 2 节）"
    exit 0
}

Write-Host ("  失败 " + $failures.Count + " 项（用时 " + $elapsed + " 秒）") -ForegroundColor Red

foreach ($failure in $failures)
{
    Write-Host ("    - " + $failure) -ForegroundColor Red
}

exit 1
