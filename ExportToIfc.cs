// =============================================================================
// ExportToIfc — экспорт активного 3D-вида Revit 2023 в IFC
// =============================================================================
// Обёртка над штатным экспортёром Revit (Document.Export + IFCExportOptions):
//   - содержимое файла всегда определяется активным видом (FilterViewId),
//     то есть "что видно на виде — то и в IFC";
//   - три пресета: IFC4 RV (лёгкий), IFC4 (для Теклы), IFC2x3 CV2 (совместимость);
//   - параметрическая передача (IFC4 DTV) не используется: IFC как "запекание";
//   - Section Box не обрабатывается специально: элементы целые и могут выходить
//     за грани куба — это нормальное поведение IFC;
//   - после экспорта файл читается построчно и в диалог выводится сводка:
//     схема, счётчики сущностей, стили, PSets/QTO, предупреждения;
//   - настройки читаются из IvanTools.ini рядом с .rvt (%APPDATA%\IvanTools как
//     личные дефолты); ключи, которых нет в диалоге, уходят в AddOption.
//
// Environment : Autodesk Revit 2023 (.NET Framework 4.8), C# 5
//
// Запуск:
//   1) через CsHotLoader (Run Script) — положить .cs в
//      C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
//   2) либо собрать ExportToIfc.dll и добавить блок <AddIn> в IvanTools.addin:
//      csc /target:library /out:ExportToIfc.dll ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPI.dll" ^
//          /reference:"%ProgramFiles%\Autodesk\Revit 2023\RevitAPIUI.dll" ^
//          /reference:System.Windows.Forms.dll ExportToIfc.cs
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
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// В Revit есть свои Form (DB.Form) и TextBox (UI.TextBox) — разрешаем неоднозначность
using Form = System.Windows.Forms.Form;
using TextBox = System.Windows.Forms.TextBox;
using ComboBox = System.Windows.Forms.ComboBox;
using Control = System.Windows.Forms.Control;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExportToIfc : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (commandData == null || commandData.Application == null)
                return Result.Cancelled;

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("IvanTools — Export To IFC", "Нет активного документа.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;
            View3D view3d = uidoc.ActiveView as View3D;
            if (view3d == null || view3d.IsTemplate)
            {
                TaskDialog.Show("IvanTools — Export To IFC",
                    "Активный вид не является 3D-видом (или это шаблон вида).\n\n" +
                    "Откройте 3D-вид (например, {3D}) и запустите команду снова.");
                return Result.Cancelled;
            }

            // Порядок поиска: <папка .rvt>\IvanTools.ini -> %APPDATA%\IvanTools\IvanTools.ini -> дефолты
            IfcConfigFile config = IfcConfigFile.Find(doc.PathName);

            IfcExportSettings settings;
            IfcExportPreset[] presets = IfcExportPreset.CreateAll();
            using (IfcExportDialog dialog = new IfcExportDialog(presets, 1, config))
            {
                if (dialog.ShowDialog() != DialogResult.OK || dialog.Settings == null)
                    return Result.Cancelled;
                settings = dialog.Settings;
                config = dialog.Config;
            }

            if (!string.IsNullOrEmpty(settings.MappingFile) && !File.Exists(settings.MappingFile))
            {
                TaskDialog.Show("IvanTools — Export To IFC",
                    "Файл маппинга не найден:\n" + settings.MappingFile);
                return Result.Cancelled;
            }
            string suggested = Sanitize(doc.Title + "_" + view3d.Name) + ".ifc";
            string targetPath;
            using (SaveFileDialog save = new SaveFileDialog())
            {
                save.Title = "Экспорт активного 3D-вида в IFC";
                save.Filter = "IFC (*.ifc)|*.ifc";
                save.DefaultExt = "ifc";
                save.AddExtension = true;
                save.OverwritePrompt = true;
                save.FileName = suggested;
                if (save.ShowDialog() != DialogResult.OK)
                    return Result.Cancelled;
                targetPath = save.FileName;
            }

            string folder = Path.GetDirectoryName(targetPath);
            string baseName = Path.GetFileNameWithoutExtension(targetPath);
            DateTime started = DateTime.Now;

            IfcExportLog.Begin(settings, view3d, targetPath);

            bool ok = false;
            if (settings.DebugKeys)
            {
                settings.DebugKeysReport = ProbeKeys();
                IfcExportLog.Write("Диагностика ключей AddOption:");
                IfcExportLog.Write(settings.DebugKeysReport);
            }

            // Экспортёр IFC пишет в документ (IFC GUID, служебные параметры), поэтому
            // Document.Export для IFC требует открытой транзакции. Commit, а не RollBack:
            // IFC GUID сохраняются в модели и остаются стабильными между экспортами.
            // no_transaction = true отключает обёртку — только для диагностики.
            bool failed = false;
            if (settings.NoTransaction)
            {
                IfcExportLog.Write("Диагностика: экспорт БЕЗ транзакции (no_transaction = true)");
                try
                {
                    ok = ExportCore(doc, folder, baseName, view3d, settings);
                }
                catch (Exception ex)
                {
                    failed = true;
                    ReportExportError(ex, ref message);
                }
            }
            else
            {
                using (Transaction transaction = new Transaction(doc, "Экспорт IFC"))
                {
                    // Предупреждения экспортёра (например «элемент без IFC-класса») не должны
                    // останавливать прогон модальным окном; ошибки остаются как есть.
                    FailureHandlingOptions failureOptions = transaction.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new IfcWarningsSilencer());
                    failureOptions.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(failureOptions);

                    try
                    {
                        transaction.Start();
                        ok = ExportCore(doc, folder, baseName, view3d, settings);
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                            transaction.RollBack();

                        failed = true;
                        ReportExportError(ex, ref message);
                    }
                }
            }

            if (failed)
                return Result.Failed;

            string actualPath = FindExportedFile(folder, baseName);
            double seconds = (DateTime.Now - started).TotalSeconds;
            IfcExportLog.Write("doc.Export -> " + ok.ToString() +
                "; .ifc в папке: " + CountIfcFiles(folder).ToString());

            if (actualPath == null)
            {
                IfcExportLog.Write("ОШИБКА: файл не найден после экспорта (doc.Export=" + ok.ToString() + ")");
                string text = ok
                    ? "Revit вернул успех, но файл не создан.\n\nПапка:\n" + folder +
                      "\n\nОбычно это внутренний сбой IFC-экспортёра (Revit показывает своё окно). " +
                      "Попробуйте пресет IFC4 RV или отключите часть опций через skip_options в IvanTools.ini."
                    : "Revit вернул false, файл не создан.\n\nПапка:\n" + folder +
                      "\n\nПроверьте права на запись и свободное место.";
                TaskDialog.Show("IvanTools — Export To IFC", text);
                return Result.Failed;
            }

            IfcStepSummary summary = null;
            string summaryError = null;
            try
            {
                summary = IfcStepSummary.Read(actualPath);
            }
            catch (Exception ex)
            {
                summaryError = ex.Message;
            }

            // Автосохранение настроек: только по галочке, после того как файл .ifc найден
            // (для несохранённого документа пишем рядом с .ifc, а не в случайную папку).
            if (settings.SaveToFile)
            {
                string configTarget = IfcConfigFile.TargetForSave(doc.PathName, actualPath);
                string configStatus;
                config.TrySave(configTarget, settings, out configStatus);
                settings.ConfigStatus = configStatus;
                IfcExportLog.Write("Конфиг: " + configStatus);
            }

            if (settings.AppliedOptions.Count > 0)
                IfcExportLog.Write("AddOption: " + string.Join("; ", settings.AppliedOptions.ToArray()));

            string report = BuildReport(actualPath, summary, summaryError, settings, seconds, ok);
            IfcExportLog.Write(report);

            // Свой Form, а не TaskDialog: текст можно выделить и скопировать
            using (IfcReportDialog reportDialog =
                new IfcReportDialog("Экспорт IFC — отчёт", report, actualPath))
            {
                reportDialog.ShowDialog();
            }

            bool good = ok && summary != null && summary.TotalEntities > 0;
            return good ? Result.Succeeded : Result.Failed;
        }

        // ---------------------------------------------------------------------
        // Опции движка: свойства + AddOption (имена ключей проверяются диагностикой)
        // ---------------------------------------------------------------------
        private static void ApplyOptions(IFCExportOptions options, IfcExportSettings settings)
        {
            // Тесселяция действует только для IFC4
            if (settings.Version != IFCVersion.IFC2x3CV2)
                AddOption(options, "TessellationLevelOfDetail", settings.TesselationLod, settings);

            AddOption(options, "ExportLinkedFiles", BoolText(settings.ExportLinkedFiles), settings);
            AddOption(options, "Export2DElements", BoolText(settings.Export2DElements), settings);
            AddOption(options, "ExportBoundingBox", BoolText(settings.ExportBoundingBox), settings);
            AddOption(options, "ExportRoomsInView", BoolText(settings.ExportRooms), settings);
            AddOption(options, "IncludeSiteElevation", BoolText(settings.IncludeSiteElevation), settings);
            AddOption(options, "UseFamilyAndTypeNameForReference", BoolText(settings.UseFamilyTypeName), settings);

            AddOption(options, "ExportInternalRevitPropertySets", BoolText(settings.ExportRevitPsets), settings);
            AddOption(options, "ExportIFCCommonPropertySets", BoolText(settings.ExportIfcCommonPsets), settings);
            AddOption(options, "ExportUserDefinedPsets", BoolText(settings.ExportUserPsets), settings);
            AddOption(options, "ExportSchedulesAsPsets", BoolText(settings.ExportSchedulesAsPsets), settings);
            AddOption(options, "StoreIFCGUID", BoolText(settings.StoreIfcGuid), settings);

            if (!string.IsNullOrEmpty(settings.UserPsetsFile))
                AddOption(options, "UserDefinedPropertySetsFile", settings.UserPsetsFile, settings);
            if (!string.IsNullOrEmpty(settings.Schedules))
                AddOption(options, "ExportSpecificSchedules", settings.Schedules, settings);
            if (!string.IsNullOrEmpty(settings.Phase))
                AddOption(options, "PhaseToExport", settings.Phase, settings);
        }

        private static void AddOption(IFCExportOptions options, string key, string value,
            IfcExportSettings settings)
        {
            if (string.IsNullOrEmpty(value))
                return;         // «(не задано)», пустые фаза и расписания
            if (IsSkipped(settings, key))
                return;         // skip_options в .ini: диагностическое отключение ключа
            try
            {
                options.AddOption(key, value);
                settings.AppliedOptions.Add(key + "=" + value);
            }
            catch (Exception ex)
            {
                settings.AddOptionFailures.Add(key + "=" + value + " (" + ex.Message + ")");
            }
        }

        private static bool IsSkipped(IfcExportSettings settings, string key)
        {
            if (string.IsNullOrEmpty(settings.SkipOptions))
                return false;
            string[] parts = settings.SkipOptions.Split(new char[] { ';', ',' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Сборка опций и сам экспорт (используется с транзакцией и без неё)
        private static bool ExportCore(Document doc, string folder, string baseName,
            View3D view3d, IfcExportSettings settings)
        {
            using (IFCExportOptions options = new IFCExportOptions())
            {
                options.FileVersion = settings.Version;
                // "что видно на виде": содержимое определяют настройки видимости вида;
                // use_active_view = false -> экспорт всего проекта (нужно для DTV)
                options.FilterViewId = settings.UseActiveView ? view3d.Id : ElementId.InvalidElementId;
                options.ExportBaseQuantities = settings.BaseQuantities;
                options.WallAndColumnSplitting = settings.SplitWallsByLevel;
                options.SpaceBoundaryLevel = settings.SpaceBoundaryLevel;
                if (!string.IsNullOrEmpty(settings.MappingFile))
                    options.FamilyMappingFile = settings.MappingFile;

                ApplyOptions(options, settings);

                // Ключи, которых нет в диалоге, уходят в движок как есть
                foreach (KeyValuePair<string, string> extra in settings.ExtraOptions)
                    AddOption(options, extra.Key, extra.Value, settings);

                return doc.Export(folder, baseName, options);
            }
        }

        private static void ReportExportError(Exception ex, ref string message)
        {
            message = ex.Message;
            IfcExportLog.Write("ОШИБКА: " + ex);
            TaskDialog.Show("IvanTools — Export To IFC",
                "Экспорт прерван ошибкой:\n\n" + ex.Message +
                "\n\nПодробности (стек вызова) — в логе:\n" +
                Path.Combine(Path.GetTempPath(), "ExportToIfc.log"));
        }

        private static int CountIfcFiles(string folder)
        {
            try { return Directory.GetFiles(folder, "*.ifc").Length; }
            catch { return -1; }
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        // Диагностика (debug_keys = true): какие ключи движок принимает без исключения
        private static string ProbeKeys()
        {
            string[] keys = new string[]
            {
                "TessellationLevelOfDetail", "ExportLinkedFiles", "Export2DElements",
                "ExportBoundingBox", "ExportRoomsInView", "IncludeSiteElevation",
                "UseFamilyAndTypeNameForReference", "ExportInternalRevitPropertySets",
                "ExportIFCCommonPropertySets", "ExportUserDefinedPsets",
                "UserDefinedPropertySetsFile", "ExportSchedulesAsPsets",
                "ExportSpecificSchedules", "PhaseToExport", "StoreIFCGUID"
            };
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                string result;
                try
                {
                    using (IFCExportOptions probe = new IFCExportOptions())
                        probe.AddOption(keys[i], "true");
                    result = "без исключения";
                }
                catch (Exception ex)
                {
                    result = ex.GetType().Name + ": " + ex.Message;
                }
                sb.AppendLine("  " + keys[i] + " — " + result);
            }
            return sb.ToString();
        }

        // Revit создаёт файл сам; имя/расширение может отличаться от введённого
        private static string FindExportedFile(string folder, string baseName)
        {
            try
            {
                string[] candidates = new string[]
                {
                    Path.Combine(folder, baseName + ".ifc"),
                    Path.Combine(folder, baseName + ".IFC"),
                    Path.Combine(folder, baseName)
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i]))
                        return candidates[i];
                }

                string[] found = Directory.GetFiles(folder, baseName + ".*");
                for (int i = 0; i < found.Length; i++)
                {
                    if (string.Compare(Path.GetExtension(found[i]), ".ifc", StringComparison.OrdinalIgnoreCase) == 0)
                        return found[i];
                }
            }
            catch
            {
            }
            return null;
        }
        private static string BuildReport(string path, IfcStepSummary summary, string summaryError,
            IfcExportSettings settings, double seconds, bool ok)
        {
            StringBuilder sb = new StringBuilder();

            long size = 0;
            try { size = new FileInfo(path).Length; }
            catch { size = 0; }

            sb.AppendLine("Файл: " + path);
            sb.AppendLine("Размер: " + (size / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) +
                " МБ (" + size.ToString() + " Б)");
            sb.AppendLine("Пресет: " + settings.Preset.Caption);
            sb.AppendLine("Конфиг: " + (settings.ConfigInfo.Length > 0 ? settings.ConfigInfo : "нет данных"));
            if (!string.IsNullOrEmpty(settings.ConfigStatus))
                sb.AppendLine("  " + settings.ConfigStatus);
            sb.AppendLine("Схема: " + (summary != null && summary.Schema.Length > 0 ? summary.Schema : "?"));
            if (summary != null && summary.ViewDefinition.Length > 0)
                sb.AppendLine("View: " + summary.ViewDefinition);
            sb.AppendLine("Сборка: " + IfcExportLog.BuildInfo());
            sb.AppendLine("Время: " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " с");
            sb.AppendLine();

            if (summary == null)
            {
                sb.AppendLine("Файл создан, но прочитать его не удалось:");
                sb.AppendLine(summaryError);
                return sb.ToString();
            }

            sb.AppendLine("Сущностей: " + summary.TotalEntities.ToString());
            sb.AppendLine("Объекты:");
            sb.AppendLine("  Стены: " + (summary.Count("IFCWALL") + summary.Count("IFCWALLSTANDARDCASE")).ToString() +
                "    Плиты: " + summary.Count("IFCSLAB").ToString() +
                "    Колонны: " + summary.Count("IFCCOLUMN").ToString() +
                "    Балки: " + summary.Count("IFCBEAM").ToString());
            sb.AppendLine("  Двери: " + summary.Count("IFCDOOR").ToString() +
                "    Окна: " + summary.Count("IFCWINDOW").ToString() +
                "    Лестницы: " + summary.Count("IFCSTAIR").ToString() +
                "    Кровля: " + summary.Count("IFCROOF").ToString());
            sb.AppendLine("  Покрытия: " + summary.Count("IFCCOVERING").ToString() +
                "    Proxy: " + summary.Count("IFCBUILDINGELEMENTPROXY").ToString() +
                "    Уровни: " + summary.Count("IFCBUILDINGSTOREY").ToString());
            sb.AppendLine("  Сайт: " + YesNo(summary.Count("IFCSITE") > 0) +
                "    Здание: " + YesNo(summary.Count("IFCBUILDING") > 0) +
                "    Проект: " + YesNo(summary.Count("IFCPROJECT") > 0));
            sb.AppendLine();
            sb.AppendLine("PSets: " + summary.Count("IFCPROPERTYSET").ToString() +
                "    QTO: " + summary.Count("IFCELEMENTQUANTITY").ToString() +
                "    Стили: " + summary.Count("IFCSURFACESTYLE").ToString() +
                " (StyledItem: " + summary.Count("IFCSTYLEDITEM").ToString() + ")");

            if (settings.AppliedOptions.Count > 0)
            {
                StringBuilder extra = new StringBuilder();
                for (int i = 0; i < settings.AppliedOptions.Count; i++)
                {
                    if (extra.Length > 0)
                        extra.Append(", ");
                    extra.Append(settings.AppliedOptions[i]);
                }
                sb.AppendLine("AddOption: " + settings.AppliedOptions.Count.ToString() +
                    " (" + extra.ToString() + ")");
            }

            if (settings.DebugKeysReport.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Диагностика ключей AddOption (debug_keys):");
                sb.Append(settings.DebugKeysReport);
            }

            List<string> warnings = CollectWarnings(summary, settings, size, ok);
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Предупреждения (" + warnings.Count.ToString() + "):");
                for (int i = 0; i < warnings.Count; i++)
                    sb.AppendLine("  • " + warnings[i]);
            }

            return sb.ToString();
        }

        private static List<string> CollectWarnings(IfcStepSummary summary, IfcExportSettings settings, long size, bool ok)
        {
            List<string> warnings = new List<string>();

            if (!ok)
                warnings.Add("Revit вернул false при экспорте (файл мог быть создан частично)");
            if (size < 1024)
                warnings.Add("Файл меньше 1 КБ — экспорт, скорее всего, не удался");
            if (summary.TotalEntities == 0)
                warnings.Add("В файле нет ни одной сущности IFC");

            if (summary.Count("IFCPROJECT") == 0)
                warnings.Add("Нет IfcProject");
            if (summary.Count("IFCBUILDING") == 0)
                warnings.Add("Нет IfcBuilding");
            if (summary.Count("IFCBUILDINGSTOREY") == 0)
                warnings.Add("Нет ни одного IfcBuildingStorey — уровни не попали в файл");

            int walls = summary.Count("IFCWALL") + summary.Count("IFCWALLSTANDARDCASE");
            int slabs = summary.Count("IFCSLAB");
            if (walls == 0 && slabs == 0)
                warnings.Add("Нет ни стен, ни плит — проверьте активный вид и его видимость");

            if (summary.Count("IFCSTYLEDITEM") == 0)
                warnings.Add("IfcStyledItem = 0: цвета/материалы в файл не попали");

            if (settings.BaseQuantities && summary.Count("IFCELEMENTQUANTITY") == 0)
                warnings.Add("QTO включён, но IfcElementQuantity = 0 — объёмы в файл не попали");
            if (!settings.BaseQuantities)
                warnings.Add("QTO выключен: объёмов/площадей в файле нет (включается пресетами DTV/CV2)");

            int proxies = summary.Count("IFCBUILDINGELEMENTPROXY");
            int typed = walls + slabs + summary.Count("IFCCOLUMN") + summary.Count("IFCBEAM") +
                summary.Count("IFCMEMBER") + summary.Count("IFCPLATE") + summary.Count("IFCDOOR") +
                summary.Count("IFCWINDOW") + summary.Count("IFCSTAIR") + summary.Count("IFCROOF") +
                summary.Count("IFCCOVERING");
            if (proxies > 0 && proxies * 100 / (typed + proxies) > 20)
                warnings.Add("Proxy: " + proxies.ToString() + " из " + (typed + proxies).ToString() +
                    " объектов — проверьте таблицу маппинга семейств");
            if (walls > 0 && summary.Count("IFCWALLSTANDARDCASE") == 0)
                warnings.Add("Стены есть, но IfcWallStandardCase = 0 — у стен нет типа/стандартной геометрии");
            if (settings.Version == IFCVersion.IFC4DTV)
                warnings.Add("IFC4 DTV: полная параметрика, файл больше; для показа используйте RV");

            string expected = settings.Version == IFCVersion.IFC2x3CV2 ? "IFC2X3" : "IFC4";
            if (summary.Schema.Length > 0 &&
                summary.Schema.ToUpperInvariant().IndexOf(expected, StringComparison.Ordinal) < 0)
                warnings.Add("Схема в файле (" + summary.Schema + ") не совпадает с пресетом (" + expected + ")");

            for (int i = 0; i < settings.ConfigErrors.Count; i++)
                warnings.Add("конфиг: " + settings.ConfigErrors[i]);
            for (int i = 0; i < settings.AddOptionFailures.Count; i++)
                warnings.Add("AddOption не принят: " + settings.AddOptionFailures[i]);

            return warnings;
        }

        private static string YesNo(bool value)
        {
            return value ? "есть" : "нет";
        }

        // Имя файла: без символов, ломающих путь
        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "Export";

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
            result = result.Trim();
            return result.Length == 0 ? "Export" : result;
        }
    }

    // =========================================================================
    // Пресеты и настройки
    // =========================================================================
    internal sealed class IfcExportPreset
    {
        public string Name;
        public string Description;
        public IFCVersion Version;
        public bool DefaultBaseQuantities;
        public string DefaultLod;      // "" = TessellationLevelOfDetail не передавать

        public IfcExportPreset(string name, string description, IFCVersion version,
            bool defaultBaseQuantities, string defaultLod)
        {
            Name = name;
            Description = description;
            Version = version;
            DefaultBaseQuantities = defaultBaseQuantities;
            DefaultLod = defaultLod;
        }

        public string Caption
        {
            get { return Name + " — " + Description; }
        }

        // Классические BIM-пресеты: Design Transfer / Reference / Coordination View 2.0
        public static IfcExportPreset[] CreateAll()
        {
            return new IfcExportPreset[]
            {
                new IfcExportPreset("IFC4 DTV — передача проекта",
                    "Design Transfer View: полная параметрика", IFCVersion.IFC4DTV, true, ""),
                new IfcExportPreset("IFC4 RV — референс",
                    "Reference View: лёгкая геометрия", IFCVersion.IFC4RV, false, "Low"),
                new IfcExportPreset("IFC2x3 CV2 — координация",
                    "Coordination View 2.0: для проектов на 2x3", IFCVersion.IFC2x3CV2, true, "")
            };
        }
    }

    internal sealed class IfcExportSettings
    {
        public IfcExportPreset Preset;
        public bool BaseQuantities;
        public bool SplitWallsByLevel;
        public string MappingFile = "";

        // Геометрия и содержимое
        public int SpaceBoundaryLevel;                 // 0 = не экспортировать
        public string TesselationLod = "";             // Low | Medium | High | ""
        public bool ExportLinkedFiles;
        public bool Export2DElements;
        public bool ExportBoundingBox;
        public bool ExportRooms = true;
        public string Phase = "";                      // пусто = все фазы
        public bool IncludeSiteElevation = true;
        public bool UseFamilyTypeName;

        // Свойства (property sets)
        public bool ExportRevitPsets = true;
        public bool ExportIfcCommonPsets = true;
        public bool ExportUserPsets;
        public string UserPsetsFile = "";
        public bool ExportSchedulesAsPsets;
        public string Schedules = "";
        public bool StoreIfcGuid;

        public bool DebugKeys;
        public string DebugKeysReport = "";
        public readonly List<string> AppliedOptions = new List<string>();

        // Диагностика и совместимость
        public bool UseActiveView = true;    // false -> FilterViewId не ставится (весь проект)
        public bool NoTransaction;           // true -> экспорт без нашей транзакции (проверка)
        public string SkipOptions = "";      // ключи AddOption через ';', которые не отправлять

        // Конфиг (IvanTools.ini)
        public bool SaveToFile;
        public string ConfigInfo = "";
        public string ConfigStatus = "";
        public readonly List<string> ConfigErrors = new List<string>();
        public readonly SortedDictionary<string, string> ExtraOptions =
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> AddOptionFailures = new List<string>();

        public IFCVersion Version
        {
            get { return Preset.Version; }
        }
    }

    // =========================================================================
    // Конфиг: IvanTools.ini (INI-подобный: [ifc], key = value, ';'/'#' — комментарий)
    // =========================================================================
    internal sealed class IfcConfigFile
    {
        internal const string SectionName = "ifc";
        private const string FileName = "IvanTools.ini";

        // Порядок ключей при записи нового файла и при добавлении недостающих
        private static readonly string[] KnownKeys = new string[]
        {
            "preset", "base_quantities", "split_walls_by_level", "space_boundaries", "mapping_file",
            "tesselation_lod", "export_linked_files", "export_2d_elements", "export_bounding_box",
            "export_rooms", "phase", "include_site_elevation", "use_family_type_name",
            "export_revit_psets", "export_ifc_common_psets", "export_user_psets", "user_psets_file",
            "export_schedules_as_psets", "schedules",
            "store_ifc_guid", "use_active_view", "no_transaction", "skip_options", "debug_keys"
        };

        internal string FilePath;
        internal readonly List<string> Errors = new List<string>();
        internal readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // [ifc:PRESET] читаются, но применяются только с v1.1
        internal readonly Dictionary<string, Dictionary<string, string>> PresetSections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        internal bool Exists
        {
            get
            {
                try { return FilePath != null && File.Exists(FilePath); }
                catch { return false; }
            }
        }

        internal string Folder
        {
            get
            {
                try
                {
                    return string.IsNullOrEmpty(FilePath) ? null : System.IO.Path.GetDirectoryName(FilePath);
                }
                catch { return null; }
            }
        }

        internal static string ProjectPath(string documentPath)
        {
            try
            {
                if (string.IsNullOrEmpty(documentPath))
                    return null;
                string folder = System.IO.Path.GetDirectoryName(documentPath);
                if (string.IsNullOrEmpty(folder))
                    return null;
                return System.IO.Path.Combine(folder, FileName);
            }
            catch { return null; }
        }

        internal static string UserPath()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData))
                    return null;
                return System.IO.Path.Combine(System.IO.Path.Combine(appData, "IvanTools"), FileName);
            }
            catch { return null; }
        }

        // Порядок поиска: проект -> %APPDATA% -> дефолты. Никогда не бросает.
        internal static IfcConfigFile Find(string documentPath)
        {
            string project = ProjectPath(documentPath);
            if (project != null && File.Exists(project))
                return Load(project);

            string user = UserPath();
            if (user != null && File.Exists(user))
                return Load(user);

            IfcConfigFile none = new IfcConfigFile();
            none.FilePath = project;    // куда писать, если пользователь включит галочку
            return none;
        }

        // Куда писать: папка проекта, а для несохранённого документа — папка готового .ifc
        internal static string TargetForSave(string documentPath, string exportedPath)
        {
            string project = ProjectPath(documentPath);
            if (project != null)
                return project;
            try
            {
                string folder = string.IsNullOrEmpty(exportedPath) ? null : System.IO.Path.GetDirectoryName(exportedPath);
                if (string.IsNullOrEmpty(folder))
                    return null;
                return System.IO.Path.Combine(folder, FileName);
            }
            catch { return null; }
        }

        internal static IfcConfigFile Load(string path)
        {
            IfcConfigFile config = new IfcConfigFile();
            config.FilePath = path;
            if (string.IsNullOrEmpty(path))
            {
                config.Errors.Add("путь к конфигу не задан");
                return config;
            }

            string section = "";
            try
            {
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    int number = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        number++;
                        string trimmed = line.Trim();
                        if (trimmed.Length > 0 && trimmed[0] == '\uFEFF')
                            trimmed = trimmed.Substring(1).TrimStart();   // двойной BOM после пересохранения
                        if (trimmed.Length == 0)
                            continue;
                        if (trimmed[0] == ';' || trimmed[0] == '#')
                            continue;

                        if (trimmed[0] == '[')
                        {
                            int close = trimmed.IndexOf(']');
                            if (close < 0)
                            {
                                config.Errors.Add("строка " + number.ToString() + ": в заголовке секции нет ']'");
                                continue;
                            }
                            section = trimmed.Substring(1, close - 1).Trim();
                            continue;
                        }

                        int equals = trimmed.IndexOf('=');
                        if (equals <= 0)
                        {
                            config.Errors.Add("строка " + number.ToString() + ": ожидается 'key = value'");
                            continue;
                        }
                        string key = trimmed.Substring(0, equals).Trim();
                        if (key.Length == 0)
                        {
                            config.Errors.Add("строка " + number.ToString() + ": пустой ключ");
                            continue;
                        }
                        string value = trimmed.Substring(equals + 1).Trim();

                        Dictionary<string, string> target = config.Section(section);
                        if (target == null)
                            continue;   // чужая секция (например [glb]) — молча пропускаем
                        if (target.ContainsKey(key))
                            config.Errors.Add("строка " + number.ToString() + ": ключ '" + key +
                                "' повторяется — берётся последнее значение");
                        target[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                config.Errors.Add("файл не прочитан: " + ex.Message);
            }
            return config;
        }

        private Dictionary<string, string> Section(string name)
        {
            if (string.IsNullOrEmpty(name))
                return Values;      // ключи до первой секции считаем основными
            if (name.Equals(SectionName, StringComparison.OrdinalIgnoreCase))
                return Values;
            if (name.Length > SectionName.Length + 1 &&
                name.StartsWith(SectionName + ":", StringComparison.OrdinalIgnoreCase))
            {
                string preset = name.Substring(SectionName.Length + 1).Trim();
                Dictionary<string, string> values;
                if (!PresetSections.TryGetValue(preset, out values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    PresetSections[preset] = values;
                }
                return values;
            }
            return null;
        }

        // Применение файла к настройкам: известные ключи -> свойства, прочие -> AddOption
        internal void Apply(IfcExportSettings settings, IfcExportPreset[] presets)
        {
            if (settings == null)
                return;

            string text;

            // Пресет приносит свои дефолты; явные ключи файла применяются поверх
            IfcExportPreset preset = settings.Preset;
            if (Values.TryGetValue("preset", out text))
            {
                IfcExportPreset match = FindPreset(presets, text);
                if (match != null)
                    preset = match;
                else
                    Errors.Add("preset = " + text +
                        ": неизвестный пресет (есть IFC4DTV, IFC4RV, IFC2x3CV2) — оставлен текущий");
            }
            if (preset != null)
            {
                settings.Preset = preset;
                settings.BaseQuantities = preset.DefaultBaseQuantities;
                settings.SplitWallsByLevel = false;
                settings.TesselationLod = preset.DefaultLod;
            }

            // Объёмы и уровни
            settings.BaseQuantities = ReadBool("base_quantities", settings.BaseQuantities);
            settings.SplitWallsByLevel = ReadBool("split_walls_by_level", settings.SplitWallsByLevel);
            settings.SpaceBoundaryLevel = ReadInt("space_boundaries", settings.SpaceBoundaryLevel, 0, 2);
            settings.MappingFile = ReadPath("mapping_file", settings.MappingFile);

            // Геометрия и содержимое
            settings.TesselationLod = ReadChoice("tesselation_lod", settings.TesselationLod,
                new string[] { "", "Low", "Medium", "High" });
            settings.ExportLinkedFiles = ReadBool("export_linked_files", settings.ExportLinkedFiles);
            settings.Export2DElements = ReadBool("export_2d_elements", settings.Export2DElements);
            settings.ExportBoundingBox = ReadBool("export_bounding_box", settings.ExportBoundingBox);
            settings.ExportRooms = ReadBool("export_rooms", settings.ExportRooms);
            settings.Phase = ReadText("phase", settings.Phase);
            settings.IncludeSiteElevation = ReadBool("include_site_elevation", settings.IncludeSiteElevation);
            settings.UseFamilyTypeName = ReadBool("use_family_type_name", settings.UseFamilyTypeName);

            // Property sets
            settings.ExportRevitPsets = ReadBool("export_revit_psets", settings.ExportRevitPsets);
            settings.ExportIfcCommonPsets = ReadBool("export_ifc_common_psets", settings.ExportIfcCommonPsets);
            settings.ExportUserPsets = ReadBool("export_user_psets", settings.ExportUserPsets);
            settings.UserPsetsFile = ReadPath("user_psets_file", settings.UserPsetsFile);
            settings.ExportSchedulesAsPsets = ReadBool("export_schedules_as_psets", settings.ExportSchedulesAsPsets);
            settings.Schedules = ReadText("schedules", settings.Schedules);

            // Прочее
            settings.StoreIfcGuid = ReadBool("store_ifc_guid", settings.StoreIfcGuid);
            settings.UseActiveView = ReadBool("use_active_view", settings.UseActiveView);
            settings.NoTransaction = ReadBool("no_transaction", settings.NoTransaction);
            settings.SkipOptions = ReadText("skip_options", settings.SkipOptions);
            settings.DebugKeys = ReadBool("debug_keys", settings.DebugKeys);

            CollectExtras(settings);
        }

        private bool ReadBool(string key, bool current)
        {
            string text;
            bool value;
            if (!Values.TryGetValue(key, out text))
                return current;
            if (TryParseBool(text, out value))
                return value;
            Errors.Add(key + " = " + text + ": ожидается true/false — оставлено текущее");
            return current;
        }

        private int ReadInt(string key, int current, int min, int max)
        {
            string text;
            int value;
            if (!Values.TryGetValue(key, out text))
                return current;
            if (int.TryParse(text, out value) && value >= min && value <= max)
                return value;
            Errors.Add(key + " = " + text + ": ожидается число " + min.ToString() + ".." + max.ToString());
            return current;
        }

        private string ReadText(string key, string current)
        {
            string text;
            return Values.TryGetValue(key, out text) ? text.Trim() : current;
        }

        private string ReadPath(string key, string current)
        {
            string text;
            return Values.TryGetValue(key, out text) ? ResolvePath(text) : current;
        }

        private string ReadChoice(string key, string current, string[] allowed)
        {
            string text;
            if (!Values.TryGetValue(key, out text))
                return current;
            string value = text.Trim();
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                    return allowed[i];
            }
            Errors.Add(key + " = " + value + ": допустимо " +
                string.Join(", ", allowed) + " — оставлено текущее");
            return current;
        }

        // Только «прочие» ключи: нужны и при загрузке, и перед экспортом
        internal void CollectExtras(IfcExportSettings settings)
        {
            if (settings == null)
                return;
            settings.ExtraOptions.Clear();
            foreach (KeyValuePair<string, string> kv in Values)
            {
                if (IsKnownKey(kv.Key))
                    continue;
                if (string.IsNullOrEmpty(kv.Value))
                    continue;       // пустое значение передавать нечего
                settings.ExtraOptions[kv.Key] = kv.Value;
            }
        }

        internal string DescribeForReport()
        {
            if (!Exists)
            {
                if (FilePath == null)
                    return "не найден (документ не сохранён) — значения по умолчанию";
                return "не найден (" + FilePath + ") — значения по умолчанию";
            }
            string text = FilePath;
            if (PresetSections.Count > 0)
                text += " [секции [ifc:PRESET] прочитаны, применяются с v1.1]";
            return text;
        }

        internal static bool TryParseBool(string text, out bool value)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            if (t == "true" || t == "1" || t == "yes" || t == "on" || t == "да")
            {
                value = true;
                return true;
            }
            if (t == "false" || t == "0" || t == "no" || t == "off" || t == "нет")
            {
                value = false;
                return true;
            }
            value = false;
            return false;
        }

        private static bool IsKnownKey(string key)
        {
            for (int i = 0; i < KnownKeys.Length; i++)
            {
                if (KnownKeys[i].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static IfcExportPreset FindPreset(IfcExportPreset[] presets, string text)
        {
            string name = (text ?? "").Trim();
            if (name.Length == 0)
                return null;
            if (name.Equals("2x3", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ifc2x3", StringComparison.OrdinalIgnoreCase))
                name = "IFC2x3CV2";

            for (int i = 0; i < presets.Length; i++)
            {
                if (presets[i].Version.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return presets[i];
            }
            return null;
        }

        private string ResolvePath(string text)
        {
            string raw = Unquote(text);
            if (raw.Length == 0)
                return "";
            try
            {
                raw = Environment.ExpandEnvironmentVariables(raw);
                if (System.IO.Path.IsPathRooted(raw))
                    return System.IO.Path.GetFullPath(raw);
                string folder = Folder;
                if (string.IsNullOrEmpty(folder))
                    return System.IO.Path.GetFullPath(raw);
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, raw));
            }
            catch { return raw; }
        }

        private static string Unquote(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"')
                t = t.Substring(1, t.Length - 2).Trim();
            return t;
        }

        // Обновляет ключи внутри [ifc], не трогая комментарии и чужие ключи.
        // Атомарность: пишем .tmp, затем Replace/Move.
        internal bool TrySave(string targetPath, IfcExportSettings settings, out string status)
        {
            status = "";
            if (string.IsNullOrEmpty(targetPath))
            {
                status = "не записано: папку определить не удалось";
                return false;
            }
            if (settings == null)
            {
                status = "не записано: нет настроек";
                return false;
            }

            try
            {
                string folder = System.IO.Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                Dictionary<string, string> wanted = WantedValues(settings, folder);
                bool existed = File.Exists(targetPath);
                List<string> lines = existed
                    ? new List<string>(File.ReadAllLines(targetPath))
                    : NewFileLines(wanted);

                if (existed)
                    MergeKnownKeys(lines, wanted);

                string temp = targetPath + ".tmp";
                File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
                if (existed)
                {
                    try
                    {
                        File.Replace(temp, targetPath, null, true);
                    }
                    catch
                    {
                        // FAT/сетевые папки: File.Replace может быть недоступен
                        File.Copy(temp, targetPath, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, targetPath);
                }

                FilePath = targetPath;
                status = "записано: " + targetPath;
                return true;
            }
            catch (Exception ex)
            {
                status = "не записано: " + ex.Message;
                return false;
            }
        }

        private Dictionary<string, string> WantedValues(IfcExportSettings settings, string folder)
        {
            Dictionary<string, string> wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            wanted["preset"] = settings.Preset.Version.ToString();
            wanted["base_quantities"] = BoolText(settings.BaseQuantities);
            wanted["split_walls_by_level"] = BoolText(settings.SplitWallsByLevel);
            wanted["space_boundaries"] = settings.SpaceBoundaryLevel.ToString(CultureInfo.InvariantCulture);
            wanted["mapping_file"] = RelativeTo(folder, settings.MappingFile);

            wanted["tesselation_lod"] = settings.TesselationLod == null ? "" : settings.TesselationLod;
            wanted["export_linked_files"] = BoolText(settings.ExportLinkedFiles);
            wanted["export_2d_elements"] = BoolText(settings.Export2DElements);
            wanted["export_bounding_box"] = BoolText(settings.ExportBoundingBox);
            wanted["export_rooms"] = BoolText(settings.ExportRooms);
            wanted["phase"] = settings.Phase == null ? "" : settings.Phase;
            wanted["include_site_elevation"] = BoolText(settings.IncludeSiteElevation);
            wanted["use_family_type_name"] = BoolText(settings.UseFamilyTypeName);

            wanted["export_revit_psets"] = BoolText(settings.ExportRevitPsets);
            wanted["export_ifc_common_psets"] = BoolText(settings.ExportIfcCommonPsets);
            wanted["export_user_psets"] = BoolText(settings.ExportUserPsets);
            wanted["user_psets_file"] = RelativeTo(folder, settings.UserPsetsFile);
            wanted["export_schedules_as_psets"] = BoolText(settings.ExportSchedulesAsPsets);
            wanted["schedules"] = settings.Schedules == null ? "" : settings.Schedules;

            wanted["store_ifc_guid"] = BoolText(settings.StoreIfcGuid);
            wanted["use_active_view"] = BoolText(settings.UseActiveView);
            wanted["no_transaction"] = BoolText(settings.NoTransaction);
            wanted["skip_options"] = settings.SkipOptions == null ? "" : settings.SkipOptions;
            wanted["debug_keys"] = BoolText(settings.DebugKeys);
            return wanted;
        }

        private static string BoolText(bool value)
        {
            return value ? "true" : "false";
        }

        private static void AddPair(List<string> lines, Dictionary<string, string> wanted, string key)
        {
            string value;
            if (wanted.TryGetValue(key, out value))
                lines.Add(key + " = " + value);
        }

        private static List<string> NewFileLines(Dictionary<string, string> wanted)
        {
            List<string> lines = new List<string>();
            lines.Add("; IvanTools — настройки экспорта IFC (плагин Export To IFC)");
            lines.Add("; Комментарий — строка, начинающаяся с ';' или '#'; inline-комментариев нет");
            lines.Add("; Ключи, которых нет в диалоге, передаются в IFCExportOptions.AddOption");
            lines.Add("");
            lines.Add("[" + SectionName + "]");
            lines.Add("; пресет: IFC4DTV (передача) | IFC4RV (референс) | IFC2x3CV2 (координация)");
            AddPair(lines, wanted, "preset");
            lines.Add("");
            lines.Add("; --- объёмы, уровни, маппинг ---");
            AddPair(lines, wanted, "base_quantities");
            AddPair(lines, wanted, "split_walls_by_level");
            lines.Add("; границы помещений: 0 = не экспортировать, 1 или 2");
            AddPair(lines, wanted, "space_boundaries");
            lines.Add("; пусто = штатная таблица Revit; относительный путь — от папки этого файла");
            AddPair(lines, wanted, "mapping_file");
            lines.Add("");
            lines.Add("; --- геометрия и содержимое ---");
            lines.Add("; тесселяция: Low | Medium | High; пусто = не передавать (для 2x3 не действует)");
            AddPair(lines, wanted, "tesselation_lod");
            AddPair(lines, wanted, "export_linked_files");
            AddPair(lines, wanted, "export_2d_elements");
            lines.Add("; true = только ограничивающий бокс, без геометрии");
            AddPair(lines, wanted, "export_bounding_box");
            AddPair(lines, wanted, "export_rooms");
            lines.Add("; имя фазы; пусто = все фазы");
            AddPair(lines, wanted, "phase");
            AddPair(lines, wanted, "include_site_elevation");
            AddPair(lines, wanted, "use_family_type_name");
            lines.Add("");
            lines.Add("; --- property sets ---");
            AddPair(lines, wanted, "export_revit_psets");
            AddPair(lines, wanted, "export_ifc_common_psets");
            AddPair(lines, wanted, "export_user_psets");
            AddPair(lines, wanted, "user_psets_file");
            AddPair(lines, wanted, "export_schedules_as_psets");
            lines.Add("; имена расписаний через ';'");
            AddPair(lines, wanted, "schedules");
            lines.Add("");
            lines.Add("; --- прочее ---");
            AddPair(lines, wanted, "store_ifc_guid");
            lines.Add("; false = не фильтровать по активному виду (экспорт всего проекта)");
            AddPair(lines, wanted, "use_active_view");
            lines.Add("; true = экспорт без транзакции плагина (диагностика)");
            AddPair(lines, wanted, "no_transaction");
            lines.Add("; ключи AddOption через ';', которые НЕ отправлять (диагностика)");
            AddPair(lines, wanted, "skip_options");
            lines.Add("; true = проверять, какие ключи AddOption принимает Revit (отчёт)");
            AddPair(lines, wanted, "debug_keys");
            lines.Add("");
            return lines;
        }

        private static void MergeKnownKeys(List<string> lines, Dictionary<string, string> wanted)
        {
            int start = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Equals("[" + SectionName + "]", StringComparison.OrdinalIgnoreCase))
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0)
                    lines.Add("");
                lines.Add("[" + SectionName + "]");
                for (int i = 0; i < KnownKeys.Length; i++)
                    lines.Add(KnownKeys[i] + " = " + wanted[KnownKeys[i]]);
                return;
            }

            int end = lines.Count;
            for (int i = start + 1; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0 && t[0] == '[')
                {
                    end = i;
                    break;
                }
            }

            List<string> missing = new List<string>();
            for (int k = 0; k < KnownKeys.Length; k++)
            {
                string key = KnownKeys[k];
                bool replaced = false;
                for (int i = start + 1; i < end; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t[0] == ';' || t[0] == '#')
                        continue;
                    int equals = t.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    if (t.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = key + " = " + wanted[key];
                        replaced = true;
                        break;
                    }
                }
                if (!replaced)
                    missing.Add(key);
            }

            if (missing.Count == 0)
                return;

            int insertAt = end;
            while (insertAt > start + 1 && lines[insertAt - 1].Trim().Length == 0)
                insertAt--;
            for (int i = 0; i < missing.Count; i++)
                lines.Insert(insertAt + i, missing[i] + " = " + wanted[missing[i]]);
        }

        // Путь маппинга: внутри папки ini пишем относительным, иначе — абсолютным
        private static string RelativeTo(string folder, string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return "";
            if (string.IsNullOrEmpty(folder))
                return fullPath;
            try
            {
                string baseDir = folder.TrimEnd('\\') + "\\";
                Uri baseUri = new Uri(baseDir);
                Uri fileUri = new Uri(fullPath);
                string relative = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString()).Replace('/', '\\');
                if (relative.Length == 0 ||
                    relative.StartsWith("..", StringComparison.Ordinal) ||
                    System.IO.Path.IsPathRooted(relative))
                    return fullPath;
                return relative;
            }
            catch { return fullPath; }
        }
    }

    // =========================================================================
    // Экспорт IFC: предупреждения Revit не должны прерывать прогон
    // =========================================================================
    internal sealed class IfcWarningsSilencer : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            try
            {
                failures.DeleteAllWarnings();
            }
            catch
            {
            }
            // Ошибки не разрешаем и не удаляем: они должны дойти до пользователя
            return FailureProcessingResult.Continue;
        }
    }

    // =========================================================================
    // Диалог настроек
    // =========================================================================
    internal sealed class IfcExportDialog : Form
    {
        private readonly IfcExportPreset[] _presets;
        private RadioButton[] _radios;

        // Вкладка «Пресет и объёмы»
        private CheckBox _qto;
        private CheckBox _split;
        private ComboBox _spaceBoundaries;

        // Вкладка «Содержимое и геометрия»
        private ComboBox _lod;
        private CheckBox _linked;
        private CheckBox _twoD;
        private CheckBox _boundingBox;
        private CheckBox _rooms;
        private TextBox _phase;
        private CheckBox _siteElevation;
        private CheckBox _familyTypeName;

        // Вкладка «PSets, конфиг»
        private CheckBox _revitPsets;
        private CheckBox _commonPsets;
        private CheckBox _userPsets;
        private TextBox _userPsetsPath;
        private CheckBox _schedulesAsPsets;
        private TextBox _schedulesPath;
        private CheckBox _storeGuid;
        private CheckBox _debugKeys;
        private TextBox _mapping;
        private Label _configLabel;
        private CheckBox _saveToFile;

        private readonly ToolTip _tips = new ToolTip();
        private IfcConfigFile _config;
        private bool _suppressPresetDefaults;

        // Диагностические ключи (только в .ini, без галочек в GUI) — сохраняем как есть
        private bool _useActiveView = true;
        private bool _noTransaction;
        private string _skipOptions = "";

        public IfcExportSettings Settings { get; private set; }
        public IfcConfigFile Config { get { return _config; } }

        public IfcExportDialog(IfcExportPreset[] presets, int selectedIndex, IfcConfigFile config)
        {
            _presets = presets;
            _config = config != null ? config : new IfcConfigFile();

            Text = "Экспорт активного 3D-вида в IFC";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(800, 600);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            TabControl tabs = new TabControl();
            tabs.Bounds = new System.Drawing.Rectangle(12, 12, 776, 520);
            Controls.Add(tabs);
            tabs.TabPages.Add(BuildPresetTab());
            tabs.TabPages.Add(BuildContentTab());
            tabs.TabPages.Add(BuildPsetsTab());

            Button ok = new Button();
            ok.Text = "Экспорт";
            ok.Bounds = new System.Drawing.Rectangle(556, 548, 108, 30);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Bounds = new System.Drawing.Rectangle(672, 548, 108, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            FormClosing += new FormClosingEventHandler(OnFormClosing);

            int index = selectedIndex >= 0 && selectedIndex < _radios.Length ? selectedIndex : 0;
            _radios[index].Checked = true;
            ApplyPresetDefaults(index);
            ApplyConfig(_config);
        }

        // ---------------------------------------------------------------------
        // Вкладки диалога
        // ---------------------------------------------------------------------
        private TabPage BuildPresetTab()
        {
            TabPage page = new TabPage("Пресет и объёмы");
            page.UseVisualStyleBackColor = true;

            GroupBox group = new GroupBox();
            group.Text = "Пресет (классические BIM-виды)";
            group.Bounds = new System.Drawing.Rectangle(10, 10, 740, 152);
            page.Controls.Add(group);

            _radios = new RadioButton[_presets.Length];
            for (int i = 0; i < _presets.Length; i++)
            {
                RadioButton radio = new RadioButton();
                radio.Text = _presets[i].Caption;
                radio.Bounds = new System.Drawing.Rectangle(16, 28 + i * 38, 710, 30);
                radio.Tag = i;
                radio.CheckedChanged += new EventHandler(OnPresetChanged);
                group.Controls.Add(radio);
                _radios[i] = radio;
            }

            _qto = MakeCheck(page,
                "Базовые объёмы (QTO): длины, площади и объёмы по стандарту IFC", 178);
            _tips.SetToolTip(_qto,
                "IfcElementQuantity (Qto_*BaseQuantities): готовые числа для смет и массы. " +
                "Включается пресетами DTV и CV2, выключается в RV.");

            _split = MakeCheck(page,
                "Разрезать стены и колонны по уровням (обычно не нужно)", 210);

            Label spaceLabel = new Label();
            spaceLabel.Text = "Границы помещений (space boundaries):";
            spaceLabel.Bounds = new System.Drawing.Rectangle(14, 248, 300, 24);
            page.Controls.Add(spaceLabel);

            _spaceBoundaries = new ComboBox();
            _spaceBoundaries.DropDownStyle = ComboBoxStyle.DropDownList;
            _spaceBoundaries.Bounds = new System.Drawing.Rectangle(320, 246, 190, 24);
            _spaceBoundaries.Items.Add("0 — не экспортировать");
            _spaceBoundaries.Items.Add("1 — 1-й уровень");
            _spaceBoundaries.Items.Add("2 — 2-й уровень");
            _spaceBoundaries.SelectedIndex = 0;
            page.Controls.Add(_spaceBoundaries);
            _tips.SetToolTip(_spaceBoundaries,
                "IFCExportOptions.SpaceBoundaryLevel: 0 — не экспортировать, 1/2 — уровни границ помещений.");

            Label hint = new Label();
            hint.Text = "DTV — передача проекта (Tekla/ArchiCAD), RV — референс и показ, " +
                "CV2 — координация для проектов на IFC2x3.";
            hint.Bounds = new System.Drawing.Rectangle(14, 292, 730, 44);
            page.Controls.Add(hint);

            return page;
        }

        private static CheckBox MakeCheck(Control parent, string text, int top)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Bounds = new System.Drawing.Rectangle(14, top, 736, 26);
            parent.Controls.Add(box);
            return box;
        }

        private TabPage BuildContentTab()
        {
            TabPage page = new TabPage("Содержимое и геометрия");
            page.UseVisualStyleBackColor = true;

            Label lodLabel = new Label();
            lodLabel.Text = "Уровень детализации (тесселяция):";
            lodLabel.Bounds = new System.Drawing.Rectangle(14, 20, 260, 24);
            page.Controls.Add(lodLabel);

            _lod = new ComboBox();
            _lod.DropDownStyle = ComboBoxStyle.DropDownList;
            _lod.Bounds = new System.Drawing.Rectangle(280, 18, 180, 24);
            _lod.Items.Add("(не задано)");
            _lod.Items.Add("Low");
            _lod.Items.Add("Medium");
            _lod.Items.Add("High");
            _lod.SelectedIndex = 0;
            page.Controls.Add(_lod);
            _tips.SetToolTip(_lod,
                "AddOption TessellationLevelOfDetail. Действует для IFC4; для IFC2x3 не передаётся.");

            _linked = MakeCheck(page, "Экспортировать связанные файлы (Revit Links)", 60);
            _twoD = MakeCheck(page, "Экспортировать 2D-элементы плана", 92);
            _boundingBox = MakeCheck(page, "Только ограничивающий бокс вместо геометрии", 124);
            _tips.SetToolTip(_boundingBox, "Файл без тел: полезно для габаритов и проверки размещения.");
            _rooms = MakeCheck(page, "Экспортировать помещения и пространства", 156);

            Label phaseLabel = new Label();
            phaseLabel.Text = "Фаза (пусто = все фазы):";
            phaseLabel.Bounds = new System.Drawing.Rectangle(14, 194, 210, 24);
            page.Controls.Add(phaseLabel);

            _phase = new TextBox();
            _phase.Bounds = new System.Drawing.Rectangle(230, 192, 340, 24);
            page.Controls.Add(_phase);
            _tips.SetToolTip(_phase, "Имя фазы документа, например «Стадия 2». Пусто — все фазы.");

            _siteElevation = MakeCheck(page, "Отметка площадки (site elevation)", 230);
            _familyTypeName = MakeCheck(page, "Имя семейства и типа в Reference", 262);

            // Дефолты совпадают с IfcExportSettings, а не «пустые галочки»
            IfcExportSettings defaults = new IfcExportSettings();
            _rooms.Checked = defaults.ExportRooms;
            _siteElevation.Checked = defaults.IncludeSiteElevation;

            return page;
        }

        private TabPage BuildPsetsTab()
        {
            TabPage page = new TabPage("PSets и конфиг");
            page.UseVisualStyleBackColor = true;

            _revitPsets = MakeCheck(page, "Внутренние PSets Revit", 14);
            _commonPsets = MakeCheck(page, "Общие IFC PSets", 44);
            _userPsets = MakeCheck(page, "Пользовательские PSets из файла:", 74);

            _userPsetsPath = new TextBox();
            _userPsetsPath.Bounds = new System.Drawing.Rectangle(34, 102, 610, 24);
            page.Controls.Add(_userPsetsPath);
            page.Controls.Add(MakeBrowse(page, 654, 100, new EventHandler(OnBrowseUserPsets)));

            _schedulesAsPsets = MakeCheck(page, "Расписания как PSets (имена через ';'):", 138);

            _schedulesPath = new TextBox();
            _schedulesPath.Bounds = new System.Drawing.Rectangle(34, 166, 716, 24);
            page.Controls.Add(_schedulesPath);
            _tips.SetToolTip(_schedulesPath,
                "Имена расписаний через ';', например: Ведомость отделки;Спецификация.");

            _storeGuid = MakeCheck(page, "Сохранять IFC GUID в параметрах Revit", 202);
            _debugKeys = MakeCheck(page, "Проверить ключи AddOption (диагностика в отчёте)", 232);

            Label mapLabel = new Label();
            mapLabel.Text = "Файл маппинга семейств (пусто = штатная таблица Revit):";
            mapLabel.Bounds = new System.Drawing.Rectangle(14, 272, 620, 24);
            page.Controls.Add(mapLabel);

            _mapping = new TextBox();
            _mapping.Bounds = new System.Drawing.Rectangle(14, 300, 630, 24);
            page.Controls.Add(_mapping);
            page.Controls.Add(MakeBrowse(page, 654, 298, new EventHandler(OnBrowse)));

            Label line = new Label();
            line.BorderStyle = BorderStyle.Fixed3D;
            line.Bounds = new System.Drawing.Rectangle(14, 340, 740, 2);
            page.Controls.Add(line);

            _configLabel = new Label();
            _configLabel.Bounds = new System.Drawing.Rectangle(14, 350, 740, 24);
            _configLabel.AutoEllipsis = true;
            page.Controls.Add(_configLabel);

            Button load = new Button();
            load.Text = "Загрузить…";
            load.Bounds = new System.Drawing.Rectangle(14, 380, 120, 30);
            load.Click += new EventHandler(OnLoadConfig);
            page.Controls.Add(load);

            Button saveAs = new Button();
            saveAs.Text = "Сохранить как…";
            saveAs.Bounds = new System.Drawing.Rectangle(142, 380, 160, 30);
            saveAs.Click += new EventHandler(OnSaveConfigAs);
            page.Controls.Add(saveAs);

            _saveToFile = new CheckBox();
            _saveToFile.Text = "Сохранять настройки в файл проекта";
            _saveToFile.Bounds = new System.Drawing.Rectangle(320, 382, 420, 26);
            page.Controls.Add(_saveToFile);

            IfcExportSettings defaults = new IfcExportSettings();
            _revitPsets.Checked = defaults.ExportRevitPsets;
            _commonPsets.Checked = defaults.ExportIfcCommonPsets;

            return page;
        }

        private static Button MakeBrowse(Control parent, int left, int top, EventHandler handler)
        {
            Button button = new Button();
            button.Text = "Обзор…";
            button.Bounds = new System.Drawing.Rectangle(left, top, 96, 28);
            button.Click += handler;
            parent.Controls.Add(button);
            return button;
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
            if (_suppressPresetDefaults)
                return;     // значения из конфига не должны затираться дефолтами пресета
            RadioButton radio = sender as RadioButton;
            if (radio == null || !radio.Checked)
                return;
            ApplyPresetDefaults((int)radio.Tag);
        }

        private void ApplyPresetDefaults(int index)
        {
            if (index < 0 || index >= _presets.Length)
                return;
            _qto.Checked = _presets[index].DefaultBaseQuantities;
            _split.Checked = false;
            SetLod(_presets[index].DefaultLod);
        }

        private void SetLod(string lod)
        {
            string value = lod == null ? "" : lod.Trim();
            if (value.Length == 0)
            {
                _lod.SelectedIndex = 0;
                return;
            }
            for (int i = 1; i < _lod.Items.Count; i++)
            {
                if (string.Compare(_lod.Items[i].ToString(), value,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _lod.SelectedIndex = i;
                    return;
                }
            }
            _lod.SelectedIndex = 0;
        }

        private string GetLod()
        {
            return _lod.SelectedIndex <= 0 ? "" : _lod.Items[_lod.SelectedIndex].ToString();
        }

        // ---------------------------------------------------------------------
        // Конфиг: путь, загрузка, сохранение
        // ---------------------------------------------------------------------
        private IfcExportSettings BuildSettings()
        {
            int index = 0;
            for (int i = 0; i < _radios.Length; i++)
            {
                if (_radios[i].Checked)
                    index = i;
            }

            IfcExportSettings settings = new IfcExportSettings();
            settings.Preset = _presets[index];
            settings.BaseQuantities = _qto.Checked;
            settings.SplitWallsByLevel = _split.Checked;
            settings.MappingFile = _mapping.Text.Trim();
            settings.SaveToFile = _saveToFile.Checked;

            settings.SpaceBoundaryLevel = _spaceBoundaries.SelectedIndex < 0 ? 0 : _spaceBoundaries.SelectedIndex;
            settings.TesselationLod = GetLod();
            settings.ExportLinkedFiles = _linked.Checked;
            settings.Export2DElements = _twoD.Checked;
            settings.ExportBoundingBox = _boundingBox.Checked;
            settings.ExportRooms = _rooms.Checked;
            settings.Phase = _phase.Text.Trim();
            settings.IncludeSiteElevation = _siteElevation.Checked;
            settings.UseFamilyTypeName = _familyTypeName.Checked;

            settings.ExportRevitPsets = _revitPsets.Checked;
            settings.ExportIfcCommonPsets = _commonPsets.Checked;
            settings.ExportUserPsets = _userPsets.Checked;
            settings.UserPsetsFile = _userPsetsPath.Text.Trim();
            settings.ExportSchedulesAsPsets = _schedulesAsPsets.Checked;
            settings.Schedules = _schedulesPath.Text.Trim();
            settings.StoreIfcGuid = _storeGuid.Checked;
            settings.DebugKeys = _debugKeys.Checked;

            settings.UseActiveView = _useActiveView;
            settings.NoTransaction = _noTransaction;
            settings.SkipOptions = _skipOptions;
            return settings;
        }

        private int IndexOfPreset(IfcExportPreset preset)
        {
            if (preset == null)
                return -1;
            for (int i = 0; i < _presets.Length; i++)
            {
                if (_presets[i].Version == preset.Version)
                    return i;
            }
            return -1;
        }

        private void ApplyConfig(IfcConfigFile config)
        {
            if (config != null)
                _config = config;
            if (_config == null)
                _config = new IfcConfigFile();

            if (_config.Values.Count > 0)
            {
                _suppressPresetDefaults = true;
                try
                {
                    IfcExportSettings snapshot = BuildSettings();
                    _config.Apply(snapshot, _presets);

                    int index = IndexOfPreset(snapshot.Preset);
                    if (index >= 0)
                        _radios[index].Checked = true;
                    _qto.Checked = snapshot.BaseQuantities;
                    _split.Checked = snapshot.SplitWallsByLevel;
                    _mapping.Text = snapshot.MappingFile;

                    int boundaries = snapshot.SpaceBoundaryLevel;
                    if (boundaries < 0) boundaries = 0;
                    if (boundaries > 2) boundaries = 2;
                    _spaceBoundaries.SelectedIndex = boundaries;

                    SetLod(snapshot.TesselationLod);
                    _linked.Checked = snapshot.ExportLinkedFiles;
                    _twoD.Checked = snapshot.Export2DElements;
                    _boundingBox.Checked = snapshot.ExportBoundingBox;
                    _rooms.Checked = snapshot.ExportRooms;
                    _phase.Text = snapshot.Phase;
                    _siteElevation.Checked = snapshot.IncludeSiteElevation;
                    _familyTypeName.Checked = snapshot.UseFamilyTypeName;

                    _revitPsets.Checked = snapshot.ExportRevitPsets;
                    _commonPsets.Checked = snapshot.ExportIfcCommonPsets;
                    _userPsets.Checked = snapshot.ExportUserPsets;
                    _userPsetsPath.Text = snapshot.UserPsetsFile;
                    _schedulesAsPsets.Checked = snapshot.ExportSchedulesAsPsets;
                    _schedulesPath.Text = snapshot.Schedules;
                    _storeGuid.Checked = snapshot.StoreIfcGuid;
                    _debugKeys.Checked = snapshot.DebugKeys;

                    _useActiveView = snapshot.UseActiveView;
                    _noTransaction = snapshot.NoTransaction;
                    _skipOptions = snapshot.SkipOptions;
                }
                finally
                {
                    _suppressPresetDefaults = false;
                }
            }

            // Галочка включается сама, только если файл уже существует
            _saveToFile.Checked = _config.Exists;
            UpdateConfigLabel();
        }

        private void UpdateConfigLabel()
        {
            string text;
            if (_config == null || _config.FilePath == null)
                text = "Настройки: файл не найден (документ не сохранён) — значения по умолчанию";
            else if (_config.Exists)
                text = "Настройки: " + _config.FilePath;
            else
                text = "Настройки: " + _config.FilePath + " (ещё не создан)";

            if (_config != null && _config.Errors.Count > 0)
                text += "   [замечаний: " + _config.Errors.Count.ToString() + "]";

            _configLabel.Text = text;
            _tips.SetToolTip(_configLabel, text);
        }

        private void OnLoadConfig(object sender, EventArgs e)
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Настройки экспорта IFC (IvanTools.ini)";
                open.Filter = "Настройки IvanTools (*.ini)|*.ini|Все файлы (*.*)|*.*";
                if (_config != null && _config.FilePath != null)
                {
                    try { open.InitialDirectory = System.IO.Path.GetDirectoryName(_config.FilePath); }
                    catch { }
                }
                if (open.ShowDialog(this) != DialogResult.OK)
                    return;

                IfcConfigFile loaded = IfcConfigFile.Load(open.FileName);
                ApplyConfig(loaded);
                _saveToFile.Checked = true;

                if (loaded.Errors.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Файл прочитан, но есть замечания:");
                    int limit = loaded.Errors.Count > 10 ? 10 : loaded.Errors.Count;
                    for (int i = 0; i < limit; i++)
                        sb.AppendLine("  • " + loaded.Errors[i]);
                    if (loaded.Errors.Count > limit)
                        sb.AppendLine("  … ещё " + (loaded.Errors.Count - limit).ToString());
                    MessageBox.Show(this, sb.ToString(), "Экспорт IFC",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OnSaveConfigAs(object sender, EventArgs e)
        {
            using (SaveFileDialog save = new SaveFileDialog())
            {
                save.Title = "Сохранить настройки экспорта IFC";
                save.Filter = "Настройки IvanTools (*.ini)|*.ini";
                save.DefaultExt = "ini";
                save.AddExtension = true;
                save.OverwritePrompt = true;
                save.FileName = "IvanTools.ini";
                if (_config != null && _config.FilePath != null)
                {
                    try
                    {
                        string folder = System.IO.Path.GetDirectoryName(_config.FilePath);
                        if (!string.IsNullOrEmpty(folder))
                            save.InitialDirectory = folder;
                    }
                    catch { }
                }
                if (save.ShowDialog(this) != DialogResult.OK)
                    return;

                if (_config == null)
                    _config = new IfcConfigFile();

                string status;
                if (!_config.TrySave(save.FileName, BuildSettings(), out status))
                {
                    MessageBox.Show(this, status, "Экспорт IFC",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _saveToFile.Checked = true;
                UpdateConfigLabel();
            }
        }

        private void OnBrowseUserPsets(object sender, EventArgs e)
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Пользовательские property sets";
                open.Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*";
                string current = _userPsetsPath.Text.Trim();
                if (current.Length > 0)
                {
                    try { open.InitialDirectory = System.IO.Path.GetDirectoryName(current); }
                    catch { }
                }
                if (open.ShowDialog(this) == DialogResult.OK)
                    _userPsetsPath.Text = open.FileName;
            }
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (OpenFileDialog open = new OpenFileDialog())
            {
                open.Title = "Таблица маппинга семейств (IFC)";
                open.Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*";
                if (open.ShowDialog(this) == DialogResult.OK)
                    _mapping.Text = open.FileName;
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK)
                return;

            string mapping = _mapping.Text.Trim();
            if (mapping.Length > 0 && !File.Exists(mapping))
            {
                MessageBox.Show(this, "Файл маппинга не найден:\n" + mapping,
                    "Экспорт IFC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            IfcExportSettings settings = BuildSettings();

            // «Прочие» ключи конфига и его замечания — в настройки, чтобы ушли в экспорт и отчёт
            if (_config != null)
            {
                _config.CollectExtras(settings);
                settings.ConfigInfo = _config.DescribeForReport();
                settings.ConfigErrors.AddRange(_config.Errors);
            }
            if (settings.ConfigInfo.Length == 0)
                settings.ConfigInfo = "не найден — значения по умолчанию";

            Settings = settings;
        }
    }
    // =========================================================================
    // Отчёт: свой Form — текст выделяется и копируется (TaskDialog Revit не даёт)
    // =========================================================================
    internal sealed class IfcReportDialog : Form
    {
        private readonly TextBox _text;
        private readonly string _folder;

        public IfcReportDialog(string caption, string report, string filePath)
        {
            Text = caption;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(840, 640);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            try
            {
                _folder = string.IsNullOrEmpty(filePath)
                    ? null
                    : System.IO.Path.GetDirectoryName(filePath);
            }
            catch { _folder = null; }

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.Font = new System.Drawing.Font("Consolas", 9f);
            _text.Bounds = new System.Drawing.Rectangle(12, 12, 816, 578);
            _text.Text = report;
            Controls.Add(_text);

            Button copy = new Button();
            copy.Text = "Копировать";
            copy.Bounds = new System.Drawing.Rectangle(12, 600, 130, 30);
            copy.Click += new EventHandler(OnCopy);
            Controls.Add(copy);

            Button open = new Button();
            open.Text = "Открыть папку";
            open.Bounds = new System.Drawing.Rectangle(150, 600, 150, 30);
            open.Click += new EventHandler(OnOpenFolder);
            Controls.Add(open);

            Button close = new Button();
            close.Text = "Закрыть";
            close.Bounds = new System.Drawing.Rectangle(698, 600, 130, 30);
            close.DialogResult = DialogResult.OK;
            Controls.Add(close);

            AcceptButton = close;
            CancelButton = close;
        }

        private void OnCopy(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(_text.Text);
            }
            catch
            {
            }
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_folder))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + _folder + "\"");
            }
            catch
            {
            }
        }
    }

    // =========================================================================
    // Валидатор STEP: читает файл построчно (IFC бывает 100+ МБ)
    // =========================================================================
    internal sealed class IfcStepSummary
    {
        public string Schema = "";
        public string ViewDefinition = "";
        public string Header = "";
        public long SizeBytes;
        public int LineCount;
        public int TotalEntities;
        public readonly Dictionary<string, int> Entities = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count(string entity)
        {
            int value;
            return Entities.TryGetValue(entity, out value) ? value : 0;
        }

        public static IfcStepSummary Read(string path)
        {
            IfcStepSummary summary = new IfcStepSummary();
            FileInfo info = new FileInfo(path);
            summary.SizeBytes = info.Length;

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                bool headerDone = false;
                while ((line = reader.ReadLine()) != null)
                {
                    summary.LineCount++;

                    if (!headerDone)
                    {
                        if (summary.Schema.Length == 0 &&
                            line.IndexOf("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase) >= 0)
                            summary.Schema = ExtractQuoted(line);
                        if (summary.ViewDefinition.Length == 0 &&
                            line.IndexOf("FILE_DESCRIPTION", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int open = line.IndexOf('[');
                            int close = open < 0 ? -1 : line.IndexOf(']', open + 1);
                            if (open >= 0 && close > open)
                                summary.ViewDefinition =
                                    line.Substring(open + 1, close - open - 1).Trim();
                        }
                        if (summary.Header.Length == 0 &&
                            line.IndexOf("FILE_NAME", StringComparison.OrdinalIgnoreCase) >= 0)
                            summary.Header = line.Trim();
                        if (summary.Schema.Length > 0 &&
                            line.IndexOf("ENDSEC", StringComparison.OrdinalIgnoreCase) >= 0)
                            headerDone = true;
                    }

                    CountEntity(summary, line);
                }
            }

            return summary;
        }

        private static string ExtractQuoted(string line)
        {
            int start = line.IndexOf('\'');
            if (start < 0)
                return "";
            int end = line.IndexOf('\'', start + 1);
            if (end <= start)
                return "";
            return line.Substring(start + 1, end - start - 1);
        }

        // Строки вида: #123=IFCWALL('guid',#1,'name',...);
        private static void CountEntity(IfcStepSummary summary, string line)
        {
            int equals = line.IndexOf('=');
            if (equals < 0)
                return;

            int i = equals + 1;
            int length = line.Length;
            while (i < length && (line[i] == ' ' || line[i] == '\t'))
                i++;

            if (i + 3 > length)
                return;
            if (string.Compare(line, i, "IFC", 0, 3, StringComparison.OrdinalIgnoreCase) != 0)
                return;

            int start = i;
            while (i < length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                i++;
            if (i == start)
                return;

            string type = line.Substring(start, i - start).ToUpperInvariant();

            while (i < length && (line[i] == ' ' || line[i] == '\t'))
                i++;
            if (i >= length || line[i] != '(')
                return;

            int count;
            if (summary.Entities.TryGetValue(type, out count))
                summary.Entities[type] = count + 1;
            else
                summary.Entities[type] = 1;
            summary.TotalEntities++;
        }
    }

    // =========================================================================
    // Лог прогонов: %TEMP%\ExportToIfc.log
    // =========================================================================
    internal static class IfcExportLog
    {
        private static string _path;

        private static string LogPath
        {
            get
            {
                if (_path == null)
                    _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ExportToIfc.log");
                return _path;
            }
        }

        internal static void Begin(IfcExportSettings settings, View3D view, string targetPath)
        {
            Write("");
            Write("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
            Write("Вид: " + view.Name + " (Id " + view.Id.ToString() + ")");
            Write("Пресет: " + settings.Preset.Caption + " [" + settings.Preset.Version.ToString() + "]");
            Write("QTO: " + settings.BaseQuantities.ToString() +
                "; Split по уровням: " + settings.SplitWallsByLevel.ToString() +
                "; Space boundaries: " + settings.SpaceBoundaryLevel.ToString());
            Write("LOD: " + (string.IsNullOrEmpty(settings.TesselationLod) ? "(не задано)" : settings.TesselationLod) +
                "; links: " + settings.ExportLinkedFiles.ToString() +
                "; 2D: " + settings.Export2DElements.ToString() +
                "; bbox: " + settings.ExportBoundingBox.ToString() +
                "; rooms: " + settings.ExportRooms.ToString() +
                "; phase: " + (string.IsNullOrEmpty(settings.Phase) ? "(все)" : settings.Phase));
            Write("PSets: revit=" + settings.ExportRevitPsets.ToString() +
                ", common=" + settings.ExportIfcCommonPsets.ToString() +
                ", user=" + settings.ExportUserPsets.ToString() +
                ", schedules=" + settings.ExportSchedulesAsPsets.ToString() +
                "; GUID: " + settings.StoreIfcGuid.ToString() +
                "; debug_keys: " + settings.DebugKeys.ToString());
            Write("Mapping: " + (string.IsNullOrEmpty(settings.MappingFile) ? "(таблица Revit по умолчанию)" : settings.MappingFile));
            Write("Конфиг: " + (string.IsNullOrEmpty(settings.ConfigInfo) ? "(не найден — значения по умолчанию)" : settings.ConfigInfo));
            if (settings.ConfigErrors.Count > 0)
                Write("Конфиг-замечания: " + string.Join(" | ", settings.ConfigErrors.ToArray()));
            Write("Сборка: " + BuildInfo());
            Write("Файл: " + targetPath);
        }

        internal static void Write(string text)
        {
            try
            {
                File.AppendAllText(LogPath, (text ?? "") + Environment.NewLine);
            }
            catch
            {
            }
        }

        // Какая именно сборка выполняется: для DLL — путь и дата, для HotLoader — «динамическая»
        internal static string BuildInfo()
        {
            try
            {
                string path = typeof(ExportToIfc).Assembly.Location;
                if (string.IsNullOrEmpty(path))
                    return "(динамическая сборка)";
                return path + " (" +
                    File.GetLastWriteTime(path).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + ")";
            }
            catch
            {
                return "?";
            }
        }
    }
}
