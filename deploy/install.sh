#!/usr/bin/env bash
# Install MapleKiosk as a systemd service on Linux.
# Usage (run as root):   sudo ./deploy/install.sh
set -euo pipefail

APP_NAME="maplekiosk"
APP_DIR="/var/www/${APP_NAME}"
SERVICE_SRC="$(dirname "$0")/${APP_NAME}.service"
SERVICE_DST="/etc/systemd/system/${APP_NAME}.service"
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ $EUID -ne 0 ]]; then
  echo "Must be run as root (try: sudo $0)" >&2
  exit 1
fi

command -v dotnet >/dev/null || { echo "dotnet SDK/runtime not found in PATH"; exit 1; }

echo "==> Publishing to ${APP_DIR}"
mkdir -p "${APP_DIR}"
dotnet publish "${PROJECT_ROOT}/MapleKiosk.Web.csproj" \
  -c Release \
  -o "${APP_DIR}" \
  --nologo

echo "==> Setting ownership (www-data)"
id -u www-data >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin www-data
chown -R www-data:www-data "${APP_DIR}"

echo "==> Installing systemd unit"
cp "${SERVICE_SRC}" "${SERVICE_DST}"
systemctl daemon-reload
systemctl enable "${APP_NAME}.service"
systemctl restart "${APP_NAME}.service"

echo "==> Status"
systemctl --no-pager status "${APP_NAME}.service" || true
echo
echo "Done. App listening on http://0.0.0.0:5500"
echo "Logs: journalctl -u ${APP_NAME} -f"
