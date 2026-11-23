using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VelocityGuard.Core.Diagnostics;
using VelocityGuard.Core.Rules;

namespace VelocityGuard.Core.Assessment;

/// <summary>
/// Holds the active <see cref="CompiledRuleSet"/> and hot-swaps it when <c>rules.json</c> changes.
/// </summary>
/// <remarks>
/// <para>Startup is strict: an invalid rule set throws, so the service never starts with rules it cannot run.</para>
/// <para>Reloads are forgiving: an invalid edit is logged, counted (<c>velocityguard.rules.reloads{result=rejected}</c>)
/// and ignored, and the last good rule set keeps serving. Requests in flight finish on the instance they started with.</para>
/// </remarks>
public sealed class RuleSetProvider : IDisposable
{
    private readonly TimeProvider _time;
    private readonly RiskMetrics _metrics;
    private readonly ILogger<RuleSetProvider> _logger;
    private readonly Lock _reloadGate = new();
    private readonly IDisposable? _subscription;
    private CompiledRuleSet _current;

    public RuleSetProvider(IOptionsMonitor<RiskRulesOptions> options, TimeProvider time, RiskMetrics metrics, ILogger<RuleSetProvider> logger)
    {
        _time = time;
        _metrics = metrics;
        _logger = logger;
        _current = RuleCompiler.Compile(options.CurrentValue, time.GetUtcNow());
        Log.RulesLoaded(logger, _current.Version, _current.Count);
        _subscription = options.OnChange(Reload);
    }

    public CompiledRuleSet Current => Volatile.Read(ref _current);

    public void Dispose() => _subscription?.Dispose();

    private void Reload(RiskRulesOptions options)
    {
        lock (_reloadGate)
        {
            var previous = Current;
            try
            {
                // File watchers often fire twice for one save; only recompile when the content changed.
                if (RuleCompiler.VersionOf(options) == previous.Version)
                {
                    return;
                }

                var next = RuleCompiler.Compile(options, _time.GetUtcNow());
                Volatile.Write(ref _current, next);
                _metrics.RulesReloaded(applied: true);
                Log.RulesReloaded(_logger, previous.Version, next.Version, next.Count);
            }
            catch (OptionsValidationException ex)
            {
                _metrics.RulesReloaded(applied: false);
                Log.RulesRejected(_logger, previous.Version, RuleCompiler.Describe(ex));
            }
        }
    }
}
