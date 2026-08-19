# SyncBridge

A Dalamud plugin that makes **PlayerSync the preferred sync provider** and **Lightless the fallback** when both services are running.

## Intended behavior

- PlayerSync handles a character -> Lightless is suppressed for that character.
- PlayerSync does not handle a character -> Lightless continues normally.
- PlayerSync stops handling a character -> Lightless is allowed to handle them again.
- Pairs and Syncshell memberships are not modified.

## Current state

- Reads `PlayerSync.GetHandledAddresses` and `LightlessSync.GetHandledAddresses`.
- Tracks overlap between the two services at 250 ms intervals.
- Hooks Lightless's per-character dispatch boundary when a supported build is found.
- Suppresses Lightless only while PlayerSync owns the same character address.
- Fails open: if discovery or hooking fails, Lightless continues normally.
- Provides a saved enable/disable switch and live diagnostics window.
- Removes its Harmony patches when the plugin unloads.

Use `/syncbridge` or `/sb` to open the settings window. Add `status` to either command to print a one-time chat diagnostic.

The suppression path is experimental and must be validated in game against the installed PlayerSync and Lightless versions.

## Build

Requirements:
- XIVLauncher / Dalamud
- .NET 10 SDK
- Visual Studio 2022 or Rider

Open `SyncBridge.sln` and build `Debug|x64` or `Release|x64`.

The project uses `Dalamud.NET.Sdk/15.0.0`.

## In-game development loading

Add the built `SyncBridge.dll` as a Dalamud dev plugin location, then enable it from `/xlplugins`.

Use either command:

```
/syncbridge
/sb
```

to open the settings and live-status window. Use `/syncbridge status` or `/sb status` when you want the same diagnostic as a chat message.

## Architecture

```text
PlayerSync.GetHandledAddresses ----\
                                    > SyncCoordinator -> LightlessSuppressor
LightlessSync.GetHandledAddresses -/
```

`LightlessSuppressor` only intercepts the Lightless side. PlayerSync remains untouched.

## AI disclosure

This project was initially scaffolded with assistance from OpenAI ChatGPT. Review and test all generated code before publishing or submitting to an official Dalamud repository.
