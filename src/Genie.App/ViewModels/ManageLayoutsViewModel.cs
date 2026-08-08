using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Genie.App.Settings;
using Genie.Core.Profiles;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>One row in the Manage Layouts list — a saved layout in a scope.</summary>
public sealed class LayoutEntry
{
    public required string      Name      { get; init; }
    public required LayoutScope Scope     { get; init; }
    public          bool        IsDefault { get; set; }

    /// <summary>Shipped with the app and not shadowed by a user save —
    /// read-only (can be copied and set as default, not renamed/deleted).</summary>
    public bool IsBuiltIn { get; init; }

    public string ScopeText => IsBuiltIn ? "Built-in"
        : Scope == LayoutScope.Global ? "Global" : "Profile";
    public string Display   => $"{(IsDefault ? "★ " : "")}{Name}   ·   {ScopeText}";
}

/// <summary>A destination for a copy — Global or a specific saved profile.</summary>
public sealed class CopyTarget
{
    public required string             Label   { get; init; }
    public          LayoutScope        Scope   { get; init; }
    public          ConnectionProfile? Profile { get; init; }   // null ⇒ Global
}

/// <summary>
/// View-model for the Manage Layouts dialog. Lists the connected profile's
/// layouts plus the global presets, and supports copy (to Global or any saved
/// profile), set-as-default, delete, and rename. All store I/O is funnelled
/// through the factory + callbacks the constructor receives, so this VM owns no
/// path logic of its own.
/// </summary>
public sealed class ManageLayoutsViewModel : ReactiveObject
{
    private readonly LayoutStore                          _global;
    private readonly LayoutStore?                         _profile;
    private readonly ConnectionProfile?                   _connectedProfile;
    private readonly Func<ConnectionProfile, LayoutStore> _profileStoreFactory;
    private readonly DisplaySettings                      _display;
    private readonly Action                               _saveDisplay;
    private readonly Action                               _saveProfiles;

    public ObservableCollection<LayoutEntry> Entries { get; } = new();
    public ObservableCollection<CopyTarget>  Targets { get; } = new();

