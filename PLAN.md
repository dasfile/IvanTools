# Техническое задание и план: ExportToGlb (IvanTools)

Экспортёр активного 3D-вида Autodesk Revit 2023 в бинарный glTF 2.0 (`.glb`): треугольная геометрия, материалы PBR и дерево нод по элементам модели. Реализация — **без внешних библиотек** (свой минимальный писатель GLB), чтобы файл запускался через `CsHotLoader` и компилировался `csc`-ом из .NET Framework.

```text
C:\ProgramData\Autodesk\Revit\Addins\2023\
├── IvanTools.addin              <- блок <AddIn> с новым AddInId
└── IvanTools\
    ├── ExportToGlb.cs           <- запуск через CsHotLoader (Run Script)
    └── ExportToGlb.dll          <- опционально, для кнопки в манифесте
```

---

## 1. Цель и границы

Экспортируется **ровно то, что показывает активный вид**:

* обрезка **Section Box** (`View3D.IsSectionBoxActive`), скрытые категории/элементы, видимость по фазам, графика вида — всё это применяет сам Revit при экспорте вида через `CustomExporter`, ручная обрезка геометрии не нужна и не делается;
* RPC-объекты приходят обычным полимешем (по документации `OnRPC` + `OnPolymesh`);
* геометрия из связей (Revit Links) — с учётом трансформаций `LinkNode`.

Не экспортируется (осознанно): 2D-аннотации и линии, Decals, карты нормалей/roughness-текстуры, источники света, камеры.

UI — минимальный: проверка активного вида → `SaveFileDialog` → сводка в `TaskDialog`. Прогресс-бар невозможен: `CustomExporter` работает синхронно в валидном API-контексте и колбэков прогресса не имеет (см. §4).

---

## 2. Среда и жёсткие ограничения сборки

| Требование | Значение |
|---|---|
| Платформа | Autodesk Revit 2023 (.NET Framework 4.8), x64 |
| Компилятор | `csc.exe` из `C:\Windows\Microsoft.NET\Framework64\v4.0.30319` → **стандарт C# 5** |
| Файл | один `.cs`, кодировка UTF-8 **с BOM** |
| Точка входа | ровно **один** `public class : IExternalCommand` (требование `CsHotLoader`: он запускает первый найденный) |
| Внешние DLL | **запрещены**: `CsHotLoader` компилирует через `CSharpCodeProvider` и подключает только `RevitAPI.dll`, `RevitAPIUI.dll` и уже загруженные в AppDomain сборки |

**Ограничения синтаксиса C# 5** (иначе не скомпилируется ни `csc`, ни HotLoader):

* без интерполяции строк — `string.Format()` или конкатенация;
* без pattern matching — `as` + проверка на `null` (`View3D view3d = uidoc.ActiveView as View3D`);
* без `?.`, `nameof`, `out var`, кортежей, `=>`-членов, `default`-литералов;
* вместо кортежей — отдельные классы (`GlbNode`, `GlbPrimitive`, ...).

**Чего нет на машине (проверено) и поэтому не используем:** .NET SDK (`dotnet build` невозможен), targeting pack .NET Framework 4.8 (`MSBuild`-сборка net48 невозможна), `netstandard.dll`-фасад (нельзя подключить `netstandard2.0`-сборки). Поэтому SharpGLTF и подобные библиотеки **исключены** — см. §15.

---

## 3. Состав файла `ExportToGlb.cs`

| Тип | Назначение |
|---|---|
| `IvanTools.ExportToGlbCommand` | `IExternalCommand`: проверка вида, `SaveFileDialog`, запуск экспорта, сводка |
| `IvanTools.GlbExportContext` | `IExportContext`: стек трансформаций, треугольники, переключение материалов |
| `IvanTools.MaterialLibrary` | кэш `ElementId -> GlbMaterial`, список предупреждений о текстурах |
| `IvanTools.GlbImageCache` | кэш картинок `(путь, ширина/высота) -> индекс`, конвертация в PNG |
| `IvanTools.GlbScene` | корень: списки нод, мешей, материалов, картинок, сэмплеров, ошибок |
| `IvanTools.GlbNode` | нода: `Name`, `MeshIndex`, `Children`, `Matrix` (для корневой — матрица масштаба/осей) |
| `IvanTools.GlbMesh` / `GlbPrimitive` | меш элемента и его примитивы (по одному на материал) |
| `IvanTools.GlbMaterial` | `Name`, `BaseColorFactor`, `BaseColorImage`, `Metallic`, `Roughness`, `Alpha`, `DoubleSided` |
| `IvanTools.GlbWriter` | сериализация GLB: JSON-чанк + BIN-чанк, accessors/bufferViews |

