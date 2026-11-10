using System.Reflection;
using Dapper;
using Npgsql;

namespace TD.Core.Data;

/// <summary>
/// The database's shape. A database of the Python program (2.22.x) is opened as it is; a new one gets the same tables
/// from baseline.sql (pg_dump of a database migrated by Django, with its django_migrations rows, so the Python could
/// still open it). Ours only add, in td_net_migrations. Run with the administrator role (db-admin.json), as the worker.
/// </summary>
public static class Migrations
{
    public const string AppRole = "tdbooking_app";

    static readonly (string Name, string Sql)[] Ours =
    {
        ("001_session", @"CREATE TABLE IF NOT EXISTS td_net_session (
            id uuid PRIMARY KEY, digest varchar(64) NOT NULL UNIQUE, user_id uuid NOT NULL REFERENCES core_user(id) ON DELETE CASCADE,
            created timestamptz NOT NULL, seen timestamptz NOT NULL, remember boolean NOT NULL, mfa boolean NOT NULL,
            auth_version integer NOT NULL, device varchar(200) NOT NULL, address varchar(64) NOT NULL);
          CREATE INDEX IF NOT EXISTS td_net_session_user ON td_net_session(user_id);"),
    };

    public static string Baseline()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TD.Core.Sql.baseline.sql")!;
        return new StreamReader(s).ReadToEnd();
    }

    /// <summary>Everything the program needs before it starts: tables, our additions, the rights of the runtime role.</summary>
    public static List<string> Apply(string adminConnection, string appPassword = null)
    {
        var done = new List<string>();
        using var c = new NpgsqlConnection(adminConnection);
        c.Open();
        var fresh = c.ExecuteScalar<string>("SELECT to_regclass('public.core_tenant')::text") == null;
        if (fresh)
        {
            using var t = c.BeginTransaction();
            c.Execute(Baseline(), transaction: t);
            c.Execute("SELECT setval(pg_get_serial_sequence('django_migrations','id'), (SELECT max(id) FROM django_migrations))", transaction: t);
            t.Commit();
            done.Add("baseline");
        }
        c.Execute("CREATE TABLE IF NOT EXISTS td_net_migrations (name varchar(100) PRIMARY KEY, applied timestamptz NOT NULL DEFAULT now())");
        var applied = c.Query<string>("SELECT name FROM td_net_migrations").ToHashSet();
        foreach (var (name, sql) in Ours)
        {
            if (applied.Contains(name)) continue;
            using var t = c.BeginTransaction();
            c.Execute(sql, transaction: t);
            c.Execute("INSERT INTO td_net_migrations(name) VALUES (@n)", new { n = name }, t);
            t.Commit();
            done.Add(name);
        }
        Grant(c, appPassword);
        return done;
    }

    /// <summary>core/management/commands/grant_runtime.py: the runtime role, never superuser nor BYPASSRLS, DML only.</summary>
    public static void Grant(NpgsqlConnection c, string password)
    {
        var row = c.QueryFirstOrDefault<(bool, bool)?>("SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname=@r", new { r = AppRole });
        if (row == null)
        {
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException($"Il ruolo {AppRole} non esiste e manca la sua password.");
            c.Execute(c.ExecuteScalar<string>("SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS', @r, @p)", new { r = AppRole, p = password }));
        }
        else if (row.Value.Item1 || row.Value.Item2) throw new InvalidOperationException($"Il ruolo {AppRole} non deve essere superuser/BYPASSRLS.");
        else if (!string.IsNullOrEmpty(password)) c.Execute(c.ExecuteScalar<string>("SELECT format('ALTER ROLE %I PASSWORD %L', @r, @p)", new { r = AppRole, p = password }));
        c.Execute(c.ExecuteScalar<string>("SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), @r)", new { r = AppRole }));
        c.Execute($@"GRANT USAGE ON SCHEMA public TO {AppRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {AppRole};
            REVOKE UPDATE, DELETE ON core_audit FROM {AppRole};
            REVOKE UPDATE, DELETE ON core_consentevent FROM {AppRole};
            REVOKE INSERT, UPDATE, DELETE ON django_migrations FROM {AppRole};
            REVOKE INSERT, UPDATE, DELETE ON td_net_migrations FROM {AppRole};");
    }
}
