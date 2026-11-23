// =============================================================================
// RoomCounter - a simple clicker for room numbers (rooms only)
// =============================================================================
// Minimal version: enter the start number and the step once, then just click
// rooms one by one - the number goes into the Number parameter (ROOM_NUMBER)
// and grows. No other categories, no branches - if something more complex is
// needed later (doors, grids, etc.), it will be a separate script.
//
// "B101" + step 1 -> B101, B102, B103...
// "007"  + step 1 -> 007, 008, 009...
//
// Can be run via IvanTools.HotLoader (Run Script).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using WinForm    = System.Windows.Forms.Form;
using WinTextBox = System.Windows.Forms.TextBox;
using WinLabel   = System.Windows.Forms.Label;
using WinButton  = System.Windows.Forms.Button;
using WinPoint   = System.Drawing.Point;
using WinSize    = System.Drawing.Size;
using DialogResult = System.Windows.Forms.DialogResult;
using FormBorderStyle = System.Windows.Forms.FormBorderStyle;
using FormStartPosition = System.Windows.Forms.FormStartPosition;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RoomCounter : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            string startValue;
            int step;
            if (!AskStartAndStep(out startValue, out step))
                return Result.Cancelled;

            NumberIncrementer counter;
            try
            {
                counter = new NumberIncrementer(startValue, step);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("IvanTools", "Could not parse the start number: " + ex.Message);
                return Result.Failed;
            }

            int count = 0;
            // Collect the ids of numbered rooms and highlight them, so it is
            // clear which rooms have already been clicked (like pyRevit ReNumber)
            var processedIds = new List<ElementId>();

            using (var tx = new Transaction(doc, "Room Counter"))
            {
                tx.Start();
                while (true)
                {
                    Reference pickedRef;
                    try
                    {
                        pickedRef = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new RoomSelectionFilter(),
                            string.Format("Click a room (Esc to finish). Next number: {0}",
                                counter.PeekNext()));
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break; // Esc
                    }

                    Element el = doc.GetElement(pickedRef);
                    Parameter p = el.get_Parameter(BuiltInParameter.ROOM_NUMBER);

                    if (p == null || p.IsReadOnly)
                    {
                        TaskDialog.Show("IvanTools",
                            string.Format("Element id {0}: the Number parameter is not available", el.Id));
                        continue;
                    }

                    p.Set(counter.Next());
                    count++;

                    // Refresh the view immediately, without waiting for the loop
                    // to finish - the number tag and highlight appear right away
                    processedIds.Add(el.Id);
                    doc.Regenerate();
                    uidoc.Selection.SetElementIds(processedIds);
                    uidoc.RefreshActiveView();
                }

                if (count > 0)
                    tx.Commit();
                else
                    tx.RollBack();
            }

            TaskDialog.Show("IvanTools", string.Format("Rooms numbered: {0}", count));
            return Result.Succeeded;
        }

        // -----------------------------------------------------------------
        // Dialog: start number + step
        // -----------------------------------------------------------------
        private bool AskStartAndStep(out string startValue, out int step)
        {
            startValue = null;
            step = 1;
            bool ok = false;

            using (var form = new WinForm())
            {
                form.Text = "Room Counter";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.ClientSize = new WinSize(320, 170);
                form.MaximizeBox = false;
                form.MinimizeBox = false;

                var label1 = new WinLabel
                {
                    Text = "Start number (for example B101)",
                    Location = new WinPoint(12, 10),
                    AutoSize = true
                };
                var textStart = new WinTextBox
                {
                    Location = new WinPoint(12, 32),
                    Width = 296,
                    Text = "101"
                };

                var label2 = new WinLabel
                {
                    Text = "Step",
                    Location = new WinPoint(12, 62),
                    AutoSize = true
                };
                var textStep = new WinTextBox
                {
                    Location = new WinPoint(12, 84),
                    Width = 296,
                    Text = "1"
                };

                var okButton = new WinButton
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new WinPoint(12, 124),
                    Width = 296,
                    Height = 30
                };

                form.Controls.Add(label1);
                form.Controls.Add(textStart);
                form.Controls.Add(label2);
                form.Controls.Add(textStep);
                form.Controls.Add(okButton);
                form.AcceptButton = okButton;
                form.Shown += (s, e) => { textStart.Focus(); textStart.SelectAll(); };

                if (form.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(textStart.Text))
                {
                    startValue = textStart.Text.Trim();
                    if (!int.TryParse(textStep.Text.Trim(), out step))
                        step = 1;
                    ok = true;
                }
            }

            return ok;
        }
    }

    // -------------------------------------------------------------------------
    // Allow picking rooms only
    // -------------------------------------------------------------------------
    public class RoomSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem is Autodesk.Revit.DB.Architecture.Room;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // Number increment: prefix/suffix are preserved, the number grows by step,
    // the width (leading zeros) is preserved. "B101" -> "B102" -> "B103"
    // -------------------------------------------------------------------------
    public class NumberIncrementer
    {
        private readonly string _prefix;
        private readonly string _suffix;
        private readonly int _width;
        private readonly int _step;
        private long _current;
        private bool _started;

        private static readonly Regex NumberPattern = new Regex(@"\d+");

        public NumberIncrementer(string start, int step)
        {
            _step = step;
            Match m = LastMatch(start);
            if (m == null)
                throw new ArgumentException("The number contains no digits: " + start);

            _prefix = start.Substring(0, m.Index);
            _suffix = start.Substring(m.Index + m.Length);
            _width = m.Length;
            _current = long.Parse(m.Value);
        }

        public string PeekNext()
        {
            long n = _started ? _current + _step : _current;
            return Format(n);
        }

        public string Next()
        {
            if (_started)
                _current += _step;
            _started = true;
            return Format(_current);
        }

        private string Format(long n)
        {
            string numStr = n.ToString();
            if (numStr.Length < _width)
                numStr = numStr.PadLeft(_width, '0');
            return _prefix + numStr + _suffix;
        }

        private static Match LastMatch(string s)
        {
            Match last = null;
            foreach (Match m in NumberPattern.Matches(s))
                last = m;
            return last;
        }
    }
}
