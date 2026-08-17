# SyncBridge

A Dalamud plugin project intended to make **PlayerSync the preferred sync provider** and **Lightless the fallback** when both services are running.

## Intended behavior

- PlayerSync handles a character -> Lightless is suppressed for that character.
- PlayerSync does not handle a character -> Lightless continues normally.
- PlayerSync stops handling a character -> Lightless is allowed to handle them again.
- Pairs and Syncshell memberships are not modified.

## Current state

### Implemented
- Reads `PlayerSync.GetHandledAddresses`.
- Reads `LightlessSync.GetHandledAddresses`.
- Tracks overlap between the two services.
- Polls at 250 ms intervals.
- `/syncbridge` displays current counts.
- Isolated suppression component ready for the Lightless interception layer.

### Not implemented yet
- Runtime interception of Lightless's per-character sync/apply path.

The initial version intentionally does **not** guess at internal Lightless offsets or method signatures. The suppression hook should be implemented only after matching it against the exact current Lightless build.

## Build

Requirements:
- XIVLauncher / Dalamud
- .NET 8 SDK
- Visual Studio 2022 or Rider

Open `SyncBridge.sln` and build `Debug|x64` or `Release|x64`.

The project uses `Dalamud.NET.Sdk/15.0.0`.

## In-game development loading

Add the built `SyncBridge.dll` as a Dalamud dev plugin location, then enable it from `/xlplugins`.

Use:

```
/syncbridge
```

to show current PlayerSync, Lightless, and overlap counts.

## Architecture

```text
PlayerSync.GetHandledAddresses ----\
                                    > SyncCoordinator -> LightlessSuppressor
LightlessSync.GetHandledAddresses -/
```

`LightlessSuppressor` will ultimately arbitrate only the Lightless side. PlayerSync remains untouched.

## AI disclosure

This project was initially scaffolded with assistance from OpenAI ChatGPT. Review and test all generated code before publishing or submitting to an official Dalamud repository.
