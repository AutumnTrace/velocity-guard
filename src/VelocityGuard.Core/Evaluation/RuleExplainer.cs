using System.Globalization;
using VelocityGuard.Core.Payments;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Evaluation;

/// <summary>
/// Turns a rule hit into a sentence a risk analyst can read. Runs only for hits, after the decision,
/// so the string building stays off the evaluation hot path.
/// </summary>
public static class RuleExplainer
{
    public static string Explain(in CompiledRule rule, RuleResult result, PaymentAttempt attempt)
    {
        var currency = attempt.Amount.Currency;
        var who = Noun(rule.Dimension);
        return rule.Type switch
        {
            RuleType.VelocityCount =>
                $"{result.Observed} attempts from this {who} in {FormatWindow(rule.Window)} (max {result.Limit})",
            RuleType.VelocitySum =>
                $"{Money.Format(result.Observed, currency)} from this {who} in {FormatWindow(rule.Window)} (limit {Money.Format(result.Limit, currency)})",
            RuleType.AmountThreshold =>
                $"Amount {Money.Format(result.Observed, currency)} is at or above {Money.Format(result.Limit, currency)}",
            RuleType.CountryMismatch =>
                $"Card issued in {attempt.CardBinCountry}, IP located in {attempt.IpCountry}",
            RuleType.NewAccountAmount =>
                $"Account is {attempt.AccountAgeDays} days old and {Money.Format(result.Observed, currency)} is at or above {Money.Format(result.Limit, currency)}",
            RuleType.FirstDeposit when result.Limit > 0 =>
                $"First deposit for this player, {Money.Format(result.Observed, currency)} is at or above {Money.Format(result.Limit, currency)}",
            RuleType.FirstDeposit =>
                "First deposit for this player",
            RuleType.ChargebackHistory =>
                $"Player has {result.Observed} chargeback(s) on record",
            RuleType.Blocklist =>
                $"This {who} is on the '{rule.List}' list",
            RuleType.Allowlist =>
                $"This {who} is on the '{rule.List}' allowlist",
            _ => rule.Id,
        };
    }

    /// <summary>Compact window text: <c>90s</c>, <c>10m</c>, <c>1h</c>, <c>36h</c>, <c>7d</c>.</summary>
    public static string FormatWindow(TimeSpan window)
    {
        static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        if (window.TotalDays >= 2 && window.TotalDays == Math.Floor(window.TotalDays))
        {
            return N(window.TotalDays) + "d";
        }

        if (window.TotalHours >= 1 && window.TotalHours == Math.Floor(window.TotalHours))
        {
            return N(window.TotalHours) + "h";
        }

        return window.TotalMinutes >= 1 && window.TotalMinutes == Math.Floor(window.TotalMinutes)
            ? N(window.TotalMinutes) + "m"
            : N(window.TotalSeconds) + "s";
    }

    private static string Noun(Dimension dimension) => dimension switch
    {
        Dimension.Player => "player",
        Dimension.Device => "device",
        Dimension.Ip => "IP",
        Dimension.Card => "card",
        _ => "subject",
    };
}
