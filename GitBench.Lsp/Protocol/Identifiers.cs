using System.Text.Json;

namespace GitBench.Lsp;

/// <summary>
/// A JSON-RPC request id. The protocol allows a number or a string and requires the exact form to be
/// echoed back, so this is a sum rather than a long: a server that asks with "cfg-1" must not be
/// answered with 0.
/// </summary>
public abstract record RequestId
{
    private RequestId() { }

    public sealed record Number(long Value) : RequestId
    {
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed record Text(string Value) : RequestId
    {
        public override string ToString() => Value;
    }

    public static RequestId Read(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var n) => new Number(n),
        JsonValueKind.String => new Text(element.GetString()!),
        _ => throw new LspParseException($"request id must be a number or a string, was {element.ValueKind}"),
    };

    public void Write(Utf8JsonWriter writer)
    {
        switch (this)
        {
            case Number n: writer.WriteNumberValue(n.Value); break;
            case Text t: writer.WriteStringValue(t.Value); break;
            default: throw new NotSupportedException($"unhandled request id {GetType().Name}");
        }
    }
}

/// <summary>A JSON-RPC method name. Distinct from every other string on the wire.</summary>
public readonly record struct LspMethod(string Name)
{
    public static readonly LspMethod Initialize = new("initialize");
    public static readonly LspMethod Hover = new("textDocument/hover");
    public static readonly LspMethod Definition = new("textDocument/definition");
    public static readonly LspMethod DidOpen = new("textDocument/didOpen");
    public static readonly LspMethod DidClose = new("textDocument/didClose");
    public static readonly LspMethod PublishDiagnostics = new("textDocument/publishDiagnostics");
    public static readonly LspMethod LogMessage = new("window/logMessage");
    public static readonly LspMethod CancelRequest = new("$/cancelRequest");
    public static readonly LspMethod Configuration = new("workspace/configuration");

    public override string ToString() => Name;
}

/// <summary>
/// A document identifier as the protocol carries it: a URI string, never a filesystem path. Kept
/// distinct so a raw path can't be handed to a server that would silently fail to match it.
/// </summary>
public readonly record struct DocumentUri
{
    private DocumentUri(string value) => Value = value;

    public string Value { get; }

    public static DocumentUri Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new LspParseException("document uri was empty");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new LspParseException($"document uri is not absolute: {value}");
        return new DocumentUri(uri.AbsoluteUri);
    }

    public static DocumentUri OfFile(string absolutePath)
    {
        if (!Path.IsPathRooted(absolutePath))
            throw new ArgumentException($"document uri needs an absolute path, got {absolutePath}", nameof(absolutePath));
        return new DocumentUri(new Uri(absolutePath).AbsoluteUri);
    }

    /// <summary>The decoded local path, for a file: uri. Empty for any other scheme.</summary>
    public string LocalPath
    {
        get
        {
            var uri = new Uri(Value);
            return uri.IsFile ? uri.LocalPath : string.Empty;
        }
    }

    public override string ToString() => Value;
}

/// <summary>The version of a document as the client and server agree on it.</summary>
public readonly record struct DocumentVersion(int Value)
{
    public DocumentVersion Next() => new(Value + 1);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A language key from the config file, normalized so "Rust" and "rust" are the same server.
/// Only <see cref="TryParse"/> builds one, so a language id in the program has already been
/// checked for emptiness and case.
/// </summary>
public readonly record struct LanguageId
{
    readonly string? _value;

    LanguageId(string value) => _value = value;

    public string Value => _value ?? string.Empty;

    /// <summary>The id for a language the program already knows it has. Throws rather than
    /// returning an empty id, because a caller here is not reading untrusted input —
    /// <see cref="TryParse"/> is the form for that.</summary>
    public static LanguageId Of(string value) =>
        TryParse(value, out var language)
            ? language
            : throw new ArgumentException($"not a language id: '{value}'", nameof(value));

    public static bool TryParse(string? raw, out LanguageId language)
    {
        language = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        language = new LanguageId(raw.Trim().ToLowerInvariant());
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>A zero-based line in a file. Not a screen row, and not a version.</summary>
public readonly record struct LspLine : IComparable<LspLine>
{
    public LspLine(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "a line number cannot be negative");
        Value = value;
    }

    public int Value { get; }

    /// <summary>
    /// The one place the protocol's zero-based lines meet the one-based lines the rest of the app
    /// counts in (gutters, <c>FileLine</c>, anything a person reads). Every crossing goes through
    /// here and back through <see cref="ToOneBased"/>; a second conversion written somewhere else
    /// is how a jump ends up one line off in a way that still looks like it worked.
    /// </summary>
    public static LspLine FromOneBased(int line) =>
        line >= 1
            ? new LspLine(line - 1)
            : throw new ArgumentOutOfRangeException(nameof(line), line, "a one-based line starts at 1");

    public int ToOneBased() => Value + 1;

    public int CompareTo(LspLine other) => Value.CompareTo(other.Value);

    public static bool operator <(LspLine a, LspLine b) => a.Value < b.Value;
    public static bool operator >(LspLine a, LspLine b) => a.Value > b.Value;
    public static bool operator <=(LspLine a, LspLine b) => a.Value <= b.Value;
    public static bool operator >=(LspLine a, LspLine b) => a.Value >= b.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A zero-based UTF-16 code unit offset within a line, which is what LSP positions count.</summary>
public readonly record struct LspCharacter
{
    public LspCharacter(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "a character offset cannot be negative");
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LspPosition(LspLine Line, LspCharacter Character)
{
    public static LspPosition At(int line, int character) => new(new LspLine(line), new LspCharacter(character));
}

public readonly record struct LspRange(LspPosition Start, LspPosition End)
{
    public static LspRange Empty(LspPosition at) => new(at, at);
}
