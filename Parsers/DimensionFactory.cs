// =============================================================================
//  StructAuto Detailing — Phase 4: Dimension & Annotation Factory
//  File:    Parsers/DimensionFactory.cs
//
//  Adds dimensions and annotations to the views produced by ViewFactory.
//
//  Strategy ("attempt full dimensioning", resilient):
//    • For the principal measurements (HEIGHT, WIDTH, LENGTH) it first tries a
//      real Revit Dimension between the column's planar-face references, computed
//      in the target view (Options.View) so the references are dimensionable.
//    • If a reference cannot be bound (common with family-instance geometry), it
//      falls back to an accurate TextNote whose value comes straight from the
//      parsed model — so the sheet is always populated with correct numbers.
//    • Elevation datums (EL x.xxx M), tie-spacing tags ("n @ s = total") and embed
//      marks (CMS1/CMS2/L) are placed as TextNotes (matches the reference sheets).
//
//  Every annotation is isolated in try/catch; a failure becomes a ParseWarning
//  and never aborts the run.
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
    public static class DimensionFactory
    {
        private static readonly double OFF = UnitConv.MmToFt(150.0); // dim/text offset off the solid
        private const double NORMAL_DOT = 0.95;                      // face-normal match tolerance

        public static void Annotate(Document doc, PrecastColumnData colData, List<ColumnView> views)
        {
            ElementId textTypeId = ResolveTextNoteType(doc);

            FamilyInstance fi = doc.GetElement(new ElementId((long)colData.RevitElementId)) as FamilyInstance;

            XYZ axX = colData.Orientation.AxisX.ToXYZ().Normalize();
            XYZ axY = colData.Orientation.AxisY.ToXYZ().Normalize();
            XYZ axZ = colData.Orientation.AxisZ.ToXYZ().Normalize();
            XYZ baseC = colData.BaseCentreWorld.ToXYZ();
            XYZ topC  = colData.TopCentreWorld.ToXYZ();
            XYZ midC  = (baseC + topC) * 0.5;

            double halfW = colData.ShaftWidthFt / 2.0;
            double halfD = colData.ShaftDepthFt / 2.0;
            double halfH = colData.TotalHeightFt / 2.0;

            foreach (ColumnView cv in views)
            {
                View view = doc.GetElement(cv.ViewId) as View;
                if (view == null) continue;

                try
                {
                    switch (cv.Kind)
                    {
                        case ViewKind.FrontElevation:
                        case ViewKind.SideElevation:
                            AnnotateElevation(doc, colData, fi, view, cv, textTypeId,
                                axX, axY, axZ, baseC, topC, midC, halfW, halfD, halfH);
                            break;

                        case ViewKind.CrossSection:
                            AnnotateSection(doc, colData, fi, view, cv, textTypeId,
                                axX, axY, axZ, baseC, halfW, halfD);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    colData.ParseWarnings.Add(
                        $"Phase 4: Annotation failed on view '{cv.Label}': {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ELEVATIONS
        // ─────────────────────────────────────────────────────────────────────

        private static void AnnotateElevation(Document doc, PrecastColumnData colData,
            FamilyInstance fi, View view, ColumnView cv, ElementId textTypeId,
            XYZ axX, XYZ axY, XYZ axZ, XYZ baseC, XYZ topC, XYZ midC,
            double halfW, double halfD, double halfH)
        {
            // The horizontal "right" axis on this elevation = the view's right direction.
            XYZ right = view.RightDirection.Normalize();
            double sideHalf = Math.Abs(right.DotProduct(axX)) > 0.5 ? halfW : halfD;

            // ── Overall HEIGHT (top/bottom faces) ─────────────────────────────
            int heightMm = (int)Math.Round(colData.TotalHeightFt * 304.8);
            XYZ heightAnchor = midC - right * (sideHalf + OFF);
            bool ok = TryDimension(doc, fi, view,
                FamilyInstanceReferenceType.Bottom, FamilyInstanceReferenceType.Top,
                faceNormalA: axZ.Negate(), faceNormalB: axZ,
                dimLineDir: axZ, dimLinePoint: heightAnchor, dimLineHalfLen: halfH + OFF,
                colData);
            if (!ok)
                PlaceText(doc, view.Id, heightAnchor + axZ * 0, $"HEIGHT\n{heightMm}", textTypeId, colData);

            // ── Elevation datums (always text, matches the EL markers) ────────
            PlaceText(doc, view.Id, baseC - right * (sideHalf + OFF) - axZ * UnitConv.MmToFt(120),
                $"EL {colData.BaseElevationM:F3} M", textTypeId, colData);
            PlaceText(doc, view.Id, topC - right * (sideHalf + OFF) + axZ * UnitConv.MmToFt(120),
                $"EL {colData.TopElevationM:F3} M", textTypeId, colData);

            // ── Tie-spacing tags (reinforcement front elevation only) ─────────
            if (cv.Variant == SheetVariant.Reinforcement &&
                cv.Kind == ViewKind.FrontElevation &&
                colData.Rebar?.TieZones != null)
            {
                foreach (RebarTieZone z in colData.Rebar.TieZones)
                {
                    double midZ = (z.StartZFt + z.EndZFt) / 2.0;
                    XYZ pos = baseC + axZ * midZ + right * (sideHalf + OFF);
                    string txt = $"T-{z.TieDiameterMm} @ {z.SpacingMm:F0} C/C\n{z.MraTagText}";
                    PlaceText(doc, view.Id, pos, txt, textTypeId, colData);
                }
            }

            // ── Embed marks (formwork only) ───────────────────────────────────
            if (cv.Variant == SheetVariant.Formwork)
            {
                foreach (EmbedData e in colData.CorrugatedSleeves.Concat(colData.Lifters))
                {
                    XYZ pos = e.CentroidWorld.ToXYZ() + right * (sideHalf + OFF * 0.5);
                    PlaceText(doc, view.Id, pos, e.DrawingMark, textTypeId, colData);
                }
            }

            // ── Tie-zone height chain (front elevation only) ──────────────────
            // A far-left vertical chain of the segment lengths between zone boundaries —
            // the "875 / 2475 / 875" style station chain from the reference sheet.
            if (cv.Kind == ViewKind.FrontElevation && colData.Rebar?.TieZones?.Count > 0)
            {
                double chainX = sideHalf + OFF * 1.3;
                foreach (RebarTieZone z in colData.Rebar.TieZones)
                {
                    double midZ = (z.StartZFt + z.EndZFt) / 2.0;
                    int segMm = (int)Math.Round((z.EndZFt - z.StartZFt) * 304.8);
                    if (segMm <= 0) continue;
                    XYZ pos = baseC + axZ * midZ - right * chainX;
                    PlaceText(doc, view.Id, pos, segMm.ToString(), textTypeId, colData);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CROSS SECTIONS
        // ─────────────────────────────────────────────────────────────────────

        private static void AnnotateSection(Document doc, PrecastColumnData colData,
            FamilyInstance fi, View view, ColumnView cv, ElementId textTypeId,
            XYZ axX, XYZ axY, XYZ axZ, XYZ baseC, double halfW, double halfD)
        {
            XYZ origin = baseC + axZ * cv.SectionHeightFt;
            int widthMm  = (int)Math.Round(colData.ShaftWidthFt * 304.8);
            int lengthMm = (int)Math.Round(colData.ShaftDepthFt * 304.8);

            // WIDTH along local X (Left/Right refs), dim line below the section.
            XYZ widthAnchor = origin - axY * (halfD + OFF);
            bool wOk = TryDimension(doc, fi, view,
                FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right,
                faceNormalA: axX.Negate(), faceNormalB: axX,
                dimLineDir: axX, dimLinePoint: widthAnchor, dimLineHalfLen: halfW + OFF, colData);
            if (!wOk)
                PlaceText(doc, view.Id, widthAnchor, $"WIDTH {widthMm}", textTypeId, colData);

            // LENGTH along local Y (Front/Back refs), dim line to the left.
            XYZ lenAnchor = origin - axX * (halfW + OFF);
            bool lOk = TryDimension(doc, fi, view,
                FamilyInstanceReferenceType.Front, FamilyInstanceReferenceType.Back,
                faceNormalA: axY.Negate(), faceNormalB: axY,
                dimLineDir: axY, dimLinePoint: lenAnchor, dimLineHalfLen: halfD + OFF, colData);
            if (!lOk)
                PlaceText(doc, view.Id, lenAnchor, $"LENGTH {lengthMm}", textTypeId, colData);

            // Tag the section with its tie spacing (if this cut maps to a zone).
            if (cv.Zone != null)
                PlaceText(doc, view.Id, origin + axX * (halfW + OFF) + axY * (halfD + OFF),
                    $"T-{cv.Zone.TieDiameterMm} @ {cv.Zone.SpacingMm:F0} C/C", textTypeId, colData);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FACE-REFERENCE DIMENSION (best effort)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Attempts a real Revit Dimension across the column. Strategy, most-reliable first:
        ///   1. The family instance's built-in references (Left/Right/Front/Back/Top/Bottom) —
        ///      these are stable and purpose-built for dimensioning.
        ///   2. Outermost planar faces matched by world normal (faceNormalA/B).
        /// Returns false (caller text-annotates) if neither yields a reference pair.
        /// </summary>
        private static bool TryDimension(Document doc, FamilyInstance fi, View view,
            FamilyInstanceReferenceType typeA, FamilyInstanceReferenceType typeB,
            XYZ faceNormalA, XYZ faceNormalB,
            XYZ dimLineDir, XYZ dimLinePoint, double dimLineHalfLen,
            PrecastColumnData colData)
        {
            if (fi == null) return false;

            Reference refA = InstRef(fi, typeA);
            Reference refB = InstRef(fi, typeB);

            if (refA == null || refB == null)
            {
                try
                {
                    Options opt = new Options { ComputeReferences = true, View = view };
                    GeometryElement ge = fi.get_Geometry(opt);
                    if (ge != null)
                    {
                        if (refA == null) refA = OuterFaceRef(ge, faceNormalA);
                        if (refB == null) refB = OuterFaceRef(ge, faceNormalB);
                    }
                }
                catch { /* fall through to text */ }
            }

            if (refA == null || refB == null) return false;

            try
            {
                var refs = new ReferenceArray();
                refs.Append(refA);
                refs.Append(refB);

                XYZ d = dimLineDir.Normalize();
                Line line = Line.CreateBound(
                    dimLinePoint - d * dimLineHalfLen,
                    dimLinePoint + d * dimLineHalfLen);

                Dimension dim = doc.Create.NewDimension(view, line, refs);
                return dim != null;
            }
            catch (Exception ex)
            {
                colData.ParseWarnings.Add($"Phase 4: dimension fell back to text ({ex.Message}).");
                return false;
            }
        }

        /// <summary>First stable reference of the given family-instance reference type, or null.</summary>
        private static Reference InstRef(FamilyInstance fi, FamilyInstanceReferenceType type)
        {
            try
            {
                var refs = fi.GetReferences(type);
                return (refs != null && refs.Count > 0) ? refs[0] : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the Reference of the planar face whose world normal best matches
        /// <paramref name="dir"/> and which is farthest along <paramref name="dir"/>
        /// (the outer face). Recurses through nested GeometryInstances.
        /// </summary>
        private static Reference OuterFaceRef(GeometryElement ge, XYZ dir)
        {
            Reference best = null;
            double bestProj = double.MinValue;
            CollectOuterFace(ge, dir.Normalize(), ref best, ref bestProj);
            return best;
        }

        private static void CollectOuterFace(GeometryElement ge, XYZ dir,
            ref Reference best, ref double bestProj)
        {
            foreach (GeometryObject go in ge)
            {
                if (go is Solid s && s.Faces.Size > 0)
                {
                    foreach (Face f in s.Faces)
                    {
                        if (!(f is PlanarFace pf)) continue;
                        if (pf.Reference == null) continue;
                        if (pf.FaceNormal.Normalize().DotProduct(dir) < NORMAL_DOT) continue;
                        double proj = pf.Origin.DotProduct(dir);
                        if (proj > bestProj) { bestProj = proj; best = pf.Reference; }
                    }
                }
                else if (go is GeometryInstance gi)
                {
                    CollectOuterFace(gi.GetInstanceGeometry(), dir, ref best, ref bestProj);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  TEXT-NOTE HELPERS
        // ─────────────────────────────────────────────────────────────────────

        private static void PlaceText(Document doc, ElementId viewId, XYZ position,
            string text, ElementId textTypeId, PrecastColumnData colData)
        {
            try
            {
                if (textTypeId == ElementId.InvalidElementId) return;
                TextNote.Create(doc, viewId, position, text, textTypeId);
            }
            catch (Exception ex)
            {
                colData.ParseWarnings.Add($"Phase 4: could not place text '{text}': {ex.Message}");
            }
        }

        private static ElementId ResolveTextNoteType(Document doc)
        {
            ElementId id = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (id != null && id != ElementId.InvalidElementId) return id;

            TextNoteType t = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault();
            return t?.Id ?? ElementId.InvalidElementId;
        }
    }
}
