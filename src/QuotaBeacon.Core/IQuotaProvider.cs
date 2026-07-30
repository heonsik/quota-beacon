namespace QuotaBeacon.Core;

/// <summary>
/// One monitored service. Implementations must never throw: every failure mode is expressed as a
/// <see cref="QuotaSnapshot.Failure"/> so the refresh loop can treat providers uniformly and one
/// provider's fault can never abort another's refresh.
/// </summary>
public interface IQuotaProvider
{
    ProviderId Id { get; }

    /// <summary>Display name for the provider, used in the popup tabs.</summary>
    string DisplayName { get; }

    Task<QuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
