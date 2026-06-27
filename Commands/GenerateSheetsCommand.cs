// =============================================================================
//  StructAuto Detailing — Command Entry Point
//  File:    Commands/GenerateSheetsCommand.cs
//
//  Pipeline on click:
//    1. Pick — restricted to structural Column / Beam / Wall (pick filter).
//    2. Phase 1 — geometry parse        (read-only)
//    3. Phase 2 — rebar cage analysis   (read-only)
//    4. Phases 3–5 — views, dimensions, sheets (inside a TransactionGroup)
//
//  Only columns are detailed in this build; picking a beam or wall is reported as
//  "not yet supported".
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

            // ── Step 1: Pick a main element (column/beam/wall) ─────────────────
            Reference pickedRef;
            try
            {
                pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new MainElementSelectionFilter(),
                    "StructAuto: Click a Column (Beam/Wall not yet supported) to generate shop drawings");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled; // Escape — not an error
            }

            // ── Only columns are detailed in this build ────────────────────────
            Element picked = doc.GetElement(pickedRef.ElementId);
            if (!MainElementSelectionFilter.IsColumn(picked))
            {
                TaskDialog.Show("StructAuto — Not Yet Supported",
                    $"'{picked?.Category?.Name}' elements are not detailed yet.\n\n" +
                    "This build generates Formwork and Reinforcement sheets for precast columns only. " +
                    "Beam and wall support is planned for a later sprint.");
                return Result.Cancelled;
            }

            // ── Step 2: Phase 1 — geometry parse (read-only) ───────────────────
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
                TaskDialog.Show("StructAuto — Unexpected Error in Phase 1",
                    $"Geometry parsing failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Stack trace (top):\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
                return Result.Failed;
            }

            // ── Step 3: Phase 2 — rebar analysis (read-only) ───────────────────
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
                TaskDialog.Show("StructAuto — Unexpected Error in Phase 2",
                    $"Rebar analysis failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                    $"Stack trace (top):\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
                return Result.Failed;
            }

            // ── Step 4: Phases 3–5 — views, dimensions, sheets ─────────────────
            List<ColumnView> createdViews = new List<ColumnView>();
            SheetBuildResult sheets = new SheetBuildResult();
            using (var tg = new TransactionGroup(doc, "StructAuto: Generate Column Shop Drawings"))
            {
                try
                {
                    tg.Start();

                    using (var t = new Transaction(doc, "StructAuto: Create Views"))
                    {
                        t.Start();
                        createdViews = ViewFactory.CreateViews(doc, colData);
                        t.Commit();
                    }

                    using (var t = new Transaction(doc, "StructAuto: Annotate Views"))
                    {
                        t.Start();
                        DimensionFactory.Annotate(doc, colData, createdViews);
                        t.Commit();
                    }

                    using (var t = new Transaction(doc, "StructAuto: Assemble Sheets"))
                    {
                        t.Start();
                        sheets = SheetBuilder.Build(doc, colData, createdViews);
                        t.Commit();
                    }

                    tg.Assimilate();
                }
                catch (Exception ex)
                {
                    if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                    TaskDialog.Show("StructAuto — View/Sheet Generation Failed",
                        $"{ex.GetType().Name}: {ex.Message}\n\n" +
                        $"Stack trace (top):\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
                    return Result.Failed;
                }
            }

            // ── Step 5: Result summary ─────────────────────────────────────────
            ShowResult(colData, createdViews, sheets);
            return Result.Succeeded;
        }

        private static void ShowResult(PrecastColumnData colData, List<ColumnView> views, SheetBuildResult sheets)
        {
            int sections = views.Count(v => v.Kind == ViewKind.CrossSection &&
                                            v.Variant == SheetVariant.Formwork);

            var td = new TaskDialog("StructAuto — Sheets Generated")
            {
                MainInstruction = $"Column '{colData.ElementMark}' — shop drawings generated",
                MainContent =
                    $"Formwork sheet:      {sheets.FormworkSheetNumber ?? "(failed)"}\n" +
                    $"Reinforcement sheet: {sheets.ReinforcementSheetNumber ?? "(failed)"}\n\n" +
                    $"Views created:   {views.Count}  ({sections} cross-section(s) per sheet)\n" +
                    $"Viewports placed: {sheets.ViewportsPlaced}\n\n" +
                    $"Shaft: {colData.ShaftWidthMm:F0} × {colData.ShaftDepthMm:F0} × {colData.TotalHeightMm:F0} mm   " +
                    $"Tie zones: {colData.Rebar?.TieZones.Count ?? 0}\n\n" +
                    $"Warnings: {colData.ParseWarnings.Count} " +
                    (colData.ParseWarnings.Count > 0
                        ? "(specialized dimensions / fragile references — complete manually)"
                        : "")
            };

            if (colData.ParseWarnings.Count > 0)
            {
                td.ExpandedContent = string.Join("\n",
                    colData.ParseWarnings.Take(40).Select((w, i) => $"  {i + 1}. {w}"));
            }
            td.CommonButtons = TaskDialogCommonButtons.Ok;
            td.Show();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SELECTION FILTER  — Column / Beam / Wall
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Restricts picking to the structural main elements: columns, beams
    /// (structural framing) and walls. Columns also accept the Specialty Equipment
    /// and Generic Model categories used by some precast families.
    /// </summary>
    public class MainElementSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem?.Category == null) return false;
            long cat = elem.Category.Id.Value;

            if (cat == (long)(int)BuiltInCategory.OST_StructuralColumns) return true;
            if (cat == (long)(int)BuiltInCategory.OST_StructuralFraming) return true; // beams
            if (cat == (long)(int)BuiltInCategory.OST_Walls)             return true;

            // Precast columns occasionally live in these categories.
            if (cat == (long)(int)BuiltInCategory.OST_SpecialityEquipment ||
                cat == (long)(int)BuiltInCategory.OST_GenericModel)
            {
                string famName = ((elem as FamilyInstance)?.Symbol?.FamilyName ?? string.Empty).ToUpperInvariant();
                if (famName.Contains("PRECAST") || famName.Contains("COLUMN") || famName.Contains("FPC"))
                    return true;
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;

        /// <summary>True if the element is (or behaves as) a precast column.</summary>
        public static bool IsColumn(Element elem)
        {
            if (elem?.Category == null) return false;
            long cat = elem.Category.Id.Value;
            if (cat == (long)(int)BuiltInCategory.OST_StructuralColumns) return true;

            if (cat == (long)(int)BuiltInCategory.OST_SpecialityEquipment ||
                cat == (long)(int)BuiltInCategory.OST_GenericModel)
            {
                string famName = ((elem as FamilyInstance)?.Symbol?.FamilyName ?? string.Empty).ToUpperInvariant();
                return famName.Contains("PRECAST") || famName.Contains("COLUMN") || famName.Contains("FPC");
            }
            return false;
        }
    }
}
