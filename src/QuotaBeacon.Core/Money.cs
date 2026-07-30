namespace QuotaBeacon.Core;

/// <summary>
/// An amount of money in a single currency.
/// </summary>
/// <remarks>
/// Currency is carried as an ISO 4217 code rather than a <see cref="System.Globalization.RegionInfo"/>
/// so that an unrecognized code from a provider round-trips for display instead of throwing.
/// </remarks>
public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>
    /// Divides this amount by <paramref name="limit"/> to produce a consumed fraction.
    /// </summary>
    /// <returns>
    /// The fraction consumed, or <c>null</c> when the limit is absent, not positive, or in a
    /// different currency. A zero or negative limit is not a full bar; it is an unknown
    /// denominator, and reporting it as 100% would be a fabricated value.
    /// </returns>
    public double? FractionOf(Money? limit)
    {
        if (limit is not { } cap)
        {
            return null;
        }

        if (cap.Amount <= 0m || !SameCurrencyAs(cap))
        {
            return null;
        }

        return (double)(Amount / cap.Amount);
    }

    public bool SameCurrencyAs(Money other) =>
        string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Amount:0.##} {Currency.ToUpperInvariant()}";
}
