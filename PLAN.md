# PLAN.md: Экспорт в IFC (IvanTools)

Плагин `ExportToIfc` для Autodesk Revit 2023: экспорт **активного 3D-вида** в IFC теми пресетами, которые нужны в работе, плюс автоматический отчёт о результате. База — **штатный экспортёр Revit** (`Document.Export` + `IFCExportOptions`); своего IFC-писателя нет.

> Связанные документы: `ExportToGlb_PLAN.md` (экспорт GLB), `AdjustToFace_PLAN.md` (завершённая задача).

---

## 1. Цель и границы

Два реальных сценария:

1. **Передача конструкторам в Tekla** — нужны целые элементы, классы, уровни, объёмы; цвет вторичен.
2. **Показ коллегам** (Online3DViewer) — нужен максимально лёгкий файл и предсказуемое содержимое.

Общее правило: **в IFC попадает ровно то, что видно на виде** (скрытые категории и элементы, фильтры вида, фаза вида, шаблон вида). Это обеспечивается `IFCExportOptions.FilterViewId = view3d.Id`.

IFC рассматривается как **«запекание»** модели: параметрическая передача (IFC4 DTV) не используется — она даёт лишнюю сложность и не нужна ни Текле, ни показу.

**Осознанные ограничения (non-goals):**

* нет фильтрации/обрезки по Section Box: элементы целые, частично выходящие за грани куба — нормальное поведение IFC;
* нет своего IFC-писателя (схема/геометрия/GUID — забота Revit);
* нет IFC4 DTV и IFC4x3 (в enum `IFCVersion` Revit 2023 `IFC4x3` отсутствует);
* нет экспорта GLB из этого плагина (для этого есть `ExportToGlb`);
* нет 2D-аннотаций, IFCXML/IFCZIP (можно добавить позже отдельным решением).

---

## 2. Проверенные факты API Revit 2023

Проверено рефлексией по `C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll` и `RevitAPI.xml`:

| Факт | Значение |
|---|---|
| Точка входа | `Boolean Document.Export(string folder, string name, IFCExportOptions options)` |
| `IFCExportOptions.FileVersion` | `IFCVersion`: `IFC2x3CV2`, `IFC2x3`, `IFC2x3FM`, `IFC2x3BFM`, **`IFC4`**, **`IFC4RV`**, **`IFC4DTV`**, `IFCBCA`, `IFCCOBIE`, `IFC2x2` |
| `IFCExportOptions.FilterViewId` | «Id of the view whose **visibility settings will govern the contents** in the exported IFC file»; `InvalidElementId` = без фильтрации |
| `IFCExportOptions.ExportBaseQuantities` | «Option to export IFC standard quantities» (по умолчанию в API `false`) |
| `IFCExportOptions.WallAndColumnSplitting` | «Option to allow division of multi-level walls and columns by levels» (по умолчанию `false`) |
| `IFCExportOptions.FamilyMappingFile` | путь к таблице маппинга семейств; пустая строка = текущая таблица Revit |
| `IFCExportOptions.AddOption(key, value)` | доступ к недокументированным опциям (tessellation, property sets и т.п.) — **ключи проверять экспериментом** |
| `Document.Export` (IFC) | **требует открытой транзакции**: экспортёр пишет в документ (IFC GUID, служебные параметры). Без `Transaction` падает с `Autodesk.Revit.Exceptions.ModificationOutsideTransaction` («Modifying is forbidden because the document has no open transaction») |
| Имя команды в UI | Revit и `CsHotLoader` показывают **полное имя класса** (`IvanTools.ExportToIfc`); суффикс `Command` в имени класса — лишний, поэтому классы называются без него |
| Файлы Revit на диске | `Revit.IFC.Export.dll`, `IntfIFC.dll`, `IFCCertifiedEntitiesAndPSets.json`, конфиги `IFC4_22.12_16.txexp` / `IFC2X3_22.12_16.txexp`, XSD `IFC4.xsd`, `IFC2X3_TC1.xsd` |
| `RevitAPIIFC.dll` | низкоуровневый API (`IFCFile`, `ExporterIFC`) существует, но **не используется**: обёртка над `Export` дешевле и совместимее |

---

