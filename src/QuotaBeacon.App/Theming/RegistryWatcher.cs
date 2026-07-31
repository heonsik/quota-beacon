using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuotaBeacon.App.Theming;

/// <summary>
/// Raises a callback when a registry key changes.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>RegNotifyChangeKeyValue</c> on a background thread rather than a timer, so a theme switch
/// is reflected immediately and an idle app costs nothing. The callback is marshalled to the UI
/// thread because it drives brush updates. Registration is re-armed after every notification, since
/// the API signals once per call.
/// </para>
/// <para>
/// Shutdown is the delicate part. The registration is asynchronous, which means the kernel holds a
/// reference to the event handle until it fires or the key closes. Closing that event from another
/// thread while the registration is live would hand the kernel a handle that may already have been
/// reused. So teardown closes the <em>registry key</em> instead: that both cancels the pending
/// registration and signals the event, letting the watcher thread wake, observe the stop request, and
/// dispose its own resources in the right order.
/// </para>
/// </remarks>
internal sealed class RegistryWatcher : IDisposable
{
    private const int NotifyChangeLastSet = 0x00000004;

    private readonly Action _onChanged;
    private readonly ManualResetEvent _signal = new(false);
    private readonly Thread _thread;

    private RegistryKey? _key;
    private volatile bool _stopping;

    public RegistryWatcher(RegistryHive hive, string subKey, Action onChanged)
    {
        _onChanged = onChanged;
        _key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);

        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = $"RegistryWatcher({subKey})",
        };

        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;

        // Closing the key cancels the registration and wakes the waiting thread.
        Interlocked.Exchange(ref _key, null)?.Dispose();

        // Nudge the event as well, in case the thread had not registered yet.
        _signal.Set();

        // Bounded: teardown must not hang shutdown if the thread is wedged. The thread is a
        // background thread, so even in that case it cannot keep the process alive.
        _thread.Join(TimeSpan.FromSeconds(2));

        _signal.Dispose();
    }

    private void Watch()
    {
        while (!_stopping)
        {
            var key = _key;

            if (key is null)
            {
                return;
            }

            try
            {
                if (RegNotifyChangeKeyValue(
                        key.Handle.DangerousGetHandle(),
                        false,
                        NotifyChangeLastSet,
                        _signal.SafeWaitHandle.DangerousGetHandle(),
                        true) != 0)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                // The key was closed by Dispose between the null check and here.
                return;
            }

            _signal.WaitOne();
            _signal.Reset();

            if (_stopping)
            {
                return;
            }

            Notify();
        }
    }

    private void Notify()
    {
        var dispatcher = Application.Current?.Dispatcher;

        // During shutdown the dispatcher stops accepting work, and queuing to it then throws.
        // A missed repaint on the way out is not worth an exception on a background thread.
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            dispatcher.BeginInvoke(_onChanged, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
            // Dispatcher shut down between the check and the queue.
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter,
        IntPtr eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
