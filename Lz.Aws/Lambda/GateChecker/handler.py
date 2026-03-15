"""
Gate-checker Lambda: verifies EFS data and database tables exist,
initializes tenant config (database, user, Settings.txt, usersettings.json),
re-writes config files after seeding, and sets up SmartStore admin users
with API credentials.
Deployed inside the VPC with EFS mount and RDS access.
Invoked by AwsTransitionChecker, AwsLambdaConfigInitRunner,
AwsLambdaPostSeedRunner, and AwsLambdaAdminSetupRunner.
"""

import json
import os
import secrets


def handler(event, context):
    """
    Entry point. Expects event:
      { "check_type": "efs" | "database" | "init_config" | "post_seed_config", ... }
    Returns:
      { "passed": bool, "reason": "..." }
    """
    check_type = event.get("check_type")

    try:
        if check_type == "efs":
            return check_efs(event)
        elif check_type == "database":
            return check_database(event)
        elif check_type == "init_config":
            return init_config(event)
        elif check_type == "post_seed_config":
            return post_seed_config(event)
        elif check_type == "diagnose":
            return diagnose_database(event)
        elif check_type == "setup_admin":
            return setup_admin(event)
        else:
            return {"passed": False, "reason": f"Unknown check_type: {check_type}"}
    except Exception as e:
        return {"passed": False, "reason": f"Check failed with error: {str(e)}"}


def check_efs(event):
    """Check that a path on EFS contains files."""
    mount_path = os.environ.get("EFS_MOUNT_PATH", "/mnt/efs")
    relative_path = event.get("path", "")
    full_path = os.path.join(mount_path, relative_path.lstrip("/"))

    if not os.path.exists(full_path):
        return {"passed": False, "reason": f"Path does not exist: {relative_path}"}

    if os.path.isfile(full_path):
        return {"passed": True, "reason": f"File exists: {relative_path}"}

    entries = os.listdir(full_path)
    if len(entries) == 0:
        return {"passed": False, "reason": f"Directory is empty: {relative_path}"}

    return {
        "passed": True,
        "reason": f"Directory contains {len(entries)} entries: {relative_path}",
    }


def check_database(event):
    """Check that a database has tables in the public schema."""
    import pg8000.native

    db_name = event.get("db_name", "")
    if not db_name:
        return {"passed": False, "reason": "No db_name provided"}

    # Read RDS credentials from Secrets Manager
    secret_arn = os.environ.get("RDS_SECRET_ARN")
    host = os.environ.get("RDS_HOST")
    port = int(os.environ.get("RDS_PORT", "5432"))

    if not secret_arn or not host:
        return {"passed": False, "reason": "RDS connection env vars not configured"}

    import boto3

    sm = boto3.client("secretsmanager")
    secret_value = sm.get_secret_value(SecretId=secret_arn)
    creds = json.loads(secret_value["SecretString"])

    conn = pg8000.native.Connection(
        user=creds.get("username", "postgres"),
        password=creds["password"],
        host=host,
        port=port,
        database=db_name,
    )

    try:
        rows = conn.run(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"
        )
        table_count = len(rows)

        if table_count == 0:
            return {"passed": False, "reason": f"Database '{db_name}' has no public tables"}

        return {
            "passed": True,
            "reason": f"Database '{db_name}' has {table_count} public tables",
        }
    finally:
        conn.close()


# -------------------------------------------------------------------------
# diagnose: inspect migration history and specific tables for debugging
# -------------------------------------------------------------------------


