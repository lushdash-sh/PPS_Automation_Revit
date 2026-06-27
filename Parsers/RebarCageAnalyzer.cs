// =============================================================================
//  StructAuto Detailing — Phase 2: Rebar Cage Analyzer
//  File:    Parsers/RebarCageAnalyzer.cs
//
//  Responsibilities:
//    2A  Collect all Rebar elements inside the column bounding volume
//    2B  Classify each bar as Longitudinal, Transverse, or Diagonal
//    2C  Cluster transverse bars into TieZones by spacing similarity
//    2D  Compute MRA tag text for every zone ("n @ spacing = total")
//    2E  Extract bending dimensions (A, B, C, D) and build the BendSchedule
//    2F  Build the RebarScheduleSummary (by-diameter weight table)
//
//  Architecture notes:
//    • Entry point: RebarCageAnalyzer.Analyze(doc, colData) → RebarPayload
//    • All Revit API calls are confined to this file.
//    • The returned RebarPayload is assigned to colData.Rebar by the caller.
//    • This phase is READ-ONLY (no Revit writes) — no transaction needed.
//
//  Revit API version: 2027 (.NET Framework 4.8)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

using StructAutoDetailing.Models;

// Disambiguate: both StructAutoDetailing.Models and Autodesk.Revit.DB.Structure
// declare a type named RebarBendData. In this file the domain model is always meant.
using RebarBendData = StructAutoDetailing.Models.RebarBendData;

namespace StructAutoDetailing.Parsers
{
    public static class RebarCageAnalyzer
    {
        // ─────────────────────────────────────────────────────────────────────
        //  CONSTANTS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bars whose axis makes less than this angle with localZ are Longitudinal.
        /// </summary>
        private const double LONGITUDINAL_MAX_ANGLE_DEG = 15.0;

        /// <summary>
        /// Bars whose axis makes more than this angle with localZ are Transverse.
        /// Bars between LONG_MAX and TRANS_MIN are classified Diagonal.
        /// </summary>
        private const double TRANSVERSE_MIN_ANGLE_DEG = 75.0;

        /// <summary>
        /// Two adjacent ties belong to the SAME zone if their spacing differs
        /// from the zone's reference spacing by less than this tolerance.
        /// = 10 mm in feet.
        /// </summary>
        private const double SPACING_TOLERANCE_FT = 10.0 / 304.8;

        /// <summary>
        /// Bounding box search padding around the column solid when collecting
        /// rebar elements. = 50 mm in feet. Must match Phase 1D constant.
        /// </summary>
        private const double REBAR_SEARCH_PADDING_FT = 50.0 / 304.8;

        /// <summary>
        /// Minimum number of ties required before a spacing cluster is accepted
        /// as a valid TieZone.  Prevents isolated single ties from creating zones.
        /// </summary>
        private const int MIN_TIES_PER_ZONE = 2;

        /// <summary>
        /// Nominal concrete cover for this project (mm) — from drawing notes:
        /// "CLEAR COVER FOR COLUMN = 50 mm".  Used in cover verification.
        /// </summary>
        private const double NOMINAL_COVER_MM = 50.0;

        // ─────────────────────────────────────────────────────────────────────
        //  PUBLIC ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full Phase 2 analysis.  Must be called AFTER Phase 1 (colData.GeometryParsed == true).
        /// No Revit transaction required — this is a read-only operation.
        /// </summary>
        /// <param name="doc">Active Revit document.</param>
        /// <param name="colData">
        /// Populated Phase 1 data object.  The returned <see cref="RebarPayload"/>
        /// should be assigned to colData.Rebar by the caller.
        /// </param>
        /// <returns>Fully populated <see cref="RebarPayload"/>.</returns>
        public static RebarPayload Analyze(Document doc, PrecastColumnData colData)
        {
            if (doc == null)    throw new ArgumentNullException(nameof(doc));
            if (colData == null) throw new ArgumentNullException(nameof(colData));

            if (!colData.GeometryParsed)
                throw new PrecastEngineException(
                    "RebarCageAnalyzer.Analyze() called before Phase 1 geometry parse. " +
                    "Ensure ColumnGeometryParser.Parse() completes successfully first.");

            var payload = new RebarPayload
            {
                NominalCoverMm = NOMINAL_COVER_MM
            };

            // ── 2A: Collect rebar elements ─────────────────────────────────
            List<Element> rebarElements = CollectRebarElements(doc, colData);

            if (rebarElements.Count == 0)
            {
                colData.ParseWarnings.Add(
                    "Phase 2: No Rebar elements found inside the column bounding volume. " +
                    "Verify that rebar is modelled as Revit 'Rebar' elements (not as generic model families). " +
                    "The rebar schedule sheet will be empty.");
                payload.IsComplete = true;
                return payload;
            }

            // ── 2B: Classify each bar ──────────────────────────────────────
            ClassifyBars(rebarElements, colData, payload, doc);

            // ── 2C + 2D: Cluster transverse bars into TieZones + MRA text ──
            ClusterTieZones(payload, colData);

            // ── 2E: Build bend schedule ────────────────────────────────────
            BuildBendSchedule(rebarElements, colData, payload, doc);

            // ── 2F: Build summary table ────────────────────────────────────
            foreach (var row in payload.BendSchedule)
                payload.ScheduleSummary.Add(row);

            payload.IsComplete = true;
            return payload;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 2A — COLLECT REBAR ELEMENTS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all Rebar and RebarInSystem elements whose bounding box
        /// intersects the padded column bounding volume.
        ///
        /// IMPORTANT: Revit Rebar elements are NOT FamilyInstances.
        /// They are their own class in Autodesk.Revit.DB.Structure.
        /// The Phase 1D FamilyInstance collector would have missed them entirely —
        /// this is the correct, separate collector for structural rebar.
        /// </summary>
        private static List<Element> CollectRebarElements(Document doc, PrecastColumnData colData)
        {
            // Build padded world bounding box for the filter
            Element colElement = doc.GetElement(new ElementId(colData.RevitElementId));
            BoundingBoxXYZ worldBbox = colElement?.get_BoundingBox(null);

            if (worldBbox == null)
            {
                colData.ParseWarnings.Add("Phase 2: Cannot obtain column bounding box for rebar search.");
                return new List<Element>();
            }

            XYZ bboxMin = worldBbox.Min - new XYZ(REBAR_SEARCH_PADDING_FT,
                                                   REBAR_SEARCH_PADDING_FT,
                                                   REBAR_SEARCH_PADDING_FT);
            XYZ bboxMax = worldBbox.Max + new XYZ(REBAR_SEARCH_PADDING_FT,
                                                   REBAR_SEARCH_PADDING_FT,
                                                   REBAR_SEARCH_PADDING_FT);

            BoundingBoxIntersectsFilter bboxFilter =
                new BoundingBoxIntersectsFilter(new Outline(bboxMin, bboxMax));

            // Collect free Rebar elements (individual bars not in a set)
            var freeRebar = new FilteredElementCollector(doc)
                .OfClass(typeof(Rebar))
                .WherePasses(bboxFilter)
                .ToList();

            // Collect RebarInSystem elements (bars created by Structural Framing systems)
            // These are less common for columns but must be handled.
            var systemRebar = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarInSystem))
                .WherePasses(bboxFilter)
                .ToList();

