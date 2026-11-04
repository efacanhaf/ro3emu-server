using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RoleImport;

/// <summary>
/// Writes a <see cref="CharacterPlan"/> into the realm database while the server is stopped.
///
/// The schema, the blob formats, the item columns and the revision parts are the server's own
/// (Accounts/Persistence/RoleItemPlans.cs, RoleColumns, RevisionStore): the tool never invents a shape,
/// it only fills the rows the server would have written after the same play. Every write is one SQLite
/// transaction, so a failed import leaves the previous character untouched.
/// </summary>
public static class RoleWriter
{
    public sealed record Result(long RoleId, bool Created, int Items, IReadOnlyList<string> Parts);

    public static Result Apply(string database, CharacterPlan plan, string account, string roleName,
        byte[]? passwordHash = null, byte[]? salt = null, bool dryRun = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout=5000;");
        using var transaction = connection.BeginTransaction();

        long role = FindRole(connection, transaction, roleName);
        bool created = role == 0;
        if (created)
        {
            EnsureAccount(connection, transaction, account, passwordHash, salt);
            role = InsertRole(connection, transaction, plan, account, roleName);
        }
        else if (passwordHash != null)
        {
            SetPassword(connection, transaction, account, passwordHash, salt!);
        }

        UpdateProgress(connection, transaction, plan, role);
        ForgetRoleLedger(connection, transaction, role);
        foreach (var (column, bytes) in plan.Blobs) WriteBlob(connection, transaction, role, column, bytes);
        foreach (string column in plan.ClearedBlobs) WriteBlob(connection, transaction, role, column, null);
        ReplaceItems(connection, transaction, role, plan.Items);
        var parts = plan.Blobs.Keys.Concat(new[] { "progress", "inventory" }).ToArray();
        foreach (string part in parts) BumpRevision(connection, transaction, role, part);

        if (dryRun) { transaction.Rollback(); return new Result(role, created, plan.Items.Count, parts); }
        transaction.Commit();
        return new Result(role, created, plan.Items.Count, parts);
    }

    private static long FindRole(SqliteConnection db, SqliteTransaction tx, string name) =>
        Scalar<long?>(db, tx, "SELECT id FROM role WHERE name=$name COLLATE NOCASE AND deleted_time=0", ("$name", name)) ?? 0;

