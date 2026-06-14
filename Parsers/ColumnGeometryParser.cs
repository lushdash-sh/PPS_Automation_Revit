// =============================================================================
//  StructAuto Detailing — Phase 1: Geometry Parser
//  File:    Parsers/ColumnGeometryParser.cs
//
//  Responsibilities:
//    1A  Extract host Transform → local axis system → ColumnOrientation
//    1B  Iterate solid geometry → compute LOCAL bounding box → shaft dims
//    1C  Detect corbels via Y/X-axis overhang slicing
//    1D  BoundingBoxIntersectsFilter → classify embedded FamilyInstances
//
//  Design contract:
//    • The public entry point is a single static method:
//        PrecastColumnData Parse(Document doc, ElementId columnId)
//    • All Revit API calls are confined to this file. The Models namespace
//      has no Revit references and stays independently testable.
//    • All internal conversions from Revit feet → mm happen here at the
//      boundary; everything stored in PrecastColumnData uses ft (internal).
//
//  Revit API version:  2027  (.NET Framework 4.8)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using StructAutoDetailing.Models;

namespace StructAutoDetailing.Parsers
{
    /// <summary>
    /// Parses a selected precast column FamilyInstance and returns a fully
    /// populated <see cref="PrecastColumnData"/> ready for view generation.
    /// Call only inside a valid Revit API context (IExternalCommand.Execute).
    /// </summary>
    public static class ColumnGeometryParser
    {
        // ─────────────────────────────────────────────────────────────────────
        //  CONSTANTS  (all in Revit internal feet unless noted)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Minimum Y- (or X-) axis overhang beyond nominal shaft dimension that
        /// is treated as a corbel, not a modelling tolerance. = 5 mm in feet.
        /// </summary>
        private const double CORBEL_DETECTION_THRESHOLD_FT = 5.0 / 304.8;

        /// <summary>
        /// Padding added around the column's world-space bounding box when
        /// building the BoundingBoxIntersectsFilter for embed detection. = 50 mm.
        /// </summary>
        private const double EMBED_SEARCH_PADDING_FT = 50.0 / 304.8;

        /// <summary>
        /// Maximum tilt angle (degrees) beyond which the engine refuses to proceed.
        /// Corresponds to ColumnOrientationStatus.Inclined (> 2°).
        /// </summary>
        private const double MAX_TILT_ANGLE_DEGREES = 2.0;

        /// <summary>
        /// Number of horizontal slices used during corbel base/top Z detection.
        /// Higher = more accurate; 50 is sufficient for typical precast columns.
        /// </summary>
        private const int CORBEL_SLICE_COUNT = 50;

        /// <summary>Concrete density in T/m³ for element weight estimate.</summary>
        private const double CONCRETE_DENSITY_T_PER_M3 = 2.5;

