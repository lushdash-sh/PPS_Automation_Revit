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

            // 1. COLUMN PANEL - one drop-down with a sub-item per view type
            RibbonPanel columnPanel = application.CreateRibbonPanel(tabName, "Smart Column Dimensioning");
            PulldownButtonData columnMenu = new PulldownButtonData("ddSmartColumn", "Smart Column\nDimensioning");
            if (columnPanel.AddItem(columnMenu) is PulldownButton columnButton)
            {
                columnButton.ToolTip = "Dimension a precast column assembly - choose the view type.";

                AddMenuItem(columnButton, asm,
                    "cmdSmartDim", "Elevation View Dimensioning",
                    "StructAutoDetailing.SmartDimensioningCommand",
                    "Add standard multi-tier dimensions to a column assembly elevation view.");

                AddMenuItem(columnButton, asm,
                    "cmdSmartColumnSectionDim", "Section View Dimensioning",
                    "StructAutoDetailing.ColumnSectionDimensioningCommand",
                    "Dimension a column section view: overall size and the centres of the corrugated metal sleeves (CMS 1, CMS 2).");
            }

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

        private static void AddMenuItem(PulldownButton menu, string assemblyPath,
            string internalName, string text, string className, string tooltip)
        {
            var data = new PushButtonData(internalName, text, assemblyPath, className);
            PushButton btn = menu.AddPushButton(data);
            if (btn != null) btn.ToolTip = tooltip;
        }
    }
}