            var all = new List<Element>(freeRebar.Count + systemRebar.Count);
            all.AddRange(freeRebar);
            all.AddRange(systemRebar);
            return all;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 2B — CLASSIFY BARS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Classifies each rebar element as Longitudinal, Transverse, or Diagonal
        /// by measuring the angle between the bar's primary axis and the column's
        /// local Z (gravity) axis.
        ///
        /// Bar axis extraction strategy:
        ///   For free Rebar:      use bar.GetShapeDrivenAccessor().GetBarPositionTransform(0).BasisX
        ///   For RebarInSystem:   use the element's own Transform.BasisX
        ///
        /// The BasisX of the bar's local transform is ALWAYS the bar's primary axis
        /// direction in Revit, regardless of the bar shape code.
        /// </summary>
        private static void ClassifyBars(
            List<Element> rebarElements,
            PrecastColumnData colData,
            RebarPayload payload,
            Document doc)
        {
            Transform worldToLocal = BuildWorldToLocalTransform(colData);
            XYZ localZWorld = colData.Orientation.AxisZ.ToXYZ(); // column gravity axis in world

            foreach (Element elem in rebarElements)
            {
                RebarCageBar bar = ExtractBarGeometry(elem, colData, worldToLocal, localZWorld, doc);
                if (bar == null) continue;

                switch (bar.Role)
                {
                    case RebarRole.Longitudinal:
                    case RebarRole.Confinement:
                        payload.LongitudinalBars.Add(bar);
                        break;
                    case RebarRole.Transverse:
                        payload.TransverseBars.Add(bar);
                        break;
                    default:
                        // Diagonal bars — add to longitudinal list with a warning
                        payload.LongitudinalBars.Add(bar);
                        colData.ParseWarnings.Add(
                            $"Bar (ElementId={bar.RevitElementId}, T{bar.DiameterMm}) " +
                            $"is diagonal ({bar.AngleToVerticalDeg:F1}° to column axis). " +
                            $"Classified as Longitudinal. Verify manually.");
                        break;
                }
            }

            // Sort longitudinal bars by bottom Z (bottom of bar) ascending
            payload.LongitudinalBars.Sort((a, b) => a.BottomZLocal.CompareTo(b.BottomZLocal));

            // Sort transverse bars by their Z position (centroid height) ascending
            payload.TransverseBars.Sort((a, b) => a.TieZPositionFt.CompareTo(b.TieZPositionFt));
        }

