using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Round-7 rules: Twilight's leap to the chosen point and Heavy Cannon Shot's lane (Eduardo, 2026-11-04).</summary>
public sealed partial class SkillCombatTests
{
    private const int Twilight = 1160710;       // SkillConfig 11607 Lv10 mode 0, HesperusLit, _kRange [600] (6 m)
    private const int HeavyCannon = 1160205;     // SkillConfig 11602 Lv5 mode 0, CannonSpeari, _kRange [1600, 500]

    /// <summary>The caster's position once the cast's action (and its displacement) has played out.</summary>
    private async Task<Vec3> CasterPositionAfter(BotClient bot, string name)
    {
        var session = Session(name);
        await Task.Delay(1800); // the leap is applied when the cast resolves, inside the action window
        return await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
    }

    [ClientDataFact]
    public async Task TwilightLeapsToTheChosenPointWithinItsReach()
    {
        // A point inside the reach (3 m) lands exactly there; a point beyond it (12 m) stops at the 6 m reach.
        await Leap("Alice");
        await Leap("Bob");
    }

    private async Task Leap(string name)
    {
        await using var bot = await InWorld(name);
        await Gm(name.ToLowerInvariant(), "!job 1410");
        await Gm(name.ToLowerInvariant(), "!lvl 99");
        await Gm(name.ToLowerInvariant(), "!warp 10111 150 190"); // Prontera's open square: walkable in every direction
        var warp = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = warp.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        bool far = name == "Bob";
        await Loop.InvokeAsync(() => { Session(name).Progress!.Skills[11607] = 10; return Task.CompletedTask; });
        var session = Session(name);
        var origin = await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
        float wanted = far ? 20 : 3; // the reach is _iDistanceMax (15 m); _kRange [600] is the area around the landing point
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = Twilight, Face = 90, TargetPos = new Vec3 { X = origin.X + wanted, Y = origin.Y, Z = origin.Z },
        });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var landed = await CasterPositionAfter(bot, name);
        await Loop.InvokeAsync(() =>
        {
            float travelled = MonsterService.Distance(origin.X, origin.Z, landed.X, landed.Z);
            if (!far) Assert.True(Math.Abs(travelled - 3) < 0.7f, $"leapt {travelled} m for a 3 m point");
            else Assert.True(travelled > 13.5f && travelled <= 15.5f, $"leapt {travelled} m for a 20 m point, reach is 15");
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task HeavyCannonHitsTheLaneFromTheCasterAndNothingBehindOrBesideIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410");
        await Gm("alice", "!lvl 99");
        await Loop.InvokeAsync(() => { Session("alice").Progress!.Skills[11602] = 5; return Task.CompletedTask; });
        var session = Session("alice");
        float x = session.Position.X, z = session.Position.Z;
        // The skill's _kRange [1600, 500] is 16 m long by 5 m wide from the caster along the aim (+X at face 90).
        var front = await SpawnAt("alice", x + 8, z);
        var behind = await SpawnAt("alice", x - 5, z);
        var beside = await SpawnAt("alice", x + 8, z + 4);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = HeavyCannon, Face = 90, TargetPos = new Vec3 { X = x + 8, Y = session.Position.Y, Z = z },
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            Assert.True(front.Hp < front.Template.MaxHp, "the monster in the lane took no hit");
            Assert.Equal(behind.Template.MaxHp, behind.Hp);
            Assert.Equal(beside.Template.MaxHp, beside.Hp);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public void ProfessionBuffRowsSelectTheEnhancedModesOfTheirSkill()
    {
        var data = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
        // BuffConfig._kSkillAttributeList ["0", group, "15", "2", mode] sets the skill's enhanced mode. The Royal Guard's
        // profession choices carry them (11163601 -> 11607 mode 1, 11163801 -> 11604 mode 5, 11164501 -> 11628 mode 1) and
        // the official's own wheel shows exactly those rows: 1160720 (11607 Lv10 mode 1), 1160455 (11604 Lv5 mode 5) and
        // 1162815 (11628 Lv5 mode 1).
        foreach (var (buff, group, mode) in new[] { (11163601, 11607, 1), (11163801, 11604, 5), (11164501, 11628, 1) })
        {
            var set = Assert.Single(SkillParameterRules.BuffModifiers(data, buff), m => m.Group == group && m.Type == 15);
            Assert.Equal(2, set.Operation);
            Assert.Equal(mode, set.Value);
        }
    }

    /// <summary>A stationary monster of the ordinary dummy at a fixed point.</summary>
    private Task<MonsterUnit> SpawnAt(string name, float x, float z) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var template = monsters.Template(5000001);
        Assert.NotNull(template);
        var result = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
            new MapPoint(x, s.Position.Y, z, 0), s.InstanceId, customTemplate: template! with { RoamDistance = 0 });
        Assert.NotNull(result);
        return result!;
    });
}