def diagnose_database(event):
    """
    Diagnostic tool: inspect migration history and check specific tables.
    Returns migration versions, table list, and whether key tables exist.
    """
    import pg8000.native

    db_name = event.get("db_name", "")
    if not db_name:
        return {"passed": False, "reason": "No db_name provided"}

    secret_arn = os.environ.get("RDS_SECRET_ARN")
    host = os.environ.get("RDS_HOST")
    port = int(os.environ.get("RDS_PORT", "5432"))

    if not secret_arn or not host:
        return {"passed": False, "reason": "RDS connection env vars not configured"}

    import boto3

    sm = boto3.client("secretsmanager")
    secret_value = sm.get_secret_value(SecretId=secret_arn)
    creds = json.loads(secret_value["SecretString"])

    conn = pg8000.native.Connection(
        user=creds.get("username", "postgres"),
        password=creds["password"],
        host=host,
        port=port,
        database=db_name,
    )

    result = {}

    try:
        # Check all public tables
        rows = conn.run(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename"
        )
        table_names = [r[0] for r in rows]
        result["table_count"] = len(table_names)
        result["tables"] = table_names

        # Check specifically for QueuedEmailAttachment (case-sensitive)
        result["has_QueuedEmailAttachment"] = "QueuedEmailAttachment" in table_names
        result["has_queuedemailattachment"] = "queuedemailattachment" in table_names

        # Check migration history
        if "__MigrationVersionInfo" in table_names or "__migrationversioninfo" in table_names:
            migration_rows = conn.run(
                'SELECT "Version", "Description" FROM "__MigrationVersionInfo" ORDER BY "Version"'
            )
            result["migration_count"] = len(migration_rows)
            result["migrations"] = [
                {"version": str(r[0]), "description": r[1]} for r in migration_rows
            ]
        else:
            result["migration_count"] = 0
            result["migrations"] = []
            result["migration_table_missing"] = True

        # Check for EF Core migration table
        result["has_EFMigrationsHistory"] = "__EFMigrationsHistory" in table_names

    finally:
        conn.close()

    return {"passed": True, "reason": json.dumps(result, indent=2)}


# -------------------------------------------------------------------------
# init_config: create tenant database, app user, write EFS config files
# -------------------------------------------------------------------------


def init_config(event):
    """
    Initialize tenant config: create DB + app user, write Settings.txt
    and usersettings.json to EFS. Idempotent — safe to re-run.

    Event:
      { "check_type": "init_config",
        "system_key": "med", "tenant_key": "meadows", "environment": "dev",
        "db_name": "med_meadows_dev_smartstore",
        "app_user": "med_meadows_app",
        "app_version": "6.3.0.0" }
    """
    import pg8000.native
    import boto3

    sk = event.get("system_key", "")
    tk = event.get("tenant_key", "")
    env = event.get("environment", "")
    db_name = event.get("db_name", "")
    app_user = event.get("app_user", "")
    app_version = event.get("app_version", "6.3.0.0")

    if not all([sk, tk, env, db_name, app_user]):
        return {"passed": False, "reason": "Missing required fields in event"}

    mount_path = os.environ.get("EFS_MOUNT_PATH", "/mnt/efs")
    secret_arn = os.environ.get("RDS_SECRET_ARN")
    rds_host = os.environ.get("RDS_HOST")
    rds_port = int(os.environ.get("RDS_PORT", "5432"))

    if not secret_arn or not rds_host:
        return {"passed": False, "reason": "RDS connection env vars not configured"}

    sm = boto3.client("secretsmanager")

    # --- Read master DB credentials ---
    master_secret = sm.get_secret_value(SecretId=secret_arn)
    master_creds = json.loads(master_secret["SecretString"])
    master_user = master_creds.get("username", "dbadmin")
    master_password = master_creds["password"]

    # --- Read or generate app user password ---
    tenant_secret_name = f"{sk}/{tk}"
    app_password = _get_or_create_app_password(sm, tenant_secret_name, app_user)

    # --- Connect to RDS as admin (use 'postgres' maintenance DB) ---
    admin_conn = pg8000.native.Connection(
        user=master_user, password=master_password,
        host=rds_host, port=rds_port, database="postgres",
    )

    steps = []

    try:
        # --- Create database if missing ---
        rows = admin_conn.run(
            "SELECT 1 FROM pg_database WHERE datname = :name",
            name=db_name,
        )
        if len(rows) == 0:
            # CREATE DATABASE cannot run inside a transaction
            admin_conn.run(f'CREATE DATABASE "{db_name}" OWNER "{master_user}"')
            steps.append(f"Created database {db_name}")
        else:
            steps.append(f"Database {db_name} already exists")

        # --- Create or update app user ---
        # Note: CREATE/ALTER ROLE ... PASSWORD does not support parameterized
        # queries in PostgreSQL — the password must be inlined as a literal.
        # token_urlsafe only produces [A-Za-z0-9_-] so SQL injection is not
        # possible, but we still escape single quotes defensively.
        escaped_pw = app_password.replace("'", "''")
        roles = admin_conn.run(
            "SELECT 1 FROM pg_roles WHERE rolname = :name",
            name=app_user,
        )
        if len(roles) == 0:
            admin_conn.run(
                f"CREATE ROLE \"{app_user}\" LOGIN PASSWORD '{escaped_pw}'"
            )
            steps.append(f"Created role {app_user}")
        else:
            admin_conn.run(
                f"ALTER ROLE \"{app_user}\" PASSWORD '{escaped_pw}'"
            )
            steps.append(f"Updated password for role {app_user}")

        # Grant connect + ownership privileges
        admin_conn.run(f'GRANT ALL PRIVILEGES ON DATABASE "{db_name}" TO "{app_user}"')
    finally:
        admin_conn.close()

    # --- Grant schema-level privileges (must connect to tenant DB) ---
    tenant_conn = pg8000.native.Connection(
        user=master_user, password=master_password,
        host=rds_host, port=rds_port, database=db_name,
    )
    try:
        tenant_conn.run(f'GRANT ALL ON SCHEMA public TO "{app_user}"')
        tenant_conn.run(
            f'ALTER DEFAULT PRIVILEGES IN SCHEMA public '
            f'GRANT ALL ON TABLES TO "{app_user}"'
        )
        tenant_conn.run(
            f'ALTER DEFAULT PRIVILEGES IN SCHEMA public '
            f'GRANT ALL ON SEQUENCES TO "{app_user}"'
        )
        steps.append("Granted schema privileges")
    finally:
        tenant_conn.close()

    # --- Extract Default.zip if present and Default/Media not yet seeded ---
    efs_prefix = f"{sk}-{tk}-{env}"
    smartstore_data = os.path.join(mount_path, efs_prefix, "smartstore-data")
    zip_steps = _extract_default_zip_if_needed(smartstore_data)
    steps.extend(zip_steps)

    # --- Restore database dump if present ---
    db_restore_steps = _restore_database_dump_if_needed(
        smartstore_data, rds_host, str(rds_port), db_name,
        master_user, master_password, app_user,
    )
    steps.extend(db_restore_steps)

    # --- Write config files to EFS ---
    user_settings = event.get("user_settings")
    file_steps = _write_config_files(
        mount_path, sk, tk, env, rds_host, db_name, app_user, app_password, app_version,
        user_settings=user_settings,
    )
    steps.extend(file_steps)

    return {
        "passed": True,
        "reason": "; ".join(steps),
    }


