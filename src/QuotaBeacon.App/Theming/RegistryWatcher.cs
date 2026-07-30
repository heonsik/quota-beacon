using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace QuotaBeacon.App.Theming;

/// <summary>
/// Raises a callback when a registry key changes.
/// </summary>
/// <remarks>
/// Uses <c>RegNotifyChangeKeyValue</c> on a background thread rather than a timer, so a theme switch
/// is reflected immediately and an idle app costs nothing. The callback is marshalled to the UI
/// thread because it drives brush updates. Registration is re-armed after every notification, since
/// the API signals once per call.
/// </remarks>
internal sealed class RegistryWatcher : IDisposable
{
    private const int NotifyChangeLastSet = 0x00000004;

    private readonly Action _onChanged;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEvent _signal = new(false);

    public RegistryWatcher(RegistryHive hive, string subKey, Action onChanged)
    {
        _onChanged = onChanged;

        _ = Task.Factory.StartNew(
            () => Watch(hive, subKey),
            _cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        // Release the wait so the thread observes cancellation instead of blocking until the next
        // registry change, which might never come.
        _signal.Set();
        _cancellation.Dispose();
        _signal.Dispose();
    }

    private void Watch(RegistryHive hive, string subKey)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);

        if (key is null)
        {
            return;
        }

        var handle = key.Handle.DangerousGetHandle();

        while (!_cancellation.IsCancellationRequested)
        {
            if (RegNotifyChangeKeyValue(handle, false, NotifyChangeLastSet, _signal.SafeWaitHandle.DangerousGetHandle(), true) != 0)
            {
                return;
            }

            _signal.WaitOne();
            _signal.Reset();

            if (_cancellation.IsCancellationRequested)
            {
                return;
            }

            Application.Current?.Dispatcher.BeginInvoke(_onChanged);
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
