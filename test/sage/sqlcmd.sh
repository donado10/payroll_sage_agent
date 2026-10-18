#!/usr/bin/env bash
# sqlcmd inside the test container. The password comes from test/sage/.env and travels as an
# environment variable, never on a command line.
#   test/sage/sqlcmd.sh -Q "SELECT @@VERSION"                               as sa
#   test/sage/sqlcmd.sh -i /work/sql/restore.sql                            test/sage/ is at /work
#   SQL_USER=agent_a SQL_PASSWORD_VAR=AGENT_A_PASSWORD test/sage/sqlcmd.sh -d PRESTIGE_TEXTILE -Q "…"
#   SQL_VARS="Database Backup" Database=X Backup=Y test/sage/sqlcmd.sh -i …  sqlcmd reads scripting
#                                                                             variables from the env
# -C trusts the container's self-signed certificate: TEST ONLY — a real binding verifies it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
set -a; . "$here/.env"; set +a
user="${SQL_USER:-sa}"
var="${SQL_PASSWORD_VAR:-MSSQL_SA_PASSWORD}"
env_args=(-e "SQLCMDPASSWORD=${!var}")
for name in ${SQL_VARS:-}; do env_args+=(-e "$name=${!name}"); done
exec docker exec -i "${env_args[@]}" payroll-sage-sql \
  /opt/mssql-tools18/bin/sqlcmd -S localhost -U "$user" -C -b -W -s '|' "$@"
