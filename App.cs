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
            
            // Try-catch prevents crashes if the tab already exists
            try { application.CreateRibbonTab(tabName); } catch { }

            string asm = Assembly.GetExecutingAssembly().Location;

            // COLUMN PANEL
            RibbonPanel columnPanel = application.CreateRibbonPanel(tabName, "Precast Column");

            AddButton(columnPanel, asm,
                "cmdCreateElevation", "Create\nElevation",
                "StructAutoDetailing.CreateElevationCommand",
                "Pick a precast column and create its Front and Side elevation views.");

            AddButton(columnPanel, asm,
                "cmdCreateSections", "Create\nSections",
                "StructAutoDetailing.CreateSectionsCommand",
                "Pick a precast column and create cut-section views where the reinforcement changes.");

            columnPanel.AddSeparator();

            AddButton(columnPanel, asm,
                "cmdSmartDim", "Smart\nDimensioning",
                "StructAutoDetailing.SmartDimensioningCommand",
                "Add standard dimensions to a generated view, using a reference line you pick.");

            AddButton(columnPanel, asm,
                "cmdGenerateSheets", "Generate\nSheets",
                "StructAutoDetailing.GenerateSheetsCommand",
                "Assemble the generated views onto Formwork and Reinforcement sheets.");


            // WALL PANEL
            RibbonPanel wallPanel = application.CreateRibbonPanel(tabName, "Precast Wall");
            
            AddButton(wallPanel, asm,
                "cmdSmartWallDim", "Smart Wall\nDimensioning",
                "StructAutoDetailing.WallDimensioningCommand",
                "Automatically dimension overall length and height of walls in the current view.");

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
