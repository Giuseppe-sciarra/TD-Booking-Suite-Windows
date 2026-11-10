using System.Data;
using System.Net;
using System.Reflection;
using System.Text;
using Dapper;
using Npgsql;

namespace TD.Core.Data;

[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute(string name) : Attribute { public string Name { get; } = name; }

/// <summary>
/// The database the Python program created and still owns the shape of (its migrations; ours go in Migrations.cs). The
/// service connects as tdbooking_app, which does not bypass row-level security: every query on a business's tables runs
/// inside Tenant(id), a transaction with app.tenant_id set, exactly as core/tenancy.scope does.
/// </summary>
public sealed class Db : IDisposable
{
    readonly NpgsqlDataSource source;
    static int configured;

    public Db(string connectionString)
    {
        Configure();
        var b = new NpgsqlDataSourceBuilder(connectionString);
        source = b.Build();
    }

    public static void Configure()
    {
        if (Interlocked.Exchange(ref configured, 1) == 1) return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new IpHandler());
        SqlMapper.AddTypeHandler(new TimeSpanHandler());
    }

    public NpgsqlConnection Open() => source.OpenConnection();
    public async Task<NpgsqlConnection> OpenAsync() => await source.OpenConnectionAsync();

    /// <summary>A unit of work outside any business (users, businesses, platform tables).</summary>
    public Scope Global() => new(Open(), null);
    /// <summary>A unit of work inside a business: row-level security sees only its rows.</summary>
    public Scope Tenant(Guid tenant) => new(Open(), tenant);

    public void Dispose() => source.Dispose();

    sealed class IpHandler : SqlMapper.TypeHandler<string>
    {
        public override void SetValue(IDbDataParameter parameter, string value) => parameter.Value = (object)value ?? DBNull.Value;
        public override string Parse(object value) => value switch { IPAddress ip => ip.ToString(), null or DBNull => null, _ => value.ToString() };
    }
    sealed class TimeSpanHandler : SqlMapper.TypeHandler<TimeSpan>
    {
        public override void SetValue(IDbDataParameter parameter, TimeSpan value) => parameter.Value = value;
        public override TimeSpan Parse(object value) => value switch { TimeSpan t => t, TimeOnly o => o.ToTimeSpan(), _ => TimeSpan.Parse(value.ToString()) };
    }
}

/// <summary>A connection with its transaction; commit with Commit(), anything else rolls back.</summary>
public sealed class Scope : IDisposable
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public Guid? TenantId { get; }
    bool done;

    internal Scope(NpgsqlConnection connection, Guid? tenant)
    {
        Connection = connection;
        Transaction = connection.BeginTransaction();
        TenantId = tenant;
        Connection.Execute("SELECT set_config('app.tenant_id', @t, true)", new { t = tenant?.ToString() ?? "" }, Transaction);
    }

    public void Commit() { Transaction.Commit(); done = true; }
    public void Dispose()
    {
        if (!done) try { Transaction.Rollback(); } catch { }
        Connection.Dispose();
    }

    // ---------- plain queries
    public IEnumerable<T> Query<T>(string sql, object args = null) => Connection.Query<T>(sql, args, Transaction);
    public T One<T>(string sql, object args = null) => Connection.QueryFirstOrDefault<T>(sql, args, Transaction);
    public T Scalar<T>(string sql, object args = null) => Connection.ExecuteScalar<T>(sql, args, Transaction);
    public int Execute(string sql, object args = null) => Connection.Execute(sql, args, Transaction);

    // ---------- rows by their entity class (Entities.g.cs)
    public T Get<T>(object id) where T : class => Connection.QueryFirstOrDefault<T>($"SELECT * FROM {Table<T>()} WHERE id=@id", new { id }, Transaction);
    public IEnumerable<T> All<T>(string where = "true", object args = null, string order = null) where T : class =>
        Connection.Query<T>($"SELECT * FROM {Table<T>()} WHERE {where}" + (order == null ? "" : " ORDER BY " + order), args, Transaction);

    /// <summary>INSERT of every column of the table that the entity has (jsonb and inet cast as such).</summary>
    public T Insert<T>(T row) where T : class
    {
        (row as IJsonColumns)?.Flush();
        var table = Table<T>();
        var cols = Schema.Columns[table];
        var props = Props<T>();
        var names = new List<string>(); var values = new List<string>(); var p = new DynamicParameters();
        foreach (var (col, type) in cols)
        {
            if (!props.TryGetValue(col, out var prop)) continue;
            var v = prop.GetValue(row);
            if (col == "id" && v is int i && i == 0) continue;  // serial ids (core_audit...)
            if (col == "id" && v is long l && l == 0) continue;
            names.Add(Quote(col)); values.Add("@" + col + Cast(type)); p.Add(col, v);
        }
        var sql = $"INSERT INTO {table} ({string.Join(",", names)}) VALUES ({string.Join(",", values)})";
        if (props.TryGetValue("id", out var idp) && (idp.PropertyType == typeof(int) || idp.PropertyType == typeof(long)) && Equals(idp.GetValue(row), Convert.ChangeType(0, idp.PropertyType)))
            idp.SetValue(row, Convert.ChangeType(Connection.ExecuteScalar(sql + " RETURNING id", p, Transaction), idp.PropertyType));
        else Connection.Execute(sql, p, Transaction);
        return row;
    }

    /// <summary>UPDATE of the given columns (all of them when none) of the row with that id.</summary>
    public int Update<T>(T row, params string[] columns) where T : class
    {
        (row as IJsonColumns)?.Flush();
        var table = Table<T>();
        var types = Schema.Columns[table].ToDictionary(c => c.Column, c => c.Type);
        var props = Props<T>();
        var set = new StringBuilder(); var p = new DynamicParameters();
        foreach (var col in columns.Length > 0 ? columns : types.Keys.Where(c => c != "id").ToArray())
        {
            if (!props.TryGetValue(col, out var prop)) throw new ArgumentException($"{table} non ha {col}");
            if (set.Length > 0) set.Append(',');
            set.Append(Quote(col)).Append("=@").Append(col).Append(Cast(types[col]));
            p.Add(col, prop.GetValue(row));
        }
        p.Add("__id", props["id"].GetValue(row));
        return Connection.Execute($"UPDATE {table} SET {set} WHERE id=@__id", p, Transaction);
    }

    public int Delete<T>(object id) where T : class => Connection.Execute($"DELETE FROM {Table<T>()} WHERE id=@id", new { id }, Transaction);

    static string Cast(string type) => type switch { "jsonb" => "::jsonb", "inet" => "::inet", "date" => "::date", "time without time zone" => "::time", _ => "" };
    static string Quote(string col) => "\"" + col + "\"";

    static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> propCache = new();
    static Dictionary<string, PropertyInfo> Props<T>()
    {
        lock (propCache)
        {
            if (propCache.TryGetValue(typeof(T), out var d)) return d;
            d = typeof(T).GetProperties().ToDictionary(p => Snake(p.Name), p => p);
            propCache[typeof(T)] = d; return d;
        }
    }
    public static string Table<T>() => typeof(T).GetCustomAttribute<TableAttribute>()?.Name ?? throw new ArgumentException(typeof(T).Name);
    public static string Snake(string pascal)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c)) { if (i > 0) sb.Append('_'); sb.Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
