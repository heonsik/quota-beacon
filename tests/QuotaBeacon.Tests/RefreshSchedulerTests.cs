using QuotaBeacon.App.Services;
using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class RefreshSchedulerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task An_expired_credential_stops_the_provider_being_polled()
    {
        // Polling cannot fix an expired token, so hammering the endpoint only burns requests.
        var clock = new TestClock();
        var provider = new FakeProvider(ProviderErrorKind.AuthenticationExpired);
        using var scheduler = Build(provider, clock);

        await scheduler.RefreshAsync(force: false, CancellationToken.None);
        clock.Advance(Interval);
        await scheduler.RefreshAsync(force: false, CancellationToken.None);

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Clearing_the_backoff_lets_the_provider_be_polled_again()
    {
        // This is what the credential watcher does when the vendor CLI rewrites its token file. Without
        // it the user fixes the problem and then waits an hour for the app to notice.
        var clock = new TestClock();
        var provider = new FakeProvider(ProviderErrorKind.AuthenticationExpired);
        using var scheduler = Build(provider, clock);

        await scheduler.RefreshAsync(force: false, CancellationToken.None);
        provider.Recover();
        scheduler.ClearBackoff(ProviderId.Claude);

        var result = await scheduler.RefreshAsync(force: false, CancellationToken.None);

        Assert.Equal(2, provider.Calls);
        Assert.True(result.States.Single().HasValues);
    }

    [Fact]
    public async Task A_manual_refresh_ignores_the_backoff()
    {
        var clock = new TestClock();
        var provider = new FakeProvider(ProviderErrorKind.AuthenticationExpired);
        using var scheduler = Build(provider, clock);

        await scheduler.RefreshAsync(force: false, CancellationToken.None);
        await scheduler.RefreshAsync(force: true, CancellationToken.None);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_retryable_failure_backs_off_and_then_retries()
    {
        var clock = new TestClock();
        var provider = new FakeProvider(ProviderErrorKind.Network);
        using var scheduler = Build(provider, clock);

        await scheduler.RefreshAsync(force: false, CancellationToken.None);

        // First backoff is one interval, so a tick earlier is still too soon.
        clock.Advance(Interval - TimeSpan.FromSeconds(1));
        await scheduler.RefreshAsync(force: false, CancellationToken.None);
        Assert.Equal(1, provider.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        await scheduler.RefreshAsync(force: false, CancellationToken.None);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_provider_that_throws_does_not_break_the_refresh()
    {
        // Providers are contracted not to throw; if one does, the other must still update.
        var clock = new TestClock();
        var thrower = new ThrowingProvider();
        var healthy = new FakeProvider(null) { Id = ProviderId.Codex };

        using var scheduler = new RefreshScheduler(
            [thrower, healthy],
            new AlertEngine(new AlertSettings()),
            Interval,
            clock.Now);

        var result = await scheduler.RefreshAsync(force: false, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.Unexpected, result.States.Single(s => s.Provider == ProviderId.Claude).Error!.Kind);
        Assert.True(result.States.Single(s => s.Provider == ProviderId.Codex).HasValues);
    }

    private static RefreshScheduler Build(IQuotaProvider provider, TestClock clock) =>
        new([provider], new AlertEngine(new AlertSettings()), Interval, clock.Now);

    private sealed class TestClock
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");

        public Func<DateTimeOffset> Now => () => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeProvider(ProviderErrorKind? failWith) : IQuotaProvider
    {
        private ProviderErrorKind? _failWith = failWith;

        public ProviderId Id { get; init; } = ProviderId.Claude;

        public string DisplayName => Id.ToString();

        public int Calls { get; private set; }

        public void Recover() => _failWith = null;

        public Task<QuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(_failWith is { } kind
                ? QuotaSnapshot.Failure(Id, now, new ProviderError(kind, "test"))
                : QuotaSnapshot.Success(Id, now, [Meter.Window("m", "M", 0.4)]));
        }
    }

    private sealed class ThrowingProvider : IQuotaProvider
    {
        public ProviderId Id => ProviderId.Claude;

        public string DisplayName => "Claude";

        public Task<QuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("contract violation");
    }
}
