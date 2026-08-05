using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FH6Mod.Cheats.RuntimeHook;
using FH6Mod.Cheats.Sql;
using FH6Mod.Services;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace FH6Mod.ViewModels.Pages;

public partial class UnlocksViewModel : PageViewModelBase
{
    private readonly CheatService _cheats;
    private readonly GameProcessService _game;
    private readonly LogService _log;

    public override string PageTitle => "Unlocks";
    public override string PageSubtitle => "All cheats in one place.";
    public override MaterialIconKind PageIcon => MaterialIconKind.LockOpenVariantOutline;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string? _logText;
    [ObservableProperty] private bool _canToggle;

    // --- Profile values ---
    [ObservableProperty] private bool _isCreditsOn;
    [ObservableProperty] private string _creditsAmountText = "999999999";

    [ObservableProperty] private bool _isWheelspinsOn;
    [ObservableProperty] private string _wheelspinsAmountText = "999";

    [ObservableProperty] private bool _isSuperWheelspinsOn;
    [ObservableProperty] private string _superWheelspinsAmountText = "999";

    [ObservableProperty] private bool _isSkillPointsOn;
    [ObservableProperty] private string _skillPointsAmountText = "999999";

    [ObservableProperty] private bool _isSellPayoutOn;
    [ObservableProperty] private string _sellPayoutText = "5";

    // --- Drift & Skills ---
    [ObservableProperty] private bool _isDriftMultiOn;
    [ObservableProperty] private string _driftMultiText = "10";
    [ObservableProperty] private bool _isNoSkillBreakOn;

    // --- Memory scanner (crash-free value finder) ---
    [ObservableProperty] private string _scanValueText = "";
    [ObservableProperty] private string _scanResultText = "No scan yet. Enter your current in-game value and press First Scan.";
    [ObservableProperty] private string _desiredValueText = "999999999";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isScanLockOn;
    [ObservableProperty] private string _permanentLabelText = "Credits";

    // Saved permanent addresses (pointer chains). Persisted; resolved fresh each launch.
    public ObservableCollection<SavedPointerItemVm> SavedChains { get; } = new();
    [ObservableProperty] private bool _hasSavedChains;

    public void RefreshSavedChains()
    {
        SavedChains.Clear();
        var i = 0;
        foreach (var e in _cheats.SavedPointers)
            SavedChains.Add(new SavedPointerItemVm(_cheats, this, i++, e));
        HasSavedChains = SavedChains.Count > 0;
    }

    public UnlocksViewModel()
        : this(App.Services.GetRequiredService<CheatService>(),
               App.Services.GetRequiredService<GameProcessService>(),
               App.Services.GetRequiredService<LogService>()) { }

    public UnlocksViewModel(CheatService cheats, GameProcessService game, LogService log)
    {
        _cheats = cheats;
        _game = game;
        _log = log;
        _game.StatusChanged += OnGameStatusChanged;
        _log.Changed += OnLogChanged;
        CanToggle = _game.IsAttached;
        RefreshSavedChains();
    }

