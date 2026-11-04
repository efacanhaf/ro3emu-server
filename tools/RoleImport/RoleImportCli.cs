using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;

namespace RoleImport;

/// <summary>One character import: args, checks, the DB write and the summary.</summary>
public static class RoleImportCli
{
    public static int Run(string[] args)
    {
        try { return Execute(args); }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException
            or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
            or Google.Protobuf.InvalidProtocolBufferException)
        {
            Console.Error.WriteLine($"RoleImport: {error.Message}");
            return 1;
        }
    }

    private sealed record Options
    {
        public string Snapshot = "";
        public string Database = "";
        public string Account = "";
        public string RoleName = "";
        public string? PasswordFromDpapi;
        public string? ResetPassword;
        public string? DataDir;
        public bool DryRun;
    }

    private static int Execute(string[] args)
    {
        var options = Parse(args);
        using var snapshot = SnapshotDocument.Load(options.Snapshot);
        var character = snapshot.Root.TryGetProperty("character", out var who) ? who : default;
        string offlineName = String(who, "name");
        string offlineAccount = String(who, "account");
        long offlineLevel = Number(who, "level");

        long firstGuid = NextItemGuid(options.Database);
        int basePoints = BaseStatPoints(options.DataDir ?? Path.GetDirectoryName(options.Database)!, (int)Number(
            snapshot.Message("ROLEBASEINFO"), "level"));
        var commandSkills = CommandSkills(options.DataDir ?? Path.GetDirectoryName(options.Database)!);
        var plan = CharacterPlan.Build(snapshot, firstGuid, basePoints, commandSkills);

        byte[]? hash = null, salt = null;
        if (options.ResetPassword is { Length: > 0 } account)
        {
            string password = Dpapi.NewPassword();
            salt = RandomNumberGenerator.GetBytes(16);
            hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
            string path = options.DryRun ? "(dry run)" : Dpapi.Save(account, password);
            Console.WriteLine($"password: reset for '{account}', saved with DPAPI at {path}");
        }
        else if (options.PasswordFromDpapi is { Length: > 0 } saved)
        {
            string password = Dpapi.Read(saved);
            salt = RandomNumberGenerator.GetBytes(16);
            hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
            Console.WriteLine($"password: '{saved}' set from the DPAPI credential");
        }

        var result = RoleWriter.Apply(options.Database, plan, options.Account, options.RoleName, hash, salt, options.DryRun);

        Console.WriteLine($"snapshot: {Path.GetFileName(options.Snapshot)} ({snapshot.Count} messages, exported {snapshot.ExportedAtText})");
        Console.WriteLine($"official: role {offlineName} ({offlineAccount}), level {offlineLevel}");
        Console.WriteLine($"local:    role {result.RoleId} '{options.RoleName}' ({(result.Created ? "created" : "replaced")}), " +
            $"{result.Items} items, guids {plan.GuidMap.Values.Min()}-{plan.GuidMap.Values.Max()}");
        Console.WriteLine($"parts:    {string.Join(' ', result.Parts)}");
        foreach (string note in plan.Notes) Console.WriteLine($"note:     {note}");
        if (options.DryRun) Console.WriteLine("dry run: nothing was written");
        return 0;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            string Next() => index + 1 < args.Length ? args[++index]
                : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--snapshot": options.Snapshot = Next(); break;
                case "--db": options.Database = Next(); break;
                case "--account": options.Account = Next(); break;
                case "--role-name": options.RoleName = Next(); break;
                case "--password-from-dpapi": options.PasswordFromDpapi = Next(); break;
                case "--reset-password": options.ResetPassword = Next(); break;
                case "--data-dir": options.DataDir = Next(); break;
                case "--dry-run": options.DryRun = true; break;
                default: throw new ArgumentException($"unknown option '{name}'.");
            }
        }
        if (options.Snapshot.Length == 0) throw new ArgumentException("--snapshot is required.");
        if (options.Account.Length == 0) throw new ArgumentException("--account is required.");
        if (options.RoleName.Length == 0) throw new ArgumentException("--role-name is required.");
        if (options.Database.Length == 0) options.Database = FindDatabase();
        if (!File.Exists(options.Database)) throw new FileNotFoundException("Database not found.", options.Database);
        return options;
    }

    /// <summary>data/ro3emu.db next to the executable's repository, so the tool also works from bin/.</summary>
    private static string FindDatabase()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "data", "ro3emu.db");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("Could not find data/ro3emu.db; pass --db.");
    }

    /// <summary>First item guid the import may use: above every id already stored (the item sequence starts at 1000000).</summary>
    private static long NextItemGuid(string database)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(guid), 1000000) FROM item";
        return (long)command.ExecuteScalar()! + 1;
    }

    /// <summary>
    /// CharacterService.TotalPoints = StartingStatPoints (48) + BonusPoints + the BaseLvConfig points earned
    /// from level 2 to this one. Reading that table lets the import keep the official leftover points instead
    /// of inventing 48 more.
    /// </summary>
    private static int BaseStatPoints(string dataDirectory, int level)
    {
        const int starting = 48; // CharacterService.StartingStatPoints
        foreach (string path in ConfigCandidates(dataDirectory))
        {
            if (!File.Exists(path)) continue;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
            var rows = document.RootElement;
            int total = starting;
            for (int lv = 2; lv <= level; lv++)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (Number(row, "_iBaseLv") != lv) continue;
                    total += (int)Number(row, "_iStatusPoint");
                    break;
                }
            }
            return total;
        }
        Console.Error.WriteLine($"RoleImport: BaseLvConfig not found under '{dataDirectory}'; assuming {starting} base points.");
        return starting;
    }

    /// <summary>
    /// The skill groups the client shows without points: `SkillConfig._iSystemType` 6 with an initial level (the queue
    /// command «Skill Bar», one per career line). Read from the exported game data; unavailable data means none.
    /// </summary>
    private static HashSet<int> CommandSkills(string dataDirectory)
    {
        var groups = new HashSet<int>();
        foreach (string folder in ConfigFolders(dataDirectory))
        {
            string path = Path.Combine(folder, "SkillConfig.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                var rows = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : root.EnumerateObject().Select(property => property.Value).ToArray();
                foreach (var row in rows)
                {
                    if (Number(row, "_iSystemType") != 6 || Number(row, "_iInitialLevel") <= 0) continue;
                    int group = (int)Number(row, "_iSkillID");
                    if (group > 0) groups.Add(group);
                }
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                Console.Error.WriteLine($"RoleImport: SkillConfig unreadable ({error.Message}); no system commands granted.");
            }
            return groups;
        }
        return groups;
    }

    /// <summary>The exported game data: &lt;dir&gt;/config, a data/ or config/ dir, or any ancestor holding one.</summary>
    private static IEnumerable<string> ConfigFolders(string directory)
    {
        yield return Path.Combine(directory, "config");
        yield return directory;
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            yield return Path.Combine(current.FullName, "data", "config");
            yield return Path.Combine(current.FullName, "config");
        }
    }

    /// <summary>The exported game data: &lt;dir&gt;/config/BaseLvConfig.json, a data/ or config/ dir, or any ancestor holding it.</summary>
    private static IEnumerable<string> ConfigCandidates(string directory) =>
        ConfigFolders(directory).Select(folder => Path.Combine(folder, "BaseLvConfig.json"));

    private static string String(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var inner) && inner.ValueKind == JsonValueKind.String
            ? inner.GetString() ?? "" : "";

    private static long Number(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var inner)
            ? Number(inner, field) : value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out long number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
                _ => 0,
            };
}
