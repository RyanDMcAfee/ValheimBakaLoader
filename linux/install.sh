#!/bin/sh
# Installs BakaLoader as a systemd service on a Linux box.
#
#   sudo linux/install.sh [path to the unpacked linux-x64 build]
#
# What it does, and nothing else: makes the valheim user and group, lays out
# /opt/bakaloader/current for the binaries and /var/lib/bakaloader for the data, copies the
# build and the unit in, enables the unit and starts it. Run it again after unpacking a new
# build and it replaces the binaries and restarts the service; the data folder is never
# touched. It prints the setup token out of the journal at the end, because the first login
# needs it and finding it by hand is the step people miss.

set -eu

BUILD_DIR="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
UNIT_SRC="$(cd "$(dirname "$0")" && pwd)/bakaloader.service"
INSTALL_DIR=/opt/bakaloader
DATA_DIR=/var/lib/bakaloader
SERVICE_USER=valheim

if [ "$(id -u)" -ne 0 ]; then
  echo "run this as root: sudo $0 $*" >&2
  exit 1
fi

if [ ! -f "$BUILD_DIR/ValheimBakaLoader.Service" ] && [ ! -f "$BUILD_DIR/ValheimBakaLoader.Service.dll" ]; then
  echo "no BakaLoader build in $BUILD_DIR (expected ValheimBakaLoader.Service there)" >&2
  exit 1
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --shell /usr/sbin/nologin --user-group "$SERVICE_USER"
  echo "made the $SERVICE_USER user"
fi

mkdir -p "$INSTALL_DIR" "$DATA_DIR"
# A fresh copy beside the old one and a symlink swap, so a running service keeps its files
# until the restart and a bad build is one 'ln' away from being rolled back.
STAMP="$(date +%Y%m%d%H%M%S)"
NEW_DIR="$INSTALL_DIR/$STAMP"
mkdir -p "$NEW_DIR"
cp -R "$BUILD_DIR"/. "$NEW_DIR"/
chown -R root:"$SERVICE_USER" "$NEW_DIR"
chmod -R a+rX "$NEW_DIR"
[ -f "$NEW_DIR/ValheimBakaLoader.Service" ] && chmod a+x "$NEW_DIR/ValheimBakaLoader.Service"
ln -sfn "$NEW_DIR" "$INSTALL_DIR/current"

chown -R "$SERVICE_USER":"$SERVICE_USER" "$DATA_DIR"
chmod 750 "$DATA_DIR"

cp "$UNIT_SRC" /etc/systemd/system/bakaloader.service
systemctl daemon-reload
systemctl enable bakaloader.service >/dev/null

if systemctl is-active --quiet bakaloader.service; then
  systemctl restart bakaloader.service
  echo "restarted bakaloader.service on the new build"
else
  systemctl start bakaloader.service
  echo "started bakaloader.service"
fi

sleep 2
echo
echo "The service listens on 127.0.0.1:8471 by default; reach it through an SSH tunnel:"
echo "  ssh -L 8471:127.0.0.1:8471 you@this-box   then open http://127.0.0.1:8471/"
echo
if journalctl -u bakaloader.service --since "-1min" --no-pager 2>/dev/null | grep -q "setup token"; then
  echo "No password is set yet. The one-time setup token for the login page:"
  journalctl -u bakaloader.service --since "-1min" --no-pager | grep "setup token" | tail -1
  echo "Or set one now with:  sudo -u $SERVICE_USER BAKALOADER_DATA_DIR=$DATA_DIR $INSTALL_DIR/current/ValheimBakaLoader.Service passwd"
fi
