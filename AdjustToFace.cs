// =============================================================================
// AdjustToFace - align shape-edited floor/roof vertices to a picked planar face
// =============================================================================
// The user picks a floor (Floor) or a roof (RoofBase) with a modified shape and
// then a planar face of any other model element. Every SlabShapeEditor vertex
// gets a new Z elevation so that it lies on the plane of that face (vertices
// move strictly vertically - that is how shape editing works).
//
// Environment : Autodesk Revit 2023 (.NET Framework 4.8), C# 5
//
// Implementation notes:
//   - In Revit 2023 SlabShapeEditor is available ONLY through the
//     .SlabShapeEditor property (there is no GetSlabShapeEditor() method).
//   - The meaning of the second SlabShapeEditor.ModifySubElement(vertex, offset)
//     argument is not documented: it may be an absolute Z elevation or an offset
//     from the original element plane. Therefore the elevation is set
//     adaptively (affine fit by measurement), see SetVertexElevation.
//   - After every ModifySubElement the vertex objects become stale, so a vertex
//     is looked up again in a fresh collection by X/Y coordinates (they do not
//     change during vertical movement).
//   - An error on one vertex does not cancel the whole operation: a summary is
//     collected instead.
//
// Running:
//   1) via IvanTools.HotLoader (Run Script) - just put this .cs into
//      C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
//   2) or compile AdjustToFace.dll and add an <AddIn> entry to IvanTools.addin:
//      csc /target:library /out:AdjustToFace.dll ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPI.dll" ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPIUI.dll" ^
//          AdjustToFace.cs
//
// Credits:
//   Ivan Rodionov - idea, requirements, testing
//   gemini.google.com, deepseek v4.1 flash 1017 - implementation
//
// License: MIT
// =============================================================================

using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

