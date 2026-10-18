#!/usr/bin/env bash
# Bring the test Sage instance to a known state, from nothing:
#   - SQL Server 2019 (docker-compose.yml), waited for;
#   - the backup restored TWICE, under two names, so two bindings can be tested on one instance;
#   - one read-only login per database (sql/login.sql), passwords from .env.
# Idempotent: an existing database is left alone unless --replace.
#   test/sage/setup.sh [--replace] [backup-file-name]     (default PRESTIGE_TEXTILE.bak)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

replace=0
backup="PRESTIGE_TEXTILE.bak"
for arg in "$@"; do
  case "$arg" in
    --replace) replace=1 ;;
    *) backup="$arg" ;;
  esac
done
[ -f .env ] || { echo "test/sage/.env is missing — copy .env.example and set the passwords." >&2; exit 1; }
[ -f "backups/$backup" ] || { echo "backups/$backup not found." >&2; exit 1; }

docker compose -f docker-compose.yml up -d >/dev/null
for _ in $(seq 1 60); do
  ./sqlcmd.sh -Q "SELECT 1" >/dev/null 2>&1 && break
  sleep 2
done
./sqlcmd.sh -Q "SELECT 1" >/dev/null || { echo "SQL Server did not become ready." >&2; exit 1; }

base="${backup%.bak}"
declare -A logins=(["$base"]=agent_a ["${base}_B"]=agent_b)
declare -A passwords=(["$base"]=AGENT_A_PASSWORD ["${base}_B"]=AGENT_B_PASSWORD)
set -a; . ./.env; set +a

for database in "$base" "${base}_B"; do
  Database="$database" Backup="/var/opt/mssql/backup/$backup" Replace="$replace" \
    SQL_VARS="Database Backup Replace" ./sqlcmd.sh -i /work/sql/restore.sql
  password_var="${passwords[$database]}"
  Login="${logins[$database]}" Password="${!password_var}" Database="$database" \
    SQL_VARS="Login Password Database" ./sqlcmd.sh -i /work/sql/login.sql
done
echo "setup: $base et ${base}_B prêtes — agent_a / agent_b, lecture seule, une base chacun."
