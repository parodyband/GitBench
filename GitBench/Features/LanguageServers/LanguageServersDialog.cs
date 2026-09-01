using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Lsp.Configuration;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// The language-server settings card: every configured server and what it is doing, the languages
/// in this repository that have none, and whatever the config file could not be read as.
/// </summary>
internal sealed record LanguageServersDialog : Widget
{
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = new LanguageServersViewModel(
            ctx.Require<ILanguageServerStore>(),
            ctx.Require<ILocalizationService>(),
            ctx.Get<IMessageBus>(),
            ctx.Get<IClipboard>());

        return new Dialog
        {
            Title = s.LanguageServersTitle,
            OnClose = OnClose,
            ViewModel = vm,
            Width = DialogFrame.WidthWide,
            CancelLabel = s.CommonClose,
            Action = (s.LanguageServersReload, DialogButtonRole.Primary, vm.Reload),
            Body =
            [
                new Text
                {
                    Value = s.LanguageServersDescription,
                    Wrap = TextWrap.Wrap,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new Text
                {
                    Value = s.LanguageServersConfigPath(vm.ConfigPath),
                    Wrap = TextWrap.Wrap,
                    FontSize = FontSize.Caption,
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new Text
                {
                    Value = s.LanguageServersNoConfig,
                    Wrap = TextWrap.Wrap,
                    Visible = Prop.Bind(() => !vm.HasConfigFile.Value),
                    Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersConfiguredHeading,
                    Empty = s.LanguageServersNoneConfigured,
                    IsEmpty = new Derived<bool>(() => vm.Servers.Value.Count == 0),
                    Child = new Column<ConfiguredServer>
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Items = Prop.Bind(vm.Servers),
                        Template = server => new LanguageServerRow { Model = vm, Server = server },
                    },
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersSuggestionsHeading,
                    IsEmpty = new Derived<bool>(() => vm.Suggestions.Value.Count == 0),
                    Child = new Column
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new Column<StarterServer>
                            {
                                Gap = Spacing.Xs,
                                CrossAxis = CrossAxisAlignment.Stretch,
                                Items = Prop.Bind(vm.Suggestions),
                                Template = suggestion => new LanguageServerSuggestionRow
                                {
                                    Model = vm,
                                    Server = suggestion,
                                },
                            },
                            new Row
                            {
                                MainAxis = MainAxisAlignment.End,
                                Children =
                                [
                                    new ButtonWidget
                                    {
                                        Style = ButtonStyle.Outline(static t => t.Palette.Accent),
                                        Visible = Prop.Bind(vm.CanCreateConfig),
                                        Command = new Command(vm.CreateConfig),
                                        Children =
                                        [
                                            new ButtonLabel { Value = s.LanguageServersCreateConfig },
                                        ],
                                    }.WithController<KbmController>(),
                                ],
                            },
                        ],
                    },
                },
                new LanguageServerSection
                {
                    Heading = s.LanguageServersProblemsHeading,
                    IsEmpty = new Derived<bool>(() => vm.Problems.Value.Count == 0),
                    Child = new Column<ConfigProblem>
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Items = Prop.Bind(vm.Problems),
                        Template = problem => new Text
                        {
                            Value = s.LanguageServersProblem(problem.Subject, problem.Message),
                            Wrap = TextWrap.Wrap,
                            FontSize = FontSize.Caption,
                            Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                        },
                    },
                },
            ],
        };
    }
}

/// <summary>A titled block of the card, hidden entirely when it has nothing to say — unless it has
/// a sentence for the empty case, which "no servers configured" does.</summary>
internal sealed record LanguageServerSection : Widget
{
    public required string Heading { get; init; }
    public required IReadable<bool> IsEmpty { get; init; }
    public required IWidget Child { get; init; }

    /// <summary>Shown in place of the list when there is none. Without one, an empty section is not
    /// drawn at all.</summary>
    public string? Empty { get; init; }

    protected override IWidget Build(Context ctx) => new Column
    {
        Gap = Spacing.Xs,
        CrossAxis = CrossAxisAlignment.Stretch,
        Visible = Prop.Bind(() => Empty is not null || !IsEmpty.Value),
        Children =
        [
            new Text
            {
                Value = Heading,
                Color = Theme.Color(t => t.DialogBody.SectionHeaderText),
            },
            new Text
            {
                Value = Empty,
                Wrap = TextWrap.Wrap,
                FontSize = FontSize.Caption,
                Visible = Prop.Bind(() => Empty is not null && IsEmpty.Value),
                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
            },
            Child,
        ],
    };
}

/// <summary>One configured server: what it runs, what it is doing, and the two things that can be
/// done to it.</summary>
internal sealed record LanguageServerRow : Widget
{
    public required LanguageServersViewModel Model { get; init; }
    public required ConfiguredServer Server { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = Model;
        var server = Server;

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Grow
                {
                    Child = new Column
                    {
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children =
                        [
                            new Text
                            {
                                Value = $"{server.Entry.Language.Value} — {server.Entry.Command}",
                                Color = Theme.Color(t => t.DialogBody.BodyText),
                            },
                            new Text
                            {
                                Value = vm.Describe(server.State),
                                Wrap = TextWrap.Wrap,
                                FontSize = FontSize.Caption,
                                Color = Theme.Color(t => t.DialogBody.RowTextMissing),
                            },
                        ],
                    },
                },
                new ButtonWidget
                {
                    Style = ButtonStyle.Plain,
                    Visible = server.IsRunning,
                    Command = new Command(() => vm.Stop(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersStop }],
                }.WithController<KbmController>(),
                new ButtonWidget
                {
                    Style = ButtonStyle.Plain,
                    Visible = !server.IsRunning,
                    Command = new Command(() => vm.Restart(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersRetry }],
                }.WithController<KbmController>(),
            ],
        };
    }
}

/// <summary>A language this repository is written in that no server answers for, and the entry that
/// would change that.</summary>
internal sealed record LanguageServerSuggestionRow : Widget
{
    public required LanguageServersViewModel Model { get; init; }
    public required StarterServer Server { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var s = ctx.Localization().Strings.Value;
        var vm = Model;
        var server = Server;

        return new Row
        {
            Gap = Spacing.Sm,
            CrossAxis = CrossAxisAlignment.Center,
            Children =
            [
                new Grow
                {
                    Child = new Text
                    {
                        Value = s.LanguageServersSuggestion(server.DisplayName, server.Command),
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(t => t.DialogBody.BodyText),
                    },
                },
                new ButtonWidget
                {
                    Style = ButtonStyle.Plain,
                    Command = new Command(() => vm.CopyEntry(server)),
                    Children = [new ButtonLabel { Value = s.LanguageServersCopySnippet }],
                }.WithController<KbmController>(),
            ],
        };
    }
}
