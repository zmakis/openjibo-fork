#!/usr/bin/env bash
# Installs jibo-vision-sync.sh as a systemd service (run once with sudo, from your normal user):
#   sudo bash scripts/self-hosted/install-vision-sync.sh
# Runs as the user who called sudo (needs docker access and password-less SSH to the robot).
set -eu
RUN_AS="${SUDO_USER:-$(id -un)}"
SCRIPT="$(cd "$(dirname "$0")" && pwd)/jibo-vision-sync.sh"
chmod +x "$SCRIPT"
cat > /etc/systemd/system/jibo-vision-sync.service <<UNIT
[Unit]
Description=Copy Jibo's newest photo to the OpenJibo server
After=docker.service network-online.target

[Service]
User=$RUN_AS
ExecStart=$SCRIPT
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable jibo-vision-sync
systemctl restart jibo-vision-sync
echo "jibo-vision-sync installed (runs as $RUN_AS): $SCRIPT"
echo "Check: journalctl -u jibo-vision-sync --no-pager | tail -3"
