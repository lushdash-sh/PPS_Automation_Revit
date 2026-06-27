// =============================================================================
//  StructAuto Detailing — Phase 5: Sheet Builder
//  File:    Parsers/SheetBuilder.cs
//
//  Creates the two shop-drawing sheets and places the generated views on them:
//    • Formwork sheet       "{mark}-F"  — DETAILS OF PRECAST COLUMN
//    • Reinforcement sheet  "{mark}-R"  — REINFORCEMENT DETAILS OF PRECAST COLUMN
//
//  Uses the first available titleblock type in the project (per project decision).
//  Viewports are laid out on an A3-landscape grid: front elevation (left),
//  side elevation (middle), cross sections stacked on the right.  Positions are
//  approximate — the user fine-tunes on the sheet — but never overlap by default.
//
//  Must be called inside an open Revit transaction.
//
//  Revit API version: 2027 (.NET Framework 4.8)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;

using StructAutoDetailing.Models;

namespace StructAutoDetailing.Parsers
{
    public class SheetBuildResult
    {
        public ElementId FormworkSheetId = ElementId.InvalidElementId;
        public ElementId ReinforcementSheetId = ElementId.InvalidElementId;
        public string FormworkSheetNumber;
        public string ReinforcementSheetNumber;
        public int ViewportsPlaced;
    }

    public static class SheetBuilder
    {
        // Temporary spawn point for a freshly created viewport, in feet (1 ft = 304.8 mm).
        // ArrangeViewports() repositions everything afterwards by measured size.
        private static readonly XYZ FRONT_ELEV_PT = new XYZ(UnitConv.MmToFt(70), UnitConv.MmToFt(150), 0);

