using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using RoleImport;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The character importer (tools/RoleImport): a snapshot exported from a live client must become exactly
/// the rows the server reads back, with new local item guids and no reference left pointing at the
/// official ones.
///
/// Fixtures/snapshot-kaori.json is a real export (ro3/bot/ferramentas/exportar-personagem.ps1) with the
/// account and character name replaced by 'fixture', bag packs 1 and 5 cut down and every worn item, the
/// whole equipment/card packs and every Soul Echo instance the emblem plans reference kept.
/// </summary>
public sealed class RoleImportTests
{
    private const int FirstGuid = 1_000_001;

    private static SnapshotDocument Fixture() =>
        SnapshotDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-kaori.json"));

    private static CharacterPlan Build(SnapshotDocument snapshot) => CharacterPlan.Build(snapshot, FirstGuid, 512);

    [Fact]
    public void ProtoJsonKeepsInt64Exact()
    {
        // JsonParser would run this through a double and answer …192 (the defect this reader avoids).
        const string json = """{"items":{"302230089544616186":{"itemGuid":302230089544616186,"itemEntry":2050550}}}""";
        using var document = JsonDocument.Parse(json);
        var message = (DBEquipInfo)ProtoJson.Parse(DBEquipInfo.Descriptor, document.RootElement);
        Assert.Equal(302230089544616186L, message.Items[302230089544616186].ItemGuid);
    }

    [Fact]
    public void RepeatedFieldsAcceptLuaTables()
    {
        const string json = """{"id":7,"name":"Kaori","all":"x"}""";
        using var document = JsonDocument.Parse("""{"skillAssignPlans":{"1":{"name":"DPS"},"2":{"name":"Tank"}}}""");
        var data = (DBSkillData)ProtoJson.Parse(DBSkillData.Descriptor, document.RootElement);
        Assert.Equal(["DPS", "Tank"], data.SkillAssignPlans.Select(plan => plan.Name));
        // Unknown keys (oneof case names, Lua-only fields) never fail the message.
        using var extra = JsonDocument.Parse(json);
        Assert.NotNull(ProtoJson.Parse(DBTitleInfo.Descriptor, extra.RootElement));
    }

