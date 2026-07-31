namespace QuotaBeacon.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Somewhere log lines can go. Implemented by the app; the lower layers only write.</summary>
public interface ILogSink
{
    void Write(LogLevel level, string category, string message);
}

/// <summary>
/// The application's log.
/// </summary>
/// <remarks>
/// <para>
/// A static entry point rather than an injected logger. The alternative is threading a logger through
/// every provider, auth source, and mapper for the sake of diagnostics, and the cost of that
/// plumbing is not repaid: there is one process, one log, and no scenario where two parts of this app
/// want different sinks.
/// </para>
/// <para>
/// <strong>Never pass credential material to these methods.</strong> This app talks to undocumented
/// endpoints with tokens and cookies, and a log is a file that gets copied into bug reports. Log
/// header names, response shapes, and status codes — never their values. <see cref="Redact"/> exists
/// for the cases where a value must be referenced at all.
/// </para>
/// </remarks>
public static class Log
{
    private static ILogSink _sink = NullSink.Instance;

    public static void UseSink(ILogSink sink) => _sink = sink;

    public static void Debug(string category, string message) =>
        _sink.Write(LogLevel.Debug, category, message);

    public static void Info(string category, string message) =>
        _sink.Write(LogLevel.Info, category, message);

    public static void Warning(string category, string message) =>
        _sink.Write(LogLevel.Warning, category, message);

    public static void Error(string category, string message) =>
        _sink.Write(LogLevel.Error, category, message);

    /// <summary>
    /// Describes a secret without disclosing it: presence and length only.
    /// </summary>
    /// <remarks>
    /// Length is enough to tell "empty", "truncated", and "looks like a real token" apart, which is
    /// what diagnosis actually needs. The value itself never helps.
    /// </remarks>
    public static string Redact(string? secret) =>
        string.IsNullOrEmpty(secret) ? "<none>" : $"<{secret.Length} chars>";

    private sealed class NullSink : ILogSink
    {
        public static readonly NullSink Instance = new();

        public void Write(LogLevel level, string category, string message)
        {
        }
    }
}
