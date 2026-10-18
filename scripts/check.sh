#!/usr/bin/env bash
# The precheck of this repo: what must be green before a change is called done.
#   1. platform/ is exactly what igf_payroll's `npm run agent:handoff` wrote (never edited here)
#   2. formatting   3. build (warnings are errors)   4. tests
# It cannot prove the service installs or runs on Windows — see .claude/rules/verification.md.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"
refresh="npm --prefix /home/igf/igf_payroll run agent:handoff"

echo "1/4 platform/ snapshot"
if [ ! -f platform/SHA256SUMS ]; then
  echo "  ✗ platform/ is empty — run: $refresh" >&2
  exit 1
fi
if ! (cd platform && sha256sum --quiet --check SHA256SUMS); then
  echo "  ✗ a file of platform/ was edited by hand. It is generated: restore it with $refresh" >&2
  echo "    (a change the agent needs belongs in igf_payroll/docs/agent-protocol/, in that repo's session)" >&2
  exit 1
fi
extra="$(comm -23 <(cd platform && find . -type f ! -name SHA256SUMS | sed 's|^\./||' | sort) <(sed 's/^[0-9a-f]*  //' platform/SHA256SUMS | sort))"
if [ -n "$extra" ]; then
  echo "  ✗ files in platform/ that the handoff did not write: $extra — restore with $refresh" >&2
  exit 1
fi
echo "  ✓ intact"

echo "2/4 format"
scripts/dotnet.sh format PayrollSageAgent.slnx --verify-no-changes

echo "3/4 build"
scripts/dotnet.sh build PayrollSageAgent.slnx --nologo

echo "4/4 test"
scripts/dotnet.sh test PayrollSageAgent.slnx --nologo

echo "check: all green."
