using System;
using System.Reactive;
using Genie.Core.Mapper;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// "Link to…" dialog: the verb that takes you from one room to another, and
/// whether to add the way back. The suggested return verb tracks a compass verb
/// (west → east) until the author types their own.
/// </summary>
public sealed class LinkRoomsViewModel : ReactiveObject
{
    public string FromLabel { get; }
    public string ToLabel   { get; }

    [Reactive] public string Verb       { get; set; } = "";
    [Reactive] public bool   AddReturn  { get; set; } = true;
    [Reactive] public string ReturnVerb { get; set; } = "";

    private bool _returnEdited;

    public ReactiveCommand<Unit, bool> OkCommand     { get; }
    public ReactiveCommand<Unit, bool> CancelCommand { get; }

    public LinkRoomsViewModel(string fromLabel, string toLabel)
    {
        FromLabel = fromLabel;
        ToLabel   = toLabel;

        this.WhenAnyValue(x => x.Verb).Subscribe(v =>
        {
            if (_returnEdited) return;
            ReturnVerb = SuggestReturn(v);
        });
        this.WhenAnyValue(x => x.ReturnVerb).Subscribe(v =>
        {
            if (v != SuggestReturn(Verb)) _returnEdited = true;
        });

        var canOk = this.WhenAnyValue(x => x.Verb, v => !string.IsNullOrWhiteSpace(v));
        OkCommand     = ReactiveCommand.Create(() => true, canOk);
        CancelCommand = ReactiveCommand.Create(() => false);
    }

    /// <summary>The opposite compass direction for a compass verb, else "".</summary>
    public static string SuggestReturn(string? verb)
    {
        var d = DirectionHelper.Parse(verb ?? "");
        return d != Direction.None
               && DirectionHelper.Opposite.TryGetValue(d, out var opp) && opp != Direction.None
            ? opp.ToString().ToLowerInvariant()
            : "";
    }
}