// In Revit the cancellation exception (Esc) is its own type, not System.OperationCanceledException
using RevitCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustToFace : IExternalCommand
    {
        // A normal is treated as "vertical" (the face is vertical) below this threshold
        private const double NormalEpsilon = 1e-6;

        // Elevation tolerance, Revit internal units (feet). 1e-4 ft is about 0.03 mm
        private const double ZTolerance = 1e-4;

        // Tolerance when looking up a vertex by X/Y after regeneration
        private const double XyTolerance = 1e-6;

        // How many times we try to set the elevation of one vertex (see SetVertexElevation)
        private const int MaxAttemptsPerVertex = 3;

        // How many errors are shown in the summary dialog
        private const int MaxReportedErrors = 10;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // -----------------------------------------------------------------
                // 1. Pick the host element (floor / roof)
                // -----------------------------------------------------------------
                Reference hostRef;
                try
                {
                    hostRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new SlabHostSelectionFilter(),
                        "Pick a floor or a roof with a modified shape");
                }
                catch (RevitCanceledException)
                {
                    return Result.Cancelled;
                }

                Element hostElement = doc.GetElement(hostRef.ElementId);
                HostObject host = hostElement as HostObject;
                SlabShapeEditor editor = GetSlabShapeEditor(hostElement);

                if (host == null || editor == null)
                {
                    TaskDialog.Show("IvanTools - Adjust to Face",
                        "The selected element is not a floor or a roof.\n" +
                        "Only Floor and RoofBase support shape editing.");
                    return Result.Cancelled;
                }

                bool needEnable = !editor.IsEnabled;
                if (needEnable)
                {
                    // Ask BEFORE the transaction starts - modal dialogs inside a
                    // transaction are best avoided.
                    TaskDialog enableDlg = new TaskDialog("IvanTools - Adjust to Face");
                    enableDlg.MainInstruction = "Shape editing is disabled for this element";
                    enableDlg.MainContent =
                        "To move vertices you need to enable shape editing\n" +
                        "(Modify Sub Elements). Enable it now?\n\n" +
                        "It cannot be disabled again through the API - only all\n" +
                        "shape edits can be reset (Reset Slab Shape).";
                    enableDlg.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                    if (enableDlg.Show() != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                // -----------------------------------------------------------------
                // 2. Pick the target planar face (retry on failure)
                // -----------------------------------------------------------------
                XYZ origin;
                XYZ normal;

                while (true)
                {
                    Reference faceRef;
                    try
                    {
                        faceRef = uidoc.Selection.PickObject(
                            ObjectType.Face,
                            new TargetFaceSelectionFilter(hostElement.Id),
                            "Pick a planar face to align the vertices to");
                    }
                    catch (RevitCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    Element faceOwner = doc.GetElement(faceRef.ElementId);
                    if (faceOwner == null)
                    {
                        TaskDialog.Show("IvanTools - Adjust to Face",
                            "Could not get the element of the picked face. Try another face.");
                        continue;
                    }

                    Face face = faceOwner.GetGeometryObjectFromReference(faceRef) as Face;
                    PlanarFace planarFace = face as PlanarFace;

                    if (planarFace == null)
                    {
                        TaskDialog.Show("IvanTools - Adjust to Face",
                            "The picked face is not planar (PlanarFace).\n" +
                            "Pick a planar face - for example the top or bottom of a slab or wall.");
                        continue;
                    }

                    XYZ faceNormal = planarFace.FaceNormal;
                    if (Math.Abs(faceNormal.Z) < NormalEpsilon)
                    {
                        TaskDialog.Show("IvanTools - Adjust to Face",
                            "The picked face is vertical: it has no single Z elevation.\n" +
                            "Vertices move strictly vertically, so this face is not suitable.\n" +
                            "Pick a sloped or horizontal planar face.");
                        continue;
                    }

                    origin = planarFace.Origin;
                    normal = faceNormal;
                    break;
                }

                // -----------------------------------------------------------------
                // 3. Top or bottom of the element lies on the plane
                // -----------------------------------------------------------------
                // SlabShapeEditor edits the TOP surface, and the bottom follows at
                // the construction thickness. So for "by the bottom face" every
                // vertex is raised by the thickness: the bottom lands on the plane.
                double thickness = GetHostThickness(doc, hostElement);
                double thicknessMm = UnitUtils.ConvertFromInternalUnits(thickness, UnitTypeId.Millimeters);
                double verticalOffset = 0.0;
                bool alongBottomFace = false;

                if (thickness > 0.0)
                {
                    TaskDialog bottomDlg = new TaskDialog("IvanTools - Adjust to Face");
                    bottomDlg.MainInstruction = "Align by the bottom face?";
                    bottomDlg.MainContent = string.Format(
                        "Element thickness: {0:F1} mm.\n\n" +
                        "Yes - vertices are raised by the thickness, the BOTTOM of the element lies on the plane.\n" +
                        "No  - the TOP of the element lies on the plane (current behavior).",
                        thicknessMm);
                    bottomDlg.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                    if (bottomDlg.Show() == TaskDialogResult.Yes)
                    {
                        verticalOffset = thickness;
                        alongBottomFace = true;
                    }
                }
                // If the thickness could not be determined (thickness <= 0), do not ask:
                // align by the top face and report it in the summary.

                // -----------------------------------------------------------------
                // 4. Align the vertices in a single transaction
                // -----------------------------------------------------------------
                int adjusted = 0;
                var errors = new List<string>();

                using (Transaction tx = new Transaction(doc, "Adjust To Face"))
                {
                    tx.Start();

                    editor = GetSlabShapeEditor(hostElement);
                    if (needEnable)
                    {
                        editor.Enable();
                        doc.Regenerate();
                        editor = GetSlabShapeEditor(hostElement);
                    }

                    // Snapshot the vertices and target elevations before any edits
                    var targets = new List<VertexTarget>();
                    SlabShapeVertexArray vertices = editor.SlabShapeVertices;
                    for (int i = 0; i < vertices.Size; i++)
                    {
                        SlabShapeVertex vertex = vertices.get_Item(i);
                        if (vertex == null)
                            continue;

                        XYZ position = vertex.Position;
                        targets.Add(new VertexTarget(position,
                            ElevationAt(origin, normal, position.X, position.Y) + verticalOffset));
                    }

                    if (targets.Count == 0)
                    {
                        tx.RollBack();
                        TaskDialog.Show("IvanTools - Adjust to Face",
                            "The element has no shape vertices to align.\n" +
                            "Check that shape editing is actually enabled.");
                        return Result.Cancelled;
                    }

                    foreach (VertexTarget target in targets)
                    {
                        try
                        {
                            string error;
                            if (SetVertexElevation(doc, hostElement, target, out error))
                                adjusted++;
                            else
                                errors.Add(error);
                        }
                        catch (Exception ex)
                        {
                            errors.Add(string.Format("Vertex ({0:F3}; {1:F3}): {2}",
                                target.Start.X, target.Start.Y, ex.Message));
                        }
                    }

                    doc.Regenerate();

                    if (adjusted == 0)
                    {
                        tx.RollBack();
                        ShowSummary(0, targets.Count, needEnable, alongBottomFace, thicknessMm, errors);
                        message = errors.Count > 0 ? errors[0] : "No vertex was aligned.";
                        return Result.Failed;
                    }

                    tx.Commit();
                }

                uidoc.RefreshActiveView();
                ShowSummary(adjusted, adjusted + errors.Count, needEnable, alongBottomFace, thicknessMm, errors);
                return Result.Succeeded;
            }
            catch (RevitCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("IvanTools - Adjust to Face",
                    string.Format("Execution error\n{0}: {1}", ex.GetType().Name, ex.Message));
                return Result.Failed;
            }
        }

        // ---------------------------------------------------------------------
        // Set the elevation of a single vertex.
        // Independent of the meaning of the ModifySubElement offset argument
        // (absolute Z or offset from the original plane): we assume that the
        // result depends linearly on the argument with slope 1, i.e.
        //     z(offset) = base + offset
        //   * absolute semantics  -> base = 0
        //   * relative semantics  -> base = original (unmodified) vertex elevation
        // The base value is measured in practice, then the argument is recomputed:
        //     offset = ZTarget - base
        // In practice 1 call (absolute) or 2 calls (relative) are enough.
        // ---------------------------------------------------------------------
        private static bool SetVertexElevation(
            Document doc,
            Element hostElement,
            VertexTarget target,
            out string error)
        {
            error = null;
            double argument = target.ZTarget;
            double currentZ = target.Start.Z;
            bool reached = false;

            for (int attempt = 0; attempt < MaxAttemptsPerVertex; attempt++)
            {
                SlabShapeEditor editor = GetSlabShapeEditor(hostElement);
                if (editor == null || !editor.IsEnabled)
                {
                    error = string.Format("Vertex ({0:F3}; {1:F3}): shape editor is not available",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                SlabShapeVertex vertex = FindVertex(editor, target.Start);
                if (vertex == null)
                {
                    error = string.Format("Vertex ({0:F3}; {1:F3}) was not found after regeneration",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                editor.ModifySubElement(vertex, argument);
                doc.Regenerate();

                editor = GetSlabShapeEditor(hostElement);
                vertex = FindVertex(editor, target.Start);
                if (vertex == null)
                {
                    error = string.Format("Vertex ({0:F3}; {1:F3}) disappeared after the move",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                currentZ = vertex.Position.Z;
                if (Math.Abs(currentZ - target.ZTarget) <= ZTolerance)
                {
                    reached = true;
                    break;
                }

                // Measurement: z = base + argument  =>  base = z - argument
                double baseEstimate = currentZ - argument;
                double nextArgument = target.ZTarget - baseEstimate;
                if (Math.Abs(nextArgument - argument) <= 1e-12)
                    break; // the recomputation changes nothing - further attempts are pointless

                argument = nextArgument;
            }

            if (!reached)
            {
                error = string.Format("Vertex ({0:F3}; {1:F3}): elevation {2:F4} instead of {3:F4}",
                    target.Start.X, target.Start.Y, currentZ, target.ZTarget);
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // Find a vertex in the current collection by X/Y coordinates
        // (they do not change during vertical movement). If several match,
        // take the one whose Z is closest to the original position.
        // ---------------------------------------------------------------------
        private static SlabShapeVertex FindVertex(SlabShapeEditor editor, XYZ start)
        {
            if (editor == null)
                return null;

            SlabShapeVertex best = null;
            double bestDeltaZ = double.MaxValue;

            SlabShapeVertexArray vertices = editor.SlabShapeVertices;
            for (int i = 0; i < vertices.Size; i++)
            {
                SlabShapeVertex vertex = vertices.get_Item(i);
                if (vertex == null)
                    continue;

                XYZ position = vertex.Position;
                if (Math.Abs(position.X - start.X) > XyTolerance)
                    continue;
                if (Math.Abs(position.Y - start.Y) > XyTolerance)
                    continue;

                double deltaZ = Math.Abs(position.Z - start.Z);
                if (deltaZ < bestDeltaZ)
                {
                    bestDeltaZ = deltaZ;
                    best = vertex;
                }
            }

            return best;
        }

        // ---------------------------------------------------------------------
        // Z elevation on the face plane at point (x, y)
        // ---------------------------------------------------------------------
        private static double ElevationAt(XYZ origin, XYZ normal, double x, double y)
        {
            return origin.Z - (normal.X * (x - origin.X) + normal.Y * (y - origin.Y)) / normal.Z;
        }

        // ---------------------------------------------------------------------
        // SlabShapeEditor via the property (there is no GetSlabShapeEditor() in Revit 2023)
        // ---------------------------------------------------------------------
        private static SlabShapeEditor GetSlabShapeEditor(Element element)
        {
            if (element == null)
                return null;

            Floor floor = element as Floor;
            if (floor != null)
                return floor.SlabShapeEditor;

            RoofBase roof = element as RoofBase;
            if (roof != null)
                return roof.SlabShapeEditor;

            return null;
        }

        // ---------------------------------------------------------------------
        // Construction thickness in feet.
        // Source order: instance parameter -> type CompoundStructure -> 0
        // (0 = the thickness could not be determined, align by the top face)
        // ---------------------------------------------------------------------
        private static double GetHostThickness(Document doc, Element host)
        {
            if (doc == null || host == null)
                return 0.0;

            BuiltInParameter thicknessParam = (host is RoofBase)
                ? BuiltInParameter.ROOF_ATTR_THICKNESS_PARAM
                : BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM;

            Parameter instanceParam = host.get_Parameter(thicknessParam);
            if (instanceParam != null
                && instanceParam.StorageType == StorageType.Double
                && instanceParam.AsDouble() > 0.0)
            {
                return instanceParam.AsDouble();
            }

            HostObjAttributes hostType = doc.GetElement(host.GetTypeId()) as HostObjAttributes;
            if (hostType != null)
            {
                CompoundStructure structure = hostType.GetCompoundStructure();
                if (structure != null && structure.GetWidth() > 0.0)
                    return structure.GetWidth();
            }

            return 0.0;
        }

        // ---------------------------------------------------------------------
        // Summary dialog with the results
        // ---------------------------------------------------------------------
        private static void ShowSummary(
            int adjusted,
            int total,
            bool enabledNow,
            bool alongBottomFace,
            double thicknessMm,
            List<string> errors)
        {
            string content = string.Format("Vertices aligned: {0} of {1}", adjusted, total);

            if (thicknessMm <= 0.0)
            {
                content += "\nOffset: 0.0 mm - construction thickness is unknown, " +
                           "aligned by the top face.";
            }
            else if (alongBottomFace)
            {
                content += string.Format(
                    "\nOffset: +{0:F1} mm - by the bottom face (construction thickness {0:F1} mm).",
                    thicknessMm);
            }
            else
            {
                content += string.Format(
                    "\nOffset: 0.0 mm - by the top face (construction thickness {0:F1} mm).",
                    thicknessMm);
            }

            if (enabledNow)
                content += "\nShape editing was enabled automatically.";

            if (errors.Count > 0)
            {
                content += string.Format("\nCould not align: {0}", errors.Count);

                int shown = Math.Min(errors.Count, MaxReportedErrors);
                for (int i = 0; i < shown; i++)
                    content += "\n  - " + errors[i];

                if (errors.Count > shown)
                    content += string.Format("\n  ... and {0} more", errors.Count - shown);
            }

            TaskDialog dlg = new TaskDialog("IvanTools - Adjust to Face");
            dlg.MainInstruction = errors.Count == 0
                ? "Vertices aligned to the face plane"
                : "Finished with warnings";
            dlg.MainContent = content;
            dlg.Show();
        }
    }

    // -------------------------------------------------------------------------
    // Target for a single vertex: original position + required Z elevation
    // (a separate class instead of a tuple - C# 5 compatibility)
    // -------------------------------------------------------------------------
    public class VertexTarget
    {
        public XYZ Start { get; private set; }
        public double ZTarget { get; private set; }

        public VertexTarget(XYZ start, double zTarget)
        {
            Start = start;
            ZTarget = zTarget;
        }
    }

    // -------------------------------------------------------------------------
    // Allow picking floors and roofs only
    // -------------------------------------------------------------------------
    public class SlabHostSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is Floor || elem is RoofBase;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // For faces: forbid picking a face of the host element itself
    // (otherwise the element is aligned "to itself" and the result is unpredictable)
    // -------------------------------------------------------------------------
    public class TargetFaceSelectionFilter : ISelectionFilter
    {
        private readonly ElementId _hostId;

        public TargetFaceSelectionFilter(ElementId hostId)
        {
            _hostId = hostId;
        }

        public bool AllowElement(Element elem)
        {
            return true;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            if (reference == null)
                return false;

            if (_hostId == null || _hostId == ElementId.InvalidElementId)
                return true;

            return reference.ElementId != _hostId;
        }
    }
}
