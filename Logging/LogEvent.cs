using System.Collections;
using System.Collections.ObjectModel;

namespace Train.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public sealed class LogEvent
{
    public DateTimeOffset Timestamp { get; }
    public Guid SessionId { get; }
    public long Sequence { get; }
    public LogLevel Level { get; }
    public string Category { get; }
    public string EventId { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, object?> Attributes { get; }
    public LogExceptionData? Exception { get; }

    public LogEvent(
        DateTimeOffset timestamp,
        Guid sessionId,
        long sequence,
        LogLevel level,
        string category,
        string eventId,
        string message,
        IReadOnlyDictionary<string, object?> attributes,
        LogExceptionData? exception)
    {
        Timestamp = timestamp;
        SessionId = sessionId;
        Sequence = sequence;
        Level = level;
        Category = category;
        EventId = eventId;
        Message = message;
        Attributes = attributes;
        Exception = exception;
    }
}

public sealed class LogExceptionData
{
    public const int MaxInnerExceptions = 8;

    public string Type { get; }
    public string Message { get; }
    public string? StackTrace { get; }
    public IReadOnlyList<LogExceptionData> InnerExceptions { get; }

    public LogExceptionData(
        string type,
        string message,
        string? stackTrace,
        IReadOnlyList<LogExceptionData> innerExceptions)
    {
        Type = type;
        Message = message;
        StackTrace = stackTrace;
        InnerExceptions = innerExceptions;
    }

    public static LogExceptionData From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var inners = new List<LogExceptionData>();
        var inner = exception.InnerException;
        for (var i = 0; i < MaxInnerExceptions && inner is not null; i++)
        {
            inners.Add(Capture(inner, Array.Empty<LogExceptionData>()));
            inner = inner.InnerException;
        }

        return Capture(exception, inners);
    }

    private static LogExceptionData Capture(Exception exception, IReadOnlyList<LogExceptionData> inners) =>
        new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace,
            inners);
}

internal static class LogEventKey
{
    public const int CategoryMaxLength = 64;
    public const int EventIdMaxLength = 100;

    public static string Validate(string value, string paramName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length is < 1 || value.Length > maxLength)
            throw new ArgumentException($"Must be 1-{maxLength} characters.", paramName);
        if (value[0] is < 'a' or > 'z')
            throw new ArgumentException("Must start with a lowercase ASCII letter.", paramName);

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                continue;
            throw new ArgumentException("Must contain only lowercase letters, digits, or underscores.", paramName);
        }

        return value;
    }
}

internal static class LogAttributes
{
    internal static readonly IReadOnlyDictionary<string, object?> Empty =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private const int MaxDepth = 16;

    public static IReadOnlyDictionary<string, object?> Normalize(
        IReadOnlyDictionary<string, object?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return Empty;

        var copy = new Dictionary<string, object?>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(attributes));
            copy[key] = NormalizeValue(value, nameof(attributes), 0);
        }

        return new ReadOnlyDictionary<string, object?>(copy);
    }

    private static object? NormalizeValue(object? value, string paramName, int depth)
    {
        switch (value)
        {
            case null:
            case string:
            case bool:
            case sbyte:
            case byte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
                return value;
            case float f:
                if (!float.IsFinite(f))
                    throw new ArgumentException("Floating-point attributes must be finite.", paramName);
                return value;
            case double d:
                if (!double.IsFinite(d))
                    throw new ArgumentException("Floating-point attributes must be finite.", paramName);
                return value;
        }

        if (depth >= MaxDepth)
            throw new ArgumentException("Attribute graphs exceed the supported nesting depth.", paramName);

        if (TryAsReadOnlyDictionary(value, out var dictionary))
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, item) in dictionary)
            {
                ArgumentNullException.ThrowIfNull(key, paramName);
                copy[key] = NormalizeValue(item, paramName, depth + 1);
            }

            return new ReadOnlyDictionary<string, object?>(copy);
        }

        if (TryAsReadOnlyList(value, out var list))
        {
            var copy = new List<object?>(list.Count);
            foreach (var item in list)
                copy.Add(NormalizeValue(item, paramName, depth + 1));
            return new ReadOnlyCollection<object?>(copy);
        }

        throw new ArgumentException(
            $"Attribute values must be null, strings, booleans, integers, finite floats, or read-only lists and string-keyed dictionaries of those types. Rejected: {value.GetType().FullName}.",
            paramName);
    }

    private static bool TryAsReadOnlyList(object value, out IReadOnlyList<object?> list)
    {
        if (value is string)
        {
            list = null!;
            return false;
        }

        if (!ImplementsGeneric(value, typeof(IReadOnlyList<>), out _))
        {
            list = null!;
            return false;
        }

        var copy = new List<object?>();
        foreach (var item in (IEnumerable)value)
            copy.Add(item);
        list = copy;
        return true;
    }

    private static bool TryAsReadOnlyDictionary(object value, out List<KeyValuePair<string, object?>> pairs)
    {
        if (!ImplementsGeneric(value, typeof(IReadOnlyDictionary<,>), out var iface))
        {
            pairs = null!;
            return false;
        }

        var args = iface.GetGenericArguments();
        if (args[0] != typeof(string))
            throw new ArgumentException("Attribute dictionaries must use string keys.", nameof(value));

        pairs = new List<KeyValuePair<string, object?>>();
        foreach (var item in (IEnumerable)value)
        {
            var itemType = item!.GetType();
            var key = (string?)itemType.GetProperty("Key")!.GetValue(item);
            var itemValue = itemType.GetProperty("Value")!.GetValue(item);
            pairs.Add(new KeyValuePair<string, object?>(key!, itemValue));
        }

        return true;
    }

    private static bool ImplementsGeneric(object value, Type generic, out Type iface)
    {
        var type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == generic)
        {
            iface = type;
            return true;
        }

        foreach (var candidate in type.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == generic)
            {
                iface = candidate;
                return true;
            }
        }

        iface = null!;
        return false;
    }
}

internal static class LogLevelText
{
    public static string ToSerialized(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level.")
    };

    public static bool TryParse(string? value, out LogLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
                level = LogLevel.Info;
                return true;
            case "warning":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            default:
                level = default;
                return false;
        }
    }

    public static string FormatBounded(LogLevel level, string? category, string? eventId, string? message)
    {
        var text = $"{ToSerialized(level)} {category}/{eventId}";
        if (string.IsNullOrEmpty(message))
            return text;
        return message.Length <= 200 ? $"{text} {message}" : $"{text} {message[..200]}";
    }
}
