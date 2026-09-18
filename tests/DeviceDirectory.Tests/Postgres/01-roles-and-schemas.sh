#!/usr/bin/env bash
# Copy of infra/postgres/init/01-roles-and-schemas.sh (phases/phase-2c-infra-and-compose.md §4), embedded so the
# Testcontainers database has the same roles and schemas as compose. Keep in sync with the infra file.
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-SQL
  CREATE ROLE devdir_user LOGIN PASSWORD '${DEVDIR_DB_PASSWORD}';
  CREATE ROLE vuln_user   LOGIN PASSWORD '${VULN_DB_PASSWORD}';
  CREATE SCHEMA device_directory AUTHORIZATION devdir_user;
  CREATE SCHEMA vulnerability    AUTHORIZATION vuln_user;
  REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
