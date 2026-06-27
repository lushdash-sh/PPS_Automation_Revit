// =============================================================================
//  StructAuto Detailing — Phase 3: View Factory
//  File:    Parsers/ViewFactory.cs
//
//  Creates the standard drawing views for a parsed precast column:
//    • Front elevation  ("DETAILS OF PRECAST COLUMN")  — corbel seen in profile
//    • Side  elevation  ("ELEVATION", marker D)        — corbel seen head-on
//    • Cross sections A, B, C…                          — one horizontal cut per
//      distinct tie-zone (+ one per corbel), i.e. only where the reinforcement
//      or geometry changes.
//
//  Every view is produced in TWO variants because a Revit view can be placed on
//  only one sheet:
//    • Formwork      variant — concrete + embeds, rebar hidden.
//    • Reinforcement variant — rebar shown.
//
//  All view creation uses ViewSection.CreateSection(doc, sectionVftId, box),
//  where the box.Transform sets the look orientation and box.Min/Max the crop
//  and clip depth.  Must be called inside an open Revit transaction.
//
//  Revit API version: 2027 (.NET Framework 4.8)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

using StructAutoDetailing.Models;

namespace StructAutoDetailing.Parsers
{
    public enum ViewKind { FrontElevation, SideElevation, CrossSection }
    public enum SheetVariant { Formwork, Reinforcement }

    /// <summary>
    /// One generated Revit view plus the context Phase 4 (dimensioning) and
    /// Phase 5 (sheet layout) need to annotate and place it.
    /// </summary>
    public class ColumnView
    {
        public ElementId ViewId;
        public ViewKind Kind;
        public SheetVariant Variant;
        public string Label;             // "DETAILS OF PRECAST COLUMN", "ELEVATION", "SECTION A"…
        public string SectionTag;        // "A", "B", "C" for cross sections; null otherwise
        public double SectionHeightFt;   // cross sections only: height above column base
        public RebarTieZone Zone;        // the tie-zone this section represents (nullable)
    }

    public static class ViewFactory
    {
        // ── Crop / clip padding around the concrete solid ─────────────────────
        private static readonly double PAD = UnitConv.MmToFt(150.0);

        // Extra crop padding sized to keep the dimension / annotation text INSIDE the
        // viewport box, so each viewport's measured footprint includes its annotations
        // and the sheet layout can space them without overlap.
        private static readonly double ELEV_PAD_H = UnitConv.MmToFt(280.0); // left chain + right tags
        private static readonly double ELEV_PAD_V = UnitConv.MmToFt(250.0); // EL datums above/below
        private static readonly double SEC_PAD    = UnitConv.MmToFt(200.0); // WIDTH/LENGTH dims + tag

        // Drawing scales taken from the reference sheets.
        private const int ELEVATION_SCALE = 20;   // 1 : 20
        private const int SECTION_SCALE   = 25;   // 1 : 25

        /// <summary>
        /// Margin around the concrete solid for the stand-alone elevation views, in feet.
        /// Generous enough that nothing is clipped, tight enough to read cleanly.
        /// </summary>
        private static readonly double ELEV_MARGIN = UnitConv.MmToFt(200.0);

