#!/usr/bin/env bash
# SessionStart hook (.claude/settings.json): one note in Claude's context saying whether the
# platform — igf_payroll, which owns the contract — moved since platform/ was written.
# Its plain stdout is what Claude reads. It never blocks a session: every path exits 0.
#   PLATFORM_REPO  where igf_payroll is (default /home/igf/igf_payroll)
repo="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
platform_repo="${PLATFORM_REPO:-/home/igf/igf_payroll}"
handoff="$platform_repo/scripts/agent-handoff.mjs"

# The comparison lives in igf_payroll, beside the contract it hashes: one implementation.
if [ -f "$handoff" ] && command -v node >/dev/null 2>&1; then
  if node "$handoff" --status --target "$repo" 2>/dev/null; then
    exit 0
  fi
fi

# No igf_payroll here (or no node): say what the snapshot is, and that freshness is unknown.
source_json="$repo/platform/source.json"
if [ ! -f "$source_json" ]; then
  echo "[platform] igf_payroll was not found at $platform_repo and platform/ is empty: the contract is unknown here. Ask the user before any protocol work."
  exit 0
fi
protocol="$(sed -n 's/.*"protocol": *\([0-9]*\).*/\1/p' "$source_json" | head -1)"
exported="$(sed -n 's/.*"exportedAt": *"\([0-9-]*\)T.*/\1/p' "$source_json" | head -1)"
integrity="intact"
if ! (cd "$repo/platform" && sha256sum --quiet --check SHA256SUMS >/dev/null 2>&1); then
  integrity="EDITED BY HAND — it is generated, never edit it"
fi
echo "[platform] igf_payroll was not found at $platform_repo: working from platform/ (contract v${protocol:-?}, exported ${exported:-?}, $integrity). Whether the platform moved since cannot be told from here."
exit 0
