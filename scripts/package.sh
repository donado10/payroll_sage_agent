#!/usr/bin/env bash
# The Windows deliverables of one version, in dist/:
#   PayrollSageAgent-<v>-x64.msi           the installer (recommended): service, rights, upgrade, uninstall
#   PayrollSageAgent-<v>-win-x64.zip       the dev fallback: the same agent, with install.ps1 / uninstall.ps1
#   PayrollSageAgent-<v>.release.json      version, protocol, signed or not, each file's SHA-256 (+ .sha256 files)
#
# The agent is published here, in the SDK container (win-x64, self-contained, one file); everything after
# that — signing, the MSI, the zip — runs in the packaging container (scripts/packaging.Dockerfile, via
# scripts/lib/installers.py). Signing is OFF unless SIGNING_ENABLED=1 (scripts/sign.sh says how to switch
# it on). Nothing here runs on Windows, and nothing here proves the result there. dist/ is gitignored.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
protocol=$(sed -n 's/.*public const int Version = \([0-9]*\);.*/\1/p' src/PayrollSageAgent.Core/AgentProtocol.cs)
[[ -n "$version" && -n "$protocol" ]] || { echo "package: version or protocol not found." >&2; exit 1; }
name="PayrollSageAgent-${version}-win-x64"
stage="dist/$name"
rm -rf "$stage" "dist/msi-build" dist/PayrollSageAgent-"$version"[.-]*
mkdir -p "$stage"

echo "package: publishing $name…"
scripts/dotnet.sh publish src/PayrollSageAgent.Service -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
  -o "/repo/$stage/app" --nologo -v quiet

image=payroll-agent-packaging:local
docker build -q -t "$image" -f scripts/packaging.Dockerfile scripts >/dev/null

signing=()
if [[ "${SIGNING_ENABLED:-0}" == "1" && -n "${SIGNING_DIR:-}" ]]; then
  signing=(-v "$(cd "$SIGNING_DIR" && pwd):/signing:ro")
fi
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -v "$repo:/repo" -w /repo "${signing[@]}" \
  -e SIGNING_ENABLED="${SIGNING_ENABLED:-0}" -e SIGNER="${SIGNER:-osslsigncode}" \
  -e SIGNING_COMMAND="${SIGNING_COMMAND:-}" -e SIGNING_TIMESTAMP_URL="${SIGNING_TIMESTAMP_URL:-}" \
  "$image" python3 /repo/scripts/lib/installers.py "/repo/$stage" "$version" "$protocol"
rm -rf dist/msi-build