Дополнительная ссылка при сборке: `System.Drawing.dll` (конвертация `.bmp/.tif` в PNG). Больше ничего.

---

## 4. Поток выполнения (`ExportToGlbCommand`)

1. `UIDocument uidoc = commandData.Application.ActiveUIDocument`; `Document doc = uidoc.Document`.
2. `View3D view3d = uidoc.ActiveView as View3D`; если `null` **или** `view3d.IsTemplate` → `TaskDialog` с предупреждением и `Result.Cancelled`.
3. `SaveFileDialog` (`System.Windows.Forms`): фильтр `glTF Binary (*.glb)|*.glb`, имя по умолчанию `{doc.Title}_{view3d.Name}.glb`, обязательное расширение `.glb`.
4. Экспорт:
   ```csharp
   GlbExportContext ctx = new GlbExportContext(doc, view3d);
   using (CustomExporter exporter = new CustomExporter(doc, ctx))
   {
       exporter.IncludeGeometricObjects = false;  // см. §10.1: полимеши придут, OnFaceBegin/End — нет
       exporter.ShouldStopOnError = false;
       exporter.Export(view3d);
   }
   ctx.Scene.WriteTo(path);   // GlbWriter
   ```
5. `TaskDialog` со сводкой: время, элементов, мешей, примитивов, треугольников, материалов, текстур (встроено / не найдено), первые 10 ошибок.
6. Транзакции **не** открываются, `doc.Regenerate()` не вызывается: экспорт только читает документ. Атрибуты `[Transaction(TransactionMode.Manual)]`, `[Regeneration(RegenerationOption.Manual)]`.
7. Отмена: `IsCanceled()` возвращает внутренний флаг (в первой версии — всегда `false`, задел под кнопку «Отмена»); учесть, что возврат `true` прерывает экспорт и `Finish()` всё равно будет вызван.
8. Ошибки: любые исключения внутри колбэков ловятся локально, пишутся в `Scene.Warnings`, экспорт продолжается (`ShouldStopOnError = false`); при фатальной ошибке файл не создаётся, показывается `TaskDialog` с текстом.

---

## 5. `GlbExportContext` (обход сцены)

* `Start()` → `true`; стек трансформаций = `Transform.Identity`. `OnViewBegin` → `RenderNodeAction.Proceed`; `OnViewEnd` / `Finish` — пустые. `IsCanceled()` — см. §4.7.
* `OnElementBegin(ElementId)` → `Proceed`, запомнить текущий элемент (`ElementId.InvalidElementId` → группа `Default`), начать новый `GlbMesh` с именем `{Category}_{FamilyAndType}_{ElementId}`; `OnElementEnd` — закрыть меш, пустой не добавлять.
* `OnInstanceBegin(InstanceNode node)` → `Proceed`, в стек положить `current.Multiply(node.GetTransform())` (метод объявлен в базовом `GroupNode`, поэтому доступен и у `LinkNode`); `OnInstanceEnd` → `Pop()`. Стек обязателен: семейства бывают вложенными.
* `OnLinkBegin(LinkNode node)` / `OnLinkEnd` → то же самое; документ связи брать через `node.GetDocument()` (иначе имена элементов связи будут пустыми, а `doc.GetElement` вернёт `null`). `LinkNode` **не** является наследником `InstanceNode` — не приводить типы, использовать общий `GroupNode`.
* `OnFaceBegin` → `Proceed` (при `IncludeGeometricObjects = false` этот метод **не вызывается вообще** — это документированное поведение, а не ошибка).
* `OnMaterial(MaterialNode node)` → получить/создать материал в `MaterialLibrary` (§7), запомнить как «активный» вместе с `node.Color`, `node.Transparency`; материал применяется к последующим `OnPolymesh`.
* `OnPolymesh(PolymeshTopology node)`:
  * точки: `current.OfPoint(node.GetPoint(i))`; нормали: `current.OfVector(node.GetNormal(i)).Normalize()` (у Revit-инстансов нет масштаба; при защите от нестандартных трансформаций — нормализовать всегда);
  * распределение нормалей по `node.DistributionOfNormals`: `AtEachPoint` — индекс нормали = индекс точки, `OnEachFacet` — индекс = индекс фасета, `OnePerFace` — всегда `GetNormal(0)`; фактическое число нормалей — `node.NumberOfNormals` (может быть 1);
  * UV: если `node.NumberOfUVs > 0` — `U' = U`, `V' = -V`; иначе примитив пишется без `TEXCOORD_0` (это нормальная ситуация, не ошибка);
  * фасеты: `node.GetFacet(f)` → `PolymeshFacet.V1/V2/V3`; треугольник отбрасывается, если `|(p1-p0) x (p2-p0)|^2 <= 1e-12` (вырожденные получаются при обрезке Section Box);
  * накопление: вершины добавляются в `GlbPrimitive` активного материала внутри `GlbMesh` текущего элемента; UV пишутся только если есть у **всех** точек этого примитива (иначе примитив создаётся без UV, чтобы не смешивать форматы вершин).