# -------------------------------------------------------------------------
# post_seed_config: re-write EFS config files after database seeding
# -------------------------------------------------------------------------


def post_seed_config(event):
    """
    Re-write Settings.txt and usersettings.json after the seed process,
    which may have overwritten them with source-environment values.
    Also serves as a hook for other post-seed tasks.

    Event: same payload as init_config.
    """
    import boto3

    sk = event.get("system_key", "")
    tk = event.get("tenant_key", "")
    env = event.get("environment", "")
    db_name = event.get("db_name", "")
    app_user = event.get("app_user", "")
    app_version = event.get("app_version", "6.3.0.0")

    if not all([sk, tk, env, db_name, app_user]):
        return {"passed": False, "reason": "Missing required fields in event"}

    mount_path = os.environ.get("EFS_MOUNT_PATH", "/mnt/efs")
    rds_host = os.environ.get("RDS_HOST")

    if not rds_host:
        return {"passed": False, "reason": "RDS_HOST env var not configured"}

    # Read app password from tenant secret
    sm = boto3.client("secretsmanager")
    tenant_secret_name = f"{sk}/{tk}"

    try:
        resp = sm.get_secret_value(SecretId=tenant_secret_name)
        secret_data = json.loads(resp["SecretString"])
        app_password = secret_data.get("smartstore-db-password")

        if not app_password:
            return {
                "passed": False,
                "reason": f"No smartstore-db-password in secret '{tenant_secret_name}'. Run init_config first.",
            }
    except Exception as e:
        return {
            "passed": False,
            "reason": f"Failed to read tenant secret '{tenant_secret_name}': {str(e)}",
        }

    user_settings = event.get("user_settings")
    steps = _write_config_files(
        mount_path, sk, tk, env, rds_host, db_name, app_user, app_password, app_version,
        user_settings=user_settings,
    )

    return {
        "passed": True,
        "reason": "; ".join(steps),
    }


