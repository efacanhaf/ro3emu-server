using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season talents: 5050 is the durable global/plan view; 83550 pays the next
/// SeasonTalentEffectConfig level, 83552 selects a plan, 83553 renames one. The official client's own save
/// (DBSEASONTALENT exported from the live game) shows the split this service keeps: the bought levels of the
/// ordinary nodes (types 1/2) and the battle oath (4) live in the top-level <c>talentNodes</c> map, and the five
/// plans hold only the profession-choice members (SkillPoint 5, Profession_L 6, Profession_M 7). The maps are
/// disjoint, so the client's "current plan over the global map" overlay (LC_DAL_SeasonTalent._processAllHandler)
/// is simply their union. Five zero-based plans come from PublicParamConfig.SchemeNum=5.
/// </summary>
public sealed partial class SeasonTalentService
{
    public const uint TalentSync = 5050, Upgrade = 83550, SetPlan = 83552, RenamePlan = 83553;
    internal const long OathNode = 10100000;
    private const long TalentFunction = 1003, ParentFunction = 10, TalentTask = 10103160, ParentTask = 10101170;
    // (inferred) server-side limits: five zero-based plans, eight Unicode scalars, three-second switch cooldown.
    private const int PlanCount = 5, NameMaxRunes = 8, SwitchCooldownSeconds = 3;
    private const int PvpNodeType = 4;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly RoleStore _roles;
    private readonly ILogger<SeasonTalentService> _log;

    public SeasonTalentService(GameData data, CharacterService character, FunctionService functions,
        SeasonService seasons, RoleStore roles, ILogger<SeasonTalentService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._roles = roles;
        this._log = log;
        character.TalentAttributes = Attributes;
    }

    /// <summary>Called before the first login stat snapshot; (inferred) saved plans carry across seasons without reset.</summary>
    public void Load(GameSession session)
    {
        session.SeasonTalent = _roles.LoadSeasonTalent(session.Role!.Id) is { } bytes
            ? DBSeasonTalent.Parser.ParseFrom(bytes) : new DBSeasonTalent();
        session.TalentEffectsEnabled = false; // TaskService has not loaded this role's quest state yet.
        session.TalentPlanSwitchUtc = default;
        session.TalentSkillModifiers = [];
        session.TalentProcBuffs = [];
        session.TalentBuffAttributes = [];
        Seed(session.SeasonTalent);
    }

