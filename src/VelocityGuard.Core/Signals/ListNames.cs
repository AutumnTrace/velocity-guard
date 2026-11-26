using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Core.Signals;

/// <summary>
/// What a list name and a list entry may look like. Rules, the admin API and configuration all use
/// this, so a list written from one place can be read and cleared from the others.
/// </summary>
public static partial class ListNames
{
    public const string NameRule = "1-64 lower-case letters, digits or '-'";

    public const string EntryRule = "1-128 letters, digits, '-', '_', '.', ':' or '/' and must not be a card number";

    public static bool IsValidName([NotNullWhen(true)] string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>List entries are ids (players, devices, card tokens, IPs or IPv6 /64 keys); never raw card numbers.</summary>
    public static bool IsValidEntry([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 128 }
        && !PaymentAttemptParser.LooksLikeCardNumber(value)
        && PaymentAttemptParser.IsValidId(value.Replace('/', '-'), 128);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern();
}
