// =============================================================================
// AdjustToFace — выравнивание вершин измененной формы перекрытия/кровли
//                по плоскости выбранной плоской грани
// =============================================================================
// Пользователь выбирает перекрытие (Floor) или кровлю (RoofBase) с измененной
// формой, затем — плоскую грань любого другого объекта модели. Все вершины
// SlabShapeEditor получают новую отметку Z так, чтобы лечь на плоскость этой
// грани (вершины двигаются строго по вертикали — так работает shape editing).
//
// Environment : Autodesk Revit 2023 (.NET Framework 4.8), C# 5
//
// Особенности реализации:
//   - В Revit 2023 SlabShapeEditor доступен ТОЛЬКО через свойство
//     .SlabShapeEditor (метода GetSlabShapeEditor() в API нет).
//   - Семантика второго параметра SlabShapeEditor.ModifySubElement(vertex, offset)
//     в API не документирована: это может быть и абсолютная отметка Z, и
//     смещение от исходной плоскости элемента. Поэтому отметка выставляется
//     адаптивно (аффинная подгонка по замеру), см. SetVertexElevation.
//   - После каждого ModifySubElement объекты вершин устаревают, поэтому вершина
//     переискивается в свежей коллекции по координатам X/Y (они не меняются
//     при вертикальном перемещении).
//   - Ошибка на одной вершине не отменяет всю операцию: собирается сводка.
//
// Запуск:
//   1) через CsHotLoader (Run Script) — просто положить этот .cs в
//      C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
//   2) либо собрать AdjustToFace.dll и добавить блок <AddIn> в IvanTools.addin:
//      csc /target:library /out:AdjustToFace.dll ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPI.dll" ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPIUI.dll" ^
//          AdjustToFace.cs
//
// Credits:
//   Ivan Rodionov — идея, постановка задачи, тестирование
//   gemini.google.com, deepseek v4.1 flash 1017 — реализация
//
// License: MIT
// =============================================================================

using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

