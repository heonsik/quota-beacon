using System.Globalization;
using System.Text;
using QuotaBeacon.Core;

namespace QuotaBeacon.App.Services;

/// <summary>
/// Writes the log to a file under <c>%LOCALAPPDATA%\QuotaBeacon\logs</c>.
/// </summary>
/// <remarks>
/// <para>
/// One file per day, with old ones pruned. The app talks to undocumented endpoints that can change
/// without notice, so when a reading looks wrong the log is the only way to tell a shape change from
/// an auth problem from a network failure without attaching a debugger.
/// </para>
/// <para>
/// Writes are serialized and flushed immediately. A log that buffers is worthless for diagnosing a
/// crash, which is exactly when the last few lines matter most.
/// </para>
/// </remarks>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private const int RetainedDays = 7;
    private const long MaximumBytes = 8 * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _file;

    private bool _failed;

    public FileLogSink()
    {
        LogDirectory = Path.Combine(AppSettings.Directory, "logs");
        _file = Path.Combine(LogDirectory, $"quotabeacon-{DateTime.Now:yyyy-MM-dd}.log");

        try
        {
            Directory.CreateDirectory(LogDirectory);
            Prune();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _failed = true;
        }
    }

    /// <summary>Folder holding the rolling log files, so settings can offer to open it.</summary>
    public string LogDirectory { get; }

    public void Write(LogLevel level, string category, string message)
    {
        if (_failed)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Abbreviate(level)} [{category}] {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                // Rolling by size as well as by day: a provider stuck in a retry loop could otherwise
                // fill the disk between midnights.
                if (File.Exists(_file) && new FileInfo(_file).Length > MaximumBytes)
                {
                    File.Move(_file, _file + ".1", overwrite: true);
                }

                File.AppendAllText(_file, line, Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the reason the app misbehaves. Stop trying rather than
                // throwing from every call site for the rest of the session.
                _failed = true;
            }
        }
    }

    public void Dispose()
    {
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        _ => "ERR",
    };

    private void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-RetainedDays);

        foreach (var file in Directory.EnumerateFiles(LogDirectory, "quotabeacon-*.log*"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file held open by a viewer is not worth failing startup over.
            }
        }
    }
}
