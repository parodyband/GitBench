using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// Everything the surfaces need to know about language servers for the repository on screen: what
/// the config file says, what is wrong with it, what is running, and which languages have no server
/// at all.
/// </summary>
/// <remarks>
/// One value rather than several observables, because "still loading" and "broken" are told apart
/// by reading two of these together, and separate slices are read one at a time and disagree while
/// they settle.
/// </remarks>
internal sealed record LanguageServerSnapshot(
    LanguageServerConfig Config,
    IReadOnlyList<ServerStatus> Servers,
    IReadOnlyList<ConfigProblem> Problems,
    IReadOnlyList<StarterServer> Suggestions,
    bool ConfigFileExists)
{
    /// <summary>No repository, or no config file: nothing configured and nothing running.</summary>
    public static readonly LanguageServerSnapshot Nothing =
        new(LanguageServerConfig.Empty, [], [], [], ConfigFileExists: false);

    /// <summary>Whether any configured server claims this file — the cheap check made before a
    /// hover is even considered.</summary>
    public bool Handles(string absolutePath) => Config.ServerFor(absolutePath) is not null;

    /// <summary>Where the server for a file stands. <see cref="ServerState.NotConfigured"/> and
    /// <see cref="ServerState.Stopped"/> are different answers: nothing claims this file, versus
    /// something does and has not been asked yet.</summary>
    public ServerState StateFor(string absolutePath) =>
        Config.ServerFor(absolutePath) is { } entry
            ? StateFor(entry.Language)
            : new ServerState.NotConfigured();

    public ServerState StateFor(LanguageId language)
    {
        foreach (var status in Servers)
            if (status.Language == language)
                return status.State;

        return Config.ServerFor(language) is null
            ? new ServerState.NotConfigured()
            : new ServerState.Stopped();
    }

    /// <summary>The configured servers, each with where it stands, in the order the config names
    /// them.</summary>
    public IReadOnlyList<ConfiguredServer> Configured =>
        Config.Servers.Select(entry => new ConfiguredServer(entry, StateFor(entry.Language))).ToArray();
}

/// <summary>A server the config names, and what it is doing.</summary>
internal sealed record ConfiguredServer(LanguageServerEntry Entry, ServerState State)
{
    /// <summary>Whether stopping it would stop anything.</summary>
    public bool IsRunning => State is ServerState.Starting or ServerState.Indexing or ServerState.Ready;
}
