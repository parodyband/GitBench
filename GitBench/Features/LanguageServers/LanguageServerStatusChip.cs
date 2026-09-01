using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Localization;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// What the language server for the file on screen is doing, beside the file's name.
/// </summary>
/// <remarks>
/// Not optional decoration. A server takes half a minute to become useful on a cold Rust project,
/// and for that half minute a working server and a broken one produce exactly the same empty
/// screen; this is the only thing that tells them apart. A dot rather than a word because it sits
/// in a header whose width belongs to the file's name, and because a reader wants to know whether
/// anything is wrong far more often than they want to read which state it is in — the tooltip says
/// that, and clicking opens the settings. Nothing at all is drawn for a file no configured server
/// claims, which is most files in most repositories.
/// </remarks>
internal sealed record LanguageServerStatusChip : Widget
{
    private const float DotSize = 8f;
    private const int EdgeGap = 8;

    protected override IWidget Build(Context ctx)
    {
        var store = ctx.Get<ILanguageServerStore>();
        var bus = ctx.Get<IMessageBus>();
        var loc = ctx.Localization();
        var theme = ctx.Theme();
        var browser = Model;

        if (store is null) return new Row { Children = [] };

        var state = new Derived<ServerState>(() => browser.Preview.Value is FilePreview.Text text
            ? store.Active.Value.StateFor(text.Path)
            : new ServerState.NotConfigured());

        var dot = new ButtonWidget
        {
            Style = ButtonStyle.BareMuted,
            Visible = Prop.Bind(() => ServerStateText.WorthShowing(state.Value)),
            Command = new Command(() => bus?.Broadcast(new ShowDialogMessage(
                onClose => new LanguageServersDialog { OnClose = onClose }))),
            Children =
            [
                new Box
                {
                    Width = DotSize,
                    Height = DotSize,
                    BorderRadius = BorderRadiusStyle.All(DotSize / 2),
                    Background = Prop.Bind(() => Color(state.Value, theme.Styles.Value)),
                },
            ],
        }
            // The state first, because that is the thing that changes; what the dot is, second,
            // because a colour on its own names nothing.
            .WithTooltip(Prop.Bind<string?>(() =>
                $"{ServerStateText.Detailed(state.Value, loc.Strings.Value)} — " +
                loc.Strings.Value.LanguageServersChipTooltip))
            .WithController<KbmController>();

        // The header runs to the window's edge, and a status light pinned against it reads as
        // clipped rather than placed.
        return new Padding
        {
            Amount = new PaddingStyle { Right = EdgeGap },
            Children = [dot],
        };
    }

    public required FileBrowserViewModel Model { get; init; }

    /// <summary>
    /// Green once it can answer, amber while it is on its way there, red when it cannot. Stopped is
    /// red with the rest: a configured server that is not running answers nothing, and whether that
    /// is a crash or an idle shutdown is what the tooltip is for.
    /// </summary>
    private static uint Color(ServerState state, ThemeStyles s) => state switch
    {
        ServerState.Ready => s.Status.Success,
        ServerState.Starting or ServerState.Indexing or ServerState.Restarting => s.Status.Warning,
        _ => s.Status.Danger,
    };
}
