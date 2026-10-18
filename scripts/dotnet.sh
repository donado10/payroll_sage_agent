#!/usr/bin/env bash
# The .NET CLI, in a container: no SDK is installed on the development machine.
#   scripts/dotnet.sh build PayrollSageAgent.slnx
#   scripts/dotnet.sh test PayrollSageAgent.slnx
#   scripts/dotnet.sh run --project src/PayrollSageAgent.Service
# The repo is mounted at /repo and the container runs as YOUR uid, so bin/ and obj/ stay yours.
# Packages are cached in .nuget-cache/ (gitignored). See docs/project-book/02-development/.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${DOTNET_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
mkdir -p "$repo/.nuget-cache"

# -i keeps stdin open (add-binding --sql-password-stdin reads it); a terminal adds -t.
tty=(-i)
if [ -t 0 ] && [ -t 1 ]; then tty=(-it); fi

# DOTNET_DOCKER_NETWORK=host: the agent, run here, reaches this machine's ports as « localhost » — the
# platform's dev server and the test SQL Server (test/sage). Off by default: a build needs no network
# but NuGet's.
net=()
if [ -n "${DOTNET_DOCKER_NETWORK:-}" ]; then net=(--network "$DOTNET_DOCKER_NETWORK"); fi

# The agent's own switches, passed through when set: its home (inside the repo: /repo/.agent-home,
# gitignored) and the development secret store (never on a customer's machine).
agent_env=()
for name in PAYROLL_AGENT_HOME PAYROLL_AGENT_DEV_SECRETS; do
  if [ -n "${!name:-}" ]; then agent_env+=(-e "$name=${!name}"); fi
done

exec docker run --rm --init "${tty[@]}" "${net[@]}" "${agent_env[@]}" \
  --user "$(id -u):$(id -g)" \
  -v "$repo:/repo" -w /repo \
  -e HOME=/tmp \
  -e DOTNET_CLI_HOME=/tmp \
  -e NUGET_PACKAGES=/repo/.nuget-cache/packages \
  -e NUGET_HTTP_CACHE_PATH=/repo/.nuget-cache/http \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -e DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
  -e DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK=1 \
  "$image" dotnet "$@"
