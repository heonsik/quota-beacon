namespace QuotaBeacon.Core;

public enum ProviderErrorKind
{
    /// <summary>No credential file was found for the provider.</summary>
    AuthenticationMissing,

    /// <summary>A credential exists but the provider rejected it.</summary>
    AuthenticationExpired,

    /// <summary>The provider throttled the request.</summary>
    RateLimited,

    /// <summary>Connectivity failure or timeout.</summary>
    Network,

    /// <summary>A success response whose body produced no meters.</summary>
    UnrecognizedResponse,

    /// <summary>Anything not covered above.</summary>
    Unexpected,
}

/// <summary>
/// A provider failure, carrying enough detail to guide the user without leaking secrets.
/// </summary>
/// <param name="Kind">The failure category, which decides retry policy and user guidance.</param>
/// <param name="Message">A user-facing sentence. Never contains credential material.</param>
/// <param name="RetryAfter">Server-requested delay, when the provider supplied one.</param>
/// <param name="ResponseShape">
/// For <see cref="ProviderErrorKind.UnrecognizedResponse"/>, a description of the body's
/// structure — key names and JSON types only, never values. This is what makes an
/// unmappable response diagnosable without logging account data.
/// </param>
public sealed record ProviderError(
    ProviderErrorKind Kind,
    string Message,
    TimeSpan? RetryAfter = null,
    string? ResponseShape = null)
{
    /// <summary>
    /// Whether retrying on the normal schedule can plausibly succeed. Authentication and
    /// mapping failures need a human or a code change, so retrying only burns requests.
    /// </summary>
    public bool IsRetryable => Kind is ProviderErrorKind.RateLimited
        or ProviderErrorKind.Network
        or ProviderErrorKind.Unexpected;
}
