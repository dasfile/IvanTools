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
| Файлы Revit на диске | `Revit.IFC.Export.dll`, `IntfIFC.dll`, `IFCCertifiedEntitiesAndPSets.json`, конфиги `IFC4_22.12_16.txexp` / `IFC2X3_22.12_16.txexp`, XSD `IFC4.xsd`, `IFC2X3_TC1.xsd` |
| `RevitAPIIFC.dll` | низкоуровневый API (`IFCFile`, `ExporterIFC`) существует, но **не используется**: обёртка над `Export` дешевле и совместимее |

---

## 3. Пресеты

| Пресет | `IFCVersion` | QTO | Split стен/колонн | Для чего |
|---|---|---|---|---|
| **Лёгкий (IFC4 RV)** | `IFC4RV` | off | off | показ, координация: референсная геометрия, минимальный размер |
| **Стандарт (IFC4)** — по умолчанию | `IFC4` | on | off | передача в Tekla: полная геометрия + объёмы |
| **Совместимость (IFC2x3)** | `IFC2x3CV2` | on | off | требование ТЗ/старый софт (в 2x3 **нет** стадий — ограничение схемы) |

Что важно понимать про «лёгкий»:

* RV **не теряет семантику**: классы (`IfcWall`, `IfcSlab`, `IfcColumn`…), уровни/`IfcBuildingStorey`, типы и property sets остаются — облегчается **только геометрия** (без параметрических зависимостей и части сложных представлений);
* маппинг — ревитовский (`FamilyMappingFile` по умолчанию пуст ⇒ используется штатная таблица Revit); своя таблица подключается опционально;
* «излишняя параметризация» (DTV) не экспортируется ни одним пресетом.

Ожидание по размеру файла: RV < IFC4 < IFC2x3 — **проверяется** в Этапе 0 на одной модели, цифры вносятся в этот раздел.

---

## 4. Область экспорта: «что видно на виде»

* Всегда `FilterViewId = активный View3D`. Других режимов нет — так исключается «экспортировал не то».
* Проверка перед запуском: активный вид — `View3D` и не `IsTemplate`.
* Revit при экспорте вида учитывает скрытые категории/элементы, фильтры и переопределения видимости, фазу вида, шаблон вида. Наша задача — не мешать и проверить результат отчётом.
* **Section Box**: элементы не режутся; если куб активен, элементы вне куба в файл не попадут, а частично видимые — попадут целиком. Это ожидаемо и фиксируется как норма.
* **Связи (Revit Links)**: поведение определяется настройками экспорта Revit; проверяется в Этапе 0 (в отчёте будет видно по числу сущностей/классов).

---

## 5. UI

Один модальный WinForms-диалог (в `TaskDialog` не помещаются независимые опции):

```text
┌ Экспорт активного 3D-вида в IFC ────────────────────────────┐
│ Пресет:                                                     │
│  ( ) Лёгкий (IFC4 RV) — минимальный размер                  │
│  (•) Стандарт (IFC4) — для Tekla: геометрия + объёмы        │
│  ( ) Совместимость (IFC2x3 CV2)                             │
│                                                             │
│  [✓] Экспортировать базовые объёмы (QTO)                    │
│  [ ] Разрезать стены и колонны по уровням                   │
│                                                             │
│  Файл маппинга семейств (необязательно):                    │
│  [____________________________________] [Обзор…]            │
│                                                             │
│                                        [ Экспорт ] [Отмена] │
└─────────────────────────────────────────────────────────────┘
```

* выбор пресета переставляет значения чекбоксов на «дефолты пресета» (QTO: on для IFC4/2x3, off для RV; Split всегда off);
* `Сохранить как…` (`SaveFileDialog`, фильтр `IFC (*.ifc)|*.ifc`), имя по умолчанию `{DocTitle}_{ViewName}.ifc`;
* если указан файл маппинга и его нет на диске — предупреждение и возврат в диалог.

---

## 6. Архитектура файла `ExportToIfc.cs`

| Тип | Назначение |
|---|---|
| `ExportToIfcCommand` | `IExternalCommand`: проверки, диалог, `SaveFileDialog`, вызов экспорта, отчёт |
| `IfcExportPreset` | имя, описание, `IFCVersion`, дефолты QTO/Split |
| `IfcExportSettings` | выбранные настройки (пресет, флаги, путь маппинга) |
| `IfcExportDialog : Form` | интерфейс из §5 (без дизайнера, кодом) |
| `IfcStepSummary` | валидатор/статистика полученного STEP-файла |
| `IfcExportLog` | лог прогона в `%TEMP%\ExportToIfc.log` |

Ядро экспорта:

```csharp
using (IFCExportOptions options = new IFCExportOptions())
{
    options.FileVersion = settings.Preset.Version;
    options.FilterViewId = view3d.Id;
    options.ExportBaseQuantities = settings.BaseQuantities;
    options.WallAndColumnSplitting = settings.SplitWallsByLevel;
    if (!string.IsNullOrEmpty(settings.MappingFile))
        options.FamilyMappingFile = settings.MappingFile;

    ok = doc.Export(folder, baseName, options);   // имя без расширения
}
```

После экспорта — поиск фактического файла (`{baseName}.ifc`, `{baseName}.IFC`, `{baseName}`), проверка `ok`, размера и содержимого.

Требования к коду: C# 5, один `IExternalCommand`, без внешних DLL (совместимость с `CsHotLoader`), UTF-8 с BOM, кириллица только в строках.

---

## 7. Валидатор STEP (`IfcStepSummary`)

