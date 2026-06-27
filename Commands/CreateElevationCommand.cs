// =============================================================================
//  StructAuto Detailing — "Create Elevation" button
//  File:    Commands/CreateElevationCommand.cs
//
//  Pick a precast column → create its Front and Side elevation views, cropped to
//  the full element with rebar and datum lines hidden, then open the Front view
//  for inspection. No dimensions, no sheets — that is the job of later buttons.
// =============================================================================

using System;
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
    public class CreateElevationCommand : IExternalCommand
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
                    ObjectType.Element,
                    new MainElementSelectionFilter(),
                    "StructAuto: Click a precast column to create its elevation views");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            Element picked = doc.GetElement(pickedRef.ElementId);
            if (!MainElementSelectionFilter.IsColumn(picked))
            {
                TaskDialog.Show("StructAuto — Not Yet Supported",
                    $"'{picked?.Category?.Name}' is not a column. Elevation creation currently " +
                    "supports precast columns only.");
                return Result.Cancelled;
            }

            // ── Parse geometry (read-only) ─────────────────────────────────────
            PrecastColumnData colData;
            try
            {
                colData = ColumnGeometryParser.Parse(doc, pickedRef.ElementId);
            }
            catch (PrecastEngineException pex)
            {
                TaskDialog.Show("StructAuto — Cannot Process Element", pex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("StructAuto — Unexpected Error",
                    $"Geometry parsing failed:\n\n{ex.GetType().Name}: {ex.Message}");
                return Result.Failed;
            }

            // ── Rebar analysis (read-only) — needed to show the cage and to size
            //     the crop for the protruding bars. Non-fatal if it fails. ───────
            try { colData.Rebar = RebarCageAnalyzer.Analyze(doc, colData); }
            catch { /* elevation still works without the cage */ }

            // ── Create the two elevations (Back + Right) ───────────────────────
            ElementId backId, rightId;
            using (var t = new Transaction(doc, "StructAuto: Create Elevation Views"))
            {
                try
                {
                    t.Start();
                    (backId, rightId) = ViewFactory.CreateElevationPair(doc, colData);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    TaskDialog.Show("StructAuto — Elevation Creation Failed",
                        $"{ex.GetType().Name}: {ex.Message}");
                    return Result.Failed;
                }
            }

            // ── Open the Back elevation for inspection ─────────────────────────
            if (doc.GetElement(backId) is View backView)
            {
                try { uidoc.ActiveView = backView; } catch { /* not activatable — ignore */ }
            }

            string backName  = (doc.GetElement(backId)  as View)?.Name ?? "(back)";
            string rightName = (doc.GetElement(rightId) as View)?.Name ?? "(right)";

            TaskDialog.Show("StructAuto — Elevations Created",
                $"Column '{colData.ElementMark}'\n\n" +
                $"Created 2 elevation views:\n" +
                $"   • {backName}\n" +
                $"   • {rightName}\n\n" +
                $"Shaft {colData.ShaftWidthMm:F0} × {colData.ShaftDepthMm:F0} × {colData.TotalHeightMm:F0} mm.\n" +
                "Opened the Back elevation. Use 'Smart Dimensioning' next to dimension it.");

            return Result.Succeeded;
        }
    }
}
