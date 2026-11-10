# Ускорение игр Paradox — установщик

`release/Uskorenie_Paradox.exe` — один файл, ~90 КБ (WPF). Нужен только .NET Framework 4.8, он есть в Windows 10/11.

![окно](docs/launcher_eu4_country.png)

## Что делает

- Показывает все игры из каталога. У каждой игры свой набор плагинов; если плагинов пока нет, игра помечена «скоро».
- Сам находит каждую игру через реестр Steam, `libraryfolders.vdf` и `appmanifest_<appid>.acf`. Папку можно указать вручную, выбор запоминается.
- Проверяет версию игры по SHA-256 exe.
- Скачивает описание плагинов и архив, сверяет контрольные суммы.
- Три режима: «Все плагины», «Только точные», «Выборочно».
- Для каждого плагина показывает описание и его настройки.
- Записывает ini, сохраняя комментарии и настройки пользователя при обновлении.
- Убирает отключённые плагины. Чужой `VERSION.dll`/`d3d9.dll` сохраняет и возвращает при удалении.
- Кнопка «Проверить работу» читает журналы плагинов после запуска игры.

## Как устроены файлы

| Файл | Что это |
|---|---|
| `catalog_src.json` | Список игр: id, название, Steam appid, путь к exe, имя процесса, цвет, статус (`available` или `planned`). |
| `games/<id>/manifest_src.json` | Плагины одной игры: файлы, ini, описания, настройки, значения для «Только точные». |
| `tools/make_release.py` | Собирает `release/<id>_plugins.zip`, `release/<id>_manifest.json` (с контрольными суммами) и `release/catalog.json`. |
| `tools/build.py` | Собирает exe компилятором .NET Framework (`src/Launcher.cs` + вшитый `src/MainWindow.xaml`). |

## Как выпустить плагины

1. Обновите `version` и описания в `games/<id>/manifest_src.json`.
2. Соберите файлы релиза из папки, которая копируется в игру:
   `python tools/make_release.py eu4 "..\eu4_package\EU4_uskorenie_1.37.5_v3\2_V_PAPKU_IGRY"`
3. Загрузите в один GitHub Release все файлы `release/*.json` и `release/*_plugins.zip`.
   Установщик всегда берёт их из `releases/latest/download/…`, поэтому раздавать новый exe не нужно.

## Как добавить новую игру

1. В `catalog_src.json` у игры поставьте `"status": "available"` и `"manifest": "<id>_manifest.json"`.
2. Создайте `games/<id>/manifest_src.json` по образцу EU4.
3. Выполните `make_release.py <id> <папка>`.

## Один раз перед публикацией

Впишите `OWNER/REPO` в `Config.DefaultSource` (`src/Launcher.cs`) и выполните `python tools/build.py`.

## Параметры командной строки

| Параметр | Что делает |
|---|---|
| `--source <url или папка>` | Другой источник каталога; например, локальная папка `release`. |
| `--game <id>` | Какую игру открыть. |
| `--dir <папка>` | Папка этой игры. |
| `--auto all\|exact\|uninstall\|check\|shot[:плагин[:log]]` | Сделать одно действие без участия пользователя и записать `launcher_auto.log` рядом с exe. |

## Лицензия

MIT, см. [LICENSE](LICENSE). Плагины, которые раздаёт установщик, идут вместе со своими лицензиями (Bellows, Hamweard, EU4 Unicode Patch, DXVK).

Проект не связан с Paradox Interactive.
