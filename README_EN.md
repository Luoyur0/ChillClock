# Chill Clock

[简体中文](README.md) | [English](README_EN.md) | [日本語](README_JA.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-Plugin-green.svg)](https://github.com/BepInEx/BepInEx)

A BepInEx plugin for *Chill with You : Lo-Fi Story*: **when Satone is focusing, it prevents you from opening apps that are not on the whitelist.**

---

[![Chill with You](imgs/header_schinese.jpg)](https://store.steampowered.com/app/3548580/)

> *Chill with You : Lo-Fi Story* is an audiovisual novel about spending your work time with Satone, a girl who loves writing stories. You can customize original music, ambient sounds, and scenery to create an environment that helps you focus—and as your relationship deepens, you might discover something special between you two.

---

## Demo

![Demo](imgs/overview_en.png)

## What problem does it solve?

<img src="imgs/satone.png" alt="satone" width="300">

### Honestly, it solves my own problem: I get distracted too easily—clicking this, checking that, and the whole day is gone without getting anything done.

- During a focus session, Chill Clock automatically minimizes apps that are **not on your whitelist** to the taskbar;
- Non-whitelisted windows that are already open get minimized;
- Even if you open them again from the Start menu or system tray, they get minimized again;
- Whitelisted apps keep working normally;
- After focus ends / a break starts / you hang up the call, Chill Clock stops interfering.
- Works with both **Pomodoro** and **Count-up** timer modes;
- Settings include "Disable End/Skip in Focus", "Hide Side UI in Focus" and "Block Game Exit in Focus" (this one also keeps Steam from being closed, since quitting Steam force-kills the game — add Steam to the whitelist to disable that part);
- Satone can play voice reminders when you get distracted, open Task Manager, or try to exit;
- The settings UI is localized in Simplified Chinese / English / Japanese.

**Satone's voice lines (2,342 in total, all embedded in the DLL)**

| Scene | Total | Chained | Single |
| --- | ---: | ---: | ---: |
| Idle chat during focus | 466 | 115 | 351 |
| Click during focus | 125 | 44 | 81 |
| Click during a break | 133 | 44 | 89 |
| Click outside focus | 51 | 4 | 47 |
| Distraction reminders | 328 | 163 | 165 |
| Break reminders | 110 | 30 | 80 |
| Task Manager interception | 110 | 30 | 80 |
| Game exit interception | 110 | 30 | 80 |
| Mini-lecture stories (idle / break chat, clicks) | 289 | 289 | 0 |
| New stories (idle / break chat, clicks) | 619 | 619 | 0 |
| **Total** | **2,342** | **1,369** | **973** |

> "Chained" means a run of lines spoken back to back — 353 chains in total: 53 "mini-lecture" stories (4–20 lines each) plus 93 new stories added in this version (3–14 lines each). The 289 mini-lecture lines and the 619 new-story lines go into the click-outside-focus / click-during-break / idle chat / break chat pools. There are also 36 festival lines, spoken only once on the day itself and split by morning / noon / evening / night. The voice pack is embedded in `ChillClock.dll`, so no extra voice folder is needed.

## Installation

### Requirements

- *Chill with You : Lo-Fi Story*
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (do not use 6.0)

### Steps

1. **Install BepInEx**
   - Download BepInEx from the link above.
   - Extract it into the game root folder.
   - Run the game once so BepInEx creates its folders (you should see `BepInEx/plugins/`).

2. **Install the Mod**
   - Download the latest `ChillClock.dll` from Releases.
   - Put `ChillClock.dll` into `BepInEx/plugins/`.
   - Upgrading from an older build? Just **overwrite** that same `ChillClock.dll`. Do not keep two differently-named copies of this plugin in `plugins/` — BepInEx would load the whole plugin twice.
   - Your folder structure should look like this:

```
[Game root]/
└── BepInEx/
    └── plugins/
            └── ChillClock.dll
```

## License

This project is released under the [MIT License](LICENSE).

> In short: you are free to use, modify, and distribute it for personal or commercial projects; just keep the copyright notice and license text, and take responsibility for your own use.

## Credits

- Thanks to the [BepInEx](https://github.com/BepInEx/BepInEx) community
- Settings page injection based on [iGPU Savior (Potato Mode)](https://github.com/Small-tailqwq/iGPUSaviorMod)
- Pomodoro hooking based on [LofiNotify](https://github.com/kanghengliu/lofinotify)

> For personal and learning purposes only. Do not sell it directly. Use at your own risk.
