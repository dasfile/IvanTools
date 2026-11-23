/*TITLE: Sheets Batch Renumbering
DESCRIPTION: Batch renumbers selected sheets in Project Browser with priority for 'General Data'.
PROJECT ROLES:
Initiator, Prompt Engineer & Tester: Ivan Rodionov
Developer: Gemini (Google)
Contributor: ChatGPT (OpenAI)
LICENSE: MIT License
HOW TO USE IN PYREVIT:
1. Create the folder structure: ...\MyTools.panel\SheetsBatchRenum.pushbutton\
2. Save this code as 'script.cs' inside that folder.
3. Reload pyRevit.
4. Select sheets in the Project Browser and run the command.
5. The Project Browser tree is refreshed automatically: after the transaction the
   panel is briefly hidden and shown again through the Idling event, because
   Revit does not rebuild the sheet tree on SheetNumber changes by itself.

Dialog (public): https://gemini.google.com/share/a330d57fb913
Private: https://gemini.google.com/app/9c30d11141a5fc3d
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace IvanTools
{
[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
public class RenumberSheets : IExternalCommand
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var dialog = new RenumberDialog();
        if (dialog.ShowDialog() != true) return Result.Cancelled;

        string prefix = dialog.Prefix;
        int startNumber = dialog.StartNumber;
        int padding = dialog.DigitsPadding;
        string priorityName = "General Data"; // project-specific parameter value

        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
        var sheets = selectedIds
            .Select(id => doc.GetElement(id))
            .OfType<ViewSheet>()
            .OrderBy(s => s.Name.IndexOf(priorityName, StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
            .ThenBy(s => s.SheetNumber, Comparer<string>.Create((x, y) => StrCmpLogicalW(x, y)))
            .ToList();

        if (sheets.Count == 0)
        {
            TaskDialog.Show("IvanTools", "Select sheets in the Project Browser.");
            return Result.Cancelled;
        }

        using (Transaction t = new Transaction(doc, "Batch Renumber Sheets"))
        {
            t.Start();
            try
            {
                string mark = DateTime.Now.Ticks.ToString().Substring(10);
                foreach (var s in sheets)
                {
                    s.SheetNumber = "TMP_" + mark + "_" + Guid.NewGuid().ToString().Substring(0,4);
                }

                for (int i = 0; i < sheets.Count; i++)
                {
                    string numPart = (i + startNumber).ToString().PadLeft(padding, '0');
                    string targetNumber = prefix + numPart;

                    int safety = 1;
                    string uniqueNumber = targetNumber;
                    while (IsExists(doc, uniqueNumber))
                    {
                        uniqueNumber = targetNumber + "_" + safety++;
                    }
                    sheets[i].SheetNumber = uniqueNumber;
                }

                doc.Regenerate();
                t.Commit();
                uidoc.RefreshActiveView();

                // Changed success message
                TaskDialog.Show("Success", string.Format("Done! Processed sheets: {0}", sheets.Count));
            }
            catch (Exception ex)
            {
                message = ex.Message;
                t.RollBack();
                return Result.Failed;
            }
        }

        // Revit does not rebuild the Project Browser sheet tree after SheetNumber
        // changes, so the panel is toggled on the next Idling cycles.
        RefreshProjectBrowser(commandData.Application);

        return Result.Succeeded;
    }
    private bool IsExists(Document doc, string num)
    {
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Any(s => s.SheetNumber.Equals(num, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Automatic refresh of the Project Browser tree.
    //
    // After a SheetNumber change Revit does not rebuild the sheet tree until
    // the panel content is created again. Therefore the panel is hidden on the
    // first Idling cycle and shown on the next one. If Revit refuses to hide
    // the built-in panel, the same toggle command is posted that the user would
    // run manually (View > User Interface > Project Browser).
    // ------------------------------------------------------------------
    private static DockablePaneId _browserPaneId;
    private static UIApplication _refreshApp;
    private static EventHandler<IdlingEventArgs> _refreshHandler;
    private static int _refreshStep;
    private static int _hideMode; // 0 - failed, 1 - DockablePane, 2 - PostCommand

    private static void RefreshProjectBrowser(UIApplication uiApp)
    {
        try
        {
            _browserPaneId = DockablePanes.BuiltInDockablePanes.ProjectBrowser;
            if (!DockablePane.PaneExists(_browserPaneId)) return;
            if (uiApp.GetDockablePane(_browserPaneId) == null) return;

            _refreshApp = uiApp;
            _refreshStep = 0;
            _hideMode = 0;

            if (_refreshHandler == null)
                _refreshHandler = new EventHandler<IdlingEventArgs>(OnRefreshIdle);

            uiApp.Idling -= _refreshHandler; // in case of a repeated run
            uiApp.Idling += _refreshHandler;
        }
        catch
        {
            // Refreshing the browser is not critical for the command result.
        }
    }

    private static void OnRefreshIdle(object sender, IdlingEventArgs e)
    {
        UIApplication uiApp = _refreshApp;
        if (uiApp == null || _browserPaneId == null)
        {
            UnsubscribeRefreshIdle();
            return;
        }

        try
        {
            DockablePane pane = uiApp.GetDockablePane(_browserPaneId);

            if (_refreshStep == 0)
            {
                _refreshStep = 1;

                // IsShown() for the built-in panel is unreliable in Revit 2023
                // (it returns false for a visible Project Browser), so the panel
                // is hidden without any preliminary checks.
                try
                {
                    if (pane != null)
                    {
                        pane.Hide();
                        _hideMode = 1;
                    }
                }
                catch
                {
                    _hideMode = 0;
                }

                if (_hideMode == 0)
                    _hideMode = TryPostProjectBrowserCommand(uiApp) ? 2 : 0;

                return;
            }

            if (_refreshStep == 1)
            {
                _refreshStep = 2;

                if (_hideMode == 1)
                {
                    try
                    {
                        if (pane != null) pane.Show();
                    }
                    catch
                    {
                    }
                }
                else if (_hideMode == 2)
                {
                    // The panel was toggled by the command - a second toggle restores it.
                    TryPostProjectBrowserCommand(uiApp);
                }
                return;
            }

            // Safety net: the panel must stay visible.
            if (_hideMode == 1)
            {
                try
                {
                    if (pane != null) pane.Show();
                }
                catch
                {
                }
            }

            UnsubscribeRefreshIdle();
        }
        catch
        {
            UnsubscribeRefreshIdle();
        }
    }

    private static void UnsubscribeRefreshIdle()
    {
        try
        {
            if (_refreshApp != null && _refreshHandler != null)
                _refreshApp.Idling -= _refreshHandler;
        }
        catch
        {
        }

        _refreshApp = null;
        _refreshStep = 0;
        _hideMode = 0;
    }

    private static bool TryPostProjectBrowserCommand(UIApplication uiApp)
    {
        try
        {
            RevitCommandId cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.ProjectBrowser);
            if (uiApp.CanPostCommand(cmdId))
            {
                uiApp.PostCommand(cmdId);
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
// DIALOG WINDOW CLASS
public class RenumberDialog : Window
{
    public string Prefix { get; private set; }
    public int StartNumber { get; private set; }
    public int DigitsPadding { get; private set; }

    private System.Windows.Controls.TextBox txtPrefix;
    private System.Windows.Controls.TextBox txtStart;
    private System.Windows.Controls.TextBox txtPadding;

    public RenumberDialog()
    {
        Title = "Renumbering Settings";
        Width = 320; Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = System.Windows.Media.Brushes.White;
        Topmost = true;

        StackPanel panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock { Text = "Prefix:", Margin = new Thickness(0, 5, 0, 0) });
        txtPrefix = new System.Windows.Controls.TextBox { Text = "", Height = 25 };
        panel.Children.Add(txtPrefix);

        panel.Children.Add(new TextBlock { Text = "Start Number:", Margin = new Thickness(0, 10, 0, 0) });
        txtStart = new System.Windows.Controls.TextBox { Text = "1", Height = 25 };
        panel.Children.Add(txtStart);

        panel.Children.Add(new TextBlock { Text = "Digits (Padding):", Margin = new Thickness(0, 10, 0, 0) });
        txtPadding = new System.Windows.Controls.TextBox { Text = "2", Height = 25 };
        panel.Children.Add(txtPadding);

        // Changed button content to "OK"
        System.Windows.Controls.Button btnOk = new System.Windows.Controls.Button
        {
            Content = "OK", // Simplified button text
            Margin = new Thickness(0, 20, 0, 0),
            IsDefault = true,
            Height = 35,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 221, 221))
        };

/*         btnOk.Click += (s, e) => {
            Prefix = txtPrefix.Text;
            StartNumber = int.TryParse(txtStart.Text, out int sn) ? sn : 1;
            DigitsPadding = int.TryParse(txtPadding.Text, out int p) ? p : 2;
            DialogResult = true;
        }; */

        btnOk.Click += (s, e) => {
            Prefix = txtPrefix.Text;

            // Declare variables up front for C# 5 compatibility
            int sn;
            int p;

            StartNumber = int.TryParse(txtStart.Text, out sn) ? sn : 1;
            DigitsPadding = int.TryParse(txtPadding.Text, out p) ? p : 2;

            DialogResult = true;
        };

        panel.Children.Add(btnOk);

        Content = panel;
    }
}
}