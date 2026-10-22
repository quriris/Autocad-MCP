# nanoCAD MCP — форк AutoCAD MCP Pro для nanoCAD

Форк [U-C4N/Autocad-MCP](https://github.com/U-C4N/Autocad-MCP), который управляет
запущенным **nanoCAD** (проверяется на Платформе nanoCAD 24.1) из Claude и других
MCP-клиентов. Все инструменты оригинала остаются; режим AutoCAD работает как раньше.

## Что изменено относительно оригинала

| Место | AutoCAD | nanoCAD |
|---|---|---|
| Подключение | `AutoCAD.Application` | `nanoCAD.Application` (режим `AUTOCAD_MCP_BACKEND=nanocad`) |
| Типы линий | `acadiso.lin` / `acad.lin` | те же имена, затем все `.lin` из путей поддержки nanoCAD |
| PDF-плоттер | `DWG To PDF.pc3` | PDF-устройство nanoCAD, найденное в списке устройств листа |
| Форматы бумаги | точное имя `ISO_A3_(420.00_x_297.00_MM)` | подбор по размерам, если nanoCAD называет формат иначе |
| Скриншот | окно «AutoCAD» | окно «nanoCAD» |

Код различий — `backends/cad_flavor.py`, тесты — `tests/test_nanocad_flavor.py`.
Режим ezdxf (DXF без запущенной программы) не менялся.

## Установка на Windows

Нужны Python 3.11+ и Git.

```bat
cd %USERPROFILE%\Documents
git clone https://github.com/quriris/Autocad-MCP.git nanocad-mcp
cd nanocad-mcp
python -m venv .venv
.venv\Scripts\python -m pip install -e ".[com,pdf,office]"
```

## Проверка на вашем nanoCAD

1. Запустите nanoCAD (достаточно пустого чертежа).
2. Выполните:

```bat
.venv\Scripts\python scripts\nanocad_probe.py
```

Скрипт создаёт **новый** чертёж, рисует в нём тестовые объекты, сохраняет DXF и PDF
во временную папку и закрывает этот чертёж без сохранения. Открытые вами файлы он
не трогает. Результат — `nanocad_probe_report.json` в папке проекта: по нему видно,
каких методов ActiveX нет в nanoCAD и какие шаги не прошли.

## Подключение к Claude Desktop

Файл `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "nanocad": {
      "command": "C:\\Users\\<вы>\\Documents\\nanocad-mcp\\.venv\\Scripts\\python.exe",
      "args": ["C:\\Users\\<вы>\\Documents\\nanocad-mcp\\server.py"],
      "env": {
        "AUTOCAD_MCP_BACKEND": "nanocad",
        "TOOL_PROFILE": "lean",
        "ALLOWED_PATHS": "C:\\Users\\<вы>\\Documents"
      }
    }
  }
}
```

После сохранения перезапустите Claude Desktop.

- `TOOL_PROFILE=lean` оставляет 84 основных инструмента черчения (вместе с СПДС) вместо 266.
  Чтобы получить все, уберите эту строку.
- `ALLOWED_PATHS` — папки, в которые серверу разрешено сохранять и из которых читать файлы.

## Настройки для nanoCAD

| Переменная | Зачем |
|---|---|
| `AUTOCAD_MCP_BACKEND=nanocad` | работать с nanoCAD; без него сервер ищет AutoCAD |
| `CAD_PROGID` | другой ProgID, например `nanoCAD.Application.24.1` |
| `CAD_LIN_FILE` | файл типов линий, который пробовать первым (например, ваш ГОСТ-овский `.lin`) |
| `CAD_PDF_DEVICE` | имя PDF-устройства, если автоподбор выбрал не то |

## Объекты СПДС (форматы, штамп, выноски, разрывы, отметки)

Объекты СПДС — это объекты MultiCAD. Через обычный COM nanoCAD показывает их как
безликие примитивы, поэтому для них есть плагин `nanocad_plugin/bin/McpSpds.dll`.
Он работает внутри nanoCAD через MultiCAD.NET, а сервер передаёт ему команды.

**Загрузка плагина.** Инструмент `spds_plugin_load` загружает его сам (NETLOAD).
Вручную: в nanoCAD команда `NETLOAD` → выбрать `nanocad_plugin\bin\McpSpds.dll`.
Чтобы плагин загружался при каждом запуске, добавьте его в автозагрузку
(команда `APPLOAD` / «Загрузить приложения» → «Автозагрузка»). Если nanoCAD
спросит, доверять ли приложению, разрешите или добавьте папку в доверенные.

**Инструменты** (группа `spds`, нужен `AUTOCAD_MCP_BACKEND=nanocad`):

| Инструмент | Что делает |
|---|---|
| `spds_status` | плагин загружен и отвечает? |
| `spds_plugin_load` | загрузить плагин в запущенный nanoCAD |
| `spds_list` | все объекты СПДС чертежа: handle, класс, слой, габариты |
| `spds_get` | все параметры объектов (поля штампа, значение отметки...) |
| `spds_set` | изменить параметры по имени (заполнить штамп, задать отметку) |
| `spds_format_create` | формат A0–A5 со штампом (СПДС/ГОСТ), масштаб, лист |
| `spds_leader_create` | позиционная или многослойная выноска |
| `spds_break_create` | линия обрыва (прямая, двойная, волнистая, трубная) |
| `spds_clone` | копия объекта СПДС со сдвигом и новыми параметрами |
| `spds_level_mark_create` | отметка уровня в точке; значение берётся из высоты или задаётся (`value=3` → «+3,000»), плюс комментарий |
| `spds_stamp_text_height` | высота текста ячейки штампа (как окно «Настройка текста»); по умолчанию адрес → 2,5 мм. Формат пересобирается и получает новый handle |
| `spds_create` | новый объект СПДС любого класса в точке — по образцу (`like_handle`) или `class_id`; так ставятся отметки уровня |
| `spds_axis_create` | строительная ось с маркером (настоящий объект СПДС, а не линия с кружком) |
| `spds_axis_grid_create` | массив осей: шаги по X и Y, имена (1, 2, 3… и А, Б, В… по ГОСТ 21.101), цепные и габаритные размеры |
| `spds_classes` | каталог всех классов объектов, которые умеет создавать nanoCAD, с их свойствами |
| `spds_native_classes` | классы, у которых нет .NET-обёртки (отметка уровня и др.), с их `class_id` для `spds_create` |
| `spds_object_create` | любой объект по имени класса: разрез, вид, узел, цепная/гребенчатая/узловая выноска, помещение, стена, окно, дверь, лестница, колонна, таблица, шероховатость, сварка… |
| `spds_invoke` | вызвать метод или изменить член уже созданного объекта (шаг осей, текст маркера, точки разреза) |

Имена параметров (например, полей штампа) у разных объектов разные: сначала
`spds_get` на объекте, потом эти имена в `spds_set` или `props`.

**Любой объект СПДС.** Для большинства объектов в MultiCAD.NET есть класс;
плагин достаёт их все через отражение, поэтому отдельный инструмент на каждый
не нужен. Порядок: `spds_classes(filter="Section")` — какие у класса свойства;
`spds_object_create(class_name="McSectionVS", init={...}, steps=[...])` —
`init` задаётся до добавления в чертёж (геометрия), `steps` — после:
`{"set": "путь", "value": ...}`, `{"call": "путь.Метод", "args": [...]}`,
`{"get": "путь"}`. Точки — `[x, y]`, перечисления — по имени (достаточно
хвоста после `_`), объекты — по handle. Например, линии выноски:
`{"call": "Leader.AddSegment", "args": [[x0, y0], [x1, y1]]}`.

**Штамп.** Основные поля — аргументы `spds_format_create`: `designation`
(Designation), `object_name` (ComplexName), `building` (DrawingName1), `title`
(Drawing type), `organization` (Enterprise), `stage` (Stage), `author` (Author),
`sheet`/`sheet_count`. Должности — `Add.text*`, фамилии — `Control`,
`Add.subject*`. Новый формат nanoCAD заполняет данными штампа из чертежа
(обозначение, объект, организация, фамилии — как на уже стоящих форматах);
переданные поля записываются поверх них. Текст адреса (ComplexName) ставится
высотой `address_text_height` — по умолчанию 2,5 мм вместо 3,5 мм nanoCAD.

**Масштаб.** Новые объекты берут масштаб чертежа (в шаблоне СПДС — 1:100);
передавайте `scale`, если нужен другой (форматы на ваших схемах — `scale=1`).

**Проверка:** откройте в nanoCAD СПДС одну из схем и выполните
`.venv\Scripts\python scripts\nanocad_spds_smoke.py`. Скрипт читает объекты
открытого чертежа (ничего в нём не меняя), а создаёт объекты в новом чертеже и
закрывает его без сохранения. Отчёт: `nanocad_spds_smoke_report.json`.

**Сборка плагина** (если нужно пересобрать): на Windows —
`dotnet build -c Release nanocad_plugin\McpSpds\McpSpds.csproj`; без NuGet —
`nanocad_plugin/build.sh` (см. комментарий в файле).