## 3. Пресеты

Классические BIM-виды IFC плюс разумные дефолты — пресет это только набор начальных значений, любая галочка потом переопределяется.

| Пресет | `IFCVersion` | QTO | LOD | Для чего |
|---|---|---|---|---|
| **IFC4 DTV — передача проекта** — по умолчанию | `IFC4DTV` | on | Medium | Design Transfer View: полная параметрика, обмен с Tekla/ArchiCAD; файл самый большой |
| **IFC4 RV — референс** | `IFC4RV` | off | Low | Reference View: лёгкая геометрия, показ и координация |
| **IFC2x3 CV2 — координация** | `IFC2x3CV2` | on | — | Coordination View 2.0: для проектов, где принят IFC2x3 (в 2x3 **нет** стадий) |

Что важно понимать:

* **RV не теряет семантику**: классы (`IfcWall`, `IfcSlab`…), уровни/`IfcBuildingStorey`, типы и property sets остаются — облегчается **только геометрия**;
* **DTV** передаёт параметрику (в т.ч. вырезы/сложные представления) — подходит для обмена в проектировании, но файл больше и часть просмотрщиков читает его хуже;
* **CV2** — самый совместимый вариант «для старых»; «лёгкий 2x3» = CV2 со снятым QTO (отдельного пресета нет);
* маппинг — ревитовский (`FamilyMappingFile` пуст ⇒ штатная таблица), своя таблица подключается опционально;
* `TessellationLevelOfDetail` отправляется только для IFC4 (для 2x3 ключ не действует и не передаётся).

Размер файла: RV < DTV, CV2 зависит от модели. Фактические цифры по вашей модели — в отчёте (см. §7), вносятся по мере накопления.

---

## 4. Область экспорта: «что видно на виде»

* Всегда `FilterViewId = активный View3D`. Других режимов нет — так исключается «экспортировал не то».
* Проверка перед запуском: активный вид — `View3D` и не `IsTemplate`.
* Revit при экспорте вида учитывает скрытые категории/элементы, фильтры и переопределения видимости, фазу вида, шаблон вида. Наша задача — не мешать и проверить результат отчётом.
* **Section Box**: элементы не режутся; если куб активен, элементы вне куба в файл не попадут, а частично видимые — попадут целиком. Это ожидаемо и фиксируется как норма.
* **Связи (Revit Links)**: поведение определяется настройками экспорта Revit; проверяется в Этапе 0 (в отчёте будет видно по числу сущностей/классов).

---

## 5. UI

Один модальный WinForms-диалог с **вкладками** (максимальный набор опций не влезает в одну простыню; `TaskDialog` для этого непригоден):

```text
┌ Экспорт активного 3D-вида в IFC ────────────────────────────────┐
│ [Пресет и объёмы] [Содержимое и геометрия] [PSets и конфиг]      │
│ ┌ Пресет (классические BIM-виды) ────────────────────────────┐  │
│ │ (•) IFC4 DTV — передача проекта (полная параметрика)       │  │
│ │ ( ) IFC4 RV — референс (лёгкая геометрия)                  │  │
│ │ ( ) IFC2x3 CV2 — координация (для проектов на 2x3)         │  │
│ └────────────────────────────────────────────────────────────┘  │
│ [✓] Базовые объёмы (QTO): длины, площади и объёмы по IFC        │
│ [ ] Разрезать стены и колонны по уровням                        │
│ Границы помещений: [0 — не экспортировать ▾]                    │
│ ...                                                             │
│                                    [ Экспорт ] [ Отмена ]       │
└─────────────────────────────────────────────────────────────────┘
```

