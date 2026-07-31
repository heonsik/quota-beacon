using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuotaBeacon.Core;
using QuotaBeacon.Providers;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Runs a GET inside a WebView2 and returns the status and body.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteScriptAsync</c> does not await promises — it serializes whatever the last expression
/// evaluates to, so an <c>async</c> script hands back <c>{}</c> and the real result is lost. That is
/// a quiet trap: the call succeeds and returns something plausible. The reliable pattern is to let
/// the script post its result back over the host channel and wait for that message, which is what
/// this does.
/// </para>
/// <para>
/// Each call carries a correlation id so overlapping requests cannot claim each other's replies.
/// </para>
/// </remarks>
internal static class BrowserFetch
{
    public static async Task<FetchResult> GetAsync(
        WebView2 view,
        Uri uri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var core = view.CoreWebView2;
        var correlation = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<FetchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var document = JsonDocument.Parse(args.WebMessageAsJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("id", out var id) || id.GetString() != correlation)
                {
                    return;
                }

                var status = root.TryGetProperty("status", out var s) ? s.GetInt32() : -1;
                var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

                completion.TrySetResult(new FetchResult(status, body));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Some other component posting on the same channel; not ours to interpret.
            }
        }

        core.WebMessageReceived += OnMessage;

        try
        {
            // Errors are reported as a status rather than thrown, so a network failure inside the page
            // does not leave the wait hanging until the timeout.
            var script = $$"""
                (async () => {
                  const reply = (status, body) =>
                    window.chrome.webview.postMessage({ id: {{JsonSerializer.Serialize(correlation)}}, status, body });
                  try {
                    const response = await fetch({{JsonSerializer.Serialize(uri.ToString())}}, {
                      credentials: 'include',
                      headers: { 'Accept': 'application/json' }
                    });
                    reply(response.status, await response.text());
                  } catch (error) {
                    reply(-1, String(error));
                  }
                })();
                """;

            await core.ExecuteScriptAsync(script);

            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timer.Token,
                cancellationToken);

            await using (linked.Token.Register(() => completion.TrySetCanceled()))
            {
                return await completion.Task.ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("browserfetch", $"no reply for {uri} within {timeout.TotalSeconds:0}s");
            return new FetchResult(-1, null);
        }
        finally
        {
            core.WebMessageReceived -= OnMessage;
        }
    }
}
