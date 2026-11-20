<#
.SYNOPSIS
Builds a small framework-dependent single-file LocalDeck.exe plus a self-contained portable folder.

.EXAMPLE
pwsh .\build.ps1
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "LocalDeck.csproj"
$resolved = [System.IO.Path]::GetFullPath($Output)
$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
if (-not $resolved.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Output must stay inside the project directory."
}

# Preserve user settings across rebuilds. The AppData probe can fail in
# portable/sandboxed environments, in which case the app stores settings in
# dist\LocalDeck-data. Never let a rebuild delete the DPAPI-protected key.
$primaryData = Join-Path $resolved "LocalDeck-data"
$portableData = Join-Path $resolved "LocalDeck-portable\LocalDeck-data"
$dataBackupRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "LocalDeck-build-data-" + [Guid]::NewGuid().ToString("N"))
$primaryBackup = Join-Path $dataBackupRoot "primary"
$portableBackup = Join-Path $dataBackupRoot "portable"
$hadPrimaryData = Test-Path $primaryData
$hadPortableData = Test-Path $portableData
if ($hadPrimaryData -or $hadPortableData) {
    New-Item -ItemType Directory -Force $dataBackupRoot | Out-Null
}
if ($hadPrimaryData) {
    New-Item -ItemType Directory -Force $primaryBackup | Out-Null
    Get-ChildItem -LiteralPath $primaryData -Force | Copy-Item -Destination $primaryBackup -Recurse -Force
}
if ($hadPortableData) {
    New-Item -ItemType Directory -Force $portableBackup | Out-Null
    Get-ChildItem -LiteralPath $portableData -Force | Copy-Item -Destination $portableBackup -Recurse -Force
}

if (Test-Path $resolved) {
    Remove-Item $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force $resolved | Out-Null
if ($hadPrimaryData) {
    New-Item -ItemType Directory -Force $primaryData | Out-Null
    Get-ChildItem -LiteralPath $primaryBackup -Force | Copy-Item -Destination $primaryData -Recurse -Force
    Write-Host "Preserved existing LocalDeck-data settings." -ForegroundColor DarkCyan
}

# 1. Primary: small single-file exe. It uses the installed .NET 8 Desktop Runtime
#    and never extracts native files to %TEMP%.
$frameworkStage = Join-Path $resolved "_framework"
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $frameworkStage

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (single file) failed with exit code $LASTEXITCODE"
}
Copy-Item (Join-Path $frameworkStage "LocalDeck.exe") (Join-Path $resolved "LocalDeck.exe") -Force
Remove-Item $frameworkStage -Recurse -Force

# 2. Portable: fully self-contained folder. No .NET runtime needed, and no temp extraction.
$portable = Join-Path $resolved "LocalDeck-portable"
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $portable

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (portable) failed with exit code $LASTEXITCODE"
}

# 3. Support files.
Copy-Item (Join-Path $PSScriptRoot "README.md") (Join-Path $resolved "README.md") -Force
Copy-Item (Join-Path $PSScriptRoot "快速开始.txt") (Join-Path $resolved "快速开始.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "刷新Windows图标.cmd") (Join-Path $resolved "刷新Windows图标.cmd") -Force
Copy-Item (Join-Path $PSScriptRoot "启动桌仪.cmd") (Join-Path $resolved "启动桌仪.cmd") -Force
Copy-Item (Join-Path $PSScriptRoot "assets\DSEG-LICENSE.txt") (Join-Path $resolved "DSEG-LICENSE.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "assets\LibreHardwareMonitor-NOTICE.txt") (Join-Path $resolved "LibreHardwareMonitor-NOTICE.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "screenshots") (Join-Path $resolved "screenshots") -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "快速开始.txt") (Join-Path $portable "快速开始.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "刷新Windows图标.cmd") (Join-Path $portable "刷新Windows图标.cmd") -Force
Copy-Item (Join-Path $PSScriptRoot "启动桌仪.cmd") (Join-Path $portable "启动桌仪.cmd") -Force
Copy-Item (Join-Path $PSScriptRoot "assets\DSEG-LICENSE.txt") (Join-Path $portable "DSEG-LICENSE.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "assets\LibreHardwareMonitor-NOTICE.txt") (Join-Path $portable "LibreHardwareMonitor-NOTICE.txt") -Force

# 4. One downloadable portable archive.
$zip = Join-Path $resolved "LocalDeck-portable.zip"
try {
    Compress-Archive -Path $portable -DestinationPath $zip -CompressionLevel Optimal
}
catch {
    Write-Warning "Portable zip was not created: $($_.Exception.Message)"
}

if ($hadPortableData) {
    New-Item -ItemType Directory -Force $portableData | Out-Null
    Get-ChildItem -LiteralPath $portableBackup -Force | Copy-Item -Destination $portableData -Recurse -Force
    Write-Host "Preserved existing portable settings (not included in the zip)." -ForegroundColor DarkCyan
}
if (Test-Path $dataBackupRoot) {
    Remove-Item $dataBackupRoot -Recurse -Force
}

# The DSH workspace is low-integrity by design. Re-label distributables as
# Medium so double-click launches by the normal user can create a tray icon.
$artifacts = @(
    (Join-Path $resolved "LocalDeck.exe"),
    (Join-Path $portable "LocalDeck.exe"),
    (Join-Path $resolved "启动桌仪.cmd"),
    (Join-Path $portable "启动桌仪.cmd"),
    $zip
)
foreach ($artifact in $artifacts) {
    if (Test-Path $artifact) {
        try { icacls $artifact /setintegritylevel Medium | Out-Null } catch { }
        try { Unblock-File -LiteralPath $artifact -ErrorAction SilentlyContinue } catch { }
    }
}

$singleSize = [math]::Round((Get-Item (Join-Path $resolved "LocalDeck.exe")).Length / 1KB, 0)
$portableSize = [math]::Round(
    (Get-ChildItem $portable -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ""
Write-Host "Primary:   $resolved\LocalDeck.exe (${singleSize} KB, needs .NET 8 Desktop Runtime)" -ForegroundColor Green
Write-Host "Portable:  $portable\LocalDeck.exe ($portableSize MB, no runtime needed)" -ForegroundColor Green
if (Test-Path $zip) {
    Write-Host "Archive:   $zip" -ForegroundColor Green
}

