using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vouch.App.ViewModels;

namespace Vouch.App.Views;

/// <summary>
/// The account sidebar. Beyond the list template it owns the interaction that can't live in the VM:
/// multi-select drag-to-reorder (implemented with pointer capture + a drop-line indicator, so it's
/// independent of the OS drag-drop plumbing) and the right-click batch actions.
/// </summary>
public partial class SidebarView : UserControl
{
    private AccountViewModel? _pressed;
    private Point _pressPos;
    private bool _dragging;
    private bool _suppressedSelect;               // kept a multi-selection on press for a possible group drag
    private List<AccountViewModel> _selAtPress = new();
    private List<AccountViewModel> _dragSet = new();

    public SidebarView()
    {
        InitializeComponent();
        // Tunnel so we see the press before the ListBox turns it into a selection change.
        AccountList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AccountList.PointerMoved += OnPointerMoved;
        AccountList.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AccountList.PointerCaptureLost += (_, _) => { if (_dragging) { EndDragVisuals(); _dragging = false; _dragSet = new(); _pressed = null; } };
        AccountList.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        AccountList.SelectionChanged += OnSelectionChanged;
        AccountList.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    /// <summary>Right-click an account (or with a selection) → account actions; empty space → new group.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var acc = ItemAt(e.Source as Visual);
        if (acc is not null && !Selection.Contains(acc))
        {
            AccountList.SelectedItems!.Clear();
            AccountList.SelectedItem = acc;
        }
        bool onAccount = acc is not null || Selection.Count > 0;
        if ((onAccount ? Resources["AccountMenu"] : Resources["NewGroupMenu"]) is FlyoutBase menu)
            AccountList.ContextFlyout = menu;
    }

    private void NewGroup_Click(object? sender, RoutedEventArgs e) => Vm?.BeginNewGroup();

