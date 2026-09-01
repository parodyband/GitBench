using GitBench.Features.Operations;
using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Widgets;
using GitBench.Lsp.Documents;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
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
    /// <summary>The gap between the anchor and the card, and the card's own bounds. Public because
    /// the controller has to know where the card sits to leave it alone while it is up.</summary>
    public const int Gap = 8;

    public const float CardWidth = HoverCard.WidthPx;

    public const float CardMaxHeight = HoverCard.MaxHeightPx;

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
                var preferred = new RectI(anchor.X, anchor.Y + anchor.Height + Gap, width, height);
                var flipped = new RectI(anchor.X, anchor.Y - Gap - height, width, height);
                return (preferred, flipped);
            },
            // The pointer stays the pane's. A card that takes it cannot be reached: the pane reads
            // losing the pointer as the reader leaving, and closing the card from its own input
            // event tears down views the input system is still walking. The controller decides
            // instead, from where the pointer is.
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
    /// <summary>
    /// Fixed, not measured. The markdown blocks stretch to the width they are given rather than
    /// reporting one of their own, so a card left to size itself horizontally collapses to the
    /// width of its copy button. Height still follows the content.
    /// </summary>
    internal const float WidthPx = 520f;

    /// <summary>Past this the hover is reference documentation, not a label for what is under the
    /// pointer, and the file behind it matters more than the rest of the prose.</summary>
    internal const float MaxHeightPx = 420f;


    public required MarkdownRender Render { get; init; }

    /// <summary>
    /// The blocks in a pane that reports their height rather than filling what it is given, so the
    /// card is exactly as tall as the hover until <see cref="MaxHeightPx"/> stops it and the rest
    /// scrolls.
    /// </summary>
    private View Scroller(Context ctx)
    {
        var pane = new VerticalScrollPane { FillParent = false, StretchContent = false };
        pane.Children.Add(new MarkdownWidget { Document = Render.Document }.BuildView(ctx));
        pane.UseController(ctx.Require<InputSystem>(), () => new VerticalScrollPaneWheelController(pane));
        return pane;
    }

    // The blocks, not MarkdownDocumentView: that wraps them in a scroll pane that fills its parent,
    // and a popup has no parent to take a size from — it is sized by what it contains. Bounded so a
    // hover carrying a page of documentation cannot become a window taller than the screen.
    protected override IWidget Build(Context ctx) => new Box
    {
        Width = WidthPx,
        MaxHeight = MaxHeightPx,
        // A hover floats over the code it describes, so it has to read as a separate surface: the
        // tooltip tokens, an outline and a shadow. Painted in the panel's own colour it looked like
        // part of the file.
        Background = Theme.Color(s => s.Tooltip.Background),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Tooltip.Border)),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = 10, Top = 8, Right = 10, Bottom = 8 },
                Children = [new Raw { View = Scroller(ctx) }],
            },
        ],
    };
}

