// =============================================================================
//  StructAuto Detailing — Command Entry Point
//  File:    Commands/GenerateSheetsCommand.cs
//
//  This is the IExternalCommand.Execute implementation.
//  Currently wired for Phase 0 + Phase 1 ONLY (geometry parse and debug output).
//  Phases 2–5 will be uncommented / added in subsequent sprints.
//
//  To register this command, add to your Application.cs:
//      panel.AddItem(new PushButtonData(
//          "GenerateSheets",
//          "Generate\nSheets",
//          Assembly.GetExecutingAssembly().Location,
//          "StructAutoDetailing.Commands.GenerateSheetsCommand"));
// =============================================================================

using System;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using System.Linq;
using StructAutoDetailing.Models;
using StructAutoDetailing.Parsers;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateSheetsCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument    uidoc = uiApp.ActiveUIDocument;
            Document      doc   = uidoc.Document;

            // ── Step 1: Prompt user to select a column ─────────────────────
            Reference pickedRef;
            try
            {
                pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new PrecastColumnSelectionFilter(),
                    "StructAuto: Click a Precast Column to generate shop drawings");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User pressed Escape — not an error
                return Result.Cancelled;
            }

            // ── Step 2: Parse geometry (Phase 0 + Phase 1) ────────────────
            PrecastColumnData colData;
            try
            {
                // Phase 1 has no Revit writes — no transaction needed for parsing.
                // (BoundingBoxIntersectsFilter reads are always safe outside transactions.)
                colData = ColumnGeometryParser.Parse(doc, pickedRef.ElementId);
            }
            catch (PrecastEngineException pex)
            {
                // Known, user-facing errors (inclined column, no geometry, etc.)
                TaskDialog.Show(
                    "StructAuto — Cannot Process Element",
                    pex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                // Unexpected API error — show full detail so developer can diagnose
                TaskDialog.Show(
                    "StructAuto — Unexpected Error in Phase 1",
                    $"An unexpected error occurred during geometry parsing:\n\n" +
                    $"{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Stack trace (top):\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
                return Result.Failed;
            }

            // ── Step 3: Phase 2 — Rebar analysis (read-only, no transaction) ──
            try
            {
                colData.Rebar = RebarCageAnalyzer.Analyze(doc, colData);
            }
            catch (PrecastEngineException pex)
            {
                TaskDialog.Show("StructAuto — Phase 2 Error", pex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(
                    "StructAuto — Unexpected Error in Phase 2",
                    $"Rebar analysis failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Stack trace (top):\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
                return Result.Failed;
            }

            // ── Step 4: Show combined debug summary ─────────────────────
            ParseDiagnostics.ShowParseSummary(uiApp, colData);

            // ── Step 5 (FUTURE): Phases 3–5 — View creation and sheet assembly ──
            // using (var tg = new TransactionGroup(doc, "StructAuto: Generate Shop Drawings"))
            // {
            //     tg.Start();
            //     ...Phases 3–5 go here...
            //     tg.Assemble();
            // }

            // ── Temporary success message ──────────────────────────────────
            var rebar = colData.Rebar;
            TaskDialog.Show(
                "StructAuto — Phase 1 + 2 Complete",
                $"Column '{colData.ElementMark}' — geometry and rebar parsed successfully.\n\n" +
                $"── Geometry ──────────────────────────\n" +
                $"Shaft:   {colData.ShaftWidthMm:F0} × {colData.ShaftDepthMm:F0} × {colData.TotalHeightMm:F0} mm\n" +
                $"Base EL: {colData.BaseElevationM:F3} m  |  Top EL: {colData.TopElevationM:F3} m\n" +
                $"Volume:  {colData.VolumeM3:F2} m³  |  Est. Weight: {colData.WeightTonnes:F2} T\n\n" +
                $"── Embeds ────────────────────────────\n" +
                $"Corbels: {colData.Corbels.Count}  |  Sleeves: {colData.CorrugatedSleeves.Count}\n" +
                $"Lifters: {colData.Lifters.Count}  |  Dowels:  {colData.DowelBars.Count}\n\n" +
                $"── Rebar ─────────────────────────────\n" +
                $"Longitudinal bars: {rebar.LongitudinalBars.Count}\n" +
                $"Transverse bars:   {rebar.TransverseBars.Count}\n" +
                $"Tie zones:         {rebar.TieZones.Count}\n" +
                $"Schedule rows:     {rebar.BendSchedule.Count}\n" +
                $"Total rebar wt:    {rebar.TotalWeightFormatted}\n\n" +
                $"Tie zones:\n" +
                string.Join("\n", rebar.TieZones.Select(z =>
                    $"  [{z.ZoneIndex + 1}] {z.MraTagText}  T{z.TieDiameterMm}  " +
                    $"Z={z.StartZMm:F0}–{z.EndZMm:F0}mm")) + "\n\n" +
                $"Warnings: {colData.ParseWarnings.Count}\n\n" +
                "View and sheet generation (Phases 3–5) will be enabled in the next build.");

            return Result.Succeeded;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SELECTION FILTER
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restricts the PickObject prompt to structural column FamilyInstances only.
    /// The filter checks both the structural type and the BuiltInCategory so it
    /// accepts precast columns placed in either the Structural Columns or the
    /// Specialty Equipment category (common in some precast workflows).
    /// </summary>
    public class PrecastColumnSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;

            // Must be a FamilyInstance
            if (!(elem is FamilyInstance fi)) return false;

            // Accept structural columns
            if (elem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralColumns)
                return true;

            // Also accept Specialty Equipment (some precast families use this category)
            if (elem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_SpecialityEquipment)
                return true;

            // Accept Generic Models that have a "Precast" or "Column" keyword in family name
            // (permissive fallback for non-standard family categorisation)
            if (elem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel)
            {
                string famName = (fi.Symbol?.FamilyName ?? string.Empty).ToUpperInvariant();
                if (famName.Contains("PRECAST") || famName.Contains("COLUMN") || famName.Contains("FPC"))
                    return true;
            }

            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}