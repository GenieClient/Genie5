using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>The Cross-Zone Connections dialog renders its zone dropdowns and room pickers.</summary>
public sealed class ZoneConnectionsDialogHeadlessTests
{
    [AvaloniaFact]
    public void Zone_dropdowns_list_the_maps_and_the_room_boxes_filter_by_title()
    {
        var dir = Path.Combine(Path.GetTempPath(), "g5zcd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var z = new MapZone { Name = "Map1_Crossing" };
            z.Nodes[50] = new MapNode { Id = 50, Title = "The Strand, Sandy Path" };
            new MapZoneRepository().Save(Path.Combine(dir, "Map1_Crossing.xml"), z);

            var vm = new ZoneConnectionsViewModel(
                new ZoneConnectionsRepository(Path.Combine(dir, "ZoneConnections.xml")), dir);
            vm.AddCommand.Execute().Subscribe();

            var dlg = new ZoneConnectionsDialog { DataContext = vm };
            dlg.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                foreach (var name in new[] { "FromZoneBox", "ToZoneBox" })
                {
                    var zbox = dlg.FindControl<AutoCompleteBox>(name)!;
                    Assert.Equal(new[] { ZoneConnectionsViewModel.AddZoneItem, "Map1_Crossing" },
                                 zbox.ItemsSource!.Cast<string>().ToArray());
                    Assert.True(zbox.ItemFilter!("cross", "Map1_Crossing"));      // type-to-filter
                    Assert.False(zbox.ItemFilter!("cross", "Map150_Fang_Cove"));
                    Assert.True(zbox.ItemFilter!("zzz", ZoneConnectionsViewModel.AddZoneItem)); // always offered
                }

                var box = dlg.FindControl<AutoCompleteBox>("ToRoomBox")!;
                var room = new RoomChoice("50", "50  ·  The Strand, Sandy Path");
                Assert.True(box.ItemFilter!("strand", room));
                Assert.True(box.ItemFilter!("50", room));
                Assert.False(box.ItemFilter!("teller", room));
                Assert.NotNull(dlg.FindControl<Button>("ToRoomDrop"));
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task Picking_a_room_from_the_filtered_list_fills_the_field_the_connection_and_the_caption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "g5zcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var z = new MapZone { Name = "Map150_Fang_Cove" };
            z.Nodes[85]  = new MapNode { Id = 85,  Title = "Fang Cove, Fate's Fortune Lane" };
            z.Nodes[185] = new MapNode { Id = 185, Title = "Fang Cove Outfitting Society, Book Store" };
            new MapZoneRepository().Save(Path.Combine(dir, "Map150_Fang_Cove.xml"), z);

            var vm = new ZoneConnectionsViewModel(
                new ZoneConnectionsRepository(Path.Combine(dir, "ZoneConnections.xml")), dir);
            vm.AddCommand.Execute().Subscribe();
            vm.FromZoneSel = "Map150_Fang_Cove";
            await vm.RefreshRoomsAsync(isFrom: true);

            var dlg = new ZoneConnectionsDialog { DataContext = vm };
            dlg.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var box = dlg.FindControl<AutoCompleteBox>("FromRoomBox")!;
                box.Text = "fate";
                Dispatcher.UIThread.RunJobs();
                box.SelectedItem = vm.FromRooms.First(r => r.Value == "85");
                Dispatcher.UIThread.RunJobs();

                Assert.Equal("85", box.Text);
                Assert.Equal("85", vm.FromRoomText);
                Assert.Equal("85", vm.Selected!.FromRoom);
                Assert.Contains("Fate's Fortune Lane", vm.FromRoomLabel);
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [AvaloniaFact]
    public void The_connection_list_updates_as_the_selected_connection_is_edited()
    {
        var dir = Path.Combine(Path.GetTempPath(), "g5zcl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new ZoneConnectionsViewModel(
                new ZoneConnectionsRepository(Path.Combine(dir, "ZoneConnections.xml")), dir);
            vm.AddCommand.Execute().Subscribe();
            var dlg = new ZoneConnectionsDialog { DataContext = vm };
            dlg.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                vm.Selected!.Verb     = "go exit portal";
                vm.Selected.FromZone  = "Map150_Fang_Cove";
                vm.Selected.ToZone    = "Map1_Crossing";
                Dispatcher.UIThread.RunJobs();

                var list = dlg.GetVisualDescendants().OfType<ListBox>().First();
                var texts = list.GetVisualDescendants().OfType<TextBlock>()
                                .Select(t => t.Text + string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? Array.Empty<string>()))
                                .ToList();
                Assert.Contains(texts, x => x.Contains("go exit portal"));
                Assert.Contains(texts, x => x.Contains("Map150_Fang_Cove") && x.Contains("Map1_Crossing"));
                Assert.DoesNotContain(texts, x => x.Contains("board boat"));
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
