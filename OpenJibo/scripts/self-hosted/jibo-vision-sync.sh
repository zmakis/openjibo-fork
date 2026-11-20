#!/usr/bin/env bash
# Copies Jibo's newest full-size photo into the OpenJibo server, only when a new one appears,
# keeping the photo's real time so the server knows how fresh it is ("what do you see").
# Needs password-less SSH from this machine to the robot (root@ROBOT). Install as a service
# with install-vision-sync.sh.
set -uo pipefail
ROBOT_HOST="${ROBOT_HOST:-root@192.168.13.185}"
PHOTO_DIR="${PHOTO_DIR:-/opt/jibo/Photos/cache}"
LOCAL_TMP="${LOCAL_TMP:-$HOME/.jibo-vision-latest.jpg}"
CONTAINER="${CONTAINER:-openjibo-api-1}"
CONTAINER_PATH="${CONTAINER_PATH:-/data/vision-latest.jpg}"
INTERVAL_SECONDS="${INTERVAL_SECONDS:-3}"
last=""
while true; do
  # Newest few photos as "epoch size name". Each picture is saved in 3 sizes in the same second.
  # The robot's busybox has no stat -c, so use date -r and ls -l.
  list=$(ssh -o BatchMode=yes -o ConnectTimeout=5 "$ROBOT_HOST" \
    "cd '$PHOTO_DIR' 2>/dev/null && for f in \$(ls -t *.jpg 2>/dev/null | head -n 3); do echo \"\$(date -r \"\$f\" +%s) \$(ls -l \"\$f\" | awk '{print \$5}') \$f\"; done" 2>/dev/null)
  if [[ -n "$list" ]]; then
    newest=$(echo "$list" | sort -k1,1nr | head -n1 | cut -d' ' -f1)
    # Among the files from that moment, the biggest is the full-size photo.
    pick=$(echo "$list" | awk -v t="$newest" '$1>=t-2' | sort -k2,2nr | head -n1)
    epoch=$(echo "$pick" | cut -d' ' -f1)
    name=$(echo "$pick" | cut -d' ' -f3)
    if [[ -n "$name" && -n "$epoch" && "$name" != "$last" ]]; then
      if scp -o BatchMode=yes -o ConnectTimeout=5 -q "$ROBOT_HOST:$PHOTO_DIR/$name" "$LOCAL_TMP" 2>/dev/null &&
         docker cp "$LOCAL_TMP" "$CONTAINER:$CONTAINER_PATH" >/dev/null 2>&1 &&
         docker exec "$CONTAINER" touch -d "@$epoch" "$CONTAINER_PATH" >/dev/null 2>&1; then
        last="$name"
        echo "$(date '+%F %T') copied $name ($(date -d "@$epoch" '+%H:%M:%S'))"
      fi
    fi
  fi
  sleep "$INTERVAL_SECONDS"
done
