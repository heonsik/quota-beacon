using QuotaBeacon.Core;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Keeps one copy of QuotaBeacon per user, and lets a second launch surface the first.
/// </summary>
/// <remarks>
/// <para>
/// Not a nicety. WebView2 user-data folders are single-process, so a second copy cannot read the
/// signed-in session at all — it fails to open the profile and reports the provider as unavailable,
/// which looks exactly like a bug. Two tray icons for one app is its own confusion on top of that.
/// </para>
/// <para>
/// The mutex is scoped to the session and named per user, so it does not interfere across a shared
/// machine or a terminal server.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\QuotaBeacon.SingleInstance";
    private const string SignalName = @"Local\QuotaBeacon.Show";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showRequested;
    private readonly CancellationTokenSource _shutdown = new();

    private SingleInstance(Mutex? mutex, EventWaitHandle? showRequested, bool isFirst)
    {
        _mutex = mutex;
        _showRequested = showRequested;
        IsFirstInstance = isFirst;
    }

    public bool IsFirstInstance { get; }

    /// <summary>Raised when another launch asked the running copy to show itself.</summary>
    public event EventHandler? ShowRequested;

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        // A named event is enough of a channel for the one thing a second launch needs to say.
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

        if (isFirst)
        {
            return new SingleInstance(mutex, signal, true);
        }

        // Ask the running copy to come forward, then let this process exit.
        signal.Set();
        signal.Dispose();
        mutex.Dispose();

        return new SingleInstance(null, null, false);
    }

    /// <summary>Starts listening for a second launch. First instance only.</summary>
    public void Listen()
    {
        if (_showRequested is null)
        {
            return;
        }

        var thread = new Thread(Wait)
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };

        thread.Start();
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        // Signalling wakes the waiting thread so it can observe the cancellation and exit.
        _showRequested?.Set();
        _showRequested?.Dispose();

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread; releasing is best effort and the handle closes regardless.
            }

            _mutex.Dispose();
        }

        _shutdown.Dispose();
    }

    private void Wait()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            _showRequested!.WaitOne();

            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            Log.Info("app", "another launch asked this instance to show itself");
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
