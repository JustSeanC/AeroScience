#!/bin/sh
set -eu
# First-initialization only. Generated hexadecimal credentials cannot alter SQL syntax.
case "${AEROSCIENCE_APPLICATION_DATABASE_PASSWORD:-}" in *[!a-f0-9]*|'') exit 1;; esac
case "${AEROSCIENCE_RECOVERY_DATABASE_PASSWORD:-}" in *[!a-f0-9]*|'') exit 1;; esac
[ "${#AEROSCIENCE_APPLICATION_DATABASE_PASSWORD}" = 64 ]
[ "${#AEROSCIENCE_RECOVERY_DATABASE_PASSWORD}" = 64 ]
[ "$POSTGRES_DB" = aeroscience ]
# Secrets go to PostgreSQL through private stdin, never process arguments, public responses or echo commands.
psql -X -q --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 <<SQL
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
CREATE ROLE aeroscience_application LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$AEROSCIENCE_APPLICATION_DATABASE_PASSWORD';
CREATE ROLE aeroscience_recovery LOGIN NOSUPERUSER CREATEDB NOCREATEROLE PASSWORD '$AEROSCIENCE_RECOVERY_DATABASE_PASSWORD';
ALTER DATABASE aeroscience OWNER TO aeroscience_application;
REVOKE ALL ON DATABASE aeroscience FROM PUBLIC;
GRANT CONNECT ON DATABASE aeroscience TO aeroscience_application;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SQL