    private static void EnsureAccount(SqliteConnection db, SqliteTransaction tx, string account, byte[]? hash, byte[]? salt)
    {
        if (Scalar<long?>(db, tx, "SELECT 1 FROM account WHERE name=$name COLLATE NOCASE", ("$name", account)) != null) return;
        if (hash == null || salt == null)
            throw new InvalidOperationException($"Account '{account}' does not exist and no password was given (--password-from-dpapi or --reset-password).");
        Execute(db, tx, "INSERT INTO account(name,password_hash,salt,created_utc) VALUES($name,$hash,$salt,$now)",
            ("$name", account), ("$hash", hash), ("$salt", salt),
            ("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
    }

    private static void SetPassword(SqliteConnection db, SqliteTransaction tx, string account, byte[] hash, byte[] salt) =>
        Execute(db, tx, "UPDATE account SET password_hash=$hash,salt=$salt WHERE name=$name COLLATE NOCASE",
            ("$name", account), ("$hash", hash), ("$salt", salt));

    private static long InsertRole(SqliteConnection db, SqliteTransaction tx, CharacterPlan plan, string account, string roleName)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Execute(db, tx, """
            INSERT INTO role(account,area,name,sex,career,level,hair,create_time,last_login_time,deleted_time)
            VALUES($account,1,$name,$sex,$career,$level,$hair,$create,$now,0)
            """,
            ("$account", account), ("$name", roleName), ("$sex", plan.Columns["sex"]),
            ("$career", plan.Columns["career"]), ("$level", plan.Columns["level"]),
            ("$hair", plan.Columns["hair"]),
            ("$create", plan.Columns.GetValueOrDefault("create_time") is long create and > 0 ? create : now),
            ("$now", now));
        long role = Scalar<long>(db, tx, "SELECT last_insert_rowid()");
        Execute(db, tx, "INSERT INTO role_lifecycle(role_id,epoch,deleted) VALUES($role,1,0)", ("$role", role));
        return role;
    }

    /// <summary>RoleProgress columns: exactly RoleProgressWritePlan's list.</summary>
    private static void UpdateProgress(SqliteConnection db, SqliteTransaction tx, CharacterPlan plan, long role)
    {
        Execute(db, tx, """
            UPDATE role SET career=$career,level=$level,job_level=$jl,exp=$exp,job_exp=$jexp,stats=$stats,
                bonus_points=$bonus,currency=$cur,starter_kit=$kit,skills=$skills,skill_bar=$bar,
                paragon_level=$plv,paragon_exp=$pexp
            WHERE id=$id
            """,
            ("$id", role),
            ("$career", plan.Columns.GetValueOrDefault("career", 0L)),
            ("$level", plan.Columns.GetValueOrDefault("level", 1L)),
            ("$jl", plan.Columns.GetValueOrDefault("job_level", 1L)),
            ("$exp", plan.Columns.GetValueOrDefault("exp", 0L)),
            ("$jexp", plan.Columns.GetValueOrDefault("job_exp", 0L)),
            ("$stats", plan.Columns.GetValueOrDefault("stats", "1,1,1,1,1,1")),
            ("$bonus", plan.Columns.GetValueOrDefault("bonus_points", 0L)),
            ("$cur", plan.Columns.GetValueOrDefault("currency", "")),
            ("$kit", 1L),
            ("$skills", plan.Columns.GetValueOrDefault("skills", "")),
            ("$bar", plan.Columns.GetValueOrDefault("skill_bar", "")),
            ("$plv", plan.Columns.GetValueOrDefault("paragon_level", 0L)),
            ("$pexp", plan.Columns.GetValueOrDefault("paragon_exp", 0L)));
    }

    private static void WriteBlob(SqliteConnection db, SqliteTransaction tx, long role, string column, byte[]? value)
    {
        // The column name comes from CharacterPlan only; an unknown one fails loudly in SQLite.
        Execute(db, tx, $"UPDATE role SET {column}=$value WHERE id=$id", ("$id", role), ("$value", value));
    }

    /// <summary>
    /// The role's economy receipts are role state too: a natural source key (task/&lt;role&gt;/&lt;task&gt;/...) that was
    /// committed once is refused on the next attempt, and replacing the journal makes the server deliver the same
    /// awards again on the next login. Importing over the previous copy would therefore fail the login with
    /// "Natural source already committed"; forgetting the role-scoped receipts lets the replaced state commit again.
    /// The legs of those operations go with them, so the ledger keeps matching the balances the import writes.
    /// </summary>
    private static void ForgetRoleLedger(SqliteConnection db, SqliteTransaction tx, long role)
    {
        string needle = "%/" + role.ToString(CultureInfo.InvariantCulture) + "/%";
        Execute(db, tx, "CREATE TEMP TABLE IF NOT EXISTS replaced_operation(op TEXT PRIMARY KEY)");
        Execute(db, tx, "DELETE FROM replaced_operation");
        Execute(db, tx, "INSERT OR IGNORE INTO replaced_operation SELECT operation_id FROM operation_source WHERE source_key LIKE $needle",
            ("$needle", needle));
        foreach (string table in new[] { "economy_entry", "operation_source", "operation_receipt" })
            Execute(db, tx, $"DELETE FROM {table} WHERE operation_id IN (SELECT op FROM replaced_operation)");
        Execute(db, tx, "DROP TABLE replaced_operation");
    }

    /// <summary>The item table is the whole inventory: replace the role's rows, keeping the guids the plan allocated.</summary>
    private static void ReplaceItems(SqliteConnection db, SqliteTransaction tx, long role, IReadOnlyList<Ro3Emu.Server.Accounts.InvItem> items)
    {
        Execute(db, tx, "DELETE FROM item WHERE role_id=$role", ("$role", role));
        foreach (var item in items)
            Execute(db, tx, """
                INSERT INTO item(guid,role_id,entry,stack,pack,sort,grid,attrs,is_locked,enchantments,str_attrs,bind_type,bind_expire)
                VALUES($guid,$role,$entry,$stack,$pack,$sort,$grid,$attrs,$locked,$ench,$str,$bind,$expire)
                """,
                ("$guid", item.Guid), ("$role", role), ("$entry", item.Entry), ("$stack", item.Stack),
                ("$pack", item.Pack), ("$sort", item.Sort), ("$grid", item.Grid),
                ("$attrs", item.Attrs is { Count: > 0 } ? Db(JsonSerializer.SerializeToUtf8Bytes(item.Attrs)) : null),
                ("$locked", item.IsLocked ? 1L : 0L),
                ("$ench", item.Enchantments.Count > 0 ? Db(JsonSerializer.SerializeToUtf8Bytes(item.Enchantments)) : null),
                ("$str", item.StringAttrs is { Count: > 0 } ? Db(JsonSerializer.SerializeToUtf8Bytes(item.StringAttrs)) : null),
                ("$bind", item.BindType), ("$expire", item.BindExpire));
        Execute(db, tx, """
            INSERT INTO sqlite_sequence(name,seq)
            SELECT 'item', COALESCE((SELECT MAX(guid) FROM item), 1000000)
            WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name='item')
            """);
        Execute(db, tx, """
            UPDATE sqlite_sequence SET seq = MAX(seq, COALESCE((SELECT MAX(guid) FROM item), 0)) WHERE name='item'
            """);
    }

    private static string Db(byte[] json) => System.Text.Encoding.UTF8.GetString(json);

    /// <summary>The server's CAS guard: a written part advances its revision (RevisionStore).</summary>
    private static void BumpRevision(SqliteConnection db, SqliteTransaction tx, long role, string part) =>
        Execute(db, tx, """
            INSERT INTO owner_revision(kind,id,part,revision) VALUES('role',$role,$part,1)
            ON CONFLICT(kind,id,part) DO UPDATE SET revision=revision+1
            """, ("$role", role.ToString(CultureInfo.InvariantCulture)), ("$part", part));

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection db, SqliteTransaction tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        object? result = command.ExecuteScalar();
        if (result is null or DBNull) return default!;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }
}
