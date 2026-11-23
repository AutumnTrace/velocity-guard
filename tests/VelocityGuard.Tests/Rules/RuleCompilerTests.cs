using Microsoft.Extensions.Options;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;
using static VelocityGuard.Tests.TestData;

namespace VelocityGuard.Tests.Rules;

public sealed class RuleCompilerTests
{
    [Fact]
    public void Rules_that_need_the_same_window_share_one_slot()
    {
        var set = Compile(
            VelocityCount("a", Dimension.Player, TimeSpan.FromMinutes(10), 5),
            VelocityCount("b", Dimension.Player, TimeSpan.FromMinutes(10), 8, score: 80),
            VelocitySum("c", Dimension.Player, TimeSpan.FromMinutes(10), 500m),
            VelocityCount("d", Dimension.Device, TimeSpan.FromMinutes(10), 3));

        Assert.Equal(3, set.Velocity.SlotCount);
        Assert.Equal(set.Rules[0].Slot, set.Rules[1].Slot);
        Assert.NotEqual(set.Rules[0].Slot, set.Rules[2].Slot);
        Assert.NotEqual(set.Rules[0].Slot, set.Rules[3].Slot);
    }

    [Fact]
    public void Each_subject_keeps_events_for_its_longest_window()
    {
        var set = Compile(
            VelocityCount("a", Dimension.Player, TimeSpan.FromMinutes(10), 5),
            VelocitySum("b", Dimension.Player, TimeSpan.FromDays(1), 2000m),
            VelocityCount("c", Dimension.Ip, TimeSpan.FromHours(1), 30));

        var player = Assert.Single(set.Velocity.Subjects, s => s.Dimension == Dimension.Player);
        var ip = Assert.Single(set.Velocity.Subjects, s => s.Dimension == Dimension.Ip);
        Assert.Equal(TimeSpan.FromDays(1), player.Retention);
        Assert.True(player.HasSums);
        Assert.Equal(2, player.Slots.Length);
        Assert.Equal(TimeSpan.FromHours(1), ip.Retention);
        Assert.False(ip.HasSums);
    }

    [Fact]
    public void List_checks_are_deduplicated_into_bit_positions()
    {
        var set = Compile(
            List("blocked-device", RuleType.Blocklist, "blocked-devices", Dimension.Device, 100),
            List("blocked-device-shadow", RuleType.Blocklist, "blocked-devices", Dimension.Device, 10),
            List("trusted", RuleType.Allowlist, "trusted-players", Dimension.Player, -20));

        Assert.Equal(2, set.ListChecks.Length);
        Assert.Equal(0, set.Rules[0].Slot);
        Assert.Equal(0, set.Rules[1].Slot);
        Assert.Equal(1, set.Rules[2].Slot);
        Assert.Equal(new ListCheck("trusted-players", Dimension.Player), set.ListChecks[1]);
    }

    [Fact]
    public void Limits_are_converted_to_minor_units_once()
    {
        var set = Compile(Amount("large", 5000m));

        Assert.True(set.Rules[0].Limits.TryGet(Currency.Parse("EUR"), out var minor));
        Assert.Equal(500_000, minor);
        Assert.False(set.Rules[0].Limits.TryGet(Currency.Parse("GBP"), out _));
    }

    [Fact]
    public void Disabled_rules_are_left_out()
    {
        var disabled = Amount("off", 10m);
        disabled.Enabled = false;

        var set = Compile(Amount("on", 10m), disabled);

        Assert.Equal(1, set.Count);
        Assert.Equal("on", set.Rules[0].Id);
    }

    [Fact]
    public void Profile_lookup_is_only_needed_when_a_rule_uses_it()
    {
        Assert.False(Compile(Amount("a", 10m)).NeedsProfile);
        Assert.True(Compile(Amount("a", 10m), new RuleDefinition { Id = "fd", Type = RuleType.FirstDeposit }).NeedsProfile);
    }

    [Fact]
    public void Version_is_a_content_hash()
    {
        var a = RuleCompiler.VersionOf(Options(Amount("large", 5000m)));
        var same = RuleCompiler.VersionOf(Options(Amount("large", 5000m)));
        var changed = RuleCompiler.VersionOf(Options(Amount("large", 4000m)));

        Assert.Equal(a, same);
        Assert.NotEqual(a, changed);
        Assert.Equal(12, a.Length);
    }

    [Fact]
    public void Refuses_to_compile_an_invalid_rule_set()
    {
        var ex = Assert.Throws<OptionsValidationException>(() =>
            RuleCompiler.Compile(Options(new RuleDefinition { Id = "x", Type = RuleType.VelocitySum }), DateTimeOffset.UnixEpoch));

        Assert.True(ex.Failures.Count() >= 3);
    }
}
