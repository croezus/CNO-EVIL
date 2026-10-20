# CNO-EVIL: Nuclear Option Enhanced Visibility, Identification, Linkage

In vanilla, everyone sees everything. With CNO-EVIL, your faction knows only what its sensors actually find.

Version 1.0.8.1. BepInEx GUID: `com.cnoevil.nuclearoption.overhaul`.

## Key features

* **Real radar physics.** Detection follows the radar equation. It accounts for RCS, Earth curvature with 4/3 refraction over real terrain, and ground and sea clutter.
* **Electronic warfare.** Jamming pods have a real beam, and stand-off jamming follows the one-way radar equation. Burn-through is modelled, and your jam cone is shown on the map.
* **Tracks and datalink.** Everything you know comes as tracks with a quality Q, shared across the whole faction. The better the track, the more accurate the shot.
* **Missiles that follow the same rules.** Seekers use the radar model, including bistatic clutter for semi-active homing. Anti-radiation missiles home on scanning radars. Hit accuracy depends on track quality and launch distance.
* **AI without cheating.** Enemy AI fires by predicted dispersion, not by magic knowledge. Air defense reacts to what it actually perceives, and guided artillery needs a real track.

## Additional changes

* RWR tiers that depend on the platform. A radar beam too weak to detect you still dips the TacScreen EM scan ring.
* Visual contacts come from vanilla optics, and aircraft magnification depends on avionics tier.
* HUD: a detection status line (ID NO to OPT) and your own frontal RCS.
* Config: many parameters can be edited in the config.

## Controls

| Key | Function |
|---|---|
| `F10` | Change between CNO-EVIL and vanilla at any time |
| `F9` | Show or hide the telemetry panel (debug) |
| `F8` | Show or hide the datalink overlay (debug) |
| `F11` | Show or hide the radar rays (debug) |

You can change each key in the config file, in sections `01. General` and `02. Debug Overlays`.

## Installation

* Requires BepInEx. Put the mod in `BepInEx\plugins\CNO-EVIL`.
* If you used Radio Wars before, delete `BepInEx\plugins\RadioWars` or the game loads both. The first start copies your old settings from `com.radiowars.nuclearoption.overhaul.cfg` to `com.cnoevil.nuclearoption.overhaul.cfg`.

## Build

Requirements: .NET Framework 4.8, C# 5, the MSBuild of .NET Framework (`Framework64\v4.0.30319`), and the game with BepInEx installed.

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" CnoEvil.csproj /p:Configuration=Release
```

The build copies `CNOEVIL.dll` to `BepInEx\plugins\CNO-EVIL` in the game folder.

## Patreon

If you enjoy this modification, you can support my work here: https://www.patreon.com/croezus

All contributions ~~will go to the European Alliance Organization~~ and be reinvested in development tools to help create new modifications.
