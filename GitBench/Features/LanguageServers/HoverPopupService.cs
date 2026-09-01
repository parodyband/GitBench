using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Widgets;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// The panel a server's answer about a symbol appears in.
/// </summary>
/// <remarks>
/// Separate from <see cref="Controls.ITooltipService"/>, which shows one line of plain text. A hover
/// is markdown — usually a fenced type signature — so it needs the markdown view, and it is wide
/// enough to need a width of its own rather than the label sizing a tooltip gets away with.
/// </remarks>
internal sealed class HoverPopupService : IDisposable
{
    private const int Gap = 8;
    private const int MaxWidth = 620;

    private readonly IPopupWindowFactory _factory;
    private readonly IWindowCoordinates _coordinates;

    private object? _owner;
    private IPopupWindow? _popup;

    public HoverPopupService(IPopupWindowFactory factory, IWindowCoordinates coordinates)
    {
        _factory = factory;
        _coordinates = coordinates;
    }

    public void Show(object owner, HoverText hover, RectF anchorCanvas)
    {
        // Whatever is up goes, whoever owns it: releasing only our own would leak a native handle
        // every time the owner changes.
        Release();

        var rendered = MarkdownFile.Render(hover.Markdown);

        var anchor = _coordinates.ToScreenPoints(anchorCanvas);
        _owner = owner;
        _popup = _factory.Acquire(new PopupRequest
        {
            BuildRoot = ctx => Direction.Wrap(new HoverCard { Render = rendered }).BuildView(ctx),
            Place = (width, height) =>
            {
                var w = Math.Min(width, MaxWidth);
                var preferred = new RectI(anchor.X, anchor.Y + anchor.Height + Gap, w, height);
                var flipped = new RectI(anchor.X, anchor.Y - Gap - height, w, height);
                return (preferred, flipped);
            },
            // The pointer stays the pane's: a hover that swallowed the mouse would dismiss itself
            // the moment it appeared, and the reader is still selecting text underneath it.
            MousePassThrough = true,
        });
    }

    public void Hide(object owner)
    {
        if (ReferenceEquals(_owner, owner)) Release();
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_popup is null) return;
        _factory.Release(_popup);
        _popup = null;
        _owner = null;
    }
}

/// <summary>The hover's contents, on the surface the theme gives popups.</summary>
internal sealed record HoverCard : Widget
{
    public required MarkdownRender Render { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 10, Top = 8, Right = 10, Bottom = 8 },
                Children =
                [
                    new MarkdownDocumentView
                    {
                        Document = Prop.Bind<MarkdownDocument?>(() => Render.Document),
                    },
                ],
            },
        ],
    };
}
