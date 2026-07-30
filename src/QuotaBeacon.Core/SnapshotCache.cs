namespace QuotaBeacon.Core;

/// <summary>
/// What the UI needs to know about one provider right now.
/// </summary>
/// <param name="Meters">The last known good meters, empty if the provider never succeeded.</param>
/// <param name="LastSuccessAt">When those meters were fetched.</param>
/// <param name="Error">The most recent failure, retained even while stale values are shown.</param>
/// <param name="IsStale">Whether the values are too old to present as current.</param>
public sealed record ProviderState(
    ProviderId Provider,
    IReadOnlyList<Meter> Meters,
    DateTimeOffset? LastSuccessAt,
    ProviderError? Error,
    bool IsStale)
{
    public bool HasValues => Meters.Count > 0;

    /// <summary>
    /// True when there is nothing to show at all: no values ever retrieved. Distinct from stale,
    /// which still has something worth displaying.
    /// </summary>
    public bool IsEmpty => Meters.Count == 0;
}

/// <summary>
/// Retains the last successful snapshot per provider so a failed refresh degrades to stale values
/// rather than to a blank popup.
/// </summary>
/// <remarks>
/// Time is passed in rather than read from the clock so staleness transitions are directly
/// testable. Each provider is tracked independently; recording one provider never touches another.
/// </remarks>
public sealed class SnapshotCache(TimeSpan refreshInterval)
{
    private readonly Dictionary<ProviderId, Entry> _entries = [];

    /// <summary>
    /// Values older than twice the refresh interval are stale: one missed refresh is normal jitter,
    /// two means something is wrong.
    /// </summary>
    public TimeSpan StaleAfter { get; } = refreshInterval * 2;

    public void Record(QuotaSnapshot snapshot)
    {
        var existing = _entries.GetValueOrDefault(snapshot.Provider);

        _entries[snapshot.Provider] = snapshot.IsSuccess
            ? new Entry(snapshot.Meters, snapshot.FetchedAt, Error: null)
            // Preserve the last good values and their timestamp; only the error changes.
            : new Entry(
                existing?.Meters ?? [],
                existing?.LastSuccessAt,
                snapshot.Error);
    }

    public ProviderState Get(ProviderId provider, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(provider, out var entry))
        {
            return new ProviderState(provider, [], null, null, IsStale: false);
        }

        var isStale = entry.LastSuccessAt is { } at && now - at > StaleAfter;

        return new ProviderState(provider, entry.Meters, entry.LastSuccessAt, entry.Error, isStale);
    }

    public IReadOnlyList<ProviderState> Get(IEnumerable<ProviderId> providers, DateTimeOffset now) =>
        providers.Select(p => Get(p, now)).ToArray();

    private sealed record Entry(
        IReadOnlyList<Meter> Meters,
        DateTimeOffset? LastSuccessAt,
        ProviderError? Error);
}
