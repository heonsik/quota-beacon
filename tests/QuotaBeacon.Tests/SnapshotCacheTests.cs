using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class SnapshotCacheTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static QuotaSnapshot Success(ProviderId provider, DateTimeOffset at, double consumed) =>
        QuotaSnapshot.Success(provider, at, [Meter.Window("m", "M", consumed)]);

    [Fact]
    public void An_unknown_provider_reports_empty_and_fresh()
    {
        var cache = new SnapshotCache(Interval);

        var state = cache.Get(ProviderId.Claude, T0);

        Assert.True(state.IsEmpty);
        Assert.False(state.IsStale);
        Assert.Null(state.Error);
        Assert.Null(state.LastSuccessAt);
    }

    [Fact]
    public void A_success_is_returned_as_fresh_values()
    {
        var cache = new SnapshotCache(Interval);
        cache.Record(Success(ProviderId.Claude, T0, 0.4));

        var state = cache.Get(ProviderId.Claude, T0);

        Assert.True(state.HasValues);
        Assert.False(state.IsStale);
        Assert.Equal(T0, state.LastSuccessAt);
    }

    [Fact]
    public void A_failure_preserves_the_last_good_values_and_attaches_the_error()
    {
        var cache = new SnapshotCache(Interval);
        cache.Record(Success(ProviderId.Claude, T0, 0.4));

        cache.Record(QuotaSnapshot.Failure(
            ProviderId.Claude,
            T0.AddMinutes(5),
            new ProviderError(ProviderErrorKind.Network, "Timed out.")));

        var state = cache.Get(ProviderId.Claude, T0.AddMinutes(5));

        Assert.True(state.HasValues);
        Assert.Equal(ProviderErrorKind.Network, state.Error!.Kind);
        // The timestamp still refers to the last success, not the failed attempt.
        Assert.Equal(T0, state.LastSuccessAt);
    }

    [Fact]
    public void A_later_success_clears_the_error()
    {
        var cache = new SnapshotCache(Interval);
        cache.Record(QuotaSnapshot.Failure(
            ProviderId.Codex,
            T0,
            new ProviderError(ProviderErrorKind.Network, "Timed out.")));

        cache.Record(Success(ProviderId.Codex, T0.AddMinutes(5), 0.2));

        Assert.Null(cache.Get(ProviderId.Codex, T0.AddMinutes(5)).Error);
    }

    [Fact]
    public void A_failure_with_no_prior_success_has_no_values()
    {
        var cache = new SnapshotCache(Interval);

        cache.Record(QuotaSnapshot.Failure(
            ProviderId.Codex,
            T0,
            new ProviderError(ProviderErrorKind.AuthenticationMissing, "Sign in.")));

        var state = cache.Get(ProviderId.Codex, T0);

        Assert.True(state.IsEmpty);
        Assert.False(state.IsStale);
        Assert.Equal(ProviderErrorKind.AuthenticationMissing, state.Error!.Kind);
    }

    [Fact]
    public void Values_become_stale_only_after_two_missed_refreshes()
    {
        var cache = new SnapshotCache(Interval);
        cache.Record(Success(ProviderId.Claude, T0, 0.4));

        Assert.False(cache.Get(ProviderId.Claude, T0.AddMinutes(10)).IsStale);
        Assert.True(cache.Get(ProviderId.Claude, T0.AddMinutes(11)).IsStale);
    }

    [Fact]
    public void Providers_are_tracked_independently()
    {
        var cache = new SnapshotCache(Interval);
        cache.Record(Success(ProviderId.Claude, T0, 0.4));
        cache.Record(Success(ProviderId.Codex, T0.AddMinutes(30), 0.6));

        var states = cache.Get([ProviderId.Claude, ProviderId.Codex], T0.AddMinutes(30));

        Assert.True(states.Single(s => s.Provider == ProviderId.Claude).IsStale);
        Assert.False(states.Single(s => s.Provider == ProviderId.Codex).IsStale);
    }
}