        // ─────────────────────────────────────────────────────────────────────
        //  PUBLIC ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full Phase 1 parse. Must be called inside a Revit transaction context.
        /// </summary>
        /// <param name="doc">Active Revit document.</param>
        /// <param name="columnId">ElementId of the selected precast column.</param>
        /// <returns>Populated <see cref="PrecastColumnData"/> (geometry only).</returns>
        /// <exception cref="PrecastEngineException">
        /// Thrown for non-recoverable conditions (wrong element type, inclined column,
        /// no geometry found). Catch in Execute() and show to user.
        /// </exception>
        public static PrecastColumnData Parse(Document doc, ElementId columnId)
        {
            if (doc    == null) throw new ArgumentNullException(nameof(doc));
            if (columnId == null || columnId == ElementId.InvalidElementId)
                throw new PrecastEngineException("Invalid ElementId passed to ColumnGeometryParser.");

            // ── Guard: must be a FamilyInstance ───────────────────────────
            Element rawElement = doc.GetElement(columnId);
            if (rawElement == null)
                throw new PrecastEngineException($"Element {columnId} not found in document.");

            FamilyInstance colInstance = rawElement as FamilyInstance;
            if (colInstance == null)
                throw new PrecastEngineException(
                    $"Selected element '{rawElement.Name}' is not a FamilyInstance. " +
                    "Please select a Precast Column family instance.");

            // ── Create output object ───────────────────────────────────────
            var colData = new PrecastColumnData
            {
                RevitElementId = (int)columnId.Value,
                TypeName       = colInstance.Symbol?.Name ?? "Unknown",
                ProjectName    = doc.ProjectInformation?.Name ?? string.Empty
            };

            // Read element mark (Revit built-in "ALL_MODEL_MARK")
            Parameter markParam = colInstance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            colData.ElementMark = markParam?.AsString()?.Trim() ?? $"FPC-{columnId.Value}";

            // ── Phase 1A: Orientation ──────────────────────────────────────
            ParseOrientation(colInstance, colData);

            // ── Guard: reject inclined columns ────────────────────────────
            if (colData.Orientation.Status == ColumnOrientationStatus.Inclined ||
                colData.Orientation.Status == ColumnOrientationStatus.Horizontal)
            {
                throw new PrecastEngineException(
                    $"Column '{colData.ElementMark}' is tilted {colData.Orientation.TiltAngleDegrees:F1}° " +
                    $"from vertical. StructAuto requires plumb columns (≤ {MAX_TILT_ANGLE_DEGREES}°). " +
                    "Please detail this element manually.");
            }

            if (colData.Orientation.Status == ColumnOrientationStatus.SlightlyTilted)
                colData.ParseWarnings.Add(
                    $"Column is slightly tilted ({colData.Orientation.TiltAngleDegrees:F2}°). " +
                    "Proceeding, but verify elevation crop alignment manually.");

            // ── Phase 1B: Local bounding box + shaft dimensions ────────────
            Solid columnSolid = ParseSolidGeometry(colInstance, colData, doc);

            // ── Phase 1C: Corbel detection ─────────────────────────────────
            ParseCorbels(columnSolid, colData);

            // ── Phase 1D: Embedded element classification ──────────────────
            ParseEmbeds(doc, colInstance, colData);

            // ── Mark complete ──────────────────────────────────────────────
            colData.GeometryParsed = true;
            return colData;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 1A — ORIENTATION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the FamilyInstance host Transform to determine the column's
        /// true local coordinate axes in world space.
        /// </summary>
        private static void ParseOrientation(FamilyInstance colInstance, PrecastColumnData colData)
        {
            Transform t = colInstance.GetTransform();

            // Revit Transform.BasisX/Y/Z are already unit vectors
            Vec3 axisX = ToVec3(t.BasisX);
            Vec3 axisY = ToVec3(t.BasisY);
            Vec3 axisZ = ToVec3(t.BasisZ);

            // ── Tilt: angle between localZ and world vertical (0,0,1) ──────
            Vec3 worldZ = new Vec3(0, 0, 1);
            double tiltDeg = axisZ.AngleTo(worldZ);

            // ── Plan rotation about world Z ────────────────────────────────
            // Project localX onto the world XY plane, measure angle from world X.
            Vec3 localXFlat = new Vec3(axisX.X, axisX.Y, 0).Normalized;
            double planRotDeg = Math.Atan2(localXFlat.Y, localXFlat.X) * (180.0 / Math.PI);

            colData.Orientation = new ColumnOrientation(axisX, axisY, axisZ, tiltDeg, planRotDeg);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 1B — SOLID GEOMETRY & LOCAL BOUNDING BOX
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the largest solid from the column geometry, computes a tight
        /// LOCAL bounding box, and populates shaft dimensions and elevation datums.
        /// </summary>
        /// <returns>The concrete Solid (reused by Phase 1C corbel slicer).</returns>
        private static Solid ParseSolidGeometry(
            FamilyInstance colInstance,
            PrecastColumnData colData,
            Document doc)
        {
            // ── Retrieve geometry at Fine detail level ─────────────────────
            Options geomOpts = new Options
            {
                DetailLevel           = ViewDetailLevel.Fine,
                ComputeReferences     = true,
                IncludeNonVisibleObjects = false
            };

            GeometryElement geomElement = colInstance.get_Geometry(geomOpts);
            if (geomElement == null)
                throw new PrecastEngineException(
                    $"Column '{colData.ElementMark}': get_Geometry() returned null. " +
                    "Ensure the family has valid 3D geometry.");

            Solid columnSolid = ExtractLargestSolid(geomElement, colInstance);
            if (columnSolid == null || columnSolid.Volume < 1e-6)
                throw new PrecastEngineException(
                    $"Column '{colData.ElementMark}': no valid solid found in geometry. " +
                    "Check family for empty or void-only geometry.");

            // ── Build the world→local inverse transform ────────────────────
            // The host Transform maps LOCAL → WORLD.
            // Its inverse maps WORLD → LOCAL (what we need for bbox computation).
            Transform worldToLocal = colInstance.GetTransform().Inverse;

            // ── Iterate all edge tessellation points in LOCAL space ─────────
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (Face face in columnSolid.Faces)
            {
                // EdgeLoops give us the full face boundary — better precision than
                // face.Tessellate() which may skip concave corners.
                foreach (EdgeArray loop in face.EdgeLoops)
                {
                    foreach (Edge edge in loop)
                    {
                        // Tessellate() returns points along the edge at rendering resolution
                        foreach (XYZ worldPt in edge.Tessellate())
                        {
                            XYZ localPt = worldToLocal.OfPoint(worldPt);
                            if (localPt.X < minX) minX = localPt.X;
                            if (localPt.X > maxX) maxX = localPt.X;
                            if (localPt.Y < minY) minY = localPt.Y;
                            if (localPt.Y > maxY) maxY = localPt.Y;
                            if (localPt.Z < minZ) minZ = localPt.Z;
                            if (localPt.Z > maxZ) maxZ = localPt.Z;
                        }
                    }
                }
            }

            // Safety check — degenerate bbox indicates tessellation produced no points
            if (maxX - minX < 1e-6 || maxY - minY < 1e-6 || maxZ - minZ < 1e-6)
                throw new PrecastEngineException(
                    $"Column '{colData.ElementMark}': local bounding box is degenerate " +
                    $"({(maxX-minX)*304.8:F1} × {(maxY-minY)*304.8:F1} × {(maxZ-minZ)*304.8:F1} mm). " +
                    "Check family geometry.");

            // ── Populate LocalBoundingBox ───────────────────────────────────
            colData.LocalBBox = new LocalBoundingBox
            {
                MinX = minX, MaxX = maxX,
                MinY = minY, MaxY = maxY,
                MinZ = minZ, MaxZ = maxZ
            };

            // ── Populate column geometry fields ────────────────────────────
            colData.TotalHeightFt = maxZ - minZ;

            // ── Base & Top centre in WORLD coordinates ─────────────────────
            // Base centre: XY midpoint at bottom face, back-transformed to world
            Transform localToWorld = colInstance.GetTransform();
            XYZ baseCentreLocal = new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, minZ);
            XYZ topCentreLocal  = new XYZ((minX + maxX) / 2.0, (minY + maxY) / 2.0, maxZ);

            colData.BaseCentreWorld = ToVec3(localToWorld.OfPoint(baseCentreLocal));
            colData.TopCentreWorld  = ToVec3(localToWorld.OfPoint(topCentreLocal));

            // ── Absolute elevations ────────────────────────────────────────
            // World Z of base and top in metres (World Z is in feet internally)
            colData.BaseElevationM = colData.BaseCentreWorld.Z * 0.3048;
            colData.TopElevationM  = colData.TopCentreWorld.Z  * 0.3048;

            // ── Volume & weight ────────────────────────────────────────────
            // Revit Volume is in cubic feet; 1 ft³ = 0.0283168 m³
            colData.VolumeM3 = columnSolid.Volume * 0.0283168;

            // ── Infer NOMINAL shaft dimensions ────────────────────────────
            // Strategy: the shaft is the tightest horizontal section. Corbels
            // cause the bounding box to exceed the shaft. We read the shaft dims
            // from either:
            //   (a) a shared parameter "Precast_Shaft_Width_mm" / "_Depth_mm", or
            //   (b) the minimum horizontal cross-section dimension found by the
            //       corbel slicer (populated below in Phase 1C as a side-effect).
            //
            // For now, seed with the full bbox extents. Phase 1C will refine these
            // down to the pure shaft dims after corbel detection.
            colData.ShaftWidthFt = maxX - minX;
            colData.ShaftDepthFt = maxY - minY;

            return columnSolid;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 1C — CORBEL DETECTION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Slices the column solid horizontally at regular Z intervals.
        /// At each slice, measures the XY footprint in LOCAL space.
        /// A sudden increase in Y (or X) extent beyond a threshold signals a corbel.
        ///
        /// Side-effect: refines <c>colData.ShaftWidthFt</c> and <c>ShaftDepthFt</c>
        /// to the minimum cross-section (the pure shaft dims, excluding corbels).
        /// </summary>
        private static void ParseCorbels(Solid columnSolid, PrecastColumnData colData)
        {
            Transform worldToLocal = GetWorldToLocalTransform(colData);

            double minZ = colData.LocalBBox.MinZ;
            double maxZ = colData.LocalBBox.MaxZ;
            double totalH = maxZ - minZ;
            double sliceStep = totalH / CORBEL_SLICE_COUNT;

            // For each slice: store min/max of X and Y extents in local space
            var sliceXExtents = new List<(double z, double xMin, double xMax)>();
            var sliceYExtents = new List<(double z, double yMin, double yMax)>();

            for (int i = 0; i <= CORBEL_SLICE_COUNT; i++)
            {
                double sliceZ = minZ + i * sliceStep;

                // Sample the solid cross-section: find all edges that cross this Z level
                double sXMin = double.MaxValue, sXMax = double.MinValue;
                double sYMin = double.MaxValue, sYMax = double.MinValue;
                bool foundAnything = false;

                foreach (Edge edge in columnSolid.Edges)
                {
                    IList<XYZ> pts = edge.Tessellate();
                    for (int j = 0; j < pts.Count - 1; j++)
                    {
                        XYZ pA = pts[j], pB = pts[j + 1];
                        // Transform both endpoints to LOCAL space
                        XYZ lA = worldToLocal.OfPoint(pA);
                        XYZ lB = worldToLocal.OfPoint(pB);

                        // Check if this segment straddles the current slice Z
                        double zA = lA.Z, zB = lB.Z;
                        if ((zA <= sliceZ && zB >= sliceZ) ||
                            (zB <= sliceZ && zA >= sliceZ))
                        {
                            // Linear interpolation to find the exact crossing point
                            double t = Math.Abs(zB - zA) < 1e-9 ? 0.5 : (sliceZ - zA) / (zB - zA);
                            double crossX = lA.X + t * (lB.X - lA.X);
                            double crossY = lA.Y + t * (lB.Y - lA.Y);

                            if (crossX < sXMin) sXMin = crossX;
                            if (crossX > sXMax) sXMax = crossX;
                            if (crossY < sYMin) sYMin = crossY;
                            if (crossY > sYMax) sYMax = crossY;
                            foundAnything = true;
                        }
                    }
                }

                if (foundAnything)
                {
                    sliceXExtents.Add((sliceZ, sXMin, sXMax));
                    sliceYExtents.Add((sliceZ, sYMin, sYMax));
                }
            }

            if (sliceXExtents.Count == 0)
            {
                colData.ParseWarnings.Add("Corbel slicer found no cross-section intersections. Skipping corbel detection.");
                return;
            }

            // ── Find the MINIMUM footprint (pure shaft cross-section) ──────
            // The shaft zones are those where both X and Y extents are at their
            // narrowest (no corbel protrusion).
            double minXWidth = sliceXExtents.Min(s => s.xMax - s.xMin);
            double minYDepth = sliceYExtents.Min(s => s.yMax - s.yMin);

            // Refine shaft nominal dims
            colData.ShaftWidthFt = minXWidth;
            colData.ShaftDepthFt = minYDepth;

            // ── Detect corbel Z ranges ─────────────────────────────────────
            // A slice has a corbel if its Y extent exceeds shaft depth + threshold
            // OR if its X extent exceeds shaft width + threshold (for side corbels).

            double shaftHalfW = minXWidth / 2.0;
            double shaftHalfD = minYDepth / 2.0;

            // State machine: track entry/exit of "corbel active" state per face
            var corbelFaceStates = new Dictionary<ColumnFace, CorbelDetectionState>
            {
                { ColumnFace.North, new CorbelDetectionState() },
                { ColumnFace.South, new CorbelDetectionState() },
                { ColumnFace.East,  new CorbelDetectionState() },
                { ColumnFace.West,  new CorbelDetectionState() },
            };

            for (int i = 0; i < sliceYExtents.Count; i++)
            {
                double sliceZ = sliceYExtents[i].z;
                double yMin   = sliceYExtents[i].yMin;
                double yMax   = sliceYExtents[i].yMax;
                double xMin   = sliceXExtents[i].xMin;
                double xMax   = sliceXExtents[i].xMax;

                // Overhang in each direction
                double northOverhang = yMax - shaftHalfD;   // +Y = North
                double southOverhang = -(yMin + shaftHalfD); // -Y = South
                double eastOverhang  = xMax - shaftHalfW;   // +X = East
                double westOverhang  = -(xMin + shaftHalfW);// -X = West

                CheckCorbelTransition(corbelFaceStates[ColumnFace.North], sliceZ,
                    northOverhang, ColumnFace.North, colData);
                CheckCorbelTransition(corbelFaceStates[ColumnFace.South], sliceZ,
                    southOverhang, ColumnFace.South, colData);
                CheckCorbelTransition(corbelFaceStates[ColumnFace.East], sliceZ,
                    eastOverhang, ColumnFace.East, colData);
                CheckCorbelTransition(corbelFaceStates[ColumnFace.West], sliceZ,
                    westOverhang, ColumnFace.West, colData);
            }

            // Close any corbels that were still active at the top of the column
            // (shouldn't happen in practice, but defensive coding)
            foreach (var kvp in corbelFaceStates)
                if (kvp.Value.IsActive)
                    FinaliseCorbelFromState(kvp.Value, maxZ, colData, sliceXExtents, sliceYExtents);

            colData.HasCorbels = colData.Corbels.Count > 0;

            // Sort corbels bottom-up
            colData.Corbels.Sort((a, b) => a.BaseHeightFt.CompareTo(b.BaseHeightFt));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CORBEL DETECTION HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private class CorbelDetectionState
        {
            public bool   IsActive;
            public double StartZ;
            public double MaxOverhang;
            public double MaxProjection; // actual projection value at max
        }

        /// <summary>
        /// Drives the state machine for one face per slice.
        /// On rising edge (overhang crosses threshold): records corbel start Z.
        /// On falling edge (overhang drops below threshold): finalises the corbel.
        /// </summary>
        private static void CheckCorbelTransition(
            CorbelDetectionState state,
            double sliceZ,
            double overhang,
            ColumnFace face,
            PrecastColumnData colData)
        {
            bool projecting = overhang > CORBEL_DETECTION_THRESHOLD_FT;

            if (projecting && !state.IsActive)
            {
                // Rising edge: corbel begins here
                state.IsActive     = true;
                state.StartZ       = sliceZ;
                state.MaxOverhang  = overhang;
                state.MaxProjection = overhang;
            }
            else if (projecting && state.IsActive)
            {
                // Still inside corbel: track peak projection
                if (overhang > state.MaxProjection)
                    state.MaxProjection = overhang;
            }
            else if (!projecting && state.IsActive)
            {
                // Falling edge: corbel ends just before this slice
                // Build the CorbellData object
                var corbel = BuildCorbellData(face, state, sliceZ, colData);
                colData.Corbels.Add(corbel);
                state.IsActive = false;
                state.MaxProjection = 0;
            }
        }

        private static void FinaliseCorbelFromState(
            CorbelDetectionState state,
            double endZ,
            PrecastColumnData colData,
            List<(double z, double xMin, double xMax)> sliceXExtents,
            List<(double z, double yMin, double yMax)> sliceYExtents)
        {
            // This is called for any corbel still open at the top of the column
            // (an unusual but possible geometry — e.g. a corbel that extends to the top)
            // We treat the column top as the corbel top.
            // Face cannot be determined from state alone — mark as Unknown
            var corbel = BuildCorbellData(ColumnFace.Unknown, state, endZ, colData);
            colData.Corbels.Add(corbel);
            state.IsActive = false;
        }

        private static CorbellData BuildCorbellData(
            ColumnFace face,
            CorbelDetectionState state,
            double endZ,
            PrecastColumnData colData)
        {
            // Corbel height = Z at end of protrusion - Z at start
            double corbelBaseZ = state.StartZ;
            double corbelTopZ  = endZ;

            // Compute the world-space outer face centre
            // Outer face is at (shaft half dim + projection) in local space,
            // back-transformed to world coords.
            Transform localToWorld = GetLocalToWorldTransform(colData);
            double midZ = (corbelBaseZ + corbelTopZ) / 2.0;
            double shaftHalfDim = face == ColumnFace.North || face == ColumnFace.South
                ? colData.ShaftDepthFt / 2.0
                : colData.ShaftWidthFt / 2.0;

            // Face direction in local space
            double faceSign = (face == ColumnFace.North || face == ColumnFace.East) ? 1.0 : -1.0;
            double outerY = face == ColumnFace.North || face == ColumnFace.South
                ? faceSign * (shaftHalfDim + state.MaxProjection)
                : 0.0;
            double outerX = face == ColumnFace.East || face == ColumnFace.West
                ? faceSign * (shaftHalfDim + state.MaxProjection)
                : 0.0;

            XYZ outerFaceLocalPt = new XYZ(
                outerX,
                outerY,
                colData.LocalBBox.MinZ + midZ);

            XYZ outerFaceWorld = localToWorld.OfPoint(outerFaceLocalPt);

            // Corbel width: for North/South faces, corbel is in the X direction
            // (its width = shaft width until we have a tighter measurement).
            // Phase 1C uses the minimum-extent slices so this is approximate.
            // The corbel width will be refined if a shared parameter exists.
            double corbelWidth = face == ColumnFace.North || face == ColumnFace.South
                ? colData.ShaftWidthFt
                : colData.ShaftDepthFt;

            var corbel = new CorbellData
            {
                Label                = $"Corbel-{face}",
                Face                 = face,
                OuterFaceCentreWorld = ToVec3(outerFaceWorld),
                BaseHeightFt         = corbelBaseZ - colData.LocalBBox.MinZ,
                TopHeightFt          = corbelTopZ  - colData.LocalBBox.MinZ,
                ProjectionFt         = state.MaxProjection,
                WidthFt              = corbelWidth
            };

            return corbel;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PHASE 1D — EMBEDDED ELEMENT CLASSIFICATION
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds all FamilyInstances within the column's bounding volume and
        /// classifies them as sleeves, lifters, dowels, or plates.
        ///
        /// Classification priority order:
        ///   1. Shared parameter "Embed_Type" (explicit, preferred if set by family author)
        ///   2. Family name keyword matching (robust fallback)
        ///   3. UnclassifiedEmbeds (logged for review)
        /// </summary>
        private static void ParseEmbeds(
            Document doc,
            FamilyInstance colInstance,
            PrecastColumnData colData)
        {
            // ── Build the search outline in World coordinates ──────────────
            // Use the column's world-space bounding box padded by EMBED_SEARCH_PADDING_FT
            // on all sides to catch elements that are partly inside, partly outside.
            BoundingBoxXYZ worldBbox = colInstance.get_BoundingBox(null);
            if (worldBbox == null)
            {
                colData.ParseWarnings.Add("Could not obtain world bounding box for embed search. Skipping embed detection.");
                return;
            }

            XYZ bboxMin = worldBbox.Min - new XYZ(EMBED_SEARCH_PADDING_FT,
                                                   EMBED_SEARCH_PADDING_FT,
                                                   EMBED_SEARCH_PADDING_FT);
            XYZ bboxMax = worldBbox.Max + new XYZ(EMBED_SEARCH_PADDING_FT,
                                                   EMBED_SEARCH_PADDING_FT,
                                                   EMBED_SEARCH_PADDING_FT);

            Outline searchOutline = new Outline(bboxMin, bboxMax);
            BoundingBoxIntersectsFilter bboxFilter = new BoundingBoxIntersectsFilter(searchOutline);

            // ── Collect candidate FamilyInstances ──────────────────────────
            // Exclude the column itself by filtering out its own Id
            List<FamilyInstance> candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WherePasses(bboxFilter)
                .Cast<FamilyInstance>()
                .Where(fi => fi.Id != colInstance.Id)
                .ToList();

            // ── Build world→local transform for positioning ────────────────
            Transform worldToLocal = colInstance.GetTransform().Inverse;
            Transform localToWorld = colInstance.GetTransform();

            // ── Classify and populate ──────────────────────────────────────
            // Track auto-assigned drawing marks per type to ensure unique labels
            var markCounters = new Dictionary<EmbedType, int>();

            foreach (FamilyInstance fi in candidates)
            {
                // Get centroid in world space via bounding box midpoint
                BoundingBoxXYZ embedBbox = fi.get_BoundingBox(null);
                if (embedBbox == null) continue;

                XYZ centroidWorld = (embedBbox.Min + embedBbox.Max) / 2.0;
                XYZ centroidLocal = worldToLocal.OfPoint(centroidWorld);

                // Skip if centroid is outside the column solid's LOCAL bounding box
                // (the filter catches intersections, not containment — this removes
                //  elements that merely clip the search region but are not truly embedded)
                if (!IsInsideLocalBbox(centroidLocal, colData.LocalBBox, EMBED_SEARCH_PADDING_FT / 2.0))
                    continue;

                // ── Read family names ──────────────────────────────────────
                string familyName = fi.Symbol?.FamilyName ?? string.Empty;
                string typeName   = fi.Symbol?.Name       ?? string.Empty;
                string combined   = (familyName + " " + typeName).ToUpperInvariant();

                // ── Classify ───────────────────────────────────────────────
                EmbedType embedType = ClassifyBySharedParam(fi)
                                   ?? ClassifyByFamilyName(combined);

                // ── Build EmbedData ────────────────────────────────────────
                int embedSizeCount = 0;
                markCounters.TryGetValue(embedType, out embedSizeCount);
                markCounters[embedType] = embedSizeCount + 1;

                var embed = new EmbedData
                {
                    RevitElementId  = (int)fi.Id.Value,
                    FamilyName      = familyName,
                    TypeName        = typeName,
                    EmbedType       = embedType,
                    CentroidWorld   = ToVec3(centroidWorld),
                    CentroidLocal   = ToVec3(centroidLocal),
                    DrawingMark     = BuildDrawingMark(fi, embedType, markCounters[embedType]),
                    Description     = BuildDescription(fi, embedType, combined),
                    Quantity        = ReadQtyParameter(fi),
                    NominalSizeMm   = ReadNominalSizeMm(fi, combined),
                    EmbedDepthFt    = ReadEmbedDepth(fi, embedBbox, colData),
                    AxisLocal       = ReadEmbedAxis(fi, worldToLocal),
                    ExitFace        = DetermineExitFace(centroidLocal, colData),
                };

                // ── Protrusion above top ────────────────────────────────────
                double embedTopLocalZ = worldToLocal.OfPoint(embedBbox.Max).Z;
                double columnTopLocalZ = colData.LocalBBox.MaxZ;
                if (embedTopLocalZ > columnTopLocalZ + (2.0 / 304.8))
                {
                    embed.ProtrudesAboveTop    = true;
                    embed.ProtrusionLengthFt   = embedTopLocalZ - columnTopLocalZ;
                }

                // ── Route to typed list ────────────────────────────────────
                RouteEmbedToList(embed, colData);
            }

            // ── Post-process: warn if no lifters found ─────────────────────
            if (colData.Lifters.Count == 0)
                colData.ParseWarnings.Add(
                    "No lifter elements found within the column bounding volume. " +
                    "Verify that lifter families are placed inside the column family or as adjacent hosted elements.");

            if (colData.CorrugatedSleeves.Count == 0)
                colData.ParseWarnings.Add(
                    "No corrugated sleeve elements found. " +
                    "Verify sleeve families use a recognisable naming convention (see ClassifyByFamilyName).");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EMBED CLASSIFICATION HELPERS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads a shared parameter named "Embed_Type" from the family instance.
        /// Returns null if the parameter does not exist or is empty.
        ///
        /// Family authors should set this to one of:
        ///   "CorrugatedSleeve", "Lifter", "DowelBar", "EmbeddedPlate",
        ///   "GroutTube", "PrestrandAnchor"
        /// </summary>
        private static EmbedType? ClassifyBySharedParam(FamilyInstance fi)
        {
            Parameter p = fi.LookupParameter("Embed_Type");
            if (p == null || p.StorageType != StorageType.String) return null;

            string val = p.AsString()?.Trim();
            if (string.IsNullOrEmpty(val)) return null;

            if (Enum.TryParse<EmbedType>(val, ignoreCase: true, out EmbedType result))
                return result;

            return null;
        }

        /// <summary>
        /// Keyword-based classification using the combined family + type name string.
        /// Extend this dictionary as new family naming conventions are encountered.
        /// The keywords are evaluated in priority order (most specific first).
        /// </summary>
        private static EmbedType ClassifyByFamilyName(string upperCombined)
        {
            // ── Corrugated sleeves ─────────────────────────────────────────
            if (upperCombined.Contains("CMS")             ||
                upperCombined.Contains("CORRUGATED")      ||
                upperCombined.Contains("METAL SLEEVE")    ||
                upperCombined.Contains("GROUT SLEEVE")    ||
                upperCombined.Contains("SPLICE SLEEVE"))
                return EmbedType.CorrugatedSleeve;

            // ── Lifters ────────────────────────────────────────────────────
            if (upperCombined.Contains("LIFTER")          ||
                upperCombined.Contains("LIFTING LOOP")    ||
                upperCombined.Contains("LIFT INSERT")     ||
                upperCombined.Contains("ERECTION LOOP")   ||
                upperCombined.Contains("HALFEN")          ||
                upperCombined.Contains("PFEIFER"))
                return EmbedType.Lifter;

            // ── Dowel bars ─────────────────────────────────────────────────
            if (upperCombined.Contains("DOWEL")           ||
                upperCombined.Contains("PROTRUDING BAR")  ||
                upperCombined.Contains("STARTER BAR"))
                return EmbedType.DowelBar;

            // ── Grout tubes ────────────────────────────────────────────────
            if (upperCombined.Contains("GROUT TUBE")      ||
                upperCombined.Contains("VENT TUBE")       ||
                upperCombined.Contains("INJECTION"))
                return EmbedType.GroutTube;

            // ── Prestrand anchors ──────────────────────────────────────────
            if (upperCombined.Contains("STRAND")          ||
                upperCombined.Contains("PRESTRESS")       ||
                upperCombined.Contains("ANCHOR PLATE")    ||
                upperCombined.Contains("15.2"))
                return EmbedType.PrestrandAnchor;

            // ── Embedded plates ────────────────────────────────────────────
            if (upperCombined.Contains("EMBED PLATE")     ||
                upperCombined.Contains("EMBEDDED PLATE")  ||
                upperCombined.Contains("CAST-IN PLATE")   ||
                upperCombined.Contains("STEEL PLATE"))
                return EmbedType.EmbeddedPlate;

            return EmbedType.Unknown;
        }

        private static string BuildDrawingMark(FamilyInstance fi, EmbedType type, int counter)
        {
            // Prefer an explicit "Embed_DrawingMark" shared parameter
            Parameter p = fi.LookupParameter("Embed_DrawingMark");
            if (p != null && p.StorageType == StorageType.String)
            {
                string v = p.AsString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }

            // Auto-assign based on type and index
            switch (type)
            {
                case EmbedType.CorrugatedSleeve: return $"CMS{counter}";
                case EmbedType.Lifter:           return $"L{counter}";
                case EmbedType.DowelBar:         return $"DB{counter}";
                case EmbedType.EmbeddedPlate:    return $"EP{counter}";
                case EmbedType.GroutTube:        return $"GT{counter}";
                case EmbedType.PrestrandAnchor:  return $"PA{counter}";
                default:                         return $"EMB{counter}";
            }
        }

        private static string BuildDescription(FamilyInstance fi, EmbedType type, string upperCombined)
        {
            // Prefer a "Embed_Description" shared parameter
            Parameter p = fi.LookupParameter("Embed_Description");
            if (p != null && p.StorageType == StorageType.String)
            {
                string v = p.AsString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }

            // Infer from nominal size and type — mirrors the drawing legend exactly
            double sizeMm = ReadNominalSizeMm(fi, upperCombined);
            switch (type)
            {
                case EmbedType.CorrugatedSleeve:
                    return sizeMm > 0
                        ? $"{(int)sizeMm} DIA CORRUGATED METAL SLEEVE"
                        : "CORRUGATED METAL SLEEVE";
                case EmbedType.Lifter:
                    double cap = ReadCapacityTonnes(fi);
                    return cap > 0 ? $"LIFTER - {cap:F0} TON CAPACITY EACH" : "LIFTING LOOP";
                case EmbedType.DowelBar:
                    return $"T{(int)sizeMm} DOWEL BAR";
                case EmbedType.EmbeddedPlate:
                    return "EMBEDDED STEEL PLATE";
                case EmbedType.GroutTube:
                    return "GROUT INJECTION TUBE";
                case EmbedType.PrestrandAnchor:
                    return "PRESTRESS STRAND ANCHOR";
                default:
                    return fi.Symbol?.FamilyName ?? "UNCLASSIFIED EMBED";
            }
        }

        private static double ReadNominalSizeMm(FamilyInstance fi, string upperCombined)
        {
            // Try shared parameter first
            foreach (string pName in new[] { "Nominal_Size_mm", "Diameter_mm", "Bar_Diameter" })
            {
                Parameter p = fi.LookupParameter(pName);
                if (p != null && (p.StorageType == StorageType.Double || p.StorageType == StorageType.Integer))
                {
                    double v = p.StorageType == StorageType.Double
                        ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters)
                        : p.AsInteger();
                    if (v > 0) return v;
                }
            }

            // Fall back to keyword pattern: look for digits after "CMS", "T", "Ø" in name
            // e.g. "CMS1 60 DIA" → 60;  "T25 DOWEL" → 25
            System.Text.RegularExpressions.Match m;
            m = System.Text.RegularExpressions.Regex.Match(upperCombined, @"(\d+)\s*DIA");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double d1)) return d1;

            m = System.Text.RegularExpressions.Regex.Match(upperCombined, @"T(\d+)\s");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double d2)) return d2;

            m = System.Text.RegularExpressions.Regex.Match(upperCombined, @"[\u00D8\u00F8]?\s*(\d+)");
            if (m.Success && double.TryParse(m.Groups[1].Value, out double d3)) return d3;

            return 0;
        }

