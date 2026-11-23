# IvanTools for Autodesk Revit

Lightweight C# tools for everyday Autodesk Revit workflows. One manifest, no
ribbon tab - every command is available in **Add-Ins > External Tools** under
the `IvanTools.*` group. Built for **Revit 2023** (.NET Framework 4.8, C# 5),
no NuGet dependencies; other versions may work but are not verified.

## Tools

### Renumber Sheets - `IvanTools.RenumberSheets`

Batch-renumbers sheets selected in the Project Browser.

* prefix, start number and digit padding are configurable;
* sheets containing "General Data" go first;
* temporary unique numbers prevent conflicts during renumbering;
* the Project Browser is refreshed automatically.

### Room Counter - `IvanTools.RoomCounter`

Assigns sequential room numbers by clicking rooms (B101 -> B102 -> B103).

* custom step; prefixes and leading zeros are preserved;
* processed rooms are highlighted in the active view immediately;
* rooms only - no other categories.

### Split Wall - `IvanTools.SplitWall`

Splits a multi-layer wall into separate single-layer walls.

* every layer keeps its exact position and thickness;
* hosted doors, windows and openings are moved to the right layer with their
  orientation and instance parameters;
* original wall joins are removed and the new layers are joined back.

### Adjust to Face - `IvanTools.AdjustToFace`

Aligns shape-edited floor or roof vertices to a picked planar face.

* asks whether the top or the bottom of the element should land on the plane;
* uses the element thickness for the offset;
* a failed vertex does not cancel the run - a summary is reported instead.

### Export to GLB - `IvanTools.ExportToGlb`

Exports the active 3D view to glTF 2.0 binary (`.glb`).

* PBR materials, textures, transparency, glossiness and metallic;
* respects section box, hidden elements and phases;
* geometry is written by a built-in glTF writer - no external libraries.

### Hot Loader - `IvanTools.HotLoader` / `IvanTools.HotLoaderRerun`

A pyRevit-like hot reload for C#: pick a `.cs` file, it is compiled in memory and
executed without restarting Revit.

* `HotLoaderRerun` repeats the last script without picking a file;
* a script must contain one public class implementing `IExternalCommand`;
* the same source file is used by both the add-in build and the hot-reload path.

## Install

1. Download the latest release archive.
2. Unpack it into **one** of these folders:

```text
C:\ProgramData\Autodesk\Revit\Addins\2023   (all users)
%APPDATA%\Autodesk\Revit\Addins\2023        (current user only)
```

3. Start Revit and open **Add-Ins > External Tools** - the tools are listed as
   `IvanTools.*`.

The archive contains `IvanTools.addin`, `IvanTools\*.dll`, `README.md` and
`LICENSE`. A ribbon tab is intentionally not created: External Tools is compact
and leaves ribbon space free. Do not install the same tool into both folders.

## Build from source

```powershell
.\build.ps1                 # build 1.0.0 into dist\
.\build.ps1 -Version 1.1.0  # build another version
.\build.ps1 -Deploy         # build and copy to ProgramData (close Revit first)
```

Requirements: Autodesk Revit 2023 installed (`RevitAPI.dll`, `RevitAPIUI.dll`)
and .NET Framework 4.x `csc.exe`. The script produces
`dist\IvanTools-<version>.zip` and prints its SHA256. Expected compiler result:
0 errors, 0 warnings.

## Development loop

Edit a plugin `.cs` file and run it through `IvanTools.HotLoader` - no Revit
restart, no rebuild of the whole package. When the change is ready, rebuild with
`build.ps1` and publish a new release.

## Credits

Concept, specification and testing: Ivan Rodionov.
Implementation assistance: Google Gemini, Anthropic Claude.

## Feedback

Open an issue in the GitHub Issues section of this repository.

## License

MIT - see `LICENSE`.

<details>
<summary>Technical reference</summary>

| Entry in External Tools | Class | AddInId |
|---|---|---|
| `IvanTools.AdjustToFace` | `IvanTools.AdjustToFace` | `aace0286-f1f7-4df6-bdf7-2cfca2a4b4ed` |
| `IvanTools.ExportToGlb` | `IvanTools.ExportToGlb` | `8F2A6C41-3D7B-4E62-9A15-6B0C7E4D2F58` |
| `IvanTools.HotLoader` | `IvanTools.HotLoader` | `C0A5E7B4-3F21-4C8D-9A62-1D8E5F0B7A31` |
| `IvanTools.HotLoaderRerun` | `IvanTools.HotLoaderRerun` | `5E2B9C13-7A44-4D18-B6F0-2C9A8D7E4B51` |
| `IvanTools.RenumberSheets` | `IvanTools.RenumberSheets` | `50280453-0000-4e32-88e2-48c4ad6e4e3b` |
| `IvanTools.RoomCounter` | `IvanTools.RoomCounter` | `7D3C1B2A-4E5F-4A6B-8C9D-0E1F2A3B4C5D` |
| `IvanTools.SplitWall` | `IvanTools.SplitWall` | `A1B2C3D4-E5F6-4321-8765-AABBCCDDEEFF` |

Conventions:

* one `.cs` file = one plugin with exactly one public `IExternalCommand` class;
* sources are ASCII, UTF-8 without BOM, LF (`.gitattributes`, `.editorconfig`);
* retired code and assets live in `deprecated/` and are git-ignored;
* `AddInId` never changes between releases, so upgrades replace tools instead of
  duplicating them.

</details>
