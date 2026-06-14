// =============================================================================
//  StructAuto Detailing — Phase 0: Domain Model
//  File:    Models/PrecastColumnData.cs
//  Purpose: Pure C# data objects that represent a parsed precast column.
//           Zero Revit API references — fully testable in isolation.
//
//  Unit convention (IMPORTANT):
//    All dimensional fields whose names end in "Ft" store values in Revit
//    internal units (decimal feet, 1 ft = 304.8 mm).
//    All fields whose names end in "Mm" store millimetres for display/output.
//    Never mix the two in arithmetic — use UnitConverter helpers at boundaries.
// =============================================================================

using System;
using System.Collections.Generic;

namespace StructAutoDetailing.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  COORDINATE TRIPLET  (simple replacement for XYZ so the model layer
    //  has zero Revit references — converted back to XYZ in the parser)
    // ─────────────────────────────────────────────────────────────────────────
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        // Basic arithmetic helpers used in sheet-placement math
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 v, double s) => new Vec3(v.X * s, v.Y * s, v.Z * s);
        public static Vec3 operator *(double s, Vec3 v) => v * s;

        public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized  => Magnitude < 1e-9 ? this : this * (1.0 / Magnitude);

        public double Dot(Vec3 other)  => X * other.X + Y * other.Y + Z * other.Z;
        public Vec3   Cross(Vec3 other) => new Vec3(
            Y * other.Z - Z * other.Y,
            Z * other.X - X * other.Z,
            X * other.Y - Y * other.X);

        public double AngleTo(Vec3 other)
        {
            double denom = Magnitude * other.Magnitude;
            if (denom < 1e-12) return 0.0;
            double cos = Math.Max(-1.0, Math.Min(1.0, Dot(other) / denom));
            return Math.Acos(cos) * (180.0 / Math.PI); // degrees
        }

        public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LOCAL BOUNDING BOX  (extents in the column's own coordinate system)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Axis-aligned bounding box expressed in the column's LOCAL coordinate
    /// frame (Origin = base-centre of shaft, axes = localX/Y/Z from Transform).
    /// All values in Revit internal feet.
    /// </summary>
    public class LocalBoundingBox
    {
        public double MinX, MaxX;   // Width  extent  (localX direction)
        public double MinY, MaxY;   // Depth  extent  (localY direction)
        public double MinZ, MaxZ;   // Height extent  (localZ / gravity direction)

        public double WidthFt  => MaxX - MinX;
        public double DepthFt  => MaxY - MinY;
        public double HeightFt => MaxZ - MinZ;

        // Geometric centre in local space
        public Vec3 CentreLocal => new Vec3(
            (MinX + MaxX) / 2.0,
            (MinY + MaxY) / 2.0,
            (MinZ + MaxZ) / 2.0);

        // Smallest face-to-face horizontal dimension (used for section scale)
        public double MinHorizontalFt => Math.Min(WidthFt, DepthFt);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CORBEL DATA
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Which face of the column shaft the corbel projects from.</summary>
    public enum ColumnFace { North, South, East, West, Unknown }

    /// <summary>
    /// Describes one corbel (bracket) on the column.
    /// Geometric values are in local column coordinates (feet).
    /// </summary>
    public class CorbellData
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>Human-readable label: "Corbel-N", "Corbel-S", etc.</summary>
        public string Label { get; set; }

        /// <summary>Which face of the shaft this corbel protrudes from.</summary>
        public ColumnFace Face { get; set; } = ColumnFace.Unknown;

        // ── Geometry in LOCAL space (feet) ────────────────────────────────
        /// <summary>
        /// Centre point of the corbel's outer face, in WORLD coordinates.
        /// Used as the origin for corbel-face section views.
        /// </summary>
        public Vec3 OuterFaceCentreWorld { get; set; }

        /// <summary>
        /// Bottom of the corbel above the column base (local Z). Feet.
        /// </summary>
        public double BaseHeightFt { get; set; }

        /// <summary>
        /// Top of the corbel above the column base (local Z). Feet.
        /// </summary>
        public double TopHeightFt { get; set; }

        /// <summary>
        /// How far the corbel protrudes beyond the shaft face (local Y or X). Feet.
        /// Derived from: localBBox Y (or X) overhang beyond shaft nominal dimension.
        /// </summary>
        public double ProjectionFt { get; set; }

        /// <summary>Corbel width parallel to shaft face. Feet.</summary>
        public double WidthFt { get; set; }

        // ── Convenience display properties ────────────────────────────────
        public double HeightFt        => TopHeightFt - BaseHeightFt;
        public double ProjectionMm    => ProjectionFt * 304.8;
        public double WidthMm         => WidthFt * 304.8;
        public double HeightMm        => HeightFt * 304.8;
        public double BaseHeightMm    => BaseHeightFt * 304.8;

        // ── Bearing surface: used to place dowel-bar dimensions ────────────
        /// <summary>
        /// True bearing surface area in mm² (Width × Projection).
        /// Used on Formwork sheet to annotate bearing capacity note.
        /// </summary>
        public double BearingAreaMm2  => WidthMm * ProjectionMm;

        public override string ToString() =>
            $"Corbel [{Face}] H={HeightMm:F0}mm Proj={ProjectionMm:F0}mm W={WidthMm:F0}mm @ Z={BaseHeightMm:F0}mm";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EMBED DATA  (lifters, corrugated sleeves, plates, dowels)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classification of embedded hardware found inside or on the column solid.
    /// Drives both the Formwork sheet symbol legend and VG override logic.
    /// </summary>
    public enum EmbedType
    {
        Unknown,
        CorrugatedSleeve,       // CMS1 (60 Ø), CMS2 (50 Ø) — grout tube connections
        Lifter,                 // L2 — 5T capacity loop lifters
        DowelBar,               // T25 protruding rebars at corbel top/bottom
        EmbeddedPlate,          // Steel face plates (if present)
        GroutTube,              // Internal grout injection tubes
        PrestrandAnchor         // 15.2Ø strand anchor plate
    }

    /// <summary>
    /// Describes one embedded element found within the column's bounding volume.
    /// </summary>
    public class EmbedData
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>
        /// The Revit ElementId (stored as integer to keep model layer API-free).
        /// Resolve back to ElementId in the View/Annotation phases.
        /// </summary>
        public int RevitElementId { get; set; }

        /// <summary>Original Revit family name (used for classification).</summary>
        public string FamilyName { get; set; }

        /// <summary>Original Revit family symbol (type) name.</summary>
        public string TypeName { get; set; }

        /// <summary>Structured classification derived from family name matching.</summary>
        public EmbedType EmbedType { get; set; } = EmbedType.Unknown;

        // ── Drawing notation ──────────────────────────────────────────────
        /// <summary>
        /// Mark/tag to show on the Formwork drawing (e.g. "CMS1", "L2").
        /// Populated from a shared parameter "Embed_DrawingMark", or
        /// auto-assigned from EmbedType + index during parsing.
        /// </summary>
        public string DrawingMark { get; set; }

        /// <summary>
        /// Human-readable description for the legend table on the sheet.
        /// E.g. "60 DIA CORRUGATED METAL SLEEVE".
        /// </summary>
        public string Description { get; set; }

        /// <summary>Quantity on this element (default 1; set higher for grouped families).</summary>
        public int Quantity { get; set; } = 1;

        // ── Position in LOCAL column coordinates (feet) ───────────────────
        /// <summary>
        /// Centroid of the embed in LOCAL column space (Origin = column base centre).
        /// Used to place annotation leaders and dimension witness lines.
        /// </summary>
        public Vec3 CentroidLocal { get; set; }

        /// <summary>Centroid in WORLD coordinates (for Viewport.Create placement).</summary>
        public Vec3 CentroidWorld { get; set; }

        // ── Size / nominal parameters ──────────────────────────────────────
        /// <summary>
        /// Nominal diameter in mm (for sleeves) or capacity in tonnes (for lifters).
        /// Read from type parameter "Nominal_Size_mm" or "Capacity_T".
        /// </summary>
        public double NominalSizeMm { get; set; }

        /// <summary>Embedment depth (sleeve/anchor depth into concrete). Feet.</summary>
        public double EmbedDepthFt { get; set; }

        // ── Orientation ───────────────────────────────────────────────────
        /// <summary>
        /// Unit vector along the embed's primary axis in LOCAL column space.
        /// For a vertical sleeve: (0,0,1). For a corbel dowel: typically (0,1,0).
        /// Used to determine if the embed appears as a dot (plan) or bar (elevation).
        /// </summary>
        public Vec3 AxisLocal { get; set; }

        /// <summary>
        /// Which face of the column this embed exits from (null for fully internal).
        /// </summary>
        public ColumnFace? ExitFace { get; set; }

        // ── Annotation helpers ─────────────────────────────────────────────
        /// <summary>
        /// True if this embed protrudes above the column top and must be shown
        /// in the elevation crop (e.g. protruding dowels, lifter loops).
        /// </summary>
        public bool ProtrudesAboveTop { get; set; }

        /// <summary>
        /// Protrusion length above column top face. Feet.
        /// </summary>
        public double ProtrusionLengthFt { get; set; }

        public double ProtrusionLengthMm => ProtrusionLengthFt * 304.8;
        public double CentroidHeightMm   => CentroidLocal.Z * 304.8;

        public override string ToString() =>
            $"[{EmbedType}] Mark={DrawingMark} @ LocalZ={CentroidHeightMm:F0}mm Ø/Cap={NominalSizeMm:F0}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  COLUMN ORIENTATION STATE  (validation result from Phase 1A)
    // ─────────────────────────────────────────────────────────────────────────

    public enum ColumnOrientationStatus
    {
        Plumb,          // localZ ≈ WorldZ — fully supported by engine
        SlightlyTilted, // < 2° off plumb — engine proceeds with a warning
        Inclined,       // > 2° off plumb — engine halts, requires manual detailing
        Horizontal      // Column is lying flat (transport/staging model)
    }

    /// <summary>
    /// Stores the validated local axis system extracted from the FamilyInstance
    /// Transform in Phase 1A. Immutable after construction.
    /// </summary>
    public class ColumnOrientation
    {
        /// <summary>Local X axis in world coordinates (column's "width" direction).</summary>
        public Vec3 AxisX { get; }

        /// <summary>Local Y axis in world coordinates (column's "depth" direction).</summary>
        public Vec3 AxisY { get; }

        /// <summary>Local Z axis in world coordinates (gravity / column height direction).</summary>
        public Vec3 AxisZ { get; }

        /// <summary>Angle in degrees between localZ and World Z (0,0,1).</summary>
        public double TiltAngleDegrees { get; }

        public ColumnOrientationStatus Status { get; }

        // Rotation of the column plan about World Z axis (0° = aligned to World X).
        // Used to rotate dimension lines into the correct 2D view orientation.
        public double PlanRotationDegrees { get; }

        public ColumnOrientation(Vec3 axisX, Vec3 axisY, Vec3 axisZ, double tiltDeg, double planRotDeg)
        {
            AxisX = axisX.Normalized;
            AxisY = axisY.Normalized;
            AxisZ = axisZ.Normalized;
            TiltAngleDegrees = tiltDeg;
            PlanRotationDegrees = planRotDeg;

            if      (tiltDeg < 0.5)  Status = ColumnOrientationStatus.Plumb;
            else if (tiltDeg < 2.0)  Status = ColumnOrientationStatus.SlightlyTilted;
            else if (tiltDeg < 85.0) Status = ColumnOrientationStatus.Inclined;
            else                     Status = ColumnOrientationStatus.Horizontal;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MAIN DATA CARRIER  (Phase 0 output / all subsequent phases read/write)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Central data object for one precast column element.
    /// Populated progressively: Phase 1 fills geometry, Phase 2 fills rebar,
    /// Phase 3+ adds generated ViewIds.
    ///
    /// THREAD SAFETY: not thread-safe. All population must occur within a
    /// single Revit API transaction context.
    /// </summary>
    public class PrecastColumnData
    {
        // ── Identity ──────────────────────────────────────────────────────
        /// <summary>Revit ElementId (as int for model-layer isolation).</summary>
        public int RevitElementId { get; set; }

        /// <summary>
        /// Element mark / drawing number. Read from "Mark" built-in parameter.
        /// Falls back to "SB-FPC1-XX" pattern if not set.
        /// e.g. "SB-FPC1-01"
        /// </summary>
        public string ElementMark { get; set; }

        /// <summary>
        /// Revit element type name (Family Symbol name).
        /// e.g. "SB-FPC1-01 - Precast Column 750x750"
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>Project name read from ProjectInformation.</summary>
        public string ProjectName { get; set; }

        // ── Orientation (Phase 1A) ─────────────────────────────────────────
        /// <summary>
        /// Validated axis system. NULL until Phase 1A completes.
        /// All geometry in this class is expressed in the LOCAL frame defined here.
        /// </summary>
        public ColumnOrientation Orientation { get; set; }

        // ── Global position ────────────────────────────────────────────────
        /// <summary>
        /// World-coordinate position of the BASE CENTRE of the column shaft.
        /// (Lowest point of the shaft solid, at shaft centroid XY.)
        /// </summary>
        public Vec3 BaseCentreWorld { get; set; }

        /// <summary>
        /// World-coordinate position of the TOP CENTRE of the column shaft
        /// (at the top of the shaft, before any protruding stubs or lifters).
        /// </summary>
        public Vec3 TopCentreWorld { get; set; }

        // ── Local bounding box (Phase 1B) ──────────────────────────────────
        /// <summary>
        /// Tight local bounding box around the full concrete solid
        /// (including corbels). NULL until Phase 1B completes.
        /// </summary>
        public LocalBoundingBox LocalBBox { get; set; }

        // ── Shaft nominal dimensions (Phase 1B) ───────────────────────────
        /// <summary>
        /// Nominal shaft width (localX direction). Feet.
        /// For a 750×750 column: 750 / 304.8 = 2.4606 ft.
        /// </summary>
        public double ShaftWidthFt  { get; set; }

        /// <summary>Nominal shaft depth (localY direction). Feet.</summary>
        public double ShaftDepthFt  { get; set; }

        /// <summary>
        /// Total height of the concrete solid including corbels and any stubs.
        /// Does NOT include protruding rebar or lifters above the top face. Feet.
        /// </summary>
        public double TotalHeightFt { get; set; }

        // ── Elevation datums (Phase 1B) ────────────────────────────────────
        /// <summary>
        /// Absolute elevation (World Z) of the bottom face of the concrete solid.
        /// Formatted to match drawing EL annotations, e.g. "EL 4.350 M".
        /// </summary>
        public double BaseElevationM { get; set; }

        /// <summary>Absolute elevation of the top face of the concrete solid.</summary>
        public double TopElevationM  { get; set; }

        // ── Concrete parameters ────────────────────────────────────────────
        public int ConcreteGrade28Day { get; set; } = 60; // M60 default from notes
        public int ConcreteGradeDemould { get; set; } = 40;

        // ── Concrete volume & weight (Phase 1B) ───────────────────────────
        /// <summary>Net concrete volume in cubic metres (from solid computation).</summary>
        public double VolumeM3 { get; set; }

        /// <summary>Estimated element weight in tonnes (Volume × 2.5 T/m³ default).</summary>
        public double WeightTonnes => VolumeM3 * 2.5;

        // ── Corbels (Phase 1C) ─────────────────────────────────────────────
        /// <summary>
        /// True if any Y-axis or X-axis overhang beyond shaft nominal dimensions
        /// exceeds the 5mm detection threshold.
        /// </summary>
        public bool HasCorbels { get; set; }

        /// <summary>All detected corbels. Ordered by ascending BaseHeightFt.</summary>
        public List<CorbellData> Corbels { get; set; } = new List<CorbellData>();

        /// <summary>
        /// Maximum corbel projection across all corbels, in feet.
        /// Used by CreateSurgicalSection to set the farClip offset.
        /// </summary>
        public double MaxCorbellProjectionFt =>
            Corbels.Count > 0
                ? Corbels.ConvertAll(c => c.ProjectionFt).Max()
                : 0.0;

        // ── Embeds (Phase 1D) ──────────────────────────────────────────────
        public List<EmbedData> CorrugatedSleeves  { get; set; } = new List<EmbedData>();
        public List<EmbedData> Lifters            { get; set; } = new List<EmbedData>();
        public List<EmbedData> DowelBars          { get; set; } = new List<EmbedData>();
        public List<EmbedData> EmbeddedPlates     { get; set; } = new List<EmbedData>();
        public List<EmbedData> UnclassifiedEmbeds { get; set; } = new List<EmbedData>();

        /// <summary>Flat list of all embeds regardless of type. Populated lazily.</summary>
        public IEnumerable<EmbedData> AllEmbeds
        {
            get
            {
                foreach (var e in CorrugatedSleeves)  yield return e;
                foreach (var e in Lifters)            yield return e;
                foreach (var e in DowelBars)          yield return e;
                foreach (var e in EmbeddedPlates)     yield return e;
                foreach (var e in UnclassifiedEmbeds) yield return e;
            }
        }

        // ── Rebar data (Phase 2 — populated by RebarCageAnalyzer) ────────
        // Assign after calling: colData.Rebar = RebarCageAnalyzer.Analyze(doc, colData);
        // Always null-check before accessing: if (colData.Rebar?.IsComplete == true)
        public RebarPayload Rebar { get; set; }

        // ── Generated Revit view IDs (Phase 3+ — as ints) ─────────────────
        public int ViewId_FrontElevation  { get; set; } = -1;
        public int ViewId_SideElevation   { get; set; } = -1;
        public int ViewId_PlanSection     { get; set; } = -1;
        public int ViewId_CutA            { get; set; } = -1;
        public int ViewId_CutB            { get; set; } = -1;
        public int ViewId_CutC            { get; set; } = -1;
        public int SheetId_Formwork       { get; set; } = -1;
        public int SheetId_Rebar          { get; set; } = -1;
        public int SheetId_Schedule       { get; set; } = -1;

        // ── Parse state / diagnostics ──────────────────────────────────────
        /// <summary>Warnings accumulated during parsing (non-fatal).</summary>
        public List<string> ParseWarnings { get; } = new List<string>();

        /// <summary>True once Phase 1 (geometry parse) is complete.</summary>
        public bool GeometryParsed { get; set; }

        // ── Convenience display helpers ────────────────────────────────────
        public double ShaftWidthMm  => ShaftWidthFt  * 304.8;
        public double ShaftDepthMm  => ShaftDepthFt  * 304.8;
        public double TotalHeightMm => TotalHeightFt * 304.8;

        /// <summary>Formats an elevation value (World Z in feet) as "EL X.XXX M".</summary>
        public static string FormatElevationLabel(double worldZFt) =>
            $"EL {worldZFt * 0.3048:F3} M";

        public override string ToString() =>
            $"[PrecastColumnData] Mark={ElementMark} " +
            $"{ShaftWidthMm:F0}×{ShaftDepthMm:F0}×{TotalHeightMm:F0}mm " +
            $"Corbels={Corbels.Count} Sleeves={CorrugatedSleeves.Count} " +
            $"Lifters={Lifters.Count} Parsed={GeometryParsed}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CUSTOM EXCEPTION
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Thrown when the geometry parser encounters a condition it cannot recover
    /// from (inclined column, null solid, no geometry found, etc.).
    /// The Execute method catches this and shows it as a Revit TaskDialog.
    /// </summary>
    public class PrecastEngineException : Exception
    {
        public PrecastEngineException(string message) : base(message) { }
        public PrecastEngineException(string message, Exception inner) : base(message, inner) { }
    }
}