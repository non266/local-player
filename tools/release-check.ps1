<#
  release-check.ps1 —— 一条命令跑完本项目的验收仪式。

  做三件事：
    1. 构建主工程（Debug；默认还构建 Release）
    2. 构建冒烟测试工程
    3. 从输出目录跑全量 20 步冒烟，打印摘要与失败原因

  零警告是硬条件：出现「N 个警告」且 N > 0 就判失败（这个项目一直按 0 警告 0 错误要求自己）。

  用法（本机默认策略禁止直接跑 .ps1，所以走 -ExecutionPolicy Bypass）：
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -Filter 歌单     # 只跑名字含「歌单」的检查（连带依赖的前置）
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -SkipSmoke       # 只构建（改文档时用）
      powershell -NoProfile -ExecutionPolicy Bypass -File tools\release-check.ps1 -SkipRelease     # 只 Debug

  退出码：0 = 全绿；1 = 有失败（摘要里列出每一步的失败原因）。

  变异验证不在这个脚本里：那要临时改源码，属于"人来做"的一步，见 docs/维护手册.md 第 2 节。
#>
[CmdletBinding()]
param(
    [string]$Filter = "",
    [switch]$SkipSmoke,
    [switch]$SkipRelease
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "播放器.csproj"
$testProject = Join-Path $root "tools\SmokeTest\SmokeTest.csproj"
$outputDirectory = Join-Path $root "bin\Debug\net8.0-windows"

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

    if ($lines -match "NETSDK1127")
    {
        Write-Host "  ! 目标包缺失：这台机器默认的 SDK 需要 net8.0 的 8.0.31 引用包。" -ForegroundColor Yellow
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

if ((dotnet --version) -like "8.*")
{
    Write-Host "  ! 当前是 8.x SDK。这个工程用了 C# 14 语法（例如 `?.` 赋值），要用 SDK 10 构建。" -ForegroundColor Yellow
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
