#!/usr/bin/env bash
# Runs once, on the first start of an empty `pgdata` volume (docker-entrypoint-initdb.d).
# One role per subgraph, one schema per role: each role owns exactly its schema and has no USAGE
# on the other, so neither subgraph can read the other's tables. Migrations run as the owning role.
# Changed this file? `scripts/reset.sh` (docker compose down -v) so it runs again.
set -euo pipefail

: "${DEVDIR_DB_PASSWORD:?DEVDIR_DB_PASSWORD must be set (see .env)}"
: "${VULN_DB_PASSWORD:?VULN_DB_PASSWORD must be set (see .env)}"

# Passwords go in as psql variables (:'name' quotes them), never through shell interpolation.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v devdir_pw="$DEVDIR_DB_PASSWORD" -v vuln_pw="$VULN_DB_PASSWORD" <<'SQL'
  CREATE ROLE devdir_user LOGIN PASSWORD :'devdir_pw';
  CREATE ROLE vuln_user   LOGIN PASSWORD :'vuln_pw';
  CREATE SCHEMA device_directory AUTHORIZATION devdir_user;
  CREATE SCHEMA vulnerability    AUTHORIZATION vuln_user;
  -- The default search_path ("$user", public) resolves to `public` for these roles.
  -- Pin each role to its own schema so unqualified names (psql, the EF history table) land there
  -- even without `Search Path=` in the connection string.
  ALTER ROLE devdir_user SET search_path = device_directory;
  ALTER ROLE vuln_user   SET search_path = vulnerability;
  -- Already the default since PostgreSQL 15; explicit so nobody can create objects in public.
  REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
