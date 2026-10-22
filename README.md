# Auto HDR

Маленькое приложение для Windows 11, которое само включает HDR при запуске игры и возвращает SDR после выхода. На рабочем столе остаётся нормальный SDR, а HDR появляется только тогда, когда он нужен

Интерфейс на русском и английском. Язык берётся из Windows, сменить его можно во вкладке «Настройки»

[English below](#english) · [Скачать](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest)

## Как это работает

1. Приложение находит установленные игры в Steam, GOG, Epic, EA app, Ubisoft Connect, Xbox и Rockstar, а также в папках, которые вы добавили сами
2. Для каждой игры проверяет поддержку HDR по трём источникам:
   - **Steam** — метка «HDR available» на странице игры
   - **PCGamingWiki** — поле HDR в таблице видеонастроек
   - **файлы игры** — упоминания настроек HDR внутри exe
3. Если Steam или PCGamingWiki подтверждают HDR, у игры сразу включается **авто-HDR**. У остальных он выключен, включить можно одним флажком
4. Когда запускается exe из папки игры с авто-HDR, приложение включает HDR на отмеченных мониторах. Когда закрываются все процессы игры, HDR выключается. Если HDR мигает при запуске игры через лаунчер, включите в настройках задержку перед выключением (по умолчанию 5 с)
5. Если HDR уже был включён вручную, приложение его не выключает

| Статус HDR | Значение | Авто-HDR по умолчанию |
|---|---|---|
| Есть | Подтверждено Steam или PCGamingWiki | Включён |
| Возможно | HDR включается модом, или в файлах игры есть настройки HDR | Выключен |
| Нет | PCGamingWiki: HDR не поддерживается | Выключен |
| Неизвестно | Данных нет | Выключен |

Ручная настройка флажка важнее автоматической и не сбрасывается при повторной проверке. Сбросить её можно в меню по правому клику на игре

## Установка

1. Скачайте `AutoHdr-<версия>-win-x64.zip` со страницы [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest) и распакуйте в любую папку. Ставить .NET не нужно: всё нужное уже внутри
2. Запустите `AutoHdr.exe`. Программа не подписана, поэтому Windows может показать окно SmartScreen. Нажмите «Подробнее», затем «Выполнить в любом случае»
3. Во вкладке «Настройки» включите «Автозапуск при входе в Windows». Приложение скопирует себя в `C:\Program Files\Auto HDR` и будет стартовать оттуда через планировщик заданий, с правами администратора и без окна UAC. То же самое делает команда `AutoHdr.exe --install-task`

После этого папку, куда вы распаковали архив, можно удалить. Чтобы обновиться, закройте Auto HDR через меню значка в трее, распакуйте новый архив и выполните в его папке `AutoHdr.exe --install-task`. Можно и без командной строки: запустите новый `AutoHdr.exe`, выключите и снова включите автозапуск. Копия в Program Files обновится

Копия в Program Files нужна для безопасности: приложение работает с правами администратора, поэтому его exe должен лежать там, где его не может подменить обычная программа

Окно закрывается в трей. Значок показывает режим: **HDR** или **SDR**. По правому клику — ручное переключение HDR, пауза автопереключения и выход

## Мониторы

HDR переключается только на мониторах с отмеченным «Авто-HDR». Флажок включается сам только у первого найденного физического HDR-монитора. Телевизоры (в названии есть «TV», «BRAVIA» или марка телевизора), виртуальные дисплеи (Parsec, SudoMaker) и все мониторы, подключённые позже, добавляются с выключенным флажком

## «Ждать HDR»

Некоторые игры, например Cyberpunk 2077, проверяют HDR в первые секунды и прячут настройку, если HDR ещё не включён. Флажок «Ждать HDR» приостанавливает процесс игры, пока HDR не включится (обычно это доля секунды)

Не включайте его для онлайн-игр с античитом: античит может принять приостановку процесса за вмешательство

Запасной вариант для Steam — параметры запуска игры (Свойства → Параметры запуска):

```
"C:\Program Files\Auto HDR\AutoHdr.exe" --launch %command%
```

Тогда HDR включается строго до старта игры, а выключается после закрытия всех её процессов. Если автопереключение на паузе, игра запустится без HDR. Готовая строка с правильным путём есть во вкладке «Настройки». Если автозапуск ни разу не включали, в ней будет путь к запущенному `AutoHdr.exe`

## Командная строка

| Команда | Что делает |
|---|---|
| `AutoHdr.exe --hdr status` | Показывает состояние HDR. Код выхода: 0 — включён, 1 — выключен, 2 — не поддерживается, 3 — ошибка |
| `AutoHdr.exe --hdr on`, `off`, `toggle` | Переключает HDR на мониторах с авто-HDR |
| `AutoHdr.exe --launch <команда>` | Обёртка для параметров запуска |
| `AutoHdr.exe --tray` | Запуск без окна, только значок в трее |
| `AutoHdr.exe --exit` | Закрывает работающую копию |
| `AutoHdr.exe --install-task`, `--uninstall-task` | Включает или выключает автозапуск. При включении копирует exe в `C:\Program Files\Auto HDR` |
| `AutoHdr.exe --data <папка> …` | Отдельная папка настроек, для тестов. Ставится первым аргументом |

## Данные

Настройки, список игр, мониторы и журнал лежат в `%LOCALAPPDATA%\AutoHdr`. Журнал также виден во вкладке «Журнал»

## Сборка

Нужен .NET 10 SDK

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Скрипт собирает `dist\AutoHdr.exe` (около 63 МБ) и пять библиотек WPF рядом с ним (`*_cor3.dll`). Сборка self-contained: .NET уже внутри, на компьютере пользователя его ставить не нужно. Если приложение запущено, скрипт сначала закрывает его. Если автозапуск включён, скрипт обновляет копию в `C:\Program Files\Auto HDR`: Windows попросит подтвердить это в окне UAC

## Если SDR в режиме HDR выглядит мутным

Windows показывает SDR в HDR-режиме по кривой sRGB, а не по гамме 2.2, отсюда «молочные» тени. Auto HDR решает это тем, что держит HDR выключенным вне игр. Если хочется оставлять HDR включённым всегда, помогут Windows HDR Calibration и утилита [gloam](https://github.com/halideworks/gloam)

## Лицензия

Код открыт для чтения, но это не open source. Лицензия — [PolyForm Strict 1.0.0](LICENSE.md): программой можно бесплатно пользоваться в некоммерческих целях. Распространять код или программу, менять их и делать на их основе свои версии нельзя. Скачивайте Auto HDR только со страницы [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases)

## Подробнее

- [CLAUDE.md](CLAUDE.md) — устройство приложения, решения и подводные камни
- [PLAN.md](PLAN.md) — исходный план и список доработок
- [AGENTS.md](AGENTS.md) — инструкции для ИИ-агентов

## English

Auto HDR is a small Windows 11 tray app that turns HDR on when a game starts and back to SDR when it exits. The desktop stays in proper SDR, and HDR shows up only when you need it

- Finds games in Steam, GOG, Epic, EA app, Ubisoft Connect, Xbox, Rockstar and your own folders
- Checks HDR support with Steam, PCGamingWiki and the game files. Auto HDR is on only for games that Steam or PCGamingWiki confirm, and you can change it for any game
- Switches HDR only on the displays you check. TVs, virtual displays and displays connected later start with auto HDR off
- “Wait for HDR” holds a game at launch until HDR is on, for games that look for HDR only in the first seconds. Do not use it with online games that have anti-cheat

The interface follows the Windows display language. You can switch between English and Russian in Settings

**Install.** Download `AutoHdr-<version>-win-x64.zip` from [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases/latest) and extract it anywhere. No .NET install is needed. The app is not signed, so SmartScreen may warn you: click “More info”, then “Run anyway”. Turn on “Start with Windows” in Settings: the app copies itself to `C:\Program Files\Auto HDR` and starts from there through Task Scheduler, with administrator rights and without a UAC prompt

**License.** The source is available to read, but it is not open source. [PolyForm Strict 1.0.0](LICENSE.md) lets you use the app for free for noncommercial purposes. You may not redistribute the code or the app, change them or build your own versions on them. Get Auto HDR only from the [Releases](https://github.com/TatsukiJane/Auto-HDR-Enabler/releases) page
