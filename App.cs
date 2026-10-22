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

            // 1. COLUMN PANEL
            RibbonPanel columnPanel = application.CreateRibbonPanel(tabName, "Smart Column Dimensioning");
            AddButton(columnPanel, asm,
                "cmdSmartDim", "Smart Column\nDimensioning",
                "StructAutoDetailing.SmartDimensioningCommand",
                "Add standard multi-tier dimensions to a column assembly view.");

            // 2. BEAM PANEL
            RibbonPanel beamPanel = application.CreateRibbonPanel(tabName, "Smart Beam Dimensioning");
            AddButton(beamPanel, asm,
                "cmdSmartBeamDim", "Smart Beam\nDimensioning",
                "StructAutoDetailing.SmartBeamDimensioningCommand",
                "Automatically dimension overall length, depth, and embeds of a beam.");

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
