using System.Globalization;
using StackExchange.Redis;
using VelocityGuard.Core.Signals;
using VelocityGuard.Core.Velocity;

namespace VelocityGuard.Redis;

/// <summary>Profiles as Redis hashes and lists as Redis sets. Read through <see cref="RiskSignalCache"/>.</summary>
public sealed class RedisRiskDataRepository(IConnectionMultiplexer redis, RedisKeys keys) : IRiskDataRepository
{
    public async ValueTask<PlayerProfile> GetProfileAsync(string playerId, CancellationToken cancellationToken)
    {
        var fields = await Db.HashGetAllAsync(keys.Profile(playerId)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return ParseProfile(fields);
    }

    public async ValueTask<PlayerProfile> ApplyOutcomeAsync(string playerId, PaymentOutcome outcome, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (outcome == PaymentOutcome.Declined)
        {
            return await GetProfileAsync(playerId, cancellationToken).ConfigureAwait(false);
        }

        RedisValue[] args = outcome == PaymentOutcome.Approved
            ? ["approved", at.ToUnixTimeMilliseconds()]
            : ["chargebacks", RedisValue.EmptyString];
        var reply = await Db.ScriptEvaluateAsync(RedisScripts.ApplyOutcome, [keys.Profile(playerId)], args)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var flat = (RedisValue[])reply!;
        var entries = new HashEntry[flat.Length / 2];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new HashEntry(flat[i * 2].ToString(), flat[(i * 2) + 1]);
        }

        return ParseProfile(entries);
    }

    public async ValueTask<bool> IsListedAsync(string list, string value, CancellationToken cancellationToken) =>
        await Db.SetContainsAsync(keys.List(list), value).WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> AddToListAsync(string list, string value, CancellationToken cancellationToken) =>
        await Db.SetAddAsync(keys.List(list), value).WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> RemoveFromListAsync(string list, string value, CancellationToken cancellationToken) =>
        await Db.SetRemoveAsync(keys.List(list), value).WaitAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask ReplaceListAsync(string list, IReadOnlyCollection<string> values, CancellationToken cancellationToken)
    {
        var key = keys.List(list);
        if (values.Count == 0)
        {
            await Db.KeyDeleteAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Build the new set under a temporary key, then RENAME over the old one: readers see either
        // the old list or the new one, never a half-written mix. The temp key shares the list key's slot.
        var staging = (RedisKey)$"{key}:staging:{Guid.NewGuid():N}";
        var db = Db;
        foreach (var chunk in values.Chunk(1000))
        {
            await db.SetAddAsync(staging, [.. chunk.Select(v => (RedisValue)v)]).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await db.KeyRenameAsync(staging, key).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> GetListAsync(string list, int limit, CancellationToken cancellationToken)
    {
        var entries = new List<string>(Math.Min(limit, 1000));
        await foreach (var value in Db.SetScanAsync(keys.List(list), pageSize: 500).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(value.ToString());
            if (entries.Count >= limit)
            {
                break;
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private IDatabase Db => redis.GetDatabase();

    private static PlayerProfile ParseProfile(HashEntry[] fields)
    {
        if (fields.Length == 0)
        {
            return PlayerProfile.New;
        }

        int approved = 0, chargebacks = 0;
        DateTimeOffset? lastApproved = null;
        foreach (var field in fields)
        {
            switch (field.Name.ToString())
            {
                case "approved":
                    approved = (int)field.Value;
                    break;
                case "chargebacks":
                    chargebacks = (int)field.Value;
                    break;
                case "lastApprovedAt":
                    lastApproved = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(field.Value.ToString(), CultureInfo.InvariantCulture));
                    break;
            }
        }

        return new PlayerProfile(approved, chargebacks, lastApproved);
    }
}
