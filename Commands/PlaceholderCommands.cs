// =============================================================================
//  StructAuto Detailing — "Create Sections" + placeholder "Smart Dimensioning"
//  File:    Commands/PlaceholderCommands.cs
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using StructAutoDetailing.Models;
using StructAutoDetailing.Parsers;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateSectionsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uidoc.Document;

            // ── Pick a column ──────────────────────────────────────────────────
            Reference pickedRef;
            try
            {
                pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element, new MainElementSelectionFilter(),
                    "StructAuto: Click a precast column to create its cut-section views");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            Element picked = doc.GetElement(pickedRef.ElementId);
            if (!MainElementSelectionFilter.IsColumn(picked))
            {
                TaskDialog.Show("StructAuto — Not Yet Supported",
                    $"'{picked?.Category?.Name}' is not a column. Sections support precast columns only.");
                return Result.Cancelled;
            }

            // ── Parse geometry + rebar (read-only) ─────────────────────────────
            PrecastColumnData colData;
            try
            {
                colData = ColumnGeometryParser.Parse(doc, pickedRef.ElementId);
                try { colData.Rebar = RebarCageAnalyzer.Analyze(doc, colData); } catch { }
            }
            catch (PrecastEngineException pex)
            {
                TaskDialog.Show("StructAuto — Cannot Process Element", pex.Message);
                return Result.Failed;
            }

            // ── Create the cut sections + A/B/C markers ────────────────────────
            List<(ElementId viewId, string tag)> sections;
            int markers; bool rightFound;
            using (var t = new Transaction(doc, "StructAuto: Create Cut Sections"))
            {
                try
                {
                    t.Start();
                    sections = ViewFactory.CreateCrossSections(doc, colData, out markers, out rightFound);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    TaskDialog.Show("StructAuto — Section Creation Failed", $"{ex.GetType().Name}: {ex.Message}");
                    return Result.Failed;
                }
            }

            // ── Open the Right elevation so the A/B/C markers are visible ──────
            string mark = colData.ElementMark;
            ViewSection rightElev = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .FirstOrDefault(v => v.Name != null && v.Name.EndsWith("- Right Elevation"));
            if (rightElev != null) { try { uidoc.ActiveView = rightElev; } catch { } }

            string list = string.Join("\n", sections.Select(s =>
                $"   • {(doc.GetElement(s.viewId) as View)?.Name}"));
            string note = rightFound
                ? $"Placed {markers} A/B/C marker(s) on the Right elevation."
                : "No 'Right Elevation' found — run 'Create Elevation' first to get the A/B/C markers.";

            var td = new TaskDialog("StructAuto — Sections Created")
            {
                MainInstruction = $"Column '{colData.ElementMark}' — {sections.Count} cut section(s)",
                MainContent = $"Created cut-section view(s):\n{list}\n\n{note}",
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            if (colData.ParseWarnings.Count > 0)
                td.ExpandedContent = string.Join("\n", colData.ParseWarnings.Take(40));
            td.Show();

            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SmartDimensioningCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show("StructAuto — Smart Dimensioning (coming next)",
                "This button will add the standard dimensions to a generated view. As suggested, it " +
                "will let you pick a reference line / face to dimension from where standardization is " +
                "required, then place the dimension chains relative to it.\n\n" +
                "Create the elevation views first with 'Create Elevation'.");
            return Result.Cancelled;
        }
    }
}