def _extract_default_zip_if_needed(smartstore_data_dir):
    """Extract Default.zip into smartstore-data/ if Default/Media doesn't exist yet."""
    import zipfile

    steps = []
    media_dir = os.path.join(smartstore_data_dir, "Default", "Media")
    zip_path = os.path.join(smartstore_data_dir, "Default.zip")

    if os.path.isdir(media_dir):
        steps.append("Default/Media already exists, skipping zip extraction")
        return steps

    if not os.path.isfile(zip_path):
        return steps  # No zip to extract — nothing to do

    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(smartstore_data_dir)
    steps.append(f"Extracted Default.zip ({os.path.getsize(zip_path)} bytes)")

    return steps


def _write_config_files(mount_path, sk, tk, env, rds_host, db_name, app_user, app_password, app_version,
                        user_settings=None):
    """Write Settings.txt and usersettings.json to EFS. Returns list of step descriptions."""
    steps = []

    efs_prefix = f"{sk}-{tk}-{env}"
    data_dir = os.path.join(mount_path, efs_prefix, "smartstore-data", "Default")
    os.makedirs(data_dir, exist_ok=True)

    conn_str = (
        f"Host={rds_host};"
        f"Database={db_name};"
        f"Username={app_user};"
        f"Password='{app_password}';"
        f"Pooling=True;"
        f"Minimum Pool Size=1;"
        f"Maximum Pool Size=1024;"
        f"Multiplexing=False"
    )
    settings_content = (
        f"AppVersion: {app_version}\n"
        f"DataProvider: PostgreSql\n"
        f"DataConnectionString: {conn_str}\n"
    )

    settings_path = os.path.join(data_dir, "Settings.txt")
    with open(settings_path, "w") as f:
        f.write(settings_content)
    steps.append(f"Wrote Settings.txt to {efs_prefix}/smartstore-data/Default/")

    config_dir = os.path.join(mount_path, efs_prefix, "smartstore-config")
    os.makedirs(config_dir, exist_ok=True)

    # Write usersettings.json from smartstore.usersettings.json content, or empty dict.
    # user_settings already has the complete top-level structure (Smartstore, Serilog, etc.)
    usersettings = user_settings if user_settings else {}
    usersettings_path = os.path.join(config_dir, "usersettings.json")
    with open(usersettings_path, "w") as f:
        json.dump(usersettings, f, indent=2)
    steps.append(f"Wrote usersettings.json to {efs_prefix}/smartstore-config/")

    return steps


# -------------------------------------------------------------------------
# Database dump restoration: restore database.sql via bundled psql binary
# -------------------------------------------------------------------------


def _prepare_psql():
    """
    Prepare the bundled psql binary for execution.
    Copies from the Lambda deployment package (/var/task) to /tmp (writable),
    sets execute permissions, and prepares LD_LIBRARY_PATH for shared libs.
    Returns (psql_path, env_dict) or (None, None) if psql not bundled.
    """
    import shutil
    import stat

    pkg_dir = os.path.dirname(os.path.abspath(__file__))
    src_bin = os.path.join(pkg_dir, "bin", "psql")

    if not os.path.isfile(src_bin):
        return None, None

    # Copy psql to /tmp where we can set execute permissions
    tmp_psql = "/tmp/psql"
    if not (os.path.isfile(tmp_psql) and os.access(tmp_psql, os.X_OK)):
        shutil.copy2(src_bin, tmp_psql)
        os.chmod(tmp_psql, stat.S_IRWXU)

    # Copy shared libraries to /tmp/lib
    src_lib = os.path.join(pkg_dir, "lib")
    tmp_lib = "/tmp/lib"
    if os.path.isdir(src_lib) and not os.path.isdir(tmp_lib):
        shutil.copytree(src_lib, tmp_lib)
        for f in os.listdir(tmp_lib):
            fpath = os.path.join(tmp_lib, f)
            if os.path.isfile(fpath):
                os.chmod(fpath, stat.S_IRWXU | stat.S_IRGRP | stat.S_IXGRP)

    # Build environment with library path
    env = os.environ.copy()
    if os.path.isdir(tmp_lib):
        env["LD_LIBRARY_PATH"] = tmp_lib + ":" + env.get("LD_LIBRARY_PATH", "")

    return tmp_psql, env


