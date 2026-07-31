using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Destroys WebView2 hosts without letting their finalizers run.
/// </summary>
/// <remarks>
/// <see cref="WebView2"/> dereferences state during teardown that only exists once initialization has
/// completed. A control that failed to initialize therefore throws <see cref="NullReferenceException"/>
/// from its own disposal — and on the finalizer thread that exception is unhandled and terminates the
/// process. Suppressing finalization is the only way to guarantee it never gets there, so it runs even
/// when the explicit disposal succeeds.
/// </remarks>
internal static class WebViewLifetime
{
    public static void Discard(WebView2? view, Window? window = null)
    {
        if (view is not null)
        {
            try
            {
                view.Dispose();
            }
            catch (Exception)
            {
                // An uninitialized control throws here. There is nothing to recover: the point of the
                // call is to release a control that *was* initialized, and the suppression below is
                // what actually protects the process.
            }
            finally
            {
                GC.SuppressFinalize(view);
            }
        }

        if (window is null)
        {
            return;
        }

        // Detach first so closing cannot walk back into the dead control.
        window.Content = null;
        window.Close();
    }
}
