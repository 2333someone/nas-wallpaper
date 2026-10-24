<#
    发布成单个 exe。

    默认：自包含（目标机无需安装 .NET），产物约 140–160 MB。
    加 -FrameworkDependent：目标机需安装 .NET 9 桌面运行时，产物约 3 MB。

    例：
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -FrameworkDependent
        powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Runtime win-arm64 -Output dist
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\NasWallpaper\NasWallpaper.csproj'
$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$Output"))

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $(if ($FrameworkDependent) { 'false' } else { 'true' }),
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-o', $outDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir 'NAS Wallpaper.exe'
$size = (Get-Item $exe).Length / 1MB
"输出目录：$outDir"
"单文件：$exe（{0:0.0} MB）" -f $size
$others = Get-ChildItem $outDir -Recurse -File | Where-Object { $_.FullName -ne $exe }
if ($others) {
    "同目录附加文件（PDB 等，可删除）："
    $others | ForEach-Object { "  " + $_.Name }
}
