namespace QuotaBeacon.Core;

public enum ProviderId
{
    Claude,
    Codex,
}

/// <summary>
/// The result of one refresh attempt for one provider.
/// </summary>
/// <remarks>
/// A snapshot is either a success carrying meters or a failure carrying an error. Providers are
/// refreshed independently and cached independently, so one provider's failure never suppresses
/// the other's values.
/// </remarks>
public sealed record QuotaSnapshot
{
    private QuotaSnapshot(ProviderId provider, DateTimeOffset fetchedAt)
    {
        Provider = provider;
        FetchedAt = fetchedAt;
    }

    public ProviderId Provider { get; }

    public DateTimeOffset FetchedAt { get; }

    public IReadOnlyList<Meter> Meters { get; private init; } = [];

    public ProviderError? Error { get; private init; }

    public bool IsSuccess => Error is null;

    /// <summary>
    /// Creates a successful snapshot.
    /// </summary>
    /// <remarks>
    /// A success with no meters is rejected: a 200 response that maps to nothing is an
    /// <see cref="ProviderErrorKind.UnrecognizedResponse"/> failure, not an empty success. Allowing
    /// it would let the UI show a confident blank instead of an actionable error.
    /// </remarks>
    public static QuotaSnapshot Success(
        ProviderId provider,
        DateTimeOffset fetchedAt,
        IEnumerable<Meter> meters)
    {
        var materialized = meters.ToArray();

        if (materialized.Length == 0)
        {
            throw new ArgumentException(
                "A successful snapshot must carry at least one meter; map an empty result to UnrecognizedResponse.",
                nameof(meters));
        }

        var duplicate = materialized
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Meter ids must be unique within a snapshot; '{duplicate.Key}' appeared more than once.",
                nameof(meters));
        }

        return new QuotaSnapshot(provider, fetchedAt) { Meters = materialized };
    }

    public static QuotaSnapshot Failure(
        ProviderId provider,
        DateTimeOffset fetchedAt,
        ProviderError error) =>
        new(provider, fetchedAt) { Error = error };
}
