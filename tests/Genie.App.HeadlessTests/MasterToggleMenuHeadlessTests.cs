using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using ReactiveUI;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Guards the File ▸ Master Toggles family (and every other checkbox MenuItem
/// that has no Command behind it).
///
/// Avalonia's <c>MenuItem.IsChecked</c> is registered with
/// <c>defaultBindingMode: OneWay</c> — unlike <c>ToggleButton.IsChecked</c>,
/// which is TwoWay. A plain <c>IsChecked="{Binding Foo}"</c> on a
/// <c>ToggleType="CheckBox"</c> MenuItem therefore ticks the checkmark on click
/// (MenuItem.Toggle sets the property locally) but NEVER writes back to the
/// view-model — the toggle looks like it worked and does nothing. That was the
/// "disabling Master Triggers does nothing" report.
/// </summary>
public class MasterToggleMenuHeadlessTests
{
    private sealed class Probe : ReactiveObject
    {
        private bool _enabled = true;
        public int Writes;
        public bool Enabled
        {
            get => _enabled;
            set { this.RaiseAndSetIfChanged(ref _enabled, value); Writes++; }
        }
    }

    /// <summary>The Avalonia fact the whole bug rests on — pinned so a future
    /// upgrade that changes it can't quietly invalidate the fix below.</summary>
    [AvaloniaFact]
    public void MenuItem_IsChecked_defaults_to_OneWay()
    {
        Assert.Equal(BindingMode.OneWay,
            MenuItem.IsCheckedProperty.GetMetadata(typeof(MenuItem)).DefaultBindingMode);
    }

    /// <summary>A default-mode binding is dead on click; TwoWay writes back.
    /// Clicking is simulated exactly as MenuItem.Toggle does it.</summary>
    [AvaloniaTheory]
    [InlineData(BindingMode.Default, false)]
    [InlineData(BindingMode.TwoWay, true)]
    public void Checkbox_menu_item_writes_back_only_when_TwoWay(BindingMode mode, bool expectWriteBack)
    {
        var vm   = new Probe();
        var item = new MenuItem { ToggleType = MenuItemToggleType.CheckBox, DataContext = vm };
        item.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(Probe.Enabled)) { Mode = mode });

        Assert.True(item.IsChecked);            // seeded from the view-model

        item.IsChecked = !item.IsChecked;       // == MenuItem.Toggle() on click

        Assert.Equal(expectWriteBack, vm.Enabled == false);
    }

    /// <summary>Source guard over the real menu: every ToggleType="CheckBox"
    /// MenuItem that binds IsChecked must either bind TwoWay or carry a
    /// Command/Click that performs the toggle itself.</summary>
    [Fact]
    public void No_checkbox_menu_item_binds_IsChecked_without_a_write_back_path()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.axaml"));
        var dead = new List<string>();

        foreach (Match m in Regex.Matches(xaml, @"<MenuItem\b((?:[^<>""]|""[^""]*"")*?)/?>", RegexOptions.Singleline))
        {
            var attrs   = m.Groups[1].Value;
            var binding = Regex.Match(attrs, @"IsChecked=""\{Binding([^}]*)\}""", RegexOptions.Singleline);
            if (!binding.Success) continue;
            if (binding.Groups[1].Value.Contains("Mode=TwoWay")) continue;
            if (attrs.Contains("Command=") || attrs.Contains("Click=")) continue;

            var header = Regex.Match(attrs, @"Header=""([^""]*)""");
            dead.Add($"line {xaml[..m.Index].Count(c => c == '\n') + 1}: "
                   + $"{(header.Success ? header.Groups[1].Value : "?")} "
                   + $"→ {binding.Groups[1].Value.Trim()}");
        }

        Assert.True(dead.Count == 0,
            "Checkbox MenuItems whose clicks can never reach the view-model:\n  " + string.Join("\n  ", dead));
    }
}
