using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>
/// Round 11: the two rules the official character proves on its own data. The echo resonance stages (EmblemMarkConfig)
/// put the season profession skills in use, and those skills' only effect is the enhanced MODE of the skill they name
/// (CombatService.Passives ResonanceModes). Twilight (HesperusLit) is a leap that moves the caster when the cast starts,
/// once, and the hit point does not move it again.
/// </summary>
public sealed partial class SkillCombatTests
{
    /// <summary>Echo 1850101: EmblemConfig [[5,0],[10,0],[15,0],[20,0]] - four lit mark slots.</summary>
    private const int EchoWithFourMarks = 1850101;

    /// <summary>
    /// The active echo plan reads the lit marks of its worn echoes (item attrs 1021+). Five echoes hold 20 mark slots;
    /// the caller picks how many are mark 1001 and how many are mark 1002, the rest stay unlit.
    /// </summary>
    private Task InstallEchoMarks(GameSession session, int marks1001, int marks1002) => Loop.InvokeAsync(() =>
    {
        session.Items.RemoveAll(item => item.Guid >= 900001 && item.Guid < 900100);
        session.Emblems.Plans.Clear();
        var plan = new EmblemService.Plan { Name = "resonance" };
        for (int echoIndex = 0; echoIndex < 5; echoIndex++)
        {
            var echo = new InvItem { Guid = 900001 + echoIndex, Entry = EchoWithFourMarks, Grid = 0, Attrs = [] };
            for (int slot = 0; slot < 4; slot++)
            {
                int index = echoIndex * 4 + slot;
                if (index < marks1001) echo.Attrs![1021 + slot] = 1001;
                else if (index < marks1001 + marks1002) echo.Attrs![1021 + slot] = 1002;
            }
            session.Items.Add(echo);
            plan.Slots[echoIndex + 1] = echo.Guid;
        }
        session.Emblems.CurrentPlan = 0;
        session.Emblems.Plans[0] = plan;
        return Task.CompletedTask;
    });

    /// <summary>The type-15 (mode) modifiers the passives hand the combat model, as group and mode.</summary>
    private Task<(int Group, int Mode)[]> ModesInUse(CombatService combat, GameSession session) => Loop.InvokeAsync(() =>
        Task.FromResult(combat.PassiveModifiers(session)
            .Where(entry => entry.Modifier.Type == 15)
            .Select(entry => (entry.Modifier.Group, (int)entry.Modifier.Value))
            .ToArray()));

    [ClientDataFact]
    public async Task EchoResonanceStagesPutTheEnhancedSkillModesInUse()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var session = Session("alice");

        // 10 marks of 1001 reach stages 1-3 and 6 of 1002 reach stages 1-2. Stage 2/3 are the special effects that put
        // 111641 (mark 1001 stage 2), 111642 (mark 1001 stage 3) and 111643 (mark 1002 stage 2) in use.
        await InstallEchoMarks(session, marks1001: 10, marks1002: 6);
        Assert.Equal(10, EmblemService.MarkCount(session, 1001, data));
        Assert.Equal(6, EmblemService.MarkCount(session, 1002, data));
        Assert.Equal(3, EmblemService.Resonances(session, data).GetValueOrDefault(1001));
        Assert.Equal(2, EmblemService.Resonances(session, data).GetValueOrDefault(1002));
        var modes = await ModesInUse(combat, session);
        Assert.Contains((11628, 1), modes); // 111641 - Crescent Moon's enhanced mode
        Assert.Contains((11607, 1), modes); // 111642 - Twilight's
        Assert.Contains((11604, 5), modes); // 111643 - Blood Cross's

        // Under six lit marks of one id only stage 1 is reached (its effects are Attr1CountAddAttr2ForJob), so no mode
        // is put in use.
        await InstallEchoMarks(session, marks1001: 5, marks1002: 0);
        Assert.Equal(1, EmblemService.Resonances(session, data).GetValueOrDefault(1001));
        Assert.Empty(await ModesInUse(combat, session));
    }

    [ClientDataFact]
    public async Task TwilightLeapsOnceWhenTheCastStartsAndTheHitDoesNotMoveItAgain()
    {
        // The plain row, and the enhanced-mode row the resonance above selects (show 111632, whose effect only the skill
        // group's show carries).
        await LeapOnce("Alice", 1160710, modeOne: false);
        await LeapOnce("Bob", 1160720, modeOne: true);
    }

    private async Task LeapOnce(string name, int skillRow, bool modeOne)
    {
        string account = name.ToLowerInvariant();
        await using var bot = await InWorld(name);
        await Gm(account, "!job 1410");
        await Gm(account, "!lvl 99");
        await Gm(account, "!warp 10111 150 190"); // the square is standable in every direction
        var warp = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = warp.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var session = Session(account);
        await Loop.InvokeAsync(() =>
        {
            // _kPreSkillRequired [[11604, 5]]: Blood Cross at level 5 unlocks Twilight. Skill condition 86000257 is
            // [2001, 5] (LC_ConditionList): an equipped item of ItemConfig subtype 5, the trident this skill requires.
            session.Progress!.Skills[11604] = 5;
            session.Progress!.Skills[11607] = 10;
            session.Items.Add(new InvItem { Guid = 960001, Entry = 2050550, Grid = 1 });
            return Task.CompletedTask;
        });
        if (modeOne)
        {
            // Mode 1 of 11607 is only castable when it is the mode the session has in use (EffectiveSkillRow).
            await InstallEchoMarks(session, marks1001: 20, marks1002: 0);
            var combat = _app.Services.GetRequiredService<CombatService>();
            await Loop.InvokeAsync(() => combat.RefreshSkillParametersAsync(session)); // login/skill changes reconcile the modes
            Assert.Contains((11607, 1), await ModesInUse(combat, session));
        }
        var origin = await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
        float wanted = 12; // inside _iDistanceMax (15 m), so the caster lands exactly on the point
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skillRow, Face = 90,
            TargetPos = new Vec3 { X = origin.X + wanted, Y = origin.Y, Z = origin.Z },
        });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var action = await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast));
        Assert.True(action is { CasterMoved: true }, "the cast did not mark the leap");

        // The landing happens while the cast is still running (the leap starts with the skill, before the hit point).
        var landed = await WaitMove(session, origin);
        await Loop.InvokeAsync(() =>
        {
            float travelled = MonsterService.Distance(origin.X, origin.Z, landed.X, landed.Z);
            Assert.True(Math.Abs(travelled - wanted) < 1.5f, $"leapt {travelled} m for a {wanted} m point");
            return Task.CompletedTask;
        });
        // The hit point does not move it a second time.
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast != null)))
            await Task.Delay(50);
        var final = await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
        await Loop.InvokeAsync(() =>
        {
            Assert.True(MonsterService.Distance(landed.X, landed.Z, final.X, final.Z) < 0.3f,
                $"the hit moved the caster from {landed.X:F1},{landed.Z:F1} to {final.X:F1},{final.Z:F1}");
            return Task.CompletedTask;
        });
    }

    /// <summary>The first position after the cast that is not the launch point (the leap's landing).</summary>
    private async Task<Vec3> WaitMove(GameSession session, Vec3 origin)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var now = await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
            if (MonsterService.Distance(origin.X, origin.Z, now.X, now.Z) > 0.5f) return now;
            await Task.Delay(40);
        }
        return await Loop.InvokeAsync(() => Task.FromResult(session.Position.Clone()));
    }
}
