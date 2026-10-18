#!/usr/bin/env bash
# Build the service, then run it in the SDK container. Ctrl+C stops it — and the container with it.
# It runs the built dll rather than `dotnet run`: in the container `dotnet run` does not pass the
# signal on to the app, so Ctrl+C left the container running (measured 2026-10-17).
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Paths are the CONTAINER's: dotnet.sh mounts the repo at /repo and works there.
"$repo/scripts/dotnet.sh" build src/PayrollSageAgent.Service --nologo -v quiet
exec "$repo/scripts/dotnet.sh" src/PayrollSageAgent.Service/bin/Debug/net10.0/PayrollSageAgent.dll "$@"