def _restore_database_dump_if_needed(smartstore_data_dir, rds_host, rds_port,
                                      db_name, master_user, master_password, app_user):
    """
    Restore database.sql (or .sql.gz) from smartstore-data/ if present.
    Drops existing public schema, restores dump via psql, grants privileges
    to app_user. Idempotent: creates a .imported marker after successful restore.
    Returns list of step descriptions.
    """
    import subprocess

    steps = []

    # Locate dump file
    sql_path = os.path.join(smartstore_data_dir, "database.sql")
    gz_path = sql_path + ".gz"
    marker_path = sql_path + ".imported"

    if os.path.isfile(marker_path):
        steps.append("Database dump already imported (marker exists), skipping")
        return steps

    # Decompress .gz if present
    if os.path.isfile(gz_path) and not os.path.isfile(sql_path):
        import gzip
        import shutil

        with gzip.open(gz_path, "rb") as f_in:
            with open(sql_path, "wb") as f_out:
                shutil.copyfileobj(f_in, f_out)
        gz_size_mb = os.path.getsize(gz_path) / (1024 * 1024)
        steps.append(f"Decompressed database.sql.gz ({gz_size_mb:.1f} MB)")

    if not os.path.isfile(sql_path):
        return steps  # No dump file present

    # Prepare psql binary
    psql_path, psql_env = _prepare_psql()
    if psql_path is None:
        steps.append(
            "WARNING: database.sql found but psql binary not bundled in Lambda, "
            "skipping restore. Run setup-psql.ps1 and redeploy."
        )
        return steps

    # Drop and recreate public schema (clean slate for restore)
    import pg8000.native

    conn = pg8000.native.Connection(
        user=master_user, password=master_password,
        host=rds_host, port=int(rds_port), database=db_name,
    )
    try:
        conn.run("DROP SCHEMA public CASCADE")
        conn.run("CREATE SCHEMA public")
        conn.run("GRANT ALL ON SCHEMA public TO PUBLIC")
        conn.run(f'GRANT ALL ON SCHEMA public TO "{master_user}"')
        steps.append("Dropped and recreated public schema")
    finally:
        conn.close()

    # Detect dump format: custom (binary) vs plain SQL
    psql_env["PGPASSWORD"] = master_password
    dump_size_mb = os.path.getsize(sql_path) / (1024 * 1024)

    with open(sql_path, "rb") as f:
        header = f.read(5)

    is_custom_format = header == b"PGDMP"

    if is_custom_format:
        # Custom format requires pg_restore, not psql
        pg_restore_src = os.path.join(
            os.path.dirname(os.path.abspath(__file__)), "bin", "pg_restore"
        )
        tmp_pg_restore = "/tmp/pg_restore"
        if not (os.path.isfile(tmp_pg_restore) and os.access(tmp_pg_restore, os.X_OK)):
            if os.path.isfile(pg_restore_src):
                import shutil
                import stat
                shutil.copy2(pg_restore_src, tmp_pg_restore)
                os.chmod(tmp_pg_restore, stat.S_IRWXU)
            else:
                steps.append(
                    "ERROR: database.sql is in custom format (pg_dump -Fc) "
                    "but pg_restore binary not bundled. Run setup-psql.ps1 and redeploy."
                )
                return steps

        steps.append(f"Restoring database.sql ({dump_size_mb:.1f} MB) via pg_restore (custom format)...")
        result = subprocess.run(
            [tmp_pg_restore,
             "-h", rds_host,
             "-p", str(rds_port),
             "-U", master_user,
             "-d", db_name,
             "--no-owner",
             "--no-privileges",
             "--verbose",
             sql_path],
            capture_output=True,
            text=True,
            env=psql_env,
            timeout=600,
        )
    else:
        # Plain SQL format — pre-process to strip database-level commands.
        # Dumps created with pg_dump --create include DROP/CREATE DATABASE
        # and \connect which would redirect tables to the wrong database.
        import re

        cleaned_path = sql_path + ".cleaned"
        with open(sql_path, "r") as fin, open(cleaned_path, "w") as fout:
            for line in fin:
                stripped = line.strip()
                # Skip DROP/CREATE/ALTER DATABASE and \connect commands
                if re.match(r"^(DROP|CREATE|ALTER)\s+DATABASE\b", stripped, re.IGNORECASE):
                    continue
                if stripped.startswith("\\connect "):
                    continue
                fout.write(line)
        steps.append(f"Restoring database.sql ({dump_size_mb:.1f} MB) via psql (plain SQL, stripped DB commands)...")

        result = subprocess.run(
            [psql_path,
             "-h", rds_host,
             "-p", str(rds_port),
             "-U", master_user,
             "-d", db_name,
             "-f", cleaned_path,
             "--quiet",
             "--no-psqlrc"],
            capture_output=True,
            text=True,
            env=psql_env,
            timeout=600,
        )

        # Clean up temp file
        if os.path.isfile(cleaned_path):
            os.remove(cleaned_path)

    restore_ok = result.returncode == 0
    stderr_text = (result.stderr or "").strip()
    stdout_text = (result.stdout or "").strip()

    if result.returncode != 0:
        # Log full stderr (truncated) for debugging
        all_output = stderr_text or stdout_text or "(no output)"
        steps.append(
            f"psql FAILED (exit code {result.returncode}): "
            + all_output[:2000]
        )
    else:
        steps.append("psql restore completed successfully")

    # Verify tables were actually created
    verify_conn = pg8000.native.Connection(
        user=master_user, password=master_password,
        host=rds_host, port=int(rds_port), database=db_name,
    )
    try:
        rows = verify_conn.run(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'"
        )
        table_count = rows[0][0] if rows else 0
        steps.append(f"Post-restore table count: {table_count}")
        if table_count == 0:
            restore_ok = False
            steps.append("RESTORE FAILED: no tables created, skipping marker")
    finally:
        verify_conn.close()

    # Grant privileges to app user on all restored objects
    if restore_ok:
        grant_conn = pg8000.native.Connection(
            user=master_user, password=master_password,
            host=rds_host, port=int(rds_port), database=db_name,
        )
        try:
            grant_conn.run(f'GRANT ALL ON SCHEMA public TO "{app_user}"')
            grant_conn.run(
                f'GRANT ALL ON ALL TABLES IN SCHEMA public TO "{app_user}"'
            )
            grant_conn.run(
                f'GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO "{app_user}"'
            )
            grant_conn.run(
                f'ALTER DEFAULT PRIVILEGES IN SCHEMA public '
                f'GRANT ALL ON TABLES TO "{app_user}"'
            )
            grant_conn.run(
                f'ALTER DEFAULT PRIVILEGES IN SCHEMA public '
                f'GRANT ALL ON SEQUENCES TO "{app_user}"'
            )
            steps.append(f"Granted all privileges to {app_user}")
        finally:
            grant_conn.close()

        # Only create marker on successful restore
        import datetime

        with open(marker_path, "w") as f:
            f.write(f"Imported at {datetime.datetime.utcnow().isoformat()}")
        steps.append("Created import marker (database.sql.imported)")

    return steps


