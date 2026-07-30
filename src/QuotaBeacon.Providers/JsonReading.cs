using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuotaBeacon.Providers;

/// <summary>
/// Case-insensitive, exception-free readers over <see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// Providers return undocumented shapes that change without notice, so every read here answers
/// "is this value present and usable" rather than asserting a schema. Nothing throws on a missing
/// or mistyped field; callers decide what an absent value means.
/// </remarks>
internal static class JsonReading
{
    public static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Exact match first, so a correctly-cased key never loses to a scan.
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static string? String(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Int64(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            // Some payloads quote large integers to survive JavaScript precision limits.
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }

    public static double? Double(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value))
        {
            return null;
        }

        return AsDouble(value);
    }

    public static double? AsDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) && IsFinite(number) => number,
        JsonValueKind.String when double.TryParse(
            value.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) && IsFinite(parsed) => parsed,
        _ => null,
    };

    public static decimal? AsDecimal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when decimal.TryParse(
            value.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// Reads a timestamp expressed as ISO 8601, Unix seconds, or Unix milliseconds.
    /// </summary>
    /// <remarks>
    /// Epoch units are told apart by magnitude: a seconds value large enough to be plausible
    /// milliseconds would place the instant tens of thousands of years out, so the ambiguity is
    /// theoretical rather than practical.
    /// </remarks>
    public static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        if (!TryProperty(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochText))
            {
                return FromEpoch(epochText);
            }

            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch)
            ? FromEpoch(epoch)
            : null;
    }

    /// <summary>Reads the first readable timestamp among several candidate field names.</summary>
    public static DateTimeOffset? Timestamp(JsonElement element, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (Timestamp(element, name) is { } timestamp)
            {
                return timestamp;
            }
        }

        return null;
    }

    private static DateTimeOffset? FromEpoch(long epoch)
    {
        const long MillisecondThreshold = 100_000_000_000L;

        try
        {
            return epoch >= MillisecondThreshold
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>
    /// Describes a payload's structure for diagnostics: key names and JSON types only.
    /// </summary>
    /// <remarks>
    /// This is what makes an unmappable response debuggable without logging account data. Values
    /// are never included, at any depth.
    /// </remarks>
    public static string DescribeShape(JsonElement element, int maxDepth = 2)
    {
        var builder = new StringBuilder();
        Describe(element, builder, depth: 0, maxDepth);
        return builder.ToString();
    }

    private static void Describe(JsonElement element, StringBuilder builder, int depth, int maxDepth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth >= maxDepth)
                {
                    builder.Append("{…}");
                    return;
                }

                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject())
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    first = false;
                    builder.Append(property.Name).Append(':');
                    Describe(property.Value, builder, depth + 1, maxDepth);
                }

                builder.Append('}');
                return;

            case JsonValueKind.Array:
                builder.Append('[');
                if (element.GetArrayLength() > 0)
                {
                    Describe(element[0], builder, depth + 1, maxDepth);
                    builder.Append(element.GetArrayLength() > 1 ? ", …" : string.Empty);
                }

                builder.Append(']');
                return;

            default:
                builder.Append(element.ValueKind.ToString().ToLowerInvariant());
                return;
        }
    }
}
