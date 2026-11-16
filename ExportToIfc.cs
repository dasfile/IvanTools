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

            bool ok;
            // Экспортёр IFC пишет в документ (IFC GUID, служебные параметры), поэтому
            // Document.Export для IFC требует открытой транзакции. Commit, а не RollBack:
            // IFC GUID сохраняются в модели и остаются стабильными между экспортами.
            using (Transaction transaction = new Transaction(doc, "Экспорт IFC"))
            {
                try
                {
                    transaction.Start();

                    using (IFCExportOptions options = new IFCExportOptions())
                    {
                        options.FileVersion = settings.Version;
                        // "что видно на виде": содержимое определяют настройки видимости вида
                        options.FilterViewId = view3d.Id;
                        options.ExportBaseQuantities = settings.BaseQuantities;
                        options.WallAndColumnSplitting = settings.SplitWallsByLevel;
                        if (!string.IsNullOrEmpty(settings.MappingFile))
                            options.FamilyMappingFile = settings.MappingFile;

                        // Ключи, которых нет в диалоге, уходят в движок как есть.
                        // Незнакомый ключ не должен ломать экспорт: фиксируем и идём дальше.
                        foreach (KeyValuePair<string, string> extra in settings.ExtraOptions)
                        {
                            try
                            {
                                options.AddOption(extra.Key, extra.Value);
                            }
                            catch (Exception ex)
                            {
                                settings.AddOptionFailures.Add(extra.Key + " = " + extra.Value + " (" + ex.Message + ")");
                            }
                        }

                        ok = doc.Export(folder, baseName, options);
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();

                    message = ex.Message;
                    IfcExportLog.Write("ОШИБКА: " + ex);
                    TaskDialog.Show("IvanTools — Export To IFC",
                        "Экспорт прерван ошибкой:\n\n" + ex.Message +
                        "\n\nПодробности (стек вызова) — в логе:\n" +
                        Path.Combine(Path.GetTempPath(), "ExportToIfc.log"));
                    return Result.Failed;
                }
            }

            string actualPath = FindExportedFile(folder, baseName);
            double seconds = (DateTime.Now - started).TotalSeconds;

            if (actualPath == null)
            {
                IfcExportLog.Write("ОШИБКА: файл не найден после экспорта");
                TaskDialog.Show("IvanTools — Export To IFC",
                    "Revit сообщил результат: " + ok.ToString() + ",\n" +
                    "но файл не найден в папке:\n" + folder + "\n\n" +
                    "Проверьте права на запись.");
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

            string report = BuildReport(actualPath, summary, summaryError, settings, seconds, ok);
            IfcExportLog.Write(report);
            TaskDialog.Show("IvanTools — Export To IFC", report);

            bool good = ok && summary != null && summary.TotalEntities > 0;
            return good ? Result.Succeeded : Result.Failed;
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
            sb.AppendLine("Схема: " + (summary != null && summary.Schema.Length > 0 ? summary.Schema : "?") +
                " (RV/DTV в заголовке не различаются)");
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

            if (settings.ExtraOptions.Count > 0)
            {
                StringBuilder extra = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in settings.ExtraOptions)
                {
                    if (extra.Length > 0)
                        extra.Append(", ");
                    extra.Append(kv.Key).Append("=").Append(kv.Value);
                }
                sb.AppendLine("AddOption: " + settings.ExtraOptions.Count.ToString() + " (" + extra.ToString() + ")");
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
                warnings.Add("QTO включён, но IfcElementQuantity = 0");

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

        public IfcExportPreset(string name, string description, IFCVersion version, bool defaultBaseQuantities)
        {
            Name = name;
            Description = description;
            Version = version;
            DefaultBaseQuantities = defaultBaseQuantities;
        }

        public string Caption
        {
            get { return Name + " — " + Description; }
        }

        public static IfcExportPreset[] CreateAll()
        {
            return new IfcExportPreset[]
            {
                new IfcExportPreset("Лёгкий (IFC4 RV)", "минимальный размер, референсная геометрия", IFCVersion.IFC4RV, false),
                new IfcExportPreset("Стандарт (IFC4)", "для Tekla: полная геометрия и объёмы", IFCVersion.IFC4, true),
                new IfcExportPreset("Совместимость (IFC2x3 CV2)", "если в ТЗ IFC2x3", IFCVersion.IFC2x3CV2, true)
            };
        }
    }

    internal sealed class IfcExportSettings
    {
        public IfcExportPreset Preset;
        public bool BaseQuantities;
        public bool SplitWallsByLevel;
        public string MappingFile = "";

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
            "preset", "base_quantities", "split_walls_by_level", "mapping_file"
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
            bool flag;

            // Пресет приносит свои дефолты; явные ключи файла применяются поверх
            IfcExportPreset preset = settings.Preset;
            if (Values.TryGetValue("preset", out text))
            {
                IfcExportPreset match = FindPreset(presets, text);
                if (match != null)
                    preset = match;
                else
                    Errors.Add("preset = " + text +
                        ": неизвестный пресет (есть IFC4, IFC4RV, IFC2x3CV2) — оставлен текущий");
            }
            if (preset != null)
            {
                settings.Preset = preset;
                settings.BaseQuantities = preset.DefaultBaseQuantities;
                settings.SplitWallsByLevel = false;
            }

            if (Values.TryGetValue("base_quantities", out text))
            {
                if (TryParseBool(text, out flag))
                    settings.BaseQuantities = flag;
                else
                    Errors.Add("base_quantities = " + text + ": ожидается true/false");
            }
            if (Values.TryGetValue("split_walls_by_level", out text))
            {
                if (TryParseBool(text, out flag))
                    settings.SplitWallsByLevel = flag;
                else
                    Errors.Add("split_walls_by_level = " + text + ": ожидается true/false");
            }
            if (Values.TryGetValue("mapping_file", out text))
                settings.MappingFile = ResolvePath(text);

            CollectExtras(settings);
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
            wanted["base_quantities"] = settings.BaseQuantities ? "true" : "false";
            wanted["split_walls_by_level"] = settings.SplitWallsByLevel ? "true" : "false";
            wanted["mapping_file"] = RelativeTo(folder, settings.MappingFile);
            return wanted;
        }

        private static List<string> NewFileLines(Dictionary<string, string> wanted)
        {
            List<string> lines = new List<string>();
            lines.Add("; IvanTools — настройки экспорта IFC");
            lines.Add("; Комментарий — строка, начинающаяся с ';' или '#'");
            lines.Add("; Ключи, которых нет в диалоге, передаются в IFCExportOptions.AddOption");
            lines.Add("");
            lines.Add("[" + SectionName + "]");
            for (int i = 0; i < KnownKeys.Length; i++)
                lines.Add(KnownKeys[i] + " = " + wanted[KnownKeys[i]]);
            lines.Add("");
            lines.Add("; --- ключи, которых нет в диалоге (уйдут в AddOption) ---");
            lines.Add("; tesselation_lod = Medium");
            lines.Add("; store_ifc_guid = true");
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
    // Диалог настроек
    // =========================================================================
    internal sealed class IfcExportDialog : Form
    {
        private readonly IfcExportPreset[] _presets;
        private readonly RadioButton[] _radios;
        private readonly CheckBox _qto;
        private readonly CheckBox _split;
        private readonly TextBox _mapping;
        private readonly Label _configLabel;
        private readonly CheckBox _saveToFile;
        private readonly ToolTip _tips = new ToolTip();
        private IfcConfigFile _config;
        private bool _suppressPresetDefaults;

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
            ClientSize = new System.Drawing.Size(560, 356);
            Font = new System.Drawing.Font("Segoe UI", 9f);

            GroupBox group = new GroupBox();
            group.Text = "Пресет";
            group.Bounds = new System.Drawing.Rectangle(12, 8, 536, 116);
            Controls.Add(group);

            _radios = new RadioButton[presets.Length];
            for (int i = 0; i < presets.Length; i++)
            {
                RadioButton radio = new RadioButton();
                radio.Text = presets[i].Caption;
                radio.Bounds = new System.Drawing.Rectangle(14, 22 + i * 28, 508, 24);
                radio.Tag = i;
                radio.CheckedChanged += new EventHandler(OnPresetChanged);
                group.Controls.Add(radio);
                _radios[i] = radio;
            }

            _qto = new CheckBox();
            _qto.Text = "Экспортировать базовые объёмы (QTO)";
            _qto.Bounds = new System.Drawing.Rectangle(12, 132, 420, 22);
            Controls.Add(_qto);

            _split = new CheckBox();
            _split.Text = "Разрезать стены и колонны по уровням (обычно не нужно)";
            _split.Bounds = new System.Drawing.Rectangle(12, 156, 520, 22);
            Controls.Add(_split);

            Label mappingLabel = new Label();
            mappingLabel.Text = "Файл маппинга семейств (необязательно; пусто = таблица Revit):";
            mappingLabel.Bounds = new System.Drawing.Rectangle(12, 190, 520, 20);
            Controls.Add(mappingLabel);

            _mapping = new TextBox();
            _mapping.Bounds = new System.Drawing.Rectangle(12, 212, 420, 23);
            Controls.Add(_mapping);

            Button browse = new Button();
            browse.Text = "Обзор…";
            browse.Bounds = new System.Drawing.Rectangle(440, 210, 108, 26);
            browse.Click += new EventHandler(OnBrowse);
            Controls.Add(browse);

            _configLabel = new Label();
            _configLabel.Bounds = new System.Drawing.Rectangle(12, 246, 536, 18);
            _configLabel.AutoEllipsis = true;
            Controls.Add(_configLabel);

            Button load = new Button();
            load.Text = "Загрузить…";
            load.Bounds = new System.Drawing.Rectangle(12, 266, 104, 26);
            load.Click += new EventHandler(OnLoadConfig);
            Controls.Add(load);

            Button saveAs = new Button();
            saveAs.Text = "Сохранить как…";
            saveAs.Bounds = new System.Drawing.Rectangle(122, 266, 140, 26);
            saveAs.Click += new EventHandler(OnSaveConfigAs);
            Controls.Add(saveAs);

            _saveToFile = new CheckBox();
            _saveToFile.Text = "Сохранять настройки в файл проекта";
            _saveToFile.Bounds = new System.Drawing.Rectangle(272, 268, 280, 22);
            Controls.Add(_saveToFile);

            Button ok = new Button();
            ok.Text = "Экспорт";
            ok.Bounds = new System.Drawing.Rectangle(340, 316, 100, 27);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Bounds = new System.Drawing.Rectangle(448, 316, 100, 27);
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
    // Валидатор STEP: читает файл построчно (IFC бывает 100+ МБ)
    // =========================================================================
    internal sealed class IfcStepSummary
    {
        public string Schema = "";
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
                "; Split по уровням: " + settings.SplitWallsByLevel.ToString());
            Write("Mapping: " + (string.IsNullOrEmpty(settings.MappingFile) ? "(таблица Revit по умолчанию)" : settings.MappingFile));
            Write("Конфиг: " + (string.IsNullOrEmpty(settings.ConfigInfo) ? "(не найден — значения по умолчанию)" : settings.ConfigInfo));
            if (settings.ConfigErrors.Count > 0)
                Write("Конфиг-замечания: " + string.Join(" | ", settings.ConfigErrors.ToArray()));
            foreach (KeyValuePair<string, string> extra in settings.ExtraOptions)
                Write("AddOption: " + extra.Key + " = " + extra.Value);
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
    }
}