# -------------------------------------------------------------------------
# setup_admin: create InternalAdmin customer + WebApi credentials
# -------------------------------------------------------------------------


def setup_admin(event):
    """
    Create an InternalAdmin customer with Administrators role and generate
    WebApi API credentials. All values are stored in Secrets Manager.
    Idempotent — safe to re-run; existing records and secrets are preserved.

    Event:
      { "check_type": "setup_admin",
        "system_key": "med", "tenant_key": "monro", "environment": "dev",
        "db_name": "med_monro_dev_smartstore" }
    """
    import hashlib
    import base64
    import uuid

    import pg8000.native
    import boto3

    sk = event.get("system_key", "")
    tk = event.get("tenant_key", "")
    env = event.get("environment", "")
    db_name = event.get("db_name", "")

    if not all([sk, tk, env, db_name]):
        return {"passed": False, "reason": "Missing required fields in event"}

    secret_arn = os.environ.get("RDS_SECRET_ARN")
    rds_host = os.environ.get("RDS_HOST")
    rds_port = int(os.environ.get("RDS_PORT", "5432"))

    if not secret_arn or not rds_host:
        return {"passed": False, "reason": "RDS connection env vars not configured"}

    sm = boto3.client("secretsmanager")

    # Read master DB credentials
    master_secret = sm.get_secret_value(SecretId=secret_arn)
    master_creds = json.loads(master_secret["SecretString"])
    master_user = master_creds.get("username", "dbadmin")
    master_password = master_creds["password"]

    # Read/create tenant secret
    tenant_secret_name = f"{sk}/{tk}"
    try:
        resp = sm.get_secret_value(SecretId=tenant_secret_name)
        secret_data = json.loads(resp["SecretString"])
    except sm.exceptions.ResourceNotFoundException:
        secret_data = {}

    conn = pg8000.native.Connection(
        user=master_user, password=master_password,
        host=rds_host, port=rds_port, database=db_name,
    )

    steps = []

    try:
        # Verify Customer table exists (schema must be seeded)
        tables = conn.run(
            "SELECT tablename FROM pg_tables "
            "WHERE schemaname = 'public' AND tablename = 'Customer'"
        )
        if len(tables) == 0:
            return {
                "passed": False,
                "reason": "Customer table not found — database must be seeded first",
            }

        # --- (a) Find or create apphostapi customer ---
        api_username = "apphostapi"
        api_email = f"apphostapi@{tk}.local"

        rows = conn.run(
            'SELECT "Id" FROM "Customer" WHERE "Username" = :username',
            username=api_username,
        )

        if len(rows) > 0:
            customer_id = rows[0][0]
            steps.append(f"{api_username} already exists (Id={customer_id})")
        else:
            # Generate and hash password
            admin_password = secrets.token_urlsafe(32)
            salt_bytes = os.urandom(5)
            salt_b64 = base64.b64encode(salt_bytes).decode()
            data = (admin_password + salt_b64).encode("utf-8")
            hashed = hashlib.sha1(data).hexdigest().upper()

            customer_guid = str(uuid.uuid4())

            # INSERT Customer — include all NOT NULL value-type columns
            # (bool/int columns don't get C# defaults in PostgreSQL)
            conn.run(
                'INSERT INTO "Customer" '
                '("CustomerGuid", "Username", "Email", "Password", "PasswordSalt", '
                '"PasswordFormatId", "Active", "Deleted", "IsSystemAccount", '
                '"IsTaxExempt", "AffiliateId", '
                '"VatNumberStatusId", "TaxDisplayTypeId", "LimitedToStores", '
                '"CreatedOnUtc", "LastActivityDateUtc") '
                "VALUES (:guid, :username, :email, :password, :salt, "
                "1, TRUE, FALSE, FALSE, "
                "FALSE, 0, "
                "0, 0, FALSE, "
                "NOW(), NOW())",
                guid=customer_guid,
                username=api_username,
                email=api_email,
                password=hashed,
                salt=salt_b64,
            )

            # Get the new customer ID
            rows = conn.run(
                'SELECT "Id" FROM "Customer" WHERE "Username" = :username',
                username=api_username,
            )
            customer_id = rows[0][0]
            steps.append(f"Created {api_username} customer (Id={customer_id})")

            # Store password in Secrets Manager
            secret_data["smartstore-apphostapi-password"] = admin_password
            _update_tenant_secret(sm, tenant_secret_name, secret_data)
            steps.append("Stored apphostapi password in Secrets Manager")

        # Ensure roles: Administrators + Registered (always check, not just on create)
        role_rows = conn.run(
            'SELECT "Id", "SystemName" FROM "CustomerRole" '
            "WHERE \"SystemName\" IN ('Administrators', 'Registered')"
        )

        for role_row in role_rows:
            role_id = role_row[0]
            role_name = role_row[1]
            # Check if mapping already exists
            existing = conn.run(
                'SELECT 1 FROM "CustomerRoleMapping" '
                'WHERE "CustomerId" = :cid AND "CustomerRoleId" = :rid',
                cid=customer_id,
                rid=role_id,
            )
            if len(existing) == 0:
                conn.run(
                    'INSERT INTO "CustomerRoleMapping" '
                    '("CustomerId", "CustomerRoleId", "IsSystemMapping") '
                    "VALUES (:cid, :rid, FALSE)",
                    cid=customer_id,
                    rid=role_id,
                )
                steps.append(f"Assigned role {role_name} (Id={role_id})")

        # --- (b) Create WebApi credentials ---
        ga_rows = conn.run(
            'SELECT "Id", "Value" FROM "GenericAttribute" '
            'WHERE "EntityId" = :eid AND "KeyGroup" = :kg AND "Key" = :key',
            eid=customer_id,
            kg="Customer",
            key="WebApiUserData",
        )

        if len(ga_rows) > 0:
            # Parse existing credentials
            value = ga_rows[0][1]
            parts = value.split("\u00b6")  # ¶ separator
            if len(parts) >= 3:
                public_key = parts[1]
                secret_key = parts[2]
                steps.append(f"WebApi credentials already exist (GA Id={ga_rows[0][0]})")

                # Ensure they're in Secrets Manager
                secret_data["smartstore_apphostapi_username"] = public_key
                secret_data["smartstore_apphostapi_password"] = secret_key
                _update_tenant_secret(sm, tenant_secret_name, secret_data)
                steps.append("Ensured API credentials are in Secrets Manager")
            else:
                steps.append(f"WARNING: Malformed WebApiUserData value: {value[:50]}")
        else:
            # Generate new API credentials (same as WebApiService.CreateKeys)
            public_key = os.urandom(32).hex()
            secret_key = os.urandom(32).hex()

            # Format: "Enabled¶PublicKey¶SecretKey"
            ga_value = f"True\u00b6{public_key}\u00b6{secret_key}"

            conn.run(
                'INSERT INTO "GenericAttribute" '
                '("EntityId", "KeyGroup", "Key", "Value", "StoreId") '
                "VALUES (:eid, :kg, :key, :val, 0)",
                eid=customer_id,
                kg="Customer",
                key="WebApiUserData",
                val=ga_value,
            )
            steps.append("Created WebApi credentials (GenericAttribute)")

            # Store in Secrets Manager
            secret_data["smartstore_apphostapi_username"] = public_key
            secret_data["smartstore_apphostapi_password"] = secret_key
            _update_tenant_secret(sm, tenant_secret_name, secret_data)
            steps.append("Stored API credentials in Secrets Manager")

    finally:
        conn.close()

    return {
        "passed": True,
        "reason": "; ".join(steps),
    }


