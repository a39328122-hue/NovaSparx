#!/bin/sh
set -eu

PORT="${PORT:-10000}"

case "$PORT" in
  ''|*[!0-9]*)
    echo "NovaSparx: PORT must be a numeric TCP port." >&2
    exit 64
    ;;
esac

if [ "$PORT" -lt 1 ] || [ "$PORT" -gt 65535 ]; then
  echo "NovaSparx: PORT must be between 1 and 65535." >&2
  exit 64
fi

cd /app

export ASPNETCORE_URLS="http://0.0.0.0:${PORT}"

# Prefer the Linux apphost emitted by dotnet publish.
# Keep the DLL fallback so the same image still starts if apphost generation is
# disabled by a future project setting.
if [ -x "./NovaSparx.Backend" ]; then
  exec ./NovaSparx.Backend --urls "http://0.0.0.0:${PORT}"
fi

if [ -f "./NovaSparx.Backend.dll" ]; then
  exec dotnet ./NovaSparx.Backend.dll --urls "http://0.0.0.0:${PORT}"
fi

echo "NovaSparx: published backend executable was not found in /app." >&2
exit 70
