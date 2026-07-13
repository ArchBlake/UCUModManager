<p align="center">
  <img src="https://i.gyazo.com/1ec4bb2be3a4f7e3dc6efd55d967009f.png" width="360" alt="UCU Mod Manager">
</p>

<h1 align="center">UCU ModManager</h1>

<p align="center">
  Universal Mod Manager for <strong>Casualties Unknown</strong>.
</p>

> [!WARNING]
> UCU ModManager is currently in early alpha. Please report bugs, launch errors,
> Mod installation issues, virtualization problems, or any odd behavior in the manager.

[**Download Latest Pre-Release**](https://github.com/ArchBlake/UCUModManager/releases/download/v0.1.4-AV-NOAPI-P/UCU-ModManager-0.1.4-alpha-public-no-API-portable.zip)

[**VirusTotal**](https://www.virustotal.com/gui/url/24d48ed7fc376d15a648f425e494b7b174e8938f39d718c7360975efddaf8be0)

Metadata from [**Jimmyking**](https://github.com/jimmyking9999999) - [**GitHub**](https://github.com/jimmyking9999999/Metadata-generator/tree/main)

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
- Check for updates and install them via manual archive downloads.
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

This build does not use personal Nexus Mods credentials. Nexus metadata is loaded from the public metadata catalog, and update actions open Nexus file pages for manual archive download.