        /// <summary>
        /// Creates the two orthogonal elevation views (Front + Side) for a column and
        /// returns their ids. Standalone — no dimensions, no sheet. Matches the reference
        /// reinforcement elevations: the full rebar cage is shown unobscured (visible
        /// through the concrete), the concrete form and corbel read completely, and the
        /// crop extends up to include the protruding starter bars. EL level datums are
        /// kept; grids and stray section marks are hidden.
        /// Requires colData.Rebar to be populated (Phase 2) to show the cage and to size
        /// the crop for the protruding bars; degrades gracefully if it is null.
        /// </summary>
        public static (ElementId front, ElementId side) CreateElevationPair(
            Document doc, PrecastColumnData colData)
        {
            ElementId vftId = FindSectionViewFamilyType(doc);
            if (vftId == ElementId.InvalidElementId)
                throw new PrecastEngineException(
                    "No 'Section' view family type found in the project. Cannot create elevations.");

            XYZ axX = colData.Orientation.AxisX.ToXYZ().Normalize();
            XYZ axY = colData.Orientation.AxisY.ToXYZ().Normalize();
            XYZ axZ = colData.Orientation.AxisZ.ToXYZ().Normalize();
            XYZ baseC = colData.BaseCentreWorld.ToXYZ();

            double halfW  = colData.ShaftWidthFt / 2.0;
            double halfD  = colData.ShaftDepthFt / 2.0;
            double corbel = colData.MaxCorbellProjectionFt;
            double m      = ELEV_MARGIN;

            // ── Vertical span: FFL (base) → top of the protruding starter bars ──
            // Bottom is clamped to the base (= first-floor level) so nothing below the
            // FFL line shows. Top uses the ACTUAL rebar bounding boxes (not the centroid
            // approximation) so the full protruding bars are captured, never half-cut.
            double topAboveBase = MaxRebarTopAboveBaseFt(doc, colData, baseC);

            double spanTop  = topAboveBase + m;
            double spanBot  = 0.0;                       // FFL — hide everything below
            double centerH  = (spanTop + spanBot) / 2.0;
            double halfHpad = (spanTop - spanBot) / 2.0;
            XYZ origin = baseC + axZ * centerH;

            // The "front" looks at the face the corbel projects from (corbel reads in
            // profile); the "side" is rotated 90° about the column axis.
            bool corbelAlongY = true;
            var firstCorbel = colData.Corbels.FirstOrDefault();
            if (firstCorbel != null)
                corbelAlongY = firstCorbel.Face == ColumnFace.North ||
                               firstCorbel.Face == ColumnFace.South ||
                               firstCorbel.Face == ColumnFace.Unknown;

            XYZ frontRight  = corbelAlongY ? axY : axX;
            XYZ sideRight   = corbelAlongY ? axX : axY;
            double frontHW  = (corbelAlongY ? halfD : halfW) + corbel + m;
            double sideHW   = (corbelAlongY ? halfW : halfD) + corbel + m;
            double frontDep = (corbelAlongY ? halfW : halfD) + corbel + m;
            double sideDep  = (corbelAlongY ? halfD : halfW) + corbel + m;

            string mark = SafeMark(colData);

            // Naming per the reference: the "Back" elevation looks at the corbel face
            // (the one carrying the lifters); the "Right" elevation is rotated 90°.
            // (frontRight = corbel-projection axis; sideRight = the perpendicular axis.)
            ElementId back = MakeElevationView(doc, vftId,
                MakeBox(origin, sideRight, axZ, sideHW, halfHpad, sideDep),
                ELEVATION_SCALE, $"{mark} - Back Elevation");

            ElementId right = MakeElevationView(doc, vftId,
                MakeBox(origin, frontRight, axZ, frontHW, halfHpad, frontDep),
                ELEVATION_SCALE, $"{mark} - Right Elevation");

            // Show the full cage in both views.
            SetRebarUnobscured(doc, colData, back);
            SetRebarUnobscured(doc, colData, right);

            colData.ViewId_FrontElevation = (int)back.Value;   // reuse storage fields
            colData.ViewId_SideElevation  = (int)right.Value;
            return (back, right);
        }

        /// <summary>
        /// Highest point of any analysed rebar, expressed in feet above the column base,
        /// using the real element bounding boxes (so protruding starter bars are fully
        /// captured). Falls back to the concrete height; capped generously for safety.
        /// </summary>
        private static double MaxRebarTopAboveBaseFt(Document doc, PrecastColumnData colData, XYZ baseC)
        {
            double top = colData.TotalHeightFt;
            if (colData.Rebar == null) return top;

            IEnumerable<int> ids = colData.Rebar.LongitudinalBars
                .Concat(colData.Rebar.TransverseBars)
                .Select(b => b.RevitElementId)
                .Distinct();

            foreach (int id in ids)
            {
                try
                {
                    BoundingBoxXYZ bb = doc.GetElement(new ElementId((long)id))?.get_BoundingBox(null);
                    if (bb != null)
                        top = Math.Max(top, bb.Max.Z - baseC.Z); // plumb column: world Z ≈ axis Z
                }
                catch { /* skip */ }
            }

            double cap = colData.TotalHeightFt + UnitConv.MmToFt(5000.0);
            return Math.Min(top, cap);
        }

