<#
.SYNOPSIS
    Builds a ship-ready REDUX release into dist\.

.DESCRIPTION
    Publishes redux.csproj as a self-contained, single-file win-x64 executable that runs on
    machines with no .NET runtime installed, then stages redux.exe, README.md and LICENSE
    into dist\ alongside a redux-<version>-win-x64.zip of those three files.

    The version is read from <Version> in redux.csproj, which is also what the exe banner
    prints, so the two can never drift.

.EXAMPLE
    .\publish.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$csprojPath = Join-Path $root 'redux.csproj'
$distDir    = Join-Path $root 'dist'
$stageDir   = Join-Path $root 'obj\publish\win-x64'

function Fail($message) {
    Write-Host ''
    Write-Host "PUBLISH FAILED: $message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- preflight --

if (-not (Test-Path -LiteralPath $csprojPath)) {
    Fail "Could not find redux.csproj at '$csprojPath'."
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Fail 'The .NET SDK was not found on PATH. Install the .NET 8 SDK from https://dotnet.microsoft.com/download and try again.'
}

# ------------------------------------------------- version (single source) --

$csprojText = Get-Content -LiteralPath $csprojPath -Raw
$versionMatch = [regex]::Match($csprojText, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $versionMatch.Success) {
    Fail "No <Version> element found in redux.csproj. Add one (e.g. <Version>0.3.0</Version>) so the release can be named."
}
$version = $versionMatch.Groups[1].Value

Write-Host ''
Write-Host "REDUX release publish - version $version (win-x64, self-contained)"
Write-Host ''

# ------------------------------------------------------------------ publish --

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

Write-Host 'Running dotnet publish...'
& dotnet publish $csprojPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $stageDir

if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish exited with code $LASTEXITCODE. See the build output above."
}

$exeSource = Join-Path $stageDir 'redux.exe'
if (-not (Test-Path -LiteralPath $exeSource)) {
    Fail "dotnet publish reported success but '$exeSource' was not produced."
}

# -------------------------------------------------------------------- stage --

$payload = @(
    @{ Path = $exeSource;                    Name = 'redux.exe' },
    @{ Path = (Join-Path $root 'README.md'); Name = 'README.md' },
    @{ Path = (Join-Path $root 'LICENSE');   Name = 'LICENSE'   }
)

foreach ($item in $payload) {
    if (-not (Test-Path -LiteralPath $item.Path)) {
        Fail "Required release file '$($item.Name)' was not found at '$($item.Path)'."
    }
}

if (Test-Path -LiteralPath $distDir) {
    Remove-Item -LiteralPath $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distDir | Out-Null

$staged = @()
foreach ($item in $payload) {
    $target = Join-Path $distDir $item.Name
    Copy-Item -LiteralPath $item.Path -Destination $target -Force
    $staged += $target
}

# ---------------------------------------------------------------------- zip --

$zipPath = Join-Path $distDir "redux-$version-win-x64.zip"

# The freshly copied exe can still be briefly locked by on-access antivirus scanning,
# so retry the archive step a few times before giving up.
$zipped   = $false
$lastZipError = $null
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Compress-Archive -LiteralPath $staged -DestinationPath $zipPath -CompressionLevel Optimal -Force -ErrorAction Stop
        $zipped = $true
        break
    } catch {
        $lastZipError = $_
        if ($attempt -lt 5) {
            Write-Host "  Archive attempt $attempt failed (file locked?), retrying..."
            Start-Sleep -Seconds 2
        }
    }
}

if (-not $zipped) {
    Fail "Could not create '$zipPath': $lastZipError"
}

if (-not (Test-Path -LiteralPath $zipPath)) {
    Fail "Failed to create '$zipPath'."
}

# ------------------------------------------------------------------ summary --

$exeInfo = Get-Item -LiteralPath (Join-Path $distDir 'redux.exe')
$zipInfo = Get-Item -LiteralPath $zipPath
$exeMB   = [math]::Round($exeInfo.Length / 1MB, 2)
$zipMB   = [math]::Round($zipInfo.Length / 1MB, 2)

Write-Host ''
Write-Host 'Publish succeeded.' -ForegroundColor Green
Write-Host "  Version   : $version"
Write-Host "  Output dir: $distDir"
Write-Host "  redux.exe : $exeMB MB ($($exeInfo.Length) bytes)"
Write-Host "  Zip       : $zipPath ($zipMB MB)"
Write-Host ''
exit 0