* `AutoScaleMode = Font`, размеры под 125–150% DPI (раньше подписи «зажимало» из-за жёстких высот);
* **вкладка 1**: 3 пресета, QTO, Split, space boundaries (0/1/2 — свойство `SpaceBoundaryLevel`);
* **вкладка 2**: LOD (Low/Medium/High/не задано), linked files, 2D-элементы, только bounding box, помещения, фаза, отметка площадки, имя семейства+типа;
* **вкладка 3**: PSets Revit / общие IFC / пользовательские (+ путь), расписания как PSets (+ имена), Store IFC GUID, `debug_keys`, файл маппинга, блок конфигурации (путь, «Загрузить…», «Сохранить как…», галочка, «Обзор…»);
* **паритет GUI ↔ `.ini`**: каждый элемент диалога = ключ (§12), «накликать» и «написать» дают одно состояние;
* выбор пресета ставит его дефолты (QTO, Split=false, LOD); порядок применения: **пресет → дефолты → ключи файла**;
* «Сохранить как…» пишет файл сразу и делает его текущим; галочка автосохранения включается сама, только если файл уже есть;
* если указан файл маппинга и его нет на диске — предупреждение и возврат в диалог.

---

## 6. Архитектура файла `ExportToIfc.cs`

| Тип | Назначение |
|---|---|
| `ExportToIfc` | `IExternalCommand`: проверки, диалог, `SaveFileDialog`, вызов экспорта, отчёт |
| `IfcExportPreset` | имя, описание, `IFCVersion`, дефолты QTO/Split |
| `IfcExportSettings` | выбранные настройки (пресет, флаги, путь маппинга) + конфиг: `SaveToFile`, `ConfigInfo/ConfigStatus`, `ConfigErrors`, `ExtraOptions` (AddOption), `AddOptionFailures` |
| `IfcConfigFile` | чтение/запись `IvanTools.ini` (§12): поиск, парсер, merge-запись |
| `IfcExportDialog : Form` | интерфейс из §5 (без дизайнера, кодом) |
| `IfcStepSummary` | валидатор/статистика полученного STEP-файла |
| `IfcExportLog` | лог прогона в `%TEMP%\ExportToIfc.log` |

Ядро экспорта:

```csharp
// IFC-экспорт пишет в документ (IFC GUID) -> нужна открытая транзакция
using (Transaction transaction = new Transaction(doc, "Экспорт IFC"))
{
    transaction.Start();
    using (IFCExportOptions options = new IFCExportOptions())
    {
        options.FileVersion = settings.Preset.Version;
        options.FilterViewId = view3d.Id;
        options.ExportBaseQuantities = settings.BaseQuantities;
        options.WallAndColumnSplitting = settings.SplitWallsByLevel;
        if (!string.IsNullOrEmpty(settings.MappingFile))
            options.FamilyMappingFile = settings.MappingFile;

        // ключи из конфига, которых нет в диалоге: по одному, с локальным try/catch
        foreach (KeyValuePair<string, string> extra in settings.ExtraOptions)
            options.AddOption(extra.Key, extra.Value);

        ok = doc.Export(folder, baseName, options);   // имя без расширения
    }
    transaction.Commit();
}
```

Свойства задаются **до** `AddOption`; ошибка `AddOption` не прерывает экспорт — пара попадает в `AddOptionFailures` и в предупреждения отчёта. При исключении транзакция откатывается (`RollBack`), пользователю показывается `ex.Message` и путь к логу, полный стек — только в лог.

Перед `Start()` на транзакцию ставится `IFailuresPreprocessor` (`IfcWarningsSilencer`): предупреждения экспортёра («элемент без IFC-класса» и т.п.) удаляются и не показываются модальным окном; **ошибки не разрешаются** — они должны дойти до пользователя.

После экспорта — поиск фактического файла (`{baseName}.ifc`, `{baseName}.IFC`, `{baseName}`), проверка `ok`, размера и содержимого.

Требования к коду: C# 5, один `IExternalCommand`, без внешних DLL (совместимость с `CsHotLoader`), UTF-8 с BOM, кириллица только в строках.

---

## 7. Валидатор STEP (`IfcStepSummary`)

Читает файл **потоком** (построчно, без загрузки целиком — файлы бывают 100+ МБ):