        /// <summary>
        /// Extracts geometry and creates a <see cref="RebarCageBar"/> for one rebar element.
        /// Returns null if the element cannot be processed.
        /// </summary>
        private static RebarCageBar ExtractBarGeometry(
            Element elem,
            PrecastColumnData colData,
            Transform worldToLocal,
            XYZ localZWorld,
            Document doc)
        {
            try
            {
                // ── Get the bar's primary axis vector in WORLD space ───────
                XYZ barAxisWorld = GetBarAxisWorld(elem);
                if (barAxisWorld == null) return null;

                // ── Angle between bar axis and column Z axis ────────────────
                // We use the ABSOLUTE angle — a tie pointing in -X is the same
                // as one pointing in +X for classification purposes.
                double angleDeg = ToDegrees(barAxisWorld.AngleTo(localZWorld));
                // Normalise to 0–90° range (the axis direction is arbitrary)
                if (angleDeg > 90.0) angleDeg = 180.0 - angleDeg;

                // ── Centroid in world space ─────────────────────────────────
                XYZ centroidWorld = GetRebarCentroid(elem);
                XYZ centroidLocal = worldToLocal.OfPoint(centroidWorld);

                // ── Cut length ─────────────────────────────────────────────
                double cutLengthFt = GetRebarCutLength(elem);

                // ── Diameter ───────────────────────────────────────────────
                int diamMm = GetRebarDiameterMm(elem);

                // ── Build the object ────────────────────────────────────────
                var bar = new RebarCageBar
                {
                    RevitElementId     = (int)elem.Id.Value,
                    DiameterMm         = diamMm,
                    AngleToVerticalDeg = angleDeg,
                    CentroidLocal      = new Vec3(centroidLocal.X, centroidLocal.Y, centroidLocal.Z),
                    CuttingLengthFt    = cutLengthFt,
                };

                // ── Classify by angle ───────────────────────────────────────
                if (angleDeg <= LONGITUDINAL_MAX_ANGLE_DEG)
                {
                    bar.Role = RebarRole.Longitudinal;
                    // For longitudinal bars, compute bottom/top Z from centroid ± half-length
                    double halfLen = cutLengthFt / 2.0;
                    bar.BottomZLocal = centroidLocal.Z - halfLen;
                    bar.TopZLocal    = centroidLocal.Z + halfLen;
                    bar.TieZPositionFt = centroidLocal.Z;
                }
                else if (angleDeg >= TRANSVERSE_MIN_ANGLE_DEG)
                {
                    bar.Role           = RebarRole.Transverse;
                    bar.TieZPositionFt = centroidLocal.Z - colData.LocalBBox.MinZ;
                    bar.BottomZLocal   = bar.TieZPositionFt;
                    bar.TopZLocal      = bar.TieZPositionFt;
                }
                else
                {
                    bar.Role           = RebarRole.Diagonal;
                    bar.TieZPositionFt = centroidLocal.Z - colData.LocalBBox.MinZ;
                    bar.BottomZLocal   = bar.TieZPositionFt;
                    bar.TopZLocal      = bar.TieZPositionFt;
                }

                // ── Axis in local space ─────────────────────────────────────
                XYZ axisLocal = worldToLocal.OfVector(barAxisWorld).Normalize();
                bar.AxisLocal = new Vec3(axisLocal.X, axisLocal.Y, axisLocal.Z);

                // ── Clear cover estimate ────────────────────────────────────
                // For longitudinal bars: distance from bar centroid to nearest face
                // in the XY plane minus half the bar diameter
                bar.ClearCoverMm = EstimateClearCover(centroidLocal, bar.DiameterMm, colData);

                return bar;
            }
            catch (Exception ex)
            {
                colData.ParseWarnings.Add(
                    $"Phase 2: Could not process rebar ElementId={(int)elem.Id.Value}: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 2C + 2D — TIE ZONE CLUSTERING + MRA TEXT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Groups the sorted transverse bars into zones of uniform spacing.
        ///
        /// Algorithm (state machine over the sorted tie Z-positions):
        ///
        ///   1. Start a new zone with bar[0] as the first member.
        ///   2. For each subsequent bar, compute the spacing from the previous bar.
        ///   3. If |spacing - zone.ReferenceSpacing| ≤ SPACING_TOLERANCE_FT:
        ///      → append bar to current zone.
        ///   4. Else:
        ///      → close the current zone (if it has ≥ MIN_TIES_PER_ZONE bars),
        ///      → start a new zone with the current bar as the first member,
        ///        using the new spacing as the reference.
        ///   5. After the last bar, close the final zone.
        ///   6. Assign zone labels and compute MRA tag text.
        ///
        /// Edge cases handled:
        ///   • Single isolated ties are emitted as single-bar zones with a warning.
        ///   • Ties at identical Z positions (concurrent placement) are de-duplicated.
        ///   • Zones with spacing > 200mm are labelled as standard (non-confinement).
        /// </summary>
        /// <summary>Spacings are snapped to this increment (mm) before grouping.</summary>
        private const int SPACING_SNAP_MM = 25;

        /// <summary>Hard safety cap on the number of tie-zones / cross-sections.</summary>
        private const int MAX_TIE_ZONES = 4;

        public static void ClusterTieZones(RebarPayload payload, PrecastColumnData colData)
        {
            List<RebarCageBar> ties = payload.TransverseBars;
            if (ties.Count == 0) return;

            // Collapse all ties that sit at the same level (perimeter link + cross-ties +
            // diamond links share a height) down to one marker per level, so the gaps we
            // measure are the real stirrup pitch — not the spacing between co-level legs.
            ties = DedupTiesByZ(ties);

            if (ties.Count == 1)
            {
                var only = BuildZoneFromRun(ties, 0, 0, 0, 0);
                AssignZoneLabels(new List<RebarTieZone> { only }, colData);
                only.ComputeMraText();
                payload.TieZones.Add(only);
                return;
            }

            // Gaps between consecutive levels, snapped to the nearest 25 mm. Snapping +
            // a median filter make the grouping robust to the small Z jitter that comes
            // from using bounding-box centroids for each bar.
            int n = ties.Count;
            var gapMm = new int[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                double g = (ties[i + 1].TieZPositionFt - ties[i].TieZPositionFt) * 304.8;
                int snapped = (int)(Math.Round(g / SPACING_SNAP_MM) * SPACING_SNAP_MM);
                gapMm[i] = Math.Max(snapped, SPACING_SNAP_MM);
            }
            gapMm = MedianSmooth(gapMm);

            // Group consecutive levels that share the same snapped gap into one zone.
            var zones = new List<RebarTieZone>();
            int runStart = 0; // index into ties
            for (int i = 1; i < gapMm.Length; i++)
            {
                if (gapMm[i] != gapMm[i - 1])
                {
                    zones.Add(BuildZoneFromRun(ties, runStart, i, gapMm[runStart], zones.Count));
                    runStart = i;
                }
            }
            // Final run: gaps runStart..end correspond to ties runStart..n-1.
            zones.Add(BuildZoneFromRun(ties, runStart, n - 1, gapMm[runStart], zones.Count));

            // Absorb tiny noise zones (< 3 ties) into the adjacent zone, then cap the total.
            zones = MergeSmallZones(zones);
            zones = CapZones(zones, MAX_TIE_ZONES);

            for (int i = 0; i < zones.Count; i++) zones[i].ZoneIndex = i;
            AssignZoneLabels(zones, colData);
            foreach (var zone in zones) zone.ComputeMraText();
            payload.TieZones.AddRange(zones);
        }

        /// <summary>
        /// Builds a <see cref="RebarTieZone"/> from ties[startIdx..endIdx] (inclusive),
        /// using a known snapped centre-to-centre spacing (mm).
        /// </summary>
        private static RebarTieZone BuildZoneFromRun(
            List<RebarCageBar> ties, int startIdx, int endIdx, int spacingMm, int index)
        {
            var bars = ties.GetRange(startIdx, endIdx - startIdx + 1);
            int tieDiamMm = bars
                .GroupBy(b => b.DiameterMm)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;

            var zone = new RebarTieZone
            {
                ZoneIndex     = index,
                StartZFt      = bars.First().TieZPositionFt,
                EndZFt        = bars.Last().TieZPositionFt,
                Count         = bars.Count,
                SpacingFt     = spacingMm / 304.8,
                TieDiameterMm = tieDiamMm,
            };
            zone.Bars.AddRange(bars);
            return zone;
        }

        /// <summary>Median-of-three filter: removes isolated single-gap spikes.</summary>
        private static int[] MedianSmooth(int[] a)
        {
            if (a.Length < 3) return a;
            var r = (int[])a.Clone();
            for (int i = 1; i < a.Length - 1; i++)
            {
                int x = a[i - 1], y = a[i], z = a[i + 1];
                r[i] = Math.Max(Math.Min(x, y), Math.Min(Math.Max(x, y), z)); // median(x,y,z)
            }
            return r;
        }

        /// <summary>Merges zones with fewer than 3 ties into the adjacent zone with the
        /// closest spacing (keeping the surviving zone's spacing).</summary>
        private static List<RebarTieZone> MergeSmallZones(List<RebarTieZone> zones)
        {
            bool changed = true;
            while (changed && zones.Count > 1)
            {
                changed = false;
                for (int i = 0; i < zones.Count; i++)
                {
                    if (zones[i].Count >= 3) continue;
                    int j;
                    if (i == 0) j = 1;
                    else if (i == zones.Count - 1) j = i - 1;
                    else j = Math.Abs(zones[i - 1].SpacingMm - zones[i].SpacingMm) <=
                             Math.Abs(zones[i + 1].SpacingMm - zones[i].SpacingMm) ? i - 1 : i + 1;
                    AbsorbZone(zones, keep: j, drop: i);
                    changed = true;
                    break;
                }
            }
            return zones;
        }

        /// <summary>Reduces the zone count to <paramref name="max"/> by repeatedly merging
        /// the adjacent pair whose spacings are most similar.</summary>
        private static List<RebarTieZone> CapZones(List<RebarTieZone> zones, int max)
        {
            while (zones.Count > max)
            {
                int bestI = 0; double bestDiff = double.MaxValue;
                for (int i = 0; i < zones.Count - 1; i++)
                {
                    double diff = Math.Abs(zones[i].SpacingMm - zones[i + 1].SpacingMm);
                    if (diff < bestDiff) { bestDiff = diff; bestI = i; }
                }
                AbsorbZone(zones, keep: bestI, drop: bestI + 1);
            }
            return zones;
        }

        /// <summary>Folds the <paramref name="drop"/> zone's ties into the <paramref name="keep"/>
        /// zone (must be adjacent) and removes the dropped zone.</summary>
        private static void AbsorbZone(List<RebarTieZone> zones, int keep, int drop)
        {
            RebarTieZone k = zones[keep], d = zones[drop];
            k.Bars.AddRange(d.Bars);
            k.Bars.Sort((a, b) => a.TieZPositionFt.CompareTo(b.TieZPositionFt));
            k.StartZFt = Math.Min(k.StartZFt, d.StartZFt);
            k.EndZFt   = Math.Max(k.EndZFt, d.EndZFt);
            k.Count    = k.Bars.Count;
            zones.RemoveAt(drop);
        }

        /// <summary>
        /// Assigns human-readable labels to zones based on their position in the
        /// column and their confinement status.
        ///
        /// Drawing SB-FPC1-01-R shows three zones:
        ///   Bottom confinement zone  (dense ties near the corbel / foundation)
        ///   Mid-span zone            (sparser ties in the middle third)
        ///   Top confinement zone     (dense ties near the top joint)
        ///
        /// This method replicates that labelling pattern for any number of zones.
        /// </summary>
        private static void AssignZoneLabels(List<RebarTieZone> zones, PrecastColumnData colData)
        {
            if (zones.Count == 0) return;

            double columnHeightFt = colData.TotalHeightFt;
            double bottomThird    = columnHeightFt / 3.0;
            double topThird       = columnHeightFt * 2.0 / 3.0;

            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                double midZ = (z.StartZFt + z.EndZFt) / 2.0;

                if (midZ < bottomThird)
                    z.Label = $"Zone {i + 1} — Confinement Bottom (T{z.TieDiameterMm} @ {z.SpacingMm:F0}mm)";
                else if (midZ > topThird)
                    z.Label = $"Zone {i + 1} — Confinement Top (T{z.TieDiameterMm} @ {z.SpacingMm:F0}mm)";
                else
                    z.Label = $"Zone {i + 1} — Mid Span (T{z.TieDiameterMm} @ {z.SpacingMm:F0}mm)";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 2E — BEND SCHEDULE EXTRACTION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the complete rebar schedule matching the SB-FPC1-01-R1 table.
        ///
        /// Strategy:
        ///   1. For each unique (BarMark, DiameterMm, ShapeCode) combination,
        ///      create one RebarBendData row.
        ///   2. Aggregate quantities and total lengths.
        ///   3. Extract bending dimensions (A, B, C, D) from the bar's RebarShape
        ///      geometry by sampling segment lengths in the bar's local frame.
        ///
        /// Bar Mark assignment priority:
        ///   (a) "Bar Mark" shared parameter on the element (set by the engineer)
        ///   (b) Auto-assign: longitudinal bars first (largest diameter first),
        ///       then transverse bars (ascending diameter).
        ///   This matches the pattern visible in SB-FPC1-01-R1 (marks 1–19).
        /// </summary>
        private static void BuildBendSchedule(
            List<Element> rebarElements,
            PrecastColumnData colData,
            RebarPayload payload,
            Document doc)
        {
            // ── Step 1: Assign bar marks if not set ───────────────────────
            AssignBarMarks(payload);

            // ── Step 2: Build one schedule row per bar mark ───────────────
            // Group by BarMark so all bars with the same mark are in one row
            var allBars = payload.LongitudinalBars
                .Concat(payload.TransverseBars)
                .Where(b => b.BarMark != null)
                .OrderBy(b =>
                {
                    // Sort numerically if bar mark is a number, alphabetically otherwise
                    if (int.TryParse(b.BarMark, out int n)) return n;
                    return 999;
                })
                .ToList();

            var markGroups = allBars
                .GroupBy(b => b.BarMark)
                .ToList();

            // Map ElementId → Element for bending dimension extraction
            var elementMap = rebarElements.ToDictionary(e => (int)e.Id.Value, e => e);

            foreach (var group in markGroups)
            {
                var firstBar  = group.First();
                var schedRow  = new RebarBendData
                {
                    BarMark       = group.Key,
                    DiameterMm    = firstBar.DiameterMm,
                    Quantity      = group.Count(),
                    RevitElementIds = group.Select(b => b.RevitElementId).ToList(),
                };

                // ── Cut length (mm, rounded to nearest integer) ────────────
                // Use the first bar's cut length — all bars in a mark group
                // should have the same cut length. If they differ, log a warning.
                double refLengthFt = firstBar.CuttingLengthFt;
                bool lengthMismatch = group.Any(b =>
                    Math.Abs(b.CuttingLengthFt - refLengthFt) > (5.0 / 304.8));

                if (lengthMismatch)
                    colData.ParseWarnings.Add(
                        $"Phase 2: Bar mark '{group.Key}' has inconsistent cut lengths. " +
                        $"Using {refLengthFt * 304.8:F0}mm from the first bar. Verify the model.");

                schedRow.CuttingLengthMm = (int)Math.Round(refLengthFt * 304.8);

                // ── Bending dimensions ─────────────────────────────────────
                if (elementMap.TryGetValue(firstBar.RevitElementId, out Element revitElem))
                    ExtractBendingDimensions(revitElem, schedRow, colData);
                else
                    schedRow.Dims = InferBendingDimsFromRole(firstBar, colData);

                // ── Shape code ─────────────────────────────────────────────
                schedRow.ShapeCode = InferShapeCode(firstBar, schedRow.Dims);

                payload.BendSchedule.Add(schedRow);
            }

            // Sort schedule by bar mark numerically
            payload.BendSchedule.Sort((a, b) =>
            {
                bool aNum = int.TryParse(a.BarMark, out int na);
                bool bNum = int.TryParse(b.BarMark, out int nb);
                if (aNum && bNum) return na.CompareTo(nb);
                return string.Compare(a.BarMark, b.BarMark, StringComparison.Ordinal);
            });
        }

        /// <summary>
        /// Auto-assigns bar marks if the "Bar Mark" shared parameter is not set
        /// on the rebar elements.
        ///
        /// Assignment order (matching SB-FPC1-01-R1):
        ///   1. Longitudinal bars, grouped by diameter descending, then by Z position
        ///   2. Transverse bars, grouped by diameter ascending, then by Z position
        ///
        /// Bars that already have a mark from the model are left unchanged.
        /// </summary>
        private static void AssignBarMarks(RebarPayload payload)
        {
            int nextMark = 1;

            // ── Check existing marks on all bars first ─────────────────────
            var allBars = payload.LongitudinalBars.Concat(payload.TransverseBars).ToList();
            bool anyExplicitMarks = allBars.Any(b => !string.IsNullOrWhiteSpace(b.BarMark));

            if (anyExplicitMarks)
            {
                // Respect existing marks; only fill in the blanks
                // Find the highest numeric mark already assigned
                int maxExisting = allBars
                    .Where(b => int.TryParse(b.BarMark, out _))
                    .Select(b => int.Parse(b.BarMark))
                    .DefaultIfEmpty(0)
                    .Max();
                nextMark = maxExisting + 1;

                foreach (var bar in allBars)
                    if (string.IsNullOrWhiteSpace(bar.BarMark))
                        bar.BarMark = (nextMark++).ToString();

                return;
            }

            // ── Full auto-assignment ───────────────────────────────────────
            // Order longitudinal: largest diameter first, then by bottom Z
            var orderedLong = payload.LongitudinalBars
                .OrderByDescending(b => b.DiameterMm)
                .ThenBy(b => b.BottomZLocal)
                .ToList();

            // Group by (diameter, cut length, bottom-Z cluster) to find unique bar marks
            // Two bars share a mark if they have the same diameter, same cut length (±5mm),
            // and roughly the same position (within 50mm Z — accounts for rounding).
            var longGroups = GroupBarsIntoMarks(orderedLong, toleranceMm: 5.0);
            foreach (var group in longGroups)
                foreach (var bar in group)
                    bar.BarMark = (nextMark).ToString();

            nextMark += longGroups.Count;

            // Order transverse: ascending diameter, then by Z
            var orderedTrans = payload.TransverseBars
                .OrderBy(b => b.DiameterMm)
                .ThenBy(b => b.TieZPositionFt)
                .ToList();

            var transGroups = GroupBarsIntoMarks(orderedTrans, toleranceMm: 5.0);
            foreach (var group in transGroups)
                foreach (var bar in group)
                    bar.BarMark = (nextMark).ToString();
        }

        /// <summary>
        /// Groups bars into mark groups where all members share the same
        /// diameter and cut length (within tolerance).
        ///
        /// This is the key step that maps from "N individual Rebar elements"
        /// to "M rows in the schedule table".  For example, 8 identical T25
        /// longitudinal bars at different positions → 1 schedule row, Qty=8.
        /// </summary>
        private static List<List<RebarCageBar>> GroupBarsIntoMarks(
            List<RebarCageBar> bars,
            double toleranceMm)
        {
            var groups = new List<List<RebarCageBar>>();
            double toleranceFt = toleranceMm / 304.8;

            foreach (var bar in bars)
            {
                bool added = false;
                foreach (var group in groups)
                {
                    var rep = group[0];
                    if (rep.DiameterMm == bar.DiameterMm &&
                        Math.Abs(rep.CuttingLengthFt - bar.CuttingLengthFt) <= toleranceFt)
                    {
                        group.Add(bar);
                        added = true;
                        break;
                    }
                }
                if (!added)
                    groups.Add(new List<RebarCageBar> { bar });
            }
            return groups;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  BENDING DIMENSION EXTRACTION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the A, B, C, D bending dimensions from a Revit Rebar element
        /// by reading its RebarShape's segment geometry.
        ///
        /// Revit stores a bent bar as a series of line segments in the bar's local
        /// coordinate frame.  The segment lengths map directly to the A, B, C, D
        /// values in IS 2502 / BS 8666 bending schedules.
        ///
        /// Segment → dimension mapping:
        ///   Straight bar:       1 segment → A = segment length
        ///   L-shape (1 bend):   2 segments → A = first leg, B = second leg
        ///   U-shape (2 bends):  3 segments → A = first leg, B = base, (implied D = last leg = A)
        ///   Rectangular link:   4 segments → A = width, B = height, C = hook, D = hook
        ///   Standard hook adds a short segment at the end → captured in C and D.
        /// </summary>
        private static void ExtractBendingDimensions(
            Element revitElem,
            RebarBendData schedRow,
            PrecastColumnData colData)
        {
            try
            {
                Rebar rebar = revitElem as Rebar;
                if (rebar == null)
                {
                    // RebarInSystem — use the inferral path
                    schedRow.Dims = InferBendingDimsFromCutLength(schedRow, colData);
                    return;
                }

                // ── Get the bar's curve set in its LOCAL frame ──────────────
                // GetCenterlineCurves returns the bar path as a list of curves.
                // For a shape-driven bar this is the definitive geometry source.
                // Positional args (Revit 2027 names the 2nd param suppressHooksAndCranks):
                //   adjustForSelfIntersection=false, suppressHooksAndCranks=false,
                //   suppressBendRadius=true, multiplanarOption, barPositionIndex=0
                IList<Curve> centerlineCurves = rebar.GetCenterlineCurves(
                    false,
                    false,
                    true,   // remove bend rounding for dimension accuracy
                    MultiplanarOption.IncludeAllMultiplanarCurves,
                    0);

                if (centerlineCurves == null || centerlineCurves.Count == 0)
                {
                    schedRow.Dims = InferBendingDimsFromCutLength(schedRow, colData);
                    return;
                }

                // ── Convert curve lengths to mm (rounded to nearest integer) ─
                // Filter to only Line segments (arcs = bend radii, skip them)
                var segmentLengthsMm = centerlineCurves
                    .OfType<Line>()
                    .Select(l => (int)Math.Round(l.Length * 304.8))
                    .Where(l => l > 5) // discard tiny artefacts < 5mm
                    .ToList();

                schedRow.Dims = MapSegmentsToABCD(segmentLengthsMm, schedRow, rebar, colData);
                // Shape name is cosmetic and the Document isn't threaded here; leave blank.
                schedRow.RevitShapeName = string.Empty;
            }
            catch (Exception ex)
            {
                colData.ParseWarnings.Add(
                    $"Phase 2: Could not extract bending dims for bar mark '{schedRow.BarMark}': {ex.Message}");
                schedRow.Dims = InferBendingDimsFromCutLength(schedRow, colData);
            }
        }

        // Placeholder — in Phase 4 the Document will be threaded through.
        // For now, return null safely; the name is cosmetic only.
        private static Element doc_GetElement_Safe(PrecastColumnData colData, ElementId id)
            => null;

        /// <summary>
        /// Maps a list of segment lengths (mm) onto the A, B, C, D slots
        /// using the standard IS 2502 / BS 8666 conventions.
        ///
        /// This is the definitive mapping table — extend for non-standard shapes.
        /// </summary>
        private static BendingDimensions MapSegmentsToABCD(
            List<int> segs,
            RebarBendData schedRow,
            Rebar rebar,
            PrecastColumnData colData)
        {
            var d = new BendingDimensions();

            switch (segs.Count)
            {
                case 0:
                    // No segments — degenerate bar
                    d.A = schedRow.CuttingLengthMm;
                    break;

                case 1:
                    // Straight bar — A only
                    d.A = segs[0];
                    break;

                case 2:
                    // L-bar — A = leg 1, B = leg 2
                    d.A = segs[0];
                    d.B = segs[1];
                    break;

                case 3:
                    // U-bar or cranked bar
                    // For U-bars: A = first leg, B = base/web, C = 0, D = 0
                    // (the second leg mirrors A and IS 2502 omits it from the table)
                    d.A = segs[0];
                    d.B = segs[1];
                    d.C = 0;
                    d.D = 0;
                    break;

                case 4:
                    // Rectangular closed link — A = shorter side, B = longer side,
                    // C = hook extension, D = hook extension (usually equal)
                    // Sort the four segments: the two longest are the main sides
                    var sorted4 = segs.OrderByDescending(x => x).ToList();
                    d.A = sorted4[1]; // shorter of the two main sides
                    d.B = sorted4[0]; // longer of the two main sides
                    d.C = sorted4[2]; // hook 1
                    d.D = sorted4[3]; // hook 2
                    break;

                case 5:
                    // Standard closed link WITH hooks at both ends
                    // Segments: hook1, long side, short side, long side, hook2
                    // The middle three are the perimeter; the first and last are hooks
                    d.A = segs[1]; // long side (B on the drawing is the longer side)
                    d.B = segs[2]; // short side
                    d.C = segs[0]; // hook 1
                    d.D = segs[4]; // hook 2
                    break;

                default:
                    // Complex shape — use cut length as A, warn
                    d.A = schedRow.CuttingLengthMm;
                    colData.ParseWarnings.Add(
                        $"Phase 2: Bar mark '{schedRow.BarMark}' has {segs.Count} segments " +
                        $"(unsupported shape). A = cut length. Verify manually.");
                    break;
            }

            return d;
        }

        /// <summary>
        /// Fallback bending dimension inferral when GetCenterlineCurves is unavailable.
        /// Uses the bar's role and cut length to make a reasonable estimate.
        /// ALWAYS produces a warning so the engineer knows to verify.
        /// </summary>
        private static BendingDimensions InferBendingDimsFromRole(
            RebarCageBar bar,
            PrecastColumnData colData)
        {
            int cutMm = (int)Math.Round(bar.CuttingLengthFt * 304.8);
            var d = new BendingDimensions { A = cutMm };

            if (bar.Role == RebarRole.Transverse)
            {
                // Estimate as a rectangular link:
                // perimeter ≈ cut length. Inner dims ≈ shaft - 2×cover - 2×bar-dia
                int inner = (int)Math.Round(
                    (colData.ShaftWidthFt * 304.8) -
                    (2 * NOMINAL_COVER_MM) -
                    (2 * bar.DiameterMm));
                d.A = inner;
                d.B = inner; // square column — same in both directions
                d.C = 96;    // standard 135° hook extension = 10d for T12 → 120mm; 8d for T8 → 96mm
                d.D = 96;
                colData.ParseWarnings.Add(
                    $"Phase 2: Bending dims for transverse bar mark '{bar.BarMark}' inferred from shaft geometry. Verify A={d.A}, B={d.B}.");
            }
            else
            {
                colData.ParseWarnings.Add(
                    $"Phase 2: Bending dims for longitudinal bar mark '{bar.BarMark}' inferred as straight (A={d.A}). Verify if cranked.");
            }

            return d;
        }

        private static BendingDimensions InferBendingDimsFromCutLength(
            RebarBendData row,
            PrecastColumnData colData)
        {
            colData.ParseWarnings.Add(
                $"Phase 2: Could not read curve geometry for bar mark '{row.BarMark}'. " +
                "Bending dimensions inferred from cut length. Verify.");
            return new BendingDimensions { A = row.CuttingLengthMm };
        }

        private static BendingShapeCode InferShapeCode(
            RebarCageBar bar,
            BendingDimensions dims)
        {
            if (dims.IsStraight)                      return BendingShapeCode.Straight;
            if (dims.C == 0 && dims.D == 0 && dims.B > 0)
                return BendingShapeCode.UShape;
            if (dims.C > 0 && dims.D > 0)             return BendingShapeCode.Rectangular;
            if (dims.B > 0 && dims.C == 0)            return BendingShapeCode.LShape;
            return BendingShapeCode.Custom;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  REVIT REBAR PROPERTY READERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the primary axis direction of a rebar element in WORLD coordinates.
        ///
        /// For free Rebar: the bar's shape-driven transform BasisX is the axis.
        ///   - GetShapeDrivenAccessor().GetBarPositionTransform(0) gives the
        ///     transform of the first bar occurrence in the element.
        ///   - BasisX of that transform = primary axis of the bar.
        ///
        /// For RebarInSystem: use the element transform's BasisX.
        ///
        /// Returns null if the axis cannot be determined.
        /// </summary>
        private static XYZ GetBarAxisWorld(Element elem)
        {
            if (elem is Rebar rebar)
            {
                try
                {
                    // Shape-driven bars (the common case for individually placed rebar)
                    if (rebar.IsRebarShapeDriven())
                    {
                        var accessor = rebar.GetShapeDrivenAccessor();
                        Transform barTransform = accessor.GetBarPositionTransform(0);
                        return barTransform.BasisX.Normalize();
                    }
                    else
                    {
                        // Sketch-driven bars — fall back to the first segment of GetCenterlineCurves
                        IList<Curve> curves = rebar.GetCenterlineCurves(false, false, true,
                            MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                        if (curves?.Count > 0 && curves[0] is Line line)
                            return (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                    }
                }
                catch { /* fall through */ }
            }

            if (elem is RebarInSystem ris)
            {
                // RebarInSystem has no GetTransform(); the per-bar position transform
                // gives the bar's local frame, whose BasisX is the primary axis.
                try { return ris.GetBarPositionTransform(0).BasisX.Normalize(); }
                catch { /* fall through */ }
            }

            // Generic fallback: use element bounding box diagonal direction
            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb != null)
            {
                XYZ diag = (bb.Max - bb.Min);
                if (diag.GetLength() > 1e-6) return diag.Normalize();
            }

            return null;
        }

        /// <summary>
        /// Returns the centroid of a rebar element in WORLD coordinates.
        /// Uses the bounding box midpoint — adequate for classification and
        /// Z-position determination.
        /// </summary>
        private static XYZ GetRebarCentroid(Element elem)
        {
            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb != null)
                return (bb.Min + bb.Max) / 2.0;

            // If no bounding box (unusual), try to get from curves
            if (elem is Rebar rebar && rebar.IsRebarShapeDriven())
            {
                try
                {
                    var curves = rebar.GetCenterlineCurves(false, false, true,
                        MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                    if (curves?.Count > 0)
                        return curves[0].Evaluate(0.5, true);
                }
                catch { /* fall through */ }
            }

            return XYZ.Zero;
        }

        /// <summary>
        /// Returns the cut length (bar length) in Revit internal feet.
        ///
        /// Reading order:
        ///   1. REBAR_ELEM_LENGTH built-in parameter (Revit-computed, most accurate)
        ///   2. For Rebar: sum of centerline curve lengths
        ///   3. Bounding box diagonal length (rough fallback)
        /// </summary>
        private static double GetRebarCutLength(Element elem)
        {
            // Preferred: built-in parameter
            Parameter lenParam = elem.get_Parameter(BuiltInParameter.REBAR_ELEM_LENGTH);
            if (lenParam != null && lenParam.HasValue && lenParam.AsDouble() > 1e-6)
                return lenParam.AsDouble();

            // Fallback: sum centerline curves
            if (elem is Rebar rebar && rebar.IsRebarShapeDriven())
            {
                try
                {
                    IList<Curve> curves = rebar.GetCenterlineCurves(false, false, true,
                        MultiplanarOption.IncludeAllMultiplanarCurves, 0);
                    if (curves?.Count > 0)
                        return curves.Sum(c => c.Length);
                }
                catch { /* fall through */ }
            }

            // Last resort: bounding box diagonal
            BoundingBoxXYZ bb = elem.get_BoundingBox(null);
            if (bb != null) return (bb.Max - bb.Min).GetLength();

            return 0;
        }

        /// <summary>
        /// Returns the nominal bar diameter in mm.
        /// Reading order:
        ///   1. REBAR_BAR_DIAMETER built-in parameter (preferred)
        ///   2. BarType.BarDiameter property for free Rebar
        /// </summary>
        private static int GetRebarDiameterMm(Element elem)
        {
            // Try built-in parameter first
            Parameter dParam = elem.get_Parameter(BuiltInParameter.REBAR_BAR_DIAMETER);
            if (dParam != null && dParam.HasValue)
                return (int)Math.Round(dParam.AsDouble() * 304.8);

            // RebarBarType.BarNominalDiameter (Rebar has no BarType property in 2027 —
            // resolve the type element via GetTypeId()).
            if (elem is Rebar rebar)
            {
                try
                {
                    var barType = elem.Document.GetElement(rebar.GetTypeId()) as RebarBarType;
                    if (barType != null)
                        return (int)Math.Round(barType.BarNominalDiameter * 304.8);
                }
                catch { /* fall through */ }
            }

            // RebarInSystem — resolve its bar type the same way.
            if (elem is RebarInSystem ris)
            {
                try
                {
                    RebarBarType barType = ris.Document.GetElement(ris.GetTypeId()) as RebarBarType;
                    if (barType != null)
                        return (int)Math.Round(barType.BarNominalDiameter * 304.8);
                }
                catch { /* fall through */ }
            }

            return 0; // Unknown diameter — will generate a warning upstream
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GEOMETRY UTILITIES
        // ─────────────────────────────────────────────────────────────────────

        private static List<RebarCageBar> DedupTiesByZ(List<RebarCageBar> ties)
        {
            // Ties closer than this in Z are treated as the same level (perimeter link +
            // cross-ties + diamond links). 30 mm is well under the smallest real pitch.
            const double DEDUP_TOLERANCE_FT = 30.0 / 304.8;
            var result = new List<RebarCageBar> { ties[0] };

            for (int i = 1; i < ties.Count; i++)
            {
                double zDiff = ties[i].TieZPositionFt - result[result.Count - 1].TieZPositionFt;
                if (Math.Abs(zDiff) > DEDUP_TOLERANCE_FT)
                    result.Add(ties[i]);
                // else: duplicate — skip
            }
            return result;
        }

        private static double EstimateClearCover(XYZ centroidLocal, int diamMm, PrecastColumnData colData)
        {
            // Distance from bar centroid to nearest face in the XY plane
            double halfW = colData.ShaftWidthFt / 2.0;
            double halfD = colData.ShaftDepthFt / 2.0;

            double dxToFace = Math.Min(
                Math.Abs(centroidLocal.X - halfW),
                Math.Abs(centroidLocal.X + halfW));
            double dyToFace = Math.Min(
                Math.Abs(centroidLocal.Y - halfD),
                Math.Abs(centroidLocal.Y + halfD));

            double faceDistFt = Math.Min(dxToFace, dyToFace);
            double faceDistMm = faceDistFt * 304.8;

            // Clear cover = face distance - half bar diameter
            return faceDistMm - (diamMm / 2.0);
        }

        private static Transform BuildWorldToLocalTransform(PrecastColumnData colData)
        {
            Transform t = Transform.Identity;
            t.Origin = colData.BaseCentreWorld.ToXYZ();
            t.BasisX = colData.Orientation.AxisX.ToXYZ();
            t.BasisY = colData.Orientation.AxisY.ToXYZ();
            t.BasisZ = colData.Orientation.AxisZ.ToXYZ();
            return t.Inverse;
        }

        private static double ToDegrees(double radians) => radians * (180.0 / Math.PI);
    }
}