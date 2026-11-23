# IvanTools - plugins for Autodesk Revit 2023

A flat repository: **one `.cs` file per plugin** plus one `<AddIn Type="Command">`
entry in `IvanTools.addin`. Everything is compiled with `csc.exe` from .NET
Framework 4.x (**C# 5**, no external DLLs), so each plugin works both as a
regular add-in and through `IvanTools.HotLoader -> Run Script`.

The tools do **not** create a ribbon tab. Revit lists every command add-in under
**Add-Ins > External Tools**, so the manifest names are prefixed and all plugin
classes live in the `IvanTools` namespace - the menu shows a compact, sorted
`IvanTools.*` group instead of using ribbon space.

## Tools

| Entry in External Tools | Source | Class | AddInId |
|---|---|---|---|
| `IvanTools.AdjustToFace` | `AdjustToFace.cs` | `IvanTools.AdjustToFace` | `aace0286-f1f7-4df6-bdf7-2cfca2a4b4ed` |
| `IvanTools.ExportToGlb` | `ExportToGlb.cs` | `IvanTools.ExportToGlb` | `8F2A6C41-3D7B-4E62-9A15-6B0C7E4D2F58` |
| `IvanTools.HotLoader` | `CsHotLoader.cs` | `IvanTools.HotLoader` | `C0A5E7B4-3F21-4C8D-9A62-1D8E5F0B7A31` |
| `IvanTools.HotLoaderRerun` | `CsHotLoader.cs` | `IvanTools.HotLoaderRerun` | `5E2B9C13-7A44-4D18-B6F0-2C9A8D7E4B51` |
| `IvanTools.RenumberSheets` | `SheetsBatchRenum.cs` | `IvanTools.RenumberSheets` | `50280453-0000-4e32-88e2-48c4ad6e4e3b` |
| `IvanTools.RoomCounter` | `RoomCounter.cs` | `IvanTools.RoomCounter` | `7D3C1B2A-4E5F-4A6B-8C9D-0E1F2A3B4C5D` |
| `IvanTools.SplitWall` | `SplitWall.cs` | `IvanTools.SplitWall` | `A1B2C3D4-E5F6-4321-8765-AABBCCDDEEFF` |

Detailed design notes for individual tools are kept in `<Tool>_PLAN.md`.

## Install

Download the release archive and unpack it into **one** of these folders:

```text
C:\ProgramData\Autodesk\Revit\Addins\2023   (all users)
%APPDATA%\Autodesk\Revit\Addins\2023        (current user only)
```

The archive layout must be preserved:

```text
IvanTools.addin
IvanTools\
    AdjustToFace.dll
    CsHotLoader.dll
    ExportToGlb.dll
    RoomCounter.dll
    SheetsBatchRenum.dll
    SplitWall.dll
```

Do not install the same `AddInId` into both locations at once - Revit will report
a duplicate add-in.

## Build and release

`build.ps1` compiles every plugin and produces a ready-to-publish package:

```powershell
.\build.ps1                        # build 1.0.0
.\build.ps1 -Version 1.1.0         # another version
.\build.ps1 -Deploy                # build + copy to ProgramData (close Revit first)
```

Output:

```text
dist\IvanTools-<version>.zip       release archive
dist\stage\                        staging folder (same content as the archive)
build.log                          compiler output of the last build
```

The archive contains only binaries and documentation: `IvanTools.addin`,
`IvanTools\*.dll`, `README.md`, `LICENSE`. Sources are distributed through this
repository only. Revit API assemblies are referenced at compile time from the
installed Revit and are never redistributed.

The script requires:

* `C:\Program Files\Autodesk\Revit 2023` (RevitAPI.dll, RevitAPIUI.dll);
* `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` plus the WPF and
  WinForms assemblies from the same framework folder.

Expected compiler result: **0 errors, 0 warnings**.

## Hot reload

`IvanTools.HotLoader` picks a `.cs` file, compiles it in memory and runs it
without restarting Revit; `IvanTools.HotLoaderRerun` repeats the last script.
A script must contain exactly one public class implementing `IExternalCommand`
(the first one found is executed).

## Repository layout

```text
*.cs               plugin sources (also used by HotLoader)
IvanTools.addin    single manifest with all command entries
build.ps1          build / package / deploy script
LICENSE
<*>_PLAN.md        design notes per tool
deprecated\        retired code and assets (git-ignored)
dist\, obj\        build output (git-ignored)
```

## Conventions

* one file = one plugin, exactly one public `IExternalCommand` class (HotLoader
  requirement);
* identifiers, UI strings, comments and commit messages are English; sources are
  ASCII, so compiler/HotLoader encoding never corrupts them;
* sources use LF line endings and UTF-8 without BOM; `.gitattributes` enforces
  `eol=lf` for every text file, and `.editorconfig` keeps the editor in sync
  (batch files stay CRLF);
* no NuGet dependencies: only `RevitAPI`, `RevitAPIUI` and .NET Framework
  assemblies (`System.Windows.Forms`, `System.Drawing`);
* a new GUID for every new plugin; an existing `AddInId` is **never** changed
  between releases, so upgrades replace the tool instead of duplicating it;
* retired code goes to `deprecated\` (kept on disk, ignored by git; history is
  still available in git).

## License

MIT - see `LICENSE`.
