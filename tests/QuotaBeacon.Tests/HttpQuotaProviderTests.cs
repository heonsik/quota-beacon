using System.Net;
using QuotaBeacon.Core;
using QuotaBeacon.Providers;

namespace QuotaBeacon.Tests;

public class HttpQuotaProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");

    private static readonly Uri First = new("https://example.test/first");
    private static readonly Uri Second = new("https://example.test/second");

    private static readonly IReadOnlyList<MeterDescriptor> Descriptors =
    [
        new WindowMeterDescriptor("test.window", "Window", ["five_hour"]),
    ];

    private static IReadOnlyList<QuotaSource> TwoSources() =>
    [
        new QuotaSource("first", First, Descriptors),
        new QuotaSource("second", Second, Descriptors),
    ];

    private static AuthChain StaticAuth() => new(
    [
        FakeAuthSource.Yielding(new AuthCredential(
            AuthSourceKind.Cli,
            new Dictionary<string, string> { ["Authorization"] = "Bearer test" })),
    ]);

    private static TestProvider Provider(
        StubHandler handler,
        AuthChain? auth = null,
        IReadOnlyList<QuotaSource>? sources = null) =>
        new(new HttpClient(handler), auth ?? StaticAuth(), sources ?? TwoSources());

    [Fact]
    public async Task A_working_first_endpoint_produces_a_success()
    {
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, """{"five_hour":{"used_percent":25}}""");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(0.25, Assert.Single(snapshot.Meters).Ratio!.Value, precision: 6);
        Assert.Equal(1, handler.CountFor(First));
    }

    [Fact]
    public async Task Probing_falls_through_to_the_next_candidate()
    {
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.NotFound, "{}");
        handler.Respond(Second, HttpStatusCode.OK, """{"five_hour":{"used_percent":60}}""");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(0.60, Assert.Single(snapshot.Meters).Ratio!.Value, precision: 6);
    }

    [Fact]
    public async Task A_two_hundred_that_maps_to_nothing_is_not_a_success()
    {
        // An empty success would render as a confident blank; the user needs an actionable error.
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, """{"detail":"nope"}""");
        handler.Respond(Second, HttpStatusCode.OK, """{"also":"nope"}""");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.False(snapshot.IsSuccess);
        Assert.Equal(ProviderErrorKind.UnrecognizedResponse, snapshot.Error!.Kind);
        Assert.Contains("detail", snapshot.Error.ResponseShape);
    }

    [Fact]
    public async Task The_endpoint_that_worked_is_tried_first_next_time()
    {
        // Probing should cost extra requests only until the account's shape is known.
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.NotFound, "{}");
        handler.Respond(Second, HttpStatusCode.OK, """{"five_hour":{"used_percent":10}}""");
        var provider = Provider(handler);

        await provider.FetchAsync(Now, CancellationToken.None);
        handler.Reset();
        await provider.FetchAsync(Now, CancellationToken.None);

        Assert.Equal(0, handler.CountFor(First));
        Assert.Equal(1, handler.CountFor(Second));
    }

    [Fact]
    public async Task Rejected_credentials_stop_the_probe_immediately()
    {
        // An auth failure repeats on every endpoint, so probing on would only burn requests.
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.Unauthorized, "{}");
        handler.Respond(Second, HttpStatusCode.OK, """{"five_hour":{"used_percent":10}}""");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.AuthenticationExpired, snapshot.Error!.Kind);
        Assert.Equal(0, handler.CountFor(Second));
    }

    [Fact]
    public async Task Rejected_cli_credentials_fall_through_to_web_credentials()
    {
        var handler = new StubHandler();
        handler.RespondSequence(
            First,
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.OK, """{"five_hour":{"used_percent":40}}"""));

        var web = FakeAuthSource.Yielding(
            new AuthCredential(
                AuthSourceKind.Web,
                new Dictionary<string, string> { ["Cookie"] = "session=web" }),
            AuthSourceKind.Web);

        var chain = new AuthChain(
        [
            FakeAuthSource.Yielding(new AuthCredential(
                AuthSourceKind.Cli,
                new Dictionary<string, string> { ["Authorization"] = "Bearer stale" })),
            web,
        ]);

        var snapshot = await Provider(handler, chain).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(2, handler.CountFor(First));
        Assert.Equal(1, web.AcquireCount);
        Assert.Equal("session=web", handler.LastRequestHeader(First, "Cookie"));
    }

    [Fact]
    public async Task Successful_cli_credentials_do_not_acquire_the_web_source()
    {
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, """{"five_hour":{"used_percent":25}}""");
        var web = FakeAuthSource.Yielding(
            new AuthCredential(AuthSourceKind.Web, new Dictionary<string, string> { ["Cookie"] = "session=web" }),
            AuthSourceKind.Web);

        var chain = new AuthChain(
        [
            FakeAuthSource.Yielding(new AuthCredential(
                AuthSourceKind.Cli,
                new Dictionary<string, string> { ["Authorization"] = "Bearer good" })),
            web,
        ]);

        var snapshot = await Provider(handler, chain).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(0, web.AcquireCount);
    }

    [Fact]
    public async Task Web_credentials_skip_sources_that_only_accept_cli_authentication()
    {
        var handler = new StubHandler();
        handler.Respond(Second, HttpStatusCode.OK, """{"five_hour":{"used_percent":30}}""");
        var sources = new[]
        {
            new QuotaSource("cli", First, Descriptors, [AuthSourceKind.Cli]),
            new QuotaSource("web", Second, Descriptors, [AuthSourceKind.Web]),
        };
        var chain = new AuthChain(
        [
            FakeAuthSource.Yielding(
                new AuthCredential(AuthSourceKind.Web, new Dictionary<string, string> { ["Cookie"] = "session=web" }),
                AuthSourceKind.Web),
        ]);

        var snapshot = await Provider(handler, chain, sources).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal(0, handler.CountFor(First));
        Assert.Equal(1, handler.CountFor(Second));
    }

    [Fact]
    public async Task Rate_limiting_is_reported_with_the_requested_delay()
    {
        var handler = new StubHandler();
        handler.Respond(
            First,
            HttpStatusCode.TooManyRequests,
            "{}",
            retryAfter: TimeSpan.FromSeconds(90));
        handler.Respond(Second, HttpStatusCode.NotFound, "{}");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.RateLimited, snapshot.Error!.Kind);
        Assert.Equal(TimeSpan.FromSeconds(90), snapshot.Error.RetryAfter);
        Assert.True(snapshot.Error.IsRetryable);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_as_network()
    {
        var handler = new StubHandler();
        handler.Throw(First, new HttpRequestException("dns"));
        handler.Throw(Second, new HttpRequestException("dns"));

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.Network, snapshot.Error!.Kind);
    }

    [Fact]
    public async Task Invalid_json_is_reported_as_unrecognized()
    {
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, "<html>signed out</html>");
        handler.Respond(Second, HttpStatusCode.OK, "<html>signed out</html>");

        var snapshot = await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.UnrecognizedResponse, snapshot.Error!.Kind);
    }

    [Fact]
    public async Task No_authentication_at_all_is_reported_before_any_request()
    {
        var handler = new StubHandler();

        var snapshot = await Provider(handler, new AuthChain([FakeAuthSource.Unavailable()]))
            .FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.AuthenticationMissing, snapshot.Error!.Kind);
        Assert.Equal(0, handler.TotalRequests);
    }

    [Fact]
    public async Task An_expired_cli_credential_falls_through_to_the_web_source()
    {
        // The user whose CLI token lapsed but who signed in through the app still gets values.
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, """{"five_hour":{"used_percent":33}}""");

        var chain = new AuthChain(
        [
            FakeAuthSource.Expired("Renew in the CLI."),
            FakeAuthSource.Yielding(new AuthCredential(
                AuthSourceKind.Web,
                new Dictionary<string, string> { ["Cookie"] = "session=x" })),
        ]);

        var snapshot = await Provider(handler, chain).FetchAsync(Now, CancellationToken.None);

        Assert.True(snapshot.IsSuccess);
        Assert.Equal("session=x", handler.LastRequestHeader(First, "Cookie"));
    }

    [Fact]
    public async Task An_expired_credential_with_no_fallback_reports_expiry_not_absence()
    {
        var chain = new AuthChain([FakeAuthSource.Expired("Renew in the CLI.")]);

        var snapshot = await Provider(new StubHandler(), chain).FetchAsync(Now, CancellationToken.None);

        Assert.Equal(ProviderErrorKind.AuthenticationExpired, snapshot.Error!.Kind);
        Assert.Equal("Renew in the CLI.", snapshot.Error.Message);
    }

    [Fact]
    public async Task Credential_headers_reach_the_request()
    {
        var handler = new StubHandler();
        handler.Respond(First, HttpStatusCode.OK, """{"five_hour":{"used_percent":5}}""");

        await Provider(handler).FetchAsync(Now, CancellationToken.None);

        Assert.Equal("Bearer test", handler.LastRequestHeader(First, "Authorization"));
    }

    private sealed class TestProvider(
        HttpClient http,
        AuthChain auth,
        IReadOnlyList<QuotaSource> sources) : HttpQuotaProvider(http, auth, sources)
    {
        public override ProviderId Id => ProviderId.Claude;

        public override string DisplayName => "Test";
    }

    private sealed class FakeAuthSource : IAuthSource
    {
        private readonly AuthCredential? _credential;
        private readonly AuthExpiredException? _expired;

        private FakeAuthSource(
            AuthCredential? credential,
            AuthExpiredException? expired,
            AuthSourceKind kind = AuthSourceKind.Cli)
        {
            _credential = credential;
            _expired = expired;
            Kind = kind;
        }

        public static FakeAuthSource Yielding(
            AuthCredential credential,
            AuthSourceKind kind = AuthSourceKind.Cli) => new(credential, null, kind);

        public static FakeAuthSource Unavailable() => new(null, null);

        public static FakeAuthSource Expired(string message) =>
            new(null, new AuthExpiredException(message));

        public AuthSourceKind Kind { get; }

        public int AcquireCount { get; private set; }

        public Task<AuthCredential?> TryAcquireAsync(CancellationToken cancellationToken)
        {
            AcquireCount++;
            return _expired is not null
                ? throw _expired
                : Task.FromResult(_credential);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<HttpResponseMessage>> _responses = [];
        private readonly Dictionary<Uri, int> _counts = [];
        private readonly Dictionary<Uri, HttpRequestMessage> _lastRequests = [];

        public int TotalRequests => _counts.Values.Sum();

        public void Respond(Uri uri, HttpStatusCode status, string body, TimeSpan? retryAfter = null) =>
            _responses[uri] = () =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body),
                };

                if (retryAfter is { } delta)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
                }

                return response;
            };

        public void RespondSequence(Uri uri, params (HttpStatusCode Status, string Body)[] responses)
        {
            var queue = new Queue<(HttpStatusCode Status, string Body)>(responses);
            _responses[uri] = () =>
            {
                var response = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return new HttpResponseMessage(response.Status)
                {
                    Content = new StringContent(response.Body),
                };
            };
        }

        public void Throw(Uri uri, Exception exception) =>
            _responses[uri] = () => throw exception;

        public void Reset() => _counts.Clear();

        public int CountFor(Uri uri) => _counts.GetValueOrDefault(uri);

        public string? LastRequestHeader(Uri uri, string name) =>
            _lastRequests.TryGetValue(uri, out var request)
            && request.Headers.TryGetValues(name, out var values)
                ? string.Join(",", values)
                : null;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            _counts[uri] = _counts.GetValueOrDefault(uri) + 1;
            _lastRequests[uri] = request;

            if (!_responses.TryGetValue(uri, out var factory))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}"),
                });
            }

            return Task.FromResult(factory());
        }
    }
}
