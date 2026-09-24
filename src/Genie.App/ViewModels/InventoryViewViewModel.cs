using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Genie.Core;
using Genie.Core.Extensions.Builtin.InventoryView;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the first-class Inventory View window: a searchable TreeView over the
/// cross-character item catalog owned by the Core
/// <see cref="InventoryViewExtension"/> (<c>/iv scan</c>). Unlike the text
/// trackers this panel binds typed data, not a pushed string — the VM
/// snapshots the extension's catalog on <c>CatalogChanged</c> and rebuilds the
/// node tree (filtered when a search is active). All extension calls are safe
/// from the UI thread; catalog events arrive on the parser thread and are
/// marshalled here.
/// </summary>
public class InventoryViewViewModel : ReactiveObject
{
    private InventoryViewExtension? _ext;
    private List<CharacterData> _snapshot = new();

    public ObservableCollection<InventoryNode> Roots { get; } = new();

    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public InventoryNode? SelectedNode { get; set; }
    /// <summary>"Found: N" while a search is active; total catalog size otherwise.</summary>
    [Reactive] public string CountText { get; private set; } = "";
    [Reactive] public string StatusText { get; private set; } =
        "Nothing scanned yet — connect and use Scan (or /iv scan).";
    /// <summary>Two-step Remove arm: first click arms, second removes.</summary>
    [Reactive] public string RemoveHeader { get; private set; } = "Remove";
    private bool _removeArmed;

    /// <summary>A scan is running: Scan and Reload are disabled, Cancel is
    /// enabled. Mirrors the extension's ScanInProgress via ScanStateChanged.</summary>
    [Reactive] public bool IsScanning { get; private set; }

    public ReactiveCommand<Unit, Unit> ScanCommand        { get; }
    public ReactiveCommand<Unit, Unit> CancelScanCommand  { get; }
    public ReactiveCommand<Unit, Unit> ReloadCommand      { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand      { get; }
    public ReactiveCommand<Unit, Unit> WikiCommand        { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand      { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCommand   { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }
    public ReactiveCommand<Unit, Unit> FindInShopsCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseShopsCommand  { get; }
    public ReactiveCommand<string, Unit> SortCommand      { get; }

    // ── Column sorting (item levels only; characters/sources stay put) ────────
    private enum IvSortColumn { None, Name, Weight, Size }
    private IvSortColumn _sortColumn = IvSortColumn.None;
    private bool _sortDescending;
    [Reactive] public string ItemHeader { get; private set; } = "Item";
    [Reactive] public string WtHeader   { get; private set; } = "Wt";
    [Reactive] public string SizeHeader { get; private set; } = "Size";

    // ── Player-shop search results (DR Service Plaza) ─────────────────────────
    public ObservableCollection<ShopListingRow> ShopResults { get; } = new();
    [Reactive] public string ShopStatus { get; private set; } = "";
    [Reactive] public bool ShopPanelVisible { get; private set; }

    /// <summary>Raised when the user (or a scan completion) asks for the window —
    /// MainWindowViewModel shows the dock tool.</summary>
    public event Action? OpenRequested;

    public InventoryViewViewModel()
    {
        // Scan and Reload are meaningless (Reload actively harmful) mid-scan, and
        // an ungated Scan button was how a wedged scan turned into a wall of
        // "a scan is already in progress." in the game window.
        var idle     = this.WhenAnyValue(vm => vm.IsScanning).Select(b => !b);
        var scanning = this.WhenAnyValue(vm => vm.IsScanning);

        ScanCommand       = ReactiveCommand.Create(() => { _ext?.StartScan(); },  idle);
        CancelScanCommand = ReactiveCommand.Create(() => { _ext?.CancelScan(); }, scanning);
        ReloadCommand     = ReactiveCommand.Create(() => { _ext?.ReloadFromDisk(); }, idle);
        ExportCommand     = ReactiveCommand.Create(() => { _ext?.ExportCsv(); });

        var hasItemSelection = this.WhenAnyValue(vm => vm.SelectedNode)
            .Select(n => n is { Kind: InventoryNodeKind.Item });
        WikiCommand = ReactiveCommand.Create(
            () => { if (SelectedNode is { Kind: InventoryNodeKind.Item } n) _ext?.WikiLookup(n.Tap); },
            hasItemSelection);

        var hasSelection = this.WhenAnyValue(vm => vm.SelectedNode).Select(n => n is not null);
        RemoveCommand = ReactiveCommand.Create(RemoveSelectedCharacter, hasSelection);

        ExpandAllCommand   = ReactiveCommand.Create(() => SetExpansion(true));
        CollapseAllCommand = ReactiveCommand.Create(() => SetExpansion(false));

        SortCommand = ReactiveCommand.Create<string>(SortBy);

        FindInShopsCommand = ReactiveCommand.CreateFromTask(FindInShopsAsync, hasItemSelection);
        CloseShopsCommand  = ReactiveCommand.Create(() =>
        {
            ShopPanelVisible = false;
            ShopResults.Clear();
            ShopStatus = "";
        });

        // Live filter: rebuild the tree as the search text settles.
        this.WhenAnyValue(vm => vm.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => Rebuild());

        // Selection change disarms a pending Remove.
        this.WhenAnyValue(vm => vm.SelectedNode).Subscribe(_ => DisarmRemove());
    }

    public void Attach(GenieCore core)
    {
        _ext = core.Scripts.Extensions.Extensions
            .OfType<InventoryViewExtension>().FirstOrDefault();
        if (_ext is null) return;

        _ext.CatalogChanged += () => Dispatcher.UIThread.Post(RefreshSnapshot);
        _ext.OpenRequested  += term => Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(term)) SearchText = term;
            OpenRequested?.Invoke();
        });
        _ext.ItemInfoUpdated += () => Dispatcher.UIThread.Post(ApplyItemInfo);
        _ext.ScanStateChanged += () => Dispatcher.UIThread.Post(
            () => IsScanning = _ext?.ScanInProgress ?? false);
        IsScanning = _ext.ScanInProgress;
        RefreshSnapshot();
    }

