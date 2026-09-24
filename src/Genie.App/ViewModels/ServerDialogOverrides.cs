using System;
using System.Linq;
using Genie.Core.Dialogs;

namespace Genie.App.ViewModels;

/// <summary>
/// A hand-built view for one kind of server dialog, used instead of the generic
/// grid renderer (#156 Phase 2). It receives the same engine snapshots the
/// generic renderer would, and sends every click back through the host
/// <see cref="ServerDialogViewModel"/> — so command resolution, the separator
/// escape and the web-link prompt stay in one place.
/// </summary>
public interface IServerDialogBespoke
{
    void Apply(ServerDialogState state);
}

/// <summary>
/// The Phase 2 registry: dialog id → bespoke view model, falling back to the
/// generic renderer. An entry belongs here only when the generic render is
/// demonstrably awkward for a dialog DR really sends — checked against the
/// dialog journal, not imagined. Everything else stays generic.
/// </summary>
public static class ServerDialogOverrides
{
    private static readonly (Func<string, bool> Handles, Func<ServerDialogViewModel, IServerDialogBespoke> Create)[] Registry =
    {
        // injuries-<charnum>: another character's wounds (empath view). Fifteen
        // body-part <image>s the generic grid can only list as sprite names.
        (OtherInjuriesViewModel.Handles, host => new OtherInjuriesViewModel(host)),
    };

    /// <summary>The bespoke view model for <paramref name="host"/>'s dialog, or
    /// null to render it generically.</summary>
    public static IServerDialogBespoke? Create(ServerDialogViewModel host)
    {
        var entry = Registry.FirstOrDefault(r => r.Handles(host.DialogId));
        return entry.Create?.Invoke(host);
    }

    /// <summary>Whether a dialog id has a bespoke view.</summary>
    public static bool HasOverride(string dialogId) => Registry.Any(r => r.Handles(dialogId));
}
