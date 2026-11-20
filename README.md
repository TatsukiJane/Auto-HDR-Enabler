# Auto HDR

A small Windows 11 app that turns HDR on when a game starts and back to SDR when it exits. The desktop stays in proper SDR, and HDR shows up only when you need it

The interface is in English and Russian. The language follows Windows, and you can change it in Settings

https://github.com/user-attachments/assets/d99db3ee-6c74-4e96-a599-ae97c47dd86d

[Download](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest) · [Demo video](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/download/v1.0.10/AutoHdr-demo.mp4) · [How it works](#how-it-works) · [Is it safe](#is-it-safe) · [How to use](#how-to-use) · [На русском](#на-русском)

## What it is

With HDR on, Windows shows the regular SDR desktop with washed-out, milky colors. With HDR off, games lose HDR. Turning HDR on and off by hand before every game is easy to forget

Auto HDR does it for you. It sits in the tray, notices when a game with HDR support starts, turns HDR on for your monitor and turns it back off when the game exits. It only flips the same HDR switch you would flip in Windows Settings

Auto HDR is not a driver, a game mod or an overlay. It does not inject anything into games and does not change game files

## How it works

1. **Finding games.** The app reads the game lists that Steam, GOG, Epic, EA app, Ubisoft Connect, Xbox and Rockstar keep on disk and in the registry. You can also add a game exe or a folder with games yourself
2. **Checking HDR support.** For each game the app asks three sources:
   - **Steam**: whether the store page has the “HDR available” tag
   - **PCGamingWiki**: the HDR field in the game video settings table
   - **game files**: whether the game exe mentions HDR settings such as `HDR10` or `PaperWhite`. The files are only read, never changed
3. **Choosing games.** If Steam or PCGamingWiki confirms HDR, **auto HDR** turns on for the game right away. For other games it is off, and one checkbox turns it on. Your own choice always wins and survives a recheck
4. **Watching for games.** Every 0.25 s the app compares the list of running processes with the previous one and reads the exe path of each new process. If the exe lies in the folder of a game with auto HDR, the game has started
5. **Turning HDR on.** The app turns HDR on for the displays you checked, through the Windows display configuration API. It is the same switch as Settings → System → Display → Use HDR. The app remembers which displays it switched
6. **Turning HDR off.** When all processes of the game exit, the app turns HDR off on those displays only. HDR that was already on before the game stays on. The list of switched displays is saved to disk, so HDR goes back to SDR even after a crash or a restart

| HDR status | Meaning | Auto HDR by default |
|---|---|---|
| Yes | Confirmed by Steam or PCGamingWiki | On |
| Maybe | HDR comes with a mod, or the game files mention HDR settings | Off |
| No | PCGamingWiki: HDR not supported | Off |
| Unknown | No data | Off |

HDR is switched only on displays with “Auto HDR” checked. The checkbox turns on by itself only for the first physical HDR monitor found. TVs (the name contains “TV”, “BRAVIA” or a TV brand), virtual displays (Parsec, SudoMaker) and all displays connected later are added with the checkbox off

The app can also switch the monitor to a saved picture mode, for example one with local dimming, while a game runs. See [Monitor picture mode](#monitor-picture-mode)

## Is it safe

Auto HDR changes one thing in Windows: the HDR on and off switch of the displays you checked

**What it changes**

- The HDR switch of checked displays, through the same Windows API the Settings app uses
- Only if you set up a game mode for a monitor: the monitor picture mode, over DDC/CI, the same channel the monitor maker apps use
- Its own files in `%LOCALAPPDATA%\AutoHdr`: `settings.json`, `games.json`, `monitors.json` and `log.txt`
- Only if you turn on “Start with Windows”: a copy of the app in `C:\Program Files\Auto HDR` and a Task Scheduler task named “AutoHDR Enabler”

**What it does not touch**

- Resolution, refresh rate, color profiles, HDR calibration and GPU driver settings
- Monitor settings such as brightness: the app only selects one of the picture modes you set up on the monitor, and only for games with “Monitor mode” checked
- Game files and game settings. Game exe files are only read, to look for HDR mentions
- The registry. The app only reads where launchers keep their game lists
- Other programs. No injection, no hooks, no reading of game memory. The app sees only the list of running processes and the paths of their exe files

**Network**

- The app talks to two sites only. Steam Store (`store.steampowered.com`) gets the Steam AppID of a game. PCGamingWiki (`pcgamingwiki.com`) gets the Steam or GOG ID of a game, or its name
- No accounts, telemetry, ads, analytics or automatic updates. Nothing about you or your PC is sent
- You can turn the online check off in Settings. Then only the game files are checked

**Administrator rights**

- Switching HDR itself needs no administrator rights. They are needed only for “Wait for HDR”: Windows lets a program pause a game that runs as administrator only if the program has the same rights
- To avoid a UAC prompt at every sign-in, the app starts through a Task Scheduler task. The task runs the copy in `C:\Program Files\Auto HDR`, where a regular program cannot replace it
- The elevated copy is hardened against tampering by other programs. It starts system programs only by their full path, keeps temporary files in `%WINDIR%\Temp`, does not follow symbolic links in its data folder and pauses only processes of the same user. Details are in [CLAUDE.md](CLAUDE.md), section «Безопасность» (in Russian)

**“Wait for HDR”**

This option pauses the game process for a moment, usually a fraction of a second, until HDR is on. It is off by default. Do not turn it on for online games with anti-cheat: the anti-cheat may take the paused process for tampering

**You can check all of this yourself**

The full source is in this repository, and releases are built from it with `build.ps1`. The release page shows the SHA-256 of the archive. The exe is not signed, so Windows SmartScreen may warn you about an unknown publisher

## How to use

### Install

1. Download `AutoHdr-<version>-win-x64.zip` from [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest) and extract it to any folder. You do not need to install .NET: everything is inside
2. Run `AutoHdr.exe`. If SmartScreen appears, click “More info”, then “Run anyway”
3. In Settings, turn on “Start with Windows”. The app copies itself to `C:\Program Files\Auto HDR` and from now on starts from there at sign-in, with administrator rights and without a UAC prompt. After that you can delete the folder you extracted the archive to

### First run

1. **Games.** The app looks for games on the first run, then checks HDR support for each of them. It takes a few minutes. Look through the list and check “Auto HDR” for the games where you want HDR. “Add game” and “Add folder” cover games outside the libraries
2. **Displays.** Make sure “Auto HDR” is checked only for the monitors where you want HDR in games. TVs start unchecked
3. **Settings.** Choose the language, turn on the delay before turning HDR off if HDR flickers when a launcher starts the game, and turn notifications on or off

### Every day

Just play. HDR turns on when a game with auto HDR starts and turns off when you exit it

Closing the window hides it to the tray. The tray icon shows the current mode: **HDR** or **SDR**. Right-click it to toggle HDR manually, pause auto switching or exit

### Games that check HDR at launch

Some games, such as Cyberpunk 2077, check for HDR in the first seconds and hide the HDR setting if HDR is not on yet. For them, check “Wait for HDR” next to the game

A fallback for Steam is the game launch options (Properties → Launch Options):

```
"C:\Program Files\Auto HDR\AutoHdr.exe" --launch %command%
```

Then HDR turns on strictly before the game starts and turns off after all its processes exit. If auto switching is paused, the game starts without HDR. Settings shows this line with the right path

### Monitor picture mode

Some monitors have several picture modes, for example with and without local dimming on a Mini LED monitor. Auto HDR can switch to the mode you want while a game or any other program runs, and switch back after it exits

1. Set up the game mode once. On the Displays tab, click “Set up game mode” next to the monitor and follow three steps: select your everyday mode with the monitor buttons, then the game mode, then the everyday mode again. Close the monitor menu before each OK. The app reads the monitor settings at each step, finds the command that switches the whole mode and checks it, so the picture may flicker. The setup takes about a minute, and the monitor ends in the everyday mode. The row then shows the saved mode, for example “game mode: E0 = 6”
2. On the Games tab, check “Monitor mode” for the games you want. The column appears once a game mode is set up. Programs that are not games can be added with “Add game”

The mode works on its own, without HDR, and together with it. When the game exits, the app restores the previous mode. If the monitor was already in the game mode, or you changed the mode yourself during the game, the app leaves it as is

Requirements and limits:

- DDC/CI must be on in the monitor menu. Close the monitor maker app during the setup and while you play: two apps talking to the monitor at once may interfere
- Not every monitor can do this. The setup tells you if the monitor does not answer over DDC/CI, if its settings do not change between the modes, or if no single command switches the whole mode. Then nothing is saved and the “Monitor mode” column stays hidden
- During the setup the app writes to the monitor only values the monitor itself reported a moment before. Turn HDR off for the setup: in HDR the monitor overrides the mode brightness and colors
- In HDR the monitor keeps the selected mode but applies its own HDR brightness and color settings on top. Checked on the Titan Army P275MS+

### Update

Exit Auto HDR from the tray icon menu, extract the new archive and run `AutoHdr.exe --install-task` in its folder. Without the command line: run the new `AutoHdr.exe`, then turn “Start with Windows” off and on again. The copy in Program Files gets updated

### Uninstall

1. In Settings, turn off “Start with Windows”. This removes the Task Scheduler task
2. Exit Auto HDR from the tray icon menu. If HDR is on because of a game, the app turns it off first
3. Delete `C:\Program Files\Auto HDR` and `%LOCALAPPDATA%\AutoHdr`

### Command line

| Command | What it does |
|---|---|
| `AutoHdr.exe --hdr status` | Shows the HDR state. Exit code: 0 — on, 1 — off, 2 — not supported, 3 — error |
| `AutoHdr.exe --hdr on`, `off`, `toggle` | Switches HDR on displays with auto HDR |
| `AutoHdr.exe --launch <command>` | Wrapper for launch options |
| `AutoHdr.exe --tray` | Starts without the window, with the tray icon only |
| `AutoHdr.exe --exit` | Closes the running copy |
| `AutoHdr.exe --install-task`, `--uninstall-task` | Turns autostart on or off. Turning it on copies the app to `C:\Program Files\Auto HDR` |
| `AutoHdr.exe --data <folder> …` | A separate settings folder, for tests. Must be the first argument |

## If SDR looks washed out in HDR mode

In HDR mode Windows shows SDR with the sRGB curve instead of gamma 2.2, which makes shadows look milky. Auto HDR avoids this by keeping HDR off outside games. If you want HDR on all the time, Windows HDR Calibration and the [gloam](https://github.com/halideworks/gloam) utility can help

## Build

Requires the .NET 10 SDK

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script builds `dist\AutoHdr.exe` (about 63 MB) and five WPF libraries next to it (`*_cor3.dll`). The build is self-contained: .NET is inside, so users do not need to install it. If the app is running, the script closes it first. If autostart is on, the script updates the copy in `C:\Program Files\Auto HDR`, and Windows asks to confirm this in a UAC prompt

## License

The source is available to read, but it is not open source. The license is [PolyForm Strict 1.0.0](LICENSE.md): you can use the app for free for noncommercial purposes. You may not redistribute the code or the app, change them or build your own versions on them. Get Auto HDR only from the [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases) page

## More

- [CLAUDE.md](CLAUDE.md): how the app works inside, decisions and pitfalls (in Russian)
- [PLAN.md](PLAN.md): the original plan, audits and test results (in Russian)
- [AGENTS.md](AGENTS.md): instructions for AI agents (in Russian)

## На русском

[Демо-видео на 90 секунд](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/download/v1.0.10/AutoHdr-demo.mp4), подписи на английском

Auto HDR — маленькое приложение для Windows 11, которое само включает HDR при запуске игры и возвращает SDR после выхода. На рабочем столе остаётся нормальный SDR, а HDR появляется только тогда, когда он нужен

**Как работает.** Приложение находит игры в Steam, GOG, Epic, EA app, Ubisoft Connect, Xbox, Rockstar и в ваших папках и проверяет поддержку HDR по Steam, PCGamingWiki и файлам игры. Раз в 0,25 с оно смотрит список запущенных процессов. Когда стартует игра с авто-HDR, приложение включает HDR на отмеченных мониторах, а после выхода из игры выключает его. HDR, включённый до игры, остаётся включённым

**Режим монитора.** Приложение может на время игры или любой программы переключать монитор в сохранённый режим изображения, например с local dimming. Нажмите «Настроить режим для игр» во вкладке «Мониторы» и по подсказкам переключите кнопками монитора обычный режим, режим для игр и снова обычный: приложение само найдёт команду, которая переключает режим. Затем отметьте «Режим монитора» у нужных игр. Нужен включённый DDC/CI в меню монитора. Если монитор не умеет переключать режимы с компьютера, настройка об этом скажет и ничего не сохранит

**Безопасность**

- Приложение меняет в Windows только переключатель HDR на отмеченных мониторах, тот же, что в «Параметры → Система → Дисплей», и режим изображения монитора, если вы настроили режим для игр. Разрешение, частоту, цветовые профили и настройки драйвера оно не трогает
- В игры ничего не внедряет, файлы игр только читает, реестр только читает
- В сеть ходит только в Steam Store и PCGamingWiki и передаёт номер игры в Steam или GOG либо её название. Ни телеметрии, ни аккаунтов, ни автообновлений. Проверку в интернете можно выключить в настройках
- Права администратора нужны только для «Ждать HDR». Чтобы не было окна UAC при каждом входе, приложение запускается через планировщик заданий из `C:\Program Files\Auto HDR`, где его не может подменить обычная программа

**Установка.** Скачайте `AutoHdr-<версия>-win-x64.zip` со страницы [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest) и распакуйте в любую папку. Ставить .NET не нужно. Программа не подписана, поэтому Windows может показать окно SmartScreen: нажмите «Подробнее», затем «Выполнить в любом случае». Во вкладке «Настройки» включите «Автозапуск при входе в Windows»

**Удаление.** Выключите автозапуск в настройках, закройте Auto HDR через меню значка в трее и удалите папки `C:\Program Files\Auto HDR` и `%LOCALAPPDATA%\AutoHdr`

**Лицензия.** Код открыт для чтения, но это не open source. [PolyForm Strict 1.0.0](LICENSE.md) разрешает бесплатно пользоваться программой в некоммерческих целях. Распространять код или программу, менять их и делать на их основе свои версии нельзя. Скачивайте Auto HDR только со страницы [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases)