    /// <summary>Fill weight/size on any item node that lacks them, from the
    /// extension's wiki cache. Runs after every resolved batch; in-place so
    /// expansion state survives. Also keeps the status strip's progress line
    /// honest while lookups are in flight.</summary>
    private void ApplyItemInfo()
    {
        if (_ext is null) return;
        foreach (var root in Roots) ApplyItemInfo(root);
        UpdateResolveProgress();
        ApplySort();   // newly-arrived weights slot into an active sort
    }

    // ── Sorting ────────────────────────────────────────────────────────────────
    /// <summary>Header click: first click sorts (name ascending; weight/size
    /// descending — heaviest/biggest first is the useful direction), a second
    /// click on the same column flips it.</summary>
    private void SortBy(string column)
    {
        var col = column switch
        {
            "name"   => IvSortColumn.Name,
            "weight" => IvSortColumn.Weight,
            "size"   => IvSortColumn.Size,
            _        => IvSortColumn.None,
        };
        if (col == IvSortColumn.None) return;

        if (_sortColumn == col) _sortDescending = !_sortDescending;
        else { _sortColumn = col; _sortDescending = col != IvSortColumn.Name; }

        UpdateSortHeaders();
        ApplySort();
    }

    private void UpdateSortHeaders()
    {
        var arrow = _sortDescending ? " ▼" : " ▲";
        ItemHeader = "Item" + (_sortColumn == IvSortColumn.Name   ? arrow : "");
        WtHeader   = "Wt"   + (_sortColumn == IvSortColumn.Weight ? arrow : "");
        SizeHeader = "Size" + (_sortColumn == IvSortColumn.Size   ? arrow : "");
    }

    /// <summary>Re-order every item level in place (characters and sources keep
    /// their positions; expansion state lives on the nodes so it survives).</summary>
    private void ApplySort()
    {
        if (_sortColumn == IvSortColumn.None) return;
        var selected = SelectedNode;
        foreach (var character in Roots)
            foreach (var source in character.Children)
                SortSubtree(source);
        SelectedNode = selected;
    }

    private void SortSubtree(InventoryNode parent)
    {
        if (parent.Children.Count > 1)
        {
            // Unknown values (no wiki data) always sink to the bottom; ties
            // break alphabetically so the order is stable and scannable.
            IOrderedEnumerable<InventoryNode> ordered = _sortColumn switch
            {
                IvSortColumn.Weight => Dir(
                    parent.Children.OrderBy(n => n.SortWeight is null ? 1 : 0),
                    n => n.SortWeight ?? 0),
                IvSortColumn.Size => Dir(
                    parent.Children.OrderBy(n => n.SortVolume is null ? 1 : 0),
                    n => n.SortVolume ?? 0),
                _ => _sortDescending
                    ? parent.Children.OrderByDescending(n => n.Text, StringComparer.OrdinalIgnoreCase)
                    : parent.Children.OrderBy(n => n.Text, StringComparer.OrdinalIgnoreCase),
            };
            var sorted = ordered.ThenBy(n => n.Text, StringComparer.OrdinalIgnoreCase).ToList();

            if (!sorted.SequenceEqual(parent.Children))
            {
                parent.Children.Clear();
                foreach (var n in sorted) parent.Children.Add(n);
            }
        }
        foreach (var child in parent.Children)
            SortSubtree(child);

        IOrderedEnumerable<InventoryNode> Dir(
            IOrderedEnumerable<InventoryNode> src, Func<InventoryNode, double> key) =>
            _sortDescending ? src.ThenByDescending(key) : src.ThenBy(key);
    }

