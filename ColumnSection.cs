using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace StructAutoDetailing
{
    /// <summary>
    /// Dimensions the cross-section (plan) view of a precast column assembly.
    /// Workflow: click the button -> tick the dimensions you want in the pop-up -> click the column / assembly in the view.
    ///   - overall length (left face -> right face) and overall height (top face -> bottom face)
    ///   - one dimension row / column per corrugated metal sleeve (CMS) group, measured to the
    ///     CENTRE of each tube: face -> centres -> face
    /// Rows stack above the column and tiers stack to its left. Nearest = CMS 1, then CMS 2, ..., then overall.
    ///
    /// Tube centres are found from the tube geometry (circle centre) and a small marker line is drawn
    /// at each centre to dimension to. Markers use the "&lt;Invisible lines&gt;" style when Revit accepts it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ColumnSectionDimensioningCommand : IExternalCommand
    {
        // =====================================================================
        //  SETTINGS
        // =====================================================================

        // Spacing is PAPER millimetres (multiplied by the view scale), nearest row/tier first.
        private const double RowFirstMm = 12.0;     // column face -> first (CMS 1) row / tier
        private const double RowStepMm = 9.0;       // spacing between rows / tiers
        private const double LabelGapMm = 2.0;      // gap between the right face and the row label
        private const double MarkerHalfLengthMm = 1.5; // half length of the centre marker lines (paper mm)

        // Family / type name that identifies a sleeve tube; the digits become the group number.
        // "PPS_Dowel Tube (CMS1)" -> group 1 -> label "CMS 1"
        private const string TubeNamePattern = @"CMS\s*(\d+)";
        private const string CmsLabelFormat = "CMS {0}";

        private const bool DrawLinkLines = true;    // L-shaped lines joining each vertical CMS tier to its horizontal row
        private const bool DrawLabelLine = true;    // line carrying each row out under its label
        private const bool ShowDiagnostics = true;

        private const double MinSegmentFt = 0.03;   // reject dimension segments shorter than ~9 mm
        // Only used if the circle centre cannot be read from the tube geometry and the bounding box is used instead.
        // Shifts the centre toward the middle of the column by this many mm (0 = off).
        private const double TubeCentreInwardCorrectionMm = 0.0;

        private const double SamePositionFt = 0.01; // tube centres closer than ~3 mm count as the same position

        // =====================================================================
        //  Working state
        // =====================================================================
        private Document _doc;
        private View _view;
        private double _scale;
        private XYZ _right, _up, _dir;
        private double _depth;
        private ElementId _textTypeId;
        private double _textHeight;
        private GraphicsStyle _markerStyle;   // null = default (visible) line style
        private string _warnings = "";
        private readonly StringBuilder _diag = new StringBuilder();
        private readonly List<Tuple<Dimension, string>> _created = new List<Tuple<Dimension, string>>();

        // remembered between runs in the same Revit session
        private static SectionDimOptions _last = new SectionDimOptions();

        private static readonly Regex TubeRegex = new Regex(TubeNamePattern, RegexOptions.IgnoreCase);

        private double Off(double paperMm) => paperMm * _scale / 304.8;
        private XYZ P(double x, double y) => _right * x + _up * y + _dir * _depth;

        private class FaceInfo
        {
            public Reference Ref;
            public double Area;
            public XYZ Origin;
        }

        private class TubeCenter
        {
            public FamilyInstance Tube;
            public double X, Y;       // view coordinates (feet)
            public string Source;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            switch (view.ViewType)
            {
                case ViewType.Section:
                case ViewType.Detail:
                case ViewType.Elevation:
                case ViewType.FloorPlan:
                case ViewType.EngineeringPlan:
                case ViewType.CeilingPlan:
                    break;
                default:
                    TaskDialog.Show("Error", "Please run this command from a 2D column section view.");
                    return Result.Failed;
            }

            try
            {
                // ---- Pop-up: which dimensions should be placed?
                Dictionary<int, int> detected = DetectCmsGroups(doc, view);   // null = unknown (not an assembly view)
                SectionDimOptions choice;
                using (SectionDimensionOptionsForm form = new SectionDimensionOptionsForm(_last, detected))
                {
                    if (form.ShowDialog(new Win32Owner(commandData.Application.MainWindowHandle)) != WF.DialogResult.OK)
                        return Result.Cancelled;
                    choice = form.Read();
                }
                _last = choice;
                if (!choice.AnyChosen)
                {
                    TaskDialog.Show("Smart Column Section Dimensioning", "No dimensions were ticked - nothing to place.");
                    return Result.Cancelled;
                }

                // ---- Ask the user to pick the column / assembly in the view
                Reference pick = uidoc.Selection.PickObject(ObjectType.Element, new SectionAssemblyFilter(),
                    "Select the column assembly in this section view");
                Element picked = doc.GetElement(pick);

                List<Element> members = ResolveMembers(doc, picked, view);

                // ---- Main column = the largest structural column among the members
                FamilyInstance mainColumn = null;
                double bestSize = -1;
                foreach (Element m in members)
                {
                    if (m is FamilyInstance fi && fi.Category != null &&
                        fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                    {
                        BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                        double size = bb == null ? 0 : (bb.Max - bb.Min).GetLength();
                        if (size > bestSize) { bestSize = size; mainColumn = fi; }
                    }
                }
                if (mainColumn == null)
                {
                    TaskDialog.Show("Error", "Could not identify the main structural column in the selected assembly.");
                    return Result.Failed;
                }

                // ---- Init state
                _doc = doc;
                _view = view;
                _scale = Math.Max(1, view.Scale);
                _right = view.RightDirection;
                _up = view.UpDirection;
                _dir = view.ViewDirection;
                _depth = view.Origin.DotProduct(_dir);
                _markerStyle = null;
                _warnings = "";
                _diag.Clear();
                _created.Clear();

                _textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                double textSizeFt = 2.5 / 304.8;
                TextNoteType tnt = doc.GetElement(_textTypeId) as TextNoteType;
                Parameter sizeParam = tnt?.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (sizeParam != null && sizeParam.AsDouble() > 0) textSizeFt = sizeParam.AsDouble();
                _textHeight = textSizeFt * _scale;

                // ---- Column faces (left / right / top / bottom in the view)
                FaceInfo left, right, top, bottom;
                FindColumnFaces(mainColumn, out left, out right, out top, out bottom);
                if (left == null || right == null || top == null || bottom == null)
                {
                    TaskDialog.Show("Error",
                        "Could not find all four column faces in this view.\n" +
                        $"Left: {(left != null)}, Right: {(right != null)}, Top: {(top != null)}, Bottom: {(bottom != null)}");
                    return Result.Failed;
                }

                double leftX = left.Origin.DotProduct(_right);
                double rightX = right.Origin.DotProduct(_right);
                double topY = top.Origin.DotProduct(_up);
                double bottomY = bottom.Origin.DotProduct(_up);

                _colMidX = (leftX + rightX) / 2.0;
                _colMidY = (topY + bottomY) / 2.0;

                // ---- Tubes grouped by CMS number (ascending: nearest row first) - only the ticked groups
                _colMidX = (leftX + rightX) / 2.0;
                _colMidY = (topY + bottomY) / 2.0;

                SortedDictionary<int, List<TubeCenter>> groups = new SortedDictionary<int, List<TubeCenter>>();
                if (choice.CmsGroups.Count > 0)
                {
                    foreach (Element m in members)
                    {
                        if (!(m is FamilyInstance fi) || ReferenceEquals(fi, mainColumn)) continue;
                        Match match = TubeRegex.Match(fi.Symbol.FamilyName + " " + fi.Symbol.Name);
                        if (!match.Success) continue;
                        int n;
                        if (!int.TryParse(match.Groups[1].Value, out n)) continue;
                        if (!choice.CmsGroups.Contains(n)) continue;

                        double cx, cy; string src;
                        if (!TryGetCenter(fi, out cx, out cy, out src))
                        {
                            _diag.AppendLine($"Could not find a centre for tube id {fi.Id.Value} ({fi.Symbol.FamilyName}).");
                            continue;
                        }
                        if (!groups.ContainsKey(n)) groups[n] = new List<TubeCenter>();
                        groups[n].Add(new TubeCenter { Tube = fi, X = cx, Y = cy, Source = src });
                    }
                    foreach (int wanted in choice.CmsGroups)
                        if (!groups.ContainsKey(wanted))
                            _diag.AppendLine($"CMS {wanted} was ticked, but no CMS {wanted} tubes were found in the selected assembly.");
                }

                _diag.AppendLine($"Column: {mainColumn.Symbol.FamilyName}. Size in view: " +
                                 $"{(rightX - leftX) * 304.8:0} x {(topY - bottomY) * 304.8:0} mm.");
                foreach (var g in groups)
                {
                    string pts = string.Join("  ", g.Value
                        .OrderBy(c => c.Y).ThenBy(c => c.X)
                        .Select(c => $"({(c.X - leftX) * 304.8:0},{(topY - c.Y) * 304.8:0})"));
                    _diag.AppendLine($"CMS {g.Key}: {g.Value.Count} tube(s), centres (mm from left face, from top face) [{g.Value[0].Source}]: {pts}");
                }

                using (Transaction t = new Transaction(doc, "Smart Column Section Dimensioning"))
                {
                    t.Start();

                    // Can Revit dimension to invisible marker lines? If not, use a visible default style.
                    if (groups.Count > 0 || choice.CentreLength)
                        ChooseMarkerStyle(left, right, leftX, rightX, topY, _colMidX, _colMidY);

                    // Horizontal rows stack above the column: CMS groups, then the centre row, then the overall.
                    int idx = 0;

                    foreach (var kv in groups)
                    {
                        int n = kv.Key;
                        List<TubeCenter> centers = kv.Value;
                        double offMm = RowFirstMm + idx * RowStepMm;
                        idx++;

                        bool hOk = false, vOk = false;
                        double rowY = topY + Off(offMm);
                        double tierX = leftX - Off(offMm);

                        // Horizontal row: left face -> tube centres -> right face (label at the right end)
                        TryDim($"CMS {n} horizontal", () =>
                        {
                            Line line = Line.CreateBound(P(leftX, rowY), P(rightX, rowY));
                            List<Reference> cands = MakeMarkerRefs(DistinctBy(centers, c => c.X), true);
                            ReferenceArray arr = BuildValidArray(new[] { left.Ref, right.Ref }, cands, line, $"CMS {n} horizontal");
                            if (arr.Size <= 2) { _diag.AppendLine($"CMS {n} horizontal skipped: no usable tube centres."); return; }
                            Dimension d = _doc.Create.NewDimension(_view, line, arr);
                            LogDim(d, $"CMS {n} horizontal");
                            hOk = d != null;
                            if (hOk) AddRowLabel(rowY, rightX, string.Format(CmsLabelFormat, n));
                        });

                        // Vertical tier: top face -> tube centres -> bottom face
                        TryDim($"CMS {n} vertical", () =>
                        {
                            Line line = Line.CreateBound(P(tierX, bottomY), P(tierX, topY));
                            List<Reference> cands = MakeMarkerRefs(DistinctBy(centers, c => c.Y), false);
                            ReferenceArray arr = BuildValidArray(new[] { top.Ref, bottom.Ref }, cands, line, $"CMS {n} vertical");
                            if (arr.Size <= 2) { _diag.AppendLine($"CMS {n} vertical skipped: no usable tube centres."); return; }
                            Dimension d = _doc.Create.NewDimension(_view, line, arr);
                            LogDim(d, $"CMS {n} vertical");
                            vOk = d != null;
                        });

                        // L-shaped link joining the vertical tier to the horizontal row
                        if (DrawLinkLines && hOk && vOk)
                        {
                            TryDim($"CMS {n} link lines", () =>
                            {
                                DrawLine(P(tierX, topY), P(tierX, rowY));
                                DrawLine(P(tierX, rowY), P(leftX, rowY));
                            });
                        }
                    }

                    // ---- Centre dimension (e.g. L2): left face -> column centre -> right face
                    if (choice.CentreLength)
                    {
                        double centreOffMm = RowFirstMm + idx * RowStepMm;
                        idx++;

                        TryDim("Centre length", () =>
                        {
                            double rowY = topY + Off(centreOffMm);
                            Line line = Line.CreateBound(P(leftX, rowY), P(rightX, rowY));

                            string how;
                            Reference centreRef = GetColumnCentreRef(mainColumn, left, right, line, _colMidX, _colMidY, out how);
                            if (centreRef == null) { _diag.AppendLine("Centre length skipped: no centre reference available."); return; }

                            ReferenceArray arr = new ReferenceArray();
                            arr.Append(left.Ref);
                            arr.Append(centreRef);
                            arr.Append(right.Ref);
                            Dimension d = _doc.Create.NewDimension(_view, line, arr);
                            LogDim(d, $"Centre length (centre from {how})");
                            if (d != null && !string.IsNullOrWhiteSpace(choice.CentreLabel))
                                AddRowLabel(rowY, rightX, choice.CentreLabel);
                        });
                    }

                    // ---- Overall length (outermost row)
                    if (choice.OverallLength)
                    {
                        double lengthOffMm = RowFirstMm + idx * RowStepMm;

                        TryDim("Overall length", () =>
                        {
                            double rowY = topY + Off(lengthOffMm);
                            Line line = Line.CreateBound(P(leftX, rowY), P(rightX, rowY));
                            ReferenceArray arr = new ReferenceArray();
                            arr.Append(left.Ref);
                            arr.Append(right.Ref);
                            Dimension d = _doc.Create.NewDimension(_view, line, arr);
                            LogDim(d, "Overall length");
                            if (d != null && !string.IsNullOrWhiteSpace(choice.LengthLabel))
                                AddRowLabel(rowY, rightX, choice.LengthLabel);
                        });
                    }

                    // ---- Overall width (outermost tier on the left; the centre row has no vertical partner)
                    if (choice.OverallWidth)
                    {
                        double widthOffMm = RowFirstMm + groups.Count * RowStepMm;

                        TryDim("Overall width", () =>
                        {
                            double tierX = leftX - Off(widthOffMm);
                            Line line = Line.CreateBound(P(tierX, bottomY), P(tierX, topY));
                            ReferenceArray arr = new ReferenceArray();
                            arr.Append(bottom.Ref);
                            arr.Append(top.Ref);
                            Dimension d = _doc.Create.NewDimension(_view, line, arr);
                            LogDim(d, "Overall width");
                            if (d != null && !string.IsNullOrWhiteSpace(choice.WidthLabel))
                                AddColumnLabel(tierX, topY, choice.WidthLabel);
                        });
                    }

                    t.Commit();
                }

                foreach (var item in _created)
                    if (!item.Item1.IsValidObject)
                        _diag.AppendLine($"WARNING: '{item.Item2}' was removed by Revit when the transaction committed.");

                string report = "";
                if (!string.IsNullOrEmpty(_warnings)) report += "Failures:\n" + _warnings + "\n";
                if (ShowDiagnostics && _diag.Length > 0) report += "Diagnostics:\n" + _diag;
                if (!string.IsNullOrEmpty(report)) TaskDialog.Show("Smart Column Section Dimensioning", report);
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
        //  Assembly resolution
        // =====================================================================

        /// <summary>CMS groups (number -> tube count) in the view's assembly, or null if it is not an assembly view.</summary>
        private static Dictionary<int, int> DetectCmsGroups(Document doc, View view)
        {
            ElementId aid = view.AssociatedAssemblyInstanceId;
            if (aid == null || aid == ElementId.InvalidElementId) return null;
            AssemblyInstance assembly = doc.GetElement(aid) as AssemblyInstance;
            if (assembly == null) return null;

            Dictionary<int, int> counts = new Dictionary<int, int>();
            foreach (ElementId id in assembly.GetMemberIds())
            {
                FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                if (fi == null) continue;
                Match match = TubeRegex.Match(fi.Symbol.FamilyName + " " + fi.Symbol.Name);
                int n;
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out n)) continue;
                counts[n] = counts.ContainsKey(n) ? counts[n] + 1 : 1;
            }
            return counts;
        }

        /// <summary>
        /// Reference for the middle of the column: the family's own centre plane when Revit accepts it AND it gives
        /// two equal halves, otherwise a marker line placed exactly midway between the left and right faces.
        /// </summary>
        private Reference GetColumnCentreRef(FamilyInstance column, FaceInfo left, FaceInfo right, Line line,
            double midX, double midY, out string how)
        {
            how = "";
            try
            {
                bool useLeftRight = Math.Abs(column.HandOrientation.DotProduct(_right)) >=
                                    Math.Abs(column.FacingOrientation.DotProduct(_right));
                IList<Reference> list = column.GetReferences(useLeftRight
                    ? FamilyInstanceReferenceType.CenterLeftRight
                    : FamilyInstanceReferenceType.CenterFrontBack);

                if (list != null && list.Count > 0)
                {
                    ReferenceArray arr = new ReferenceArray();
                    arr.Append(left.Ref);
                    arr.Append(list[0]);
                    arr.Append(right.Ref);

                    List<double> segs;
                    if (TryDimensionSegments(arr, line, out segs) && segs.Count == 2 &&
                        Math.Abs(segs[0] - segs[1]) < 0.005 && segs.All(v => v >= MinSegmentFt))
                    {
                        how = "column centre plane";
                        return list[0];
                    }
                }
            }
            catch { }

            List<Reference> refs = MakeMarkerRefs(new List<TubeCenter> { new TubeCenter { X = midX, Y = midY } }, true);
            how = "marker line";
            return refs.Count > 0 ? refs[0] : null;
        }

        private static List<Element> ResolveMembers(Document doc, Element picked, View view)
        {
            AssemblyInstance assembly = picked as AssemblyInstance;

            if (assembly == null && picked.AssemblyInstanceId != null &&
                picked.AssemblyInstanceId != ElementId.InvalidElementId)
                assembly = doc.GetElement(picked.AssemblyInstanceId) as AssemblyInstance;

            if (assembly == null)
            {
                ElementId viewAssembly = view.AssociatedAssemblyInstanceId;
                if (viewAssembly != null && viewAssembly != ElementId.InvalidElementId)
                    assembly = doc.GetElement(viewAssembly) as AssemblyInstance;
            }

            List<Element> members = new List<Element>();
            if (assembly != null)
            {
                foreach (ElementId id in assembly.GetMemberIds()) members.Add(doc.GetElement(id));
            }
            else
            {
                members.Add(picked);
            }
            return members;
        }

        // =====================================================================
        //  Tube centres
        // =====================================================================

        /// <summary>
        /// Centre of the tube circle in view coordinates. Order of preference:
        /// circle/arc facing the view (Fine detail 3D geometry, then view geometry)
        /// -> solid edge extents -> bounding box -> location point.
        /// </summary>
        private bool TryGetCenter(FamilyInstance inst, out double cx, out double cy, out string source)
        {
            cx = cy = 0; source = "";
            try
            {
                Options[] optionSets =
                {
                    new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = true },
                    new Options { View = _view, ComputeReferences = false, IncludeNonVisibleObjects = true }
                };

                bool haveExtents = false;
                double eMinX = 0, eMaxX = 0, eMinY = 0, eMaxY = 0;

                foreach (Options opts in optionSets)
                {
                    GeometryElement ge = inst.get_Geometry(opts);
                    Arc best = null;
                    double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                    bool anyPoint = false;

                    foreach (GeometryObject o in FlattenGeometry(ge))
                    {
                        if (o is Arc a) Consider(a, ref best);
                        else if (o is Solid s && s.Faces.Size > 0)
                        {
                            foreach (Edge e in s.Edges)
                            {
                                if (e.AsCurve() is Arc ea) Consider(ea, ref best);
                                foreach (XYZ p in e.Tessellate())
                                {
                                    double x = p.DotProduct(_right), y = p.DotProduct(_up);
                                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                                    anyPoint = true;
                                }
                            }
                        }
                    }

                    // 1) circle facing the view: its centre is the true tube centre
                    if (best != null)
                    {
                        cx = best.Center.DotProduct(_right);
                        cy = best.Center.DotProduct(_up);
                        source = "circle centre";
                        return true;
                    }

                    if (anyPoint && !haveExtents)
                    {
                        haveExtents = true;
                        eMinX = minX; eMaxX = maxX; eMinY = minY; eMaxY = maxY;
                    }
                }

                // 2) middle of the solid's extents
                if (haveExtents)
                {
                    cx = (eMinX + eMaxX) / 2.0;
                    cy = (eMinY + eMaxY) / 2.0;
                    source = "solid extents";
                    return true;
                }

                // 3) middle of the element's bounding box, measured on the view axes
                double bx0, bx1, by0, by1;
                if (TryGetViewExtents(inst, out bx0, out bx1, out by0, out by1))
                {
                    cx = (bx0 + bx1) / 2.0;
                    cy = (by0 + by1) / 2.0;
                    source = "bounding box";
                    ApplyInwardCorrection(ref cx, ref cy);
                    return true;
                }

                // 4) family location point (last resort - it sits on the column face for these tubes)
                LocationPoint lp = inst.Location as LocationPoint;
                if (lp != null)
                {
                    cx = lp.Point.DotProduct(_right);
                    cy = lp.Point.DotProduct(_up);
                    source = "location point";
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Bounding box of an element projected on the view's right / up axes.</summary>
        private bool TryGetViewExtents(Element e, out double x0, out double x1, out double y0, out double y1)
        {
            x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue;
            BoundingBoxXYZ bb = e.get_BoundingBox(_view) ?? e.get_BoundingBox(null);
            if (bb == null) return false;
            Transform tf = bb.Transform ?? Transform.Identity;

            double[] xs = { bb.Min.X, bb.Max.X };
            double[] ys = { bb.Min.Y, bb.Max.Y };
            double[] zs = { bb.Min.Z, bb.Max.Z };
            foreach (double bx in xs)
                foreach (double by in ys)
                    foreach (double bz in zs)
                    {
                        XYZ pt = tf.OfPoint(new XYZ(bx, by, bz));
                        double px = pt.DotProduct(_right), py = pt.DotProduct(_up);
                        if (px < x0) x0 = px; if (px > x1) x1 = px;
                        if (py < y0) y0 = py; if (py > y1) y1 = py;
                    }
            return true;
        }

        // set once per run by Execute (column middle, view coordinates)
        private double _colMidX, _colMidY;

        private void ApplyInwardCorrection(ref double cx, ref double cy)
        {
            if (TubeCentreInwardCorrectionMm == 0.0) return;
            double corr = TubeCentreInwardCorrectionMm / 304.8;
            double dx = cx - _colMidX, dy = cy - _colMidY;
            if (Math.Abs(dx) >= Math.Abs(dy)) cx -= Math.Sign(dx) * corr;   // tube on a left/right face
            else cy -= Math.Sign(dy) * corr;                                 // tube on a top/bottom face
        }

        /// <summary>One-line summary of what geometry a tube exposes, to diagnose centre problems.</summary>
        private string ProbeGeometry(FamilyInstance inst, double leftX, double topY)
        {
            StringBuilder sb = new StringBuilder();
            string[] names = { "Fine", "View" };
            Options[] sets =
            {
                new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true },
                new Options { View = _view, IncludeNonVisibleObjects = true }
            };
            for (int i = 0; i < sets.Length; i++)
            {
                int arcs = 0, solids = 0, edgeArcs = 0, lines = 0, meshes = 0, other = 0;
                foreach (GeometryObject o in FlattenGeometry(inst.get_Geometry(sets[i])))
                {
                    if (o is Arc) arcs++;
                    else if (o is Solid so && so.Faces.Size > 0)
                    {
                        solids++;
                        foreach (Edge e in so.Edges) if (e.AsCurve() is Arc) edgeArcs++;
                    }
                    else if (o is Line) lines++;
                    else if (o is Mesh) meshes++;
                    else other++;
                }
                sb.Append($"{names[i]}: {solids} solid(s), {edgeArcs} edge arc(s), {arcs} arc(s), {lines} line(s), {meshes} mesh(es), {other} other; ");
            }

            double x0, x1, y0, y1;
            if (TryGetViewExtents(inst, out x0, out x1, out y0, out y1))
                sb.Append($"bbox x {(x0 - leftX) * 304.8:0}..{(x1 - leftX) * 304.8:0}, y {(topY - y1) * 304.8:0}..{(topY - y0) * 304.8:0} mm from left/top face");
            return sb.ToString();
        }

        private void Consider(Arc arc, ref Arc best)
        {
            if (Math.Abs(arc.Normal.DotProduct(_dir)) < 0.95) return;   // must face the view
            if (best == null || arc.Radius > best.Radius) best = arc;
        }

        private static List<TubeCenter> DistinctBy(List<TubeCenter> list, Func<TubeCenter, double> key)
        {
            List<TubeCenter> result = new List<TubeCenter>();
            foreach (TubeCenter c in list.OrderBy(key))
            {
                if (result.Count == 0 || Math.Abs(key(c) - key(result[result.Count - 1])) > SamePositionFt)
                    result.Add(c);
            }
            return result;
        }

        // =====================================================================
        //  Centre marker lines (what we actually dimension to)
        // =====================================================================

        private DetailCurve CreateMarker(double x, double y, bool vertical, GraphicsStyle style)
        {
            double half = Off(MarkerHalfLengthMm);
            Line l = vertical
                ? Line.CreateBound(P(x, y - half), P(x, y + half))
                : Line.CreateBound(P(x - half, y), P(x + half, y));
            DetailCurve dc = _doc.Create.NewDetailCurve(_view, l);
            if (style != null) dc.LineStyle = style;
            return dc;
        }

        private static Reference MarkerRef(DetailCurve dc)
        {
            Reference r = null;
            try { r = dc.GeometryCurve?.Reference; } catch { }
            if (r == null)
            {
                try { r = new Reference(dc); } catch { }
            }
            return r;
        }

        /// <summary>
        /// Creates one marker per position and returns its reference.
        /// vertical = true  -> vertical marker, used by HORIZONTAL dimensions (measures X)
        /// vertical = false -> horizontal marker, used by VERTICAL dimensions (measures Y)
        /// </summary>
        private List<Reference> MakeMarkerRefs(List<TubeCenter> positions, bool vertical)
        {
            List<DetailCurve> markers = new List<DetailCurve>();
            foreach (TubeCenter c in positions)
            {
                try { markers.Add(CreateMarker(c.X, c.Y, vertical, _markerStyle)); }
                catch (Exception ex) { _diag.AppendLine($"Marker failed: {ex.Message}"); }
            }

            _doc.Regenerate();

            List<Reference> refs = new List<Reference>();
            foreach (DetailCurve dc in markers)
            {
                Reference r = MarkerRef(dc);
                if (r != null) refs.Add(r);
            }
            return refs;
        }

        private GraphicsStyle FindInvisibleStyle()
        {
            foreach (GraphicsStyle gs in new FilteredElementCollector(_doc).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>())
            {
                Category c = gs.GraphicsStyleCategory;
                if (c == null || gs.GraphicsStyleType != GraphicsStyleType.Projection) continue;
                if (c.Name.IndexOf("invisible", StringComparison.OrdinalIgnoreCase) >= 0) return gs;
            }
            return null;
        }

        /// <summary>Test whether Revit will dimension to an invisible marker; otherwise fall back to a visible one.</summary>
        private void ChooseMarkerStyle(FaceInfo left, FaceInfo right, double leftX, double rightX, double topY, double sampleX, double sampleY)
        {
            _markerStyle = null;
            GraphicsStyle inv = FindInvisibleStyle();
            if (inv == null)
            {
                _diag.AppendLine("Marker lines: '<Invisible lines>' style not found - using the default (visible) line style.");
                return;
            }

            DetailCurve probe = null;
            bool ok = false;
            try
            {
                probe = CreateMarker(sampleX, sampleY, true, inv);
                _doc.Regenerate();
                Reference pr = MarkerRef(probe);
                if (pr != null)
                {
                    ReferenceArray arr = new ReferenceArray();
                    arr.Append(left.Ref);
                    arr.Append(pr);
                    arr.Append(right.Ref);
                    double rowY = topY + Off(RowFirstMm);
                    ok = TestDimension(arr, Line.CreateBound(P(leftX, rowY), P(rightX, rowY)));
                }
            }
            catch (Exception ex) { _diag.AppendLine($"Invisible marker test failed: {ex.Message}"); }
            finally
            {
                if (probe != null) { try { _doc.Delete(probe.Id); } catch { } }
            }

            if (ok)
            {
                _markerStyle = inv;
                _diag.AppendLine("Marker lines: Revit accepts '<Invisible lines>' - centre markers are invisible.");
            }
            else
            {
                _diag.AppendLine("Marker lines: Revit would not dimension to invisible lines - using the default (visible) style, drawn as small centre marks.");
            }
        }

        // =====================================================================
        //  Dimension validation (trial create -> read segments -> roll back)
        // =====================================================================

        private bool TryDimensionSegments(ReferenceArray refs, Line line, out List<double> segs)
        {
            segs = new List<double>();
            using (SubTransaction st = new SubTransaction(_doc))
            {
                st.Start();
                try
                {
                    Dimension d = _doc.Create.NewDimension(_view, line, refs);
                    if (d == null) return false;
                    _doc.Regenerate();
                    segs = ReadSegments(d);
                    return true;
                }
                catch { return false; }
                finally { st.RollBack(); }
            }
        }

        private static List<double> ReadSegments(Dimension d)
        {
            List<double> list = new List<double>();
            if (d.NumberOfSegments > 0)
            {
                foreach (DimensionSegment seg in d.Segments)
                    if (seg.Value.HasValue) list.Add(seg.Value.Value);
            }
            else if (d.Value.HasValue)
            {
                list.Add(d.Value.Value);
            }
            return list;
        }

        private bool TestDimension(ReferenceArray refs, Line line)
        {
            List<double> segs;
            if (!TryDimensionSegments(refs, line, out segs)) return false;
            return segs.All(v => v >= MinSegmentFt);
        }

        /// <summary>Adds centre references one at a time, keeping only those Revit accepts without a zero-length segment.</summary>
        private ReferenceArray BuildValidArray(IEnumerable<Reference> required, IEnumerable<Reference> candidates,
            Line line, string tag)
        {
            ReferenceArray arr = new ReferenceArray();
            foreach (Reference r in required) arr.Append(r);

            int total = 0, kept = 0;
            foreach (Reference c in candidates)
            {
                total++;
                ReferenceArray trial = new ReferenceArray();
                foreach (Reference r in arr) trial.Append(r);
                trial.Append(c);

                if (TestDimension(trial, line)) { arr = trial; kept++; }
            }
            _diag.AppendLine($"{tag}: {kept} of {total} centre line(s) accepted.");
            return arr;
        }

        private void LogDim(Dimension dim, string tag)
        {
            if (dim == null) return;
            _created.Add(Tuple.Create(dim, tag));
            try
            {
                _doc.Regenerate();
                string vals = string.Join(" | ", ReadSegments(dim).Select(v => (v * 304.8).ToString("0.#")));
                _diag.AppendLine($"{tag}: segments (mm) = {vals}");
            }
            catch (Exception ex) { _diag.AppendLine($"{tag}: could not read segments ({ex.Message})"); }
        }

        private void TryDim(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { _warnings += $"- {name}: {ex.Message}\n"; }
        }

        // =====================================================================
        //  Labels and lines
        // =====================================================================

        /// <summary>Label to the right of the right face, sitting on the row's dimension line.</summary>
        private void AddRowLabel(double rowY, double rightX, string text)
        {
            try
            {
                double h = _textHeight;
                double width = text.Length * h * 0.95;

                XYZ lineStart = P(rightX, rowY);
                XYZ textStart = lineStart + _right * Off(LabelGapMm);

                // TextNote insertion point is the top-left corner -> lift above the line
                CreateNote(textStart + _up * (h * 1.45), text, 0.0);

                if (DrawLabelLine)
                    DrawLine(lineStart, textStart + _right * (width + h * 0.5));
            }
            catch (Exception ex) { _warnings += $"- Label '{text}': {ex.Message}\n"; }
        }

        /// <summary>Rotated label above a vertical tier (overall width label).</summary>
        private void AddColumnLabel(double tierX, double topY, string text)
        {
            try
            {
                double h = _textHeight;
                double width = text.Length * h * 0.95;

                XYZ lineStart = P(tierX, topY);
                XYZ textStart = lineStart + _up * Off(LabelGapMm);

                CreateNote(textStart - _right * (h * 1.45), text, Math.PI / 2.0);

                if (DrawLabelLine)
                    DrawLine(lineStart, textStart + _up * (width + h * 0.5));
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
        //  Column faces
        // =====================================================================

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

        /// <summary>
        /// The four side faces of the column body as seen in the view. The largest face in each direction
        /// is used, so a wider head block or small recesses never get picked by mistake.
        /// </summary>
        private void FindColumnFaces(FamilyInstance column, out FaceInfo left, out FaceInfo right,
            out FaceInfo top, out FaceInfo bottom)
        {
            left = right = top = bottom = null;
            Options opts = new Options { ComputeReferences = true, View = _view };
            GeometryElement ge = column.get_Geometry(opts);
            if (ge == null) return;

            foreach (GeometryObject o in FlattenGeometry(ge))
            {
                if (!(o is Solid solid) || solid.Faces.Size == 0) continue;
                foreach (Face f in solid.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;

                    double dr = pf.FaceNormal.DotProduct(_right);
                    double du = pf.FaceNormal.DotProduct(_up);

                    if (dr < -0.95) Keep(ref left, pf);
                    else if (dr > 0.95) Keep(ref right, pf);
                    else if (du > 0.95) Keep(ref top, pf);
                    else if (du < -0.95) Keep(ref bottom, pf);
                }
            }
        }

        private static void Keep(ref FaceInfo slot, PlanarFace pf)
        {
            if (slot == null || pf.Area > slot.Area)
                slot = new FaceInfo { Ref = pf.Reference, Area = pf.Area, Origin = pf.Origin };
        }
    }

    /// <summary>What the user ticked in the pop-up.</summary>
    internal class SectionDimOptions
    {
        public bool OverallLength = true;
        public string LengthLabel = "LENGTH";

        public bool OverallWidth = true;
        public string WidthLabel = "WIDTH";

        public bool CentreLength = false;
        public string CentreLabel = "L2";

        public HashSet<int> CmsGroups = new HashSet<int>();

        public bool AnyChosen => OverallLength || OverallWidth || CentreLength || CmsGroups.Count > 0;
    }

    internal class Win32Owner : WF.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public Win32Owner(IntPtr handle) { Handle = handle; }
    }

    /// <summary>Check-box pop-up listing every dimension the tool can place in a section view.</summary>
    internal class SectionDimensionOptionsForm : WF.Form
    {
        private WF.CheckBox _chkLength, _chkWidth, _chkCentre;
        private WF.TextBox _txtLength, _txtWidth, _txtCentre;
        private readonly Dictionary<int, WF.CheckBox> _cms = new Dictionary<int, WF.CheckBox>();
        private readonly List<WF.CheckBox> _all = new List<WF.CheckBox>();

        public SectionDimensionOptionsForm(SectionDimOptions start, Dictionary<int, int> cmsCounts)
        {
            Text = "Section View Dimensioning";
            FormBorderStyle = WF.FormBorderStyle.FixedDialog;
            StartPosition = WF.FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int y = 14;
            AddText("Select the dimensions to place:", 14, y, 440, true);
            y += 30;

            AddText("Overall and centre dimensions", 14, y, 440, false);
            y += 24;
            AddRow("Overall length (horizontal)", start.OverallLength, start.LengthLabel, y, out _chkLength, out _txtLength); y += 28;
            AddRow("Overall width (vertical)", start.OverallWidth, start.WidthLabel, y, out _chkWidth, out _txtWidth); y += 28;
            AddRow("Centre dimension (left | centre | right)", start.CentreLength, start.CentreLabel, y, out _chkCentre, out _txtCentre); y += 36;

            AddText("Corrugated metal sleeves (centre to centre)", 14, y, 440, false);
            y += 24;

            if (cmsCounts == null)
            {
                // not an assembly view: we cannot look ahead, so offer the usual groups
                foreach (int n in new[] { 1, 2 })
                    AddCms(n, $"CMS {n}", start.CmsGroups.Contains(n), true, ref y);
            }
            else if (cmsCounts.Count == 0)
            {
                AddText("No CMS tubes found in this view.", 30, y, 420, false);
                y += 26;
            }
            else
            {
                foreach (var kv in cmsCounts.OrderBy(k => k.Key))
                    AddCms(kv.Key, $"CMS {kv.Key}  -  {kv.Value} tube(s) found", start.CmsGroups.Contains(kv.Key), true, ref y);
            }

            y += 10;
            WF.Button all = new WF.Button { Text = "Select all", Left = 14, Top = y, Width = 90 };
            WF.Button none = new WF.Button { Text = "Clear all", Left = 110, Top = y, Width = 90 };
            WF.Button ok = new WF.Button { Text = "OK", Left = 290, Top = y, Width = 80, DialogResult = WF.DialogResult.OK };
            WF.Button cancel = new WF.Button { Text = "Cancel", Left = 376, Top = y, Width = 80, DialogResult = WF.DialogResult.Cancel };
            all.Click += (s, e) => { foreach (WF.CheckBox c in _all) if (c.Enabled) c.Checked = true; };
            none.Click += (s, e) => { foreach (WF.CheckBox c in _all) c.Checked = false; };
            Controls.Add(all);
            Controls.Add(none);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            ClientSize = new SD.Size(470, y + 44);
        }

        public SectionDimOptions Read()
        {
            SectionDimOptions o = new SectionDimOptions
            {
                OverallLength = _chkLength.Checked,
                LengthLabel = _txtLength.Text.Trim(),
                OverallWidth = _chkWidth.Checked,
                WidthLabel = _txtWidth.Text.Trim(),
                CentreLength = _chkCentre.Checked,
                CentreLabel = _txtCentre.Text.Trim()
            };
            foreach (var kv in _cms)
                if (kv.Value.Checked && kv.Value.Enabled) o.CmsGroups.Add(kv.Key);
            return o;
        }

        private void AddText(string text, int x, int y, int w, bool bold)
        {
            WF.Label l = new WF.Label { Text = text, Left = x, Top = y, Width = w, Height = 20, AutoSize = false };
            if (bold) l.Font = new SD.Font(l.Font, SD.FontStyle.Bold);
            Controls.Add(l);
        }

        private void AddRow(string text, bool isChecked, string label, int y, out WF.CheckBox chk, out WF.TextBox txt)
        {
            chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 270, Checked = isChecked };
            WF.Label l = new WF.Label { Text = "Label:", Left = 306, Top = y + 3, Width = 44, Height = 20, AutoSize = false };
            txt = new WF.TextBox { Left = 354, Top = y, Width = 100, Text = label ?? "" };
            Controls.Add(chk);
            Controls.Add(l);
            Controls.Add(txt);
            _all.Add(chk);
        }

        private void AddCms(int number, string text, bool isChecked, bool enabled, ref int y)
        {
            WF.CheckBox chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 420, Checked = isChecked, Enabled = enabled };
            Controls.Add(chk);
            _cms[number] = chk;
            _all.Add(chk);
            y += 28;
        }
    }

    public class SectionAssemblyFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            elem is FamilyInstance || elem is AssemblyInstance || elem is Autodesk.Revit.DB.Structure.Rebar;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}