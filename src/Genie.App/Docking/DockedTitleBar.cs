using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Genie.App.Docking;

/// <summary>
/// Public #299: collapse a docked frame's header while it holds a single window
/// whose <see cref="WindowMenuModel.IsDockedTitleBarHidden"/> is on.
///
/// <para>Dock already shrinks a lone tool's TAB strip to a 1px sliver
/// (<c>ToolTabStrip:singleitem</c>), so the row a single-tab tool frame still
/// spends is its <see cref="ToolChromeControl"/> title bar: the window name plus
/// the pin / float / close buttons, above the content. For the Game group
/// (<see cref="DocumentControl"/>) the equivalent row is its document tab strip.
/// Either one only repeats the window's name when nothing shares the frame, so
/// the user may hide it from the window's right-click menu. The menu keeps
/// Float / Close / Show Title Bar, so nothing the header offered is lost.</para>
///
/// <para>Scope rules: the header comes back the moment a second tab joins the
/// frame (the tabs are then how you switch), and a floating chrome is never
/// touched — a float has its own session-only title-bar toggle
/// (<see cref="GenieHostWindow.SetTitleBarHidden"/>) that writes the same template
/// parts, so this behavior stays out of its way.</para>
///
/// <para>Who writes what: the tool chrome's band already has one owner,
/// <see cref="BannerChrome"/> (the global banner toggle, #302 / #320), so for a
/// ToolChromeControl this class only works out <see cref="WantsHidden"/> and asks
/// BannerChrome to re-apply. The document strip has no other owner; it is
/// collapsed here with a LOCAL IsVisible and restored with ClearValue, so the
/// theme's own visibility rule for it comes back untouched.</para>
///
/// <para>Applied app-wide by style (Themes/BannerChromeStyles.axaml, which the
/// headless tests load too) to both control types.</para>
/// </summary>
public static class DockedTitleBar
{
    /// <summary>Set true (via style) on a ToolChromeControl or DocumentControl.</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TemplatedControl, bool>(
            "Enabled", typeof(DockedTitleBar));

    public static bool GetEnabled(AvaloniaObject o)         => o.GetValue(EnabledProperty);
    public static void SetEnabled(AvaloniaObject o, bool v) => o.SetValue(EnabledProperty, v);

    private static readonly ConditionalWeakTable<TemplatedControl, Binder> _binders = new();

    static DockedTitleBar()
    {
        EnabledProperty.Changed.AddClassHandler<TemplatedControl>((control, e) =>
        {
            if (control is not (ToolChromeControl or DocumentControl)) return;
            if (e.NewValue is true)
            {
                if (_binders.TryGetValue(control, out _)) return;
                var binder = new Binder(control);
                _binders.Add(control, binder);
                binder.Start();   // after registration, so WantsHidden can see it
            }
            else if (_binders.TryGetValue(control, out var binder))
            {
                binder.Detach();
                _binders.Remove(control);
            }
        });
    }

    /// <summary>True when this frame's lone window asks for its header hidden and
    /// the frame is docked. <see cref="BannerChrome"/> folds this into the tool
    /// chrome band it owns; the document strip is written here directly.</summary>
    public static bool WantsHidden(TemplatedControl control) =>
        _binders.TryGetValue(control, out var b) && b.Hidden;

    /// <summary>Per-control state: follows the frame's dock, its visible
    /// dockables, and the lone dockable's menu model, and re-applies whenever
    /// any of them moves.</summary>
    private sealed class Binder
    {
        private readonly TemplatedControl _control;
        private IDock?                   _dock;
        private INotifyCollectionChanged? _dockables;
        private WindowMenuModel?          _menu;
        private Control?                  _header;

        /// <summary>The frame's lone window wants its header hidden, and may have it.</summary>
        public bool Hidden { get; private set; }

        /// <summary>The document strip currently carries our local IsVisible.</summary>
        private bool _collapsed;

        public Binder(TemplatedControl control) => _control = control;

        public void Start()
        {
            _control.DataContextChanged   += OnDataContextChanged;
            _control.TemplateApplied      += OnTemplateApplied;
            _control.PropertyChanged      += OnControlPropertyChanged;
            _control.AttachedToVisualTree += OnAttached;
            Rebind();
        }

        public void Detach()
        {
            _control.DataContextChanged   -= OnDataContextChanged;
            _control.TemplateApplied      -= OnTemplateApplied;
            _control.PropertyChanged      -= OnControlPropertyChanged;
            _control.AttachedToVisualTree -= OnAttached;
            UnhookDock();
            UnhookMenu();
            Hidden = false;
            if (_control is ToolChromeControl chrome) BannerChrome.Refresh(chrome);
            else Restore();
        }

        private void OnDataContextChanged(object? s, EventArgs e) => Rebind();
        private void OnAttached(object? s, VisualTreeAttachmentEventArgs e) => Apply();

        private void OnTemplateApplied(object? s, TemplateAppliedEventArgs e)
        {
            // A re-template builds fresh parts; the old header is gone with its
            // local value, so start clean.
            _header    = null;
            _collapsed = false;
            Apply();
        }

        private void OnControlPropertyChanged(object? s, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToolChromeControl.IsFloatingProperty) Apply();
        }

        private void Rebind()
        {
            UnhookDock();
            _dock = _control.DataContext as IDock;
            if (_dock is INotifyPropertyChanged npc) npc.PropertyChanged += OnDockPropertyChanged;
            HookDockables();
            Apply();
        }

        private void HookDockables()
        {
            if (_dockables is not null) _dockables.CollectionChanged -= OnDockablesChanged;
            _dockables = _dock?.VisibleDockables as INotifyCollectionChanged;
            if (_dockables is not null) _dockables.CollectionChanged += OnDockablesChanged;
        }

        private void UnhookDock()
        {
            if (_dock is INotifyPropertyChanged npc) npc.PropertyChanged -= OnDockPropertyChanged;
            if (_dockables is not null) _dockables.CollectionChanged -= OnDockablesChanged;
            _dock      = null;
            _dockables = null;
        }

        private void OnDockPropertyChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IDock.VisibleDockables)) HookDockables();
            if (e.PropertyName is nameof(IDock.VisibleDockables) or nameof(IDock.ActiveDockable)) Apply();
        }

        private void OnDockablesChanged(object? s, NotifyCollectionChangedEventArgs e) => Apply();

        private void HookMenu(WindowMenuModel? menu)
        {
            if (ReferenceEquals(menu, _menu)) return;
            UnhookMenu();
            _menu = menu;
            if (_menu is not null) _menu.PropertyChanged += OnMenuPropertyChanged;
        }

        private void UnhookMenu()
        {
            if (_menu is not null) _menu.PropertyChanged -= OnMenuPropertyChanged;
            _menu = null;
        }

        private void OnMenuPropertyChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowMenuModel.IsDockedTitleBarHidden)) Apply();
        }

        private void Apply()
        {
            var lone = _dock?.VisibleDockables is { Count: 1 } list ? list[0] : null;
            HookMenu((lone as IWindowMenuHost)?.WindowMenu);

            Hidden = lone is not null
                     && _menu?.IsDockedTitleBarHidden == true
                     && !IsFloating();

            if (_control is ToolChromeControl chrome)
                BannerChrome.Refresh(chrome);   // the band's single writer
            else if (Hidden)
                Collapse();
            else
                Restore();
        }

        /// <summary>Floats keep their own toggle. <c>IsFloating</c> is not set on
        /// every OS (Dock's Linux float path leaves it false until
        /// GenieHostWindow corrects it), so the host window is checked too.</summary>
        private bool IsFloating() =>
            (_control is ToolChromeControl { IsFloating: true })
            || _control.GetVisualRoot() is HostWindow;

        /// <summary>The document group's tab strip. Its separator host binds to
        /// the strip's IsVisible, so it follows on its own.</summary>
        private Control? FindHeader()
        {
            if (_header is not null && _header.GetVisualRoot() is not null) return _header;
            _header = _control.GetVisualDescendants().OfType<DocumentTabStrip>()
                .FirstOrDefault(t => t.Name == "PART_TabStrip" && ReferenceEquals(t.TemplatedParent, _control));
            return _header;
        }

        private void Collapse()
        {
            if (FindHeader() is not { } header) return;
            header.SetValue(Visual.IsVisibleProperty, false);
            _collapsed = true;
        }

        private void Restore()
        {
            if (!_collapsed) return;
            _collapsed = false;
            FindHeader()?.ClearValue(Visual.IsVisibleProperty);
        }
    }
}
