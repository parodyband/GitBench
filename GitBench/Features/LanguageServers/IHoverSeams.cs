using GitBench.Features.Diff;
using GitBench.Lsp.Documents;
using ZGF.Geometry;

namespace GitBench.Features.LanguageServers;

/// <summary>What the hover probe needs from the thing under the pointer: a pixel turned into a
/// place in a file, or nothing when the pixel is not on the file's text.</summary>
internal interface IHoverSurface
{
    FilePositionHit? HitTestFilePosition(PointF point);
}

/// <summary>What the hover probe needs from the language servers: whether a file has one at all,
/// and what one says about a position.</summary>
/// <remarks>
/// Narrower than <see cref="LanguageServerService"/> on purpose. The probe's own rules — when to
/// ask, when to stop asking, what to do with an answer that arrived too late — are the part that
/// went wrong repeatedly, and they are only testable if asking does not require a subprocess.
/// </remarks>
internal interface IHoverSource
{
    bool Handles(string absolutePath);

    Task<HoverText?> HoverAsync(
        string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken ct);
}

/// <summary>Where an answer is shown, and how it is taken away.</summary>
internal interface IHoverPresenter
{
    void Show(object owner, HoverText hover, RectF anchorCanvas);

    void Hide(object owner);
}
