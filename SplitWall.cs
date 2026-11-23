// =============================================================================
// IvanTools - SplitWall
// =============================================================================
// Splits a multi-layer compound wall into individual single-layer walls,
// preserving hosted elements (doors, windows, and other components)
// with their original orientation and instance parameters.
//
// Environment : Autodesk Revit 2021-2025
//
// Credits:
//   Ivan Rodionov      - idea, prompt engineering, testing, validation
//   Google Gemini      - initial draft
//   Claude Sonnet 4.6  - developed to working state (Anthropic)
//
// License: MIT
//
// Development conversation (public):
//   https://claude.ai/share/8f6ba09e-4169-4a4d-a755-3cb23fbebcd7
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace IvanTools
{
public class SplitWall : IExternalCommand
{
    private Document _doc;
    private UIDocument _uidoc;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        _uidoc = commandData.Application.ActiveUIDocument;
        _doc   = _uidoc.Document;

        // 1. Get the selected wall
        var selectedIds = _uidoc.Selection.GetElementIds();
        if (selectedIds.Count == 0)
        {
            TaskDialog.Show("IvanTools", "Select a wall before running the command.");
            return Result.Cancelled;
        }

        var wall = _doc.GetElement(selectedIds.First()) as Wall;
        if (wall == null)
        {
            TaskDialog.Show("IvanTools", "The selected element is not a wall.");
            return Result.Cancelled;
        }

        var wallType = wall.WallType;
        var cs = wallType.GetCompoundStructure();

        if (cs == null || cs.LayerCount < 2)
        {
            TaskDialog.Show("IvanTools", "The selected wall is not a multi-layer wall.");
            return Result.Cancelled;
        }

        // 2. Offset math (find the wall centerline)
        int locLineParamVal = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).AsInteger();
        double offsetToCenter = cs.GetOffsetForLocationLine((WallLocationLine)locLineParamVal);

        XYZ orientation = wall.Orientation;
        Curve baseCurve = (wall.Location as LocationCurve).Curve;

        // LocationCurve.Curve already returns the geometric center of the wall,
        // regardless of the Location Line. No extra offset is needed.
        Curve centerCurve = baseCurve;

        string wallTypeName = wallType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME).AsString();

        // 3. Collect hosted elements BEFORE deleting the wall
        var hostedElements = GetHostedElements(wall);

        var newWalls    = new List<Wall>();
        var layerRanges = new List<LayerRange>();
        var errors      = new List<string>();

        // --- Transaction 1: break joins between the original wall and neighbors ---
        using (var txUnjoin = new Transaction(_doc, "Split Wall - unjoin neighbors"))
        {
            txUnjoin.Start();
            var joinedIds = JoinGeometryUtils.GetJoinedElements(_doc, wall).ToList();
            foreach (ElementId joinedId in joinedIds)
            {
                var joinedEl = _doc.GetElement(joinedId);
                if (joinedEl != null)
                {
                    try { JoinGeometryUtils.UnjoinGeometry(_doc, wall, joinedEl); }
                    catch { }
                }
            }
            txUnjoin.Commit();
        }

        // --- Transaction 2: create walls and move hosted elements ---
        using (var tx = new Transaction(_doc, "Split Wall into layers"))
        {
            tx.Start();

            double totalWidth = cs.GetWidth();
            double currentOffsetFromExt = 0.0;

            for (int i = 0; i < cs.LayerCount; i++)
            {
                double    lWidth = cs.GetLayerWidth(i);
                ElementId lMatId = cs.GetMaterialId(i);

                // --- A. Create a new wall type ---
                string newTypeName = string.Format("{0}_Layer_{1}_{2}mm",
                    wallTypeName, i + 1, (int)(lWidth * 304.8));

                WallType newType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault(wt =>
                        wt.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME)
                          .AsString() == newTypeName);

                if (newType == null)
                {
                    newType = wallType.Duplicate(newTypeName) as WallType;

                    ElementId matId = lMatId;
                    if (matId == ElementId.InvalidElementId)
                    {
                        matId = new FilteredElementCollector(_doc)
                            .OfClass(typeof(Material))
                            .FirstElementId();
                    }

                    var singleLayer = new CompoundStructureLayer(
                        lWidth, MaterialFunctionAssignment.Structure, matId);
                    var layerList = new List<CompoundStructureLayer> { singleLayer };
                    var newCs     = CompoundStructure.CreateSimpleCompoundStructure(layerList);
                    newType.SetCompoundStructure(newCs);
                }

                // --- B. Compute the curve for the center of this layer ---
                // All new walls are built on the layer centerline. The location
                // line is explicitly set to WallCenterline, so Revit applies no
                // extra offset and the wall stays exactly in place.
                double distFromExt    = currentOffsetFromExt + (lWidth / 2.0);
                double distFromCenter = (totalWidth / 2.0) - distFromExt;

                XYZ   vecToLayer = orientation.Multiply(distFromCenter);
                Curve layerCurve = centerCurve.CreateTransformed(
                    Transform.CreateTranslation(vecToLayer));

                double half = lWidth / 2.0;
                layerRanges.Add(new LayerRange(distFromCenter - half, distFromCenter + half, i));

                // --- C. Build the new wall ---
                ElementId levelId = wall.LevelId;

                Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                double height;
                if (heightParam != null)
                    height = heightParam.AsDouble();
                else
                {
                    BoundingBoxXYZ bb = wall.get_BoundingBox(null);
                    height = bb != null ? bb.Max.Z - bb.Min.Z : 10.0;
                }

                Wall newWall = Wall.Create(_doc, layerCurve, newType.Id, levelId,
                    height, 0.0, false, false);

                // Disable automatic joins at the ends
                WallUtils.DisallowWallJoinAtEnd(newWall, 0);
                WallUtils.DisallowWallJoinAtEnd(newWall, 1);

                double baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
                newWall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(baseOffset);

                ElementId topLevelId = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).AsElementId();
                if (topLevelId != ElementId.InvalidElementId)
                {
                    newWall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(topLevelId);
                    double topOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).AsDouble();
                    newWall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(topOffset);
                }

                newWalls.Add(newWall);
                currentOffsetFromExt += lWidth;
            }

            // --- D. Move hosted elements ---
            int transferred = 0;
            foreach (var fi in hostedElements)
            {
                int  targetIdx  = FindTargetWallForHosted(fi, orientation, centerCurve, layerRanges);
                Wall targetWall = newWalls[targetIdx];
                var  newFi      = CopyHostedElement(fi, targetWall, errors);
                if (newFi != null) transferred++;
            }

            // --- E. Delete the original wall ---
            _doc.Delete(wall.Id);

            tx.Commit();

            // --- F. Join the geometry in a separate transaction ---
            using (var tx2 = new Transaction(_doc, "Split Wall - join layers"))
            {
                tx2.Start();
                for (int i = 0; i < newWalls.Count - 1; i++)
                {
                    try
                    {
                        JoinGeometryUtils.JoinGeometry(_doc, newWalls[i], newWalls[i + 1]);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(string.Format(
                            "Could not join layers {0} and {1}: {2}", i, i + 1, ex.Message));
                    }
                }
                tx2.Commit();
            }

            string report = string.Format(
                "Wall split into {0} layers.\nHosted elements moved: {1} of {2}.",
                newWalls.Count, transferred, hostedElements.Count);

            if (errors.Count > 0)
                report += "\n\nWarnings:\n" + string.Join("\n", errors);

            TaskDialog.Show("IvanTools", report);
        }

        return Result.Succeeded;
    }

    // -------------------------------------------------------------------------

    private List<FamilyInstance> GetHostedElements(Wall wall)
    {
        var result    = new List<FamilyInstance>();
        var collector = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance));
        foreach (FamilyInstance fi in collector)
        {
            try
            {
                if (fi.Host != null && fi.Host.Id == wall.Id)
                    result.Add(fi);
            }
            catch { }
        }
        return result;
    }

    private int FindTargetWallForHosted(
        FamilyInstance fi,
        XYZ orientation,
        Curve centerCurve,
        List<LayerRange> layerRanges)
    {
        XYZ insertPt;
        try   { insertPt = (fi.Location as LocationPoint).Point; }
        catch { return 0; }

        XYZ    refPt = centerCurve.GetEndPoint(0);
        XYZ    vec   = insertPt - refPt;
        double proj  = vec.DotProduct(orientation);

        foreach (LayerRange lr in layerRanges)
        {
            if (proj >= lr.Lo && proj <= lr.Hi)
                return lr.Idx;
        }

        // If nothing matched, use the layer whose center is closest
        int    bestIdx  = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < layerRanges.Count; i++)
        {
            double mid  = (layerRanges[i].Lo + layerRanges[i].Hi) / 2.0;
            double dist = Math.Abs(proj - mid);
            if (dist < bestDist) { bestDist = dist; bestIdx = i; }
        }
        return bestIdx;
    }

    private FamilyInstance CopyHostedElement(
        FamilyInstance fi,
        Wall targetWall,
        List<string> errors)
    {
        try
        {
            FamilySymbol symbol   = fi.Symbol;
            XYZ          insertPt = (fi.Location as LocationPoint).Point;
            ElementId    levelId  = fi.LevelId;

            Level level = levelId != ElementId.InvalidElementId
                ? _doc.GetElement(levelId) as Level
                : null;

            FamilyInstance newFi = _doc.Create.NewFamilyInstance(
                insertPt, symbol, targetWall, level, StructuralType.NonStructural);

            // Reproduce manual flips of the original instance
            if (fi.HandFlipped   != newFi.HandFlipped)   newFi.flipHand();
            if (fi.FacingFlipped != newFi.FacingFlipped) newFi.flipFacing();

            // Copy all instance parameters
            var skipBips = new HashSet<int>
            {
                (int)BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
                (int)BuiltInParameter.ELEM_FAMILY_PARAM,
                (int)BuiltInParameter.ELEM_TYPE_PARAM,
                (int)BuiltInParameter.FAMILY_LEVEL_PARAM,
                (int)BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
                (int)BuiltInParameter.PHASE_CREATED,
                (int)BuiltInParameter.PHASE_DEMOLISHED,
                (int)BuiltInParameter.HOST_ID_PARAM,
            };

            foreach (Parameter srcP in fi.Parameters)
            {
                if (srcP.IsReadOnly) continue;
                if (skipBips.Contains(srcP.Id.IntegerValue)) continue;

                Parameter dstP = newFi.get_Parameter(srcP.Definition);
                if (dstP == null || dstP.IsReadOnly) continue;

                try
                {
                    switch (srcP.StorageType)
                    {
                        case StorageType.Double:    dstP.Set(srcP.AsDouble());    break;
                        case StorageType.Integer:   dstP.Set(srcP.AsInteger());   break;
                        case StorageType.ElementId: dstP.Set(srcP.AsElementId()); break;
                        case StorageType.String:
                            string val = srcP.AsString();
                            if (val != null) dstP.Set(val);
                            break;
                    }
                }
                catch { /* calculated or linked parameters are skipped */ }
            }

            return newFi;
        }
        catch (Exception ex)
        {
            errors.Add(string.Format("Could not move element {0}: {1}", fi.Id, ex.Message));
            return null;
        }
    }
}

// Helper class instead of a tuple (C# 5 compatibility)
public class LayerRange
{
    public double Lo  { get; private set; }
    public double Hi  { get; private set; }
    public int    Idx { get; private set; }
    public LayerRange(double lo, double hi, int idx) { Lo = lo; Hi = hi; Idx = idx; }
}
}
