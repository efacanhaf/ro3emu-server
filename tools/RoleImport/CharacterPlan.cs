using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace RoleImport;

/// <summary>
/// Everything one import writes for a role, computed from a snapshot and nothing else: the role row's
/// columns, one blob per dataset, the item rows, and the official-guid to local-guid map that ties them
/// together. No database access happens here, so the whole mapping is unit-testable.
///
/// What is copied (all of it combat state):
///   role row   level / job_level / exp / job_exp / career / sex / hair / stats / bonus_points / skills /
///              skill_bar / paragon_level / paragon_exp / currency
///   blobs      stat_plans (BaseAttrInfo), skill_plans (DBSkillData), season_talent (DBSeasonTalent),
///              season_page (DBSeasonPage), season_refine (DBSeasonRefine), equip_plans (DBEquipInfo plan
///              fields), equip_levels (JSON EquipLevels from the refine/strength/luck maps), card_plans
///              (DBStorageHoleInfo), emblems (JSON EmblemState from DBEmblemInfo = Soul Echo), genre
///              (DBGenre), growth_class (DBGrowthClassInfo), paragon (DBParagonLevel), title (DBTitleInfo),
///              career_special (DBCareerSpecial), heroic_spirit (DBOdinStatue out of DBHeroicSpiritInfo),
///              totems (DBTotemInfo), pets (DBPetInfo), mounts (JSON MountService.SavedState carrying
///              DBMountsInfo), wardrobe (DBWardRobeInfo), social_outfit (DBSocialOutfit), lifejob
///              (DBLifeJobInfo), life_restrict (DBRestrictInfo), library (DBLibraryInfo), bag_state
///              (DBItemsInfo without the item list: cooldowns, quick use, shortcut bar)
///   items      every DBItemsInfo pack (1,2,3,4,5,100), every worn item of DBEquipInfo.items and every
///              card socketed in DBItemsInfo.storagePack
///
/// Not copied, by decision (see docs/tools/role-import.md): quests/tasks, achievements, daily/activities/
/// lottery/shop, mail, auction, trade, stalls, guild, friends, team, and the settings tied to the official
/// account. None of them is a combat input; the client rebuilds its UI state from the server.
/// </summary>
public sealed class CharacterPlan
{
    private readonly SnapshotDocument _snapshot;
    private readonly long _firstGuid;
    private readonly Dictionary<long, long> _guids = new();
    private readonly List<string> _notes = new();

    private CharacterPlan(SnapshotDocument snapshot, long firstGuid)
    {
        _snapshot = snapshot;
        _firstGuid = firstGuid;
    }

