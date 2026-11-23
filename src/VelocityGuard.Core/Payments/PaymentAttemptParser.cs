using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace VelocityGuard.Core.Payments;

/// <summary>Raw, untrusted assessment input, flattened so REST and gRPC can share one validation path.</summary>
public sealed record PaymentAttemptInput
{
    public string? AttemptId { get; init; }
    public string? PlayerId { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public string? Method { get; init; }
    public string? Ip { get; init; }
    public string? IpCountry { get; init; }
    public string? CardBinCountry { get; init; }
    public string? CardFingerprint { get; init; }
    public string? WalletNetwork { get; init; }
    public string? DeviceId { get; init; }
    public int? AccountAgeDays { get; init; }
}

/// <summary>
/// Validates and normalises <see cref="PaymentAttemptInput"/> into a <see cref="PaymentAttempt"/>.
/// Error keys use the public JSON field names, so they can go straight into a ProblemDetails response.
/// Error messages never echo the submitted value.
/// </summary>
public static class PaymentAttemptParser
{
    public const int MaxIdLength = 64;
    public const int MaxDeviceIdLength = 128;
    public const int MaxAccountAgeDays = 36_500;

    private static readonly SearchValues<char> IdChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.:");

    public static bool TryParse(
        PaymentAttemptInput input,
        TimeProvider time,
        [NotNullWhen(true)] out PaymentAttempt? attempt,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        attempt = null;

        var attemptId = input.AttemptId is null ? NewAttemptId(time) : input.AttemptId;
        RequireId(errors, "attemptId", attemptId, MaxIdLength);

        RequireId(errors, "playerId", input.PlayerId, MaxIdLength);

        var currencyOk = Payments.Currency.TryParse(input.Currency, out var currency);
        if (!currencyOk)
        {
            Add(errors, "currency", "must be a supported ISO 4217 code, e.g. EUR");
        }

        var money = default(Money);
        if (input.Amount is not { } amount)
        {
            Add(errors, "amount", "is required");
        }
        else if (currencyOk && !Money.TryFromMajor(amount, currency, out money, out var amountError))
        {
            Add(errors, "amount", amountError);
        }

        var method = ParseMethod(input.Method);
        if (method is null)
        {
            Add(errors, "method", "must be one of: card, wallet, bankTransfer");
        }

        string? ipText = null;
        string? ipKey = null;
        if (!TryNormalizeIp(input.Ip, out ipText, out ipKey))
        {
            Add(errors, "ip", "must be a valid IPv4 or IPv6 address");
        }

        var ipCountry = NormalizeCountry(input.IpCountry);
        if (ipCountry is null)
        {
            Add(errors, "ipCountry", "must be an ISO 3166-1 alpha-2 code, e.g. DE");
        }

        string? binCountry = null;
        if (method == PaymentMethod.Card)
        {
            binCountry = NormalizeCountry(input.CardBinCountry);
            if (binCountry is null)
            {
                Add(errors, "card.binCountry", "is required for card payments and must be an ISO 3166-1 alpha-2 code");
            }

            ValidateFingerprint(errors, input.CardFingerprint);
        }
        else if (method is not null && (input.CardBinCountry is not null || input.CardFingerprint is not null))
        {
            Add(errors, "card", "is only allowed when method is card");
        }

        if (method == PaymentMethod.Wallet)
        {
            RequireId(errors, "wallet.network", input.WalletNetwork, 32);
        }
        else if (method is not null && input.WalletNetwork is not null)
        {
            Add(errors, "wallet", "is only allowed when method is wallet");
        }

        if (input.DeviceId is not null)
        {
            RequireId(errors, "deviceId", input.DeviceId, MaxDeviceIdLength);
        }

        if (input.AccountAgeDays is not { } age || age < 0 || age > MaxAccountAgeDays)
        {
            Add(errors, "accountAgeDays", $"is required and must be between 0 and {MaxAccountAgeDays}");
        }

        if (errors.Count > 0)
        {
            return false;
        }

        attempt = new PaymentAttempt
        {
            AttemptId = attemptId,
            PlayerId = input.PlayerId!,
            Amount = money,
            Method = method!.Value,
            IpAddress = ipText!,
            IpKey = ipKey!,
            IpCountry = ipCountry!,
            CardBinCountry = binCountry,
            CardFingerprint = method == PaymentMethod.Card ? input.CardFingerprint : null,
            WalletNetwork = method == PaymentMethod.Wallet ? input.WalletNetwork : null,
            DeviceId = input.DeviceId,
            AccountAgeDays = input.AccountAgeDays!.Value,
        };
        return true;
    }

