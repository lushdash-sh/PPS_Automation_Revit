using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    public class SmartDimensioningCommand : IExternalCommand
    {
        // =====================================================================
        //  SETTINGS - tweak these instead of hunting through the code
        // =====================================================================

        // Distances below are PAPER millimetres. They are multiplied by the
        // view scale, so spacing looks identical in a 1:10 and a 1:25 view.
        private const double TierFirstMm = 8.0;     // first vertical tier from the left face
        private const double TierStepMm = 8.0;      // spacing between vertical tiers
        private const double LabelGapMm = 2.0;      // gap between dimension end and label

        // Top horizontal dimensions
        private const bool PlaceTopDimsAboveRebar = true; // true = sit above the highest bar/embed in the assembly
        private const double TopDimClearanceMm = 10.0;    // gap between highest bar and the partial dim (when above rebar)
        private const double TopDimStepMm = 10.0;         // gap between partial and overall dim
        private const double TopPartialMm = 14.0;         // partial dim height above the column top (only if PlaceTopDimsAboveRebar = false)

        // Labels (set to null to skip a label)
        private const string LblLength = "LENGTH";  // overall horizontal
        private const string LblPartial = "L1";     // partial horizontal
        private const string LblHeight = "HEIGHT";  // overall vertical
        private const string LblTier = "L2";        // intermediate vertical tier

        private const bool DrawLabelLine = true;    // short detail line carrying the dim line out under the label

        // Corbel detection
        private const bool DetectCorbelsByProtrusion = true; // also treat any embed sticking out past the column sides as a corbel
        private const double ProtrusionTolFt = 0.05;

        // Show a diagnostics dialog after each run (families found, what was accepted/rejected)
        private const bool ShowDiagnostics = true;

        // Model tolerances (feet)
        private const double GeomTolFt = 0.02;               // treat Z values closer than this as the same plane
        private const double RebarZoneMinSeparationFt = 1.0; // side view: min spacing between rebar-zone planes

        // =====================================================================
        //  Working state for one run
        // =====================================================================
        private Document _doc;
        private View _view;
        private double _scale;
        private ElementId _textTypeId;
        private double _textHeight;   // model feet
        private double _colLeftX, _colRightX;
        private XYZ _testOrigin;
        private string _warnings = "";
        private readonly StringBuilder _diag = new StringBuilder();

        private double Off(double paperMm) => paperMm * _scale / 304.8;
        private double TierMm(int tier) => TierFirstMm + (tier - 1) * TierStepMm;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView.ViewType != ViewType.Elevation && activeView.ViewType != ViewType.Section && activeView.ViewType != ViewType.Detail)
            {
                TaskDialog.Show("Error", "Please run this command from an isolated 2D Assembly view.");
                return Result.Failed;
            }

            try
            {
                Reference colRef = uidoc.Selection.PickObject(ObjectType.Element, new ColumnSelectionFilter(), "Select the Precast Column Assembly");
                Element selectedElem = doc.GetElement(colRef);

                List<Element> elementsToProcess = new List<Element>();
                FamilyInstance mainColumn = null;

                // Assembly isolation: only harvest members of the selected assembly
                if (selectedElem is AssemblyInstance assembly)
                {
                    foreach (ElementId memberId in assembly.GetMemberIds())
                    {
                        Element member = doc.GetElement(memberId);
                        elementsToProcess.Add(member);
                        if (member is FamilyInstance fi && fi.Category != null &&
                            fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                        {
                            mainColumn = fi;
                        }
                    }
                }
                else
                {
                    elementsToProcess.Add(selectedElem);
                    mainColumn = selectedElem as FamilyInstance;
                }

                if (mainColumn == null)
                {
                    TaskDialog.Show("Error", "Could not identify the main structural column in the selection.");
                    return Result.Failed;
                }

                Reference[] sideFaces = GetLeftAndRightColumnFaces(activeView, mainColumn);
                Reference bottomFace = GetLowestColumnFace(activeView, mainColumn);
                Reference topFace = GetHighestColumnFace(activeView, mainColumn);

                if (bottomFace == null || topFace == null || sideFaces[0] == null || sideFaces[1] == null)
                {
                    TaskDialog.Show("Error", "Could not find primary concrete faces.");
                    return Result.Failed;
                }

                PlanarFace topPlanarFace = mainColumn.GetGeometryObjectFromReference(topFace) as PlanarFace;
                PlanarFace leftPlanarFace = mainColumn.GetGeometryObjectFromReference(sideFaces[0]) as PlanarFace;
                PlanarFace rightPlanarFace = mainColumn.GetGeometryObjectFromReference(sideFaces[1]) as PlanarFace;
                PlanarFace bottomPlanarFace = mainColumn.GetGeometryObjectFromReference(bottomFace) as PlanarFace;

                if (topPlanarFace == null || leftPlanarFace == null || rightPlanarFace == null || bottomPlanarFace == null)
                {
                    TaskDialog.Show("Error", "Could not resolve the column faces to planar geometry.");
                    return Result.Failed;
                }

                XYZ viewUp = activeView.UpDirection;
                XYZ viewRight = activeView.RightDirection;
                double colTopZ = topPlanarFace.Origin.DotProduct(viewUp);
                double colBottomZ = bottomPlanarFace.Origin.DotProduct(viewUp);
                double colRightX = rightPlanarFace.Origin.DotProduct(viewRight);
                double colLeftX = leftPlanarFace.Origin.DotProduct(viewRight);

                // View orientation vs. the column's own axes (not world axes)
                XYZ viewDir = activeView.ViewDirection;
                XYZ colHand = mainColumn.HandOrientation;
                XYZ colFacing = mainColumn.FacingOrientation;

                double dotX = Math.Abs(viewRight.DotProduct(colHand));
                double dotY = Math.Abs(viewRight.DotProduct(colFacing));

                // Looking along the column's facing axis = Front/Back elevation.
                // Looking along the hand axis = Right/Left elevation.
                bool isSideView = Math.Abs(viewDir.DotProduct(colHand)) > Math.Abs(viewDir.DotProduct(colFacing));
                FamilyInstanceReferenceType topCenterRefType = dotX > dotY
                    ? FamilyInstanceReferenceType.CenterLeftRight
                    : FamilyInstanceReferenceType.CenterFrontBack;

                // Init working state
                _doc = doc;
                _view = activeView;
                _scale = Math.Max(1, activeView.Scale);
                _warnings = "";
                _diag.Clear();
                _colLeftX = colLeftX;
                _colRightX = colRightX;
                _textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                double textSizeFt = 2.5 / 304.8;
                TextNoteType tnt = doc.GetElement(_textTypeId) as TextNoteType;
                Parameter sizeParam = tnt?.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (sizeParam != null && sizeParam.AsDouble() > 0) textSizeFt = sizeParam.AsDouble();
                _textHeight = textSizeFt * _scale;

                XYZ leftOrigin = leftPlanarFace.Origin;
                XYZ topOrigin = topPlanarFace.Origin;
                _testOrigin = leftOrigin + viewRight * -Off(TierMm(1));

                // Where do the horizontal dims start? Above the highest bar so they never cross rebar.
                double assemblyTopZ = ComputeAssemblyTopZ(elementsToProcess, viewUp, colTopZ);
                double dimBaseZ = PlaceTopDimsAboveRebar ? Math.Max(colTopZ, assemblyTopZ) : colTopZ;
                XYZ topDimBase = topOrigin + viewUp * (dimBaseZ - topOrigin.DotProduct(viewUp));
                double partialOff = PlaceTopDimsAboveRebar ? TopDimClearanceMm : TopPartialMm;
                double overallOff = partialOff + TopDimStepMm;

                using (Transaction t = new Transaction(doc, "Smart Dimensioning"))
                {
                    t.Start();

                    // ---------- Top horizontal dimensions ----------
                    Reference centerRef = null;
                    IList<Reference> centerRefs = mainColumn.GetReferences(topCenterRefType);
                    if (centerRefs != null && centerRefs.Count > 0) centerRef = centerRefs[0];

                    TryDim("Top Partial", () =>
                    {
                        if (centerRef == null) return;
                        ReferenceArray refs = new ReferenceArray();
                        refs.Append(sideFaces[0]);
                        refs.Append(centerRef);
                        refs.Append(sideFaces[1]);
                        MakeHorizontalDim(refs, topDimBase, partialOff, colRightX, LblPartial);
                    });

                    TryDim("Top Overall", () =>
                    {
                        ReferenceArray refs = new ReferenceArray();
                        refs.Append(sideFaces[0]);
                        refs.Append(sideFaces[1]);
                        MakeHorizontalDim(refs, topDimBase, overallOff, colRightX, LblLength);
                    });

                    if (!isSideView)
                    {
                        // ---------- Back / Front elevation ----------
                        List<Reference> topHookRef, lowerHookRefs, corbelRefs;
                        Reference extremeBottomDowel;
                        HarvestBackViewEmbeds(elementsToProcess, mainColumn, colBottomZ, colTopZ, bottomFace, topFace,
                            out topHookRef, out lowerHookRefs, out corbelRefs, out extremeBottomDowel);

                        // Tier 1: bottom -> corbel bottoms/tops -> top hook -> top
                        TryDim("Back Tier 1", () =>
                        {
                            List<Reference> cands = new List<Reference>();
                            cands.AddRange(corbelRefs);
                            cands.AddRange(topHookRef);
                            ReferenceArray r1 = BuildValidArray(new[] { bottomFace, topFace }, cands, _testOrigin, "Tier 1");
                            if (r1.Size > 2) MakeVerticalDim(r1, leftOrigin, TierMm(1), colTopZ, null);
                            else _diag.AppendLine("Tier 1 skipped: no corbel or hook reference was usable.");
                        });

                        // Tier 2: bottom -> lower hooks -> top
                        TryDim("Back Tier 2", () =>
                        {
                            if (lowerHookRefs.Count == 0) return;
                            XYZ o2 = leftOrigin + viewRight * -Off(TierMm(2));
                            ReferenceArray r2 = BuildValidArray(new[] { bottomFace, topFace }, lowerHookRefs, o2, "Tier 2");
                            if (r2.Size > 2) MakeVerticalDim(r2, leftOrigin, TierMm(2), colTopZ, LblTier);
                        });

                        // Tier 3: (dowel ->) bottom -> top  = overall concrete depth (+ the dowel projection)
                        TryDim("Back Tier 3", () =>
                        {
                            ReferenceArray r3 = new ReferenceArray();
                            if (extremeBottomDowel != null) r3.Append(extremeBottomDowel);
                            r3.Append(bottomFace);
                            r3.Append(topFace);
                            MakeVerticalDim(r3, leftOrigin, TierMm(3), colTopZ, LblHeight);
                        });

                        // Tier 4: extreme dowel -> top
                        TryDim("Back Tier 4", () =>
                        {
                            if (extremeBottomDowel == null)
                            {
                                _diag.AppendLine("Tier 4 skipped: no dowel / receptor found below the column bottom.");
                                return;
                            }
                            ReferenceArray r4 = new ReferenceArray();
                            r4.Append(extremeBottomDowel);
                            r4.Append(topFace);
                            MakeVerticalDim(r4, leftOrigin, TierMm(4), colTopZ, null);
                        });
                    }
                    else
                    {
                        // ---------- Right / Left elevation ----------
                        List<Reference> rebarZoneRefs;
                        Reference extremeBottomDowel;
                        HarvestRightViewZones(elementsToProcess, colBottomZ, colTopZ, out rebarZoneRefs, out extremeBottomDowel);

                        TryDim("Right Tier 1 (Rebar)", () =>
                        {
                            if (extremeBottomDowel == null || rebarZoneRefs.Count == 0) return;
                            ReferenceArray r1 = new ReferenceArray();
                            r1.Append(extremeBottomDowel);
                            foreach (Reference r in rebarZoneRefs) r1.Append(r);
                            r1.Append(topFace);
                            MakeVerticalDim(r1, leftOrigin, TierMm(1), colTopZ, LblTier);
                        });

                        TryDim("Right Tier 2", () =>
                        {
                            ReferenceArray r2 = new ReferenceArray();
                            r2.Append(bottomFace);
                            r2.Append(topFace);
                            MakeVerticalDim(r2, leftOrigin, TierMm(2), colTopZ, LblHeight);
                        });

                        TryDim("Right Tier 3", () =>
                        {
                            if (extremeBottomDowel == null) return;
                            ReferenceArray r3 = new ReferenceArray();
                            r3.Append(extremeBottomDowel);
                            r3.Append(topFace);
                            MakeVerticalDim(r3, leftOrigin, TierMm(3), colTopZ, null);
                        });
                    }

                    t.Commit();
                }

                string report = "";
                if (!string.IsNullOrEmpty(_warnings)) report += "Failures:\n" + _warnings + "\n";
                if (ShowDiagnostics && _diag.Length > 0) report += "Diagnostics:\n" + _diag;
                if (!string.IsNullOrEmpty(report)) TaskDialog.Show("Smart Dimensioning", report);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        // =====================================================================
        //  Dimension + label creation
        // =====================================================================

        private void TryDim(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { _warnings += $"- {name}: {ex.Message}\n"; }
        }

        /// <summary>
        /// Trial-creates a vertical dimension in a SubTransaction and rolls it back.
        /// Tells us whether Revit will actually accept this set of references.
        /// </summary>
        private bool TestDimension(ReferenceArray refs, XYZ origin)
        {
            using (SubTransaction st = new SubTransaction(_doc))
            {
                st.Start();
                try
                {
                    Dimension d = _doc.Create.NewDimension(_view, Line.CreateBound(origin, origin + _view.UpDirection), refs);
                    return d != null;
                }
                catch { return false; }
                finally { st.RollBack(); }
            }
        }

        private bool IsUsable(Reference candidate, Reference bottomFace, Reference topFace)
        {
            ReferenceArray arr = new ReferenceArray();
            arr.Append(bottomFace);
            arr.Append(topFace);
            arr.Append(candidate);
            return TestDimension(arr, _testOrigin);
        }

        /// <summary>Adds candidates one at a time, keeping only those Revit accepts, so one bad ref can't kill a whole tier.</summary>
        private ReferenceArray BuildValidArray(IEnumerable<Reference> required, IEnumerable<Reference> candidates,
            XYZ origin, string tag)
        {
            ReferenceArray arr = new ReferenceArray();
            foreach (Reference r in required) arr.Append(r);

            int rejected = 0;
            foreach (Reference c in candidates)
            {
                ReferenceArray trial = new ReferenceArray();
                foreach (Reference r in arr) trial.Append(r);
                trial.Append(c);

                if (TestDimension(trial, origin)) arr = trial;
                else rejected++;
            }
            if (rejected > 0) _diag.AppendLine($"{tag}: {rejected} reference(s) rejected by Revit.");
            return arr;
        }

        /// <summary>Vertical dimension string left of the column. Label goes above the top face.</summary>
        private Dimension MakeVerticalDim(ReferenceArray refs, XYZ leftOrigin, double offsetMm, double colTopZ, string label)
        {
            XYZ o = leftOrigin + _view.RightDirection * -Off(offsetMm);
            Dimension dim = _doc.Create.NewDimension(_view, Line.CreateBound(o, o + _view.UpDirection), refs);
            if (dim != null && !string.IsNullOrEmpty(label)) AddVerticalLabel(o, colTopZ, label);
            return dim;
        }

        /// <summary>Horizontal dimension string above the column. Label goes right of the right face.</summary>
        private Dimension MakeHorizontalDim(ReferenceArray refs, XYZ baseOrigin, double offsetMm, double colRightX, string label)
        {
            XYZ o = baseOrigin + _view.UpDirection * Off(offsetMm);
            Dimension dim = _doc.Create.NewDimension(_view, Line.CreateBound(o, o + _view.RightDirection), refs);
            if (dim != null && !string.IsNullOrEmpty(label)) AddHorizontalLabel(o, colRightX, label);
            return dim;
        }

        private void AddHorizontalLabel(XYZ dimLineOrigin, double colRightX, string text)
        {
            try
            {
                XYZ r = _view.RightDirection, u = _view.UpDirection;
                double h = _textHeight;
                double width = text.Length * h * 0.8;

                XYZ lineStart = dimLineOrigin + r * (colRightX - dimLineOrigin.DotProduct(r));
                XYZ textStart = lineStart + r * Off(LabelGapMm);

                // TextNote insertion point is the top-left corner -> lift by one text height
                CreateNote(textStart + u * (h * 1.25), text, 0.0);

                if (DrawLabelLine)
                    DrawLine(lineStart, textStart + r * width);
            }
            catch (Exception ex) { _warnings += $"- Label '{text}': {ex.Message}\n"; }
        }

        private void AddVerticalLabel(XYZ tierOrigin, double colTopZ, string text)
        {
            try
            {
                XYZ r = _view.RightDirection, u = _view.UpDirection;
                double h = _textHeight;
                double width = text.Length * h * 0.8;

                XYZ lineStart = tierOrigin + u * (colTopZ - tierOrigin.DotProduct(u));
                XYZ textStart = lineStart + u * Off(LabelGapMm);

                // Text rotated 90deg CCW reads bottom-to-top and its body extends to the RIGHT of the
                // insertion point, so shift left by one text height to sit beside the tier line.
                CreateNote(textStart - r * (h * 1.25), text, Math.PI / 2.0);

                if (DrawLabelLine)
                    DrawLine(lineStart, textStart + u * width);
            }
            catch (Exception ex) { _warnings += $"- Label '{text}': {ex.Message}\n"; }
        }

        private void CreateNote(XYZ position, string text, double rotationRadians)
        {
            TextNoteOptions opts = new TextNoteOptions(_textTypeId)
            {
                HorizontalAlignment = HorizontalTextAlignment.Left,
                VerticalAlignment = VerticalTextAlignment.Top,
                Rotation = rotationRadians
            };
            TextNote.Create(_doc, _view.Id, position, text, opts);
        }

        private void DrawLine(XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) < 0.01) return;
            _doc.Create.NewDetailCurve(_view, Line.CreateBound(a, b));
        }

        // =====================================================================
        //  Geometry helpers
        // =====================================================================

        /// <summary>Geometry objects with nested GeometryInstances expanded (up to 3 levels deep).</summary>
        private static IEnumerable<GeometryObject> FlattenGeometry(GeometryElement ge, int depth = 0)
        {
            if (ge == null) yield break;
            foreach (GeometryObject go in ge)
            {
                if (go is GeometryInstance gi)
                {
                    if (depth >= 3) continue;
                    foreach (GeometryObject inner in FlattenGeometry(gi.GetInstanceGeometry(), depth + 1))
                        yield return inner;
                }
                else
                {
                    yield return go;
                }
            }
        }

        private static IEnumerable<Solid> GetSolids(GeometryElement ge)
        {
            foreach (GeometryObject o in FlattenGeometry(ge))
                if (o is Solid s && s.Faces.Size > 0) yield return s;
        }

        private static IEnumerable<PlanarFace> GetHorizontalFaces(GeometryElement ge, XYZ viewUp, double minDot = 0.99)
        {
            foreach (Solid s in GetSolids(ge))
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.Reference != null &&
                        Math.Abs(pf.FaceNormal.DotProduct(viewUp)) > minDot)
                        yield return pf;
        }

        /// <summary>Min/max of an element's bounding box projected on an axis (all 8 corners, so rotated views are fine).</summary>
        private bool TryGetExtents(Element e, XYZ axis, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            BoundingBoxXYZ bb = e.get_BoundingBox(_view) ?? e.get_BoundingBox(null);
            if (bb == null) return false;
            Transform tf = bb.Transform ?? Transform.Identity;

            double[] xs = { bb.Min.X, bb.Max.X };
            double[] ys = { bb.Min.Y, bb.Max.Y };
            double[] zs = { bb.Min.Z, bb.Max.Z };
            foreach (double x in xs)
                foreach (double y in ys)
                    foreach (double z in zs)
                    {
                        double d = tf.OfPoint(new XYZ(x, y, z)).DotProduct(axis);
                        if (d < min) min = d;
                        if (d > max) max = d;
                    }
            return true;
        }

        /// <summary>Highest point of anything in the assembly (bars, hooks, dowels), along the view's up axis.</summary>
        private double ComputeAssemblyTopZ(List<Element> elems, XYZ viewUp, double colTopZ)
        {
            double top = colTopZ;
            foreach (Element e in elems)
            {
                double mn, mx;
                if (TryGetExtents(e, viewUp, out mn, out mx) && mx > top) top = mx;
            }
            return top;
        }

        private static void TrackLowestDowel(GeometryElement ge, XYZ viewUp, double colBottomZ,
            ref double lowestZ, ref Reference lowestRef)
        {
            foreach (PlanarFace pf in GetHorizontalFaces(ge, viewUp))
            {
                double z = pf.Origin.DotProduct(viewUp);
                if (z < lowestZ && z < (colBottomZ - GeomTolFt))
                {
                    lowestZ = z;
                    lowestRef = pf.Reference;
                }
            }
        }

        /// <summary>Highest and lowest horizontal face of a corbel, each paired with its exact Z.</summary>
        private static void CollectCorbelFaceRefs(FamilyInstance inst, XYZ viewUp, Options opts,
            List<Tuple<Reference, double>> sink)
        {
            GeometryElement ge = inst.get_Geometry(opts);
            if (ge == null) return;

            Reference top = null, bottom = null;
            double maxZ = double.MinValue, minZ = double.MaxValue;

            foreach (PlanarFace pf in GetHorizontalFaces(ge, viewUp))
            {
                double z = pf.Origin.DotProduct(viewUp);
                if (z > maxZ) { maxZ = z; top = pf.Reference; }
                if (z < minZ) { minZ = z; bottom = pf.Reference; }
            }

            if (top != null) sink.Add(Tuple.Create(top, maxZ));
            if (bottom != null && Math.Abs(maxZ - minZ) > GeomTolFt) sink.Add(Tuple.Create(bottom, minZ));
        }

        /// <summary>Family reference planes (DimRef / Top / Bottom) with Z estimated from the bounding box.</summary>
        private static void CollectCorbelPlaneRefs(FamilyInstance inst, double zMin, double zMax,
            List<Tuple<Reference, double>> sink)
        {
            try
            {
                Reference named = inst.GetReferenceByName("DimRef");
                if (named != null) sink.Add(Tuple.Create(named, zMax));
            }
            catch { }

            try
            {
                IList<Reference> tops = inst.GetReferences(FamilyInstanceReferenceType.Top);
                if (tops != null && tops.Count > 0) sink.Add(Tuple.Create(tops[0], zMax));
            }
            catch { }

            try
            {
                IList<Reference> bottoms = inst.GetReferences(FamilyInstanceReferenceType.Bottom);
                if (bottoms != null && bottoms.Count > 0) sink.Add(Tuple.Create(bottoms[0], zMin));
            }
            catch { }
        }

        /// <summary>
        /// Downward-facing horizontal faces of the column body between its top and bottom that reach the
        /// column's side extremes (the underside of a head block, for example). Each is tested against Revit.
        /// </summary>
        private void CollectColumnStepFaces(FamilyInstance column, double colTopZ, double colBottomZ,
            Reference bottomFace, Reference topFace, List<Tuple<Reference, double>> sink)
        {
            Options opts = new Options { ComputeReferences = true, View = _view };
            GeometryElement ge = column.get_Geometry(opts);
            if (ge == null) return;

            XYZ up = _view.UpDirection, right = _view.RightDirection;

            foreach (Solid s in GetSolids(ge))
            {
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                    if (pf.FaceNormal.DotProduct(up) > -0.99) continue;   // underside faces only

                    double z = pf.Origin.DotProduct(up);
                    if (z <= colBottomZ + GeomTolFt || z >= colTopZ - GeomTolFt) continue;

                    // must reach the left or right extreme of the column silhouette
                    double mn = double.MaxValue, mx = double.MinValue;
                    foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
                        foreach (Curve c in loop)
                            foreach (XYZ p in c.Tessellate())
                            {
                                double d = p.DotProduct(right);
                                if (d < mn) mn = d;
                                if (d > mx) mx = d;
                            }
                    if (mn > _colLeftX + ProtrusionTolFt && mx < _colRightX - ProtrusionTolFt) continue;

                    if (IsUsable(pf.Reference, bottomFace, topFace))
                        sink.Add(Tuple.Create(pf.Reference, z));
                }
            }
        }

        private static Reference FindHookGeometryRef(GeometryElement ge, XYZ viewUp)
        {
            foreach (GeometryObject o in FlattenGeometry(ge))
            {
                if (o is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                        if (face is PlanarFace pf && pf.Reference != null &&
                            Math.Abs(pf.FaceNormal.DotProduct(viewUp)) > 0.99)
                            return pf.Reference;
                }
                else if (o is Line line && line.Reference != null &&
                         Math.Abs(line.Direction.DotProduct(viewUp)) < 0.05)
                {
                    return line.Reference;
                }
            }
            return null;
        }

        // =====================================================================
        //  Back / Front elevation harvesting
        // =====================================================================

        private void HarvestBackViewEmbeds(List<Element> elementsToProcess, FamilyInstance mainColumn, double colBottomZ, double colTopZ,
            Reference bottomFace, Reference topFace,
            out List<Reference> topHookRef, out List<Reference> lowerHookRefs, out List<Reference> corbelRefs,
            out Reference extremeBottomDowel)
        {
            topHookRef = new List<Reference>();
            lowerHookRefs = new List<Reference>();
            corbelRefs = new List<Reference>();
            extremeBottomDowel = null;
            double absoluteLowestDowelZ = double.MaxValue;

            List<Tuple<Reference, double>> tempHooks = new List<Tuple<Reference, double>>();
            List<Tuple<Reference, double>> tempCorbels = new List<Tuple<Reference, double>>();

            XYZ viewUp = _view.UpDirection;
            XYZ viewRight = _view.RightDirection;
            Options geomOptions = new Options { ComputeReferences = true, View = _view, IncludeNonVisibleObjects = true };

            // family name -> role, for the diagnostics report
            Dictionary<string, string> roles = new Dictionary<string, string>();
            int rebarCount = 0, corbelCount = 0, hookCount = 0, dowelCount = 0;
            double dowelLowestMm = double.MaxValue;
            int dowelHorizFaces = 0, dowelHorizRefs = 0;

            foreach (Element elem in elementsToProcess)
            {
                if (elem is FamilyInstance inst)
                {
                    if (inst.Category != null && inst.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                        continue;

                    string famName = inst.Symbol.FamilyName.ToLower();

                    bool isDowel = famName.Contains("groutec") || famName.Contains("receptor") || famName.Contains("donor") || famName.Contains("tube");
                    bool isHook = famName.Contains("hock") || famName.Contains("lifting") || famName.Contains("lifter");
                    bool nameCorbel = (famName.Contains("corbel") || famName.Contains("conn") || famName.Contains("pic")) && !isDowel;

                    // Geometry-based corbel detection: anything that sticks out past the column sides
                    bool protrudes = false;
                    if (DetectCorbelsByProtrusion && !isDowel && !isHook)
                    {
                        double rMin, rMax;
                        if (TryGetExtents(inst, viewRight, out rMin, out rMax))
                            protrudes = rMin < _colLeftX - ProtrusionTolFt || rMax > _colRightX + ProtrusionTolFt;
                    }
                    bool isCorbel = (nameCorbel || protrudes) && !isDowel && !isHook;

                    string role = isCorbel ? (nameCorbel ? "corbel (by name)" : "corbel (by protrusion)")
                                : isHook ? "hook" : isDowel ? "dowel" : "ignored";
                    roles[inst.Symbol.FamilyName] = role;

                    GeometryElement geomElem = inst.get_Geometry(geomOptions);

                    if (isCorbel)
                    {
                        corbelCount++;

                        // 1) exact top/bottom faces from the geometry, each tested against Revit
                        List<Tuple<Reference, double>> faceCands = new List<Tuple<Reference, double>>();
                        CollectCorbelFaceRefs(inst, viewUp, geomOptions, faceCands);
                        List<Tuple<Reference, double>> usable = faceCands.Where(c => IsUsable(c.Item1, bottomFace, topFace)).ToList();
                        string source = "geometry faces";

                        // 2) fall back to the family's reference planes
                        int planeCandCount = 0;
                        if (usable.Count == 0)
                        {
                            List<Tuple<Reference, double>> planeCands = new List<Tuple<Reference, double>>();
                            double zMin, zMax;
                            if (TryGetExtents(inst, viewUp, out zMin, out zMax))
                                CollectCorbelPlaneRefs(inst, zMin, zMax, planeCands);
                            planeCandCount = planeCands.Count;
                            usable = planeCands.Where(c => IsUsable(c.Item1, bottomFace, topFace)).ToList();
                            source = "reference planes";
                        }

                        tempCorbels.AddRange(usable);
                        _diag.AppendLine($"Corbel '{inst.Symbol.FamilyName}' ({role}): " +
                                         $"{faceCands.Count} face ref(s) found, " +
                                         (planeCandCount > 0 || source == "reference planes" ? $"{planeCandCount} plane ref(s) found, " : "") +
                                         $"{usable.Count} usable via {source}.");
                    }

                    if (isHook)
                    {
                        hookCount++;
                        Reference validRef = null;

                        // 1. Named reference plane "DimRef"
                        try { validRef = inst.GetReferenceByName("DimRef"); } catch { }

                        // 2. Built-in reference planes
                        if (validRef == null)
                        {
                            FamilyInstanceReferenceType[] refTypes = {
                                FamilyInstanceReferenceType.CenterElevation,
                                FamilyInstanceReferenceType.CenterLeftRight,
                                FamilyInstanceReferenceType.CenterFrontBack,
                                FamilyInstanceReferenceType.Top,
                                FamilyInstanceReferenceType.Bottom
                            };
                            foreach (var rt in refTypes)
                            {
                                IList<Reference> refs = inst.GetReferences(rt);
                                if (refs != null && refs.Count > 0) { validRef = refs[0]; break; }
                            }
                        }

                        // 3. Geometry fallback (horizontal face or the invisible model line)
                        if (validRef == null && geomElem != null)
                            validRef = FindHookGeometryRef(geomElem, viewUp);

                        BoundingBoxXYZ bbox = inst.get_BoundingBox(_view) ?? inst.get_BoundingBox(null);
                        if (bbox != null && validRef != null)
                        {
                            double refZ = ((bbox.Max + bbox.Min) / 2.0).DotProduct(viewUp);
                            tempHooks.Add(new Tuple<Reference, double>(validRef, refZ));
                        }
                    }
                    else if (isDowel && geomElem != null)
                    {
                        dowelCount++;
                        TrackLowestDowel(geomElem, viewUp, colBottomZ, ref absoluteLowestDowelZ, ref extremeBottomDowel);

                        // probe: where is this dowel relative to the column bottom, and does it expose usable faces?
                        double dmn, dmx;
                        if (TryGetExtents(inst, viewUp, out dmn, out dmx))
                            dowelLowestMm = Math.Min(dowelLowestMm, (dmn - colBottomZ) * 304.8);
                        foreach (Solid ds in GetSolids(geomElem))
                            foreach (Face df in ds.Faces)
                                if (df is PlanarFace dpf && Math.Abs(dpf.FaceNormal.DotProduct(viewUp)) > 0.99)
                                {
                                    dowelHorizFaces++;
                                    if (dpf.Reference != null) dowelHorizRefs++;
                                }
                    }
                }
                else if (elem is Autodesk.Revit.DB.Structure.Rebar rebar)
                {
                    rebarCount++;
                    // Dowels modeled as structural rebar
                    GeometryElement geomElem = rebar.get_Geometry(geomOptions);
                    if (geomElem != null)
                        TrackLowestDowel(geomElem, viewUp, colBottomZ, ref absoluteLowestDowelZ, ref extremeBottomDowel);
                }
            }

            // ---- Fallback 1: dowel tips by name found nothing -> use ANY member that sticks out below the column bottom
            if (extremeBottomDowel == null)
            {
                int shown = 0, belowCount = 0;
                foreach (Element e in elementsToProcess)
                {
                    if (e is FamilyInstance fc && fc.Category != null &&
                        fc.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns) continue;

                    double mn, mx;
                    if (!TryGetExtents(e, viewUp, out mn, out mx)) continue;
                    if (mn >= colBottomZ - GeomTolFt) continue;

                    belowCount++;
                    if (shown < 5)
                    {
                        string nm = e is FamilyInstance fi2 ? fi2.Symbol.FamilyName : e.GetType().Name;
                        _diag.AppendLine($"Member below column bottom: {nm} ({(colBottomZ - mn) * 304.8:0.#} mm below).");
                        shown++;
                    }

                    GeometryElement g = e.get_Geometry(geomOptions);
                    if (g != null)
                        TrackLowestDowel(g, viewUp, colBottomZ, ref absoluteLowestDowelZ, ref extremeBottomDowel);

                    // family reference-plane fallback (Bottom plane), tested against Revit
                    if (extremeBottomDowel == null && e is FamilyInstance fi3)
                    {
                        try
                        {
                            IList<Reference> bots = fi3.GetReferences(FamilyInstanceReferenceType.Bottom);
                            if (bots != null && bots.Count > 0 && mn < absoluteLowestDowelZ &&
                                IsUsable(bots[0], bottomFace, topFace))
                            {
                                absoluteLowestDowelZ = mn;
                                extremeBottomDowel = bots[0];
                            }
                        }
                        catch { }
                    }
                }
                _diag.AppendLine($"Fallback dowel search: {belowCount} member(s) extend below the column bottom -> " +
                                 (extremeBottomDowel != null ? "reference found." : "no usable reference."));
            }

            // ---- Underside steps of the column itself (e.g. the head block's underside)
            int stepCount0 = tempCorbels.Count;
            try { CollectColumnStepFaces(mainColumn, colTopZ, colBottomZ, bottomFace, topFace, tempCorbels); }
            catch (Exception ex) { _diag.AppendLine($"Column step faces failed: {ex.Message}"); }
            _diag.AppendLine($"Column underside steps added to Tier 1: {tempCorbels.Count - stepCount0}.");

            // De-duplicate corbel planes. Skip anything sitting on the column's top/bottom face
            // (those are already in the dimension) and anything at the same Z as one already kept.
            List<double> keptZ = new List<double>();
            foreach (var c in tempCorbels.OrderByDescending(c => c.Item2))
            {
                double z = c.Item2;
                if (Math.Abs(z - colTopZ) < GeomTolFt || Math.Abs(z - colBottomZ) < GeomTolFt) continue;
                if (keptZ.Any(k => Math.Abs(k - z) < GeomTolFt)) continue;
                corbelRefs.Add(c.Item1);
                keptZ.Add(z);
            }

            // Split hooks into the topmost hook (Tier 1) and the lower hooks (Tier 2)
            if (tempHooks.Count > 0)
            {
                var sortedHooks = tempHooks.OrderByDescending(h => h.Item2).ToList();
                topHookRef.Add(sortedHooks[0].Item1);

                double lastZ = sortedHooks[0].Item2;
                for (int i = 1; i < sortedHooks.Count; i++)
                {
                    if (Math.Abs(sortedHooks[i].Item2 - lastZ) > 0.05)
                    {
                        lowerHookRefs.Add(sortedHooks[i].Item1);
                        lastZ = sortedHooks[i].Item2;
                    }
                }
            }

            // Summary for the diagnostics dialog
            _diag.AppendLine();
            _diag.AppendLine($"Assembly members: {rebarCount} rebar, {corbelCount} corbel(s), {hookCount} hook(s), {dowelCount} dowel family instance(s).");
            _diag.AppendLine($"Corbel planes kept after de-duplication: {corbelRefs.Count}. Hooks: {tempHooks.Count}. Bottom dowel found: {(extremeBottomDowel != null ? "yes" : "NO")}.");
            if (dowelCount > 0)
                _diag.AppendLine($"Dowel probe: lowest dowel bbox is {dowelLowestMm:0.#} mm from the column bottom (negative = below). " +
                                 $"Horizontal faces: {dowelHorizFaces}, with usable references: {dowelHorizRefs}.");
            _diag.AppendLine("Families in the assembly (non-column):");
            foreach (var kv in roles.OrderBy(k => k.Value))
                _diag.AppendLine($"  {kv.Key}  ->  {kv.Value}");
        }

        // =====================================================================
        //  Right / Left elevation harvesting
        // =====================================================================

        private void HarvestRightViewZones(List<Element> elementsToProcess, double colBottomZ, double colTopZ,
            out List<Reference> rebarZoneRefs, out Reference extremeBottomDowel)
        {
            rebarZoneRefs = new List<Reference>();
            extremeBottomDowel = null;
            double absoluteLowestDowelZ = double.MaxValue;

            XYZ viewUp = _view.UpDirection;
            Options geomOptions = new Options { ComputeReferences = true, View = _view, IncludeNonVisibleObjects = true };
            List<Tuple<Reference, double>> allRebarPlanes = new List<Tuple<Reference, double>>();

            foreach (Element elem in elementsToProcess)
            {
                if (elem is Autodesk.Revit.DB.Structure.Rebar rebar)
                {
                    GeometryElement geomElem = rebar.get_Geometry(geomOptions);
                    if (geomElem == null) continue;

                    foreach (PlanarFace pf in GetHorizontalFaces(geomElem, viewUp))
                    {
                        double z = pf.Origin.DotProduct(viewUp);
                        if (z < (colTopZ - 0.05) && z > (colBottomZ + 0.05))
                            allRebarPlanes.Add(new Tuple<Reference, double>(pf.Reference, z));
                    }

                    TrackLowestDowel(geomElem, viewUp, colBottomZ, ref absoluteLowestDowelZ, ref extremeBottomDowel);
                }
            }

            double lastZ = double.MinValue;
            foreach (var plane in allRebarPlanes.OrderBy(p => p.Item2))
            {
                if (lastZ == double.MinValue || Math.Abs(plane.Item2 - lastZ) > RebarZoneMinSeparationFt)
                {
                    rebarZoneRefs.Add(plane.Item1);
                    lastZ = plane.Item2;
                }
            }
        }

        // =====================================================================
        //  Concrete face finders
        // =====================================================================

        private Reference[] GetLeftAndRightColumnFaces(View activeView, FamilyInstance column)
        {
            Reference leftRef = null, rightRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return new Reference[] { null, null };

            XYZ viewRight = activeView.RightDirection;
            double maxLeftArea = -1.0, maxRightArea = -1.0;

            foreach (Solid solid in GetSolids(geomElem))
            {
                foreach (Face face in solid.Faces)
                {
                    if (face is PlanarFace pf)
                    {
                        double dot = pf.FaceNormal.DotProduct(viewRight);
                        if (dot < -0.95 && pf.Area > maxLeftArea) { maxLeftArea = pf.Area; leftRef = pf.Reference; }
                        else if (dot > 0.95 && pf.Area > maxRightArea) { maxRightArea = pf.Area; rightRef = pf.Reference; }
                    }
                }
            }
            return new Reference[] { leftRef, rightRef };
        }

        private Reference GetLowestColumnFace(View activeView, FamilyInstance column)
        {
            Reference bottomRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return null;

            XYZ viewUp = activeView.UpDirection;
            double minElevation = double.MaxValue;

            foreach (PlanarFace pf in GetHorizontalFaces(geomElem, viewUp, 0.95))
            {
                double elevation = pf.Origin.DotProduct(viewUp);
                if (elevation < minElevation) { minElevation = elevation; bottomRef = pf.Reference; }
            }
            return bottomRef;
        }

        private Reference GetHighestColumnFace(View activeView, FamilyInstance column)
        {
            Reference topRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return null;

            XYZ viewUp = activeView.UpDirection;
            double maxElevation = double.MinValue;

            foreach (PlanarFace pf in GetHorizontalFaces(geomElem, viewUp, 0.95))
            {
                double elevation = pf.Origin.DotProduct(viewUp);
                if (elevation > maxElevation) { maxElevation = elevation; topRef = pf.Reference; }
            }
            return topRef;
        }
    }

    public class ColumnSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FamilyInstance || elem is AssemblyInstance;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}