using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace StructAutoDetailing
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string tabName = "PPS Shop Tools";
            application.CreateRibbonTab(tabName);

            // The workflow is split into focused steps, each its own button.
            RibbonPanel panel = application.CreateRibbonPanel(tabName, "Precast Column");
            string asm = Assembly.GetExecutingAssembly().Location;

            AddButton(panel, asm,
                "cmdCreateElevation", "Create\nElevation",
                "StructAutoDetailing.CreateElevationCommand",
                "Pick a precast column and create its Front and Side elevation views.");

            AddButton(panel, asm,
                "cmdCreateSections", "Create\nSections",
                "StructAutoDetailing.CreateSectionsCommand",
                "Pick a precast column and create cut-section views where the reinforcement changes.");

            panel.AddSeparator();

            AddButton(panel, asm,
                "cmdSmartDim", "Smart\nDimensioning",
                "StructAutoDetailing.SmartDimensioningCommand",
                "Add standard dimensions to a generated view, using a reference line you pick.");

            AddButton(panel, asm,
                "cmdGenerateSheets", "Generate\nSheets",
                "StructAutoDetailing.GenerateSheetsCommand",
                "Assemble the generated views onto Formwork and Reinforcement sheets.");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private static void AddButton(RibbonPanel panel, string assemblyPath,
            string internalName, string text, string className, string tooltip)
        {
            var data = new PushButtonData(internalName, text, assemblyPath, className);
            if (panel.AddItem(data) is PushButton btn)
                btn.ToolTip = tooltip;
        }
    }
}