        private static double ReadCapacityTonnes(FamilyInstance fi)
        {
            foreach (string pName in new[] { "Capacity_T", "Lift_Capacity_T", "Load_Capacity" })
            {
                Parameter p = fi.LookupParameter(pName);
                if (p != null && p.StorageType == StorageType.Double)
                    return p.AsDouble(); // stored as-is (not in Revit ft)
                if (p != null && p.StorageType == StorageType.Integer)
                    return p.AsInteger();
            }
            return 0;
        }

        private static int ReadQtyParameter(FamilyInstance fi)
        {
            Parameter p = fi.get_Parameter(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            // Quantity is typically derived externally. Return 1 here;
            // the sheet legend counts by type across all embeds.
            return 1;
        }

        private static double ReadEmbedDepth(FamilyInstance fi, BoundingBoxXYZ embedBbox, PrecastColumnData colData)
        {
            // Embed depth = the Z-extent of the embed bounding box (in world space)
            // This is a reasonable approximation for sleeves and anchors.
            double bboxHeightFt = embedBbox.Max.Z - embedBbox.Min.Z;
            return bboxHeightFt;
        }

        private static Vec3 ReadEmbedAxis(FamilyInstance fi, Transform worldToLocal)
        {
            // The embed's principal axis is the Z axis of its own transform,
            // transformed into LOCAL column space.
            Transform fiT = fi.GetTransform();
            XYZ axisBasisZ = fiT.BasisZ;
            XYZ localAxis  = worldToLocal.OfVector(axisBasisZ);
            return ToVec3(localAxis).Normalized;
        }

        private static ColumnFace? DetermineExitFace(XYZ centroidLocal, PrecastColumnData colData)
        {
            // Determine which face the embed is closest to exiting from.
            // An embed whose centroid is within 25mm of a face is assigned that face.
            const double FACE_PROXIMITY_FT = 25.0 / 304.8;

            double halfW = colData.ShaftWidthFt / 2.0;
            double halfD = colData.ShaftDepthFt / 2.0;

            if (Math.Abs(centroidLocal.Y - halfD) < FACE_PROXIMITY_FT)  return ColumnFace.North;
            if (Math.Abs(centroidLocal.Y + halfD) < FACE_PROXIMITY_FT)  return ColumnFace.South;
            if (Math.Abs(centroidLocal.X - halfW) < FACE_PROXIMITY_FT)  return ColumnFace.East;
            if (Math.Abs(centroidLocal.X + halfW) < FACE_PROXIMITY_FT)  return ColumnFace.West;
            return null;
        }

        private static void RouteEmbedToList(EmbedData embed, PrecastColumnData colData)
        {
            switch (embed.EmbedType)
            {
                case EmbedType.CorrugatedSleeve:
                    colData.CorrugatedSleeves.Add(embed);  break;
                case EmbedType.Lifter:
                    colData.Lifters.Add(embed);            break;
                case EmbedType.DowelBar:
                    colData.DowelBars.Add(embed);          break;
                case EmbedType.EmbeddedPlate:
                    colData.EmbeddedPlates.Add(embed);     break;
                default:
                    colData.UnclassifiedEmbeds.Add(embed); break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GEOMETRY UTILITIES
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Traverses a GeometryElement (which may contain nested GeometryInstances)
        /// and returns the Solid with the largest volume — the concrete body.
        /// </summary>
        private static Solid ExtractLargestSolid(GeometryElement geomElement, FamilyInstance instance)
        {
            Solid largest = null;
            double largestVolume = 0;

            foreach (GeometryObject obj in geomElement)
            {
                Solid s = TrySolid(obj, instance.GetTransform());
                if (s != null && s.Volume > largestVolume)
                {
                    largest = s;
                    largestVolume = s.Volume;
                }

                // Drill into GeometryInstance (nested families)
                if (obj is GeometryInstance gi)
                {
                    foreach (GeometryObject innerObj in gi.GetInstanceGeometry())
                    {
                        Solid inner = TrySolid(innerObj, gi.Transform);
                        if (inner != null && inner.Volume > largestVolume)
                        {
                            largest = inner;
                            largestVolume = inner.Volume;
                        }
                    }
                }
            }
            return largest;
        }

        private static Solid TrySolid(GeometryObject obj, Transform transform)
        {
            if (obj is Solid solid && solid.Volume > 1e-9)
                return solid;
            return null;
        }

        /// <summary>
        /// Tests whether a LOCAL-space point is inside the given LocalBoundingBox,
        /// with an optional tolerance expansion on each side.
        /// </summary>
        private static bool IsInsideLocalBbox(XYZ localPt, LocalBoundingBox bbox, double tolerance)
        {
            return localPt.X >= bbox.MinX - tolerance && localPt.X <= bbox.MaxX + tolerance
                && localPt.Y >= bbox.MinY - tolerance && localPt.Y <= bbox.MaxY + tolerance
                && localPt.Z >= bbox.MinZ - tolerance && localPt.Z <= bbox.MaxZ + tolerance;
        }

        /// <summary>
        /// Reconstructs the world→local Transform from the stored ColumnOrientation.
        /// Needed in Phase 1C where we no longer hold the FamilyInstance reference.
        /// </summary>
        private static Transform GetWorldToLocalTransform(PrecastColumnData colData)
        {
            Transform t = Transform.Identity;
            t.Origin = colData.BaseCentreWorld.ToXYZ();
            t.BasisX = colData.Orientation.AxisX.ToXYZ();
            t.BasisY = colData.Orientation.AxisY.ToXYZ();
            t.BasisZ = colData.Orientation.AxisZ.ToXYZ();
            return t.Inverse;
        }

        private static Transform GetLocalToWorldTransform(PrecastColumnData colData)
        {
            Transform t = Transform.Identity;
            t.Origin = colData.BaseCentreWorld.ToXYZ();
            t.BasisX = colData.Orientation.AxisX.ToXYZ();
            t.BasisY = colData.Orientation.AxisY.ToXYZ();
            t.BasisZ = colData.Orientation.AxisZ.ToXYZ();
            return t;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CONVERSION HELPERS  (Vec3 ↔ Revit XYZ at the boundary layer)
        // ─────────────────────────────────────────────────────────────────────

        private static Vec3 ToVec3(XYZ xyz) => new Vec3(xyz.X, xyz.Y, xyz.Z);
        private static XYZ  ToXYZ(Vec3 v)  => new XYZ(v.X, v.Y, v.Z);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EXTENSION METHODS  (keep Vec3 ↔ XYZ conversions ergonomic in callers)
    // ─────────────────────────────────────────────────────────────────────────
    internal static class Vec3Extensions
    {
        public static XYZ ToXYZ(this Vec3 v) => new XYZ(v.X, v.Y, v.Z);
        public static Vec3 ToVec3(this XYZ xyz) => new Vec3(xyz.X, xyz.Y, xyz.Z);
    }
}