---

## 6. Координаты, оси и масштаб

Вершины пишутся в **футах Revit, без изменения**. Переход «Revit: правая система, Z-up, футы» → «glTF: правая система, Y-up, метры» делается **одной матрицей на корневой ноде** `BasisTransform` — это дешевле и не требует пересчёта каждой вершины/нормали:

```text
M = Scale(0.3048) * RotX(-90°)      p' = (0.3048*x, 0.3048*z, -0.3048*y)
```

glTF хранит матрицу **column-major** (16 float):

```text
h = 0.3048f
[ h, 0, 0, 0,    0, 0, -h, 0,    0, h, 0, 0,    0, 0, 0, 1 ]
```

Следствия, которые нужно удержать в коде и в ревью:

* определитель матрицы `+1` (масштаб положительный, поворот −90° вокруг X) → **порядок обхода вершин не инвертируется**, нормали не «выворачиваются»;
* масштаб равномерный → после преобразования нормали остаются единичными, повторная нормализация не нужна;
* `accessor.min/max` для `POSITION` считаются по сырым (футовым) координатам — спецификация требует соответствия фактическим данным `accessor`-а, а не итоговым метрам;
* если решим писать вершины сразу в метрах, матрица заменяется на `RotX(-90°)`, и тогда масштаб применяется к каждой точке — это допустимая альтернатива, но выбрать нужно **одно** и не смешивать.

---

## 7. Материалы (`MaterialLibrary`)

Кэш: `Dictionary<ElementId, GlbMaterial>`; `MaterialId == ElementId.InvalidElementId` → материал по умолчанию `Default` (цвет `node.Color`, прозрачность `node.Transparency`).

1. **Базовый цвет.** `MaterialNode.Color` — «цвет, которым материал отрисовывается»; свойство `HasColor` в Revit 2023 **отсутствует** (проверено рефлексией), `Color` возвращается всегда → `BaseColorFactor = (R/255, G/255, B/255)`.
2. **Текстура (baseColorTexture).** Доступ к ассету — `MaterialNode.GetAppearance()` (есть в 2023) либо `Material.AppearanceAssetId` → `AppearanceAssetElement.GetRenderingAsset()`:
   * `asset.FindByName("generic_diffuse")` (для других схем — `"wallpaint_color"`, `"masonrycmu_color"`, `"metal_color"`, `"wood_color"`; перебор списка кандидатов, а не жёсткая схема);
   * `prop.GetSingleConnectedAsset()` → ассет `UnifiedBitmap`; путь — `FindByName("unifiedbitmap_Bitmap") as AssetPropertyString`;
   * **фактические имена ключей проверить логом на реальной модели** (вспомогательные константы `Autodesk.Revit.DB.Visual.Generic.GenericDiffuse`, `...Visual.UnifiedBitmap.UnifiedbitmapBitmap` существуют, но их строковые значения документацией не гарантированы). Пока ключ не подтверждён — fallback-цвет, а не падение;
   * резолв пути: абсолютный как есть; относительный — `%ProgramFiles(x86)%\Common Files\Autodesk Shared\Materials\Textures\...` (папка на машине есть), затем папка проекта;
   * файла нет / не читается → предупреждение в `Scene.Warnings` + остаётся `BaseColorFactor`.