    /// <summary>Group headers aren't selectable — drop any that slip into the selection.</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItems is not { } sel) return;
        foreach (var h in sel.OfType<GroupHeader>().ToList()) sel.Remove(h);
    }

    // ---- smooth (eased) wheel scrolling, stepped on the render frame clock so it stays fluid ----

    private ScrollViewer? _scroll;
    private double _targetScrollY;
    private bool _scrollAnimating;

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        _scroll ??= AccountList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;

        double max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (max <= 0) return;

        // Retarget from the live offset when idle; keep accumulating while a scroll is already animating.
        if (!_scrollAnimating) _targetScrollY = _scroll.Offset.Y;
        _targetScrollY = Math.Clamp(_targetScrollY - e.Delta.Y * 55, 0, max);
        e.Handled = true;

        if (!_scrollAnimating)
        {
            _scrollAnimating = true;
            RequestScrollFrame();
        }
    }

    private void RequestScrollFrame() =>
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ => ScrollFrame());

    private void ScrollFrame()
    {
        if (_scroll is null) { _scrollAnimating = false; return; }

        double cur = _scroll.Offset.Y;
        double diff = _targetScrollY - cur;
        if (Math.Abs(diff) < 0.5)
        {
            _scroll.Offset = new Vector(_scroll.Offset.X, _targetScrollY);
            _scrollAnimating = false;
            return;
        }

        _scroll.Offset = new Vector(_scroll.Offset.X, cur + diff * 0.22); // ease-out toward target
        RequestScrollFrame(); // continue next frame
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private List<AccountViewModel> Selection =>
        AccountList.SelectedItems?.OfType<AccountViewModel>().ToList() ?? new();

    // ---- press / drag / release ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _suppressedSelect = false;
        _pressed = ItemAt(e.Source as Visual);
        _pressPos = e.GetPosition(AccountList);
        _selAtPress = Selection; // snapshot before the ListBox mutates it
        if (_pressed is null || !e.GetCurrentPoint(AccountList).Properties.IsLeftButtonPressed) return;

        // Pressing inside a multi-selection: keep it (a plain press would collapse it) so the group can
        // be dragged. A press that turns out to be a click re-selects just this row on release.
        if (_selAtPress.Count > 1 && _selAtPress.Contains(_pressed))
        {
            _suppressedSelect = true;
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || !e.GetCurrentPoint(AccountList).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(AccountList);

        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPos.X) < 6 && Math.Abs(p.Y - _pressPos.Y) < 6) return; // threshold
            _dragging = true;
            _dragSet = _selAtPress.Contains(_pressed) && _selAtPress.Count > 0
                ? _selAtPress
                : new List<AccountViewModel> { _pressed };
            e.Pointer.Capture(AccountList);
            BeginDragVisuals();
        }

        ShowDropLine(p.Y);
        Canvas.SetLeft(DragGhost, 16);
        Canvas.SetTop(DragGhost, p.Y - 16);
        e.Handled = true;
    }

    /// <summary>Dims the dragged rows and raises the floating ghost that follows the cursor.</summary>
    private void BeginDragVisuals()
    {
        foreach (var a in _dragSet) a.IsDragging = true;
        DragGhost.DataContext = _pressed;
        bool multi = _dragSet.Count > 1;
        DragCountBadge.IsVisible = multi;
        if (multi) DragCountText.Text = _dragSet.Count.ToString();
        DragGhost.IsVisible = true;
    }

    private void EndDragVisuals()
    {
        foreach (var a in _dragSet) a.IsDragging = false;
        DragGhost.IsVisible = false;
        DropLine.IsVisible = false;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            var (group, dropBefore, _) = DropTarget(e.GetPosition(AccountList).Y);
            Vm?.MoveAccountsToGroupAt(_dragSet, group, dropBefore);
            e.Pointer.Capture(null);
            EndDragVisuals();
            _dragSet = new();
            e.Handled = true;
        }
        else if (_suppressedSelect && _pressed is not null)
        {
            // click (no drag) on a multi-selected row -> collapse to just that row
            AccountList.SelectedItems!.Clear();
            AccountList.SelectedItem = _pressed;
        }

        _dragging = false;
        _suppressedSelect = false;
        _pressed = null;
    }

    // ---- drop target + indicator ----

    private void ShowDropLine(double posY)
    {
        var (_, _, y) = DropTarget(posY);
        DropLine.Width = Math.Max(0, AccountList.Bounds.Width - 20);
        Canvas.SetLeft(DropLine, 10);
        Canvas.SetTop(DropLine, y - 1);
        DropLine.IsVisible = true;
    }

    /// <summary>Resolves a drop at <paramref name="posY"/> to a target group (which section the cursor is
    /// in — so a drag lands in the group under the cursor, not the source group), the account to land
    /// before within that group (null = append to the group), and the indicator line's Y.</summary>
    private (string Group, AccountViewModel? Before, double Y) DropTarget(double posY)
    {
        var items = AccountList.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(it => (Data: it.DataContext,
                           Top: it.TranslatePoint(new Point(0, 0), AccountList)?.Y ?? 0,
                           Height: it.Bounds.Height))
            .OrderBy(x => x.Top)
            .ToList();

        // Target group = the last group header at or above the cursor (default "" if above the first).
        string group = "";
        foreach (var x in items)
            if (x.Data is GroupHeader gh && x.Top <= posY) group = gh.Key;

        // Within that group, drop before the first account whose midpoint is past the cursor; else append.
        AccountViewModel? before = null;
        double lineY = 0;
        bool haveLine = false;
        foreach (var x in items)
        {
            if (x.Data is AccountViewModel a && (a.Group ?? "") == group)
            {
                if (posY < x.Top + x.Height / 2) { before = a; lineY = x.Top; haveLine = true; break; }
                lineY = x.Top + x.Height; // trailing edge of the last account in the group
                haveLine = true;
            }
            else if (x.Data is GroupHeader gh && gh.Key == group && !haveLine)
            {
                lineY = x.Top + x.Height; // empty/collapsed group: line just under its header
            }
        }
        return (group, before, lineY);
    }

    private static AccountViewModel? ItemAt(Visual? v) =>
        v?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AccountViewModel;

    // ---- batch actions (operate on the selection, falling back to the single selected account) ----

    private List<AccountViewModel> BatchTargets()
    {
        var sel = Selection;
        if (sel.Count == 0 && Vm?.SelectedAccount is { } one) sel.Add(one);
        return sel;
    }

    private void SignInSelected_Click(object? sender, RoutedEventArgs e)
    {
        Vm?.BatchSignIn(BatchTargets());
    }

    private void UpdateSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) _ = Vm.BatchUpdateInfo(BatchTargets());
    }

    private void TransferSelected_Click(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginTransfer(BatchTargets());
    }

    private void ExportSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) _ = Vm.BatchExport(BatchTargets());
    }

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginBatchRemove(BatchTargets());
    }

    private void DeactivateSelected_Click(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginDeactivate(BatchTargets());
    }

    private void MoveToGroup_Click(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginMoveToGroup(BatchTargets());
    }

    private void SyncCs2_Click(object? sender, RoutedEventArgs e)
    {
        // the right-clicked / selected account is the source to copy CS2 config from
        Vm?.BeginCs2Sync(BatchTargets().FirstOrDefault());
    }

    private void RestoreCs2_Click(object? sender, RoutedEventArgs e) => Vm?.BeginCs2Restore();

    private void RenameGroup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GroupHeader h) Vm?.BeginRenameGroup(h);
    }

    private void DeleteGroup_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GroupHeader h) Vm?.DeleteGroupCommand.Execute(h);
    }
}
