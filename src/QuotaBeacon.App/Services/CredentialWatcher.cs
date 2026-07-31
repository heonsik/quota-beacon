using QuotaBeacon.Core;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Watches the vendor CLI credential files and reports when one is rewritten.
/// </summary>
/// <remarks>
/// <para>
/// An expired token is not a retryable error — polling harder cannot fix it — so the scheduler backs
/// a provider off for an hour when it sees one. That is correct while nothing changes, but a CLI
/// access token lasts only hours, and the user's own fix is to run the CLI once. Without this watcher
/// they would fix it and then wait up to an hour for the app to notice.
/// </para>
/// <para>
/// The directory is watched rather than the file: vendors write credentials by creating a temporary
/// file and replacing the original, which a file-scoped watcher can miss entirely.
/// </para>
/// </remarks>
public sealed class CredentialWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<ProviderId, DateTimeOffset> _lastRaised = [];
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// How long to ignore further changes for the same provider after reporting one.
    /// </summary>
    /// <remarks>
    /// A single credential write produces several filesystem events — create, write, rename — and
    /// each one would otherwise trigger its own refresh.
    /// </remarks>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);

    public CredentialWatcher(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);

        Watch(ProviderId.Claude, "CLAUDE_CONFIG_DIR", ".claude", ".credentials.json");
        Watch(ProviderId.Codex, "CODEX_HOME", ".codex", "auth.json");
    }

    /// <summary>Raised on a background thread when a provider's credential file changes.</summary>
    public event EventHandler<ProviderId>? CredentialChanged;

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void Watch(ProviderId provider, string environmentVariable, string defaultDirectory, string fileName)
    {
        var overridden = Environment.GetEnvironmentVariable(environmentVariable);

        var directory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultDirectory)
            : overridden;

        // A missing directory means the CLI is not installed. Watching a path that does not exist
        // throws, and there is nothing to wait for, so this provider simply has no watcher.
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            watcher.Changed += (_, _) => Raise(provider);
            watcher.Created += (_, _) => Raise(provider);
            watcher.Renamed += (_, _) => Raise(provider);

            _watchers.Add(watcher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the watcher costs promptness, not correctness: the scheduled poll still recovers
            // once the backoff elapses.
        }
    }

    private void Raise(ProviderId provider)
    {
        lock (_gate)
        {
            var now = _clock();

            if (_lastRaised.TryGetValue(provider, out var last) && now - last < Quiet)
            {
                return;
            }

            _lastRaised[provider] = now;
        }

        CredentialChanged?.Invoke(this, provider);
    }
}
