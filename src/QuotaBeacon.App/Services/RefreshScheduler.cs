using QuotaBeacon.Core;

namespace QuotaBeacon.App.Services;

/// <summary>The state of every provider after a refresh, ready for display.</summary>
public sealed record RefreshResult(
    IReadOnlyList<ProviderState> States,
    TrayState TrayState,
    IReadOnlyList<Alert> Alerts);

/// <summary>
/// Drives refreshes and applies backoff.
/// </summary>
/// <remarks>
/// <para>
/// Providers are refreshed concurrently and awaited together, but each is isolated: one provider's
/// fault cannot cancel the other's request, and a provider that throws despite its contract is
/// converted into a failure snapshot rather than taking down the loop.
/// </para>
/// <para>
/// Backoff is per provider, so a rate-limited Claude does not slow Codex down. Only retryable
/// categories back off; an expired credential waits for the user instead of doubling a delay it can
/// never escape.
/// </para>
/// </remarks>
public sealed class RefreshScheduler : IDisposable
{
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(60);

    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly SnapshotCache _cache;
    private readonly AlertEngine _alerts;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<ProviderId, DateTimeOffset> _nextAttempt = [];
    private readonly Dictionary<ProviderId, int> _consecutiveFailures = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public RefreshScheduler(
        IReadOnlyList<IQuotaProvider> providers,
        AlertEngine alerts,
        TimeSpan interval,
        Func<DateTimeOffset>? clock = null)
    {
        _providers = providers;
        _alerts = alerts;
        _interval = interval;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _cache = new SnapshotCache(interval);
    }

    /// <summary>Raised after every refresh, on the thread that awaited it.</summary>
    public event EventHandler<RefreshResult>? Refreshed;

    /// <summary>Runs until cancelled, refreshing on the configured interval.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);

        using var timer = new PeriodicTimer(_interval);

        await RefreshAsync(force: false, linked.Token).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
        {
            await RefreshAsync(force: false, linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refreshes now.
    /// </summary>
    /// <param name="force">
    /// When true, ignores backoff. A manual refresh is an explicit user request, and refusing it
    /// because of a backoff window the user cannot see would look broken.
    /// </param>
    public async Task<RefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        // Serialized so a manual refresh landing during a scheduled one cannot interleave cache
        // writes or double-deliver an alert.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = _clock();

            var due = _providers
                .Where(provider => force || IsDue(provider.Id, now))
                .ToArray();

            var snapshots = await Task.WhenAll(due.Select(provider => FetchAsync(provider, now, cancellationToken)))
                .ConfigureAwait(false);

            foreach (var snapshot in snapshots)
            {
                _cache.Record(snapshot);
                UpdateBackoff(snapshot, now);
            }

            var states = _cache.Get(_providers.Select(provider => provider.Id), now);
            var trayState = TrayStateResolver.Resolve(states, _alerts.Settings);
            var alerts = _alerts.Evaluate(states);

            var result = new RefreshResult(states, trayState, alerts);
            Refreshed?.Invoke(this, result);

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private async Task<QuotaSnapshot> FetchAsync(
        IQuotaProvider provider,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.FetchAsync(now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Providers are contracted not to throw. If one does, the bug must not stop the other
            // provider from updating, so it becomes an ordinary failure snapshot.
            return QuotaSnapshot.Failure(
                provider.Id,
                now,
                new ProviderError(
                    ProviderErrorKind.Unexpected,
                    $"{provider.DisplayName} check failed unexpectedly: {exception.GetType().Name}."));
        }
    }

    private bool IsDue(ProviderId provider, DateTimeOffset now) =>
        !_nextAttempt.TryGetValue(provider, out var next) || now >= next;

    private void UpdateBackoff(QuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.IsSuccess)
        {
            _consecutiveFailures.Remove(snapshot.Provider);
            _nextAttempt.Remove(snapshot.Provider);
            return;
        }

        var error = snapshot.Error!;

        if (!error.IsRetryable)
        {
            // Nothing will change until the user acts, so stop asking; a credential-file change or a
            // manual refresh is what resumes this provider.
            _nextAttempt[snapshot.Provider] = now + MaximumBackoff;
            return;
        }

        var failures = _consecutiveFailures.GetValueOrDefault(snapshot.Provider) + 1;
        _consecutiveFailures[snapshot.Provider] = failures;

        // Honour an explicit Retry-After over the computed curve: the server knows better than we do.
        var delay = error.RetryAfter ?? TimeSpan.FromTicks(
            Math.Min(_interval.Ticks * (1L << Math.Min(failures - 1, 8)), MaximumBackoff.Ticks));

        _nextAttempt[snapshot.Provider] = now + (delay > MaximumBackoff ? MaximumBackoff : delay);
    }
}