        /// <summary>
        /// Creates one elevation section view that SHOWS rebar (unlike the formwork
        /// MakeSection which hides it). Grids and stray section marks are hidden; levels
        /// (EL datums) are kept to match the reference elevations.
        /// </summary>
        private static ElementId MakeElevationView(Document doc, ElementId vftId,
            BoundingBoxXYZ box, int scale, string baseName)
        {
            ViewSection vs = ViewSection.CreateSection(doc, vftId, box);
            vs.Scale = scale;
            try { vs.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { vs.CropBoxActive = true; vs.CropBoxVisible = false; } catch { }
            try
            {
                Parameter ac = vs.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (ac != null && !ac.IsReadOnly) ac.Set(1);
            }
            catch { }

            // Hide grids; keep levels (EL datums), rebar, and the Section category
            // (so the A/B/C section markers placed later by Create Sections are visible).
            foreach (BuiltInCategory bic in new[]
                { BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox })
                SetCategoryHiddenSafe(vs, bic, true);

            TrySetUniqueName(vs, baseName);
            return vs.Id;
        }

        /// <summary>Sets every analysed rebar in the column unobscured in the given view so
        /// the cage renders through the concrete (matching the reference elevations).</summary>
        private static void SetRebarUnobscured(Document doc, PrecastColumnData colData, ElementId viewId)
        {
            if (!(doc.GetElement(viewId) is View view) || colData.Rebar == null) return;

            IEnumerable<int> ids = colData.Rebar.LongitudinalBars
                .Concat(colData.Rebar.TransverseBars)
                .Select(b => b.RevitElementId)
                .Distinct();

            foreach (int id in ids)
            {
                try
                {
                    Element el = doc.GetElement(new ElementId((long)id));
                    if (el is Rebar r) r.SetUnobscuredInView(view, true);
                    else if (el is RebarInSystem ris) ris.SetUnobscuredInView(view, true);
                }
                catch { /* bar not showable in this view — skip */ }
            }
        }

        // Thin slab depth for a clean single-level cross section (mm → ft).
        private static readonly double SECTION_SLAB = UnitConv.MmToFt(60.0);
        private static readonly double SECTION_MARGIN = UnitConv.MmToFt(120.0);

        /// <summary>
        /// Creates the cross-section (cut) views where the reinforcement changes — one per
        /// distinct tie-zone (+ corbel) — labelled A, B, C… bottom-to-top, each cut landing
        /// on an actual tie so the section reads cleanly. Each section shows the cage cut.
        /// A reference section marker (A/B/C) is placed on the column's Right elevation at
        /// each cut height, if that elevation exists.
        /// </summary>
        public static List<(ElementId viewId, string tag)> CreateCrossSections(
            Document doc, PrecastColumnData colData, out int markersPlaced, out bool rightElevationFound)
        {
            markersPlaced = 0;
            var result = new List<(ElementId, string)>();

            ElementId vftId = FindSectionViewFamilyType(doc);
            if (vftId == ElementId.InvalidElementId)
                throw new PrecastEngineException(
                    "No 'Section' view family type found in the project. Cannot create sections.");

            string mark = SafeMark(colData);
            ViewSection rightElev = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .FirstOrDefault(v => v.Name == $"{mark} - Right Elevation");
            rightElevationFound = rightElev != null;

            XYZ axX = colData.Orientation.AxisX.ToXYZ().Normalize();
            XYZ axY = colData.Orientation.AxisY.ToXYZ().Normalize();
            XYZ axZ = colData.Orientation.AxisZ.ToXYZ().Normalize();
            XYZ baseC = colData.BaseCentreWorld.ToXYZ();

            double halfW  = colData.ShaftWidthFt / 2.0;
            double halfD  = colData.ShaftDepthFt / 2.0;
            double corbel = colData.MaxCorbellProjectionFt;
            double hx = halfW + corbel + SECTION_MARGIN;   // along local X (length)
            double hy = halfD + corbel + SECTION_MARGIN;   // along local Y (width)

            // Markers (Section category + our text labels) belong on the RIGHT elevation
            // only. Show the Section category there and hide it on the Back elevation.
            ViewSection backElev = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSection)).Cast<ViewSection>()
                .FirstOrDefault(v => v.Name == $"{mark} - Back Elevation");
            if (rightElev != null) SetCategoryHiddenSafe(rightElev, BuiltInCategory.OST_Sections, false);
            if (backElev != null)  SetCategoryHiddenSafe(backElev,  BuiltInCategory.OST_Sections, true);
            ElementId textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

