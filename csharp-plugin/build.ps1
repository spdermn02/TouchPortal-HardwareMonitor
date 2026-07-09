# Build script for Touch Portal Hardware Monitor C# Plugin

$ErrorActionPreference = "Stop"

$projectDir = "$PSScriptRoot\TouchPortalHardwareMonitor"
$launcherDir = "$PSScriptRoot\Launcher"
$publishDir = "$projectDir\bin\Release\net10.0\win-x64\publish"
$launcherPublishDir = "$launcherDir\bin\Release\net10.0\win-x64\publish"
$installersDir = "$PSScriptRoot\Installers"
$pluginName = "TouchPortalHardwareMonitor"
$version = "2.2.2"

# PresentMon (Intel, MIT) is bundled for the accurate FPS backend. The binary
# is NOT committed to the repo - it's downloaded here into tools\ (gitignored)
# on first build and copied into the package.
$toolsDir = "$PSScriptRoot\tools"
$presentMonVersion = "1.10.0"
$presentMonExe = "$toolsDir\PresentMon.exe"
$presentMonUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v$presentMonVersion/PresentMon-$presentMonVersion-x64.exe"

Write-Host "Building Touch Portal Hardware Monitor C# Plugin v$version..." -ForegroundColor Cyan

# Ensure PresentMon is available (download once)
if (-not (Test-Path $presentMonExe)) {
    Write-Host "Downloading PresentMon $presentMonVersion..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    try {
        Invoke-WebRequest -Uri $presentMonUrl -OutFile $presentMonExe
        Write-Host "PresentMon downloaded to $presentMonExe" -ForegroundColor Green
    }
    catch {
        Write-Host "WARNING: Could not download PresentMon ($($_.Exception.Message))." -ForegroundColor Red
        Write-Host "The PresentMon FPS backend will be unavailable in this build; RTSS/Built-in still work." -ForegroundColor Red
    }
}

# Clean previous builds
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
if (Test-Path $launcherPublishDir) {
    Remove-Item -Recurse -Force $launcherPublishDir
}

# Build and publish main plugin
Write-Host "Publishing main plugin..." -ForegroundColor Yellow
dotnet publish $projectDir -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Main plugin build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Main plugin build successful!" -ForegroundColor Green

# Build and publish launcher
Write-Host "Publishing launcher..." -ForegroundColor Yellow
dotnet publish $launcherDir -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "Launcher build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Launcher build successful!" -ForegroundColor Green

# Copy launcher to main publish directory
Write-Host "Copying launcher to publish directory..." -ForegroundColor Yellow
Copy-Item "$launcherPublishDir\TouchPortalHardwareMonitor-Launcher.exe" "$publishDir\"

# Bundle PresentMon next to the plugin (if it was downloaded)
if (Test-Path $presentMonExe) {
    Write-Host "Bundling PresentMon..." -ForegroundColor Yellow
    Copy-Item $presentMonExe "$publishDir\PresentMon.exe"
} else {
    Write-Host "PresentMon.exe not present - packaging without the PresentMon FPS backend." -ForegroundColor Yellow
}

# Create Installers directory
if (-not (Test-Path $installersDir)) {
    New-Item -ItemType Directory -Path $installersDir | Out-Null
}

# Create .tpp package (ZIP file)
$tppPath = "$installersDir\$pluginName-Windows-$version.tpp"

Write-Host "Creating TPP package..." -ForegroundColor Yellow

# Remove old package if exists
if (Test-Path $tppPath) {
    Remove-Item $tppPath
}

# Create temp directory for packaging
$tempDir = "$env:TEMP\$pluginName-package"
if (Test-Path $tempDir) {
    Remove-Item -Recurse -Force $tempDir
}
New-Item -ItemType Directory -Path "$tempDir\$pluginName" | Out-Null

# Copy files to temp directory
Copy-Item "$publishDir\*" "$tempDir\$pluginName\" -Recurse

# Rename icon to match plugin name
if (Test-Path "$tempDir\$pluginName\plugin_icon.png") {
    Rename-Item "$tempDir\$pluginName\plugin_icon.png" "$pluginName.png"
}

# Create ZIP first, then rename to TPP
$zipPath = "$installersDir\$pluginName-Windows-$version.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}
Compress-Archive -Path "$tempDir\$pluginName" -DestinationPath $zipPath
Rename-Item $zipPath $tppPath

# Cleanup
Remove-Item -Recurse -Force $tempDir

Write-Host "Package created: $tppPath" -ForegroundColor Green
Write-Host "Done!" -ForegroundColor Cyan
