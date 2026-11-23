## IvanTools 1.0.0

First public release: seven commands for Autodesk Revit 2023, available in
Add-Ins > External Tools as `IvanTools.*` (no ribbon tab).

### Included

- Renumber Sheets - batch renumbering of selected sheets, "General Data" first
- Room Counter - click-to-number rooms, custom step, prefixes and leading zeros kept
- Split Wall - split a compound wall into single-layer walls, hosted elements preserved
- Adjust to Face - align shape-edited slab/roof vertices to a picked planar face
- Export to GLB - active 3D view to glTF 2.0 binary
- Hot Loader / Hot Loader Rerun - compile and run .cs scripts without restarting Revit

### Install

1. Download `IvanTools-1.0.0.zip`.
2. Extract the contents (not the wrapper folder) so that `IvanTools.addin` and the
   `IvanTools` folder are directly in `C:\ProgramData\Autodesk\Revit\Addins\2023`
   (or `%APPDATA%\Autodesk\Revit\Addins\2023`).
3. Start Revit -> Add-Ins -> External Tools.

Requires Autodesk Revit 2023. Sources and changelog: this repository.

SHA256: 8534259D494F04E06FCB731F3C2CEFA5F0D4012C5459D32DFC53DC5C7CFB9C6F