            // Diagnostics so the cut selection can be verified.
            int hostedRebar = ColumnHostedRebarIds(doc, colData,
                new ElementId((long)colData.RevitElementId)).Count;
            int totalRebar = (colData.Rebar?.LongitudinalBars.Count ?? 0) +
                             (colData.Rebar?.TransverseBars.Count ?? 0);
            colData.ParseWarnings.Add(
                $"DIAG sections: ties={colData.Rebar?.TransverseBars.Count ?? 0} " +
                $"sleeves={colData.CorrugatedSleeves.Count} corbels={colData.Corbels.Count} " +
                $"rebar hosted/total={hostedRebar}/{totalRebar}");

            // Exactly the three meaningful cuts: sleeve zone (bottom), typical (mid),
            // corbel (top). Snap to a nearby tie only.
            List<double> cutHeights = ChooseThreeCutHeights(colData);
            int i = 0;
            foreach (double rawH in cutHeights)
            {
                string tag = ((char)('A' + i)).ToString();
                double cutH = LandOnNearestTie(colData, rawH);
                colData.ParseWarnings.Add(
                    $"DIAG cut {tag}: raw={rawH * 304.8:F0}mm landed={cutH * 304.8:F0}mm above base");
                XYZ origin = baseC + axZ * cutH;

                ViewSection sv = ViewSection.CreateSection(doc, vftId,
                    MakeBox(origin, axX, axY, hx, hy, SECTION_SLAB));
                sv.Scale = SECTION_SCALE;
                try { sv.DetailLevel = ViewDetailLevel.Fine; } catch { }
                try { sv.CropBoxActive = true; sv.CropBoxVisible = false; } catch { }
                try
                {
                    Parameter ac = sv.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (ac != null && !ac.IsReadOnly) ac.Set(1);
                }
                catch { }
                foreach (BuiltInCategory bic in new[]
                    { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids,
                      BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Sections })
                    SetCategoryHiddenSafe(sv, bic, true);

                TrySetUniqueName(sv, $"{mark} - Section {tag}");
                try
                {
                    Parameter dn = sv.get_Parameter(BuiltInParameter.VIEWER_DETAIL_NUMBER);
                    if (dn != null && !dn.IsReadOnly) dn.Set(tag);
                }
                catch { }

                // NOTE: do NOT set rebar unobscured here — a cross section must show only
                // the bars cut at this level, not the whole cage projected through.
                // Isolate this column (+ its rebar/embeds) so neighbouring elements are hidden.
                IsolateColumnInView(doc, colData, sv.Id);
                result.Add((sv.Id, tag));

                // Place the A/B/C marker + a reliable text label on the Right elevation.
                if (rightElev != null)
                {
                    XYZ er = rightElev.RightDirection.Normalize();
                    XYZ c = baseC + axZ * cutH;
                    double span = hx;
                    try
                    {
                        ViewSection.CreateReferenceSection(doc, rightElev.Id, sv.Id,
                            c + er * span, c - er * span);
                        markersPlaced++;
                    }
                    catch (Exception ex)
                    {
                        colData.ParseWarnings.Add($"Section {tag}: marker placement failed ({ex.Message}).");
                    }
                    // Explicit A/B/C label (the off-sheet section bubble can be blank).
                    if (textTypeId != ElementId.InvalidElementId)
                    {
                        try { TextNote.Create(doc, rightElev.Id, c + er * (span + UnitConv.MmToFt(120)), tag, textTypeId); }
                        catch { }
                    }
                }

                i++;
            }

            return result;
        }