3. **Формат картинок.** Спецификация glTF: только `image/png` и `image/jpeg`. `.bmp/.tif/.tga` конвертируются в PNG в памяти через `System.Drawing` (`System.Drawing.Image.FromFile` → `MemoryStream` → `Save(ImageFormat.Png)`); картинка встраивается в BIN-чанк (`images[i].bufferView` + `mimeType`). Кэш по нормализованному пути.
4. **Прозрачность.** `Alpha = 1 - node.Transparency` (0 — непрозрачный, 1 — полностью прозрачный); `Alpha < 0.999` → `alphaMode = "BLEND"`, иначе `"OPAQUE"`; `baseColorFactor[3] = Alpha`.
5. **Шероховатость.** `Material.Smoothness` (документировано: 0..100) → `Roughness = 1 - Smoothness/100`, зажать в `[0.02, 1.0]`. Fallback при отсутствии материала — `0.5`. `MaterialNode.Glossiness` **не использовать**: диапазон в документации не указан (0..128 или 0..100 — предположение).
6. **Металличность.** Ассет содержит `metal_color` / `GenericIsMetal == 1` → `Metallic = 0.9`, иначе `0.0`.
7. **Двусторонность.** `doubleSided = true` для всех материалов: страхует стекло, тонкие листы и вывернутые нормали ценой небольшой потери производительности в Blender. Решение намеренное, менять только осознанно.

---

## 8. UV-координаты: измерено на реальной модели (Этап 0 выполнен)

Документация Revit (`PolymeshTopology.GetUVs`, `NumberOfUVs`) не определяет ни диапазон, ни систему координат UV, поэтому диапазон был измерен логом `%TEMP%\ExportToGlb_uv.log` (первые 30 полимешей: `min/max` по U и V, габарит полимеша).

**Результат замера (модель SE003-2025-AP, 1250 элементов):**

```text
Structural Columns_default_3077956; 8;8; U 44.8983…46.8668; V -17.2244…27.7231; габарит 44.991 ft
Structural Columns_default_3077956; 8;8; U -0.9843…0.9843;  V -0.0026…0.4921;  габарит 2.03 ft
```

Диапазон V ≈ 44.95 при габарите 44.99 ft, диапазон U ≈ 1.97 при грани ~2 ft → **UV численно равны футам Revit**. Следствие: без пересчёта текстура тайлилась бы «один тайл на фут», что мельче, чем в Revit.

**Принятая формула** (реализована в `MaterialLibrary` + `GlbExportContext.GetVertexIndex`):

```text
U' =  U * TextureURepeat / TextureRealWorldScaleX
V' = -V * TextureVRepeat / TextureRealWorldScaleY
```

* `texture_RealWorldScaleX/Y` читаются из подключённого ассета `UnifiedBitmap` как `AssetPropertyDistance` (значение в футах; fallback — `AssetPropertyInteger`/`AssetPropertyDouble`, при отсутствии или 0 — 1 ft);
* `texture_URepeat/VRepeat` — аналогично, по умолчанию 1;
* множители `repeat / scale` считаются один раз на материал (`GlbMaterial.UvMulU/UvMulV`), а не на вершину;
* найденные `UvMulU/UvMulV` пишутся в UV-лог — для сверки с диалогом Revit («Размер» текстуры).

Инверсия V: **`V' = -V`**, а не `1 - V`. При значениях вне [0..1] вычитание из единицы сдвигает рисунок и зеркалит тайлинг; смена знака корректна и для [0..1], и для тайлинга.

Ограничение: поворот текстуры (`TextureWAngle`) пока не применяется (обещает расхождение только на «повёрнутых» текстурах; `KHR_texture_transform` не используется).

---

## 9. Формат GLB: что именно пишет `GlbWriter`

**Контейнер**

```text
header:  uint32 magic = 0x46546C67 ("glTF"), uint32 version = 2, uint32 totalLength
chunk 0: uint32 length, uint32 type = 0x4E4F534A ("JSON"), UTF-8 JSON, паддинг ПРОБЕЛАМИ (0x20) до кратности 4
chunk 1: uint32 length, uint32 type = 0x004E4942 ("BIN\0"), бинарные данные, паддинг НУЛЯМИ до кратности 4
```

**JSON-корень**

