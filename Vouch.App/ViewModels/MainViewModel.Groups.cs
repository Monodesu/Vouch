using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Storage;

namespace Vouch.App.ViewModels;

/// <summary>A collapsible group header row in the sidebar. Key "" is the default group.</summary>
public sealed partial class GroupHeader : ObservableObject
{
    public string Key { get; }
    public GroupHeader(string key, int count, bool collapsed)
    {
        Key = key;
        _count = count;
        _collapsed = collapsed;
    }

    public bool IsDefault => Key.Length == 0;
    public string Display => IsDefault ? Loc.T("Group_Default") : Key;
    [ObservableProperty] private bool _collapsed;
    [ObservableProperty] private int _count;
    public string Glyph => Collapsed ? "▸" : "▾";
    partial void OnCollapsedChanged(bool value) => OnPropertyChanged(nameof(Glyph));
}

/// <summary>
/// Sidebar grouping: accounts carry a group name and the list is rebuilt as a flat stream of group
/// headers + their accounts, so the existing ListBox keeps its multi-select / drag / smooth-scroll.
/// The default group ("") holds ungrouped accounts and is always first.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Flat sidebar model: a mix of <see cref="GroupHeader"/> and <see cref="AccountViewModel"/>.</summary>
    public ObservableCollection<object> SidebarItems { get; } = new();
    /// <summary>Custom (non-default) group names, for the "move to group" menu.</summary>
    public ObservableCollection<string> GroupNames { get; } = new();

    private List<string> _groupOrder = new();
    private readonly HashSet<string> _collapsedGroups = new();
    private Dictionary<string, string> _accountGroupMap = new();

    private void InitGroups()
    {
        Accounts.CollectionChanged += (_, _) => { RebuildSidebar(); RefreshUnencryptedWarning(); };
        RebuildSidebar();
    }

    // ---- persistence bridge: the layout lives in maFiles/entries.json (see MaFileIndex) ----

    /// <summary>Pulls the account order + groups out of the repo index into the in-memory state used by
    /// <see cref="ApplyAccountOrder"/> / <see cref="ApplyGroupsToAccounts"/>. Safe while locked — the
    /// index isn't encrypted.</summary>
    private void LoadLayoutFromIndex()
    {
        var index = _repo.GetIndex();
        _accountOrder = index.Accounts.Select(e => e.SteamId.ToString()).ToList();
        _accountGroupMap = index.Accounts
            .Where(e => !string.IsNullOrEmpty(e.Group))
            .GroupBy(e => e.SteamId.ToString())
            .ToDictionary(g => g.Key, g => g.Last().Group!);
        _groupOrder = index.Groups.Select(g => g.Name).Where(n => n.Length > 0).Distinct().ToList();
        _collapsedGroups.Clear();
        foreach (var g in index.Groups.Where(g => g.Collapsed)) _collapsedGroups.Add(g.Name);
    }

    /// <summary>Persists the current sidebar layout (account order + each account's group + group
    /// order/collapsed) to the repo index. Replaces the old settings-based save.</summary>
    private void SaveLayout()
    {
        if (_loadingSettings) return;
        var accounts = Accounts
            .Select(a => new MaFileEntry
            {
                SteamId = ulong.TryParse(a.SteamId, out var id) ? id : 0,
                Group = string.IsNullOrEmpty(a.Group) ? null : a.Group,
            })
            .Where(e => e.SteamId != 0)
            .ToList();
        var groups = _groupOrder
            .Where(g => g.Length > 0).Distinct()
            .Select(name => new GroupEntry { Name = name, Collapsed = _collapsedGroups.Contains(name) })
            .ToList();
        _repo.SaveLayout(accounts, groups);
    }

    /// <summary>Applies persisted group assignments to the loaded accounts, then rebuilds the list.</summary>
    public void ApplyGroupsToAccounts()
    {
        foreach (var a in Accounts)
            a.Group = _accountGroupMap.TryGetValue(a.SteamId, out var g) ? g : "";
        RebuildSidebar();
    }

    // ---- rebuild ----

    private void RebuildSidebar()
    {
        SidebarItems.Clear();

        var byGroup = new Dictionary<string, List<AccountViewModel>>();
        foreach (var a in Accounts)
        {
            var key = a.Group ?? "";
            if (!byGroup.TryGetValue(key, out var list)) byGroup[key] = list = new();
            list.Add(a);
        }

        // default group first, then the saved custom order, then any newcomers
        var order = new List<string> { "" };
        foreach (var g in _groupOrder) if (g.Length > 0 && !order.Contains(g)) order.Add(g);
        foreach (var g in byGroup.Keys) if (!order.Contains(g)) order.Add(g);

        foreach (var key in order)
        {
            byGroup.TryGetValue(key, out var accs);
            accs ??= new();
            if (key.Length == 0 && accs.Count == 0) continue; // hide the default group when empty
            SidebarItems.Add(new GroupHeader(key, accs.Count, _collapsedGroups.Contains(key)));
            if (!_collapsedGroups.Contains(key))
                foreach (var a in accs) SidebarItems.Add(a);
        }

        GroupNames.Clear();
        foreach (var g in order) if (g.Length > 0) GroupNames.Add(g);
    }

    // ---- commands ----

    [RelayCommand]
    private void ToggleGroupCollapse(GroupHeader? header)
    {
        if (header is null) return;
        if (!_collapsedGroups.Remove(header.Key)) _collapsedGroups.Add(header.Key);
        SaveLayout();
        RebuildSidebar();
    }

    /// <summary>Moves accounts into <paramref name="group"/> ("" = default), creating the group if new.</summary>
    public void MoveAccountsToGroup(IReadOnlyList<AccountViewModel> accounts, string group)
    {
        group = group.Trim();
        if (accounts.Count == 0) return;
        foreach (var a in accounts) a.Group = group;
        if (group.Length > 0 && !_groupOrder.Contains(group)) _groupOrder.Add(group);
        SaveLayout();
        RebuildSidebar();
    }

    /// <summary>Deletes a group: its accounts fall back to the default group.</summary>
    [RelayCommand]
    private void DeleteGroup(GroupHeader? header)
    {
        if (header is null || header.IsDefault) return;
        foreach (var a in Accounts.Where(a => a.Group == header.Key).ToList()) a.Group = "";
        _groupOrder.Remove(header.Key);
        _collapsedGroups.Remove(header.Key);
        SaveLayout();
        RebuildSidebar();
    }

    /// <summary>Renames a group, keeping its accounts and position.</summary>
    public void RenameGroup(string oldName, string newName)
    {
        newName = newName.Trim();
        if (oldName.Length == 0 || newName.Length == 0 || oldName == newName) return;
        foreach (var a in Accounts.Where(a => a.Group == oldName).ToList()) a.Group = newName;
        var idx = _groupOrder.IndexOf(oldName);
        if (idx >= 0) _groupOrder[idx] = newName; else _groupOrder.Add(newName);
        if (_collapsedGroups.Remove(oldName)) _collapsedGroups.Add(newName);
        SaveLayout();
        RebuildSidebar();
    }

    // ---- move-to-group / rename dialog ----

    [ObservableProperty] private bool _showGroupPicker;
    [ObservableProperty] private string _groupPickerName = "";
    [ObservableProperty] private bool _groupDialogIsRename;
    private List<AccountViewModel> _groupPickerTargets = new();
    private string _renameOldName = "";

    /// <summary>The picker shows existing-group chips only when moving actual accounts.</summary>
    public bool GroupDialogShowPick => !GroupDialogIsRename && _groupPickerTargets.Count > 0;
    public string GroupDialogTitle => GroupDialogIsRename ? Loc.T("Group_RenameTitle")
        : _groupPickerTargets.Count == 0 ? Loc.T("Group_NewTitle") : Loc.T("Group_MoveTitle");

    private void RaiseGroupDialog()
    {
        OnPropertyChanged(nameof(GroupDialogTitle));
        OnPropertyChanged(nameof(GroupDialogShowPick));
    }

    /// <summary>Opens the picker to move the given accounts into a group (existing or new).</summary>
    public void BeginMoveToGroup(IReadOnlyList<AccountViewModel> accounts)
    {
        _groupPickerTargets = accounts.ToList();
        if (_groupPickerTargets.Count == 0) return;
        CloseDialogs();
        GroupDialogIsRename = false;
        GroupPickerName = "";
        RaiseGroupDialog();
        ShowGroupPicker = true;
    }

    /// <summary>Opens the picker to create a new (empty) group.</summary>
    public void BeginNewGroup()
    {
        CloseDialogs();
        GroupDialogIsRename = false;
        _groupPickerTargets = new();
        GroupPickerName = "";
        RaiseGroupDialog();
        ShowGroupPicker = true;
    }

    /// <summary>Opens the picker in rename mode for a group header.</summary>
    public void BeginRenameGroup(GroupHeader header)
    {
        if (header.IsDefault) return;
        CloseDialogs();
        GroupDialogIsRename = true;
        _renameOldName = header.Key;
        GroupPickerName = header.Key;
        RaiseGroupDialog();
        ShowGroupPicker = true;
    }

    [RelayCommand]
    private void ConfirmGroupDialog()
    {
        if (GroupDialogIsRename)
            RenameGroup(_renameOldName, GroupPickerName.Trim());
        else if (_groupPickerTargets.Count == 0)
            CreateGroup(GroupPickerName.Trim());
        else
            MoveAccountsToGroup(_groupPickerTargets, GroupPickerName.Trim());
        _groupPickerTargets = new();
        ShowGroupPicker = false;
    }

    /// <summary>Creates a new empty group (shown even with no accounts yet).</summary>
    public void CreateGroup(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || _groupOrder.Contains(name)) return;
        _groupOrder.Add(name);
        SaveLayout();
        RebuildSidebar();
    }

    /// <summary>Picks an existing group (or default via "") from the chips in the move dialog.</summary>
    [RelayCommand]
    private void PickGroup(string? name)
    {
        MoveAccountsToGroup(_groupPickerTargets, name ?? "");
        _groupPickerTargets = new();
        ShowGroupPicker = false;
    }
}