    private void OnGameStatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CanToggle = _game.IsAttached;
            if (!CanToggle)
                StatusMessage = "FH6 is not running — start the game first.";
            else
                RefreshSavedChains();
        });
    }

    private void OnLogChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogText = _log.GetTail(60);
        });
    }

    private static int Parse(string s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static int ParseFloatAsIntBits(string s, float fallback)
    {
        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || f <= 0)
            f = fallback;
        return BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    }

    private void Toggle(RuntimeProfileFeature f, bool target, int value, string nameLabel)
    {
        var ok = _cheats.Apply(f, value, target);
        SetStatus(ok, ok
            ? (target ? $"{nameLabel} ON." : $"{nameLabel} OFF.")
            : _cheats.LastError);
    }

    private void ApplyValue(RuntimeProfileFeature f, int value, string nameLabel)
    {
        var ok = _cheats.UpdateValue(f, value);
        SetStatus(ok, ok ? $"{nameLabel} updated." : _cheats.LastError);
    }

    private void SetStatus(bool ok, string? msg)
    {
        StatusIsError = !ok;
        StatusMessage = msg;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000);
            StatusMessage = null;
        });
    }

    [RelayCommand] private void ClearLog() => _log.Clear();

    // ===== Quick Start =====
    [RelayCommand]
    private void QuickStart()
    {
        var cr = 999_999_999;
        var ok1 = _cheats.Apply(RuntimeProfileFeature.Credits, cr, true);
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
        var ok2 = _cheats.RunSql(SqlFeature.FreeCarPrices);
        var ok3 = _cheats.RunSql(SqlFeature.AutoshowUnlock);
        var ok4 = _cheats.RunSql(SqlFeature.InstallFlags);
        var ok5 = _cheats.RunSql(SqlFeature.AddAllCars);

        var allOk = ok1 && ok2 && ok3 && ok4 && ok5;
        StatusIsError = !allOk;
        StatusMessage = allOk
            ? "Quick Start done — 999M credits, all cars free & unlocked."
            : "Partially applied. Check Database tab.";
    }

    // ===== Max All =====
    [RelayCommand]
    private void MaxAll()
    {
        var cr = 999_999_999;
        var ws = 999;
        var sws = 999;
        var sp = 999_999;

        CreditsAmountText = cr.ToString();
        WheelspinsAmountText = ws.ToString();
        SuperWheelspinsAmountText = sws.ToString();
        SkillPointsAmountText = sp.ToString();

        var ok1 = _cheats.Apply(RuntimeProfileFeature.Credits, cr, true);
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
        var ok2 = _cheats.Apply(RuntimeProfileFeature.Wheelspins, ws, true);
        IsWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.Wheelspins);
        var ok3 = _cheats.Apply(RuntimeProfileFeature.SuperWheelspins, sws, true);
        IsSuperWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
        var ok4 = _cheats.Apply(RuntimeProfileFeature.SkillPoints, sp, true);
        IsSkillPointsOn = _cheats.IsActive(RuntimeProfileFeature.SkillPoints);

        var allOk = ok1 && ok2 && ok3 && ok4;
        SetStatus(allOk, allOk
            ? "Max All applied — all profile values maxed."
            : _cheats.LastError);
    }

    // ===== Credits =====
    [RelayCommand] private void ToggleCredits()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.Credits);
        Toggle(RuntimeProfileFeature.Credits, on, Parse(CreditsAmountText, 1_000_000), "Credits");
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
    }
    [RelayCommand] private void ApplyCredits()
        => ApplyValue(RuntimeProfileFeature.Credits, Parse(CreditsAmountText, 1_000_000), "Credits");
    [RelayCommand] private void SetCredits(string? amount) { if (amount is not null) { CreditsAmountText = amount; if (IsCreditsOn) ApplyCredits(); } }

    // ===== Wheelspins =====
    [RelayCommand] private void ToggleWheelspins()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.Wheelspins);
        Toggle(RuntimeProfileFeature.Wheelspins, on, Parse(WheelspinsAmountText, 100), "Wheelspins");
        IsWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.Wheelspins);
    }
    [RelayCommand] private void ApplyWheelspins()
        => ApplyValue(RuntimeProfileFeature.Wheelspins, Parse(WheelspinsAmountText, 100), "Wheelspins");
    [RelayCommand] private void SetWheelspins(string? a) { if (a is not null) { WheelspinsAmountText = a; if (IsWheelspinsOn) ApplyWheelspins(); } }

    // ===== Super Wheelspins =====
    [RelayCommand] private void ToggleSuperWheelspins()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
        Toggle(RuntimeProfileFeature.SuperWheelspins, on, Parse(SuperWheelspinsAmountText, 100), "Super Wheelspins");
        IsSuperWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
    }
    [RelayCommand] private void ApplySuperWheelspins()
        => ApplyValue(RuntimeProfileFeature.SuperWheelspins, Parse(SuperWheelspinsAmountText, 100), "Super Wheelspins");
    [RelayCommand] private void SetSuperWheelspins(string? a) { if (a is not null) { SuperWheelspinsAmountText = a; if (IsSuperWheelspinsOn) ApplySuperWheelspins(); } }

    // ===== Skill Points =====
    [RelayCommand] private void ToggleSkillPoints()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SkillPoints);
        Toggle(RuntimeProfileFeature.SkillPoints, on, Parse(SkillPointsAmountText, 10_000), "Skill Points");
        IsSkillPointsOn = _cheats.IsActive(RuntimeProfileFeature.SkillPoints);
    }
    [RelayCommand] private void ApplySkillPoints()
        => ApplyValue(RuntimeProfileFeature.SkillPoints, Parse(SkillPointsAmountText, 10_000), "Skill Points");
    [RelayCommand] private void SetSkillPoints(string? a) { if (a is not null) { SkillPointsAmountText = a; if (IsSkillPointsOn) ApplySkillPoints(); } }

    // ===== Sell Payout =====
    [RelayCommand] private void ToggleSellPayout()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SellFactor);
        Toggle(RuntimeProfileFeature.SellFactor, on, Parse(SellPayoutText, 5), "Sell Payout x");
        IsSellPayoutOn = _cheats.IsActive(RuntimeProfileFeature.SellFactor);
    }
    [RelayCommand] private void ApplySellPayout()
        => ApplyValue(RuntimeProfileFeature.SellFactor, Parse(SellPayoutText, 5), "Sell Payout x");

    // ===== Drift Score Multiplier =====
    [RelayCommand] private void ToggleDriftMulti()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.DriftScoreMultiplier);
        Toggle(RuntimeProfileFeature.DriftScoreMultiplier, on, ParseFloatAsIntBits(DriftMultiText, 10f), "Drift Score x");
        IsDriftMultiOn = _cheats.IsActive(RuntimeProfileFeature.DriftScoreMultiplier);
    }
    [RelayCommand] private void ApplyDriftMulti()
        => ApplyValue(RuntimeProfileFeature.DriftScoreMultiplier, ParseFloatAsIntBits(DriftMultiText, 10f), "Drift Score x");

    // ===== No Skill Break =====
    [RelayCommand] private void ToggleNoSkillBreak()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.NoSkillBreak);
        Toggle(RuntimeProfileFeature.NoSkillBreak, on, 0, "No Skill Break");
        IsNoSkillBreakOn = _cheats.IsActive(RuntimeProfileFeature.NoSkillBreak);
    }

    // ===== Memory Scanner (crash-free value editing) =====
    // Finds an in-game number by value, then writes a new one directly to memory.
    // No code hooks, so it cannot trigger the integrity scan that crashes the game.

    private void RefreshScanResult(int n)
    {
        if (n < 0) { ScanResultText = "Scan failed (not attached?)."; return; }
        if (n == 0) { ScanResultText = "0 matches. Try a different value or the changed/increased filters."; return; }
        var addrs = _cheats.ScannerAddresses;
        var sample = string.Join("\n", addrs.Take(8).Select(a => $"  0x{a:X}"));
        var more = n > 8 ? $"\n  ...and {n - 8} more" : "";
        ScanResultText = $"{n} match{(n == 1 ? "" : "es")}:\n{sample}{more}";
    }

    [RelayCommand]
    private async Task FirstScanAsync()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var value = Parse(ScanValueText, 0);
        IsScanning = true;
        ScanResultText = "Scanning memory (can take a few seconds)...";
        try
        {
            var n = await System.Threading.Tasks.Task.Run(() => _cheats.ScanFirst(value));
            RefreshScanResult(n);
            SetStatus(n >= 0, n > 0 ? $"First scan: {n} matches." : "No matches for that value.");
        }
        finally { IsScanning = false; }
    }

    /// <summary>
    /// One-shot canonical finder: scans for the value, then keeps only real profile fields
    /// (valid guard pointer at +8). Aims to return the correct canonical address on the first
    /// try so Set/Lock sticks, with no repeated narrowing.
    /// </summary>
    [RelayCommand]
    private async Task FindValueAsync()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var value = Parse(ScanValueText, 0);
        IsScanning = true;
        ScanResultText = "Finding canonical value (scan + profile-field filter, a few seconds)...";
        try
        {
            var n = await System.Threading.Tasks.Task.Run(() => _cheats.FindValue(value));
            RefreshScanResult(n);
            SetStatus(n >= 0, n > 0
                ? $"Found {n} canonical match(es). Enter the amount you want and Set (or Lock)."
                : "No canonical match. Double-check the value, or use First Scan + filters.");
        }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private async Task NextScanExactAsync()
    {
        if (!CanToggle) return;
        var value = Parse(ScanValueText, 0);
        await RunFilterAsync(() => _cheats.ScanExact(value), "exact");
    }

    [RelayCommand] private async Task NextScanIncreasedAsync() => await RunFilterAsync(_cheats.ScanIncreased, "increased");
    [RelayCommand] private async Task NextScanDecreasedAsync() => await RunFilterAsync(_cheats.ScanDecreased, "decreased");
    [RelayCommand] private async Task NextScanChangedAsync()   => await RunFilterAsync(_cheats.ScanChanged,   "changed");
    [RelayCommand] private async Task NextScanUnchangedAsync() => await RunFilterAsync(_cheats.ScanUnchanged, "unchanged");

    private async System.Threading.Tasks.Task RunFilterAsync(Func<int> op, string label)
    {
        if (!CanToggle) return;
        IsScanning = true;
        try
        {
            var n = await System.Threading.Tasks.Task.Run(op);
            RefreshScanResult(n);
            SetStatus(true, $"{label}: {n} match{(n == 1 ? "" : "es")}.");
        }
        finally { IsScanning = false; }
    }

    [RelayCommand]
    private void ScanSetValue()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        if (_cheats.ScannerMatchCount == 0) { SetStatus(false, "Scan for the value first."); return; }
        var value = Parse(DesiredValueText, 0);
        var written = _cheats.ScanWrite(value);
        SetStatus(written > 0, written > 0
            ? $"Set {value} at {written} address{(written == 1 ? "" : "es")}."
            : (_cheats.LastError ?? "Write failed."));
        RefreshScanResult(_cheats.ScannerMatchCount);
    }

    [RelayCommand]
    private void ToggleScanLock()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var on = !_cheats.IsScanLockActive;
        if (on && _cheats.ScannerMatchCount == 0) { SetStatus(false, "Scan for the value first."); return; }
        var value = Parse(DesiredValueText, 0);
        var ok = _cheats.ScanLock(value, on);
        IsScanLockOn = _cheats.IsScanLockActive;
        SetStatus(ok, ok ? (on ? "Lock ON (value re-applied every few seconds)." : "Lock OFF.") : _cheats.LastError);
    }

    [RelayCommand]
    private void NewScan()
    {
        _cheats.ScannerReset();
        IsScanLockOn = _cheats.IsScanLockActive;
        ScanResultText = "Scan reset. Enter a value and press First Scan.";
        SetStatus(true, "Scan reset.");
    }

    /// <summary>
    /// After a value scan narrows to the real address, discover a static pointer chain
    /// to it and save it. Future launches resolve the chain directly (one-click), no scan.
    /// </summary>
    [RelayCommand]
    private async Task FindPermanentAsync()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var addrs = _cheats.CurrentScanAddresses;
        if (addrs.Count == 0) { SetStatus(false, "Scan for the value first (need at least 1 match)."); return; }

        var target = addrs[0];
        IsScanning = true;
        ScanResultText = "Searching for a permanent pointer chain (can take 1-3 minutes)...";
        try
        {
            var chains = await System.Threading.Tasks.Task.Run(() => _cheats.FindPointerChains(target));
            if (chains.Count == 0)
            {
                SetStatus(false, "No static pointer chain found. Narrow the scan to exactly 1 match and retry. Some values have no permanent address.");
                return;
            }
            var label = string.IsNullOrWhiteSpace(PermanentLabelText) ? "value" : PermanentLabelText;
            _cheats.SavePointerChain(chains[0], label);
            RefreshSavedChains();
            SetStatus(true, $"Saved permanent address for {label}: {chains[0]}");
        }
        finally { IsScanning = false; }
    }
}
