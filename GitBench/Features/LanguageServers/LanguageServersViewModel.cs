using GitBench.Features.Notifications;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// The settings card's side of language servers: what the config file says, what each server is
/// doing, which languages in this repository have no server at all, and the four things a reader
/// can do about any of it.
/// </summary>
/// <remarks>
/// The config file is hand-written and the app never rewrites it. A starter file is offered only
/// when there is none, and a language added later is offered as an entry to paste — anything else
/// would throw away the comments and ordering the user put there.
/// </remarks>
internal sealed class LanguageServersViewModel : IDialogViewModel
{
    private readonly ILanguageServerStore _store;
    private readonly IMessageBus? _bus;
    private readonly ILocalizationService _loc;
    private readonly IClipboard? _clipboard;

    public LanguageServersViewModel(
        ILanguageServerStore store,
        ILocalizationService loc,
        IMessageBus? bus = null,
        IClipboard? clipboard = null)
    {
        _store = store;
        _loc = loc;
        _bus = bus;
        _clipboard = clipboard;

        Servers = new Derived<IReadOnlyList<ConfiguredServer>>(() => store.Active.Value.Configured);
        Suggestions = new Derived<IReadOnlyList<StarterServer>>(() => store.Active.Value.Suggestions);
        Problems = new Derived<IReadOnlyList<ConfigProblem>>(() => store.Active.Value.Problems);
        HasConfigFile = new Derived<bool>(() => store.Active.Value.ConfigFileExists);
        CanCreateConfig = new Derived<bool>(() =>
            !store.Active.Value.ConfigFileExists && store.Active.Value.Suggestions.Count > 0);
    }

    public event Action? CloseRequested;

    public IReadable<IReadOnlyList<ConfiguredServer>> Servers { get; }

    public IReadable<IReadOnlyList<StarterServer>> Suggestions { get; }

    public IReadable<IReadOnlyList<ConfigProblem>> Problems { get; }

    public IReadable<bool> HasConfigFile { get; }

    /// <summary>Whether writing a starter file would do anything: there is no file, and this
    /// repository is written in something a known server answers for.</summary>
    public IReadable<bool> CanCreateConfig { get; }

    public string ConfigPath => _store.ConfigPath;

    public string Describe(ServerState state) => ServerStateText.Detailed(state, _loc.Strings.Value);

    public void Reload() => _store.ReloadConfig();

    public void Stop(ConfiguredServer server) => _store.StopServer(server.Entry.Language);

    public void Restart(ConfiguredServer server) => _store.RetryServer(server.Entry.Language);

    public void CreateConfig()
    {
        var s = _loc.Strings.Value;
        switch (_store.WriteStarterConfig())
        {
            case StarterConfigOutcome.Written:
                Toast(ToastIntent.Success(s.LanguageServersConfigWritten));
                break;
            case StarterConfigOutcome.AlreadyExists:
                Toast(ToastIntent.Info(s.LanguageServersConfigExists));
                break;
        }
    }

    /// <summary>Puts one server's config entry on the clipboard, for a config file that already
    /// exists and is the user's to edit.</summary>
    public void CopyEntry(StarterServer server)
    {
        if (_clipboard is null) return;
        _clipboard.SetText(StarterServers.EntryText(server));
        Toast(ToastIntent.Success(_loc.Strings.Value.LanguageServersSnippetCopied));
    }

    public void Dispose() => CloseRequested = null;

    private void Toast(ToastIntent intent) => _bus?.Broadcast(new ShowToastMessage(intent));
}
