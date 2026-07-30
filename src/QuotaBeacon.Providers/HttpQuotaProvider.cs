using System.Net;
using System.Text.Json;
using QuotaBeacon.Core;

namespace QuotaBeacon.Providers;

/// <summary>One endpoint that might carry quota data, with the meters to look for in it.</summary>
public sealed record QuotaSource(
    string Name,
    Uri Endpoint,
    IReadOnlyList<MeterDescriptor> Descriptors);

/// <summary>
/// Fetches quota over HTTP by probing candidate endpoints until one yields meters.
/// </summary>
/// <remarks>
/// <para>
/// Consumption-based Enterprise response shapes are undocumented, so which endpoint works for a
/// given account is discovered at runtime rather than assumed at design time. The first endpoint
/// that produces at least one meter is remembered and tried first on subsequent refreshes, so
/// probing costs extra requests only until the account's shape is known.
/// </para>
/// <para>
/// This method never throws. Every failure becomes a <see cref="QuotaSnapshot.Failure"/> so the
/// refresh loop can treat providers uniformly.
/// </para>
/// </remarks>
public abstract class HttpQuotaProvider(
    HttpClient httpClient,
    AuthChain authChain,
    IReadOnlyList<QuotaSource> sources) : IQuotaProvider
{
    private string? _knownGoodSource;

    public abstract ProviderId Id { get; }

    public abstract string DisplayName { get; }

    public async Task<QuotaSnapshot> FetchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AuthCredential credential;
        try
        {
            credential = await authChain.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthUnavailableException unavailable)
        {
            return QuotaSnapshot.Failure(
                Id,
                now,
                new ProviderError(unavailable.Kind, DescribeAuthFailure(unavailable)));
        }

        ProviderError? bestError = null;

        foreach (var source in Ordered(sources))
        {
            var attempt = await AttemptAsync(source, credential, now, cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Snapshot is { } success)
            {
                _knownGoodSource = source.Name;
                return success;
            }

            bestError = MoreActionable(bestError, attempt.Error);

            // An authentication problem will repeat on every endpoint, so stop probing.
            if (attempt.Error?.Kind is ProviderErrorKind.AuthenticationExpired)
            {
                break;
            }
        }

        return QuotaSnapshot.Failure(
            Id,
            now,
            bestError ?? new ProviderError(
                ProviderErrorKind.Unexpected,
                $"{DisplayName} returned no usable quota data."));
    }

    /// <summary>Tries the endpoint that worked last time first, then the rest in declared order.</summary>
    private IEnumerable<QuotaSource> Ordered(IReadOnlyList<QuotaSource> candidates)
    {
        if (_knownGoodSource is null)
        {
            return candidates;
        }

        return candidates
            .Where(s => s.Name == _knownGoodSource)
            .Concat(candidates.Where(s => s.Name != _knownGoodSource));
    }

    private async Task<Attempt> AttemptAsync(
        QuotaSource source,
        AuthCredential credential,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Endpoint);

            foreach (var (name, value) in credential.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new Attempt(null, TranslateStatus(response));
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using var document = await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var meters = QuotaMapper.Map(document.RootElement, source.Descriptors, now);

            if (meters.Count == 0)
            {
                return new Attempt(
                    null,
                    new ProviderError(
                        ProviderErrorKind.UnrecognizedResponse,
                        $"{DisplayName} responded in a shape QuotaBeacon does not recognize.",
                        ResponseShape: QuotaMapper.DescribeShape(document.RootElement)));
            }

            return new Attempt(QuotaSnapshot.Success(Id, now, meters), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // A timeout surfaces as OperationCanceledException without the token being cancelled.
            return new Attempt(
                null,
                new ProviderError(ProviderErrorKind.Network, $"Could not reach {DisplayName}."));
        }
        catch (JsonException)
        {
            return new Attempt(
                null,
                new ProviderError(
                    ProviderErrorKind.UnrecognizedResponse,
                    $"{DisplayName} returned a response that is not valid JSON."));
        }
    }

    private ProviderError TranslateStatus(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderError(
            ProviderErrorKind.AuthenticationExpired,
            $"{DisplayName} rejected the saved sign-in. Sign in again to continue."),

        HttpStatusCode.TooManyRequests => new ProviderError(
            ProviderErrorKind.RateLimited,
            $"{DisplayName} is rate limiting usage checks.",
            RetryAfter: response.Headers.RetryAfter?.Delta),

        // A 404 means this candidate endpoint does not exist for this account, which is expected
        // while probing and must not be reported as a hard failure.
        HttpStatusCode.NotFound => new ProviderError(
            ProviderErrorKind.UnrecognizedResponse,
            $"{DisplayName} does not expose this usage endpoint for your account."),

        _ => new ProviderError(
            ProviderErrorKind.Unexpected,
            $"{DisplayName} returned HTTP {(int)response.StatusCode}."),
    };

    private string DescribeAuthFailure(AuthUnavailableException unavailable) =>
        unavailable.Kind == ProviderErrorKind.AuthenticationMissing
            ? $"Not signed in to {DisplayName}. Sign in through settings, or use its CLI."
            : unavailable.Message;

    /// <summary>
    /// Picks the error worth showing when every candidate failed.
    /// </summary>
    /// <remarks>
    /// Ranked by what the user can act on: renewing a sign-in beats waiting out a rate limit, which
    /// beats reporting a shape change, which beats a bare transport failure.
    /// </remarks>
    private static ProviderError? MoreActionable(ProviderError? current, ProviderError? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        return current is null || Rank(candidate.Kind) > Rank(current.Kind) ? candidate : current;
    }

    private static int Rank(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.AuthenticationExpired => 5,
        ProviderErrorKind.AuthenticationMissing => 4,
        ProviderErrorKind.RateLimited => 3,
        ProviderErrorKind.UnrecognizedResponse => 2,
        ProviderErrorKind.Network => 1,
        _ => 0,
    };

    private readonly record struct Attempt(QuotaSnapshot? Snapshot, ProviderError? Error);
}
