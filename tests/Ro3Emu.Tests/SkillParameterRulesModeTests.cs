using Ro3Emu.Server.Game;
using Xunit;

namespace Ro3Emu.Tests;

/// <summary>Blood Cross (11604) modes: 0 normal, 4 Over Brand, 2 normal with the Golden Moon Ring resonance, 5 Over Brand with it.</summary>
public sealed class SkillParameterRulesModeTests
{
    private static SkillParameterRules.Modifier Resonance() => new(11604, 15, 2, 2, SkillParameterRules.ResonanceMarker);
    private static SkillParameterRules.Modifier OverBrand() => new(11604, 15, 2, 4, 0);

    [Fact]
    public void BloodCrossModesFollowOverBrandAndTheResonance()
    {
        Assert.Equal(0, SkillParameterRules.Mode(11604, [], []));
        Assert.Equal(4, SkillParameterRules.Mode(11604, [], [OverBrand()]));
        Assert.Equal(2, SkillParameterRules.Mode(11604, [], [Resonance()]));
        Assert.Equal(5, SkillParameterRules.Mode(11604, [], [Resonance(), OverBrand()]));
    }
}
