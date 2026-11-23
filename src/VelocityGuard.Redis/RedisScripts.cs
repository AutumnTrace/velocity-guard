namespace VelocityGuard.Redis;

/// <summary>
/// The Lua scripts, embedded from <c>Scripts/*.lua</c>. StackExchange.Redis caches each script's SHA1
/// and sends <c>EVALSHA</c>, falling back to <c>EVAL</c> after a <c>NOSCRIPT</c> (e.g. a Redis restart).
/// </summary>
internal static class RedisScripts
{
    public static readonly string RecordAttempt = Load("record_attempt.lua");

    public static readonly string SetOutcome = Load("set_outcome.lua");

    public static readonly string ApplyOutcome = Load("apply_outcome.lua");

    private static string Load(string name)
    {
        using var stream = typeof(RedisScripts).Assembly.GetManifestResourceStream("VelocityGuard.Redis.Scripts." + name)
            ?? throw new InvalidOperationException($"Embedded script {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
