using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Localization;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
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
/// screen; this is the only thing that tells them apart. It says nothing at all for a file no
/// configured server claims, which is most files in most repositories.
/// </remarks>
internal sealed record LanguageServerStatusChip : Widget
{
    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = ctx.Get<ILanguageServerStore>();
        var bus = ctx.Get<IMessageBus>();
        var loc = ctx.Localization();
        var browser = Model;

        if (store is null) return new Row { Children = [] };

        var state = new Derived<ServerState>(() => browser.Preview.Value is FilePreview.Text text
            ? store.Active.Value.StateFor(text.Path)
            : new ServerState.NotConfigured());

        return new ButtonWidget
        {
            Style = ButtonStyle.BareMuted,
            Visible = Prop.Bind(() => ServerStateText.WorthShowing(state.Value)),
            Command = new Command(() => bus?.Broadcast(new ShowDialogMessage(
                onClose => new LanguageServersDialog { OnClose = onClose }))),
            Children =
            [
                new ButtonLabel
                {
                    Value = Prop.Bind<string?>(() => ServerStateText.Of(state.Value, loc.Strings.Value)),
                },
            ],
        }
            .WithTooltip(Prop.Bind<string?>(() => ServerStateText.Detailed(state.Value, loc.Strings.Value)))
            .WithController<KbmController>();
    }
}
