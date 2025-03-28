#!/bin/bash

set -e
set -u

function create_user_and_database() {
	local database=$1
	echo "Creating user and database '$database'"
	psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-EOSQL
	   CREATE USER $database WITH PASSWORD 'umami';
	   CREATE DATABASE $database;
	   GRANT ALL PRIVILEGES ON DATABASE $database TO $database;

	   -- Switch to the new database
	   \c $database

	   -- Ensure the user owns the public schema
	   ALTER SCHEMA public OWNER TO $database;
	   GRANT USAGE, CREATE ON SCHEMA public TO $database;
	   GRANT ALL PRIVILEGES ON SCHEMA public TO $database;
EOSQL
}

if [ -n "$POSTGRES_MULTIPLE_DATABASES" ]; then
	echo "Multiple database creation requested: $POSTGRES_MULTIPLE_DATABASES"
	for db in $(echo $POSTGRES_MULTIPLE_DATABASES | tr ',' ' '); do
		if [ "$db" != "$POSTGRES_DB" ]; then
			create_user_and_database $db
		fi
	done
	echo "Multiple databases created"
fi

