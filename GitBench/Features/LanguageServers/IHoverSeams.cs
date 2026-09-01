using GitBench.Features.Diff;
using GitBench.Lsp.Documents;
using ZGF.Geometry;

namespace GitBench.Features.LanguageServers;

internal interface IHoverSurface
{
    FilePositionHit? HitTestFilePosition(PointF point);
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
