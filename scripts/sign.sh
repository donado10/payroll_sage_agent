#!/usr/bin/env bash
# The ONE signing step (Phase 8). Signs FILE in place — or, as long as SIGNING_ENABLED is not 1, leaves it
# unchanged and SAYS it is not signed. Nothing else in the build knows how signing is done.
#
#   SIGNING_ENABLED=1            switch it on (off by default: the dev phase, no certificate bought yet)
#   SIGNER=osslsigncode          the default signer: a PKCS#12 certificate, from SIGNING_DIR (mounted at
#                                /signing): signing.pfx + signing.password (the password, in a file)
#   SIGNER=command               any other signer — a hardware token, a cloud key (jsign, …):
#                                SIGNING_COMMAND is run with the file as $1 and must sign it IN PLACE
#                                (its tool must be in scripts/packaging.Dockerfile)
#   SIGNING_TIMESTAMP_URL        optional RFC 3161 timestamp server, so a signature outlives its certificate
#
# Run inside the packaging container by scripts/lib/installers.py, never by hand.
set -euo pipefail
file=$1
name=$(basename "$file")

if [[ "${SIGNING_ENABLED:-0}" != "1" ]]; then
  echo "sign: $name — NON SIGNÉ (SIGNING_ENABLED n'est pas 1 : version de développement)"
  exit 0
fi

case "${SIGNER:-osslsigncode}" in
  osslsigncode)
    pfx=/signing/signing.pfx
    pass=/signing/signing.password
    [[ -f $pfx && -f $pass ]] || { echo "sign: $pfx and $pass are required (SIGNING_DIR)" >&2; exit 1; }
    args=(sign -pkcs12 "$pfx" -readpass "$pass" -n "IGF Paie - Agent Sage" -h sha256 -in "$file" -out "$file.signed")
    [[ -n "${SIGNING_TIMESTAMP_URL:-}" ]] && args+=(-ts "$SIGNING_TIMESTAMP_URL")
    osslsigncode "${args[@]}" >/dev/null
    mv "$file.signed" "$file"
    ;;
  command)
    [[ -n "${SIGNING_COMMAND:-}" ]] || { echo "sign: SIGNER=command needs SIGNING_COMMAND" >&2; exit 1; }
    bash -c "$SIGNING_COMMAND" sign "$file"
    ;;
  *)
    echo "sign: unknown SIGNER « ${SIGNER} »" >&2
    exit 1
    ;;
esac
echo "sign: $name — signé (${SIGNER:-osslsigncode})"