// В Revit исключение отмены (Esc) своё собственное, не System.OperationCanceledException
using RevitCanceledException = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AdjustToFaceCommand : IExternalCommand
    {
        // Нормаль считается «вертикальной» (грань вертикальна) ниже этого порога
        private const double NormalEpsilon = 1e-6;

        // Допуск по высоте, внутренние единицы Revit (футы). 1e-4 ft ≈ 0.03 мм
        private const double ZTolerance = 1e-4;

        // Допуск при поиске вершины по X/Y после регенерации
        private const double XyTolerance = 1e-6;

        // Сколько раз пробуем выставить отметку одной вершины (см. SetVertexElevation)
        private const int MaxAttemptsPerVertex = 3;

        // Сколько ошибок показывать в сводном диалоге
        private const int MaxReportedErrors = 10;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // -----------------------------------------------------------------
                // 1. Выбор хост-элемента (перекрытие / кровля)
                // -----------------------------------------------------------------
                Reference hostRef;
                try
                {
                    hostRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new SlabHostSelectionFilter(),
                        "Выберите перекрытие или кровлю с измененной формой");
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
                    TaskDialog.Show("IvanTools — AdjustToFace",
                        "Выбранный элемент не является перекрытием или кровлей.\n" +
                        "Редактирование формы поддерживают только Floor и RoofBase.");
                    return Result.Cancelled;
                }

                bool needEnable = !editor.IsEnabled;
                if (needEnable)
                {
                    // Спрашиваем ДО открытия транзакции — модальные диалоги внутри
                    // транзакции лучше не показывать.
                    TaskDialog enableDlg = new TaskDialog("IvanTools — AdjustToFace");
                    enableDlg.MainInstruction = "У элемента отключено редактирование формы";
                    enableDlg.MainContent =
                        "Чтобы перемещать вершины, нужно включить режим редактирования формы\n" +
                        "(Modify Sub Elements). Включить его сейчас?\n\n" +
                        "Отключить обратно средствами API нельзя — только сбросить все\n" +
                        "правки формы (Reset Slab Shape).";
                    enableDlg.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                    if (enableDlg.Show() != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                // -----------------------------------------------------------------
                // 2. Выбор целевой плоской грани (с повтором при неудаче)
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
                            "Выберите плоскую грань объекта, по которой выровнять вершины");
                    }
                    catch (RevitCanceledException)
                    {
                        return Result.Cancelled;
                    }

                    Element faceOwner = doc.GetElement(faceRef.ElementId);
                    if (faceOwner == null)
                    {
                        TaskDialog.Show("IvanTools — AdjustToFace",
                            "Не удалось получить элемент выбранной грани. Попробуйте другую грань.");
                        continue;
                    }

                    Face face = faceOwner.GetGeometryObjectFromReference(faceRef) as Face;
                    PlanarFace planarFace = face as PlanarFace;

                    if (planarFace == null)
                    {
                        TaskDialog.Show("IvanTools — AdjustToFace",
                            "Выбранная грань не является плоской (PlanarFace).\n" +
                            "Выберите плоскую грань — например, верх или низ плиты/стены.");
                        continue;
                    }

                    XYZ faceNormal = planarFace.FaceNormal;
                    if (Math.Abs(faceNormal.Z) < NormalEpsilon)
                    {
                        TaskDialog.Show("IvanTools — AdjustToFace",
                            "Выбранная грань вертикальна: у неё нет единственной отметки Z.\n" +
                            "Вершины перемещаются строго по вертикали, поэтому такая грань не подходит.\n" +
                            "Выберите наклонную или горизонтальную плоскую грань.");
                        continue;
                    }

                    origin = planarFace.Origin;
                    normal = faceNormal;
                    break;
                }

                // -----------------------------------------------------------------
                // 3. Верх или низ элемента ложится на плоскость
                // -----------------------------------------------------------------
                // SlabShapeEditor правит ВЕРХНЮЮ поверхность, а низ идет за ней на
                // толщину конструкции. Поэтому для «по нижней грани» все вершины
                // поднимаются на толщину: низ ложится точно на плоскость.
                double thickness = GetHostThickness(doc, hostElement);
                double thicknessMm = UnitUtils.ConvertFromInternalUnits(thickness, UnitTypeId.Millimeters);
                double verticalOffset = 0.0;
                bool alongBottomFace = false;

                if (thickness > 0.0)
                {
                    TaskDialog bottomDlg = new TaskDialog("IvanTools — AdjustToFace");
                    bottomDlg.MainInstruction = "Выравнивать по нижней грани?";
                    bottomDlg.MainContent = string.Format(
                        "Толщина элемента: {0:F1} мм.\n\n" +
                        "Да  — вершины поднимутся на толщину, на плоскость ляжет НИЗ элемента.\n" +
                        "Нет — на плоскость ляжет ВЕРХ элемента (текущее поведение).",
                        thicknessMm);
                    bottomDlg.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                    if (bottomDlg.Show() == TaskDialogResult.Yes)
                    {
                        verticalOffset = thickness;
                        alongBottomFace = true;
                    }
                }
                // Если толщину определить не удалось (thickness <= 0) — не спрашиваем,
                // выравниваем по верхней грани и сообщаем об этом в сводке.

                // -----------------------------------------------------------------
                // 4. Выравнивание вершин в одной транзакции
                // -----------------------------------------------------------------
                int adjusted = 0;
                var errors = new List<string>();

                using (Transaction tx = new Transaction(doc, "Выравнивание вершин по грани (IvanTools)"))
                {
                    tx.Start();

                    editor = GetSlabShapeEditor(hostElement);
                    if (needEnable)
                    {
                        editor.Enable();
                        doc.Regenerate();
                        editor = GetSlabShapeEditor(hostElement);
                    }

                    // Снимок вершин и целевых отметок — до любых правок
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
                        TaskDialog.Show("IvanTools — AdjustToFace",
                            "У элемента нет вершин формы для выравнивания.\n" +
                            "Проверьте, что редактирование формы действительно включено.");
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
                            errors.Add(string.Format("Вершина ({0:F3}; {1:F3}): {2}",
                                target.Start.X, target.Start.Y, ex.Message));
                        }
                    }

                    doc.Regenerate();

                    if (adjusted == 0)
                    {
                        tx.RollBack();
                        ShowSummary(0, targets.Count, needEnable, alongBottomFace, thicknessMm, errors);
                        message = errors.Count > 0 ? errors[0] : "Ни одна вершина не была выровнена.";
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
                TaskDialog.Show("IvanTools — AdjustToFace",
                    string.Format("Ошибка выполнения\n{0}: {1}", ex.GetType().Name, ex.Message));
                return Result.Failed;
            }
        }

        // ---------------------------------------------------------------------
        // Выставление отметки одной вершины.
        // Не зависит от того, что значит параметр offset у ModifySubElement
        // (абсолютная Z или смещение от исходной плоскости): считаем, что
        // результат линейно зависит от аргумента с наклоном 1, то есть
        //     z(offset) = base + offset
        //   * абсолютная семантика  -> base = 0
        //   * относительная         -> base = исходная (неизмененная) отметка вершины
        // Значение base измеряется по факту, после чего аргумент пересчитывается:
        //     offset = ZTarget - base
        // На практике достаточно 1 вызова (абсолютная) или 2 (относительная).
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
                    error = string.Format("Вершина ({0:F3}; {1:F3}): редактор формы недоступен",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                SlabShapeVertex vertex = FindVertex(editor, target.Start);
                if (vertex == null)
                {
                    error = string.Format("Вершина ({0:F3}; {1:F3}) не найдена после регенерации",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                editor.ModifySubElement(vertex, argument);
                doc.Regenerate();

                editor = GetSlabShapeEditor(hostElement);
                vertex = FindVertex(editor, target.Start);
                if (vertex == null)
                {
                    error = string.Format("Вершина ({0:F3}; {1:F3}) исчезла после перемещения",
                        target.Start.X, target.Start.Y);
                    return false;
                }

                currentZ = vertex.Position.Z;
                if (Math.Abs(currentZ - target.ZTarget) <= ZTolerance)
                {
                    reached = true;
                    break;
                }

                // Замер: z = base + argument  =>  base = z - argument
                double baseEstimate = currentZ - argument;
                double nextArgument = target.ZTarget - baseEstimate;
                if (Math.Abs(nextArgument - argument) <= 1e-12)
                    break; // пересчет ничего не меняет — пробовать дальше бессмысленно

                argument = nextArgument;
            }

            if (!reached)
            {
                error = string.Format("Вершина ({0:F3}; {1:F3}): отметка {2:F4} вместо {3:F4}",
                    target.Start.X, target.Start.Y, currentZ, target.ZTarget);
                return false;
            }

            return true;
        }

        // ---------------------------------------------------------------------
        // Поиск вершины в актуальной коллекции по координатам X/Y
        // (при вертикальном перемещении они не меняются). Если совпадений
        // несколько — берем ближайшую по Z к исходному положению.
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
        // Отметка Z на плоскости грани в точке (x, y)
        // ---------------------------------------------------------------------
        private static double ElevationAt(XYZ origin, XYZ normal, double x, double y)
        {
            return origin.Z - (normal.X * (x - origin.X) + normal.Y * (y - origin.Y)) / normal.Z;
        }

        // ---------------------------------------------------------------------
        // SlabShapeEditor через свойство (метода GetSlabShapeEditor() в Revit 2023 нет)
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
        // Толщина конструкции в футах.
        // Порядок источников: инстанс-параметр -> CompoundStructure типа -> 0
        // (0 = толщину определить не удалось, работаем по верхней грани)
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
        // Сводный диалог по результатам работы
        // ---------------------------------------------------------------------
        private static void ShowSummary(
            int adjusted,
            int total,
            bool enabledNow,
            bool alongBottomFace,
            double thicknessMm,
            List<string> errors)
        {
            string content = string.Format("Вершин выровнено: {0} из {1}", adjusted, total);

            if (thicknessMm <= 0.0)
            {
                content += "\nСмещение: 0.0 мм — толщина конструкции не определена, " +
                           "выравнивание по верхней грани.";
            }
            else if (alongBottomFace)
            {
                content += string.Format(
                    "\nСмещение: +{0:F1} мм — по нижней грани (толщина конструкции {0:F1} мм).",
                    thicknessMm);
            }
            else
            {
                content += string.Format(
                    "\nСмещение: 0.0 мм — по верхней грани (толщина конструкции {0:F1} мм).",
                    thicknessMm);
            }

            if (enabledNow)
                content += "\nРедактирование формы было включено автоматически.";

            if (errors.Count > 0)
            {
                content += string.Format("\nНе удалось выровнять: {0}", errors.Count);

                int shown = Math.Min(errors.Count, MaxReportedErrors);
                for (int i = 0; i < shown; i++)
                    content += "\n  • " + errors[i];

                if (errors.Count > shown)
                    content += string.Format("\n  … и еще {0}", errors.Count - shown);
            }

            TaskDialog dlg = new TaskDialog("IvanTools — AdjustToFace");
            dlg.MainInstruction = errors.Count == 0
                ? "Вершины выровнены по плоскости грани"
                : "Готово с замечаниями";
            dlg.MainContent = content;
            dlg.Show();
        }
    }

    // -------------------------------------------------------------------------
    // Цель по одной вершине: исходное положение + требуемая отметка Z
    // (отдельный класс вместо кортежа — совместимость с C# 5)
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
    // Разрешаем кликать только перекрытия и кровли
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
    // Для грани: запрещаем брать грань самого хост-элемента
    // (иначе выравнивание идет «по самому себе» и результат непредсказуем)
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
