# Changelog

All notable changes to IvanTools are documented here.
The format follows Keep a Changelog, and the project uses semantic versioning.

## [1.0.0] - 2026-11-23

### Added

- Renumber Sheets (`IvanTools.RenumberSheets`): batch renumbering of sheets
  selected in the Project Browser, "General Data" sheets first, automatic
  Project Browser refresh.
- Room Counter (`IvanTools.RoomCounter`): click-to-number rooms with a custom
  step, preserving prefixes and leading zeros.
- Split Wall (`IvanTools.SplitWall`): split a multi-layer wall into single-layer
  walls, moving hosted elements and restoring wall joins.
- Adjust to Face (`IvanTools.AdjustToFace`): align shape-edited floor or roof
  vertices to a picked planar face.
- Export to GLB (`IvanTools.ExportToGlb`): export the active 3D view to glTF 2.0
  binary with PBR materials.
- Hot Loader and Hot Loader Rerun (`IvanTools.HotLoader`,
  `IvanTools.HotLoaderRerun`): compile and run .cs scripts without restarting
  Revit.
- `build.ps1`: build, package and deploy script producing
  `dist/IvanTools-<version>.zip`.
- `IvanTools.addin` with every command declared as `AddIn Type="Command"`; no
  ribbon tab is created.
- `.gitattributes`, `.editorconfig` and `.gitignore` for deterministic LF
  sources and ASCII-only code.

### Changed

- All commands moved to the `IvanTools` namespace and renamed to `IvanTools.*`.
- All user-facing strings, comments and documentation translated to English;
  sources are ASCII, UTF-8 without BOM, LF.

### Removed

- Export to IFC (`ExportToIfc`) moved to `deprecated/` and excluded from the
  build and the manifest.
- The ribbon tab created by the old HotLoader application; the commands now live
  in Add-Ins > External Tools.
- PNG icons and per-command image references; External Tools is text-only.
