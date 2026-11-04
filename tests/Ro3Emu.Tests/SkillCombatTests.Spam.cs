using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Repeating a skill while its action still runs (Eduardo: the official accepts the spam, the emulator restarted it).</summary>
public sealed partial class SkillCombatTests
{
    private const int BloodCross = 1160405; // SkillConfig 11604 Lv5 mode 0: no cooldown, 2350 ms action, release-to-repeat

    /// <summary>Presses the same no-cooldown skill every <paramref name="periodMs"/> for <paramref name="windowMs"/> and counts hits.</summary>
    private async Task<int> SpamBloodCross(BotClient bot, MonsterUnit monster, int periodMs, int windowMs)
    {
        int hits = 0;
        await Loop.InvokeAsync(() =>
        {
            _app.Services.GetRequiredService<CombatService>().DealtAsync.Add((source, target, _, _) =>
            {
                if (target == monster) hits++;
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
        var until = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (DateTime.UtcNow < until)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BloodCross, TargetId = monster.Uid });
            await Task.Delay(periodMs);
        }
        await Task.Delay(3000); // let the last action finish and its hit land
        return hits;
    }

    [ClientDataFact]
    public async Task SpammingASkillDuringItsActionNeverLosesACast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1410");
        await Gm("alice", "!lvl 99");
        await Loop.InvokeAsync(() => { Session("alice").Progress!.Skills[11604] = 5; return Task.CompletedTask; });
        // Learned() compares the row level: Blood Cross Lv5 is the row with the 2350 ms action and no cooldown.
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 50_000_000; return Task.CompletedTask; });

        // Blood Cross takes 2350 ms per action: at 1400 ms the second press always lands inside the first action, which is
        // what the official accepts (it releases again right after) and what the emulator used to answer with an
        // interrupt — the animation restarted and the hit never landed.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BloodCross, TargetId = monster.Uid });
        var rsp = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Console.WriteLine($"DBG first cast result {rsp.Result}");
        int slow = await SpamBloodCross(alice, monster, 1400, 100); // one press: it lands its own hit
        int fast = await SpamBloodCross(alice, monster, 100, 300); // three presses inside one action
        Assert.True(fast >= slow, $"100 ms presses landed {fast} hits, 1400 ms presses landed {slow}");
        Assert.True(fast >= 2, $"100 ms presses landed only {fast} hits in two actions");
    }
}
