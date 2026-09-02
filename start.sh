#!/bin/sh
set -eu

PORT="${PORT:-10000}"

case "$PORT" in
  ''|*[!0-9]*)
    echo "NovaSparx: PORT must be numeric." >&2
    exit 64
    ;;
esac

if [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
  echo "NovaSparx: PORT must be between 1 and 65535." >&2
  exit 64
fi

# Docker deployment: published files live in /app.
# Local/Buildpack fallback: published files may live in bin/publish.
APP_DIR="/app"

if [ ! -x "$APP_DIR/NovaSparx.Backend" ] && [ ! -f "$APP_DIR/NovaSparx.Backend.dll" ]; then
  if [ -x "./bin/publish/NovaSparx.Backend" ] || [ -f "./bin/publish/NovaSparx.Backend.dll" ]; then
    APP_DIR="$(pwd)/bin/publish"
  elif [ -x "/workspace/source/bin/publish/NovaSparx.Backend" ] || [ -f "/workspace/source/bin/publish/NovaSparx.Backend.dll" ]; then
    APP_DIR="/workspace/source/bin/publish"
  fi
fi

cd "$APP_DIR"

export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"

if [ -x "./NovaSparx.Backend" ]; then
  exec ./NovaSparx.Backend --urls "http://0.0.0.0:${PORT}"
fi

if [ -f "./NovaSparx.Backend.dll" ]; then
  exec dotnet ./NovaSparx.Backend.dll --urls "http://0.0.0.0:${PORT}"
fi

echo "NovaSparx: published backend executable was not found in $APP_DIR." >&2
exit 70
