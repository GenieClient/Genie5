using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Genie.App.ViewModels;

namespace Genie.App.Views;

/// <summary>
/// One Configuration tab's panel (public #402). The tab holds an empty host;
/// the panel is built the first time it is needed and filled for the profile
/// being edited. Every fill goes through <see cref="Ensure"/>, which builds the
/// panel if it doesn't exist yet, so no caller has to know whether it does.
/// </summary>
internal abstract class ConfigPanelSlot
{
    private int _filledFor = -1;

    protected ConfigPanelSlot(ContentControl host) => Host = host;

    public ContentControl Host  { get; }
    public Control?       Panel { get; private set; }
    public abstract Type  PanelType { get; }

    /// <summary>True when the panel exists and was filled for fill pass <paramref name="pass"/>.</summary>
    public bool IsCurrent(int pass) => Panel is not null && _filledFor == pass;

    /// <summary>True when the tab is on screen: its tab and every tab around it is selected.</summary>
    public bool IsShowing =>
        Host.GetLogicalAncestors().OfType<TabItem>().All(t => t.IsSelected);

    /// <summary>Build the panel if needed, then fill it unless it was already
    /// filled for <paramref name="pass"/>.</summary>
    public void Ensure(ConfigurationViewModel vm, int pass)
    {
        var panel = Build();
        if (_filledFor == pass) return;
        _filledFor = pass;
        Fill(panel, vm);
    }

    /// <summary>Build the panel and put it in its tab, if that hasn't happened yet.</summary>
    public Control Build()
    {
        if (Panel is null)
        {
            Panel = Create();
            Host.Content = Panel;
        }
        return Panel;
    }

    protected abstract Control Create();
    protected abstract void Fill(Control panel, ConfigurationViewModel vm);
}

internal sealed class ConfigPanelSlot<T> : ConfigPanelSlot where T : Control, new()
{
    private readonly Action<T, ConfigurationViewModel> _fill;

    public ConfigPanelSlot(ContentControl host, Action<T, ConfigurationViewModel> fill) : base(host) => _fill = fill;

    public override Type PanelType => typeof(T);
    protected override Control Create() => new T();
    protected override void Fill(Control panel, ConfigurationViewModel vm) => _fill((T)panel, vm);
}
