# Avalonia + Dock notes

Hard-won lessons about Dock.Avalonia setup and Avalonia ListBox virtualization
inside Dock content areas.

## Dock.Avalonia setup rules

- Package `Dock.Avalonia.Themes.Fluent` is REQUIRED — DockControl is completely invisible without `<DockFluentTheme/>` in Application.Styles. Do not use `StyleInclude` or `ResourceInclude` for it in this version.
- Namespace split: `IRootDock` is in `Dock.Model.Controls`; `IDockable`, `IFactory`, `Orientation`, `Alignment` are in `Dock.Model.Core`; concrete classes (`Document`, `Tool`, `DocumentDock`, `ToolDock`, etc.) are in `Dock.Model.ReactiveUI.Controls`.
- `SplitterDockable` was renamed to `ProportionalDockSplitter` in this version.
- Do NOT double-initialize: either call `factory.InitLayout(layout)` manually OR set `InitializeLayout="True"` on DockControl — not both. Currently using `InitializeLayout="True"` on DockControl only.
- Do NOT use `ControlRecycling` — causes content to appear in wrong panes (reuses control instances across dockable types).

**Why:** These were discovered by examining NuGet errors, XML documentation inspection, and GitHub API fetching the DockReactiveUISample.

## Dock 11.3.11 stacked-tab "wrong view" bug (issue #80, FIXED)

The stock `ToolControl` theme (Dock 11.3.11 `DockFluentTheme`) hosts the active tool in a **single** `DeferredContentControl` bound to `ActiveDockable`, whose `ContentTemplate` is a `ControlRecyclingDataTemplate`. That template's `Match()` **always returns `true`**, so on a tab switch Avalonia's `ContentPresenter` treats the template as unchanged, takes its child-reuse fast path, keeps the realized child and only re-points its `DataContext` — it never rebuilds for the new tool's type (Avalonia content-recycling, #16891 / #14863). The recycler itself is never consulted, so trying to fix it by feeding an Id-keyed `ControlRecycling` (even with `TryToUseIdAsKey`) does **nothing** — and our Mvvm `DockableBase` doesn't implement `IControlRecyclingIdProvider` anyway, so Id-keying is a no-op.

- **Symptom:** a stack of **different** tool types (Backpack / Experience / Scripts / Scene / Time Tracker) shows whichever tab built the shared view first ("the second window takes over all tabs"). A **same-type** stack (the stream tabs — all `StreamTool`) survives, because the reused view's template still fits the swapped data.
- **Fix:** `Themes/ToolControlCachedSkin.axaml` overrides `ToolControl` with Dock's own **cached, per-dockable** content host — the same shape as the `DockDocumentControlCachedContentTemplate` that ships for `DocumentControl` but was never provided for `ToolControl`. Every visible dockable gets its OWN `ContentControl`, materialized once into a shared `Grid` cell and shown/hidden by `IsVisible == Owner.ActiveDockable`. No shared host, no always-match template, no DataContext swap.
- Don't downgrade to escape it: the MDI window skin (`MdiDocumentWindowSkin`) depends on 11.3.11-only capability APIs (`DockCapabilityOverrides`/`Policy`, `Can*MultiConverter`), and no recycling-free Dock release has them.
- `DocumentControl` ships both a single (recycling) and a cached template; it currently uses the single one. Our only stacked-tab surfaces are ToolDocks, so only `ToolControl` is overridden. If documents are ever stacked in one `DocumentDock`, apply the same cached override there.

## Avalonia VirtualizingStackPanel inside Dock

When a `ListBox` (or subclass) is placed directly inside a Dock content area, the Dock's `ContentPresenter` passes infinite available height during the measure pass. The default `VirtualizingStackPanel` ItemsPanel collapses to 0 visible height when available height is infinite — resulting in 0 rendered items even with a populated `ItemsSource`.

**Fix:** Always set `ItemsPanel` to a non-virtualizing `StackPanel` when using ListBox inside Dock content:
```xml
<ListBox.ItemsPanel>
  <ItemsPanelTemplate><StackPanel/></ItemsPanelTemplate>
</ListBox.ItemsPanel>
```

With a 2000-item cap this is acceptable performance. Vitals/Room panels work because they use `ScrollViewer > StackPanel` (not a ListBox), which doesn't suffer from this issue.

**Why:** Diagnosed by adding a `{Binding Lines.Count}` diagnostic label which confirmed 152 items existed while the ListBox rendered nothing.

## Application.DataTemplates vs Window.DataTemplates

DataTemplates in `Application.DataTemplates` work correctly for Dock content rendering. No need to move them to `Window.DataTemplates`.
