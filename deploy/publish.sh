#!/usr/bin/env bash
# Publish MapleKiosk to a remote Linux host via SSH + rsync.
#
# Usage:    ./deploy/publish.sh user@host [remote-path]
# Example:  ./deploy/publish.sh eric@maplekiosk.ca
#
# Assumes:
#   - passwordless SSH to the target
#   - sudo without password on the target (for restart) — or adjust SUDO below
#   - target has .NET 8 runtime installed
#   - /etc/systemd/system/maplekiosk.service already installed
#     (see deploy/install.sh for first-time setup)
set -euo pipefail

TARGET="${1:-}"
REMOTE_DIR="${2:-/var/www/maplekiosk}"
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOCAL_PUB="${PROJECT_ROOT}/bin/Release/net8.0/publish"
SUDO="sudo"

if [[ -z "${TARGET}" ]]; then
  echo "Usage: $0 user@host [remote-path]" >&2
  exit 1
fi

echo "==> [1/4] Publishing Release build"
rm -rf "${LOCAL_PUB}"
dotnet publish "${PROJECT_ROOT}/MapleKiosk.Web.csproj" \
  -c Release \
  -o "${LOCAL_PUB}" \
  --nologo

echo "==> [2/4] Ensuring ${REMOTE_DIR} exists on ${TARGET}"
ssh "${TARGET}" "${SUDO} mkdir -p '${REMOTE_DIR}' && ${SUDO} chown -R \$USER '${REMOTE_DIR}'"

echo "==> [3/4] rsync to ${TARGET}:${REMOTE_DIR}"
rsync -avz --delete \
  --exclude 'appsettings.Development.json' \
  "${LOCAL_PUB}/" "${TARGET}:${REMOTE_DIR}/"

echo "==> [4/4] Restart maplekiosk.service"
ssh "${TARGET}" "${SUDO} chown -R eric:eric '${REMOTE_DIR}' && ${SUDO} systemctl restart maplekiosk && ${SUDO} systemctl --no-pager -l status maplekiosk | head -20"

echo
echo "Done. Tail logs with:  ssh ${TARGET} 'sudo journalctl -u maplekiosk -f'"
