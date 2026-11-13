// =============================================================================
// ExportToGlb — экспорт активного 3D-вида Revit 2023 в glTF 2.0 (.glb)
// =============================================================================
// Из активного View3D собирается треугольная геометрия (IExportContext через
// CustomExporter), материалы PBR (цвет / текстура / прозрачность / гладкость /
// металличность) и дерево нод по элементам модели. Запись .glb выполняется
// собственным минимальным писателем glTF 2.0 — без внешних NuGet-библиотек.
//
// Environment : Autodesk Revit 2023 (.NET Framework 4.8), C# 5
//
// Особенности реализации:
//   - IncludeGeometricObjects = false: колбэки граней (OnFaceBegin/OnFaceEnd)
//     не приходят вовсе, но триангуляция всё равно приходит в OnPolymesh.
//     Это документированное поведение и ускорение экспорта, а не ошибка.
//   - Section Box, скрытые категории/элементы и фазы учитывает сам Revit:
//     экспортируется ровно то, что видно на виде.
//   - Оси: вершины остаются в футах Revit, переход "Z-up, футы" -> "Y-up, метры"
//     выполняется матрицей на корневой ноде BasisTransform:
//         p' = (0.3048*x, 0.3048*z, -0.3048*y)
//     Определитель +1 => порядок обхода треугольников НЕ инвертируется.
//   - UV: V' = -V (а не 1-V: иначе ломается тайлинг при UV вне [0..1]).
//     Диапазон UV в документации Revit не определён, поэтому плагин пишет
//     отладочный лог %TEMP%\ExportToGlb_uv.log (первые 30 полимешей) —
//     по нему принимается решение о необходимости RealWorldScale.
//   - Ошибка на отдельном элементе не отменяет экспорт: собираются warnings.
//
// Запуск:
//   1) через CsHotLoader (Run Script) — положить этот .cs в
//      C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
//   2) либо собрать ExportToGlb.dll и добавить блок <AddIn> в IvanTools.addin:
//      csc /target:library /out:ExportToGlb.dll ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPI.dll" ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPIUI.dll" ^
//          /reference:System.Drawing.dll ^
//          /reference:System.Windows.Forms.dll ExportToGlb.cs
//
// Credits:
//   Ivan Rodionov — идея, постановка задачи, тестирование
//
// License: MIT
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportToGlbCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (commandData == null || commandData.Application == null)
                return Result.Cancelled;

            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("IvanTools — Export To GLB",
                    "Нет активного документа.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;
            View3D view3d = uidoc.ActiveView as View3D;
            if (view3d == null || view3d.IsTemplate)
            {
                TaskDialog.Show("IvanTools — Export To GLB",
                    "Активный вид не является 3D-видом (или это шаблон вида).\n\n" +
                    "Откройте 3D-вид (например, {3D}) и запустите команду снова.");
                return Result.Cancelled;
            }

            string fileName = Sanitize(Path.GetFileName(doc.Title + "_" + view3d.Name)) + ".glb";
            string path;
            using (System.Windows.Forms.SaveFileDialog dlg = new System.Windows.Forms.SaveFileDialog())
            {
                dlg.Title = "Экспорт активного 3D-вида в GLB";
                dlg.Filter = "glTF Binary (*.glb)|*.glb";
                dlg.DefaultExt = "glb";
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                dlg.FileName = fileName;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;
                path = dlg.FileName;
            }
            DateTime started = DateTime.Now;
            GlbExportContext ctx = null;
            try
            {
                ctx = new GlbExportContext(doc, view3d);
                using (CustomExporter exporter = new CustomExporter(doc, ctx))
                {
                    exporter.IncludeGeometricObjects = false;
                    exporter.ShouldStopOnError = false;
                    exporter.Export(view3d);
                }

                if (ctx.Scene.TriangleCount == 0)
                {
                    TaskDialog.Show("IvanTools — Export To GLB",
                        "В виде не найдено ни одного треугольника.\n\n" +
                        "Вид в режиме «Каркас» не даёт граней: переключите вид на\n" +
                        "«Скрытая линия», «Заливка» или «Реалистичный» и повторите экспорт.\n\n" +
                        "Файл не создан.");
                    return Result.Cancelled;
                }

                ctx.Scene.WriteTo(path);
                double seconds = (DateTime.Now - started).TotalSeconds;
                TaskDialog.Show("IvanTools — Export To GLB", BuildReport(path, ctx.Scene, seconds));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("IvanTools — Export To GLB",
                    "Экспорт прерван ошибкой:\n\n" + ex.ToString());
                return Result.Failed;
            }
        }

        private static string BuildReport(string path, GlbScene scene, double seconds)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Файл: " + path);
            sb.AppendLine("Время: " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " с");
            sb.AppendLine();
            sb.AppendLine("Элементов: " + scene.ElementCount.ToString());
            sb.AppendLine("Мешей: " + scene.Meshes.Count.ToString());
            sb.AppendLine("Примитивов: " + scene.PrimitiveCount.ToString());
            sb.AppendLine("Треугольников: " + scene.TriangleCount.ToString());
            sb.AppendLine("Материалов: " + scene.Materials.Count.ToString());
            sb.AppendLine("Текстур: " + scene.Images.Count.ToString());

            if (scene.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Предупреждения (" + scene.Warnings.Count.ToString() + "):");
                int max = scene.Warnings.Count < 10 ? scene.Warnings.Count : 10;
                for (int i = 0; i < max; i++)
                    sb.AppendLine("  • " + scene.Warnings[i]);
                if (scene.Warnings.Count > max)
                    sb.AppendLine("  … и ещё " + (scene.Warnings.Count - max).ToString());
            }

            return sb.ToString();
        }

        // Имя файла / имя ноды: без переводов строк и символов, ломающих XML/JSON
        internal static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "Value";

            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\r' || c == '\n' || c == '\t' || c == ':' || c == '"' ||
                    c == '/' || c == '\\' || c == '|' || c == '*' || c == '?' ||
                    c == '<' || c == '>')
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            string result = sb.ToString();
            while (result.IndexOf("__", StringComparison.Ordinal) >= 0)
                result = result.Replace("__", "_");
            return result.Trim();
        }
    }
    // =========================================================================
    // IExportContext: обход сцены Revit
    // =========================================================================
    internal sealed class GlbExportContext : IExportContext
    {
        // Перевод футов Revit в метры (единицы glTF)
        internal const double FeetToMeters = 0.3048;

        private readonly Document _doc;
        private readonly View3D _view;
        private readonly GlbScene _scene = new GlbScene();
        private readonly MaterialLibrary _materials;
        private readonly List<Transform> _transforms = new List<Transform>();
        private readonly List<Document> _docs = new List<Document>();
        private readonly List<ElementState> _elements = new List<ElementState>();
        private readonly List<GlbNode> _nodePath = new List<GlbNode>();

        private GlbMaterial _currentMaterial;
        private int _uvProbeCount;
        private bool _canceled = false;

        public GlbExportContext(Document doc, View3D view)
        {
            _doc = doc;
            _view = view;
            _materials = new MaterialLibrary(_scene);
        }

        public GlbScene Scene { get { return _scene; } }

        private Transform CurrentTransform
        {
            get { return _transforms[_transforms.Count - 1]; }
        }

        private Document CurrentDoc
        {
            get { return _docs[_docs.Count - 1]; }
        }

        private GlbNode CurrentParent
        {
            get { return _nodePath[_nodePath.Count - 1]; }
        }

        // ---------------------------------------------------------------------
        // Служебные колбэки
        // ---------------------------------------------------------------------
        public bool Start()
        {
            _scene.Name = _view.Name;
            _transforms.Clear();
            _transforms.Add(Transform.Identity);
            _docs.Clear();
            _docs.Add(_doc);
            _elements.Clear();
            _nodePath.Clear();

            // Корневая нода: футы -> метры и Z-up -> Y-up одной матрицей.
            // glTF хранит матрицы по столбцам; определитель = +1 (winding не меняется).
            float h = (float)FeetToMeters;
            GlbNode basis = new GlbNode("BasisTransform");
            basis.Matrix = new float[]
            {
                h, 0f, 0f, 0f,
                0f, 0f, -h, 0f,
                0f, h, 0f, 0f,
                0f, 0f, 0f, 1f
            };
            _scene.Roots.Add(basis);
            _nodePath.Add(basis);

            _scene.StartTime = DateTime.Now;
            UvProbe.Begin();
            return true;
        }

        public void Finish()
        {
            _scene.EndTime = DateTime.Now;
        }

        // Прогресс/отмена: у CustomExporter нет колбэков прогресса; возврат true
        // здесь прерывает экспорт (в текущей версии отмены из UI нет).
        public bool IsCanceled()
        {
            return _canceled;
        }

        public RenderNodeAction OnViewBegin(ViewNode node)
        {
            return RenderNodeAction.Proceed;
        }

        public void OnViewEnd(ElementId elementId)
        {
        }

        // ---------------------------------------------------------------------
        // Элементы
        // ---------------------------------------------------------------------
        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            try
            {
                ElementState state = new ElementState();
                state.Mesh = new GlbMesh(MakeElementName(elementId));
                _elements.Add(state);
                _scene.ElementCount++;
            }
            catch (Exception ex)
            {
                Warn("OnElementBegin: " + ex.Message);
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            try
            {
                if (_elements.Count == 0)
                    return;

                ElementState state = _elements[_elements.Count - 1];
                _elements.RemoveAt(_elements.Count - 1);

                // Пустой меш (элемент целиком за Section Box / без граней) не пишем
                if (!state.Mesh.HasGeometry())
                    return;

                int meshIndex = _scene.AddMesh(state.Mesh);
                GlbNode node = new GlbNode(state.Mesh.Name);
                node.Mesh = meshIndex;
                CurrentParent.Children.Add(node);
            }
            catch (Exception ex)
            {
                Warn("OnElementEnd: " + ex.Message);
            }
        }

        private string MakeElementName(ElementId id)
        {
            if (id == ElementId.InvalidElementId)
                return "Default";

            string category = "NoCategory";
            string name = "Element";
            try
            {
                Element e = CurrentDoc.GetElement(id);
                if (e != null)
                {
                    if (e.Category != null && !string.IsNullOrEmpty(e.Category.Name))
                        category = e.Category.Name;
                    if (!string.IsNullOrEmpty(e.Name))
                        name = e.Name;
                }
            }
            catch
            {
                // имя — не повод прерывать экспорт
            }

            return ExportToGlbCommand.Sanitize(category + "_" + name + "_" + id.ToString());
        }
        // ---------------------------------------------------------------------
        // Вхождения (трансформации) и связи
        // ---------------------------------------------------------------------
        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            try
            {
                // GetTransform() объявлен в базовом GroupNode (общий для InstanceNode и LinkNode)
                _transforms.Add(CurrentTransform.Multiply(node.GetTransform()));
            }
            catch (Exception ex)
            {
                Warn("OnInstanceBegin: " + ex.Message);
                _transforms.Add(CurrentTransform);
            }

            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node)
        {
            PopTransform();
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            try
            {
                _transforms.Add(CurrentTransform.Multiply(node.GetTransform()));

                Document linkDoc = node.GetDocument();
                _docs.Add(linkDoc != null ? linkDoc : CurrentDoc);

                // Группирующая нода связи: элементы связи будут её детьми
                string linkName = "Link";
                try
                {
                    if (!string.IsNullOrEmpty(node.NodeName))
                        linkName = node.NodeName;
                    else if (linkDoc != null && !string.IsNullOrEmpty(linkDoc.Title))
                        linkName = linkDoc.Title;
                }
                catch
                {
                }

                GlbNode linkNode = new GlbNode("Link_" + ExportToGlbCommand.Sanitize(linkName));
                CurrentParent.Children.Add(linkNode);
                _nodePath.Add(linkNode);
            }
            catch (Exception ex)
            {
                Warn("OnLinkBegin: " + ex.Message);
                _transforms.Add(CurrentTransform);
                _docs.Add(CurrentDoc);
                _nodePath.Add(CurrentParent);
            }

            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            if (_nodePath.Count > 1)
                _nodePath.RemoveAt(_nodePath.Count - 1);
            if (_docs.Count > 1)
                _docs.RemoveAt(_docs.Count - 1);
            PopTransform();
        }

        private void PopTransform()
        {
            if (_transforms.Count > 1)
                _transforms.RemoveAt(_transforms.Count - 1);
        }

        private void Warn(string text)
        {
            if (_scene.Warnings.Count < 200)
                _scene.Warnings.Add(text);
        }
        // ---------------------------------------------------------------------
        // Грани, материалы, полимеши
        // ---------------------------------------------------------------------
        public RenderNodeAction OnFaceBegin(FaceNode node)
        {
            // При IncludeGeometricObjects = false этот метод не вызывается вовсе
            return RenderNodeAction.Proceed;
        }

        public void OnFaceEnd(FaceNode node)
        {
        }

        public void OnRPC(RPCNode node)
        {
        }

        public void OnLight(LightNode node)
        {
        }

        public void OnMaterial(MaterialNode node)
        {
            try
            {
                _currentMaterial = _materials.Get(node, CurrentDoc);
            }
            catch (Exception ex)
            {
                Warn("OnMaterial: " + ex.Message);
                _currentMaterial = _materials.DefaultMaterial;
            }
        }

        public void OnPolymesh(PolymeshTopology node)
        {
            try
            {
                BuildPolymesh(node);
            }
            catch (Exception ex)
            {
                Warn("OnPolymesh: " + ex.Message);
            }
        }

        private void BuildPolymesh(PolymeshTopology node)
        {
            if (_elements.Count == 0 || node == null)
                return;

            ElementState state = _elements[_elements.Count - 1];
            GlbMaterial material = _currentMaterial != null ? _currentMaterial : _materials.DefaultMaterial;

            int pointCount = node.NumberOfPoints;
            int facetCount = node.NumberOfFacets;
            int normalCount = node.NumberOfNormals;
            int uvCount = node.NumberOfUVs;
            if (pointCount <= 0 || facetCount <= 0)
                return;

            Transform xf = CurrentTransform;
            DistributionOfNormals normalsMode = node.DistributionOfNormals;

            GlbPrimitive prim = state.GetPrimitive(material, uvCount > 0);
            int baseIndex = prim.Positions.Count / 3;
            Dictionary<long, int> vertexMap = new Dictionary<long, int>();

            // Этап 0: замер диапазона UV (см. PLAN.md §8)
            if (_uvProbeCount < UvProbe.MaxPolymeshes)
            {
                UvProbe.Log(state.Mesh.Name, node, xf);
                _uvProbeCount++;
            }

            for (int f = 0; f < facetCount; f++)
            {
                PolymeshFacet facet = node.GetFacet(f);
                int i0 = facet.V1;
                int i1 = facet.V2;
                int i2 = facet.V3;

                XYZ p0 = xf.OfPoint(node.GetPoint(i0));
                XYZ p1 = xf.OfPoint(node.GetPoint(i1));
                XYZ p2 = xf.OfPoint(node.GetPoint(i2));

                // Вырожденные треугольники появляются при обрезке Section Box
                XYZ cross = (p1 - p0).CrossProduct(p2 - p0);
                if (cross.DotProduct(cross) <= 1e-12)
                    continue;

                int v0 = GetVertexIndex(prim, vertexMap, baseIndex, node, xf, normalsMode,
                    normalCount, uvCount, pointCount, i0, f);
                int v1 = GetVertexIndex(prim, vertexMap, baseIndex, node, xf, normalsMode,
                    normalCount, uvCount, pointCount, i1, f);
                int v2 = GetVertexIndex(prim, vertexMap, baseIndex, node, xf, normalsMode,
                    normalCount, uvCount, pointCount, i2, f);

                prim.Indices.Add(v0);
                prim.Indices.Add(v1);
                prim.Indices.Add(v2);
                _scene.TriangleCount++;
            }
        }
        // Одна вершина примитива на уникальную комбинацию (точка, нормаль, UV)
        private static int GetVertexIndex(
            GlbPrimitive prim,
            Dictionary<long, int> vertexMap,
            int baseIndex,
            PolymeshTopology node,
            Transform xf,
            DistributionOfNormals normalsMode,
            int normalCount,
            int uvCount,
            int pointCount,
            int pointIndex,
            int facetIndex)
        {
            int normalIndex;
            switch (normalsMode)
            {
                case DistributionOfNormals.OnePerFace:
                    normalIndex = 0;
                    break;
                case DistributionOfNormals.OnEachFacet:
                    normalIndex = facetIndex;
                    break;
                default:
                    normalIndex = pointIndex;
                    break;
            }
            if (normalCount <= 0)
                normalIndex = -1;
            else if (normalIndex >= normalCount)
                normalIndex = 0;

            int uvIndex = -1;
            if (uvCount > 0)
            {
                if (uvCount == pointCount) uvIndex = pointIndex;
                else if (uvCount == 1) uvIndex = 0;
                else uvIndex = pointIndex < uvCount ? pointIndex : 0;
            }

            long key = ((long)((normalIndex + 1) & 0x1FFFFF) << 42)
                     | ((long)((uvIndex + 1) & 0x1FFFFF) << 21)
                     | (long)((uint)pointIndex & 0x1FFFFF);
            int existing;
            if (vertexMap.TryGetValue(key, out existing))
                return existing;

            XYZ p = xf.OfPoint(node.GetPoint(pointIndex));
            prim.Positions.Add((float)p.X);
            prim.Positions.Add((float)p.Y);
            prim.Positions.Add((float)p.Z);

            if (normalIndex >= 0)
            {
                XYZ n = xf.OfVector(node.GetNormal(normalIndex)).Normalize();
                prim.Normals.Add((float)n.X);
                prim.Normals.Add((float)n.Y);
                prim.Normals.Add((float)n.Z);
            }
            else
            {
                prim.Normals.Add(0f);
                prim.Normals.Add(0f);
                prim.Normals.Add(1f);
            }

            if (prim.HasUv)
            {
                UV uv = node.GetUV(uvIndex);
                prim.Uvs.Add((float)uv.U);
                // glTF: origin текстуры сверху => инверсия V (при тайлинге 1-V неверно)
                prim.Uvs.Add((float)(-uv.V));
            }

            int index = baseIndex + vertexMap.Count;
            vertexMap[key] = index;
            return index;
        }
    }

    // Состояние сборки меша текущего элемента (по одному примитиву на материал)
    internal sealed class ElementState
    {
        public GlbMesh Mesh;

        public GlbPrimitive GetPrimitive(GlbMaterial material, bool hasUv)
        {
            for (int i = 0; i < Mesh.Primitives.Count; i++)
            {
                GlbPrimitive p = Mesh.Primitives[i];
                if (object.ReferenceEquals(p.Material, material) && p.HasUv == hasUv)
                    return p;
            }

            GlbPrimitive prim = new GlbPrimitive();
            prim.Material = material;
            prim.HasUv = hasUv;
            Mesh.Primitives.Add(prim);
            return prim;
        }
    }
    // =========================================================================
    // Материалы: цвет, текстура, прозрачность, гладкость, металличность
    // =========================================================================
    internal sealed class MaterialLibrary
    {
        // Ключи ассетов Revit: значение проверяется по факту (см. PLAN.md §7.2),
        // поэтому перебираем известные варианты, а не полагаемся на один.
        private static readonly string[] DiffuseKeys =
        {
            "generic_diffuse", "wallpaint_color", "masonrycmu_color", "metal_color",
            "wood_color", "plasticvinyl_color", "ceramic_color", "stone_color",
            "concrete_color", "glazing_transmittance_color"
        };

        private static readonly string[] BitmapKeys =
        {
            "unifiedbitmap_Bitmap", "unifiedbitmap_bitmap", "bitmap"
        };

        private readonly GlbScene _scene;
        private readonly Dictionary<string, GlbMaterial> _cache = new Dictionary<string, GlbMaterial>();
        private readonly Dictionary<string, int> _imageCache = new Dictionary<string, int>();
        private readonly Dictionary<string, bool> _missing = new Dictionary<string, bool>();
        private readonly GlbMaterial _defaultMaterial;
        private static string _texturesRoot;

        public MaterialLibrary(GlbScene scene)
        {
            _scene = scene;
            _defaultMaterial = new GlbMaterial();
            _defaultMaterial.Name = "Default";
            _defaultMaterial.R = 0.72f;
            _defaultMaterial.G = 0.72f;
            _defaultMaterial.B = 0.72f;
            _defaultMaterial.A = 1f;
            _defaultMaterial.Roughness = 0.6f;
            _defaultMaterial.Metallic = 0f;
            _defaultMaterial.DoubleSided = true;
            _defaultMaterial.ImageIndex = -1;
            _scene.Materials.Add(_defaultMaterial);
        }

        public GlbMaterial DefaultMaterial
        {
            get { return _defaultMaterial; }
        }

        public GlbMaterial Get(MaterialNode node, Document doc)
        {
            if (node == null)
                return _defaultMaterial;

            double transparency = node.Transparency;
            if (transparency < 0) transparency = 0;
            if (transparency > 1) transparency = 1;
            float alpha = (float)(1.0 - transparency);
            if (alpha < 0.02f) alpha = 0.02f;

            bool isDefault = node.MaterialId == ElementId.InvalidElementId;
            string alphaKey = alpha.ToString("0.###", CultureInfo.InvariantCulture);
            string key = (isDefault
                ? "default"
                : (doc != null ? doc.GetHashCode().ToString(CultureInfo.InvariantCulture) : "0") +
                  ":" + node.MaterialId.ToString()) + ":" + alphaKey;

            GlbMaterial cached;
            if (_cache.TryGetValue(key, out cached))
                return cached;

            GlbMaterial material = Build(node, alpha);
            _cache[key] = material;
            return material;
        }
        private GlbMaterial Build(MaterialNode node, float alpha)
        {
            GlbMaterial m = new GlbMaterial();
            m.A = alpha;
            m.Blend = alpha < 0.999f;
            m.DoubleSided = true;
            m.Metallic = 0f;
            m.Roughness = 0.5f;
            m.ImageIndex = -1;

            Color color = null;
            try { color = node.Color; }
            catch { color = null; }

            if (color != null)
            {
                m.R = color.Red / 255f;
                m.G = color.Green / 255f;
                m.B = color.Blue / 255f;
            }
            else
            {
                m.R = 0.72f;
                m.G = 0.72f;
                m.B = 0.72f;
            }

            string title = null;
            Asset asset = null;
            try
            {
                asset = node.HasOverriddenAppearance ? node.GetAppearanceOverride() : node.GetAppearance();
            }
            catch
            {
                asset = null;
            }

            if (asset != null)
            {
                try { title = asset.Title; }
                catch { title = null; }

                // Гладкость -> шероховатость (документированный диапазон 0..1)
                try
                {
                    AssetPropertyDouble glossiness = asset.FindByName("generic_glossiness") as AssetPropertyDouble;
                    if (glossiness != null && glossiness.Value >= 0 && glossiness.Value <= 1)
                        m.Roughness = (float)(1.0 - glossiness.Value);
                }
                catch
                {
                }

                // Металличность: канал metal_color либо флаг generic_is_metal
                try
                {
                    if (asset.FindByName("metal_color") != null)
                    {
                        m.Metallic = 0.9f;
                    }
                    else
                    {
                        AssetPropertyInteger isMetal = asset.FindByName("generic_is_metal") as AssetPropertyInteger;
                        if (isMetal != null && isMetal.Value != 0)
                            m.Metallic = 0.9f;
                    }
                }
                catch
                {
                }

                // Текстура base color: текстура перекрывает цвет, фактор = белый
                try
                {
                    string texturePath = FindTexturePath(asset);
                    if (!string.IsNullOrEmpty(texturePath))
                    {
                        int imageIndex = GetImageIndex(texturePath);
                        if (imageIndex >= 0)
                        {
                            m.ImageIndex = imageIndex;
                            m.R = 1f;
                            m.G = 1f;
                            m.B = 1f;
                        }
                    }
                }
                catch
                {
                }
            }

            if (m.Roughness < 0.02f) m.Roughness = 0.02f;
            if (m.Roughness > 1f) m.Roughness = 1f;

            string id = node.MaterialId == ElementId.InvalidElementId
                ? "Default"
                : node.MaterialId.ToString();
            string baseName = string.IsNullOrEmpty(title) ? "Material" : title;
            m.Name = ExportToGlbCommand.Sanitize(baseName + "_" + id);

            _scene.Materials.Add(m);
            return m;
        }

        private static string FindTexturePath(Asset asset)
        {
            for (int i = 0; i < DiffuseKeys.Length; i++)
            {
                AssetProperty prop = null;
                try { prop = asset.FindByName(DiffuseKeys[i]); }
                catch { prop = null; }
                if (prop == null)
                    continue;

                Asset connected = null;
                try { connected = prop.GetSingleConnectedAsset(); }
                catch { connected = null; }
                if (connected == null)
                    continue;

                string path = GetBitmapPath(connected);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }

            return null;
        }

        private static string GetBitmapPath(Asset asset)
        {
            for (int i = 0; i < BitmapKeys.Length; i++)
            {
                AssetPropertyString text = null;
                try { text = asset.FindByName(BitmapKeys[i]) as AssetPropertyString; }
                catch { text = null; }
                if (text != null && !string.IsNullOrEmpty(text.Value))
                    return text.Value;
            }

            return null;
        }
        private int GetImageIndex(string rawPath)
        {
            string path = ResolveImagePath(rawPath);
            if (path == null)
            {
                ReportMissingTexture(rawPath);
                return -1;
            }

            int cachedIndex;
            if (_imageCache.TryGetValue(path, out cachedIndex))
                return cachedIndex;

            byte[] data;
            string mimeType;
            if (!TryLoadImage(path, out data, out mimeType))
            {
                ReportMissingTexture(rawPath);
                return -1;
            }

            GlbImage image = new GlbImage();
            image.Data = data;
            image.MimeType = mimeType;

            int index = _scene.Images.Count;
            _scene.Images.Add(image);
            _imageCache[path] = index;
            return index;
        }

        // Абсолютный путь принимается как есть, иначе ищется файл с тем же именем
        // в библиотеке текстур Autodesk (%ProgramFiles(x86)%\Common Files\Autodesk Shared\...)
        private static string ResolveImagePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
                return null;

            try
            {
                if (Path.IsPathRooted(rawPath) && File.Exists(rawPath))
                    return rawPath;
            }
            catch
            {
            }

            string fileName = null;
            try { fileName = Path.GetFileName(rawPath); }
            catch { fileName = null; }
            if (string.IsNullOrEmpty(fileName))
                return null;

            string root = TexturesRoot;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                try
                {
                    string[] found = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
                    if (found.Length > 0)
                        return found[0];
                }
                catch
                {
                }
            }

            try
            {
                if (File.Exists(rawPath))
                    return Path.GetFullPath(rawPath);
            }
            catch
            {
            }

            return null;
        }

        private static string TexturesRoot
        {
            get
            {
                if (_texturesRoot == null)
                {
                    string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
                    if (string.IsNullOrEmpty(common))
                        common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
                    _texturesRoot = Path.Combine(common, "Autodesk Shared\\Materials\\Textures");
                }
                return _texturesRoot;
            }
        }

        // glTF 2.0 поддерживает только PNG и JPEG: остальное конвертируем в PNG
        private static bool TryLoadImage(string path, out byte[] data, out string mimeType)
        {
            data = null;
            mimeType = null;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".png")
            {
                data = File.ReadAllBytes(path);
                mimeType = "image/png";
                return data.Length > 0;
            }
            if (extension == ".jpg" || extension == ".jpeg")
            {
                data = File.ReadAllBytes(path);
                mimeType = "image/jpeg";
                return data.Length > 0;
            }

            byte[] raw = File.ReadAllBytes(path);
            using (MemoryStream input = new MemoryStream(raw))
            using (System.Drawing.Image image = System.Drawing.Image.FromStream(input))
            using (MemoryStream output = new MemoryStream())
            {
                image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                data = output.ToArray();
                mimeType = "image/png";
            }

            return data.Length > 0;
        }

        private void ReportMissingTexture(string rawPath)
        {
            if (_missing.ContainsKey(rawPath))
                return;
            _missing[rawPath] = true;
            if (_scene.Warnings.Count < 200)
                _scene.Warnings.Add("Текстура не найдена: " + rawPath);
        }
    }

    // =========================================================================
    // Данные сцены (вместо кортежей — отдельные классы: C# 5)
    // =========================================================================
    internal sealed class GlbScene
    {
        public string Name = "";
        public readonly List<GlbNode> Roots = new List<GlbNode>();
        public readonly List<GlbMesh> Meshes = new List<GlbMesh>();
        public readonly List<GlbMaterial> Materials = new List<GlbMaterial>();
        public readonly List<GlbImage> Images = new List<GlbImage>();
        public readonly List<string> Warnings = new List<string>();

        public int ElementCount;
        public int TriangleCount;
        public DateTime StartTime;
        public DateTime EndTime;

        public int PrimitiveCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Meshes.Count; i++)
                    count += Meshes[i].Primitives.Count;
                return count;
            }
        }

        public int AddMesh(GlbMesh mesh)
        {
            Meshes.Add(mesh);
            return Meshes.Count - 1;
        }

        public void WriteTo(string path)
        {
            GlbWriter writer = new GlbWriter(this);
            writer.Save(path);
        }
    }
    internal sealed class GlbNode
    {
        public string Name;
        public int Mesh = -1;
        public float[] Matrix;
        public readonly List<GlbNode> Children = new List<GlbNode>();

        public GlbNode(string name)
        {
            Name = name;
        }
    }

    internal sealed class GlbMesh
    {
        public string Name;
        public readonly List<GlbPrimitive> Primitives = new List<GlbPrimitive>();

        public GlbMesh(string name)
        {
            Name = name;
        }

        public bool HasGeometry()
        {
            for (int i = 0; i < Primitives.Count; i++)
            {
                if (Primitives[i].Indices.Count > 0)
                    return true;
            }
            return false;
        }
    }

    internal sealed class GlbPrimitive
    {
        public GlbMaterial Material;
        public bool HasUv;
        public readonly List<float> Positions = new List<float>();
        public readonly List<float> Normals = new List<float>();
        public readonly List<float> Uvs = new List<float>();
        public readonly List<int> Indices = new List<int>();
    }

    internal sealed class GlbMaterial
    {
        public string Name = "Material";
        public float R = 0.72f;
        public float G = 0.72f;
        public float B = 0.72f;
        public float A = 1f;
        public float Metallic;
        public float Roughness = 0.5f;
        public bool DoubleSided = true;
        public bool Blend;
        public int ImageIndex = -1;
    }

    internal sealed class GlbImage
    {
        public byte[] Data;
        public string MimeType = "image/png";
    }

    // =========================================================================
    // Этап 0: замер UV (PLAN.md §8). Лог: %TEMP%\ExportToGlb_uv.log
    // =========================================================================
    internal static class UvProbe
    {
        internal const int MaxPolymeshes = 30;
        private static string _path;
        private static int _written;

        internal static void Begin()
        {
            _written = 0;
            try
            {
                _path = Path.Combine(Path.GetTempPath(), "ExportToGlb_uv.log");
                File.WriteAllText(_path,
                    "ExportToGlb: замер UV (элемент; точки; UV; minU; maxU; minV; maxV; габарит полимеша, фут)" +
                    Environment.NewLine);
            }
            catch
            {
                _path = null;
            }
        }

        internal static void Log(string elementName, PolymeshTopology node, Transform xf)
        {
            if (_path == null || _written >= MaxPolymeshes)
                return;

            try
            {
                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;
                int uvCount = node.NumberOfUVs;
                for (int i = 0; i < uvCount; i++)
                {
                    UV uv = node.GetUV(i);
                    if (uv.U < minU) minU = uv.U;
                    if (uv.U > maxU) maxU = uv.U;
                    if (uv.V < minV) minV = uv.V;
                    if (uv.V > maxV) maxV = uv.V;
                }

                // Габарит полимеша: показывает, растут ли UV вместе с размером грани
                XYZ min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
                XYZ max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
                for (int i = 0; i < node.NumberOfPoints; i++)
                {
                    XYZ p = xf.OfPoint(node.GetPoint(i));
                    min = new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                    max = new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
                }
                double size = (max - min).GetLength();

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0};{1};{2};{3:0.####};{4:0.####};{5:0.####};{6:0.####};{7:0.###}",
                    elementName, node.NumberOfPoints, uvCount,
                    uvCount > 0 ? minU : 0, uvCount > 0 ? maxU : 0,
                    uvCount > 0 ? minV : 0, uvCount > 0 ? maxV : 0, size);

                File.AppendAllText(_path, line + Environment.NewLine);
                _written++;
            }
            catch
            {
                // лог не должен ломать экспорт
            }
        }
    }
    // =========================================================================
    // Запись GLB (glTF 2.0, binary): контейнер + JSON + BIN
    // =========================================================================
    internal sealed class GlbWriter
    {
        private readonly GlbScene _scene;
        private readonly MemoryStream _bin = new MemoryStream();
        private readonly List<string> _bufferViews = new List<string>();
        private readonly List<string> _accessors = new List<string>();
        private readonly List<string> _meshesJson = new List<string>();

        public GlbWriter(GlbScene scene)
        {
            _scene = scene;
        }

        public void Save(string path)
        {
            _bin.SetLength(0);
            _bufferViews.Clear();
            _accessors.Clear();
            _meshesJson.Clear();

            BuildMeshes();
            BuildImages();

            string json = BuildJson();
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonLength = jsonBytes.Length;
            int jsonPadded = (jsonLength + 3) & ~3;

            byte[] binary = _bin.ToArray();
            int binaryPadded = (binary.Length + 3) & ~3;
            int total = 12 + 8 + jsonPadded + (binary.Length > 0 ? 8 + binaryPadded : 0);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(0x46546C67u);          // "glTF"
                writer.Write(2u);                   // версия контейнера
                writer.Write((uint)total);

                writer.Write((uint)jsonPadded);
                writer.Write(0x4E4F534Au);          // "JSON"
                writer.Write(jsonBytes);
                for (int i = jsonLength; i < jsonPadded; i++)   // паддинг пробелами
                    writer.Write((byte)0x20);

                if (binary.Length > 0)
                {
                    writer.Write((uint)binaryPadded);
                    writer.Write(0x004E4942u);      // "BIN\0"
                    writer.Write(binary);
                    for (int i = binary.Length; i < binaryPadded; i++)   // паддинг нулями
                        writer.Write((byte)0);
                }

                writer.Flush();
            }
        }

        private void BuildMeshes()
        {
            for (int mi = 0; mi < _scene.Meshes.Count; mi++)
            {
                GlbMesh mesh = _scene.Meshes[mi];
                StringBuilder primitives = new StringBuilder();

                for (int pi = 0; pi < mesh.Primitives.Count; pi++)
                {
                    GlbPrimitive prim = mesh.Primitives[pi];
                    if (prim.Indices.Count == 0 || prim.Positions.Count == 0)
                        continue;

                    int positionAccessor = WriteFloatAccessor(prim.Positions, 3, true);
                    int normalAccessor = WriteFloatAccessor(prim.Normals, 3, false);
                    int uvAccessor = prim.HasUv && prim.Uvs.Count > 0
                        ? WriteFloatAccessor(prim.Uvs, 2, false)
                        : -1;
                    int indexAccessor = WriteIndexAccessor(prim.Indices);

                    if (primitives.Length > 0)
                        primitives.Append(',');
                    primitives.Append("{\"attributes\":{\"POSITION\":").Append(positionAccessor);
                    primitives.Append(",\"NORMAL\":").Append(normalAccessor);
                    if (uvAccessor >= 0)
                        primitives.Append(",\"TEXCOORD_0\":").Append(uvAccessor);
                    primitives.Append("},\"indices\":").Append(indexAccessor);

                    int materialIndex = _scene.Materials.IndexOf(prim.Material);
                    if (materialIndex >= 0)
                        primitives.Append(",\"material\":").Append(materialIndex);

                    primitives.Append(",\"mode\":4}");
                }

                if (primitives.Length == 0)
                {
                    _meshesJson.Add("{\"name\":\"" + JsonEscape(mesh.Name) + "\",\"primitives\":[]}");
                    continue;
                }

                _meshesJson.Add("{\"name\":\"" + JsonEscape(mesh.Name) +
                    "\",\"primitives\":[" + primitives.ToString() + "]}");
            }
        }
        private int WriteFloatAccessor(List<float> data, int components, bool withBounds)
        {
            float[] values = data.ToArray();
            byte[] bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

            int offset = (int)_bin.Position;
            _bin.Write(bytes, 0, bytes.Length);

            _bufferViews.Add("{\"buffer\":0,\"byteOffset\":" + offset.ToString(CultureInfo.InvariantCulture) +
                ",\"byteLength\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ",\"target\":34962}");

            int count = values.Length / components;
            StringBuilder fragment = new StringBuilder();
            fragment.Append("{\"bufferView\":").Append(_bufferViews.Count - 1);
            fragment.Append(",\"componentType\":5126,\"count\":").Append(count);
            fragment.Append(",\"type\":\"").Append(components == 3 ? "VEC3" : "VEC2").Append('"');

            if (withBounds && count > 0)
            {
                // POSITION обязан содержать min/max по спецификации glTF
                fragment.Append(",\"min\":[");
                fragment.Append(Float(MinComponent(values, components, 0))).Append(',');
                fragment.Append(Float(MinComponent(values, components, 1))).Append(',');
                fragment.Append(Float(MinComponent(values, components, 2)));
                fragment.Append("],\"max\":[");
                fragment.Append(Float(MaxComponent(values, components, 0))).Append(',');
                fragment.Append(Float(MaxComponent(values, components, 1))).Append(',');
                fragment.Append(Float(MaxComponent(values, components, 2)));
                fragment.Append(']');
            }

            fragment.Append('}');
            _accessors.Add(fragment.ToString());
            return _accessors.Count - 1;
        }

        private int WriteIndexAccessor(List<int> data)
        {
            int[] values = data.ToArray();
            byte[] bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);

            int offset = (int)_bin.Position;
            _bin.Write(bytes, 0, bytes.Length);

            _bufferViews.Add("{\"buffer\":0,\"byteOffset\":" + offset.ToString(CultureInfo.InvariantCulture) +
                ",\"byteLength\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ",\"target\":34963}");

            _accessors.Add("{\"bufferView\":" + (_bufferViews.Count - 1).ToString(CultureInfo.InvariantCulture) +
                ",\"componentType\":5125,\"count\":" + values.Length.ToString(CultureInfo.InvariantCulture) +
                ",\"type\":\"SCALAR\"}");
            return _accessors.Count - 1;
        }

        private void BuildImages()
        {
            for (int i = 0; i < _scene.Images.Count; i++)
            {
                GlbImage image = _scene.Images[i];
                if (image.Data == null || image.Data.Length == 0)
                {
                    _bufferViews.Add("{\"buffer\":0,\"byteOffset\":0,\"byteLength\":0}");
                    continue;
                }

                int offset = (int)_bin.Position;
                _bin.Write(image.Data, 0, image.Data.Length);
                _bufferViews.Add("{\"buffer\":0,\"byteOffset\":" + offset.ToString(CultureInfo.InvariantCulture) +
                    ",\"byteLength\":" + image.Data.Length.ToString(CultureInfo.InvariantCulture) + "}");

                PadTo4();
            }
        }

        private void PadTo4()
        {
            while ((_bin.Position & 3) != 0)
                _bin.WriteByte(0);
        }

        private string BuildJson()
        {
            // Нумерация нод: обход дерева от корней (дети всегда после родителя)
            List<GlbNode> flat = new List<GlbNode>();
            Dictionary<GlbNode, int> indexByNode = new Dictionary<GlbNode, int>();
            for (int i = 0; i < _scene.Roots.Count; i++)
                NumberNodes(_scene.Roots[i], flat, indexByNode);

            StringBuilder json = new StringBuilder(64 * 1024);
            json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"IvanTools ExportToGlb (Revit 2023)\"}");
            json.Append(",\"scene\":0");

            json.Append(",\"scenes\":[{\"name\":\"").Append(JsonEscape(_scene.Name)).Append("\",\"nodes\":[");
            for (int i = 0; i < _scene.Roots.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(indexByNode[_scene.Roots[i]]);
            }
            json.Append("]}]");

            json.Append(",\"nodes\":[");
            for (int i = 0; i < flat.Count; i++)
            {
                if (i > 0) json.Append(',');
                AppendNodeJson(json, flat[i], indexByNode);
            }
            json.Append(']');

            json.Append(",\"meshes\":[");
            for (int i = 0; i < _meshesJson.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(_meshesJson[i]);
            }
            json.Append(']');

            AppendMaterialsJson(json);
            AppendTexturesJson(json);

            json.Append(",\"accessors\":[");
            for (int i = 0; i < _accessors.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(_accessors[i]);
            }
            json.Append(']');

            json.Append(",\"bufferViews\":[");
            for (int i = 0; i < _bufferViews.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(_bufferViews[i]);
            }
            json.Append(']');

            long binaryLength = _bin.Length;
            json.Append(",\"buffers\":[{\"byteLength\":").Append(binaryLength.ToString(CultureInfo.InvariantCulture));
            json.Append("}]}");

            return json.ToString();
        }
        private static void NumberNodes(GlbNode node, List<GlbNode> flat, Dictionary<GlbNode, int> indexByNode)
        {
            if (indexByNode.ContainsKey(node))
                return;

            indexByNode[node] = flat.Count;
            flat.Add(node);
            for (int i = 0; i < node.Children.Count; i++)
                NumberNodes(node.Children[i], flat, indexByNode);
        }

        private static void AppendNodeJson(StringBuilder json, GlbNode node, Dictionary<GlbNode, int> indexByNode)
        {
            json.Append("{\"name\":\"").Append(JsonEscape(node.Name)).Append('"');

            if (node.Matrix != null && node.Matrix.Length == 16)
            {
                json.Append(",\"matrix\":[");
                for (int i = 0; i < 16; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append(Float(node.Matrix[i]));
                }
                json.Append(']');
            }

            if (node.Mesh >= 0)
                json.Append(",\"mesh\":").Append(node.Mesh);

            if (node.Children.Count > 0)
            {
                json.Append(",\"children\":[");
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    json.Append(indexByNode[node.Children[i]]);
                }
                json.Append(']');
            }

            json.Append('}');
        }
        private void AppendMaterialsJson(StringBuilder json)
        {
            json.Append(",\"materials\":[");
            for (int i = 0; i < _scene.Materials.Count; i++)
            {
                GlbMaterial m = _scene.Materials[i];
                if (i > 0) json.Append(',');

                json.Append("{\"name\":\"").Append(JsonEscape(m.Name)).Append('"');

                if (m.DoubleSided)
                    json.Append(",\"doubleSided\":true");

                if (m.Blend)
                {
                    json.Append(",\"alphaMode\":\"BLEND\"");
                    json.Append(",\"alphaCutoff\":0.01");
                }

                json.Append(",\"pbrMetallicRoughness\":{");
                json.Append("\"baseColorFactor\":[").Append(Float(m.R)).Append(',')
                    .Append(Float(m.G)).Append(',').Append(Float(m.B)).Append(',')
                    .Append(Float(m.A)).Append(']');
                json.Append(",\"metallicFactor\":").Append(Float(m.Metallic));
                json.Append(",\"roughnessFactor\":").Append(Float(m.Roughness));

                if (m.ImageIndex >= 0 && m.ImageIndex < _scene.Images.Count)
                    json.Append(",\"baseColorTexture\":{\"index\":").Append(m.ImageIndex).Append('}');

                json.Append("}}");
            }
            json.Append(']');
        }

        private void AppendTexturesJson(StringBuilder json)
        {
            if (_scene.Images.Count == 0)
                return;

            json.Append(",\"textures\":[");
            for (int i = 0; i < _scene.Images.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"sampler\":0,\"source\":").Append(i).Append('}');
            }
            json.Append(']');

            // BufferView изображений идут последними (после буферов геометрии)
            int firstImageBufferView = _bufferViews.Count - _scene.Images.Count;

            json.Append(",\"images\":[");
            for (int i = 0; i < _scene.Images.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append("{\"bufferView\":").Append(firstImageBufferView + i);
                json.Append(",\"mimeType\":\"").Append(_scene.Images[i].MimeType).Append("\"}");
            }
            json.Append(']');

            json.Append(",\"samplers\":[{\"wrapS\":10497,\"wrapT\":10497," +
                "\"magFilter\":9729,\"minFilter\":9987}]");
        }
        private static float MinComponent(float[] values, int components, int component)
        {
            float result = float.MaxValue;
            for (int i = component; i < values.Length; i += components)
            {
                if (values[i] < result)
                    result = values[i];
            }
            return result == float.MaxValue ? 0f : result;
        }

        private static float MaxComponent(float[] values, int components, int component)
        {
            float result = float.MinValue;
            for (int i = component; i < values.Length; i += components)
            {
                if (values[i] > result)
                    result = values[i];
            }
            return result == float.MinValue ? 0f : result;
        }

        private static string Float(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                value = 0f;
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string JsonEscape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            StringBuilder sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