    [Fact]
    public void ImportAllocatesOneLocalGuidPerInstance()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        Assert.Equal(plan.GuidMap.Count, plan.Items.Count);
        Assert.Equal(plan.Items.Count, plan.Items.Select(item => item.Guid).Distinct().Count());
        Assert.All(plan.GuidMap.Values, guid => Assert.InRange(guid, FirstGuid, FirstGuid + plan.GuidMap.Count - 1));
        Assert.DoesNotContain(plan.GuidMap.Keys, guid => guid < 1_000_000_000_000);
    }

    [Fact]
    public void EquipmentPlanGridsPointAtTheNewGuids()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var equip = DBEquipInfo.Parser.ParseFrom(plan.Blobs["equip_plans"]);
        var owned = plan.Items.Select(item => item.Guid).ToHashSet();
        var worn = plan.Items.Where(item => item.Grid > 0).Select(item => item.Guid).ToHashSet();
        Assert.NotEmpty(worn);
        // Every plan only references instances the role owns (inactive plans keep their gear in the bag).
        foreach (var (_, grid) in equip.EquipPlans)
            foreach (var (_, guid) in grid.ItemsEquipGrid)
                Assert.Contains(guid, owned);
        // The active plan carries exactly the worn items, one per grid, all of them equipment pack (2).
        var active = equip.EquipPlans[equip.CurrentEquipPlan];
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], active.ItemsEquipGrid.Keys.Order());
        Assert.Equal(worn, active.ItemsEquipGrid.Values.ToHashSet());
        Assert.All(plan.Items.Where(item => item.Grid > 0), item => Assert.Equal(2, item.Pack));
        Assert.Equal(1, plan.Items.Count(item => item.Grid == 1));
    }

    [Fact]
    public void SoulEchoSlotsAreRemappedOntoOwnedInstances()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var emblems = JsonSerializer.Deserialize<EmblemService.EmblemState>(plan.Blobs["emblems"])!;
        Assert.Equal(5, emblems.Plans.Count);
        var owned = plan.Items.Where(item => item.Pack == 5 && item.Grid == 0).Select(item => item.Guid).ToHashSet();
        var slots = emblems.Plans.SelectMany(echo => echo.Value.Slots.Values).ToArray();
        Assert.NotEmpty(slots);
        Assert.All(slots, guid => Assert.Contains(guid, owned));
        Assert.Equal("DPS", emblems.Plans[0].Name);
        Assert.True(emblems.HistorySeasonPower > 0);
        Assert.NotNull(emblems.Power);
    }

    [Fact]
    public void ItemAttrsFollowTheServerEncoding()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        // Card cost history (520) is an ItemAttrValue.strVal: it belongs to str_attrs, never to attrs.
        var card = plan.Items.First(item => item.Grid == -11); // equip grid 1, socket 1
        Assert.Equal("1440101|1|1|0|0", card.StringAttrs![520]);
        Assert.Equal(1, card.Attrs![502]); // ...while the star level (502, a longVal) stays in attrs
        Assert.DoesNotContain(520, card.Attrs!.Keys);
        // Star level (502) is a longVal and stays in attrs.
        var starred = plan.Items.First(item => item.Attrs?.ContainsKey(502) == true);
        Assert.True(starred.Attrs![502] >= 1);
        Assert.DoesNotContain(520, starred.Attrs.Keys);
        var enchanted = plan.Items.First(item => item.Enchantments.Count > 0);
        Assert.Equal(2, enchanted.Enchantments[1014201].Attr[1]);
        Assert.Equal(1793557376, enchanted.Enchantments[1014201].GetTime);
    }

    [Fact]
    public void EquipLevelsCarryRefineStrengthAndLuck()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var levels = JsonSerializer.Deserialize<EquipLevels>(plan.Blobs["equip_levels"])!;
        Assert.Equal([9, 7, 7, 7, 7, 6, 6], Enumerable.Range(1, 7).Select(grid => levels.Refine[grid]));
        Assert.Equal(400, levels.Luck[3]);
        Assert.Equal(1200, levels.Luck[6]);
        Assert.Empty(levels.Strength);
    }

    [Fact]
    public void RoleColumnsComeFromTheActivePlans()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        Assert.Equal(67L, plan.Columns["level"]);
        Assert.Equal(40L, plan.Columns["job_level"]);
        Assert.Equal(1410L, plan.Columns["career"]);
        Assert.Equal(2992048L, plan.Columns["exp"]);
        Assert.Equal("65,67,1,1,1,1", plan.Columns["stats"]);
        Assert.Equal(-48L, plan.Columns["bonus_points"]);
        Assert.Equal(72000006L, plan.Columns["hair"]);
        var skills = ((string)plan.Columns["skills"]!).Split(',');
        Assert.Equal(50, skills.Length); // the union of the five plans' assignments (the learned set)
        Assert.Contains("11401:10", skills);
        Assert.Contains("11600:1", skills);
        Assert.Equal("1:11607,2:11602,3:11628,4:11604,6:11616,7:11600", plan.Columns["skill_bar"]);
        Assert.Equal(1L, plan.Columns["paragon_level"]);
        Assert.StartsWith("1:392286,2:369137", (string)plan.Columns["currency"]!);
    }

    [Fact]
    public void BlobsRoundTripThroughTheServerTypes()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var stats = BaseAttrInfo.Parser.ParseFrom(plan.Blobs["stat_plans"]);
        Assert.Equal(0, stats.CurPlanIndex);
        Assert.Equal(5, stats.AllPlanBaseProperty.Count);
        Assert.Equal(464, stats.AllAttrPoints);
        Assert.Equal(65, stats.AllPlanBaseProperty[0].BaseProperty[1]);
        var skills = DBSkillData.Parser.ParseFrom(plan.Blobs["skill_plans"]);
        Assert.Equal(5, skills.SkillAssignPlans.Count);
        Assert.Equal(11410, skills.SkillAssignPlans[0].SkillQueue[1].SkillId);
        var cards = DBStorageHoleInfo.Parser.ParseFrom(plan.Blobs["card_plans"]);
        Assert.True(cards.Inited);
        Assert.Equal(1440101, cards.StoragePlans[0].CardInfo[1].CardSlotInfo[1].ItemId);
        var mounts = JsonSerializer.Deserialize<MountService.SavedState>(plan.Blobs["mounts"])!;
        Assert.Equal(2, DBMountsInfo.Parser.ParseFrom(mounts.Info).MountIds.Count);
        var bag = DBItemsInfo.Parser.ParseFrom(plan.Blobs["bag_state"]);
        Assert.Empty(bag.PackInfo);
        Assert.NotEmpty(bag.ShortcutBarInfos);
        Assert.Contains(bag.ShortcutBarInfos, slot => slot.ItemGuid == 0 || plan.GuidMap.Values.Contains(slot.ItemGuid));
        var pets = DBPetInfo.Parser.ParseFrom(plan.Blobs["pets"]);
        Assert.NotEmpty(pets.ListPet);
    }

    [Fact]
    public void QuestStateAndGuidesComeAlongSoFunctionsOpen()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var tasks = DBTaskInfo.Parser.ParseFrom(plan.Blobs["tasks"]);
        Assert.Contains(10101170, tasks.CompleteTaskNum.Keys);
        Assert.Contains(10103160, tasks.CompleteTaskNum.Keys);
        Assert.True(tasks.CompleteTaskNum.Count > 300);
        Assert.NotEmpty(tasks.Infos);
        var guides = DBGuideInfo.Parser.ParseFrom(plan.Blobs["guides"]);
        Assert.NotEmpty(guides.FinishedGuideIds);
        // The runtime carries the guides the TGuide objectives read, and the old story's award debt is gone.
        var runtime = JsonSerializer.Deserialize<TaskRuntimeState>(plan.Blobs["task_runtime"])!;
        Assert.Equal(guides.FinishedGuideIds.ToHashSet(), runtime.CompletedGuides);
        Assert.Equal("[]", System.Text.Encoding.UTF8.GetString(plan.Blobs["task_followup"]));
    }

    [Fact]
    public void SeasonTalentBoughtLevelsReachThePlans()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var official = (DBSeasonTalent)ProtoJson.Parse(DBSeasonTalent.Descriptor, snapshot.Message("DBSEASONTALENT"));
        var imported = DBSeasonTalent.Parser.ParseFrom(plan.Blobs["season_talent"]);
        // The server reads the grade from the ACTIVE PLAN (SeasonTalentService.TalentLevel), the client keeps the
        // bought level in the global map: every plan must carry the bought level, or the login rebuild lands one tree.
        foreach (var (id, bought) in official.TalentNodes)
            foreach (var (index, season) in imported.Plan)
                Assert.True(season.TalentNodes[id].CurrentLevel >= bought.CurrentLevel,
                    $"plan {index} lost node {id}: {season.TalentNodes[id].CurrentLevel} < {bought.CurrentLevel}");
        // Its own selection survives where the global map has nothing.
        foreach (var (id, selected) in official.Plan[official.CurPlanId].TalentNodes)
            Assert.True(imported.Plan[official.CurPlanId].TalentNodes[id].CurrentLevel >= selected.CurrentLevel);
        // The grade the plan now carries is the official global sum (the client's own total).
        long grade = imported.Plan[official.CurPlanId].TalentNodes.Values.Sum(node => (long)node.CurrentLevel);
        long global = official.TalentNodes.Values.Sum(node => (long)node.CurrentLevel);
        Assert.True(grade >= global, $"grade {grade} < global {global}");
    }

    [Fact]
    public void LearnedSkillsAreThePlansUnionNotEveryListedSkill()
    {
        using var snapshot = Fixture();
        var plan = Build(snapshot);
        var skills = ((string)plan.Columns["skills"]!).Split(',')
            .Select(pair => pair.Split(':'))
            .ToDictionary(pair => int.Parse(pair[0], CultureInfo.InvariantCulture), pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
        // The learned set is the union of the five plans' assignments (50 groups in the fixture): the client only paints
        // a group it has a level in, and the plans are loadouts of the same learned set.
        Assert.Equal(50, skills.Count);
        Assert.Equal(5, skills[11616]);      // only the Tank plan assigns it
        Assert.Equal(10, skills[11502]);
        // The queue command is granted like the server grants a system skill, and keeps the "Fila" tab alive.
        Assert.Contains(11600, skills.Keys);
        Assert.Equal(1, skills[11600]);
        // A group `skillList` lists but no plan assigns is NOT learned (the official paints it "Não aprendida").
        Assert.DoesNotContain(11003, skills.Keys);
        Assert.DoesNotContain(11403, skills.Keys);
        Assert.Contains("7:11600", ((string)plan.Columns["skill_bar"]!).Split(','));
        Assert.Contains("1:11607", ((string)plan.Columns["skill_bar"]!).Split(','));
    }

    [Fact]
    public void MissingMessagesAreReportedNotInvented()
    {
        using var snapshot = SnapshotDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot-kaori.json"));
        var plan = Build(snapshot);
        Assert.DoesNotContain("trade_info", plan.Blobs.Keys);
        Assert.DoesNotContain("heroic_spirit", plan.Notes); // it is present in the fixture
        Assert.Empty(plan.Blobs["heroic_spirit"]);
    }
}