        /// <summary>
        /// The three meaningful cut heights (feet above base): the sleeve zone (or lower
        /// third), a typical mid-height, and the corbel (or upper third). De-duplicated
        /// if any two fall within 200 mm. Returns ascending heights.
        /// </summary>
        private static List<double> ChooseThreeCutHeights(PrecastColumnData colData)
        {
            var ties = colData.Rebar?.TransverseBars;
            if (ties != null && ties.Count >= 3)
            {
                var sorted = ties.OrderBy(t => t.TieZPositionFt).ToList();
                double minTie = sorted.First().TieZPositionFt;       // bottom of cage
                double maxTie = sorted.Last().TieZPositionFt;        // top of cage (corbel zone)
                double minZ   = colData.LocalBBox?.MinZ ?? 0.0;

                // Bottom — the corrugated-sleeve zone if known, else low in the cage.
                double bottom = colData.CorrugatedSleeves.Count > 0
                    ? colData.CorrugatedSleeves.Average(e => e.CentroidLocal.Z - minZ)
                    : minTie + (maxTie - minTie) * 0.10;

                // Top — near the very top of the cage (the corbel / top-confinement band).
                double top = maxTie - UnitConv.MmToFt(250.0);
                if (top <= bottom) top = maxTie;

                double mid = (bottom + top) / 2.0;

                // Land each on a real tie.
                return new[] { bottom, mid, top }
                    .Select(h => sorted.OrderBy(t => Math.Abs(t.TieZPositionFt - h)).First().TieZPositionFt)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();
            }

            // Fallback — no usable ties: simple column fractions.
            double H = colData.TotalHeightFt;
            return new List<double> { H * 0.12, H * 0.50, H * 0.88 };
        }

        /// <summary>
        /// Permanently isolates the column, its rebar and its embeds (sleeves/lifters/
        /// dowels) in the given view, hiding every other element — so a cross section
        /// shows ONLY this column's cut, not neighbouring columns/beams.
        /// </summary>
        private static void IsolateColumnInView(Document doc, PrecastColumnData colData, ElementId viewId)
        {
            if (!(doc.GetElement(viewId) is View view)) return;

            ElementId colId = new ElementId((long)colData.RevitElementId);
            var keep = new List<ElementId> { colId };
            foreach (EmbedData e in colData.AllEmbeds)
                keep.Add(new ElementId((long)e.RevitElementId));
            keep.AddRange(ColumnHostedRebarIds(doc, colData, colId));

            try
            {
                view.IsolateElementsTemporary(keep);
                view.ConvertTemporaryHideIsolateToPermanent();
            }
            catch { /* isolation not supported in this view — leave as is */ }
        }

        /// <summary>
        /// Rebar ids that are actually HOSTED BY this column — the analysed set is
        /// collected by bounding box and can include adjacent beam/slab bars, which
        /// otherwise sprawl across the section. Falls back to the full set if none of
        /// the bars report this column as host (e.g. free-placed rebar).
        /// </summary>
        private static List<ElementId> ColumnHostedRebarIds(Document doc, PrecastColumnData colData, ElementId colId)
        {
            var all = new List<ElementId>();
            var hosted = new List<ElementId>();
            if (colData.Rebar == null) return hosted;

            foreach (RebarCageBar b in colData.Rebar.LongitudinalBars.Concat(colData.Rebar.TransverseBars))
            {
                Element el = doc.GetElement(new ElementId((long)b.RevitElementId));
                if (el == null) continue;
                all.Add(el.Id);
                ElementId host = (el as Rebar)?.GetHostId() ?? (el as RebarInSystem)?.GetHostId();
                if (host != null && host == colId) hosted.Add(el.Id);
            }
            return hosted.Count > 0 ? hosted : all;
        }

        /// <summary>Snaps a target height to the nearest tie so the cut lands on a real
        /// tie level (cleaner section). Returns the target unchanged if no ties exist.</summary>
        private static double LandOnNearestTie(PrecastColumnData colData, double heightFt)
        {
            var ties = colData.Rebar?.TransverseBars;
            if (ties == null || ties.Count == 0) return heightFt;
            var nearest = ties.OrderBy(t => Math.Abs(t.TieZPositionFt - heightFt)).First();
            // Only snap if a tie is genuinely close, so the three cuts stay well separated.
            return Math.Abs(nearest.TieZPositionFt - heightFt) <= UnitConv.MmToFt(150.0)
                ? nearest.TieZPositionFt : heightFt;
        }