    /// <summary>A time-ordered (UUIDv7) attempt id, for callers that do not send their own.</summary>
    public static string NewAttemptId(TimeProvider time) => "att_" + Guid.CreateVersion7(time.GetUtcNow()).ToString("N");

    public static bool IsValidId([NotNullWhen(true)] string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && !value.AsSpan().ContainsAnyExcept(IdChars);

    /// <summary>
    /// True when <paramref name="value"/> is 12 to 19 digits once spaces and dashes are removed:
    /// the shape of a primary account number. Such values are refused before they reach a log or Redis.
    /// </summary>
    public static bool LooksLikeCardNumber(string value)
    {
        var digits = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
            else if (c is not (' ' or '-'))
            {
                return false;
            }
        }

        return digits is >= 12 and <= 19;
    }

    public static bool TryNormalizeIp(
        [NotNullWhen(true)] string? value,
        [NotNullWhen(true)] out string? canonical,
        [NotNullWhen(true)] out string? velocityKey)
    {
        canonical = velocityKey = null;
        if (value is null || value.Length > 45 || value.Contains('%'))
        {
            return false;
        }

        // IPAddress.TryParse accepts legacy shorthands such as "10.1" or "167772161"; insist on dotted quads.
        var isV6 = value.Contains(':');
        if (!isV6 && value.Count(c => c == '.') != 3)
        {
            return false;
        }

        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        canonical = address.ToString();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            velocityKey = canonical;
            return true;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        velocityKey = new IPAddress(bytes).ToString() + "/64";
        return true;
    }

    private static PaymentMethod? ParseMethod(string? value) => value?.ToLowerInvariant() switch
    {
        "card" => PaymentMethod.Card,
        "wallet" => PaymentMethod.Wallet,
        "banktransfer" => PaymentMethod.BankTransfer,
        _ => null,
    };

    private static string? NormalizeCountry(string? value) =>
        value is { Length: 2 } && char.IsAsciiLetter(value[0]) && char.IsAsciiLetter(value[1])
            ? value.ToUpperInvariant()
            : null;

    private static void ValidateFingerprint(Dictionary<string, string[]> errors, string? fingerprint)
    {
        if (fingerprint is null)
        {
            Add(errors, "card.fingerprint", "is required for card payments (a PSP token or card fingerprint)");
        }
        else if (LooksLikeCardNumber(fingerprint))
        {
            Add(errors, "card.fingerprint", "looks like a card number; send the PSP token or fingerprint instead, never the PAN");
        }
        else if (fingerprint.Length < 8 || !IsValidId(fingerprint, 128))
        {
            Add(errors, "card.fingerprint", "must be 8 to 128 characters of letters, digits, '-', '_', '.' or ':'");
        }
    }

    private static void RequireId(Dictionary<string, string[]> errors, string field, string? value, int maxLength)
    {
        if (!IsValidId(value, maxLength))
        {
            Add(errors, field, $"is required: 1 to {maxLength} characters of letters, digits, '-', '_', '.' or ':'");
        }
    }

    private static void Add(Dictionary<string, string[]> errors, string field, string message) =>
        errors[field] = errors.TryGetValue(field, out var existing) ? [.. existing, message] : [message];
}