Читает файл **потоком** (построчно, без загрузки целиком — файлы бывают 100+ МБ):

1. Заголовок: `FILE_SCHEMA(('IFC4'|'IFC2X3'))`, `FILE_NAME` (имя, время, «originating system»).
2. Счётчики сущностей: разбор строк вида `#123=IFCWALL('...` — имя сущности до `(`; подсчёт всех типов + `TotalEntities`.
3. Явно вытаскиваются: `IFCWALL`, `IFCSLAB`, `IFCCOLUMN`, `IFCBEAM`, `IFCMEMBER`, `IFCPLATE`, `IFCDOOR`, `IFCWINDOW`, `IFCSTAIR`, `IFCROOF`, `IFCCOVERING`, `IFCBUILDINGELEMENTPROXY`, `IFCBUILDINGSTOREY`, `IFCSITE`, `IFCBUILDING`, `IFCPROJECT`, `IFCSURFACESTYLE`, `IFCSTYLEDITEM`, `IFCPROPERTYSET`, `IFCELEMENTQUANTITY`, `IFCRELCONTAINEDINSPATIALSTRUCTURE`.
4. Стили: если `IFCSTYLEDITEM` = 0 — отдельное предупреждение «цвета/материалы в файл не попали».

Отчёт (`TaskDialog`, стиль как у GLB-плагина):

```text
Файл: ...\SE003_3D.ifc
Размер: 12.34 МБ (12 943 001 Б)
Пресет: Стандарт (IFC4)   Схема: IFC4
Время: 24.1 с

Сущностей: 84 512
Стены: 312    Плиты: 78    Колонны: 96    Балки: 145
Двери: 41     Окна: 88     Лестницы: 6    Кровля: 2
Уровни (IfcBuildingStorey): 9    Сайт/здание: есть
PSets: 1 240   QTO: 860   Стили: 214 (StyledItem: 214)

Предупреждения (2):
  • IfcStyledItem = 0: цвета не экспортировались
  • домен: файл не содержит IfcSlab
```

Предупреждения (правила): схема не соответствует ожидаемой; `IFCPROJECT`/`IFCBUILDING`/`IFCBUILDINGSTOREY` отсутствуют; 0 стен и 0 плит; 0 стилей; размер < 1 КБ; экспорт вернул `false`; ошибки из лога Revit (см. Этап 0).

---

## 8. Этап 0 — эксперименты до/во время реализации

1. `FilterViewId`: скрытые элементы и категории, фильтры вида, шаблон вида, фаза вида — что именно попадает в файл. Отдельно — активный Section Box (элементы вне куба).
2. Три пресета на одной модели: размер файла, число сущностей, число стилей и QTO. Цифры → в §3.
3. `AddOption`: какие ключи принимаются в 2023 (в первую очередь `TessellationLevelOfDetail`; списки property sets; «IFC GUID в параметр»). Незнакомые ключи — игнорируются или бросают исключение.
4. Лог экспорта Revit: где лежит, формат, есть ли ошибки/предупреждения для отчёта.
5. Поведение `Export`: имя с расширением/без, перезапись существующего файла, поведение при несохранённом документе, экспорт связей.
6. Цвета: сравнение `IFCSTYLEDITEM`/`IFCSURFACESTYLE` для IFC4 и IFC2x3 на одной и той же модели.

---

## 9. Этапы реализации

* **Этап 1 (каркас).** `ExportToIfcCommand` + `IfcExportDialog` + `IfcExportPreset/Settings`; экспорт через `doc.Export` с `FilterViewId`; поиск фактического файла; минимальный отчёт (размер, время, схема, всего сущностей); лог. Проверка: файл создаётся, три пресета дают три разных результата, ничего не падает на несохранённом документе.
* **Этап 2 (диагностика).** `IfcStepSummary`: счётчики по типам, стили, PSets/QTO, проверки и предупреждения; полный отчёт в стиле GLB-плагина. Проверка: отчёт совпадает с ручным подсчётом по файлу (grep).
* **Этап 3 (доводка).** Поле маппинга с валидацией пути; проверенные `AddOption`-ключи (tessellation LOD) через конфиг-константы; мелочи UX (запоминание последнего пресета в `%APPDATA%` при желании).
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

Класс: `IvanTools.ExportToIfcCommand`. После правки манифеста нужен перезапуск Revit; через HotLoader — без перезапуска.

---

## 11. Ограничения и риски

* **IFC2x3 и стадии**: схема не поддерживает фазы; если нужны стадии — только IFC4 (и то большинство просмотрщиков `IfcTask` игнорируют, но набор элементов будет соответствовать фазе вида).
* **Цвета в просмотрщике**: Online3DViewer рендерит IFC через web-ifc (Phong-материалы), поэтому IFC и GLB всегда будут выглядеть по-разному; «цвета отлетели» сначала проверяем по счётчику `IFCSTYLEDITEM` в отчёте (экспорт vs просмотрщик).
* **Нет прогресса**: `Document.Export` синхронный, колбэков нет; на больших моделях — минуты. Плагин не блокирует UI, но и прогресс показать не может.
* **Связи**: экспорт линков управляется настройками Revit; поведение проверяется на реальной модели (Этап 0).
* **Mapping**: неверная таблица даёт неверные классы (`IfcBuildingElementProxy` вместо `IfcBeam`) — отчёт покажет распределение по типам, это лучший индикатор.
* **Размер**: IFC с QTO и полной геометрией может быть в разы больше GLB; для показа использовать пресет RV.