1. Заголовок: `FILE_SCHEMA(('IFC4'|'IFC2X3'))`, **`FILE_DESCRIPTION` → `ViewDefinition [ReferenceView_V1.2]`** (проверено на реальном файле: RV/DTV различаются именно здесь, а не в схеме), `FILE_NAME`.
2. Счётчики сущностей: разбор строк вида `#123=IFCWALL('...` — имя сущности до `(`; подсчёт всех типов + `TotalEntities`.
3. Явно вытаскиваются: `IFCWALL`, `IFCWALLSTANDARDCASE`, `IFCSLAB`, `IFCCOLUMN`, `IFCBEAM`, `IFCMEMBER`, `IFCPLATE`, `IFCDOOR`, `IFCWINDOW`, `IFCSTAIR`, `IFCROOF`, `IFCCOVERING`, `IFCBUILDINGELEMENTPROXY`, `IFCBUILDINGSTOREY`, `IFCSITE`, `IFCBUILDING`, `IFCPROJECT`, `IFCSURFACESTYLE`, `IFCSTYLEDITEM`, `IFCPROPERTYSET`, `IFCELEMENTQUANTITY`, `IFCRELCONTAINEDINSPATIALSTRUCTURE`.
4. Стили: если `IFCSTYLEDITEM` = 0 — отдельное предупреждение «цвета/материалы в файл не попали».

Отчёт — **свой WinForms-диалог**, а не `TaskDialog`: текст в `TextBox` `ReadOnly/Multiline` (Consolas) выделяется мышью и копируется `Ctrl+A`/`Ctrl+C`, есть кнопки **«Копировать»**, **«Открыть папку»**, **«Закрыть»**. `TaskDialog` Revit не позволяет выделять текст — это ограничение Autodesk, не плагина.

```text
Файл: D:\project\outgoing\SE003_canopy.ifc
Размер: 6.55 МБ (6 868 346 Б)
Пресет: IFC4 RV — референс — Reference View: лёгкая геометрия
Конфиг: D:\project\IvanTools.ini
Схема: IFC4
View: ReferenceView_V1.2
Сборка: ...\IvanTools\ExportToIfc.dll (16.11.2026 12:27)
Время: 51.2 с

Сущностей: 89 710
Объекты:
  Стены: 21    Плиты: 9    Колонны: 12    Балки: 22
  Двери: 4     Окна: 0    Лестницы: 1    Кровля: 10
  Покрытия: 0  Proxy: 825  Уровни: 4
  Сайт: есть   Здание: есть   Проект: есть

PSets: 9150   QTO: 0   Стили: 27 (StyledItem: 6566)
AddOption: 6 (TessellationLevelOfDetail=Low, ExportLinkedFiles=false, …)

Предупреждения (3):
  • QTO выключен: объёмов/площадей в файле нет (включается пресетами DTV/CV2)
  • Proxy: 825 из 899 объектов — проверьте таблицу маппинга семейств
  • Стены есть, но IfcWallStandardCase = 0 — у стен нет типа/стандартной геометрии
```

Предупреждения (правила): схема не соответствует пресету; `IFCPROJECT`/`IFCBUILDING`/`IFCBUILDINGSTOREY` отсутствуют; 0 стен и 0 плит; 0 стилей; размер < 1 КБ; экспорт вернул `false`; QTO включён, но наборов 0; **доля proxy > 20%**; **`IFCWALLSTANDARDCASE = 0` при непустых стенах**; DTV-предупреждение «файл больше»; замечания конфига (§12) с номерами строк; непринятые ключи `AddOption`. Строка `Сборка:` показывает, какая именно сборка выполнялась — важно при отладке через `CsHotLoader`.

---

## 8. Этап 0 — эксперименты до/во время реализации

1. `FilterViewId`: скрытые элементы и категории, фильтры вида, шаблон вида, фаза вида — что именно попадает в файл. Отдельно — активный Section Box (элементы вне куба).
2. Три пресета на одной модели: размер файла, число сущностей, число стилей и QTO. Цифры → в §3.
3. `AddOption`: **API подтверждён рефлексией** — `void AddOption(String name, String value)` в `IFCExportOptions` (Revit 2023). Имена ключей проверяются режимом **`debug_keys = true`** (реализован): плагин прогоняет список кандидатов в пробном `IFCExportOptions` и пишет в лог/отчёт «ключ — без исключения / тип исключения». Результат переносится в таблицу §12; непринятые галочки из GUI убираются.
4. Лог экспорта Revit: где лежит, формат, есть ли ошибки/предупреждения для отчёта.
5. Поведение `Export`: имя с расширением/без, перезапись существующего файла, поведение при несохранённом документе, экспорт связей.
6. Цвета: сравнение `IFCSTYLEDITEM`/`IFCSURFACESTYLE` для IFC4 и IFC2x3 на одной и той же модели.

---

## 9. Этапы реализации

* **Этап 1 (каркас).** `ExportToIfc` + `IfcExportDialog` + `IfcExportPreset/Settings`; экспорт через `doc.Export` с `FilterViewId`; поиск фактического файла; минимальный отчёт (размер, время, схема, всего сущностей); лог. Проверка: файл создаётся, три пресета дают три разных результата, ничего не падает на несохранённом документе.
* **Этап 2 (диагностика).** `IfcStepSummary`: счётчики по типам, стили, PSets/QTO, проверки и предупреждения; полный отчёт в стиле GLB-плагина. Проверка: отчёт совпадает с ручным подсчётом по файлу (grep).
* **Этап 3 (сделан).** Пресеты IFC4 DTV / IFC4 RV / IFC2x3 CV2; диалог на трёх вкладках со всем набором галочек; `space_boundaries`; режим `debug_keys`; копируемый отчёт (`IfcReportDialog`); `ViewDefinition` из `FILE_DESCRIPTION`; предупреждения proxy/WallStandardCase/QTO; строка «Сборка» в отчёте и логе; `.ini` — 21 ключ с паритетом GUI и merge-записью. Осталось: прогон `debug_keys = true` и перенос фактических имён ключей в §12.4.
* **Этап 4 (по желанию).** Батч по нескольким 3D-видам; IFCZIP; экспорт нескольких пресетов за один запуск.

---

## 10. Сборка и развёртывание

```powershell
csc /nologo /warn:4 /target:library /out:"$env:TEMP\ExportToIfc_check.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPIUI.dll" `
    /reference:System.Windows.Forms.dll ExportToIfc.cs
```

Ожидается 0 ошибок / 0 предупреждений. Развёртывание — как у остальных плагинов:

```text
C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
  ├── ExportToIfc.cs       (для CsHotLoader → Run Script)
  ├── ExportToIfc.dll
  └── ExportToIfc.png
IvanTools.addin            (блок <AddIn>, новый уникальный AddInId)
```

Класс: `IvanTools.ExportToIfc`. После правки манифеста нужен перезапуск Revit; через HotLoader — без перезапуска.

---

## 11. Ограничения и риски

* **IFC2x3 и стадии**: схема не поддерживает фазы; если нужны стадии — только IFC4 (и то большинство просмотрщиков `IfcTask` игнорируют, но набор элементов будет соответствовать фазе вида).
* **Цвета в просмотрщике**: Online3DViewer рендерит IFC через web-ifc (Phong-материалы), поэтому IFC и GLB всегда будут выглядеть по-разному; «цвета отлетели» сначала проверяем по счётчику `IFCSTYLEDITEM` в отчёте (экспорт vs просмотрщик).
* **Нет прогресса**: `Document.Export` синхронный, колбэков нет; на больших моделях — минуты. Плагин не блокирует UI, но и прогресс показать не может.
* **Связи**: экспорт линков управляется настройками Revit; поведение проверяется на реальной модели (Этап 0).
* **Mapping**: неверная таблица даёт неверные классы (`IfcBuildingElementProxy` вместо `IfcBeam`) — отчёт покажет распределение по типам, это лучший индикатор.
* **Размер**: IFC с QTO и полной геометрией может быть в разы больше GLB; для показа использовать пресет RV.
* **Конфиг в сетевой папке / OneDrive**: `IvanTools.ini` лежит рядом с `.rvt`, поэтому может быть доступен только для чтения, конфликтовать при синхронизации или попадать в шаринг. Запись необязательна (галочка) и никогда не отменяет экспорт; при сбое в отчёте появляется «не записано: …». Для `mapping_file` сетевой путь допустим, но проверяется на существование до экспорта.

---

## 12. Конфигурация (IvanTools.ini)

Штатный IFC Export Setup («Modify setup…») хранится **внутри `.rvt`** и текстом наружу не выгружается; файлы `IFC4_22.12_16.txexp` в папке Revit — это PE-модули экспортёра, а не конфиги. Поэтому настройки экспорта выносятся в текстовый файл рядом с проектом: его можно править руками, хранить в git и передавать вместе с моделью.

### 12.1. Имя, расположение, порядок поиска

1. `<папка .rvt>\IvanTools.ini` — настройки проекта (приоритет);
2. `%APPDATA%\IvanTools\IvanTools.ini` — личные дефолты;
3. встроенные значения плагина.

Если документ не сохранён (`doc.PathName` пуст), шаг 1 пропускается: писать в папку, которую пользователь не выбирал, нельзя. Один файл — все плагины: секции `[ifc]`, позже `[glb]`.

### 12.2. Синтаксис

* UTF-8; BOM при чтении допускается (в том числе задвоенный), при записи не пишется — `new UTF8Encoding(false)`;
* пустая строка и строка, начинающаяся с `;` или `#`, — комментарий;
* `key = value`: ключ — до первого `=`, значение — остаток строки с `Trim()`;
* ключи регистронезависимы; дубликат — побеждает последний, в замечания идёт предупреждение с номером строки;
* **inline-комментариев нет**: иначе путь с `;` (`C:\проект;архив\map.txt`) неотличим от комментария;
* секции: `[ifc]` — рабочая; `[ifc:PRESET]` — читается в отдельный словарь, применяется с v1.1 (в отчёт идёт пометка);
* ключи до первой секции считаются принадлежащими `[ifc]`; чужие секции (`[glb]`) молча пропускаются.