```text
asset      { version: "2.0", generator: "IvanTools ExportToGlb (Revit 2023)" }
scene      0
scenes     [ { name: <имя вида>, nodes: [ <корневые ноды> ] } ]
nodes      [ { name, matrix | mesh, children } ]
meshes     [ { name, primitives: [ { attributes: {POSITION, NORMAL, TEXCOORD_0}, indices, material, mode: 4 } ] } ]
materials  [ { name, doubleSided, alphaMode, pbrMetallicRoughness: { baseColorFactor, baseColorTexture, metallicFactor, roughnessFactor } } ]
textures   [ { sampler: 0, source: <image> } ]
images     [ { bufferView, mimeType } ]
samplers   [ { wrapS: 10497, wrapT: 10497, magFilter: 9729, minFilter: 9987 } ]
accessors  [ { bufferView, byteOffset, componentType, count, type, min/max } ]
bufferViews[ { buffer, byteOffset, byteLength, target? } ]
buffers    [ { byteLength } ]
```

**Правила, без которых файл невалиден или «не открывается в Blender»**

1. `POSITION` accessor **обязан** содержать `min` и `max` (по 3 числа); `NORMAL`/`TEXCOORD_0`/индексы — без `min/max`.
2. `byteOffset` каждого accessor кратен размеру компонента: 4 байта для `POSITION`/`NORMAL`/`TEXCOORD_0` (float) и индексов `uint32` (5125).
3. `bufferView.target`: 34962 (`ARRAY_BUFFER`) для вершин, 34963 (`ELEMENT_ARRAY_BUFFER`) для индексов; у bufferView картинок `target` не указывается.
4. `mode: 4` (TRIANGLES); индексы — `uint32` без разветвления на `uint16`.
5. Все числа — `CultureInfo.InvariantCulture` (`0.3048`, а не `0,3048`); строки экранируются (`\`, `"`, управляющие символы), кириллица остаётся UTF-8 без BOM.
6. Вершины не склеиваются между примитивами: каждый примитив — свой диапазон в BIN (`bufferView`), чтобы не строить словарь вершин и не тратить память на больших моделях.
7. Один меш = один элемент; один примитив = один материал внутри элемента. Ноды мешей создаются в порядке обхода, корневая нода `BasisTransform` содержит матрицу из §6.
8. Пустые меши/примитивы не попадают в JSON; если после фильтрации сцена пуста — файл не пишется, выводится предупреждение.

---

## 10. Подводные камни (чек-лист для реализации и код-ревью)

1. **`IncludeGeometricObjects = false` — это правильно, а не ошибка.** Документация: контекст не получит вызовов, связанных с гранями и кривыми (`OnFaceBegin`/`OnFaceEnd`), но объекты всё равно обрабатываются и их триангуляция приходит через `OnPolymesh`; экспорт при этом заметно быстрее. Не «исправлять» на `true`.
2. **Пустые меши/примитивы.** Элемент может дать `OnElementBegin` без единого `OnPolymesh` (полностью за Section Box, скрыт, нет граней). Такие меши не писать — иначе получаются accessors с `count: 0`.
3. **Вырожденные треугольники** после обрезки — фильтр по площади (§5).
4. **`NumberOfUVs == 0`** — норма для материалов без текстуры; не писать `TEXCOORD_0` для такого примитива.
5. **Битые/отсутствующие текстуры** (файл удалён, путь от другой машины) — предупреждение + `baseColorFactor`, не исключение.
6. **`ElementId.InvalidElementId`** — материал по умолчанию, элемент без id; группировать в `Default`, не падать на `doc.GetElement`.
7. **Связи.** `doc.GetElement(id)` для элемента связи вернёт `null` → имена/категории брать из `LinkNode.GetDocument()`; `LinkNode` не приводить к `InstanceNode` (общий базовый тип — `GroupNode`).
8. **Вид.** В `Wireframe` полимешей не будет вовсе; низкий Detail Level даёт грубую тесселяцию. В сводке предупреждать, если элементов 0 или треугольников подозрительно мало.
9. **Культура.** Только `InvariantCulture` в форматировании чисел (§9.5) — иначе GLB невалиден на локали `ru-RU`.
10. **Память.** Модель целиком держится в `List<float>`/`List<int>`; большие проекты — минуты работы и сотни МБ. Не добавлять «на всякий случай» копии списков вершин.
11. **`IsCanceled()`** — флаг, в первой версии всегда `false`; возврат `true` прерывает экспорт (при этом `Finish()` всё равно вызывается, файл нужно считать неполным и не сохранять).
12. **Исключение в колбэке** не должно валить экспорт: локальный `try/catch`, счётчик ошибок, продолжение (при `ShouldStopOnError = false`).
13. **Имена нод** должны быть пригодны для поиска в Blender: `{Category}_{FamilyAndType}_{ElementId}`, без символов перевода строки; повторяющиеся пробелы/`:` заменять на `_`.
14. **Единицы.** Все длины в Revit — футы; коэффициент `0.3048` задаётся одной константой и используется только в матрице (§6).

---

## 11. Порядок реализации (этапы)

* **Этап 0.** Проба данных на реальной модели: лог UV/нормалей/числа точек, размеры полимешей; проверить фактические ключи ассетов (`generic_diffuse`, `unifiedbitmap_Bitmap`). Результат — зафиксированные решения по §8 и §7.2.
* **Этап 1.** Каркас: `Command` + `GlbExportContext` + `GlbWriter`, материалы только цветом (`baseColorFactor`), без текстур. Проверка в Blender: масштаб (габарит = Revit × 0.3048), ориентация (Y-up), Section Box, порядок обхода (нет «дыр» и вывернутых граней).
* **Этап 2.** Материалы: прозрачность, roughness/metallic, включение UV по результатам Этапа 0.
* **Этап 3.** Текстуры: извлечение, резолв путей, конвертация в PNG, кэш, отчёт о ненайденных.
* **Этап 4.** Иерархия и связи (`LinkNode` → ноды элементов), имена, устойчивость к ошибкам, отмена.
* **Этап 5.** Производительность и приёмка: время на реальном проекте, потребление памяти, сводка в `TaskDialog`.

---

## 12. Сборка и развёртывание

CMD:
```bat
set PATH=%PATH%;C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set REVIT_DIR=%ProgramFiles%\Autodesk\Revit 2023

csc /target:library /out:ExportToGlb.dll /reference:"%REVIT_DIR%\RevitAPI.dll" /reference:"%REVIT_DIR%\RevitAPIUI.dll" /reference:System.Drawing.dll ExportToGlb.cs
```

Проверка компиляции «в никуда» (без мусора в папке проекта), PowerShell:
```powershell
csc /nologo /warn:4 /target:library /out:"$env:TEMP\ExportToGlb_check.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPIUI.dll" `
    /reference:System.Drawing.dll ExportToGlb.cs
```

Ожидаемый результат: **0 ошибок, 0 предупреждений**.

Манифест (`IvanTools.addin`, `AddInId` должен быть новым и уникальным):
```xml
  <!-- Экспорт активного 3D-вида в GLB -->
  <AddIn Type="Command">
    <Name>Export To GLB</Name>
    <FullClassName>IvanTools.ExportToGlbCommand</FullClassName>
    <Assembly>.\IvanTools\ExportToGlb.dll</Assembly>
    <AddInId>8F2A6C41-3D7B-4E62-9A15-6B0C7E4D2F58</AddInId>
    <VendorId>RODIONOV</VendorId>
    <VendorDescription>Ivan Rodionov, Architectural Engineer</VendorDescription>
    <LargeImage>.\IvanTools\ExportToGlb.png</LargeImage>
    <Image>.\IvanTools\ExportToGlb.png</Image>
    <ToolTip>Экспортирует геометрию и материалы активного 3D-вида в glTF Binary (.glb).</ToolTip>
  </AddIn>
```

Запуск без перезапуска Revit: положить `ExportToGlb.cs` в `C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\` и нажать **HotLoader → Run Script**.

---

## 13. Критерии приёмки

1. `.glb` открывается в Blender без ошибок импорта; сцена содержит ожидаемое число объектов.
2. Габариты модели равны габаритам в Revit с точностью до 0.1 % (метры), модель стоит на «полу», ось Z-up в Blender после импорта.
3. Section Box: элементы за плоскостями обрезки отсутствуют, обрезанные грани закрыты корректно (нет вывернутых нормалей).
4. Материалы: цвета совпадают с видом Revit, текстуры встроены в `.glb` (файл автономен), прозрачные элементы прозрачны.
5. Сводка в `TaskDialog` содержит время, число элементов/треугольников/материалов/текстур и список предупреждений; при ошибке на отдельном элементе экспорт завершается успешно.
6. Компиляция `csc`: 0 ошибок, 0 предупреждений; плагин запускается и из `CsHotLoader`, и из манифеста.

---

## 14. Известные ограничения

* Прогресс-бар и фон-экспорт невозможны (см. §4); интерфейс только по завершении.
* Нет карт нормалей/шероховатости/ambient occlusion — только base color (цвет или одна текстура).
* Decals, 2D-аннотации, свет и камеры не экспортируются.
* Тайлинг пересчитывается по `RealWorldScale` (замер в §8), но при нестандартных настройках текстуры (поворот, нецелый повтор) возможно расхождение с видом Revit.
* Вся сцена строится в памяти: на очень больших моделях — минуты работы и сотни МБ (риск `OutOfMemoryException` для моделей порядка миллионов треугольников).
* Поворот текстуры (`TextureWAngle`) не учитывается (сказывается только на «повёрнутых» текстурах).
* `doubleSided = true` для всех материалов: корректнее по нормалям, но чуть медленнее при рендере в Blender.
* Связи обрабатываются при экспорте, но элементы связи не проверяются на «свою» видимость внутри связи сверх того, что делает Revit.

---

## 15. Справочно: проверенные факты API и окружения

Проверено на локальной установке (`C:\Program Files\Autodesk\Revit 2023\RevitAPI.xml` + рефлексия по `RevitAPI.dll`), а также по исходникам `CsHotLoader.cs`:

| Факт | Значение |
|---|---|
| `CustomExporter.IncludeGeometricObjects = false` | полимеши приходят, `OnFaceBegin/End` — нет, экспорт быстрее |
| `MaterialNode` (2023) | `MaterialId, Color, Transparency, Smoothness, Glossiness, ThumbnailFile, HasOverriddenAppearance, GetAppearance(), GetAppearanceOverride()`; **`HasColor` отсутствует** |
| `InstanceNode : GroupNode`, `LinkNode : GroupNode` | `GetTransform()` объявлен в `GroupNode`; `LinkNode.GetDocument()` — документ связи |
| `DistributionOfNormals` | `OnePerFace`, `OnEachFacet`, `AtEachPoint` |
| `Material.Smoothness` / `Shininess` | 0..100 / 0..128 (документировано); `MaterialNode.Glossiness` — диапазон не документирован |
| `PolymeshTopology.GetUVs` / `NumberOfUVs` | диапазон UV в документации не определён; `NumberOfUVs` может быть 0 |
| `AssetProperties.FindByName(string)` | есть; `Autodesk.Revit.DB.Visual.Generic` / `UnifiedBitmap` — наборы констант-«лейблов» |
| `CsHotLoader` | компилирует через `CSharpCodeProvider` (C# 5), ссылается только на `RevitAPI`/`RevitAPIUI` + уже загруженные сборки → внешние DLL недоступны |
| Сборка на машине | нет .NET SDK, нет targeting pack 4.8, нет `netstandard.dll`-фасада → только `csc` v4.0.30319 (C# 5) по `RevitAPI.dll` |
| `%ProgramFiles(x86)%\Common Files\Autodesk Shared\Materials\Textures` | существует (оттуда резолвятся пути текстур) |

---

## 16. Статус реализации

Реализовано (файл `ExportToGlb.cs`, один публичный `IExternalCommand`, без внешних DLL):

* `ExportToGlbCommand` — проверка `View3D`/`IsTemplate`, `SaveFileDialog`, `CustomExporter` (`IncludeGeometricObjects = false`, `ShouldStopOnError = false`), отказ при нуле треугольников («Каркас»), сводка: размер файла, время, элементы (в т.ч. без геометрии), меши, примитивы, треугольники, материалы, текстуры «встроено / не найдено», первые 10 предупреждений.
* `GlbExportContext` — стек трансформаций (`GroupNode.GetTransform()`), документы связей (`LinkNode.GetDocument()`), нода-группа на связь, меш на элемент, примитив на материал, дедупликация вершин внутри полимеша, отбрасывание вырожденных треугольников, UV по формуле §8, локальный `try/catch` в каждом колбэке.
* `MaterialLibrary` — `node.Color` с конвертацией **sRGB → linear**, `GetAppearance()`/`GetAppearanceOverride()`, `generic_glossiness` → roughness, `metal_color`/`generic_is_metal` → metallic, текстуры: разбор `|`-списка путей, нормализация смешанных слешей, индекс библиотеки текстур по имени файла (один обход на экспорт), png/jpeg как есть, остальное → PNG через `System.Drawing`, `RealWorldScale`/`Repeat` → множители UV, кэши.
* `GlbWriter` — GLB-контейнер с паддингами, `POSITION` c `min/max`, **`uint16`-индексы** при `< 65536` вершин (с выравниванием BIN после нечётных длин), `bufferView.target`, `InvariantCulture`, JSON-экранирование, картинки в BIN.
* `UvProbe` — лог `%TEMP%\ExportToGlb_uv.log`: min/max UV, габарит полимеша и применённые `UvMulU/UvMulV`.

**Первый прогон на реальной модели (SE003-2025-AP):** 1250 элементов, 1117 мешей, 1141 примитив, 28 169 треугольников, 26 материалов, 1.3 с, файл 2.46 МБ, `Текстур: 0`, 2 предупреждения о ненайденных текстурах. Выяснено и исправлено:

1. `AssetPropertyString` содержит несколько вариантов пути через `|`, а слеши смешаны (`Textures\/1/Mats/...`) → путь не резолвился. Теперь: `Split('|')` → нормализация → проверка `File.Exists` → поиск по имени файла в библиотеке Autodesk.
2. Цвета выглядели «нежнее и голубее» (по отзыву) — это отсутствие пересчёта sRGB → linear для `baseColorFactor` (glTF ожидает линейные значения). Добавлено с флагом `ConvertSrgbToLinear`.
3. UV в футах подтверждены логом → добавлен пересчёт по `RealWorldScale` (§8).

Проверено:

* `csc /warn:4`: **0 ошибок, 0 предупреждений**; файл в UTF-8 с BOM.
* Дымовой тест писателя: контейнер (magic, version, длины чанков), JSON, `POSITION.min/max`, `images.bufferView`, выравнивание всех `bufferViews` по 4, `uint16`-индексы (`componentType 5123`, `byteLength 6` для одного треугольника + паддинг), множители UV применены (`0.5 / -2`).
* Тест на **реальных строках из предупреждения Revit**: `C:/…/Textures/\1/Mats/Concrete.Precast Structural Concrete.Smooth.jpg` → нормализуется в существующий файл; `1/Mats/Stone.Marble.Square.White-Black.Large Veins.png` → находится по имени в библиотеке текстур.
* Развёрнуто: `C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\ExportToGlb.dll|.cs|.png`; блок `<AddIn>` в `IvanTools.addin` (XML валиден, 4 аддина; резервная копия — `IvanTools.addin.bak`).

Осталось (требует Revit + просмотрщик):

* Повторный прогон: `Текстур > 0`, масштаб рисунка бетона/камня как в Revit, цвета насыщеннее.
* Этап 5: сверка габаритов/осей/Section Box на большом проекте, замер размера файла после встраивания текстур.

---

## 17. Размер файла и сжатие

Замер первого прогона: **2 578 960 Б** на 28 169 треугольников ≈ **91 Б/треугольник**. Основная масса — float32-данные вершин (позиция + нормаль = 24 Б на вершину) и индексы.

**Почему `.zip`/`.7z` даёт −90 %.** Мантиссы координат и нормалей имеют малую энтропию на байтовом уровне, поэтому deflate/LZMA сжимает их в разы. Это нормальный способ хранения и пересылки — по аналогии с `.ifczip`: `*.glb.zip` / `*.glb.7z`. Ограничение: онлайн-просмотрщики обычно не открывают архивы — для просмотра нужен распакованный `.glb` (или серверный `Content-Encoding: gzip`).

**Что меняется после встраивания текстур.** jpg/png уже сжаты, поэтому общая экономия zip упадёт (сжиматься будет только геометрия), а сам `.glb` вырастет на сумму картинок.

**Способы уменьшить файл без внешних библиотек:**

| Приём | Эффект | Статус |
|---|---|---|
| `uint16`-индексы при `< 65536` вершин в примитиве | −5…10 % | реализовано |
| Дедупликация вершин внутри полимеша | до −40 % на триангуляции | реализовано |
| Не писать `NORMAL` (просмотрщик посчитает плоские) | −40 %, но изогнутое станет фасеточным | не делать по умолчанию |
| `KHR_mesh_quantization` (нормали int8, позиции short + масштаб ноды) | −30…50 % | возможный этап, требует поддержки в просмотрщике |
| WebP-текстуры (`EXT_texture_webp`) | −30…50 % на текстурах | не делать: поддержка неполная |

**Что даёт максимум, но вне плагина:** `gltfpack` (meshopt) или `gltf-transform` (Draco) — 5–10×, ценой внешнего тулчейна и поддержки расширений. Логичный шаг «публикации» для показательных моделей, а не функция Revit-плагина.

Практическая рекомендация: `.glb` — рабочая единица (просмотр, импорт в Blender), `.glb.7z` — архив/пересылка.

