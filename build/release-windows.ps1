<#
.SYNOPSIS
  Builds the Windows release of UniFiles: tests, self-contained publish, bundled tools
  (ffmpeg, Ghostscript) with their licenses, then the Velopack installer and update packages.

.EXAMPLE
  ./build/release-windows.ps1 -Version 0.1.0

.NOTES
  Output: artifacts/releases (UniFiles-win-Setup.exe, UniFiles-win-Portable.zip, update packages).
  Signing: pass -SignParams (signtool arguments) once a code-signing certificate is available.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $SkipTests,
    # Rebuilding an already packaged version: start from an empty releases folder.
    [switch] $Rebuild,
    [string] $SignParams = "",
    # Ghostscript is taken from a local install (its official download is an installer, not an archive).
    [string] $GhostscriptBin = "C:\Program Files\gs\gs10.08.0\bin"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $PSScriptRoot "cache"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
$tools = Join-Path $publish "tools"
$releases = Join-Path $artifacts "releases"

# Pinned third-party binaries: same bytes in every release (checked by SHA-256).
$ffmpeg = @{
    Name   = "ffmpeg-n9.0.2-27-g7e93014c5b-win64-gpl-shared-9.0"
    Url    = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-13-13-05/ffmpeg-n9.0.2-27-g7e93014c5b-win64-gpl-shared-9.0.zip"
    Sha256 = "73222d3e5e0952033b1140dc771fe00b82a4414c7a6544d5ff2febe7e450850c"
}
$ghostscriptVersion = "10.08.0"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Yellow }

Step "Version $Version"
New-Item -ItemType Directory -Force $cache, $artifacts | Out-Null
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if ($Rebuild -and (Test-Path $releases)) { Remove-Item $releases -Recurse -Force }

if (-not $SkipTests) {
    Step "Tests"
    dotnet test (Join-Path $root "UniFiles.slnx") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Step "Publish (self-contained, win-x64)"
dotnet publish (Join-Path $root "src\UniFiles.App\UniFiles.App.csproj") -c Release -o $publish --nologo -v q `
    -p:Version=$Version -p:InformationalVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Step "ffmpeg $($ffmpeg.Name)"
$zip = Join-Path $cache "$($ffmpeg.Name).zip"
if (-not (Test-Path $zip)) {
    # Retries cover dropped connections; the temporary name keeps a partial download from being reused.
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri $ffmpeg.Url -OutFile "$zip.part"
            break
        }
        catch {
            if ($attempt -ge 5) { throw }
            Write-Host "Download failed ($($_.Exception.Message)), retrying..."
            Start-Sleep -Seconds 5
        }
    }
    Move-Item "$zip.part" $zip -Force
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $ffmpeg.Sha256) { throw "ffmpeg archive checksum mismatch ($hash)." }
$extract = Join-Path $cache $ffmpeg.Name
if (-not (Test-Path $extract)) { Expand-Archive $zip -DestinationPath $cache }
New-Item -ItemType Directory -Force $tools | Out-Null
# ffplay is not used by UniFiles.
Get-ChildItem (Join-Path $extract "bin") -File | Where-Object Name -ne "ffplay.exe" | Copy-Item -Destination $tools
Copy-Item (Join-Path $extract "LICENSE.txt") (Join-Path $tools "ffmpeg-LICENSE.txt")

Step "Ghostscript $ghostscriptVersion"
$gsExe = Join-Path $GhostscriptBin "gswin64c.exe"
if (-not (Test-Path $gsExe)) { throw "Ghostscript not found in $GhostscriptBin (install gs$($ghostscriptVersion.Replace('.', '')) or pass -GhostscriptBin)." }
$gsVersion = (& $gsExe --version).Trim()
if ($gsVersion -ne $ghostscriptVersion) { throw "Expected Ghostscript $ghostscriptVersion, found $gsVersion." }
# The DLL embeds Ghostscript's resources: the console exe and the DLL are enough.
Copy-Item $gsExe, (Join-Path $GhostscriptBin "gsdll64.dll") -Destination $tools
Copy-Item (Join-Path $GhostscriptBin "..\doc\COPYING") (Join-Path $tools "ghostscript-COPYING.txt")

Step "Smoke test of the bundled tools"
& (Join-Path $tools "ffmpeg.exe") -hide_banner -version | Select-Object -First 1
& (Join-Path $tools "gswin64c.exe") --version

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published size: {0:N0} MB" -f $size)

Step "Velopack package"
$notes = Join-Path $PSScriptRoot "release-notes\$Version.md"
$packArgs = @(
    "pack",
    "--packId", "UniFiles",
    "--packVersion", $Version,
    "--runtime", "win-x64",
    "--packDir", $publish,
    "--mainExe", "UniFiles.exe",
    "--packTitle", "UniFiles",
    "--packAuthors", "Uniform Systems",
    "--icon", (Join-Path $root "src\UniFiles.App\Assets\unifiles.ico"),
    "--splashImage", (Join-Path $root "src\UniFiles.App\Assets\unifiles-icon.png"),
    "--shortcuts", "Desktop,StartMenuRoot",
    "--outputDir", $releases
)
if (Test-Path $notes) { $packArgs += @("--releaseNotes", $notes) }
if ($SignParams) { $packArgs += @("--signParams", $SignParams) }

Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
}
finally {
    Pop-Location
}

Step "Done"
Get-ChildItem $releases -File | Sort-Object LastWriteTime -Descending |
    ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }

