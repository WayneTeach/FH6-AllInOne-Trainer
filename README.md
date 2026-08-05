# FH6 All-in-One Trainer

An all-in-one trainer for **Forza Horizon 6** — car/physics cheats, live SQL access to the game's in-memory database, and runtime profile value hooks. Self-contained `.exe`, no .NET install needed.

> **Offline mode only.** This trainer modifies game memory. Online play (Rivals, Eventlab, Multiplayer, leaderboards) will not work and may result in a ban. Run FH6 in offline mode before using.

## Status

The current release is **v7.3.0** (pre-release, in testing).

**Why profile cheats crash, and what changed.** Forza Horizon 6 periodically hashes its own code section (`.text`). Any modification to that section, whether written externally or by injected shellcode, is detected and the game kills itself cleanly (no crash dump). This is why every hook-based profile cheat has crashed across v6.0–v7.1. The SQL cheats never crash because they only touch newly allocated memory, never `.text`.

**v7.3.0 adds a crash-free Memory Finder.** Full decompilation of the game confirms profile values (Credits, Wheelspins, Skill Points, etc.) are stored as **plaintext `int32` at `[obj+8]`**, each with a **guard object pointer at `[obj+0x10]`**. The Finder scans for your current value, then keeps only the matches that have a valid guard pointer 8 bytes later — i.e. the real profile field — and discards display caches. That means it lands on the **canonical address on the first try** (the thing earlier "scanners" missed, causing values to revert). No code hook is involved, so it cannot trigger the integrity scan.

- **Memory Finder** — crash-free, one-shot. Enter your current value, click **Find Value**, then Set or Lock. Works for Credits, Wheelspins, Super Wheelspins, Skill Points, XP, and any integer.
- **SQL cheats** (Free Cars, Autoshow, Add All Cars, etc.) continue to work across all versions.
- **Profile value toggles and physics hooks** (Drift multiplier, No Skill Break, etc.) install `.text` hooks and **will crash the game**. The experimental bypass that tried to neutralize the integrity check was **removed** — it crashed testers in v7.2.0 and v7.2.1 (the signature matched several `TerminateProcess` wrappers and patched the wrong one). Use the Memory Finder for those values.

## Download

Latest release: **[GitHub Releases](../../releases)** — download the `.zip`, extract, and run `FH6AllInOneTrainer.exe` as Administrator.

## How to use

1. Start Forza Horizon 6 and **load fully into the world** (be driving, not in a menu).
2. Launch the trainer as Administrator and attach.
3. To edit money/spins/points: open the **Memory Finder**, enter your exact current value, click **Find Value**, then enter the amount you want and **Set** (or **Lock** to keep it applied).
4. SQL cheats (cars, upgrades, etc.) are on the Database tab — one click.

> Enable cheats only once you are fully in-game. Offline mode only.

## Features

### SQL Database (in-memory SQLite)
- **Unlock Everything** — all SQL cheats in one click
- Free Cars (BaseCost=0), Autoshow Unlock, Install Flags
- Add All Cars (CarBuckets approach), Free Upgrades (47 tables), Free Wheels, Full Autoshow
- Unlock Upgrade Presets, Clear "NEW!" Tag

### Physics & Performance (SQL)
- Drift Score 10x, Max Traction, Torque 2x, Reduce Drag 0.5x

### Memory Finder (crash-free, one-shot)
- **Find Value** — enter your current in-game number; the trainer scans then keeps only the canonical profile field (valid guard pointer at +8), discarding display caches. Correct on the first try.
- Set a value once, or **Lock** it to keep re-applying.
- Manual First Scan / Next Scan filters are still available for edge cases.
- The recommended way to edit Credits, Wheelspins, Super Wheelspins, Skill Points, and XP.

### Profile Values & Physics (runtime hooks — will crash)
- Credits, Wheelspins, Super Wheelspins, Skill Points, Drift Score Multiplier, No Skill Break, Sell Payout.
- These install `.text` hooks and **crash FH6**. No working bypass exists, so they are disabled/unsafe. Use the Memory Finder for the values above.

### Quick Actions
- **Quick Start** — 999M Credits + Free Cars + Autoshow Unlock + Install Flags + All Cars
- **Max All** — max Credits, Wheelspins, Super Wheelspins, Skill Points

## Known Limitations

- **Hook-based profile cheats and physics hooks can crash the game** because Forza Horizon 6 detects modifications to its code section. The Memory Scanner and SQL cheats avoid this entirely. Use the scanner for Credits, Wheelspins, and Skill Points.
- **Experimental Integrity Bypass** (Unlocks page) neutralizes FH6's `.text` integrity kill path so hook cheats *may* survive. Found by reversing the kill chain (check function → HMAC-SHA256 compare → kill wrapper → `TerminateProcess`). It is a single kill path and untested against a live game — a secondary checker could still crash it. Turn it on, then test one hook cheat.
- **Physics/behavior cheats** (Drift multiplier, No Skill Break, gravity, teleport, etc.) require code hooks and currently have no crash-free equivalent.
- **Memory Scanner is version-independent** — it finds values by content, not by fixed offsets, so it works across game updates. SQL and hook signatures may need updating when the game patches.

## Build from Source

Requires **.NET 10 SDK** on Windows:

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## Credits

| Who | Contribution |
|-----|-------------|
| **[paris' club](https://discord.gg/WSd3bRNJuJ)** | Core profile cheats (CALL-resolution approach), SQL features |
| **[ForzaMods](https://github.com/ForzaMods/Forza-Mods-AIO)** | AOB signatures reference |
| **[matkhl](https://www.unknowncheats.me/forum/other-games/752793)** | Free Upgrades SQL (47 tables), CarBuckets approach, database dumper |
| **[Omkmakwana](https://github.com/Omkmakwana/FH6Trainer)** | Add All Cars reference |
| **[Chaarkor](https://github.com/Chaarkoor)** | Original Avalonia UI shell, MVVM architecture |
| **[changcheng967](https://github.com/changcheng967)** | All-in-one integration, physics SQL cheats, in-process hook installation, UI |

## License

GPL-3.0 — see [LICENSE](LICENSE).
