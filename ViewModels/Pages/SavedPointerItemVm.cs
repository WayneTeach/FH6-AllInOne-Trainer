using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FH6Mod.Cheats.Scan;
using FH6Mod.Services;

namespace FH6Mod.ViewModels.Pages;

/// <summary>
/// One row in the saved-permanent-addresses list. Backed by a persisted pointer chain;
/// resolves fresh on every launch, so the value is reachable without re-scanning.
/// </summary>
public partial class SavedPointerItemVm : ObservableObject
{
    private readonly CheatService _cheats;
    private readonly UnlocksViewModel _parent;
    public int Index { get; }
    public string Label { get; }
    public string ChainText { get; }

    [ObservableProperty] private string _currentText = "?";
    [ObservableProperty] private string _desiredText = "999999999";
    [ObservableProperty] private bool _isLocked;

    public SavedPointerItemVm(CheatService cheats, UnlocksViewModel parent, int index, SavedPointerStore.Entry entry)
    {
        _cheats = cheats;
        _parent = parent;
        Index = index;
        Label = entry.Label;
        ChainText = new PointerChain { RootOffset = entry.RootOffset, Offsets = entry.Offsets }.ToString();
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var r = _cheats.ReadSavedChain(Index);
        CurrentText = r.HasValue ? $"0x{r.Value.Address:X}  =  {r.Value.Value}" : "chain broke (re-scan)";
    }

    [RelayCommand]
    public void SetValue()
    {
        if (int.TryParse(DesiredText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            _cheats.WriteSavedChain(Index, v);
        RefreshCommand.Execute(null);
    }

    [RelayCommand]
    public void ToggleLock()
    {
        var on = !_cheats.IsChainLockActive(Index);
        if (int.TryParse(DesiredText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            _cheats.ChainLock(Index, v, on);
        IsLocked = _cheats.IsChainLockActive(Index);
    }

    [RelayCommand]
    public void Remove()
    {
        _cheats.RemoveSavedChain(Index);
        _parent.RefreshSavedChains();
    }
}
