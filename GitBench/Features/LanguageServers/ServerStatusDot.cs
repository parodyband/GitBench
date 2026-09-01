using GitBench.Lsp.Lifecycle;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Widgets;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// A server's state as a colour: green once it can answer, amber while it is on its way there, red
/// when it cannot.
/// </summary>
/// <remarks>
/// Shared by the Files pane's header and the settings dialog's rows so the same server is never
/// drawn two different colours in two places. Stopped is red with the failures: a configured server
/// that is not running answers nothing, and whether that was deliberate is what the words beside it
/// are for.
/// </remarks>
internal sealed record ServerStatusDot : Widget
{
    public const float Size = 8f;

    public required Prop<ServerState> State { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var state = State.ToReadable(ctx);

        return new Box
        {
            Width = Size,
            Height = Size,
            BorderRadius = BorderRadiusStyle.All(Size / 2),
            Background = Prop.Bind(() => Color(state.Value, theme.Styles.Value)),
        };
    }

    private static uint Color(ServerState state, ThemeStyles s) => state switch
    {
        ServerState.Ready => s.Status.Success,
        ServerState.Starting or ServerState.Indexing or ServerState.Restarting => s.Status.Warning,
        _ => s.Status.Danger,
    };
}
