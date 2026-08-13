#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build the standalone "gold" Genie 5 plugins and deploy their DLLs into the
  Genie 5 Plugins folder for INTERNAL TESTING.

.DESCRIPTION
  The remaining standalone plugin (InventoryView) is NOT part of the Core
  build. (EXPTracker, SpellTimer and TimeTracker were absorbed into Core as
  built-in trackers and are no longer deployed as plugins; a leftover DLL is
  ignored by the host's load-guard.) Each lives in its own repo under
  github.com/GenieClient and is the
  single source of truth ("gold"). The Core solution (Genie.slnx) and CI never
  compile them — this dev-only script is the path that makes them loadable for
  testing, keeping plugin loading fully decoupled from the Core build.

  For each gold repo it will:
    1. clone it under _refs/ (or `git pull` if already present),
    2. refresh the bundled contract with the CURRENT Genie.Plugins.Abstractions
       so the plugin compiles against the live host API (catches contract drift
       that CI no longer would),
    3. build it in Release, and
    4. copy the resulting Plugin_*V5.dll into the Plugins folder.

  The running app discovers the DLLs on next connect (or Plugins -> Reload).

.PARAMETER PluginsDir
  Override the destination Plugins folder. Defaults to the per-user Genie 5
  Plugins folder for the current OS (matches Genie.Core/Runtime/AppPaths):
    Windows  %APPDATA%\Genie5\Plugins
    macOS    ~/Library/Application Support/Genie5/Plugins
    Linux    $XDG_DATA_HOME/Genie5/Plugins  (or ~/.local/share/Genie5/Plugins)

.PARAMETER SkipClone
  Don't clone/pull the gold repos — build whatever is already under _refs/.

.EXAMPLE
  pwsh scripts/deploy-plugins.ps1
  pwsh scripts/deploy-plugins.ps1 -SkipClone
  pwsh scripts/deploy-plugins.ps1 -PluginsDir 'D:\PortableGenie\Plugins'
#>
[CmdletBinding()]
param(
    [string] $PluginsDir,
    [switch] $SkipClone
)

$ErrorActionPreference = 'Stop'

# Repo root = parent of this script's folder.
$RepoRoot = Split-Path -Parent $PSScriptRoot
$RefsDir  = Join-Path $RepoRoot '_refs'

# The standalone "gold" plugin repos (source of truth) under GenieClient.
# EXPTracker / SpellTimer / TimeTracker are intentionally absent — they're now
# built in to Core (see Genie.Core/Extensions/Builtin).
$Plugins = @(
    @{ Repo = 'Plugin_InventoryViewV5'; Csproj = 'Plugin_InventoryViewV5.csproj' }
)

# ── Resolve the Plugins folder (mirrors Genie.Core AppPaths) ─────────────────
if (-not $PluginsDir) {
    if ($IsWindows) {
        $PluginsDir = Join-Path $env:APPDATA 'Genie5\Plugins'
    } elseif ($IsMacOS) {
        $PluginsDir = Join-Path $HOME 'Library/Application Support/Genie5/Plugins'
    } else {
        $base = $env:XDG_DATA_HOME
        if (-not $base) { $base = Join-Path $HOME '.local/share' }
        $PluginsDir = Join-Path $base 'Genie5/Plugins'
    }
}
New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
Write-Host "Plugins folder : $PluginsDir"

# ── Build the current plugin contract so plugins compile against the live API ─
$AbstractionsProj = Join-Path $RepoRoot 'src/Genie.Plugins.Abstractions/Genie.Plugins.Abstractions.csproj'
Write-Host "Building plugin contract (Genie.Plugins.Abstractions)..."
dotnet build $AbstractionsProj -c Release --nologo -v quiet
$AbstractionsDll = Join-Path $RepoRoot 'src/Genie.Plugins.Abstractions/bin/Release/net10.0/Genie.Plugins.Abstractions.dll'
if (-not (Test-Path $AbstractionsDll)) { throw "Contract DLL not found: $AbstractionsDll" }

# ── Build + deploy each gold plugin ──────────────────────────────────────────
$deployed = @()
foreach ($p in $Plugins) {
    $clone = Join-Path $RefsDir $p.Repo

    if (-not $SkipClone) {
        if (Test-Path $clone) {
            Write-Host "Updating $($p.Repo)..."
            git -C $clone pull --ff-only
        } else {
            Write-Host "Cloning $($p.Repo)..."
            git clone "https://github.com/GenieClient/$($p.Repo).git" $clone
        }
    }
    if (-not (Test-Path $clone)) {
        Write-Warning "Skipping $($p.Repo): no clone at $clone (run without -SkipClone to fetch it)."
        continue
    }

    # Refresh the bundled contract so the plugin builds against the current host API.
    $lib = Join-Path $clone 'lib'
    if (Test-Path $lib) { Copy-Item $AbstractionsDll $lib -Force }

    $csproj = Join-Path $clone $p.Csproj
    if (-not (Test-Path $csproj)) {
        Write-Warning "Skipping $($p.Repo): $($p.Csproj) not found in clone."
        continue
    }

    Write-Host "Building $($p.Repo)..."
    dotnet build $csproj -c Release --nologo -v quiet

    $dllName = [System.IO.Path]::GetFileNameWithoutExtension($p.Csproj) + '.dll'
    # net8.0 here is the EXTERNAL plugin repos' own TargetFramework, not ours —
    # it only changes when those repos retarget, independent of the app's TFM.
    $dll     = Join-Path $clone "bin/Release/net8.0/$dllName"
    if (-not (Test-Path $dll)) {
        Write-Warning "Built but DLL not found: $dll"
        continue
    }

    Copy-Item $dll $PluginsDir -Force
    $deployed += $dllName
    Write-Host "  deployed -> $(Join-Path $PluginsDir $dllName)"
}

Write-Host ""
if ($deployed.Count -gt 0) {
    Write-Host "Deployed $($deployed.Count) plugin(s): $($deployed -join ', ')"
    Write-Host "Reconnect in Genie 5 (or Plugins -> Reload) to load them."
} else {
    Write-Warning "No plugins deployed."
}
