<p align="center">
  <img src="https://i.gyazo.com/6272d7aec36d9c4c9a3f4446dbeb54bd.png" width="128" alt="UCU ModManager">
</p>

<h1 align="center">UCU ModManager</h1>

<p align="center">
  Universal Mod Manager for <strong>Casualties Unknown</strong>.
</p>

> [!WARNING]
> UCU ModManager is currently in early alpha. Please report bugs, launch errors,
> Mod installation issues, virtualization problems, or any odd behavior in the manager.

[**Download Latest Pre-Release**](https://github.com/ArchBlake/UCUModManager/releases/tag/v0.1.3-AV)

[**VirusTotal**](https://www.virustotal.com/gui/file/0d1520ae5e2f43050eb2964e535258815f330c74538bd49be60b836417a482dd)

Metadata from **Jimmyking** - [**GitHub**](https://github.com/jimmyking9999999/Metadata-generator/tree/main)

## About

UCU ModManager is a tool for conveniently managing Casualties Unknown mods. It lets you install and remove mods, create separate profiles, configure load order, build and install modpacks, check for updates, and launch the game with a selected mod setup.

The main goal of the project is to simplify working with mods so you do not have to manually rebuild the game every time you want to update mods, switch builds, or prepare a new modpack.

## Virtualization

Virtualization is currently being actively tested. With this feature, the base game with BepInEx installed remains unchanged: mods are loaded through a profile overlay, and you can freely switch, edit, and create different configurations.

For now, virtualization should be treated as an alpha feature. It is recommended by default, but feedback and bug reports are very important.

## Important

Use a clean game installation whenever possible. BepInEx must be installed into the game folder first, preferably through UCU ModManager itself.

The manager treats a clean game with BepInEx installed as the expected base state. Mods, profiles, modpacks, and manager data are stored inside the manager folder.

## Features

- Install mods from archives or folders.
- Manage separate profiles with their own enabled mods and load order.
- Launch the game through an experimental virtual profile.
- Download and install updates when possible.
- Create and import `.UCU` modpack recipes.
- Create and import `.UCUP` portable modpacks with included mod files.
- Show mod dependencies, warnings, conflicts, and Nexus information.

## Installation

1. Download the latest release archive.
2. Extract it into an empty folder.
3. Run `UCU ModManager.exe`.

## Requirements

- .NET 8 Desktop Runtime.
- Steam version of Casualties Unknown Demo.

## Notes

Automatic Nexus downloads require a configured Nexus Mods API key and may require Nexus Premium. If automatic download is not available, the manager can open Nexus file pages for manual download.
