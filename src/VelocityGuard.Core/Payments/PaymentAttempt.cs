namespace VelocityGuard.Core.Payments;

public enum PaymentMethod
{
    Card = 1,
    Wallet = 2,
    BankTransfer = 3,
}

/// <summary>
/// Who or what a velocity window or a list check is keyed on.
/// </summary>
public enum Dimension
{
    Player = 1,
    Device = 2,
    Ip = 3,
    Card = 4,
}

/// <summary>
/// A validated deposit attempt, as the rules engine sees it. Build one through
/// <see cref="PaymentAttemptParser"/>; every string here is already normalised.
/// </summary>
public sealed record PaymentAttempt
{
    public required string AttemptId { get; init; }

    public required string PlayerId { get; init; }

    public required Money Amount { get; init; }

    public required PaymentMethod Method { get; init; }

    /// <summary>The caller's IP address in canonical text form.</summary>
    public required string IpAddress { get; init; }

    /// <summary>
    /// The key used for IP velocity: the address itself for IPv4, the /64 network for IPv6
    /// (one subscriber usually owns a whole /64, so per-address counting is trivially evaded).
    /// </summary>
    public required string IpKey { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country the IP geolocates to, upper case.</summary>
    public required string IpCountry { get; init; }

    /// <summary>Issuing country of the card BIN (card payments only), upper case.</summary>
    public string? CardBinCountry { get; init; }

    /// <summary>A PSP-issued card token or fingerprint. Never a card number.</summary>
    public string? CardFingerprint { get; init; }

    /// <summary>Wallet network or scheme name (wallet payments only).</summary>
    public string? WalletNetwork { get; init; }

    public string? DeviceId { get; init; }

    public required int AccountAgeDays { get; init; }

    /// <summary>The identifier a rule on <paramref name="dimension"/> is keyed on, or null when the attempt has none.</summary>
    public string? SubjectFor(Dimension dimension) => dimension switch
    {
        Dimension.Player => PlayerId,
        Dimension.Device => DeviceId,
        Dimension.Ip => IpKey,
        Dimension.Card => CardFingerprint,
        _ => null,
    };
}
