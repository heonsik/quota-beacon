using QuotaBeacon.Core;

namespace QuotaBeacon.Tests;

public class QuotaSnapshotTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");

    [Fact]
    public void Success_carries_meters_and_reports_success()
    {
        var snapshot = QuotaSnapshot.Success(
            ProviderId.Claude,
            Now,
            [Meter.Window("claude.session5h", "5-hour", 0.4)]);

        Assert.True(snapshot.IsSuccess);
        Assert.Null(snapshot.Error);
        Assert.Single(snapshot.Meters);
    }

    [Fact]
    public void Success_rejects_an_empty_meter_set()
    {
        // A 200 that maps to nothing must surface as UnrecognizedResponse, so the UI shows an
        // actionable error rather than a confident blank.
        Assert.Throws<ArgumentException>(
            () => QuotaSnapshot.Success(ProviderId.Claude, Now, []));
    }

    [Fact]
    public void Success_rejects_duplicate_meter_ids()
    {
        // Alert latching keys on meter id, so duplicates would make latches collide.
        Assert.Throws<ArgumentException>(() => QuotaSnapshot.Success(
            ProviderId.Codex,
            Now,
            [Meter.Window("codex.5h", "5-hour", 0.1), Meter.Window("codex.5h", "Again", 0.2)]));
    }

    [Fact]
    public void Failure_reports_the_error_and_no_meters()
    {
        var error = new ProviderError(ProviderErrorKind.AuthenticationExpired, "Sign in again.");

        var snapshot = QuotaSnapshot.Failure(ProviderId.Codex, Now, error);

        Assert.False(snapshot.IsSuccess);
        Assert.Empty(snapshot.Meters);
        Assert.Equal(error, snapshot.Error);
    }

    [Theory]
    [InlineData(ProviderErrorKind.RateLimited, true)]
    [InlineData(ProviderErrorKind.Network, true)]
    [InlineData(ProviderErrorKind.Unexpected, true)]
    [InlineData(ProviderErrorKind.AuthenticationMissing, false)]
    [InlineData(ProviderErrorKind.AuthenticationExpired, false)]
    [InlineData(ProviderErrorKind.UnrecognizedResponse, false)]
    public void Retryability_follows_the_error_category(ProviderErrorKind kind, bool retryable)
    {
        Assert.Equal(retryable, new ProviderError(kind, "m").IsRetryable);
    }
}
