/*
=============================================================================
CsHotLoader - hot reload of .cs scripts inside Revit
=============================================================================
A permanent add-in that compiles and runs .cs files on the fly,
without restarting Revit. A C#-only analogue of PyRevit.

Installation:
  1. Compile this file into CsHotLoader.dll
  2. Put CsHotLoader.dll into the IvanTools folder, for example:
     C:\ProgramData\Autodesk\Revit\Addins\2023\IvanTools\
  3. Register both commands in IvanTools.addin (see the end of this file)
  4. Start Revit - the commands appear in Add-Ins > External Tools as
     "IvanTools.HotLoader" and "IvanTools.HotLoaderRerun"

Usage:
  - Run "IvanTools.HotLoader"
  - Pick a .cs file
  - The script is compiled and executed immediately
  - Edit the .cs, run it again - no Revit restart required

Requirements for a .cs script:
  - A public class implementing IExternalCommand
  - The standard Execute(...) method
  - Any RevitAPI using directives are allowed

Credits:
  Ivan Rodionov    - idea
  Claude Sonnet 4.6 - implementation (Anthropic)

License: MIT

Origin:
https://claude.ai/share/69e7c50c-0c79-47aa-945e-896a71933137
=============================================================================
*/

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CSharp;

namespace IvanTools
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class HotLoader : IExternalCommand
    {
        // Remember the last used file for a quick rerun
        private static string _lastScriptPath = null;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Show the file picker
            string scriptPath = PickScriptFile();
            if (scriptPath == null)
            {
                // If cancelled, offer to rerun the last script
                if (_lastScriptPath != null && File.Exists(_lastScriptPath))
                {
                    var td = new TaskDialog("IvanTools - Hot Loader");
                    td.MainInstruction = "Run the last script?";
                    td.MainContent = _lastScriptPath;
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Yes, run it");
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel");
                    if (td.Show() == TaskDialogResult.CommandLink1)
                        scriptPath = _lastScriptPath;
                    else
                        return Result.Cancelled;
                }
                else
                {
                    return Result.Cancelled;
                }
            }

            _lastScriptPath = scriptPath;
            return CompileAndRun(scriptPath, commandData, ref message, elements);
        }

        private string PickScriptFile()
        {
            // Use the WinForms OpenFileDialog
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title       = "Hot Loader - select a .cs script",
                Filter      = "C# Scripts (*.cs)|*.cs|All files (*.*)|*.*",
                Multiselect = false,
            };

            if (_lastScriptPath != null)
                dlg.InitialDirectory = Path.GetDirectoryName(_lastScriptPath);

            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? dlg.FileName
                : null;
        }

        private Result CompileAndRun(
            string scriptPath,
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // --- 1. Compile ---
            string source;
            try
            {
                source = File.ReadAllText(scriptPath);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Hot Loader - error", "Could not read the file:\n" + ex.Message);
                return Result.Failed;
            }

            var compilerParams = new CompilerParameters
            {
                GenerateInMemory   = true,   // keep the dll in memory, write nothing to disk
                GenerateExecutable = false,
                IncludeDebugInformation = false,
            };

            // Reference the Revit API from the same folder as Revit itself
            string revitDir = Path.GetDirectoryName(
                typeof(Autodesk.Revit.DB.Document).Assembly.Location);

            foreach (string dll in new[] { "RevitAPI.dll", "RevitAPIUI.dll" })
            {
                string fullPath = Path.Combine(revitDir, dll);
                if (File.Exists(fullPath))
                    compilerParams.ReferencedAssemblies.Add(fullPath);
            }

            // Add every assembly that is already loaded - the script may use
            // any dll that already lives in the Revit process.
            // The HashSet guards against duplicates (the same path twice = compiler warning).
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string already in compilerParams.ReferencedAssemblies)
                addedPaths.Add(already);

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location)
                        && addedPaths.Add(asm.Location))
                    {
                        compilerParams.ReferencedAssemblies.Add(asm.Location);
                    }
                }
                catch { }
            }

            CompilerResults results;
            using (var provider = new CSharpCodeProvider())
            {
                results = provider.CompileAssemblyFromSource(compilerParams, source);
            }

            // --- 2. Check compilation errors ---
            if (results.Errors.HasErrors)
            {
                var errorLines = new List<string>();
                foreach (CompilerError err in results.Errors)
                {
                    if (!err.IsWarning)
                        errorLines.Add(string.Format("  [{0}:{1}] {2}", err.Line, err.Column, err.ErrorText));
                }

                TaskDialog.Show("Hot Loader - compilation errors",
                    string.Join("\n", errorLines.Take(15)));
                return Result.Failed;
            }

            // --- 3. Find a class implementing IExternalCommand ---
            Assembly assembly = results.CompiledAssembly;
            Type commandType  = null;

            foreach (Type t in assembly.GetTypes())
            {
                if (typeof(IExternalCommand).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                {
                    commandType = t;
                    break;
                }
            }

            if (commandType == null)
            {
                TaskDialog.Show("Hot Loader - error",
                    "The script has no public class implementing IExternalCommand.");
                return Result.Failed;
            }

            // --- 4. Create an instance and run it ---
            try
            {
                var command = (IExternalCommand)Activator.CreateInstance(commandType);
                return command.Execute(commandData, ref message, elements);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Hot Loader - execution error",
                    string.Format("{0}: {1}\n\n{2}", ex.GetType().Name, ex.Message, ex.StackTrace));
                return Result.Failed;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Second command - rerun the last script without the file picker
    // -------------------------------------------------------------------------
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class HotLoaderRerun : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Delegate to HotLoader with an empty file selection:
            // it will offer to rerun the last script
            var cmd = new HotLoader();
            return cmd.Execute(commandData, ref message, elements);
        }
    }
}

/*
=============================================================================
IvanTools.addin - command entries for this DLL
=============================================================================

  <AddIn Type="Command">
    <Name>IvanTools - Hot Loader</Name>
    <FullClassName>IvanTools.HotLoader</FullClassName>
    <Assembly>.\IvanTools\CsHotLoader.dll</Assembly>
    <AddInId>C0A5E7B4-3F21-4C8D-9A62-1D8E5F0B7A31</AddInId>
    <VendorId>RODIONOV</VendorId>
    <VendorDescription>Ivan Rodionov, Architectural Engineer</VendorDescription>
    <ToolTip>Pick a .cs file, compile and run it without restarting Revit.</ToolTip>
  </AddIn>

  <AddIn Type="Command">
    <Name>IvanTools - Hot Loader Rerun</Name>
    <FullClassName>IvanTools.HotLoaderRerun</FullClassName>
    <Assembly>.\IvanTools\CsHotLoader.dll</Assembly>
    <AddInId>5E2B9C13-7A44-4D18-B6F0-2C9A8D7E4B51</AddInId>
    <VendorId>RODIONOV</VendorId>
    <VendorDescription>Ivan Rodionov, Architectural Engineer</VendorDescription>
    <ToolTip>Run the last script again without picking a file.</ToolTip>
  </AddIn>

=============================================================================
Compilation (CMD):

set PATH=%PATH%;C:\Windows\Microsoft.NET\Framework64\v4.0.30319

csc /target:library /out:CsHotLoader.dll ^
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll" ^
    /reference:"C:\Program Files\Autodesk\Revit 2023\RevitAPIUI.dll" ^
    /reference:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll" ^
    CsHotLoader.cs
=============================================================================
*/