        /// <summary>
        /// Creates all formwork and reinforcement views for the column.
        /// Returns the flat list of created views (both variants).
        /// </summary>
        public static List<ColumnView> CreateViews(Document doc, PrecastColumnData colData)
        {
            var result = new List<ColumnView>();

            ElementId sectionVftId = FindSectionViewFamilyType(doc);
            if (sectionVftId == ElementId.InvalidElementId)
                throw new PrecastEngineException(
                    "No 'Section' view family type found in the project. " +
                    "Cannot create elevation or section views.");

            // ── Local axis system (world vectors) ─────────────────────────────
            XYZ axX = colData.Orientation.AxisX.ToXYZ().Normalize();
            XYZ axY = colData.Orientation.AxisY.ToXYZ().Normalize();
            XYZ axZ = colData.Orientation.AxisZ.ToXYZ().Normalize();

            XYZ baseC = colData.BaseCentreWorld.ToXYZ();
            XYZ topC  = colData.TopCentreWorld.ToXYZ();
            XYZ midC  = (baseC + topC) * 0.5;

            double halfW   = colData.ShaftWidthFt / 2.0;
            double halfD   = colData.ShaftDepthFt / 2.0;
            double halfH   = colData.TotalHeightFt / 2.0;
            double corbel  = colData.MaxCorbellProjectionFt;

            // Which horizontal axis the corbel projects along decides the "front".
            // North/South corbels project along local Y; East/West along local X.
            bool corbelAlongY = true;
            var firstCorbel = colData.Corbels.FirstOrDefault();
            if (firstCorbel != null)
                corbelAlongY = firstCorbel.Face == ColumnFace.North ||
                               firstCorbel.Face == ColumnFace.South ||
                               firstCorbel.Face == ColumnFace.Unknown;

            // Front: right axis = corbel-projection axis (so the corbel reads in
            // profile); Side: the perpendicular horizontal axis.
            XYZ frontRight = corbelAlongY ? axY : axX;
            XYZ sideRight  = corbelAlongY ? axX : axY;
            double frontHalfW = (corbelAlongY ? halfD : halfW) + corbel;
            double sideHalfW  = (corbelAlongY ? halfW : halfD) + corbel;
            // Look-through depth must clear shaft + corbels on the perpendicular axis.
            double frontDepth = (corbelAlongY ? halfW : halfD) + corbel + PAD;
            double sideDepth  = (corbelAlongY ? halfD : halfW) + corbel + PAD;

            string mark = SafeMark(colData);

            foreach (SheetVariant variant in new[] { SheetVariant.Formwork, SheetVariant.Reinforcement })
            {
                // Full words + " - " separators so a search for the sheet number
                // "{mark}-F" / "{mark}-R" does NOT also match the view names.
                string vp = variant == SheetVariant.Formwork ? "Formwork" : "Reinforcement";

                // ── Front elevation ───────────────────────────────────────────
                BoundingBoxXYZ frontBox = MakeBox(midC, frontRight, axZ,
                    frontHalfW + ELEV_PAD_H, halfH + ELEV_PAD_V, frontDepth);
                var front = MakeSection(doc, sectionVftId, frontBox, variant, ELEVATION_SCALE,
                    $"{mark} - {vp} - Front Elevation");
                result.Add(new ColumnView
                {
                    ViewId = front,
                    Kind = ViewKind.FrontElevation,
                    Variant = variant,
                    Label = variant == SheetVariant.Formwork
                        ? "DETAILS OF PRECAST COLUMN"
                        : "REINFORCEMENT DETAILS OF PRECAST COLUMN"
                });

                // ── Side elevation ────────────────────────────────────────────
                BoundingBoxXYZ sideBox = MakeBox(midC, sideRight, axZ,
                    sideHalfW + ELEV_PAD_H, halfH + ELEV_PAD_V, sideDepth);
                var side = MakeSection(doc, sectionVftId, sideBox, variant, ELEVATION_SCALE,
                    $"{mark} - {vp} - Side Elevation");
                result.Add(new ColumnView
                {
                    ViewId = side,
                    Kind = ViewKind.SideElevation,
                    Variant = variant,
                    Label = "ELEVATION"
                });

                // ── Cross sections ────────────────────────────────────────────
                List<(double heightFt, RebarTieZone zone)> cuts = ChooseSectionHeights(colData);
                double secHalf = Math.Max(halfW, halfD) + corbel + SEC_PAD;
                int tag = 0;
                foreach (var cut in cuts)
                {
                    string tagLetter = ((char)('A' + tag)).ToString();
                    XYZ origin = baseC + axZ * cut.heightFt;
                    // Horizontal cut: right = local X, up = local Y, look straight down.
                    BoundingBoxXYZ secBox = MakeBox(origin, axX, axY, secHalf, secHalf,
                        Math.Max(halfW, halfD) + PAD);
                    var secView = MakeSection(doc, sectionVftId, secBox, variant, SECTION_SCALE,
                        $"{mark} - {vp} - Section {tagLetter}");
                    result.Add(new ColumnView
                    {
                        ViewId = secView,
                        Kind = ViewKind.CrossSection,
                        Variant = variant,
                        Label = $"SECTION {tagLetter}",
                        SectionTag = tagLetter,
                        SectionHeightFt = cut.heightFt,
                        Zone = cut.zone
                    });
                    tag++;
                }
            }

            // Record a representative set on the model (formwork variant) for callers
            // that consult colData directly.
            var fw = result.Where(v => v.Variant == SheetVariant.Formwork).ToList();
            colData.ViewId_FrontElevation = (int)(fw.FirstOrDefault(v => v.Kind == ViewKind.FrontElevation)?.ViewId.Value ?? -1);
            colData.ViewId_SideElevation  = (int)(fw.FirstOrDefault(v => v.Kind == ViewKind.SideElevation)?.ViewId.Value ?? -1);

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SECTION-HEIGHT SELECTION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Picks the heights (above the column base) at which to cut cross sections:
        /// the mid-height of each tie-zone (where reinforcement spacing changes) plus
        /// the mid-height of each corbel. De-duplicated when two cuts fall within
        /// 100 mm of each other. If no rebar/zones exist, a single mid-column cut.
        /// </summary>
        private static List<(double heightFt, RebarTieZone zone)> ChooseSectionHeights(PrecastColumnData colData)
        {
            var picks = new List<(double heightFt, RebarTieZone zone)>();
            double dedup = UnitConv.MmToFt(100.0);

            if (colData.Rebar?.TieZones != null && colData.Rebar.TieZones.Count > 0)
            {
                foreach (var z in colData.Rebar.TieZones.OrderBy(z => z.StartZFt))
                    picks.Add(((z.StartZFt + z.EndZFt) / 2.0, z));
            }

            foreach (var c in colData.Corbels)
            {
                double h = (c.BaseHeightFt + c.TopHeightFt) / 2.0;
                if (!picks.Any(p => Math.Abs(p.heightFt - h) < dedup))
                    picks.Add((h, null));
            }

            if (picks.Count == 0)
                picks.Add((colData.TotalHeightFt / 2.0, null));

            picks = picks.OrderBy(p => p.heightFt).ToList();

            // De-duplicate sections that would look identical: same tie diameter + snapped
            // spacing AND the same embed context (sleeves/lifters/dowels nearby). Two zones
            // at the same pitch but with different embeds (e.g. sleeve zone vs corbel zone)
            // are kept separate; pure noise duplicates collapse to one. Keeps the lowest.
            var unique = new List<(double heightFt, RebarTieZone zone)>();
            var seen = new HashSet<string>();
            foreach (var p in picks)
            {
                if (!seen.Add(SectionSignature(colData, p.heightFt, p.zone)))
                    continue;
                unique.Add(p);
            }
            picks = unique;

            // Hard safety cap: never emit more than 6 cross sections, even if the tie-zone
            // clustering misbehaves. Keep an evenly spread subset.
            const int MAX_SECTIONS = 4;
            if (picks.Count > MAX_SECTIONS)
            {
                var trimmed = new List<(double heightFt, RebarTieZone zone)>();
                double stride = (picks.Count - 1) / (double)(MAX_SECTIONS - 1);
                for (int i = 0; i < MAX_SECTIONS; i++)
                    trimmed.Add(picks[(int)Math.Round(i * stride)]);
                picks = trimmed.Distinct().ToList();
            }

            return picks;
        }

        /// <summary>
        /// A signature that two cross sections share only when they would draw identically:
        /// tie diameter, snapped spacing, and whether an embed sits within ±200 mm.
        /// Corbel cuts (no zone) always get a unique signature so they are never merged away.
        /// </summary>
        private static string SectionSignature(PrecastColumnData colData, double heightFt, RebarTieZone zone)
        {
            if (zone == null) return "corbel@" + Math.Round(heightFt * 304.8);

            double tol = UnitConv.MmToFt(200.0);
            double minZ = colData.LocalBBox?.MinZ ?? 0.0;
            bool embedNear = colData.AllEmbeds.Any(e =>
                Math.Abs((e.CentroidLocal.Z - minZ) - heightFt) < tol);

            int spacingBucket = (int)Math.Round(zone.SpacingMm / 25.0);
            return $"d{zone.TieDiameterMm}-s{spacingBucket}-e{(embedNear ? 1 : 0)}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SECTION-BOX + VIEW CREATION HELPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a section BoundingBoxXYZ. The view looks along -BasisZ, where
        /// BasisZ = right × up (out of screen). Min/Max are symmetric in depth so
        /// the centred solid always sits between the near and far clip planes.
        /// </summary>
        private static BoundingBoxXYZ MakeBox(XYZ origin, XYZ right, XYZ up,
            double halfW, double halfH, double halfDepth)
        {
            XYZ x = right.Normalize();
            XYZ y = up.Normalize();
            XYZ z = x.CrossProduct(y).Normalize();

            Transform t = Transform.Identity;
            t.Origin = origin;
            t.BasisX = x;
            t.BasisY = y;
            t.BasisZ = z;

            return new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-halfW, -halfH, -halfDepth),
                Max = new XYZ( halfW,  halfH,  halfDepth)
            };
        }