    /// <summary>Called after 5027 and before 5056, also when quests or progress change.</summary>
    public async Task SyncAsync(GameSession session)
    {
        if (session.SeasonTalent == null) return;
        // The DAL expects configured tree and node keys in 5050. An empty map can crash its overlay.
        if (!HasTalentGraph()) return;
        bool before = session.TalentEffectsEnabled;
        var oldTrees = session.SeasonTalent.UnlockedTrees.ToDictionary(kv => kv.Key, kv => kv.Value);
        var oldNodes = session.SeasonTalent.UnlockedNode.ToDictionary(kv => kv.Key, kv => kv.Value);
        session.TalentEffectsEnabled = Available(session);
        BuildUnlocks(session);
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent, session.SeasonTalent.Clone);
        // Seeding false map keys at login cannot alter stats; avoid an extra pre-world 31008.
        if (before != session.TalentEffectsEnabled || (session.TalentEffectsEnabled
            && (!Same(oldTrees, session.SeasonTalent.UnlockedTrees)
                || !Same(oldNodes, session.SeasonTalent.UnlockedNode)))) await _character.RefreshStatsAsync(session);
        await RefreshEffectsAsync(session);
    }

    private static bool Same(IReadOnlyDictionary<long, bool> old, IDictionary<long, bool> now) =>
        old.Count == now.Count && old.All(kv => now.TryGetValue(kv.Key, out bool value) && value == kv.Value);

    private bool Available(GameSession s) => s.Role != null && s.Progress != null && s.Tasks != null
        && HasTalentGraph()
        && TaskService.IsDelivered(s, ParentTask) && TaskService.IsDelivered(s, TalentTask)
        && _functions.IsOpen(s, ParentFunction) && _functions.IsOpen(s, TalentFunction)
        && Trees().Any(Applies);

    /// <summary>Use 83552's unlock and cooldown policy when Genre changes the active talent plan.</summary>
    internal int ValidateGenrePlan(GameSession session, DBSeasonTalent state, int index, bool replaceCurrent = false)
    {
        if (index == state.CurPlanId && !replaceCurrent) return 0;
        if (!Available(session)) return ErrorCodes.FeatureUnavailable;
        if (index is < 0 or >= PlanCount || !state.Plan.ContainsKey(index)) return ErrorCodes.Parameter;
        if (index != state.CurPlanId && DateTime.UtcNow - session.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds)) return ErrorCodes.Cooldown;
        state.CurPlanId = index;
        BuildUnlocks(session, state);
        return 0;
    }

    internal async Task SyncGenrePlanAsync(GameSession session)
    {
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent!, session.SeasonTalent!.Clone);
        await RefreshEffectsAsync(session);
    }

    private bool HasTalentGraph() => Trees().Any(tree => tree.Id > 0) && Nodes().Count > 0;

    private bool Applies(JsonElement row) => row.TryGetProperty("_kMultiverseArray", out var books)
        && books.ValueKind == JsonValueKind.Array
        && books.EnumerateArray().Any(v => GameData.Number(v) == _seasons.PlayBook);
    private bool Applies(TalentTree tree) => tree.Books.Any(book => book == _seasons.PlayBook);

    /// <summary>
    /// The official split (see the class comment): every configured node is seeded into the map that owns it —
    /// choice members into each plan, ordinary nodes and the battle oath into the global map — and entries that
    /// an older save put in the other map are dropped, so a login normalises to the shape the client exports.
    /// </summary>
    private void Seed(DBSeasonTalent state)
    {
        // (inferred) plan keys 0..4, default 0: the client labels a key by key+1 (LC_DAL_SeasonTalent.GetPlanNameByPlan).
        if (state.CurPlanId is < 0 or >= PlanCount) state.CurPlanId = 0;
        for (int planId = 0; planId < PlanCount; planId++)
            if (!state.Plan.TryGetValue(planId, out _))
                state.Plan[planId] = new SeasonTalentPlan();
        var nodes = Nodes();
        foreach (var node in nodes)
        {
            if (IsChoice(node.Type))
            {
                // Choice members belong to the plans; a global entry would be a stale duplicate of a plan's level.
                state.TalentNodes.Remove(node.Id);
            }
            else if (!state.TalentNodes.ContainsKey(node.Id))
                state.TalentNodes[node.Id] = new DBSeasonTalentNode { TalentId = node.Id };
            foreach (var plan in state.Plan.Values)
            {
                if (IsChoice(node.Type))
                {
                    if (!plan.TalentNodes.ContainsKey(node.Id))
                        plan.TalentNodes[node.Id] = new DBSeasonTalentNode { TalentId = node.Id };
                }
                else if (plan.TalentNodes.TryGetValue(node.Id, out var moved))
                {
                    // A save written before the official shape kept bought levels in the plans: move them across.
                    if (moved.CurrentLevel > state.TalentNodes[node.Id].CurrentLevel)
                        state.TalentNodes[node.Id].CurrentLevel = moved.CurrentLevel;
                    plan.TalentNodes.Remove(node.Id);
                }
            }
        }
    }

    private void BuildUnlocks(GameSession s, DBSeasonTalent? candidate = null)
    {
        var state = candidate ?? s.SeasonTalent!;
        s.TalentEffectsEnabled = Available(s);
        Seed(state);
        long total = TalentLevel(state);
        state.UnlockedTrees.Clear();
        long? oathMax = null;
        foreach (var tree in Trees())
        {
            if (tree.Id <= 0) continue;
            bool unlocked = s.TalentEffectsEnabled && Applies(tree)
                && _functions.ConditionMet(s, tree.Condition) && total >= tree.NeedLevel;
            state.UnlockedTrees[tree.Id] = unlocked;
            // (inferred) the oath cap is the largest cap marker on an eligible, unlocked tree.
            if (unlocked) oathMax = Math.Max(oathMax ?? long.MinValue, tree.PvpLimit);
        }
        state.BattleOathMaxLevel = oathMax ?? 0;
        // Every configured node is rewritten below, so existing entries are updated in place (a login used to
        // rebuild both maps several times: ~22% of the loop's allocations in a login storm); entries of nodes the
        // table no longer has are dropped as the old clear did.
        var nodes = Nodes();
        // Activation follows the graph in tiers: an ordinary node opens from a maxed parent (a choice recorded by any
        // plan counts), a SkillPoint gate from its own maxed parent, and the branch/option members from the tier that
        // owns them. (official evidence) the client's own save has all 134 nodes reachable that way active — every
        // branch and option of an opened gate, not only the picked one — while the nodes below an unmaxed parent stay
        // closed, so the tiers are what decides.
        var activated = new HashSet<long>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var node in nodes)
            {
                if (activated.Contains(node.Id) || !state.UnlockedTrees.GetValueOrDefault(node.Tree)) continue;
                bool opens = node.Type == PvpNodeType ? state.BattleOathMaxLevel > 0
                    : node.Start || TierOpen(state, node, activated);
                if (opens && activated.Add(node.Id)) changed = true;
            }
        }
        var plans = state.Plan.Values.ToArray(); // once, not two map views and enumerators per node
        foreach (var node in nodes)
        {
            long id = node.Id;
            bool pvp = node.Type == PvpNodeType;
            bool open = activated.Contains(id);
            state.UnlockedNode[id] = open;
            if (pvp && state.TalentNodes.TryGetValue(id, out var oath))
            {
                oath.TalentId = id;
                oath.CurrentLevel = state.BattleOathLevel; // the oath level is its own shared track
                oath.IsActivated = open;
            }
            else if (state.TalentNodes.TryGetValue(id, out var global))
            {
                global.TalentId = id;
                global.IsActivated = open;
                if (pvp) global.CurrentLevel = state.BattleOathLevel;
            }
            else if (!IsChoice(node.Type))
                state.TalentNodes[id] = new DBSeasonTalentNode { TalentId = id, CurrentLevel = pvp ? state.BattleOathLevel : 0, IsActivated = open };
            foreach (var plan in plans)
                if (plan.TalentNodes.TryGetValue(id, out var selected))
                {
                    selected.TalentId = id;
                    selected.IsActivated = open;
                    // The DAL overlays selected plan nodes after global nodes, including the shared oath node.
                    if (pvp) selected.CurrentLevel = state.BattleOathLevel;
                }
        }
        if (state.UnlockedNode.Count != _nodeIds!.Count)
            foreach (long stale in state.UnlockedNode.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.UnlockedNode.Remove(stale);
        if (state.TalentNodes.Count != _nodeIds.Count)
            foreach (long stale in state.TalentNodes.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.TalentNodes.Remove(stale);
    }

    private static bool IsChoice(long type) => type is ChoiceNodeType or ProfessionBranchType or ProfessionOptionType;

    /// <summary>A node's level where the plan-owner keeps it: choices in the active plan, everything else globally.</summary>
    private long LevelOf(DBSeasonTalent state, SeasonTalentPlan plan, TalentNode node) => node.Type == PvpNodeType
        ? state.BattleOathLevel
        : IsChoice(node.Type)
            ? plan.TalentNodes.TryGetValue(node.Id, out var selected) ? Math.Max(0, selected.CurrentLevel) : 0
            : Math.Max(0, state.TalentNodes.TryGetValue(node.Id, out var global) ? global.CurrentLevel : 0);

    /// <summary>The highest level a node holds in any map: the global purchases plus every plan's choice.</summary>
    private long HighestLevel(DBSeasonTalent state, long id)
    {
        long level = state.TalentNodes.TryGetValue(id, out var global) ? global.CurrentLevel : 0;
        foreach (var plan in state.Plan.Values)
            if (plan.TalentNodes.TryGetValue(id, out var choice) && choice.CurrentLevel > level) level = choice.CurrentLevel;
        return level;
    }

    private sealed record TalentNode(long Id, long Tree, long Type, long NeedLevel, bool Start, long LevelGroup);
    private sealed record TalentTree(long Id, double?[] Books, long Condition, long NeedLevel, long PvpLimit);

    /// <summary>SeasonTalentTreeConfig's rows (table order), read once: a login rebuilt the unlocks several times,
    /// each time looking every field up in the JSON rows (stage G).</summary>
    private IReadOnlyList<TalentTree> Trees() => _trees ??= [.. _data.Table("SeasonTalentTreeConfig").Values.Select(row =>
        new TalentTree(GameData.Int(row, "_iId"),
            row.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array
                ? [.. books.EnumerateArray().Select(GameData.Number)] : [],
            GameData.Int(row, "_iTreeCondition"), GameData.Int(row, "_iNeedLevel"), GameData.Int(row, "_iUpPvPLvLimit")))];
    private TalentTree[]? _trees;

    /// <summary>SeasonTalentConfig's nodes (id &gt; 0, table order) and each node's same-tree parents with their caps,
    /// indexed once: the activation pass scanned all 232 rows for every node of every rebuild.</summary>
    private IReadOnlyList<TalentNode> Nodes()
    {
        if (_nodes != null) return _nodes;
        var rows = _data.Table("SeasonTalentConfig").Values.ToArray();
        var parents = new Dictionary<(long Node, long Tree), List<(long Parent, long Max)>>();
        foreach (var candidate in rows)
        {
            if (!candidate.TryGetProperty("_kAfterids", out var after) || after.ValueKind != JsonValueKind.Array) continue;
            long tree = GameData.Int(candidate, "_iTalentTreeID"), parent = GameData.Int(candidate, "_iId"), max = GameData.Int(candidate, "_iMaxLevel");
            foreach (var v in after.EnumerateArray())
                if (GameData.Number(v) is { } child)
                {
                    var key = ((long)child, tree);
                    if (!parents.TryGetValue(key, out var list)) parents[key] = list = [];
                    list.Add((parent, max));
                }
        }
        _parents = parents.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var nodes = rows.Select(r => new TalentNode(GameData.Int(r, "_iId"), GameData.Int(r, "_iTalentTreeID"), GameData.Int(r, "_iType"),
            GameData.Int(r, "_iNeedLevel"), GameData.Int(r, "_iIsStartPoint") == 1, GameData.Int(r, "_iLevelGroupId"))).Where(n => n.Id > 0).ToArray();
        _nodeIds = [.. nodes.Select(n => n.Id)];
        _nodeById = nodes.ToDictionary(n => n.Id);
        return _nodes = nodes;
    }

    private TalentNode[]? _nodes;
    private HashSet<long>? _nodeIds;
    private Dictionary<long, TalentNode>? _nodeById;
    private Dictionary<(long Node, long Tree), (long Parent, long Max)[]>? _parents;

    /// <summary>
    /// Whether an activated parent opens a node: an ordinary node (and a SkillPoint gate) needs a maxed parent, a
    /// Profession_L branch needs its gate open and a Profession_M option needs its branch open. (official evidence) the
    /// client's own save activates every branch and option under an opened gate, not only the picked one.
    /// </summary>
    private bool TierOpen(DBSeasonTalent state, TalentNode node, IReadOnlySet<long> activated)
    {
        Nodes();
        var parents = _parents!.GetValueOrDefault((node.Id, node.Tree), []);
        if (node.Type is ProfessionBranchType or ProfessionOptionType)
        {
            long wanted = node.Type == ProfessionBranchType ? ChoiceNodeType : ProfessionBranchType;
            return parents.Any(p => activated.Contains(p.Parent) && _nodeById!.GetValueOrDefault(p.Parent)?.Type == wanted);
        }
        return parents.Any(p => p.Max > 0 && HighestLevel(state, p.Parent) >= p.Max);
    }

    /// <summary>The "total Talent Lv" a tree's or node's _iNeedLevel names: the grade the talent panel shows
    /// (UI_Sys_Skill_SeasonTalentPanelCtrl._buildGradeNodeIDs/_refreshGrade), the sum of the active plan's effective
    /// map — the bought levels plus this plan's chosen Profession_L/Profession_M members — except the SkillPoint gates.
    /// The battle oath counts at its shared level. A changed choice zeroes the old path before it sets the new one, so
    /// switching never raises the total. (inferred) The server checks the total the client shows.</summary>
    private long TalentLevel(DBSeasonTalent state) => state.Plan.TryGetValue(state.CurPlanId, out var plan)
        ? TotalLevel(state, plan, _data, includeOath: true) : 0;

    /// <summary>The same total for callers that only hold the durable state (achievements and task counters).</summary>
    internal static long TotalLevel(DBSeasonTalent state, SeasonTalentPlan plan, GameData data, bool includeOath)
    {
        long total = 0;
        foreach (var (id, node) in state.TalentNodes)
        {
            if (!includeOath && id == OathNode) continue;
            if (data.Row("SeasonTalentConfig", id) is not { } row || GameData.Int(row, "_iType") == ChoiceNodeType) continue;
            total += Math.Max(0, node.CurrentLevel);
        }
        foreach (var (id, node) in plan.TalentNodes)
        {
            if (id == OathNode) continue;
            if (data.Row("SeasonTalentConfig", id) is not { } row) continue;
            long type = GameData.Int(row, "_iType");
            if (type == ChoiceNodeType || type == PvpNodeType) continue;
            if (state.TalentNodes.ContainsKey(id)) continue; // the plan only overrides its own members
            total += Math.Max(0, node.CurrentLevel);
        }
        return total;
    }

    /// <summary>Effect rows by (group, level, career). CharacterService.Stats asks for every bought node, and the table does
    /// not change once loaded, so it is scanned once per key.</summary>
    private readonly ConcurrentDictionary<(long Group, long Level, int Career), JsonElement?> _effects = new();

    private JsonElement? Effect(long group, long level, int career) =>
        _effects.GetOrAdd((group, level, career), static (key, self) => self.FindEffect(key.Group, key.Level, key.Career), this);

    private JsonElement? FindEffect(long group, long level, int career)
    {
        JsonElement? generic = null;
        foreach (var row in _data.Table("SeasonTalentEffectConfig").Values)
        {
            if (GameData.Int(row, "_iGroup") != group || GameData.Int(row, "_iLevel") != level) continue;
            if (!row.TryGetProperty("_iJobId", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0)
                generic = row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == career)) return row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == 0)) generic = row;
        }
        return generic; // Lua CFG GetCfgByJob falls back to job 0.
    }

    private static bool HasEntries(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static bool Supported(JsonElement row) => HasEntries(row, "_kAttrs") && HasEntries(row, "_kCosts")
        && !HasEntries(row, "_kSpecialEffectList") && !HasEntries(row, "_kProfessionID")
        && GameData.Int(row, "_iSkillPoint") == 0 && GameData.Int(row, "_kSkillPointCosts") == 0;

    private List<(int Entry, long Count)>? Costs(JsonElement effect)
    {
        var costs = new List<(int, long)>();
        foreach (var (id, amount) in GameData.Pairs(effect, "_kCosts"))
        {
            if (id <= 0 || id > int.MaxValue || amount <= 0 || amount != Math.Truncate(amount)
                || amount > long.MaxValue || _data.Row("ItemConfig", id) == null) return null;
            costs.Add(((int)id, (long)amount));
        }
        return costs.Count > 0 ? costs : null;
    }

    /// <summary>83550: lock validation, payment, plan/oath mutation and durable save as one role operation.</summary>
    public async Task UpgradeAsync(GameSession s, SeasonTalentUpgradeReq req)
    {
        if (_data.Row("SeasonTalentConfig", req.TalentId) is { } requested
            && GameData.Int(requested, "_iType") is ProfessionBranchType or ProfessionOptionType)
        {
            await SelectProfessionAsync(s, req);
            return;
        }
        int result;
        using (await CharacterService.Lock(s))
        {
            result = UpgradeLocked(s, req.TalentId, out var cost, out var node, out var isOath);
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                long type = GameData.Int(_data.Row("SeasonTalentConfig", node)!.Value, "_iType");
                if (isOath) next.BattleOathLevel++;
                else if (IsChoice(type)) next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel++; // choice members are per plan
                else next.TalentNodes[node].CurrentLevel++; // (official evidence) bought levels are global
                BuildUnlocks(s, next);
                long level = isOath ? next.BattleOathLevel
                    : IsChoice(type) ? next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel : next.TalentNodes[node].CurrentLevel;
                List<InvItem>? paid = null;
                try
                {
                    var economy = _character.ProgressionEconomy(s, "cd13c/season-talent-upgrade", cost!, [],
                        new { Node = node, Oath = isOath, Level = level, Plan = next.CurPlanId });
                    paid = await _character.ExchangeLockedAsync(s, cost!, [], popup: false, sync: false,
                        persist: tx => { tx.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray()); tx.WithEconomy(economy); },
                        committed: () => s.SeasonTalent = next, asyncCommit: true);
                }
                catch (Exception e) when (e is SqliteException or Accounts.Persistence.PersistenceException
                    or InvalidOperationException)
                {
                    _log.LogError(e, "Talent purchase {Node} for role {Role} rolled back", node, s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
                if (result == 0 && paid == null) result = _character.ShortageOf(s, cost!);
                if (paid != null)
                {
                    await _character.ItemsChangedAsync(s, stats: false); // 5009 before 5050 and the reply
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                    await _character.RefreshStatsAsync(s);
                    await RefreshEffectsAsync(s);
                }
            }
        }
        _log.LogInformation("{Name} talent {Node}: {Result}", s.Role?.Name, req.TalentId, result);
        await s.SendGameAsync(Upgrade, new SeasonTalentUpgradeResp { Result = result, TalentId = req.TalentId });
    }

    private int UpgradeLocked(GameSession s, long id, out List<(int Entry, long Count)>? cost, out long node, out bool oath)
    {
        cost = null; node = id; oath = false;
        if (s.SeasonTalent == null || s.Progress == null || !Available(s)) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("SeasonTalentConfig", id) is not { } row) return ErrorCodes.TalentInvalid;
        BuildUnlocks(s);
        var state = s.SeasonTalent;
        if (!state.UnlockedNode.GetValueOrDefault(id)) return ErrorCodes.TalentLocked;
        oath = GameData.Int(row, "_iType") == PvpNodeType;
        bool choice = IsChoice(GameData.Int(row, "_iType"));
        long current = oath ? state.BattleOathLevel
            : choice ? state.Plan[state.CurPlanId].TalentNodes[id].CurrentLevel
            : state.TalentNodes.TryGetValue(id, out var bought) ? bought.CurrentLevel : 0;
        long cap = oath ? state.BattleOathMaxLevel : GameData.Int(row, "_iMaxLevel");
        if (cap <= 0 || current >= cap) return ErrorCodes.SkillMaxLevel;
        long group = GameData.Int(row, "_iLevelGroupId");
        if (group <= 0 || Effect(group, current + 1, s.Progress.Career) is not { } effect || !Supported(effect))
            return ErrorCodes.FeatureUnavailable; // no invented connector, missing group or profession/special engine
        cost = Costs(effect);
        if (cost == null) return ErrorCodes.FeatureUnavailable;
        return cost.GroupBy(c => c.Entry).Any(g => _character.CountOf(s, g.Key) < g.Sum(c => c.Count))
            ? _character.ShortageOf(s, cost) : 0;
    }

    /// <summary>83552: all plans have complete node maps so the client's overlay clears old levels; (inferred) 3s cooldown.</summary>
    public async Task SetPlanAsync(GameSession s, SeasonTalentSetCurPlanReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount ? ErrorCodes.Parameter
                : DateTime.UtcNow - s.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds) ? ErrorCodes.Cooldown : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.CurPlanId = req.PlanId;
                BuildUnlocks(s, next);
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/set-plan", _ =>
                    {
                        s.SeasonTalent = next;
                        s.TalentPlanSwitchUtc = DateTime.UtcNow;
                    });
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                    await _character.RefreshStatsAsync(s);
                    await RefreshEffectsAsync(s);
                }
                catch (Exception e) when (e is SqliteException or Accounts.Persistence.PersistenceException
                    or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan switch for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(SetPlan, new SeasonTalentSetCurPlanResp { Result = result, Req = req });
    }

    /// <summary>83553: (inferred) trim, nonempty/no-control and eight-Unicode-scalar server validation from PublicParamConfig.SchemeNameNumMax=8.</summary>
    public async Task RenameAsync(GameSession s, SeasonTalentSetPlanNameReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            string name = req.PlanName.Trim();
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount || name.Length == 0 || name.EnumerateRunes().Count() > NameMaxRunes
                    || name.Any(char.IsControl) ? ErrorCodes.Parameter : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.Plan[req.PlanId].PlanName = name;
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/rename", _ => s.SeasonTalent = next);
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                }
                catch (Exception e) when (e is SqliteException or Accounts.Persistence.PersistenceException
                    or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan rename for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(RenamePlan, new SeasonTalentSetPlanNameResp { Result = result, Req = req });
    }

    /// <summary>Only bought target-level totals contribute once; profession rows add their special effects'
    /// attributes (SeasonTalentService.Effects).</summary>
    public IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        foreach (var row in ActiveEffects(s))
        {
            if (Supported(row)) foreach (var pair in GameData.Pairs(row, "_kAttrs")) yield return pair;
            foreach (var pair in SpecialAttributes(s, row)) yield return pair;
        }
    }

    /// <summary>
    /// The active plan's effective map at their bought levels — the global purchases and the plan's chosen
    /// Profession_L/Profession_M members, exactly the levels the client's overlay shows (LC_DAL_SeasonTalent) — plus
    /// the oath's row at its shared level. Only nodes the client draws as activated contribute.
    /// </summary>
    private IEnumerable<JsonElement> ActiveEffects(GameSession s)
    {
        if (!Available(s) || s.SeasonTalent is not { } state || s.Progress == null
            || !state.Plan.TryGetValue(state.CurPlanId, out var plan)) yield break;
        foreach (var node in Nodes())
        {
            if (node.Id == OathNode || !state.UnlockedNode.GetValueOrDefault(node.Id)) continue;
            long level = LevelOf(state, plan, node);
            if (level <= 0 || node.LevelGroup <= 0) continue;
            if (Effect(node.LevelGroup, level, s.Progress.Career) is { } row) yield return row;
        }
        if (state.BattleOathLevel > 0 && state.UnlockedNode.GetValueOrDefault(OathNode)
            && _data.Row("SeasonTalentConfig", OathNode) is { } oath
            && Effect(GameData.Int(oath, "_iLevelGroupId"), state.BattleOathLevel, s.Progress.Career) is { } oathRow)
            yield return oathRow;
    }

}
