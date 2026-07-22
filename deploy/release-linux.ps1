<#
.SYNOPSIS
    Build and release MapleKiosk.Web to the Phoebus Linux host (192.168.2.5).

.DESCRIPTION
    Publishes MapleKiosk.Web (self-contained, linux-x64 - same flags as
    .github/workflows/build.yml), ships the published output to the host, and
    restarts the systemd service. NO sudo is required at any step.

    Why self-contained: the host's dotnet install is split-brained (runtime under
    /usr/lib/dotnet, host/fxr under /usr/share/dotnet - neither a complete root), so
    a framework-dependent apphost can't resolve a runtime and crash-loops with
    "No frameworks were found" (exit 150). Bundling the runtime makes the release
    self-sufficient.

    Why no sudo: the install dir (/var/www/maplekiosk_www/app) is eric-writable
    (drwxrwsrwx eric:eric), so files are placed without elevation. The service runs
    as eric with Restart=always, so a plain kill of MainPID makes systemd relaunch
    on the new binary (~6s, brief downtime) - no interactive sudo password needed.

    The transfer is safe against the running apphost: the remote script extracts to a
    staging dir, then host-side rsync writes-temp-then-renames each file (new inode),
    so the live process is never overwritten in place ("text file busy").

.EXAMPLE
    pwsh ./deploy/release-linux.ps1 -Version 1.05
#>
[CmdletBinding()]
param(
    # Version stamped into the assembly (workflow scheme is 1.0N, e.g. 1.05).
    [string]$Version = "1.0.0",

    [string]$RemoteHost = "192.168.2.5",
    [string]$User       = "eric",

    # SSH private key. Matches the deploy notes (eric@192.168.2.5, id_ed25519).
    [string]$SshKey     = "$HOME\.ssh\id_ed25519",

    # Remote layout - keep in lockstep with maplekiosk-www.service.
    [string]$Service    = "maplekiosk-www",
    [string]$InstallDir = "/var/www/maplekiosk_www/app",
    [string]$AppExe     = "MapleKiosk.Web"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# --- Paths -------------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "MapleKiosk.Web.csproj"
$PublishDir  = Join-Path $RepoRoot "bin\Release\publish-phoebus"

if (-not (Test-Path $ProjectPath)) { throw "Project not found: $ProjectPath" }
if (-not (Test-Path $SshKey))      { throw "SSH key not found: $SshKey" }

$Target = "$User@$RemoteHost"
$sshCommon = @("-i", $SshKey, "-o", "StrictHostKeyChecking=accept-new")

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# --- 1. Publish (self-contained, linux-x64) ----------------------------------
Step "Publishing $AppExe  (Version=$Version, linux-x64, self-contained=true)"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

dotnet publish $ProjectPath `
    --configuration Release `
    --runtime linux-x64 `
    --self-contained true `
    "/p:Version=$Version" `
    "/p:AssemblyVersion=$Version.0" `
    "/p:FileVersion=$Version.0" `
    --output $PublishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# --- 2. Pack the published output --------------------------------------------
# Pack locally with the bundled bsdtar (tar.exe ships with Windows 10+); gzip keeps
# the transfer small.
Step "Packing release archive"
$TarLocal = Join-Path $env:TEMP "maplekiosk-deploy.tar.gz"
if (Test-Path $TarLocal) { Remove-Item -Force $TarLocal }
tar.exe -C $PublishDir -czf $TarLocal "."
if ($LASTEXITCODE -ne 0) { throw "tar failed (exit $LASTEXITCODE)" }

# --- 3. Build the remote deploy script (no sudo) -----------------------------
# Extract to a staging dir, rsync into the eric-writable install dir (write-temp-then-
# rename is safe against the running apphost), then restart by killing MainPID -
# systemd (Restart=always) relaunches on the new binary. Development.json is excluded
# so the host's copy (owned eric:spc) is preserved.
$remoteScript = @"
set -euo pipefail

INSTALL='$InstallDir'
SERVICE='$Service'
APP='$AppExe'
TARBALL='/tmp/maplekiosk-deploy.tar.gz'
STAGING='/tmp/maplekiosk-deploy-staging'

rm -rf "`$STAGING"
mkdir -p "`$STAGING"
tar -C "`$STAGING" -xzf "`$TARBALL"

echo 'Syncing into install dir'
mkdir -p "`$INSTALL"
rsync -rlptv --delete --exclude 'appsettings.Development.json' "`$STAGING/" "`$INSTALL/"
chmod +x "`$INSTALL/`$APP"

echo 'Restarting service (kill MainPID; Restart=always relaunches)'
PID=`$(systemctl show "`$SERVICE" -p MainPID --value)
if [ -n "`$PID" ] && [ "`$PID" != "0" ]; then
    kill "`$PID" || true
else
    echo "WARN: no MainPID for `$SERVICE (not running?); systemd should start it if enabled" >&2
fi

# Wait for systemd to relaunch (RestartSec=5) and report status.
sleep 8
echo '--- status ---'
systemctl is-active "`$SERVICE"
systemctl --no-pager --lines=15 status "`$SERVICE" || true

rm -rf "`$STAGING" "`$TARBALL"
"@

# Normalize to LF so bash on the host parses it cleanly.
$remoteScript = $remoteScript -replace "`r`n", "`n"
$ScriptLocal  = Join-Path $env:TEMP "maplekiosk-deploy.sh"
[IO.File]::WriteAllText($ScriptLocal, $remoteScript)

# --- 4. Ship artifact + script, then execute --------------------------------
try {
    Step "Copying archive to $Target"
    scp @sshCommon $TarLocal "${Target}:/tmp/maplekiosk-deploy.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "scp (archive) failed (exit $LASTEXITCODE)" }

    Step "Copying deploy script to $Target"
    scp @sshCommon $ScriptLocal "${Target}:/tmp/maplekiosk-deploy.sh"
    if ($LASTEXITCODE -ne 0) { throw "scp (script) failed (exit $LASTEXITCODE)" }

    Step "Deploying + restarting $Service on $RemoteHost"
    ssh @sshCommon $Target "bash /tmp/maplekiosk-deploy.sh; rm -f /tmp/maplekiosk-deploy.sh"
    if ($LASTEXITCODE -ne 0) { throw "Remote deploy failed (exit $LASTEXITCODE)" }

    Write-Host ""
    Step "Released v$Version to $RemoteHost - $Service restarted."
}
finally {
    # Scrub local temp artifacts.
    Remove-Item -Force $ScriptLocal -ErrorAction SilentlyContinue
    Remove-Item -Force $TarLocal    -ErrorAction SilentlyContinue
}
