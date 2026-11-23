using Microsoft.Extensions.Time.Testing;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Tests.Payments;

public sealed class PaymentAttemptParserTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Parses_and_normalises_a_valid_card_attempt()
    {
        var ok = PaymentAttemptParser.TryParse(TestData.CardInput(), _time, out var attempt, out var errors);

        Assert.True(ok, string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(",", e.Value)}")));
        Assert.NotNull(attempt);
        Assert.Equal("att_1", attempt.AttemptId);
        Assert.Equal(2500, attempt.Amount.Minor);
        Assert.Equal(PaymentMethod.Card, attempt.Method);
        Assert.Equal("DE", attempt.IpCountry);
        Assert.Equal("DE", attempt.CardBinCountry);
        Assert.Equal("203.0.113.10", attempt.IpKey);
    }

    [Fact]
    public void Generates_a_time_ordered_attempt_id_when_the_caller_sends_none()
    {
        PaymentAttemptParser.TryParse(TestData.CardInput() with { AttemptId = null }, _time, out var first, out _);
        _time.Advance(TimeSpan.FromMilliseconds(5));
        PaymentAttemptParser.TryParse(TestData.CardInput() with { AttemptId = null }, _time, out var second, out _);

        Assert.StartsWith("att_", first!.AttemptId);
        Assert.True(string.CompareOrdinal(first.AttemptId, second!.AttemptId) < 0);
    }

    [Theory]
    [InlineData("4111111111111111")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("371449635398431")]
    public void Refuses_anything_shaped_like_a_card_number_without_echoing_it(string pan)
    {
        var ok = PaymentAttemptParser.TryParse(TestData.CardInput() with { CardFingerprint = pan }, _time, out _, out var errors);

        Assert.False(ok);
        var message = Assert.Single(errors["card.fingerprint"]);
        Assert.Contains("never the PAN", message);
        Assert.DoesNotContain("1111", message);
    }

    [Theory]
    [InlineData("2001:db8:abcd:12:1:2:3:4", "2001:db8:abcd:12::/64")]
    [InlineData("2001:db8:abcd:12::ffff", "2001:db8:abcd:12::/64")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7")]
    [InlineData("198.51.100.7", "198.51.100.7")]
    public void Velocity_key_for_ipv6_is_the_64_bit_network(string ip, string expectedKey)
    {
        Assert.True(PaymentAttemptParser.TryNormalizeIp(ip, out _, out var key));
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("10.1")]
    [InlineData("167772161")]
    [InlineData("fe80::1%eth0")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void Rejects_ambiguous_or_invalid_ip_addresses(string ip)
    {
        Assert.False(PaymentAttemptParser.TryNormalizeIp(ip, out _, out _));
    }

    [Fact]
    public void Wallet_attempts_need_a_network_and_must_not_carry_card_data()
    {
        var input = TestData.CardInput() with { Method = "wallet", WalletNetwork = null };

        var ok = PaymentAttemptParser.TryParse(input, _time, out _, out var errors);

        Assert.False(ok);
        Assert.Contains("wallet.network", errors.Keys);
        Assert.Contains("card", errors.Keys);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var input = new PaymentAttemptInput
        {
            PlayerId = "bad id with spaces",
            Amount = 10.001m,
            Currency = "EUR",
            Method = "cheque",
            Ip = "999.1.1.1",
            IpCountry = "Germany",
            AccountAgeDays = -1,
        };

        var ok = PaymentAttemptParser.TryParse(input, _time, out _, out var errors);

        Assert.False(ok);
        Assert.Equal(
            ["accountAgeDays", "amount", "ip", "ipCountry", "method", "playerId"],
            errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("card", null, "fp_12345678", "card.binCountry")]
    [InlineData("card", "DE", null, "card.fingerprint")]
    [InlineData("card", "DE", "short", "card.fingerprint")]
    public void Card_attempts_need_a_bin_country_and_a_token(string method, string? bin, string? fingerprint, string expectedField)
    {
        var input = TestData.CardInput() with { Method = method, CardBinCountry = bin, CardFingerprint = fingerprint };

        PaymentAttemptParser.TryParse(input, _time, out _, out var errors);

        Assert.Contains(expectedField, errors.Keys);
    }
}
