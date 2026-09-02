using GitBench.Features.Diff;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Geometry;

namespace GitBench.Features.LanguageServers;

internal interface IHoverSurface
{
    FilePositionHit? HitTestFilePosition(PointF point);

    /// <summary>What the server said about a line, for the card that shows it. Read from the
    /// surface rather than asked of the servers again, so the message on the card is the one whose
    /// squiggle the reader is pointing at.</summary>
    IReadOnlyList<Diagnostic> DiagnosticsOn(FileLine line);
}

internal interface IHoverSource
{
    bool Handles(string absolutePath);

    Task<HoverText?> HoverAsync(
        string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

internal interface IHoverPresenter
{
    void Show(object owner, HoverText hover, RectF anchorCanvas);

    void Hide(object owner);
}