### 12.3. Пример (полный набор ключей)

```ini
; IvanTools — настройки экспорта IFC (плагин Export To IFC)
; Комментарий — строка, начинающаяся с ';' или '#'; inline-комментариев нет
; Ключи, которых нет в диалоге, передаются в IFCExportOptions.AddOption

[ifc]
; пресет: IFC4DTV (передача) | IFC4RV (референс) | IFC2x3CV2 (координация)
preset = IFC4DTV

; --- объёмы, уровни, маппинг ---
base_quantities = true
split_walls_by_level = false
space_boundaries = 0
mapping_file =

; --- геометрия и содержимое ---
tesselation_lod = Medium
export_linked_files = false
export_2d_elements = false
export_bounding_box = false
export_rooms = true
phase =
include_site_elevation = true
use_family_type_name = false

; --- property sets ---
export_revit_psets = true
export_ifc_common_psets = true
export_user_psets = false
user_psets_file =
export_schedules_as_psets = false
schedules =

; --- прочее ---
store_ifc_guid = false
debug_keys = false

; переопределения по пресету (читаются, применяются с v1.1)
[ifc:IFC4RV]
base_quantities = false
tesselation_lod = Low
```

### 12.4. Маппинг ключей

| Ключ `.ini` | Куда идёт | Статус |
|---|---|---|
| `preset` | `IFCExportOptions.FileVersion` (через пресет) | проверено (API) |
| `base_quantities` | `ExportBaseQuantities` | проверено (API) |
| `split_walls_by_level` | `WallAndColumnSplitting` | проверено (API) |
| `space_boundaries` | `SpaceBoundaryLevel` (0/1/2) | проверено (API) |
| `mapping_file` | `FamilyMappingFile` (относительный — от папки ini) | проверено (API) |
| `tesselation_lod` | `AddOption("TessellationLevelOfDetail")`; не передаётся для 2x3 | кандидат |
| `export_linked_files` | `AddOption("ExportLinkedFiles")` | кандидат |
| `export_2d_elements` | `AddOption("Export2DElements")` | кандидат |
| `export_bounding_box` | `AddOption("ExportBoundingBox")` | кандидат |
| `export_rooms` | `AddOption("ExportRoomsInView")` | кандидат |
| `phase` | `AddOption("PhaseToExport")` (пусто = все фазы) | кандидат |
| `include_site_elevation` | `AddOption("IncludeSiteElevation")` | кандидат |
| `use_family_type_name` | `AddOption("UseFamilyAndTypeNameForReference")` | кандидат |
| `export_revit_psets` | `AddOption("ExportInternalRevitPropertySets")` | кандидат |
| `export_ifc_common_psets` | `AddOption("ExportIFCCommonPropertySets")` | кандидат |
| `export_user_psets` | `AddOption("ExportUserDefinedPsets")` | кандидат |
| `user_psets_file` | `AddOption("UserDefinedPropertySetsFile")` | кандидат |
| `export_schedules_as_psets` | `AddOption("ExportSchedulesAsPsets")` | кандидат |
| `schedules` | `AddOption("ExportSpecificSchedules")` (имена через `;`) | кандидат |
| `store_ifc_guid` | `AddOption("StoreIFCGUID")` | кандидат |
| `debug_keys` | диагностика: прогон кандидатов в пробном `IFCExportOptions` | служебный |
| любой другой непустой ключ | `AddOption(key, value)` + строка в лог и отчёт | как есть |