    public Dictionary<string, object?> Columns { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Columns written as NULL instead of a snapshot value. `settings` (DBSettingInfo) is the client's own
    /// preferences: its values are binary inside the text dump, and the old copy's "auto assign points"
    /// preference spent 2 of the 8 free stat points on every login. Cleared, the client starts from its defaults
    /// (verified: 65,67 stays 65,67 after a login).
    /// </summary>
    public HashSet<string> ClearedBlobs { get; } = new(StringComparer.Ordinal) { "settings" };

    public List<InvItem> Items { get; } = new();
    public IReadOnlyDictionary<long, long> GuidMap => _guids;
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Reads the snapshot into a complete import plan; <paramref name="firstGuid"/> is the first item guid to use.</summary>
    public static CharacterPlan Build(SnapshotDocument snapshot, long firstGuid, int localBaseStatPoints)
    {
        var plan = new CharacterPlan(snapshot, firstGuid);
        plan.CollectGuids();
        plan.BuildItems();
        plan.BuildBlobs();
        plan.BuildColumns(localBaseStatPoints);
        return plan;
    }

    // ---- guids -------------------------------------------------------------------------------------

    /// <summary>Every official instance id the snapshot references, in a stable order, gets a local id.</summary>
    private void CollectGuids()
    {
        foreach (long guid in OfficialGuids())
            _guids[guid] = _firstGuid + _guids.Count;
    }

    private IEnumerable<long> OfficialGuids()
    {
        var seen = new HashSet<long>();
        if (_snapshot.TryMessage("DBITEMSINFO", out var bag))
        {
            foreach (var (_, pack) in Map(bag, "packInfo"))
                foreach (var (guid, _) in Map(pack, "items"))
                    if (seen.Add(guid)) yield return guid;
            foreach (var (_, _, slot) in CardSlots(bag))
                if (slot.TryGetProperty("itemGuid", out var card) && card.TryGetInt64(out long guid) && guid != 0 && seen.Add(guid))
                    yield return guid;
        }
        if (_snapshot.TryMessage("DBEQUIPINFO", out var equip))
            foreach (var (guid, _) in Map(equip, "items"))
                if (seen.Add(guid)) yield return guid;
    }

    // ---- items -------------------------------------------------------------------------------------

    private void BuildItems()
    {
        var nextSort = new Dictionary<long, long>();
        long SortFor(long pack) => nextSort[pack] = nextSort.GetValueOrDefault(pack) + 1;

        if (_snapshot.TryMessage("DBITEMSINFO", out var bag))
        {
            foreach (var (packId, pack) in Map(bag, "packInfo").OrderBy(p => p.Key))
            {
                if (packId is not (1 or 2 or 3 or 4 or 5 or 100)) { _notes.Add($"pack {packId} ignored (not a bag pack)"); continue; }
                var sorted = SortedIndex(pack);
                foreach (var (guid, node) in Map(pack, "items").OrderBy(p => sorted.TryGetValue(p.Key, out long sort) ? sort : long.MaxValue).ThenBy(p => p.Key))
                {
                    long sort = sorted.TryGetValue(guid, out long known) && known > 0 ? known : SortFor(packId);
                    if (sort > nextSort.GetValueOrDefault(packId)) nextSort[packId] = sort;
                    Items.Add(Item(guid, node, (int)packId, sort, 0));
                }
            }
            foreach (var (grid, socket, node) in CardSlots(bag))
            {
                if (!node.TryGetProperty("itemGuid", out var id) || !id.TryGetInt64(out long guid) || guid == 0) continue;
                // CardService.CardGrid: the socket is -1 * (equip grid * 10 + socket), the card lives in the card pack.
                Items.Add(Item(guid, node, 3, SortFor(3), -(grid * 10 + socket)));
            }
        }
        if (_snapshot.TryMessage("DBEQUIPINFO", out var equip))
        {
            var worn = new Dictionary<long, int>();
            if (equip.TryGetProperty("equipPlans", out var plans) && plans.ValueKind == JsonValueKind.Object)
            {
                int current = equip.TryGetProperty("currentEquipPlan", out var index) ? (int)Long(index) : 0;
                var active = plans.TryGetProperty(current.ToString(CultureInfo.InvariantCulture), out var plan) ? plan : default;
                if (active.ValueKind == JsonValueKind.Object && active.TryGetProperty("itemsEquipGrid", out var grids))
                    // itemsEquipGrid is map<equip grid, item guid>: the grid is the key, the guid the value.
                    foreach (var (grid, item) in Map(grids, null).OrderBy(p => p.Key))
                        if (item.TryGetInt64(out long guid) && guid != 0 && !worn.ContainsKey(guid)) worn[guid] = (int)grid;
            }
            foreach (var (guid, node) in Map(equip, "items").OrderBy(p => p.Key))
            {
                if (Items.Any(i => i.Guid == Local(guid))) continue;
                int grid = worn.GetValueOrDefault(guid, 0);
                Items.Add(Item(guid, node, 2, SortFor(2), grid));
            }
        }
    }

    private static Dictionary<long, long> SortedIndex(JsonElement pack) =>
        pack.TryGetProperty("sortedIndex", out var sorted) && sorted.ValueKind == JsonValueKind.Object
            ? Map(sorted, null).ToDictionary(p => p.Key, p => Long(p.Value))
            : new Dictionary<long, long>();

    private InvItem Item(long official, JsonElement node, int pack, long sort, int grid)
    {
        var item = new InvItem
        {
            Guid = Local(official),
            Entry = (int)Long(node, "itemEntry"),
            Stack = (int)Long(node, "stack", 1),
            Pack = pack,
            Sort = sort,
            Grid = grid,
            IsLocked = node.TryGetProperty("isLocked", out var locked) && locked.ValueKind == JsonValueKind.True,
        };
        if (node.TryGetProperty("itemGuid", out var declared) && declared.TryGetInt64(out long inside) && inside != official)
            throw new InvalidDataException($"Item {official}: itemGuid says {inside}.");
        if (node.TryGetProperty("itemBindInfo", out var bind))
        {
            item.BindType = (int)Long(bind, "itemBindType");
            item.BindExpire = Long(bind, "expireTimeStamp");
        }
        if (node.TryGetProperty("itemInfos", out var infos) && infos.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, value) in Map(infos, null))
            {
                int field = (int)key;
                if (value.ValueKind != JsonValueKind.Object) continue;
                // ItemAttrValue is a oneof: the dump prints the case name ("val") next to the value.
                if (value.TryGetProperty("longVal", out var longValue)) (item.Attrs ??= new())[field] = Long(longValue);
                else if (value.TryGetProperty("strVal", out var strValue)) (item.StringAttrs ??= new())[field] = strValue.GetString() ?? "";
            }
        }
        if (node.TryGetProperty("enchantment", out var enchantment) && enchantment.ValueKind == JsonValueKind.Object)
            foreach (var (row, value) in Map(enchantment, null))
                item.Enchantments[(int)row] = new ItemEnchantment
                {
                    GetTime = Long(value, "getTime"),
                    Attr = value.TryGetProperty("attr", out var attr)
                        ? Map(attr, null).ToDictionary(p => (int)p.Key, p => (int)Long(p.Value))
                        : new Dictionary<int, int>(),
                };
        return item;
    }

    private long Local(long official) =>
        _guids.TryGetValue(official, out long local) ? local
            : throw new InvalidDataException($"Item guid {official} was never allocated; the snapshot references an unknown instance.");

    private IEnumerable<(int Grid, int Socket, JsonElement Node)> CardSlots(JsonElement bag)
    {
        if (!bag.TryGetProperty("storagePack", out var storage) || !storage.TryGetProperty("cardInfo", out var cardInfo)
            || cardInfo.ValueKind != JsonValueKind.Object) yield break;
        foreach (var (grid, slots) in Map(cardInfo, null).OrderBy(p => p.Key))
            if (slots.TryGetProperty("cardSlotInfo", out var socketMap))
                foreach (var (socket, node) in Map(socketMap, null).OrderBy(p => p.Key))
                    yield return ((int)grid, (int)socket, node);
    }

    // ---- blobs -------------------------------------------------------------------------------------

    private void BuildBlobs()
    {
        Blob("stat_plans", "BASEATTRINFO", BaseAttrInfo.Descriptor);
        Blob("skill_plans", "DBSKILLDATA", DBSkillData.Descriptor);
        Blob("season_talent", "DBSEASONTALENT", DBSeasonTalent.Descriptor);
        Blob("season_page", "DBSEASONPAGE", DBSeasonPage.Descriptor);
        Blob("season_refine", "DBSEASONREFINE", DBSeasonRefine.Descriptor);
        Blob("card_plans", "DBSTORAGEHOLEINFO", DBStorageHoleInfo.Descriptor);
        Blob("genre", "DBGENRE", DBGenre.Descriptor);
        Blob("growth_class", "DBGROWTHCLASSINFO", DBGrowthClassInfo.Descriptor);
        Blob("paragon", "DBPARAGONLEVEL", DBParagonLevel.Descriptor);
        Blob("title", "DBTITLEINFO", DBTitleInfo.Descriptor);
        Blob("career_special", "DBCAREERSPECIAL", DBCareerSpecial.Descriptor);
        Blob("totems", "DBTOTEMINFO", DBTotemInfo.Descriptor);
        Blob("pets", "DBPETINFO", DBPetInfo.Descriptor);
        Blob("wardrobe", "DBWARDROBEINFO", DBWardRobeInfo.Descriptor);
        Blob("social_outfit", "DBSOCIALOUTFIT", DBSocialOutfit.Descriptor);
        Blob("lifejob", "DBLIFEJOBINFO", DBLifeJobInfo.Descriptor);
        Blob("life_restrict", "DBRESTRICTINFO", DBRestrictInfo.Descriptor);
        Blob("library", "DBLIBRARYINFO", DBLibraryInfo.Descriptor);
        Blob("tasks", "DBTASKINFO", DBTaskInfo.Descriptor);
        Blob("guides", "DBGUIDEINFO", DBGuideInfo.Descriptor);
        BuildTaskRuntime();
        BuildEquipPlans();
        BuildEquipLevels();
        BuildEmblems();
        BuildMounts();
        BuildBagState();
        BuildHeroicSpirit();
    }

    private void Blob(string column, string message, Google.Protobuf.Reflection.MessageDescriptor descriptor)
    {
        if (!_snapshot.TryMessage(message, out var json)) { _notes.Add($"{column}: no {message} in the snapshot"); return; }
        var proto = ProtoJson.Parse(descriptor, json);
        GuidRemap.Apply(proto, _guids);
        Blobs[column] = proto.ToByteArray();
    }

    /// <summary>Only the plan fields: BuildEquip rebuilds the worn items and the refine levels from the item rows and equip_levels.</summary>
    private void BuildEquipPlans()
    {
        if (!_snapshot.TryMessage("DBEQUIPINFO", out var json)) { _notes.Add("equip_plans: no DBEQUIPINFO in the snapshot"); return; }
        var full = (DBEquipInfo)ProtoJson.Parse(DBEquipInfo.Descriptor, json);
        var plans = new DBEquipInfo
        {
            CurrentEquipPlan = full.CurrentEquipPlan,
            Inited = true,
            RefineEffectLv = 0,
        };
        foreach (var (index, plan) in full.EquipPlans) plans.EquipPlans[index] = plan;
        foreach (var (type, craft) in full.CraftInfo) plans.CraftInfo[type] = craft;
        foreach (var (rank, count) in full.CraftRankCount) plans.CraftRankCount[rank] = count;
        foreach (var (grid, card) in full.CardInfo) plans.CardInfo[grid] = card;
        GuidRemap.Apply(plans, _guids);
        Blobs["equip_plans"] = plans.ToByteArray();
    }

    /// <summary>Game.EquipLevels: strengthen, refine and refine luck per equipment grid (1..7).</summary>
    private void BuildEquipLevels()
    {
        if (!_snapshot.TryMessage("DBEQUIPINFO", out var json)) { _notes.Add("equip_levels: no DBEQUIPINFO in the snapshot"); return; }
        var levels = new EquipLevels();
        Levels(json, "itemsRefine", levels.Refine);
        Levels(json, "itemsStrength", levels.Strength);
        Levels(json, "refineLuckyAdd", levels.Luck);
        Blobs["equip_levels"] = JsonSerializer.SerializeToUtf8Bytes(levels);
    }

    private static void Levels(JsonElement json, string field, Dictionary<int, int> target)
    {
        if (!json.TryGetProperty(field, out var map) || map.ValueKind != JsonValueKind.Object) return;
        foreach (var (grid, value) in Map(map, null)) target[(int)grid] = (int)Long(value);
    }

    /// <summary>EmblemService.EmblemState out of DBEmblemInfo: the five Soul Echo plans, their slots and the published power.</summary>
    private void BuildEmblems()
    {
        if (!_snapshot.TryMessage("DBEMBLEMINFO", out var json)) { _notes.Add("emblems: no DBEMBLEMINFO in the snapshot"); return; }
        var state = new EmblemService.EmblemState { CurrentPlan = (int)Long(json, "currentPlan") };
        long season = 0;
        if (json.TryGetProperty("emblePlans", out var plans) && plans.ValueKind == JsonValueKind.Object)
        {
            foreach (var (index, plan) in Map(plans, null))
            {
                var slots = new Dictionary<int, long>();
                foreach (var (slot, guid) in PlanSlots(plan))
                    if (_guids.TryGetValue(guid, out long local)) slots[(int)slot] = local;
                    else { _notes.Add($"emblems: slot {slot} of plan {index} references {guid}, which is not owned"); }
                state.Plans[(int)index] = new EmblemService.Plan
                {
                    Slots = slots,
                    Name = plan.TryGetProperty("planName", out var name) ? name.GetString() ?? "" : "",
                };
                if (plan.TryGetProperty("power", out var power)) season = Math.Max(season, Long(power, "seasonPower"));
            }
        }
        state.HistorySeasonPower = season;
        if (_snapshot.TryMessage("ROLEBASEINFO", out var baseInfo))
        {
            var current = baseInfo.TryGetProperty("curPower", out var cur) ? cur : default;
            var history = baseInfo.TryGetProperty("historyMaxPower", out var max) ? max : default;
            state.Power = new PowerService.Record
            {
                CurrentNormal = Long(current, "normalPower"),
                CurrentSeason = Long(current, "seasonPower"),
                HistoryNormal = Long(history, "normalPower"),
                HistorySeason = Long(history, "seasonPower"),
            };
        }
        Blobs["emblems"] = JsonSerializer.SerializeToUtf8Bytes(state);
    }

    private IEnumerable<(long Slot, long Guid)> PlanSlots(JsonElement plan)
    {
        if (!plan.TryGetProperty("setUpEmblem", out var slots) || slots.ValueKind != JsonValueKind.Object) yield break;
        foreach (var (slot, guid) in Map(slots, null))
            if (guid.TryGetInt64(out long id) && id != 0) yield return (slot, id);
    }

    /// <summary>MountService.SavedState: the 5012 dataset inside the JSON state the service persists.</summary>
    private void BuildMounts()
    {
        if (!_snapshot.TryMessage("DBMOUNTSINFO", out var json)) { _notes.Add("mounts: no DBMOUNTSINFO in the snapshot"); return; }
        var info = ProtoJson.Parse(DBMountsInfo.Descriptor, json);
        GuidRemap.Apply(info, _guids);
        Blobs["mounts"] = JsonSerializer.SerializeToUtf8Bytes(new MountService.SavedState { Info = info.ToByteArray() });
    }

    /// <summary>
    /// DBHeroicSpiritInfo carries the Valhalla statue; the server stores that message alone (DBOdinStatue).
    /// </summary>
    private void BuildHeroicSpirit()
    {
        if (!_snapshot.TryMessage("DBHEROICSPIRITINFO", out var json)) { _notes.Add("heroic_spirit: no DBHEROICSPIRITINFO in the snapshot"); return; }
        var full = (DBHeroicSpiritInfo)ProtoJson.Parse(DBHeroicSpiritInfo.Descriptor, json);
        Blobs["heroic_spirit"] = full.OdinStatue.ToByteArray();
    }

    /// <summary>
    /// The server's own quest side state (Game.TaskRuntimeState): which guides the tasks' TGuide objectives count as
    /// done. It is rebuilt from the imported DBGuideInfo so no step of the old local story survives, and the pending
    /// award debt of that story is dropped (task_followup empty).
    /// </summary>
    private void BuildTaskRuntime()
    {
        var runtime = new TaskRuntimeState();
        if (_snapshot.TryMessage("DBGUIDEINFO", out var json))
        {
            var guides = (DBGuideInfo)ProtoJson.Parse(DBGuideInfo.Descriptor, json);
            foreach (var id in guides.FinishedGuideIds) runtime.CompletedGuides.Add(id);
        }
        else _notes.Add("task_runtime: no DBGUIDEINFO in the snapshot");
        Blobs["task_runtime"] = JsonSerializer.SerializeToUtf8Bytes(runtime);
        Blobs["task_followup"] = "[]"u8.ToArray();
    }

    /// <summary>
    /// DBItemsInfo without the bag: the item rows are the item table. What stays is what the client mirrors
    /// from the 5009 dataset that is not an item: cooldowns, quick use, medicines, the shortcut bar and the
    /// currencies (BuildItems refills the packs from the role's items).
    /// </summary>
    private void BuildBagState()
    {
        if (!_snapshot.TryMessage("DBITEMSINFO", out var json)) { _notes.Add("bag_state: no DBITEMSINFO in the snapshot"); return; }
        var full = (DBItemsInfo)ProtoJson.Parse(DBItemsInfo.Descriptor, json);
        var state = new DBItemsInfo
        {
            CurrentArrowID = full.CurrentArrowID,
            MedicinesAutoUse = full.MedicinesAutoUse,
            ItemCds = full.ItemCds,
            ShareCds = full.ShareCds,
        };
        state.QuickUseInfos.Add(full.QuickUseInfos);
        state.ShortcutBarInfos.Add(full.ShortcutBarInfos);
        // A slot whose item is no longer owned (consumed) would keep pointing at an official guid.
        foreach (var slot in state.ShortcutBarInfos.Where(slot => slot.ItemGuid != 0 && !_guids.ContainsKey(slot.ItemGuid)))
        {
            _notes.Add($"bag_state: shortcut slot {slot.SlotIndex} referenced {slot.ItemGuid}, which is not owned");
            slot.ItemGuid = 0;
        }
        foreach (var (entry, times) in full.UseTimes) state.UseTimes[entry] = times;
        foreach (var (id, amount) in full.Currency) state.Currency[id] = amount;
        GuidRemap.Apply(state, _guids);
        Blobs["bag_state"] = state.ToByteArray();
    }

    // ---- role row ----------------------------------------------------------------------------------

    private void BuildColumns(int localBaseStatPoints)
    {
        if (!_snapshot.TryMessage("ROLEBASEINFO", out var role)) throw new InvalidDataException("Snapshot has no ROLEBASEINFO.");
        Columns["account"] = null; // filled by the caller: the snapshot's account is the official one
        Columns["sex"] = Long(role, "sex");
        Columns["career"] = Long(role, "career");
        Columns["level"] = Long(role, "level");
        Columns["job_level"] = Long(role, "jobLevel");
        Columns["exp"] = Long(role, "exp");
        Columns["job_exp"] = Long(role, "jobExp");
        Columns["create_time"] = Long(role, "createTime");
        Columns["starter_kit"] = 1L; // never hand a starter kit to an imported character

        Columns["hair"] = Appearance() ?? 0L;
        if (Appearance() is null) _notes.Add("hair: no appearance 7 in DBPERSONALINFO");

        BuildStats(localBaseStatPoints);
        BuildSkills();
        BuildParagon();
        BuildCurrency();
    }

    private long? Appearance()
    {
        if (!_snapshot.TryMessage("DBPERSONALINFO", out var personal)) return null;
        int current = (int)Long(personal, "curPlanId", 1);
        if (!personal.TryGetProperty("planInfos", out var plans) || plans.ValueKind != JsonValueKind.Object) return null;
        if (!plans.TryGetProperty(current.ToString(CultureInfo.InvariantCulture), out var plan)) return null;
        if (!plan.TryGetProperty("roleInfos", out var roles) || roles.ValueKind != JsonValueKind.Object) return null;
        foreach (var (_, info) in Map(roles, null))
            if (info.TryGetProperty("clientEditorData", out var editor)
                && editor.TryGetProperty("kAppearances", out var appearances)
                && appearances.ValueKind == JsonValueKind.Object
                && appearances.TryGetProperty("7", out var hair) && hair.TryGetInt64(out long id))
                return id;
        return null;
    }

    /// <summary>The six base attributes of the active plan; the points leftover become the role's free points.</summary>
    private void BuildStats(int localBaseStatPoints)
    {
        if (!_snapshot.TryMessage("BASEATTRINFO", out var json)) { _notes.Add("stats: no BASEATTRINFO in the snapshot"); return; }
        var attrs = (BaseAttrInfo)ProtoJson.Parse(BaseAttrInfo.Descriptor, json);
        if (!attrs.AllPlanBaseProperty.TryGetValue(attrs.CurPlanIndex, out var plan))
            throw new InvalidDataException($"BASEATTRINFO has no active plan {attrs.CurPlanIndex}.");
        var stats = Enumerable.Range(1, 6).Select(index => (int)plan.BaseProperty.GetValueOrDefault(index)).ToArray();
        if (stats.Any(value => value < 1)) throw new InvalidDataException("Active stat plan has an unset attribute.");
        Columns["stats"] = string.Join(',', stats);
        // RoleProgress.BonusPoints is the server's lever over the earned total (StartingStatPoints + BonusPoints
        // + the level table), and CharacterService clamps it at -StartingStatPoints. Setting it so the total
        // equals the official AllAttrPoints keeps the leftover points identical instead of inventing 48 extra.
        long bonus = attrs.AllAttrPoints - localBaseStatPoints;
        Columns["bonus_points"] = bonus;
        _notes.Add($"bonus_points: {bonus} (official total {attrs.AllAttrPoints} - local base {localBaseStatPoints})");
    }

    /// <summary>The active skill plan's assignments are the authoritative skills; the bar comes from its load list.</summary>
    private void BuildSkills()
    {
        if (!_snapshot.TryMessage("DBSKILLDATA", out var json)) { _notes.Add("skills: no DBSKILLDATA in the snapshot"); return; }
        var data = (DBSkillData)ProtoJson.Parse(DBSkillData.Descriptor, json);
        int current = data.CurrentPlanIndex;
        if (current < 0 || current >= data.SkillAssignPlans.Count)
            throw new InvalidDataException($"DBSKILLDATA has no active plan {current}.");
        var plan = data.SkillAssignPlans[current];
        Columns["skills"] = string.Join(',', plan.SkillAssigns
            .Where(assign => assign.Value.SkillLevel > 0)
            .OrderBy(assign => assign.Key)
            .Select(assign => $"{assign.Key}:{assign.Value.SkillLevel}"));
        var bar = new List<string>();
        for (int slot = 1; slot <= plan.ManualLoadSkill.Count; slot++)
        {
            var load = plan.ManualLoadSkill[slot - 1];
            if (load.SkillId > 0) bar.Add($"{slot}:{load.SkillId}");
        }
        Columns["skill_bar"] = string.Join(',', bar);
    }

    private void BuildParagon()
    {
        if (!_snapshot.TryMessage("DBPARAGONLEVEL", out var json)) { _notes.Add("paragon_level: no DBPARAGONLEVEL in the snapshot"); return; }
        Columns["paragon_level"] = Long(json, "lv");
        Columns["paragon_exp"] = Long(json, "exp");
    }

    private void BuildCurrency()
    {
        if (!_snapshot.TryMessage("DBITEMSINFO", out var json)) { _notes.Add("currency: no DBITEMSINFO in the snapshot"); return; }
        if (!json.TryGetProperty("currency", out var map) || map.ValueKind != JsonValueKind.Object) return;
        Columns["currency"] = string.Join(',', Map(map, null).OrderBy(p => p.Key).Select(p => $"{p.Key}:{Long(p.Value)}"));
    }

    // ---- json helpers ------------------------------------------------------------------------------

    /// <summary>Entries of a map object whose keys are integers (the probe prints numeric keys as `[n]`, the JSON as "n").</summary>
    private static IEnumerable<(long Key, JsonElement Value)> Map(JsonElement obj, string? field)
    {
        var target = field is null ? obj : obj.TryGetProperty(field, out var inner) ? inner : default;
        if (target.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in target.EnumerateObject())
        {
            if (!long.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long key))
                throw new FormatException($"Map key '{property.Name}' is not an integer.");
            yield return (key, property.Value);
        }
    }

    private static long Long(JsonElement value, string field, long fallback = 0) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var inner) ? Long(inner, fallback) : Long(value, fallback);

    private static long Long(JsonElement value, long fallback = 0) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => fallback,
        };
}