    private void UpdateResolveProgress()
    {
        if (_ext is null) return;
        var taps = new List<string>();
        foreach (var c in _snapshot) CollectTaps(c.Items, taps);
        int remaining = _ext.ItemInfo.Unresolved(taps).Count;
        if (remaining > 0)
            StatusText = $"Looking up item weight/size on Elanthipedia… {remaining} item(s) remaining.";
        else if (StatusText.StartsWith("Looking up item weight/size", StringComparison.Ordinal))
            StatusText = "";
    }

    private void ApplyItemInfo(InventoryNode node)
    {
        if (node.Kind == InventoryNodeKind.Item &&
            node.WeightText.Length == 0 && node.SizeText.Length == 0 &&
            _ext!.ItemInfo.Get(node.Tap) is { } info)
        {
            SetItemInfo(node, info);
        }
        foreach (var child in node.Children) ApplyItemInfo(child);
    }

    private static void SetItemInfo(InventoryNode node, WikiItemInfo info)
    {
        node.WeightText = info.WeightText;
        node.SizeText   = info.SizeText;
        node.SortWeight = info.Weight;
        node.SortVolume = info is { Length: { } l, Width: { } w, Height: { } h }
            ? (double)l * w * h : null;
    }

    private async Task FindInShopsAsync()
    {
        if (_ext is null || SelectedNode is not { Kind: InventoryNodeKind.Item } node) return;
        var term = InventoryViewExtension.CleanTap(node.Tap);

        ShopPanelVisible = true;
        ShopResults.Clear();
        ShopStatus = $"Searching player shops for \"{term}\"…";

        var results = await _ext.ShopSearchAsync(term);
        if (results is null)
        {
            ShopStatus = "Shop data unavailable — offline and no cached copy yet (drservice.info).";
            return;
        }
        foreach (var l in results)
            ShopResults.Add(new ShopListingRow(l));

        var age = _ext.Plaza.DataTimestampUtc is { } ts
            ? $" — Plaza data {ts:yyyy-MM-dd HH:mm} UTC"
            : "";
        ShopStatus = results.Count == 0
            ? $"No player-shop listings match \"{term}\"{age}."
            : $"{results.Count} listing(s) for \"{term}\"{age} (drservice.info).";
    }

    private void RefreshSnapshot()
    {
        _snapshot = _ext?.SnapshotCatalog() ?? new List<CharacterData>();
        Rebuild();

        // Kick background weight/size resolution for anything not yet cached
        // (batched + throttled in the extension; no-op when all cached).
        if (_ext is not null)
        {
            var taps = new List<string>();
            foreach (var c in _snapshot) CollectTaps(c.Items, taps);
            _ext.RequestItemInfo(taps);
            UpdateResolveProgress();
        }
    }

    private static void CollectTaps(List<ItemData> items, List<string> taps)
    {
        foreach (var i in items)
        {
            taps.Add(i.Tap);
            CollectTaps(i.Items, taps);
        }
    }

    // ── Tree building / filtering ───────────────────────────────────────────────
    private void Rebuild()
    {
        var term = SearchText.Trim();
        bool filtering = term.Length > 0;
        int found = 0, total = 0;

        Roots.Clear();
        foreach (var character in _snapshot.Select(c => c.Name).Distinct()
                                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var sources   = _snapshot.Where(c => c.Name == character).ToList();
            int charTotal = sources.Sum(s => Count(s.Items));
            total += charTotal;

            var charNode = new InventoryNode(
                $"{character}  (T: {charTotal})", "", InventoryNodeKind.Character, character);

            foreach (var src in sources)
            {
                var srcNode = new InventoryNode(
                    $"{src.Source}  ({Count(src.Items)})", "", InventoryNodeKind.Source, character);
                foreach (var item in src.Items)
                {
                    var child = BuildNode(item, character, term, ref found);
                    if (child != null) srcNode.Children.Add(child);
                }
                if (!filtering || srcNode.Children.Count > 0)
                {
                    srcNode.IsExpanded = filtering;
                    charNode.Children.Add(srcNode);
                }
            }

            if (!filtering || charNode.Children.Count > 0)
            {
                charNode.IsExpanded = true;   // characters always start open (G4 did too)
                Roots.Add(charNode);
            }
        }

        CountText  = filtering ? $"Found: {found}" : (total > 0 ? $"Total: {total}" : "");
        StatusText = _snapshot.Count == 0
            ? "Nothing scanned yet — connect and use Scan (or /iv scan)."
            : filtering && found == 0 ? $"No items match \"{term}\"." : "";
        ApplySort();   // a rebuild resets to catalog order; restore the active sort
    }

