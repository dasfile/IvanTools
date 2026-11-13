# IvanTools — плагины для Autodesk Revit 2023

Один репозиторий, плоская структура: каждый плагин — один `.cs` (+ при необходимости `.dll` и `.png`) и один блок `<AddIn>` в `IvanTools.addin`. Все плагины собираются `csc.exe` из .NET Framework 4.x (стандарт **C# 5**, без внешних DLL), поэтому работают и через `CsHotLoader → Run Script`, и как обычный add-in.

## Состав

| Плагин | Файл | Класс | AddInId | Статус |
|---|---|---|---|---|
| Adjust To Face | `AdjustToFace.cs` | `IvanTools.AdjustToFaceCommand` | `aace0286-f1f7-4df6-bdf7-2cfca2a4b4ed` | готово, `AdjustToFace_PLAN.md` |
| Export To GLB | `ExportToGlb.cs` | `IvanTools.ExportToGlbCommand` | `8F2A6C41-3D7B-4E62-9A15-6B0C7E4D2F58` | готово, `ExportToGlb_PLAN.md` |
| Export To IFC | `ExportToIfc.cs` | `IvanTools.ExportToIfcCommand` | `3B7E5D92-6C1A-4F08-9E24-5A1D2C8B7F63` | в работе, `PLAN.md` |
| Batch Sheets Renumber | `SheetsBatchRenum.cs` | `RenumberCommand` | `50280453-0000-4e32-88e2-48c4ad6e4e3b` | ранний, в репозитории только код вне этого каталога |
| Split Wall | `SplitWall.cs` | `SplitWallToLayers` | `A1B2C3D4-E5F6-4321-8765-AABBCCDDEEFF` | ранний |

## Сборка

```powershell
csc /nologo /warn:4 /target:library /out:"$env:TEMP\<Имя>_check.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll" `
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPIUI.dll" `
    <Имя>.cs
```

Ожидается 0 ошибок / 0 предупреждений. Исходники — UTF-8 **с BOM** (иначе `csc`/HotLoader испортят кириллицу).

## Развёртывание

```text
C:\ProgramData\Autodesk\Revit\Addins\2023\
├── IvanTools.addin
└── IvanTools\
    ├── <Плагин>.cs      (HotLoader → Run Script)
    ├── <Плагин>.dll     (кнопка в манифесте)
    └── <Плагин>.png     (иконка 32×32)
```

## Соглашения

* план текущей задачи — `PLAN.md`; завершённые планы — `<Плагин>_PLAN.md`;
* один файл = один плагин, ровно один `public class : IExternalCommand` (требование `CsHotLoader`);
* никаких NuGet-зависимостей: только `RevitAPI`, `RevitAPIUI` и сборки .NET Framework (`System.Windows.Forms`, `System.Drawing`);
* `AddInId` — новый GUID на каждый плагин (проверять, что не занят).
