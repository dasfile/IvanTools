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
//     схема, счётчики сущностей, стили, PSets/QTO, предупреждения.
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
    public class ExportToIfcCommand : IExternalCommand
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

            IfcExportSettings settings;
            IfcExportPreset[] presets = IfcExportPreset.CreateAll();
            using (IfcExportDialog dialog = new IfcExportDialog(presets, 1))
            {
                if (dialog.ShowDialog() != DialogResult.OK || dialog.Settings == null)
                    return Result.Cancelled;
                settings = dialog.Settings;
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
            try
            {
                using (IFCExportOptions options = new IFCExportOptions())
                {
                    options.FileVersion = settings.Version;
                    // "что видно на виде": содержимое определяют настройки видимости вида
                    options.FilterViewId = view3d.Id;
                    options.ExportBaseQuantities = settings.BaseQuantities;
                    options.WallAndColumnSplitting = settings.SplitWallsByLevel;
                    if (!string.IsNullOrEmpty(settings.MappingFile))
                        options.FamilyMappingFile = settings.MappingFile;

                    ok = doc.Export(folder, baseName, options);
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                IfcExportLog.Write("ОШИБКА: " + ex);
                TaskDialog.Show("IvanTools — Export To IFC",
                    "Экспорт прерван ошибкой:\n\n" + ex.ToString());
                return Result.Failed;
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

        public IFCVersion Version
        {
            get { return Preset.Version; }
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

        public IfcExportSettings Settings { get; private set; }

        public IfcExportDialog(IfcExportPreset[] presets, int selectedIndex)
        {
            _presets = presets;

            Text = "Экспорт активного 3D-вида в IFC";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(560, 330);
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

            Button ok = new Button();
            ok.Text = "Экспорт";
            ok.Bounds = new System.Drawing.Rectangle(340, 292, 100, 27);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Отмена";
            cancel.Bounds = new System.Drawing.Rectangle(448, 292, 100, 27);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            FormClosing += new FormClosingEventHandler(OnFormClosing);

            int index = selectedIndex >= 0 && selectedIndex < _radios.Length ? selectedIndex : 0;
            _radios[index].Checked = true;
            ApplyPresetDefaults(index);
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
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
            settings.MappingFile = mapping;
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