    [Reactive] public LayoutEntry? Selected       { get; set; }
    [Reactive] public CopyTarget?  SelectedTarget { get; set; }
    [Reactive] public string       Status         { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> CopyCommand       { get; }
    public ReactiveCommand<Unit, Unit> SetDefaultCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand     { get; }

    /// <summary>Designer ctor.</summary>
    public ManageLayoutsViewModel()
        : this(new LayoutStore(Path.GetTempPath()), null, null,
               Array.Empty<ConnectionProfile>(),
               _ => new LayoutStore(Path.GetTempPath()),
               new DisplaySettings(), () => { }, () => { }) { }

    public ManageLayoutsViewModel(
        LayoutStore                          global,
        LayoutStore?                         profile,
        ConnectionProfile?                   connectedProfile,
        IReadOnlyList<ConnectionProfile>     allProfiles,
        Func<ConnectionProfile, LayoutStore> profileStoreFactory,
        DisplaySettings                      display,
        Action                               saveDisplay,
        Action                               saveProfiles)
    {
        _global              = global;
        _profile             = profile;
        _connectedProfile    = connectedProfile;
        _profileStoreFactory = profileStoreFactory;
        _display             = display;
        _saveDisplay         = saveDisplay;
        _saveProfiles        = saveProfiles;

        // Copy destinations: Global + every saved profile.
        Targets.Add(new CopyTarget { Label = "Global", Scope = LayoutScope.Global });
        foreach (var p in allProfiles)
            Targets.Add(new CopyTarget
            {
                Label   = string.IsNullOrWhiteSpace(p.Name) ? p.CharacterName : p.Name,
                Scope   = LayoutScope.Profile,
                Profile = p,
            });
        SelectedTarget = Targets.FirstOrDefault();

        RefreshEntries();

        var hasSelection = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        var canCopy = this.WhenAnyValue(x => x.Selected, x => x.SelectedTarget,
                                        (s, t) => s is not null && t is not null);

        CopyCommand       = ReactiveCommand.Create(Copy,       canCopy);
        SetDefaultCommand = ReactiveCommand.Create(SetDefault, hasSelection);
        DeleteCommand     = ReactiveCommand.Create(Delete,     hasSelection);
    }

    private LayoutStore? StoreFor(LayoutScope scope) =>
        scope == LayoutScope.Global ? _global : _profile;

    public void RefreshEntries()
    {
        var keepName  = Selected?.Name;
        var keepScope = Selected?.Scope;

        Entries.Clear();

        if (_profile is not null)
        {
            var profDefault = _connectedProfile?.DefaultLayoutName ?? "";
            foreach (var name in _profile.List())
                Entries.Add(new LayoutEntry
                {
                    Name      = name,
                    Scope     = LayoutScope.Profile,
                    IsDefault = string.Equals(name, profDefault, StringComparison.OrdinalIgnoreCase),
                });
        }

        foreach (var name in _global.List())
            Entries.Add(new LayoutEntry
            {
                Name      = name,
                Scope     = LayoutScope.Global,
                IsDefault = string.Equals(name, _display.GlobalDefaultLayout, StringComparison.OrdinalIgnoreCase),
                IsBuiltIn = _global.IsBuiltIn(name) && !_global.HasUserCopy(name),
            });

        Selected = Entries.FirstOrDefault(e =>
            e.Name == keepName && e.Scope == keepScope) ?? Entries.FirstOrDefault();
    }

    private void Copy()
    {
        if (Selected is null || SelectedTarget is null) return;
        var source = StoreFor(Selected.Scope);
        var layout = source?.Load(Selected.Name);
        if (layout is null) { Status = $"Could not read '{Selected.Name}'."; return; }

        var targetStore = SelectedTarget.Scope == LayoutScope.Global
            ? _global
            : _profileStoreFactory(SelectedTarget.Profile!);

        // Copying into the same store under the same name is a no-op the user
        // probably didn't intend — flag it instead of silently overwriting.
        if (ReferenceEquals(targetStore, source))
        {
            Status = $"'{Selected.Name}' is already in {SelectedTarget.Label}.";
            return;
        }

        layout.Name = Selected.Name;
        targetStore.Save(layout);
        Status = $"Copied '{Selected.Name}' → {SelectedTarget.Label}.";
        RefreshEntries();
    }

    private void SetDefault()
    {
        if (Selected is null) return;

        if (Selected.Scope == LayoutScope.Profile)
        {
            if (_connectedProfile is null) { Status = "No connected profile to set a default on."; return; }
            _connectedProfile.DefaultLayoutName = Selected.Name;
            _saveProfiles();
            Status = $"'{Selected.Name}' is now this profile's default.";
        }
        else
        {
            _display.GlobalDefaultLayout = Selected.Name;
            _saveDisplay();
            Status = $"'{Selected.Name}' is now the global default.";
        }
        RefreshEntries();
    }

    private void Delete()
    {
        if (Selected is null) return;
        var store = StoreFor(Selected.Scope);
        if (store is null) return;

        // Shipped built-ins are read-only — the file sits in the install dir.
        // (A user save shadowing one lists as a normal Global entry; deleting
        // that reverts to the shipped version, handled by the store.)
        if (Selected.IsBuiltIn)
        {
            Status = $"'{Selected.Name}' ships with Genie and can't be deleted — copy it instead.";
            return;
        }

        // Clear a default that pointed at the deleted layout so connect doesn't
        // try to apply a missing preset.
        if (Selected.Scope == LayoutScope.Profile && _connectedProfile is not null
            && string.Equals(_connectedProfile.DefaultLayoutName, Selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            _connectedProfile.DefaultLayoutName = "";
            _saveProfiles();
        }
        else if (Selected.Scope == LayoutScope.Global
            && string.Equals(_display.GlobalDefaultLayout, Selected.Name, StringComparison.OrdinalIgnoreCase))
        {
            _display.GlobalDefaultLayout = "";
            _saveDisplay();
        }

        store.Delete(Selected.Name);
        Status = $"Deleted '{Selected.Name}'.";
        RefreshEntries();
    }

    /// <summary>Rename the selected layout within its own store. Called from the
    /// dialog code-behind after prompting for the new name.</summary>
    public void Rename(string newName)
    {
        if (Selected is null || string.IsNullOrWhiteSpace(newName)) return;

        // Shipped built-ins keep their name — renaming would just re-save a
        // copy and leave the shipped original listed beside it.
        if (Selected.IsBuiltIn)
        {
            Status = $"'{Selected.Name}' ships with Genie and can't be renamed — copy it instead.";
            return;
        }

        newName = newName.Trim();
        if (string.Equals(newName, Selected.Name, StringComparison.Ordinal)) return;

        var store  = StoreFor(Selected.Scope);
        var layout = store?.Load(Selected.Name);
        if (store is null || layout is null) { Status = $"Could not read '{Selected.Name}'."; return; }

        var wasDefault = Selected.IsDefault;
        var oldName    = Selected.Name;

        layout.Name = newName;
        store.Save(layout);
        store.Delete(oldName);

        if (wasDefault)
        {
            if (Selected.Scope == LayoutScope.Profile && _connectedProfile is not null)
            {
                _connectedProfile.DefaultLayoutName = newName;
                _saveProfiles();
            }
            else if (Selected.Scope == LayoutScope.Global)
            {
                _display.GlobalDefaultLayout = newName;
                _saveDisplay();
            }
        }

        Status = $"Renamed '{oldName}' → '{newName}'.";
        RefreshEntries();
    }

    public bool HasSelection => Selected is not null;
}
