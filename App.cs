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

            try { application.CreateRibbonTab(tabName); } catch { }

            string asm = Assembly.GetExecutingAssembly().Location;

            // 1. COLUMN PANEL - one button for every column view (elevation and section).
            //    A pop-up lists the dimensions that fit the active view.
            RibbonPanel columnPanel = application.CreateRibbonPanel(tabName, "Smart Column Dimensioning");
            AddButton(columnPanel, asm,
                "cmdSmartColumnDim", "Smart Column\nDimensioning",
                "StructAutoDetailing.ColumnSectionDimensioningCommand",
                "Dimension a precast column assembly view (elevation or section). Choose the dimensions in the pop-up, then pick the column.");

            // 2. STRUCTURAL FRAMING PANEL (was "Smart Beam Dimensioning")
            RibbonPanel framingPanel = application.CreateRibbonPanel(tabName, "Structural Framing Dimensioning");
            AddButton(framingPanel, asm,
                "cmdSmartBeamDim", "Structural Framing\nDimensioning",
                "StructAutoDetailing.SmartBeamDimensioningCommand",
                "Automatically dimension overall length, depth, and embeds of structural framing.");

            // 3. WALL PANEL
            RibbonPanel wallPanel = application.CreateRibbonPanel(tabName, "Precast Wall");
            AddButton(wallPanel, asm,
                "cmdSmartWallDim", "Smart Wall\nDimensioning",
                "StructAutoDetailing.WallDimensioningCommand",
                "Automatically dimension overall length and height of walls.");

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