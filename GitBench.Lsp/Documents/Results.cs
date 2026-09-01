namespace GitBench.Lsp.Documents;

/// <summary>
/// Hover content as the protocol allows it: a bare string, a language-tagged code block, a markup
/// block, or a list of those. Lifted off JSON by the reader and no further — normalising the four
/// shapes into one is this layer's job, not the reader's.
/// </summary>
public abstract record HoverPayload
{
    private HoverPayload() { }

    public static readonly HoverPayload Nothing = new Absent();

    public sealed record Absent : HoverPayload;

    /// <summary>The deprecated <c>MarkedString</c> string form.</summary>
    public sealed record PlainText(string Value) : HoverPayload;

    /// <summary>The deprecated <c>MarkedString</c> object form: <c>{ language, value }</c>.</summary>
    public sealed record CodeBlock(string Language, string Value) : HoverPayload;

    /// <summary><c>MarkupContent</c>, the current form.</summary>
    public sealed record Markup(MarkupKind Kind, string Value) : HoverPayload;

    public sealed record Sections(IReadOnlyList<HoverPayload> Parts) : HoverPayload;
}

/// <summary>Hover content in the one form the popup renders: markdown.</summary>
public sealed record HoverText(string Markdown)
{
    private const string SectionBreak = "\n\n---\n\n";

    /// <summary>The hover a server sent, as markdown, or null when it said nothing. Plain text is
    /// fenced rather than passed through: a type signature is full of characters markdown eats.</summary>
    public static HoverText? Of(Hover hover) => hover switch
    {
        Hover.Text(var kind, var value, _) when !string.IsNullOrWhiteSpace(value) =>
            new HoverText(kind == MarkupKind.Markdown ? value : Fence(string.Empty, value)),
        _ => null,
    };

    /// <summary>Normalises any payload shape to markdown, empty when every part is empty. Plain
    /// text is fenced rather than passed through, so a type signature full of angle brackets and
    /// underscores renders as what the server said instead of as markup.</summary>
    public static string ToMarkdown(HoverPayload payload)
    {
        var parts = new List<string>();
        Collect(payload, parts);
        return string.Join(SectionBreak, parts);
    }

    private static void Collect(HoverPayload payload, List<string> into)
    {
        switch (payload)
        {
            case HoverPayload.Absent:
                break;
            case HoverPayload.PlainText plain:
                Add(into, plain.Value);
                break;
            case HoverPayload.CodeBlock code:
                Add(into, Fence(code.Language, code.Value));
                break;
            case HoverPayload.Markup { Kind: MarkupKind.Markdown } markdown:
                Add(into, markdown.Value);
                break;
            case HoverPayload.Markup markup:
                Add(into, Fence(string.Empty, markup.Value));
                break;
            case HoverPayload.Sections sections:
                foreach (var part in sections.Parts) Collect(part, into);
                break;
            default:
                throw new NotSupportedException($"unhandled hover payload {payload.GetType().Name}");
        }
    }

    private static void Add(List<string> into, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0) into.Add(trimmed);
    }

    private static string Fence(string language, string value)
    {
        var body = value.Trim();
        return body.Length == 0 ? string.Empty : $"```{language}\n{body}\n```";
    }
}

/// <summary>A place in a file, as a server reports one.</summary>
public sealed record Location(DocumentUri Uri, LspRange Range);

/// <summary>The richer <c>LocationLink</c> form: the whole declaration, plus the range of just its
/// name when the server bothered to say.</summary>
public sealed record LocationLink(DocumentUri TargetUri, LspRange TargetRange, OptionalRange TargetSelectionRange);

/// <summary>Definition results as the protocol allows them: one location, several, or the link
/// form.</summary>
public abstract record DefinitionPayload
{
    private DefinitionPayload() { }

    public static readonly DefinitionPayload Nothing = new Absent();

    public sealed record Absent : DefinitionPayload;

    public sealed record Single(Location Location) : DefinitionPayload;

    public sealed record Many(IReadOnlyList<Location> Locations) : DefinitionPayload;

    public sealed record Links(IReadOnlyList<LocationLink> Items) : DefinitionPayload;
}

/// <summary>Where a definition lives. The pane handles the two cases with different machinery —
/// one expands the tree and moves the selection, the other opens a detached preview with no
/// selection at all — so they are different types rather than a path and a flag.</summary>
public abstract record DefinitionTarget
{
    private DefinitionTarget() { }

    public sealed record InRepo(string RelativePath, LspPosition Position) : DefinitionTarget;

    public sealed record OutsideRepo(string AbsolutePath, LspPosition Position) : DefinitionTarget;
}

public enum PathComparison
{
    CaseSensitive,
    CaseInsensitive,
}

/// <summary>Decides whether a path the server named is inside the repository the pane is showing.
/// Most jumps in Rust and Go land in a standard library or a package cache, so this is the common
/// case rather than the exceptional one.</summary>
public sealed class RepoBoundary
{
    private readonly string _root;
    private readonly StringComparison _comparison;

    private RepoBoundary(string root, StringComparison comparison)
    {
        _root = root;
        _comparison = comparison;
    }

    public static RepoBoundary At(string rootPath) =>
        At(rootPath, OperatingSystem.IsLinux() ? PathComparison.CaseSensitive : PathComparison.CaseInsensitive);

    public static RepoBoundary At(string rootPath, PathComparison comparison) =>
        new(
            Normalize(rootPath).TrimEnd('/'),
            comparison == PathComparison.CaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    public DefinitionTarget Classify(DocumentUri uri, LspPosition position)
    {
        var path = Normalize(uri.LocalPath);
        var prefix = _root + "/";
        return path.StartsWith(prefix, _comparison)
            ? new DefinitionTarget.InRepo(path[prefix.Length..], position)
            : new DefinitionTarget.OutsideRepo(uri.LocalPath, position);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>Definition targets, normalised out of whichever shape arrived.</summary>
public static class DefinitionTargets
{
    public static IReadOnlyList<DefinitionTarget> From(DefinitionPayload payload, RepoBoundary boundary) =>
        payload switch
        {
            DefinitionPayload.Absent => Array.Empty<DefinitionTarget>(),
            DefinitionPayload.Single one => [Of(one.Location, boundary)],
            DefinitionPayload.Many many => many.Locations.Select(l => Of(l, boundary)).ToArray(),
            DefinitionPayload.Links links => links.Items.Select(l => Of(l, boundary)).ToArray(),
            _ => throw new NotSupportedException($"unhandled definition payload {payload.GetType().Name}"),
        };

    private static DefinitionTarget Of(Location location, RepoBoundary boundary) =>
        boundary.Classify(location.Uri, location.Range.Start);

    // The selection range is the name; the target range is the whole declaration with its doc
    // comment and attributes above it. Landing on the name is what the user asked for.
    private static DefinitionTarget Of(LocationLink link, RepoBoundary boundary) =>
        boundary.Classify(
            link.TargetUri,
            link.TargetSelectionRange is OptionalRange.Present present
                ? present.Range.Start
                : link.TargetRange.Start);
}