        private static ElementId MakeSection(Document doc, ElementId vftId,
            BoundingBoxXYZ box, SheetVariant variant, int scale, string baseName)
        {
            ViewSection vs = ViewSection.CreateSection(doc, vftId, box);
            vs.Scale = scale;

            try { vs.DetailLevel = ViewDetailLevel.Fine; } catch { /* template-locked */ }
            try { vs.CropBoxActive = true; vs.CropBoxVisible = false; } catch { }

            // Enable the ANNOTATION crop too. Without it, section-marker bubbles and
            // dimension text that extend beyond the model crop still render and get
            // counted in the viewport's box outline — inflating it far past the column
            // (front elevation measured 580 mm tall on a 297 mm sheet). With it on, the
            // viewport box is bounded to the crop region.
            try
            {
                Parameter ac = vs.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (ac != null && !ac.IsReadOnly) ac.Set(1);
            }
            catch { }

            // Rebar visibility per variant.
            SetCategoryHiddenSafe(vs, BuiltInCategory.OST_Rebar,
                hidden: variant == SheetVariant.Formwork);

            // Hide datum / marker categories that sprawl across the whole view and
            // inflate the viewport box: level lines (FFL / floor-level lines run the full
            // width), grids, and section-marker bubbles. EL datums are re-drawn as our
            // own text notes, so nothing useful is lost.
            foreach (BuiltInCategory bic in new[]
                { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_SectionBox,
                  BuiltInCategory.OST_Sections })
                SetCategoryHiddenSafe(vs, bic, true);

            // Unique, human-readable view name.
            TrySetUniqueName(vs, baseName);

            return vs.Id;
        }

        private static void SetCategoryHiddenSafe(View view, BuiltInCategory bic, bool hidden)
        {
            try
            {
                var catId = new ElementId(bic);
                if (view.CanCategoryBeHidden(catId))
                    view.SetCategoryHidden(catId, hidden);
            }
            catch { /* category not present in this view — ignore */ }
        }

        private static void TrySetUniqueName(View view, string baseName)
        {
            for (int i = 0; i < 50; i++)
            {
                string candidate = i == 0 ? baseName : $"{baseName} ({i})";
                try { view.Name = candidate; return; }
                catch { /* name in use — try next suffix */ }
            }
        }

        private static ElementId FindSectionViewFamilyType(Document doc)
        {
            ViewFamilyType vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
            return vft?.Id ?? ElementId.InvalidElementId;
        }

        private static string SafeMark(PrecastColumnData colData)
        {
            string m = colData.ElementMark;
            if (string.IsNullOrWhiteSpace(m)) return "COL";
            // Strip characters Revit dislikes in view names.
            foreach (char c in new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                m = m.Replace(c, '-');
            return m.Trim();
        }
    }
}