«Кандидат» = имя ключа проверяется режимом `debug_keys = true`: результат переносится сюда как «принят / исключение», непринятые галочки убираются из GUI.

`preset` принимает имена `IFCVersion` регистронезависимо; алиасы `IFC2X3` и `2X3` → `IFC2x3CV2`; `space_boundaries` — 0..2; `tesselation_lod` — `Low|Medium|High` (или пусто). Bool понимает `true/false`, `1/0`, `yes/no`, `on/off`, `да/нет`.

Порядок применения: **пресет из файла → его дефолты (QTO, Split=false, LOD) → явные ключи файла**. Так `preset = IFC4RV` без `base_quantities` даёт QTO = false и LOD = Low, как в диалоге.

### 12.5. Ошибки не ломают экспорт

| Ситуация | Что видит пользователь |
|---|---|
| Битая строка (`мусор без равно`, пустой ключ, нет `]`) | замечание с номером строки; остальные ключи применяются |
| Неизвестный `preset` | замечание, остаётся текущий пресет |
| Неверный bool | замечание, значение = дефолт пресета |
| Незнакомый ключ | не ошибка: пометка «→ AddOption», пара в лог |
| Файл не читается | замечание, дефолты |
| Файл только для чтения / сеть | «не записано: …» в отчёте, экспорт выполнен |
| `AddOption` бросил исключение | пара в «AddOption не принят: …», экспорт продолжается |

### 12.6. Запись — merge, а не regenerate

Перегенерация файла уничтожила бы ручные `AddOption`-ключи и комментарии. Поэтому запись точечная, внутри `[ifc]`:

1. найти заголовок `[ifc]` (или создать секцию в конце файла);
2. существующую непустую строку с известным ключом заменить на `key = value`;
3. отсутствующие известные ключи вставить перед следующей секцией (пропустив пустые строки в конце блока);
4. чужие ключи, комментарии и другие секции не трогать.

Атомарность: пишем `IvanTools.ini.tmp`, затем `File.Replace`; на FAT/сетевой папке — `File.Copy` + удаление временного. При сбое исходный файл остаётся целым. `mapping_file` пишется относительным, если файл лежит внутри поддерева папки ini, иначе — абсолютным.

### 12.7. Порядок применения в диалоге

`check radio → ApplyPresetDefaults → Apply(config) → снять _suppressPresetDefaults`. Без флага подавления `CheckedChanged` затрёт значения из файла дефолтами пресета. Галочка «Сохранять настройки в файл проекта» включается сама, только если файл уже существует; «Сохранить как…» пишет файл сразу и делает его текущим.

Запись выполняется **в команде** после `SaveFileDialog`: папка проекта, а для несохранённого документа — папка фактического `.ifc`. Ошибка записи не влияет на результат экспорта.

### 12.8. Наблюдаемость

Лог (`%TEMP%\ExportToIfc.log`): пресет, QTO/Split/Space boundaries, LOD и остальные флаги одной строкой, PSets, `debug_keys`, «Конфиг: `<путь>`», «Конфиг-замечания: …», «Сборка: `<путь к загруженной сборке>`», после экспорта — `AddOption: key=value; …` (фактически применённые), «Конфиг: записано/не записано».

Отчёт: «Конфиг», «Схема», **«View»** (view definition из файла), **«Сборка»**, строка `AddOption: N (…)`, блок «Диагностика ключей AddOption» при `debug_keys`, предупреждения конфига/экспортёра. Отчёт выделяется и копируется целиком («Копировать»).

