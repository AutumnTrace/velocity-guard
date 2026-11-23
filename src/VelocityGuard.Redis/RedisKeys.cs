using StackExchange.Redis;
using VelocityGuard.Core.Payments;

namespace VelocityGuard.Redis;

/// <summary>
/// Key layout. A subject's two sorted sets share a <c>{hash tag}</c>, so the per-subject Lua script
/// only touches one hash slot and works unchanged on Redis Cluster.
/// </summary>
/// <remarks>
/// <code>
/// vg:{device:dev-7f3a}:att      sorted set, attempt ids by time
/// vg:{device:dev-7f3a}:amt      sorted set, "attemptId|EUR|2500" by time
/// vg:attempt:att_0193...        hash, journal entry (expires after the journal retention)
/// vg:list:{blocked-devices}     set (tagged so a staging copy can be RENAMEd over it on a cluster)
/// vg:profile:p-1001             hash
/// </code>
/// </remarks>
public sealed class RedisKeys(string prefix)
{
    public RedisKey Attempts(Dimension dimension, string subject) => $"{prefix}:{{{Name(dimension)}:{subject}}}:att";

    public RedisKey Amounts(Dimension dimension, string subject) => $"{prefix}:{{{Name(dimension)}:{subject}}}:amt";

    public RedisKey Journal(string attemptId) => $"{prefix}:attempt:{attemptId}";

    public RedisKey List(string list) => $"{prefix}:list:{{{list}}}";

    public RedisKey Profile(string playerId) => $"{prefix}:profile:{playerId}";

    private static string Name(Dimension dimension) => dimension switch
    {
        Dimension.Player => "player",
        Dimension.Device => "device",
        Dimension.Ip => "ip",
        Dimension.Card => "card",
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };
}
