# ForestCheat 编译脚本
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Forest"
#   powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install     # 编译后自动复制到 BepInEx\plugins

param(
    [string]$GameDir = "",
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'src\ForestCheat.cs'
$outDir = Join-Path $here 'bin'
$outDll = Join-Path $outDir 'ForestCheat.dll'

function Find-GameDir {
    param([string]$Hint)
    $candidates = @()
    if ($Hint) { $candidates += $Hint }
    try {
        $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
        if ($steam) {
            $libFile = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $libFile) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $libFile -Raw), '"path"\s+"([^"]+)"')) {
                    $libPath = $m.Groups[1].Value -replace '\\\\', '\'
                    if ($libPath) {
                        try { $candidates += [System.IO.Path]::Combine($libPath, 'steamapps\common\The Forest') } catch { }
                    }
                }
            }
            try { $candidates += [System.IO.Path]::Combine($steam, 'steamapps\common\The Forest') } catch { }
        }
    } catch { }

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        try {
            $probe = $c.TrimEnd('\') + '\TheForest_Data\Managed\UnityEngine.dll'
            if (Test-Path -LiteralPath $probe) { return $c }
        } catch { }
    }
    return $null
}

$dir = Find-GameDir -Hint $GameDir
if (-not $dir) {
    Write-Host '没有找到已安装的 The Forest。' -ForegroundColor Red
    Write-Host '如果装在别的盘，用参数指定：' -ForegroundColor Yellow
    Write-Host '  .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Forest"'
    exit 1
}
Write-Host "游戏目录：$dir" -ForegroundColor Green

$managed = Join-Path $dir 'TheForest_Data\Managed'
$bepCore = Join-Path $dir 'BepInEx\core'
$bepPlugins = Join-Path $dir 'BepInEx\plugins'

if (-not (Test-Path -LiteralPath (Join-Path $managed 'UnityEngine.dll'))) {
    throw "找不到 $managed\UnityEngine.dll，游戏可能没装完整。"
}
if (-not (Test-Path -LiteralPath $bepCore)) {
    Write-Host '找不到 BepInEx\core。' -ForegroundColor Red
    Write-Host '请先安装 BepInEx 5（x64, Unity Mono）：'
    Write-Host '  1. 下载 BepInEx_win_x64_5.4.x.zip（GitHub Releases）'
    Write-Host '  2. 解压全部文件到游戏根目录'
    Write-Host '  3. 启动一次游戏，确认生成 BepInEx\plugins 后再运行本脚本'
    exit 1
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 C# 编译器：$csc"
}

$refs = @()
# 关键：用游戏自带的 .NET 3.5 程序集，而不是编译器的 .NET 4.0
$refs += (Join-Path $managed 'mscorlib.dll')
$refs += (Join-Path $managed 'System.dll')
$refs += (Join-Path $managed 'System.Core.dll')
$refs += (Join-Path $managed 'UnityEngine.dll')
$refs += (Join-Path $managed 'UnityEngine.UI.dll')
Get-ChildItem -LiteralPath $bepCore -Filter *.dll -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch 'Mono\.Cecil|Il2Cpp' } |
    ForEach-Object { $refs += $_.FullName }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$cscArgs = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/platform:x64', '/optimize+', "/out:$outDll")
foreach ($r in ($refs | Select-Object -Unique)) {
    $cscArgs += "/reference:`"$r`""
}
$cscArgs += $source

Write-Host "编译中…（引用 $($refs.Count) 个程序集）"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败，见上面错误。' -ForegroundColor Red
    exit 1
}

Write-Host "编译成功：$outDll" -ForegroundColor Green

if ($Install) {
    New-Item -ItemType Directory -Force -Path $bepPlugins | Out-Null
    Copy-Item -LiteralPath $outDll -Destination $bepPlugins -Force
    Write-Host "已复制到：$bepPlugins" -ForegroundColor Green
}
else {
    Write-Host ''
    Write-Host "手动安装：把 $outDll 复制到"
    Write-Host "  $bepPlugins"
    Write-Host '或者重跑本脚本并加 -Install 参数。'
}
