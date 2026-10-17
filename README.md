# CNO-EVIL

CNO-EVIL is a mod for **Nuclear Option**. It changes radar, electronic warfare and missile guidance to a physical model. Your faction knows only what its sensors find.

Version 1.0.6. BepInEx GUID: `com.cnoevil.nuclearoption.overhaul`.

## Controls

| Key | Function |
|---|---|
| `F10` | Change between CNO-EVIL and vanilla |
| `F9` | Show or hide the telemetry panel (debug) |
| `F8` | Show or hide the datalink overlay (debug) |
| `F11` | Show or hide the radar rays (debug) |

You can change each key in the config file, in sections `01. General` and `02. Debug Overlays`.

## Build

Requirements: .NET Framework 4.8, C# 5, the MSBuild of .NET Framework (`Framework64\v4.0.30319`), and the game with BepInEx installed.

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" CnoEvil.csproj /p:Configuration=Release
```

The build copies `CNOEVIL.dll` to `BepInEx\plugins\CNO-EVIL` in the game folder.

## Update from Radio Wars

The mod was called Radio Wars before. Delete the old folder `BepInEx\plugins\RadioWars`, else the game loads both mods. The first start copies your settings from `com.radiowars.nuclearoption.overhaul.cfg` to `com.cnoevil.nuclearoption.overhaul.cfg`.