def _update_tenant_secret(sm, secret_name, secret_data):
    """Write secret_data dict to Secrets Manager (create or update)."""
    secret_json = json.dumps(secret_data)
    try:
        sm.put_secret_value(
            SecretId=secret_name,
            SecretString=secret_json,
        )
    except sm.exceptions.ResourceNotFoundException:
        sm.create_secret(
            Name=secret_name,
            SecretString=secret_json,
        )


def _get_or_create_app_password(sm, secret_name, app_user):
    """
    Read existing app password from tenant secret, or generate a new one
    and store it. Returns the password string.
    """
    try:
        resp = sm.get_secret_value(SecretId=secret_name)
        secret_data = json.loads(resp["SecretString"])

        existing_pw = secret_data.get("smartstore-db-password")
        if existing_pw:
            return existing_pw

        # Secret exists but no password yet — generate and update
        new_pw = secrets.token_urlsafe(32)
        secret_data["smartstore-db-password"] = new_pw
        secret_data["smartstore-db-username"] = app_user
        sm.put_secret_value(
            SecretId=secret_name,
            SecretString=json.dumps(secret_data),
        )
        return new_pw

    except sm.exceptions.ResourceNotFoundException:
        # Secret doesn't exist yet — create it
        new_pw = secrets.token_urlsafe(32)
        sm.create_secret(
            Name=secret_name,
            SecretString=json.dumps({
                "smartstore-db-password": new_pw,
                "smartstore-db-username": app_user,
            }),
        )
        return new_pw