    /// <summary>Depth-first copy of an item subtree into display nodes. While
    /// filtering, keeps a node only if it matches or an ancestor of a match —
    /// matching branches come back pre-expanded.</summary>
    private InventoryNode? BuildNode(ItemData item, string character, string term, ref int found)
    {
        bool selfMatch = term.Length > 0 &&
            item.Tap.Contains(term, StringComparison.OrdinalIgnoreCase);

        var node = new InventoryNode(item.Tap, item.Tap, InventoryNodeKind.Item, character)
        {
            IsMatch = selfMatch,
        };
        if (_ext?.ItemInfo.Get(item.Tap) is { } info)
            SetItemInfo(node, info);
        foreach (var child in item.Items)
        {
            var childNode = BuildNode(child, character, term, ref found);
            if (childNode != null) node.Children.Add(childNode);
        }

        if (term.Length == 0)
            return node;                       // unfiltered: keep everything
        if (selfMatch) found++;
        if (!selfMatch && node.Children.Count == 0)
            return null;                       // filtered out
        node.IsExpanded = node.Children.Count > 0;
        return node;
    }

    private static int Count(List<ItemData> items)
    {
        int n = items.Count;
        foreach (var i in items) n += Count(i.Items);
        return n;
    }

    private void SetExpansion(bool expanded)
    {
        foreach (var root in Roots) SetExpansion(root, expanded);
    }

    private static void SetExpansion(InventoryNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpansion(child, expanded);
    }

    // ── Remove (two-step arm, like the Script Manager delete) ─────────────────
    private void RemoveSelectedCharacter()
    {
        if (SelectedNode is null || _ext is null) return;
        var character = SelectedNode.CharacterName;
        if (!_removeArmed)
        {
            _removeArmed = true;
            RemoveHeader = $"Remove {character}?";
            return;
        }
        DisarmRemove();
        _ext.RemoveCharacter(character);       // fires CatalogChanged → rebuild
    }

    private void DisarmRemove()
    {
        _removeArmed = false;
        RemoveHeader = "Remove";
    }
}

public enum InventoryNodeKind { Character, Source, Item }

/// <summary>One row in the Inventory View tree.</summary>
public sealed class InventoryNode : ReactiveObject
{
    public InventoryNode(string text, string tap, InventoryNodeKind kind, string characterName)
    {
        Text          = text;
        Tap           = tap;
        Kind          = kind;
        CharacterName = characterName;
    }

    public string Text { get; }
    /// <summary>Raw item tap (empty on character/source rows) — feeds Wiki Lookup.</summary>
    public string Tap  { get; }
    public InventoryNodeKind Kind { get; }
    public string CharacterName   { get; }
    public bool IsHeader => Kind != InventoryNodeKind.Item;
    public ObservableCollection<InventoryNode> Children { get; } = new();

    [Reactive] public bool IsExpanded { get; set; }
    public bool IsMatch { get; init; }

    /// <summary>Elanthipedia weight (stones) / size (L×W×H) — filled in the
    /// background as wiki batches resolve; empty when the wiki doesn't know.</summary>
    [Reactive] public string WeightText { get; set; } = "";
    [Reactive] public string SizeText   { get; set; } = "";

    /// <summary>Numeric sort keys behind the display texts (null = unknown,
    /// sorts last). Volume = L×W×H, only when all three are recorded.</summary>
    public double? SortWeight { get; set; }
    public double? SortVolume { get; set; }
}

/// <summary>One row of the "Find in Shops" results list.</summary>
public sealed class ShopListingRow
{
    public ShopListingRow(ShopListing l)
    {
        Tap      = l.Tap;
        Price    = l.Price;
        Where    = string.IsNullOrEmpty(l.Room) ? l.Shop : $"{l.Shop} — {l.Room}";
        City     = l.City;
        Owner    = string.IsNullOrEmpty(l.Owner) ? "" : $"({l.Owner})";
    }

    public string Tap   { get; }
    public string Price { get; }
    public string Where { get; }
    public string City  { get; }
    public string Owner { get; }
}
