using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace StructAutoDetailing
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            // 1. Create a custom ribbon tab
            string tabName = "StructAuto";
            application.CreateRibbonTab(tabName);

            // 2. Create a ribbon panel inside that tab
            RibbonPanel detailingPanel = application.CreateRibbonPanel(tabName, "Detailing");

            // 3. Get the path of the current DLL so Revit knows where to find the execution code
            string thisAssemblyPath = Assembly.GetExecutingAssembly().Location;

            // 4. Create the button data
            // Parameters: Internal Name, Display Text, Assembly Path, Namespace.ClassName of the command
            PushButtonData buttonData = new PushButtonData(
                "cmdGenerateSheets",
                "Generate\nSheets",
                thisAssemblyPath,
                "StructAutoDetailing.GenerateSheetsCommand");

            // 5. Add the button to the panel
            PushButton generateButton = detailingPanel.AddItem(buttonData) as PushButton;

            // Optional: Add a tooltip so the user knows what it does
            generateButton.ToolTip = "Automatically generates Formwork and Reinforcement sheets for selected elements.";

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Nothing needed here for now
            return Result.Succeeded;
        }
    }
}