        public static SheetBuildResult Build(Document doc, PrecastColumnData colData, List<ColumnView> views)
        {
            var result = new SheetBuildResult();
            ElementId tbId = FindTitleBlock(doc);
            // Derived lazily from the first viewport actually created (robust — the
            // category-based lookup of viewport types can come back empty).
            ElementId noTitleVpType = ElementId.InvalidElementId;
            string mark = SafeMark(colData);

            // ── Formwork sheet ────────────────────────────────────────────────
            var fwViews = views.Where(v => v.Variant == SheetVariant.Formwork).ToList();
            ViewSheet fwSheet = CreateSheet(doc, tbId, $"{mark}-F", "DETAILS OF PRECAST COLUMN", colData);
            if (fwSheet != null)
            {
                result.FormworkSheetId = fwSheet.Id;
                result.FormworkSheetNumber = fwSheet.SheetNumber;
                colData.SheetId_Formwork = (int)fwSheet.Id.Value;
                result.ViewportsPlaced += PlaceViews(doc, fwSheet, fwViews, colData, ref noTitleVpType);
            }

            // ── Reinforcement sheet ───────────────────────────────────────────
            var rfViews = views.Where(v => v.Variant == SheetVariant.Reinforcement).ToList();
            ViewSheet rfSheet = CreateSheet(doc, tbId, $"{mark}-R",
                "REINFORCEMENT DETAILS OF PRECAST COLUMN", colData);
            if (rfSheet != null)
            {
                result.ReinforcementSheetId = rfSheet.Id;
                result.ReinforcementSheetNumber = rfSheet.SheetNumber;
                colData.SheetId_Rebar = (int)rfSheet.Id.Value;
                result.ViewportsPlaced += PlaceViews(doc, rfSheet, rfViews, colData, ref noTitleVpType);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SHEET CREATION
        // ─────────────────────────────────────────────────────────────────────

        private static ViewSheet CreateSheet(Document doc, ElementId tbId,
            string number, string name, PrecastColumnData colData)
        {
            try
            {
                ViewSheet sheet = ViewSheet.Create(doc, tbId);
                TrySetSheetNumber(sheet, number);
                try { sheet.Name = name; } catch { /* name clash — leave default */ }
                return sheet;
            }
            catch (Exception ex)
            {
                colData.ParseWarnings.Add($"Phase 5: could not create sheet '{number}': {ex.Message}");
                return null;
            }
        }

        private static void TrySetSheetNumber(ViewSheet sheet, string number)
        {
            for (int i = 0; i < 50; i++)
            {
                string candidate = i == 0 ? number : $"{number}-{i}";
                try { sheet.SheetNumber = candidate; return; }
                catch { /* number in use — try next suffix */ }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  VIEWPORT PLACEMENT
        // ─────────────────────────────────────────────────────────────────────

        private static int PlaceViews(Document doc, ViewSheet sheet, List<ColumnView> views,
            PrecastColumnData colData, ref ElementId noTitleVpType)
        {
            // Pass 1 — create every viewport (initial position is temporary).
            var made = new List<(Viewport vp, ColumnView cv)>();
            foreach (ColumnView cv in views)
            {
                try
                {
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, cv.ViewId))
                    {
                        colData.ParseWarnings.Add(
                            $"Phase 5: view '{cv.Label}' cannot be placed on sheet {sheet.SheetNumber}.");
                        continue;
                    }

                    Viewport vp = Viewport.Create(doc, sheet.Id, cv.ViewId, FRONT_ELEV_PT);
                    if (vp == null) continue;

                    if (noTitleVpType == ElementId.InvalidElementId)
                        noTitleVpType = EnsureNoTitleType(doc, vp.GetTypeId());
                    if (noTitleVpType != ElementId.InvalidElementId)
                        try { vp.ChangeTypeId(noTitleVpType); } catch { /* keep default type */ }

                    made.Add((vp, cv));
                }
                catch (Exception ex)
                {
                    colData.ParseWarnings.Add(
                        $"Phase 5: failed to place '{cv.Label}' on {sheet.SheetNumber}: {ex.Message}");
                }
            }

            // Pass 2 — measure each viewport's real footprint and lay them out so nothing
            // overlaps and everything aligns. Regenerate first so the box outlines are valid.
            doc.Regenerate();
            ArrangeViewports(made);
            return made.Count;
        }

        /// <summary>
        /// Bottom-aligns the elevations in a left row and stacks the cross sections in
        /// aligned columns to their right, packing by each viewport's measured size so
        /// nothing overlaps. Sheet coordinates are in feet, origin bottom-left, Y up.
        /// </summary>
        private static void ArrangeViewports(List<(Viewport vp, ColumnView cv)> made)
        {
            double margin = UnitConv.MmToFt(12);
            double gap    = UnitConv.MmToFt(14);
            double top    = UnitConv.MmToFt(285);
            double bottom = margin;
            double left   = margin;

            var elevs = made.Where(m => m.cv.Kind != ViewKind.CrossSection)
                            .OrderBy(m => (int)m.cv.Kind).ToList();
            var secs  = made.Where(m => m.cv.Kind == ViewKind.CrossSection)
                            .OrderBy(m => m.cv.SectionHeightFt).ToList();

            // Elevations: left-to-right, sharing a common baseline (like the reference).
            double x = left;
            foreach (var m in elevs)
            {
                (double w, double h) = SizeOf(m.vp);
                m.vp.SetBoxCenter(new XYZ(x + w / 2.0, bottom + h / 2.0, 0));
                x += w + gap;
            }

            // Cross sections: a tidy aligned grid to the right of the elevations, using
            // uniform cells sized to the largest section so rows and columns line up.
            if (secs.Count > 0)
            {
                double maxW = 0, maxH = 0;
                foreach (var m in secs)
                {
                    (double w, double h) = SizeOf(m.vp);
                    maxW = Math.Max(maxW, w);
                    maxH = Math.Max(maxH, h);
                }

                double cellW = maxW + gap;
                double cellH = maxH + gap;
                int rows = Math.Max(1, (int)Math.Floor((top - bottom) / cellH));
                rows = Math.Min(rows, secs.Count);

                double gridX = x + gap;
                for (int i = 0; i < secs.Count; i++)
                {
                    int col = i / rows;
                    int row = i % rows;
                    double cx = gridX + col * cellW + maxW / 2.0;   // column-major, left→right
                    double cy = top - row * cellH - maxH / 2.0;     // top→down within a column
                    secs[i].vp.SetBoxCenter(new XYZ(cx, cy, 0));
                }
            }
        }

        private static (double w, double h) SizeOf(Viewport vp)
        {
            Outline o = vp.GetBoxOutline();
            return (o.MaximumPoint.X - o.MinimumPoint.X,
                    o.MaximumPoint.Y - o.MinimumPoint.Y);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  TITLEBLOCK
        // ─────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────
        //  VIEWPORT TITLE SUPPRESSION
        // ─────────────────────────────────────────────────────────────────────

        private const string NO_TITLE_TYPE_NAME = "StructAuto - No Title";

        /// <summary>
        /// Given the type of a real viewport, returns a sibling viewport type whose
        /// "Show Title" (VIEWPORT_ATTR_SHOW_LABEL = 0) is off, duplicating it the first
        /// time and re-using it by name afterwards. Deriving from an actual viewport's
        /// type avoids the category-based lookup that can return nothing.
        /// </summary>
        private static ElementId EnsureNoTitleType(Document doc, ElementId sampleTypeId)
        {
            try
            {
                ElementType sample = doc.GetElement(sampleTypeId) as ElementType;
                if (sample == null) return ElementId.InvalidElementId;

                // Re-use the one we made on a previous run (same family = "Viewport").
                ElementType existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElementType))
                    .Cast<ElementType>()
                    .FirstOrDefault(t => t.FamilyName == sample.FamilyName && t.Name == NO_TITLE_TYPE_NAME);
                if (existing != null) return existing.Id;

                ElementType dup = sample.Duplicate(NO_TITLE_TYPE_NAME);
                Parameter show = dup.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
                if (show != null && !show.IsReadOnly) show.Set(0); // 0 = No title
                return dup.Id;
            }
            catch
            {
                return ElementId.InvalidElementId; // fall back to titled viewports
            }
        }

        private static ElementId FindTitleBlock(Document doc)
        {
            FamilySymbol tb = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault();

            if (tb == null) return ElementId.InvalidElementId;
            if (!tb.IsActive)
            {
                try { tb.Activate(); doc.Regenerate(); } catch { }
            }
            return tb.Id;
        }

        private static string SafeMark(PrecastColumnData colData)
        {
            string m = colData.ElementMark;
            if (string.IsNullOrWhiteSpace(m)) return "COL";
            foreach (char c in new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                m = m.Replace(c, '-');
            return m.Trim();
        }
    }
}
