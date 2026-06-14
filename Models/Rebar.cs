// =============================================================================
//  StructAuto Detailing — Phase 2: Rebar Domain Model
//  File:    Models/RebarData.cs
//
//  Extends PrecastColumnData with strongly-typed rebar objects.
//  Zero Revit API references — all values stored in feet (internal) or
//  millimetres (display) per the project convention.
//
//  These replace the placeholder  List<object>  fields in PrecastColumnData.
//  After Phase 2 runs, cast those lists to the concrete types here, or
//  (preferred) replace the object lists in PrecastColumnData with typed lists
//  once Phase 2 is confirmed stable.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace StructAutoDetailing.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  BAR SHAPE ENUM  (mirrors IS 2502 / BS 8666 standard shape codes)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Standard bending shape codes used in the rebar schedule.
    /// Maps directly to the shape sketches on drawing SB-FPC1-01-R1.
    /// </summary>
    public enum BendingShapeCode
    {
        Unknown     = 0,
        Straight    = 1,    // Shape A only — bars 1, 2, 8, 14, 18
        UShape      = 2,    // Two legs + base — bars 3, 4, 5 (closed links will be Rectangular)
        LShape      = 3,    // One bend
        Rectangular = 4,    // Closed rectangular link — bars 6, 7 (standard ties)
        Cranked     = 5,    // One crank
        Custom      = 99    // Shape not auto-detected — requires manual check
    }

    /// <summary>
    /// Classification of a rebar relative to the column axis.
    /// Drives which dimensioning logic is applied.
    /// </summary>
    public enum RebarRole
    {
        Unknown,
        Longitudinal,   // Runs parallel to the column height axis (angle < 15° to localZ)
        Transverse,     // Runs perpendicular — ties/links (angle > 75° to localZ)
        Diagonal,       // Inclined bars (15°–75°) — e.g. corbel ties
        Confinement,    // Transverse bar inside a confinement zone (high-density tie spacing)
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REBAR CAGE BAR  (one physical rebar element)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents one rebar element (or one bar in a RebarInSystem set) extracted
    /// from the column.  Geometric values in Revit internal feet.
    /// </summary>
    public class RebarCageBar
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>Revit ElementId as int (avoids Revit API dependency in model).</summary>
        public int RevitElementId  { get; set; }

        /// <summary>
        /// Bar mark from the rebar schedule — read from "Bar Mark" shared parameter,
        /// or auto-assigned (1, 2, 3…) by the analyser in bar-diameter + role order.
        /// </summary>
        public string BarMark      { get; set; }

        /// <summary>Nominal diameter in mm (e.g. 25, 20, 12, 10, 8, 16).</summary>
        public int DiameterMm      { get; set; }

        // ── Classification ─────────────────────────────────────────────────
        public RebarRole Role      { get; set; } = RebarRole.Unknown;

        /// <summary>
        /// The angle between the bar's principal axis and the column's local Z axis,
        /// in degrees. Stored for debugging; Role is derived from this.
        /// </summary>
        public double AngleToVerticalDeg { get; set; }

        // ── Position (LOCAL column coordinates, feet) ─────────────────────
        /// <summary>
        /// Centre-of-gravity of the bar in LOCAL column space.
        /// For straight longitudinal bars, this is the midpoint of the bar length.
        /// For ties, this is the centroid of the ring.
        /// </summary>
        public Vec3 CentroidLocal  { get; set; }

        /// <summary>
        /// For longitudinal bars: the Z-position of the bar's BOTTOM end (feet,
        /// measured from column base). Used to detect lap zones.
        /// </summary>
        public double BottomZLocal { get; set; }

        /// <summary>
        /// For longitudinal bars: the Z-position of the bar's TOP end (feet).
        /// </summary>
        public double TopZLocal    { get; set; }

        // ── For transverse bars: position along column height ──────────────
        /// <summary>
        /// Z-height of the centroid of this tie bar above the column base, in feet.
        /// Used exclusively by the TieZone clustering algorithm.
        /// </summary>
        public double TieZPositionFt { get; set; }

        // ── Geometry ───────────────────────────────────────────────────────
        /// <summary>
        /// Unit vector along the bar's primary axis in LOCAL column space.
        /// For longitudinal bars: ≈ (0, 0, 1).
        /// For ties: lies in the XY plane.
        /// </summary>
        public Vec3 AxisLocal      { get; set; }

        /// <summary>Cut length of this individual bar in feet.</summary>
        public double CuttingLengthFt { get; set; }

        // ── Cover ──────────────────────────────────────────────────────────
        /// <summary>
        /// Calculated clear cover from the bar face to the nearest concrete surface,
        /// in mm.  For the SB-FPC1 column: nominal cover = 50mm.
        /// </summary>
        public double ClearCoverMm { get; set; }

        // ── Display helpers ────────────────────────────────────────────────
        public double CuttingLengthMm => CuttingLengthFt * 304.8;
        public double TieZPositionMm  => TieZPositionFt  * 304.8;
        public double BottomZMm       => BottomZLocal     * 304.8;
        public double TopZMm          => TopZLocal        * 304.8;

        public override string ToString() =>
            $"T{DiameterMm} [{Role}] Mark={BarMark} " +
            $"Z={TieZPositionMm:F0}mm CL={CuttingLengthMm:F0}mm";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REBAR TIE ZONE  (clustered group of transverse bars at uniform spacing)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A contiguous vertical zone of the column where transverse bars (ties) are
    /// spaced at a single uniform pitch.  Produced by the spacing-clustering
    /// algorithm in <c>RebarCageAnalyzer.ClusterTieZones()</c>.
    ///
    /// The MRA (Multi-Rebar Annotation) tag text is computed here and stored as
    /// <see cref="MraTagText"/> — ready to pass to the MultiReferenceAnnotation API
    /// or to a manual Dimension override in Phase 4.
    /// </summary>
    public class RebarTieZone
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>Sequential zone index (0 = bottom of column).</summary>
        public int ZoneIndex  { get; set; }

        /// <summary>
        /// Human-readable label: "Zone 1 — Confinement Bottom",
        /// "Zone 2 — Mid Span", "Zone 3 — Confinement Top".
        /// </summary>
        public string Label   { get; set; }

        // ── Z extents (feet, measured from column base) ───────────────────
        /// <summary>Z-height at bottom of this zone (first tie centroid). Feet.</summary>
        public double StartZFt { get; set; }

        /// <summary>Z-height at top of this zone (last tie centroid). Feet.</summary>
        public double EndZFt   { get; set; }

        // ── Spacing data ───────────────────────────────────────────────────
        /// <summary>
        /// Number of tie bars in this zone.
        /// Note: count is the number of BARS, not the number of spaces.
        /// The zone length = (Count - 1) × Spacing for uniformly spaced bars.
        /// </summary>
        public int Count       { get; set; }

        /// <summary>Centre-to-centre spacing between ties. Feet.</summary>
        public double SpacingFt { get; set; }

        /// <summary>Diameter of the tie bars in this zone. mm.</summary>
        public int TieDiameterMm { get; set; }

        // ── References to the actual bar objects ───────────────────────────
        /// <summary>
        /// All <see cref="RebarCageBar"/> objects that belong to this zone,
        /// ordered bottom-to-top.  Used to build the Revit ReferenceArray for MRA.
        /// </summary>
        public List<RebarCageBar> Bars { get; set; } = new List<RebarCageBar>();

        // ── MRA annotation text ────────────────────────────────────────────
        /// <summary>
        /// Pre-formatted annotation string for the Multi-Rebar Annotation tag.
        /// Format: "n @ spacing = total" — e.g. "15 @ 100 = 1500".
        /// ALL values in millimetres (display standard for this project).
        /// Computed by <see cref="ComputeMraText"/>.
        /// </summary>
        public string MraTagText { get; private set; }

        /// <summary>
        /// Recomputes <see cref="MraTagText"/> from current Count and SpacingFt.
        /// Call after any modification to Count or SpacingFt.
        /// </summary>
        public void ComputeMraText()
        {
            int spacingMm = (int)Math.Round(SpacingFt * 304.8);
            int totalMm   = (int)Math.Round((Count - 1) * SpacingFt * 304.8);
            // Standard format seen on SB-FPC1-01-R: "15 @ 100 = 1500"
            // (count of SPACES = Count - 1, matching IS/BS convention)
            MraTagText = $"{Count - 1} @ {spacingMm} = {totalMm}";
        }

        // ── Classification helpers ─────────────────────────────────────────
        /// <summary>
        /// True if this zone's spacing is ≤ 150mm — classified as a confinement
        /// zone (seismic / corbel bearing).  Drawn with denser annotation.
        /// </summary>
        public bool IsConfinementZone => SpacingFt * 304.8 <= 150.0;

        // ── Display ────────────────────────────────────────────────────────
        public double SpacingMm  => SpacingFt * 304.8;
        public double StartZMm   => StartZFt  * 304.8;
        public double EndZMm     => EndZFt    * 304.8;
        public double ZoneLengthFt => EndZFt - StartZFt;
        public double ZoneLengthMm => ZoneLengthFt * 304.8;

        public override string ToString() =>
            $"[TieZone {ZoneIndex}] {Label} " +
            $"Z={StartZMm:F0}–{EndZMm:F0}mm  {MraTagText}  T{TieDiameterMm}  " +
            $"Confinement={IsConfinementZone}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BENDING DIMENSIONS  (A, B, C, D values from the schedule table)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four bending dimensions (A, B, C, D) for one bar, in millimetres,
    /// as printed in the rebar schedule table on drawing SB-FPC1-01-R1.
    ///
    /// For a straight bar: A = cut length, B = C = D = 0.
    /// For a rectangular link: A = outer width, B = outer height, C = D = hook extension.
    /// For a U-bar: A = leg length, B = width, C = D = hook extension (if any).
    /// </summary>
    public class BendingDimensions
    {
        /// <summary>Primary leg / straight length (mm). Always populated.</summary>
        public int A { get; set; }

        /// <summary>Second leg or return (mm). 0 for straight bars.</summary>
        public int B { get; set; }

        /// <summary>Third dimension — typically hook or crank leg (mm). 0 if not applicable.</summary>
        public int C { get; set; }

        /// <summary>Fourth dimension — hook on opposite end (mm). 0 if not applicable.</summary>
        public int D { get; set; }

        /// <summary>
        /// True if all dimensions are zero except A — indicates a straight bar
        /// where cut length = A and the schedule prints "0" for B, C, D.
        /// </summary>
        public bool IsStraight => B == 0 && C == 0 && D == 0;

        public override string ToString() =>
            IsStraight
                ? $"A={A}"
                : $"A={A}  B={B}  C={C}  D={D}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REBAR BEND DATA  (one row in the rebar schedule table)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row in the rebar schedule table (SB-FPC1-01-R1).
    /// Aggregates all bars of the same mark/diameter/shape into a single row.
    /// Weight is calculated from total bar length × unit weight.
    /// </summary>
    public class RebarBendData
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>Bar mark (1–19 in the reference drawing).</summary>
        public string BarMark      { get; set; }

        /// <summary>Nominal diameter in mm.</summary>
        public int DiameterMm      { get; set; }

        /// <summary>Total quantity of bars at this bar mark.</summary>
        public int Quantity        { get; set; }

        // ── Shape ──────────────────────────────────────────────────────────
        public BendingShapeCode ShapeCode { get; set; }

        /// <summary>
        /// Revit RebarShape element name (e.g. "M_00", "M_21", or custom name).
        /// Stored verbatim for reference; ShapeCode is the engine's classification.
        /// </summary>
        public string RevitShapeName { get; set; }

        // ── Dimensions ─────────────────────────────────────────────────────
        public BendingDimensions Dims { get; set; } = new BendingDimensions();

        // ── Lengths ────────────────────────────────────────────────────────
        /// <summary>Cut length of ONE bar in mm (after bending allowances).</summary>
        public int CuttingLengthMm { get; set; }

        /// <summary>Total bar length for ALL bars at this mark (mm).</summary>
        public int TotalBarLengthMm => CuttingLengthMm * Quantity;

        // ── Weight calculation ─────────────────────────────────────────────
        /// <summary>
        /// Unit weight of the bar in kg/m.
        /// Formula: (D² / 162) where D is diameter in mm — standard IS 1786 formula.
        /// Pre-computed from DiameterMm so the schedule can display correct weights.
        /// </summary>
        public double UnitWeightKgPerM => (DiameterMm * DiameterMm) / 162.0;

        /// <summary>Weight of ALL bars at this mark in kg.</summary>
        public double TotalWeightKg =>
            UnitWeightKgPerM * (TotalBarLengthMm / 1000.0);

        /// <summary>Formatted weight string for schedule table: "127.31 kg".</summary>
        public string WeightFormatted => $"{TotalWeightKg:F2} kg";

        // ── ElementId references (for Revit annotation in Phase 4) ────────
        /// <summary>
        /// All Revit ElementIds (as int) for bars belonging to this mark.
        /// Used to build the ReferenceArray for MRA and IndependentTag calls.
        /// </summary>
        public List<int> RevitElementIds { get; set; } = new List<int>();

        public override string ToString() =>
            $"Mark {BarMark}  T{DiameterMm}  Qty={Quantity}  " +
            $"L={CuttingLengthMm}mm  {WeightFormatted}  [{Dims}]";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  REBAR SCHEDULE SUMMARY  (the summary table on SB-FPC1-01-R1)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregated weight summary per diameter, matching the
    /// "SB-FPC1-01 REBAR SCHEDULE SUMMARY" table on the R1 sheet.
    /// Populated by <c>RebarCageAnalyzer.BuildScheduleSummary()</c>.
    /// </summary>
    public class RebarScheduleSummary
    {
        /// <summary>Rows keyed by nominal diameter (mm).</summary>
        public Dictionary<int, ScheduleSummaryRow> RowsByDiameter { get; }
            = new Dictionary<int, ScheduleSummaryRow>();

        /// <summary>Sum of all rows' TotalWeight.</summary>
        public double GrandTotalWeightKg =>
            RowsByDiameter.Values.Sum(r => r.TotalWeightKg);

        public string GrandTotalFormatted => $"{GrandTotalWeightKg:F2} kg";

        /// <summary>Adds a <see cref="RebarBendData"/> row to the correct diameter bucket.</summary>
        public void Add(RebarBendData row)
        {
            if (!RowsByDiameter.ContainsKey(row.DiameterMm))
                RowsByDiameter[row.DiameterMm] = new ScheduleSummaryRow { DiameterMm = row.DiameterMm };

            RowsByDiameter[row.DiameterMm].TotalBarLengthMm += row.TotalBarLengthMm;
        }
    }

    public class ScheduleSummaryRow
    {
        public int    DiameterMm        { get; set; }
        public int    TotalBarLengthMm  { get; set; }
        public double UnitWeightKgPerM  => (DiameterMm * DiameterMm) / 162.0;
        public double TotalWeightKg     => UnitWeightKgPerM * (TotalBarLengthMm / 1000.0);
        public string WeightFormatted   => $"{TotalWeightKg:F2} kg";

        public override string ToString() =>
            $"T{DiameterMm}  L={TotalBarLengthMm}mm  W={WeightFormatted}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TYPED EXTENSION OF PrecastColumnData
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strongly-typed rebar payload attached to a PrecastColumnData instance
    /// after Phase 2 completes.  Stored on the parent via the
    /// <c>RebarPayload</c> property below.
    ///
    /// Kept separate from the domain model fields on PrecastColumnData so that
    /// Phase 1 never has a compile-time dependency on rebar types, and so the
    /// payload can be null-checked at runtime before Phase 2 is called.
    /// </summary>
    public class RebarPayload
    {
        public List<RebarCageBar>       LongitudinalBars  { get; } = new List<RebarCageBar>();
        public List<RebarCageBar>       TransverseBars    { get; } = new List<RebarCageBar>();
        public List<RebarTieZone>       TieZones          { get; } = new List<RebarTieZone>();
        public List<RebarBendData>      BendSchedule      { get; } = new List<RebarBendData>();
        public RebarScheduleSummary     ScheduleSummary   { get; } = new RebarScheduleSummary();

        /// <summary>True once Phase 2 has fully populated this payload.</summary>
        public bool IsComplete { get; set; }

        /// <summary>Nominal concrete cover read from column parameters (mm).</summary>
        public double NominalCoverMm    { get; set; } = 50.0;

        /// <summary>Sum of all bend schedule weights — matches grand total on R1 sheet.</summary>
        public double TotalRebarWeightKg =>
            BendSchedule.Sum(r => r.TotalWeightKg);

        public string TotalWeightFormatted => $"{TotalRebarWeightKg:F2} kg";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EXTENSION PROPERTY ON PrecastColumnData  (via a holder pattern)
    // ─────────────────────────────────────────────────────────────────────────
    //  C# doesn't allow true extension properties on a type we own, so we add
    //  the field directly.  Uncomment and add this to PrecastColumnData.cs:
    //
    //      public RebarPayload Rebar { get; set; }
    //
    //  The analyser calls:
    //      colData.Rebar = RebarCageAnalyzer.Analyze(doc, colData);
}