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
    /// BeamSection - dimensioning for ANY precast beam (structural framing) view.
    /// ONE command for every view kind:
    ///   - ELEVATION / PLAN (the view looks along the side of the beam, the beam runs across the screen)
    ///   - SECTION (the view looks along the beam axis)
    /// The kind is detected automatically from the beam axis and the view direction.
    ///
    /// Workflow: click the button -> tick the dimensions in the pop-up -> OK.
    ///           (A click on the beam is only asked for when a non-assembly view contains several beams.)
    ///
    /// Rows (measured along screen axis "A", stacked ABOVE the beam, nearest first):
    ///   embed groups (CMS n, then lifters L1) -> CORBEL (every corbel face) -> corbel envelope (elevation)
    ///   -> LEDGE (ledge | body) -> centre -> overall (LENGTH / WIDTH)
    /// Tiers (measured along screen axis "B", stacked LEFT of the beam, nearest first):
    ///   Tier 1 = every level (ledge top, notch floor, corbel faces, ...), Tier 2 = main levels (labelled DEPTH, or WIDTH in a plan).
    ///
    /// Everything comes from the model geometry / family reference planes - no hard-coded sizes or offsets.
    /// Family keyword lists are at the top of the class.
    /// Register in Revit as: StructAutoDetailing.BeamSectionCommand
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class BeamSectionCommand : IExternalCommand
    {
        // =====================================================================
        //  SETTINGS
        // =====================================================================

        // Spacing is PAPER millimetres (multiplied by the view scale), nearest row / tier first.
        private const double RowFirstMm = 12.0;
        private const double RowStepMm = 9.0;
        private const double LabelGapMm = 2.0;
        private const double MarkerHalfLengthMm = 1.5;

        private const bool ShowDiagnostics = true;

        // ---- family keyword lists (regular expressions, case-insensitive) - extend them for new families
        private const string TubePattern = @"CMS\s*(\d+)";
        private const string TubeWordPattern = @"cms|sleeve|corrugat|groutec|grout|dowel\s*tube";
        private const string LifterPattern = @"hock|hook|lifting|lifter|anchor";
        private const string LedgePattern = @"ledge";
        private const string CorbelPattern = @"corbel|nib|haunch|bracket";
        private const string CmsLabelFormat = "CMS {0}";
        private const string LifterDefaultLabel = "L1";

        private const bool DetectLedgesByProtrusion = true;

        // ---- tolerances (feet)
        private const double PosTolFt = 0.005;
        private const double MinSegmentFt = 0.03;
        private const double ClusterTolFt = 15.0 / 304.8;
        private const double ProtrusionTolFt = 0.05;
        private const double PlaneTolFt = 25.0 / 304.8;
        private const double DepthTolFt = 0.02;
        private const double SideFaceMinFraction = 0.02;
        private const double LevelMinFraction = 0.003;
        private const double MainLevelFraction = 0.15;

        private enum ViewKind { Elevation, Section }
        private enum BeamRole { None, Tube, Lifter, Ledge, Corbel }

        // =====================================================================
        //  Working state
        // =====================================================================
        private Document _doc;
        private View _view;
        private double _scale;
        private ViewKind _kind;
        private bool _isPlan;
        private XYZ _viewRight, _viewUp, _dir;
        private XYZ _A, _B;
        private double _depth;
        private ElementId _textTypeId;
        private double _textHeight;
        private GraphicsStyle _markerStyle;
        private bool _hasSlab;
        private double _slabMin, _slabMax;
        private double _bodyNear = double.MaxValue;
        private double _bodyMinA, _bodyMaxA;
        private double _labelColA;
        private BeamDimOptions _opts = new BeamDimOptions();
        private string _warnings = "";
        private readonly StringBuilder _diag = new StringBuilder();
        private readonly List<Tuple<Dimension, string>> _created = new List<Tuple<Dimension, string>>();
        private readonly List<ElementId> _tracked = new List<ElementId>();

        private static BeamDimOptions _last = new BeamDimOptions();
        private static readonly Dictionary<string, List<ElementId>> _history = new Dictionary<string, List<ElementId>>();

        private static readonly Regex TubeRegex = new Regex(TubePattern, RegexOptions.IgnoreCase);
        private static readonly Regex TubeWordRegex = new Regex(TubeWordPattern, RegexOptions.IgnoreCase);
        private static readonly Regex LifterRegex = new Regex(LifterPattern, RegexOptions.IgnoreCase);
        private static readonly Regex LedgeRegex = new Regex(LedgePattern, RegexOptions.IgnoreCase);
        private static readonly Regex CorbelRegex = new Regex(CorbelPattern, RegexOptions.IgnoreCase);

        private double Off(double paperMm) => paperMm * _scale / 304.8;
        private XYZ P(double a, double b) => _A * a + _B * b + _dir * _depth;

        private class FaceInfo
        {
            public Reference Ref;
            public double Area;
            public double Pos;
            public double DMin, DMax;
        }

        private class PosGroup
        {
            public double Pos;
            public double Area;
            public FaceInfo Best;
        }

        private class EmbedPt
        {
            public FamilyInstance Inst;
            public double A, B;
            public string Source;
        }

        private class Cand
        {
            public Reference Ref;
            public ElementId Marker = ElementId.InvalidElementId;
            public bool Used;
            public string How = "";
        }

        private class BeamGeo
        {
            public PosGroup BodyMin, BodyMax;
            public PosGroup ExtMin, ExtMax;
            public List<PosGroup> LevelsAll = new List<PosGroup>();
            public List<PosGroup> LevelsMain = new List<PosGroup>();
            public List<PosGroup> CorbelPos = new List<PosGroup>();   // corbel faces facing +/-A (without the body faces)
        }

        // =====================================================================
        //  Entry point
        // =====================================================================
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
                    TaskDialog.Show("Error",
                        "Please run this command from a 2D beam view (elevation, section or plan).\n" +
                        "If you are on a sheet, double-click inside the viewport first to activate the view.");
                    return Result.Failed;
            }

            try
            {
                // ---- Beam + members. Assembly view or a single beam in the view: no click needed.
                ElementId aid = view.AssociatedAssemblyInstanceId;
                AssemblyInstance viewAssembly = (aid != null && aid != ElementId.InvalidElementId)
                    ? doc.GetElement(aid) as AssemblyInstance : null;
                Element picked = null;
                if (viewAssembly == null)
                {
                    picked = FindSingleFramingInView(doc, view);
                    if (picked == null)
                    {
                        Reference pick = uidoc.Selection.PickObject(ObjectType.Element, new BeamAssemblyFilter(),
                            "Select the precast beam in this view");
                        picked = doc.GetElement(pick);
                    }
                }

                FamilyInstance beam;
                List<Element> members = ResolveMembers(doc, picked, viewAssembly, view, out beam);
                if (beam == null)
                {
                    TaskDialog.Show("Error", "Could not identify the main structural framing (beam) in this view / assembly.");
                    return Result.Failed;
                }

                InitState(doc, view, beam);
                List<BeamEmbedGroup> groups = DetectEmbedGroups(members, beam);
                bool elev = _kind == ViewKind.Elevation;

                // ---- Pop-up
                BeamDimOptions choice;
                using (BeamDimensionOptionsForm form = new BeamDimensionOptionsForm(_last, groups, elev, _isPlan))
                {
                    if (form.ShowDialog(new BeamWin32Owner(commandData.Application.MainWindowHandle)) != WF.DialogResult.OK)
                        return Result.Cancelled;
                    choice = form.Read();
                }
                _last = choice;
                _opts = choice;
                if (!choice.AnyChosen(elev))
                {
                    TaskDialog.Show("Beam Dimensioning", "No dimensions were ticked - nothing to place.");
                    return Result.Cancelled;
                }

                // ---- Geometry
                BeamGeo geo = AnalyseBeam(beam, members);
                if (geo.BodyMin == null || geo.BodyMax == null)
                {
                    TaskDialog.Show("Error",
                        "Could not find the beam's side / end faces in this view.\n\nDiagnostics:\n" + _diag);
                    return Result.Failed;
                }

                return Place(doc, beam, members, groups, geo, choice);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// For the automated pipeline: dimension an assembly view that another method has just created,
        /// with no pop-up and no click. Example: new BeamSectionCommand().RunOnView(doc, newView, new BeamDimOptions());
        /// </summary>
        public Result RunOnView(Document doc, View view, BeamDimOptions options)
        {
            ElementId aid = view.AssociatedAssemblyInstanceId;
            AssemblyInstance viewAssembly = (aid != null && aid != ElementId.InvalidElementId)
                ? doc.GetElement(aid) as AssemblyInstance : null;
            if (viewAssembly == null) return Result.Failed;

            FamilyInstance beam;
            List<Element> members = ResolveMembers(doc, null, viewAssembly, view, out beam);
            if (beam == null) return Result.Failed;

            InitState(doc, view, beam);
            _opts = options ?? new BeamDimOptions();
            List<BeamEmbedGroup> groups = DetectEmbedGroups(members, beam);
            BeamGeo geo = AnalyseBeam(beam, members);
            if (geo.BodyMin == null || geo.BodyMax == null) return Result.Failed;
            return Place(doc, beam, members, groups, geo, _opts);
        }

        // =====================================================================
        //  Beam / members resolution
        // =====================================================================

        private static bool IsFraming(Element e)
        {
            return e != null && e.Category != null &&
                   e.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;
        }

        /// <summary>If the view shows exactly one structural framing element, it is the beam (no click needed).</summary>
        private static FamilyInstance FindSingleFramingInView(Document doc, View view)
        {
            List<FamilyInstance> list = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>().ToList();
            return list.Count == 1 ? list[0] : null;
        }

        private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b, double tol)
        {
            return a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol &&
                   a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol &&
                   a.Min.Z <= b.Max.Z + tol && a.Max.Z >= b.Min.Z - tol;
        }

        private static FamilyInstance LargestFraming(IEnumerable<Element> elems)
        {
            FamilyInstance best = null;
            double bestSize = -1;
            foreach (Element e in elems)
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi == null || !IsFraming(fi)) continue;
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                double size = bb == null ? 0 : (bb.Max - bb.Min).GetLength();
                if (size > bestSize) { bestSize = size; best = fi; }
            }
            if (best != null) return best;

            foreach (Element e in elems)
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi == null) continue;
                int n;
                if (RoleOf(fi, out n) != BeamRole.None) continue;
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                double size = bb == null ? 0 : (bb.Max - bb.Min).GetLength();
                if (size > bestSize) { bestSize = size; best = fi; }
            }
            return best;
        }

        private static List<Element> ResolveMembers(Document doc, Element picked, AssemblyInstance viewAssembly,
            View view, out FamilyInstance beam)
        {
            beam = null;
            AssemblyInstance assembly = viewAssembly;
            if (assembly == null && picked != null)
            {
                assembly = picked as AssemblyInstance;
                if (assembly == null && picked.AssemblyInstanceId != null &&
                    picked.AssemblyInstanceId != ElementId.InvalidElementId)
                    assembly = doc.GetElement(picked.AssemblyInstanceId) as AssemblyInstance;
            }

            List<Element> members = new List<Element>();
            if (assembly != null)
            {
                foreach (ElementId id in assembly.GetMemberIds())
                {
                    Element e = doc.GetElement(id);
                    if (e != null) members.Add(e);
                }
                beam = LargestFraming(members);
                return members;
            }

            if (picked == null) return members;

            List<Element> inView = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType()
                .Where(el => el is FamilyInstance || el is Autodesk.Revit.DB.Structure.Rebar).ToList();

            FamilyInstance pickedFi = picked as FamilyInstance;
            if (pickedFi != null && IsFraming(pickedFi))
            {
                beam = pickedFi;
            }
            else
            {
                BoundingBoxXYZ pb = picked.get_BoundingBox(null);
                List<Element> near = inView.Where(el =>
                {
                    BoundingBoxXYZ b = el.get_BoundingBox(null);
                    return pb != null && b != null && Overlaps(pb, b, 0.5);
                }).ToList();
                beam = LargestFraming(near);
            }
            if (beam == null) return members;

            BoundingBoxXYZ beamBox = beam.get_BoundingBox(null);
            foreach (Element e in inView)
            {
                if (e.Id == beam.Id) { members.Add(e); continue; }
                FamilyInstance fi = e as FamilyInstance;
                if (fi != null && IsFraming(fi)) continue;
                BoundingBoxXYZ b = e.get_BoundingBox(null);
                if (beamBox != null && b != null && Overlaps(beamBox, b, 0.5)) members.Add(e);
            }
            ElementId beamId = beam.Id;
            if (!members.Any(m => m.Id == beamId)) members.Add(beam);
            return members;
        }

        // =====================================================================
        //  View state
        // =====================================================================

        private void InitState(Document doc, View view, FamilyInstance beam)
        {
            _doc = doc;
            _view = view;
            _scale = Math.Max(1, view.Scale);
            _viewRight = view.RightDirection;
            _viewUp = view.UpDirection;
            _dir = view.ViewDirection;
            _depth = view.Origin.DotProduct(_dir);
            _markerStyle = null;
            _warnings = "";
            _diag.Clear();
            _created.Clear();
            _tracked.Clear();
            _bodyNear = double.MaxValue;

            XYZ axis = GetBeamAxis(beam);
            _kind = Math.Abs(axis.DotProduct(_dir)) > 0.7 ? ViewKind.Section : ViewKind.Elevation;
            _isPlan = _kind == ViewKind.Elevation && Math.Abs(_dir.Z) > 0.9;

            if (_kind == ViewKind.Section)
            {
                _A = _viewRight;
                _B = _viewUp;
            }
            else
            {
                XYZ inPlane = axis - _dir * axis.DotProduct(_dir);
                if (Math.Abs(inPlane.DotProduct(_viewRight)) >= Math.Abs(inPlane.DotProduct(_viewUp)))
                { _A = _viewRight; _B = _viewUp; }
                else
                { _A = _viewUp; _B = _viewRight; }
            }

            _textTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            double textSizeFt = 2.5 / 304.8;
            TextNoteType tnt = doc.GetElement(_textTypeId) as TextNoteType;
            Parameter sizeParam = tnt?.get_Parameter(BuiltInParameter.TEXT_SIZE);
            if (sizeParam != null && sizeParam.AsDouble() > 0) textSizeFt = sizeParam.AsDouble();
            _textHeight = textSizeFt * _scale;

            ComputeViewSlab();
        }

        private static XYZ GetBeamAxis(FamilyInstance beam)
        {
            try
            {
                LocationCurve lc = beam.Location as LocationCurve;
                if (lc != null && lc.Curve != null)
                {
                    XYZ d = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
                    if (d.GetLength() > 1e-6) return d.Normalize();
                }
            }
            catch { }

            BoundingBoxXYZ bb = beam.get_BoundingBox(null);
            if (bb != null)
            {
                XYZ s = bb.Max - bb.Min;
                if (s.X >= s.Y && s.X >= s.Z) return XYZ.BasisX;
                if (s.Y >= s.Z) return XYZ.BasisY;
                return XYZ.BasisZ;
            }
            return XYZ.BasisX;
        }

        /// <summary>The slab of space (along the view direction) that the view really shows: cut plane to far clip.</summary>
        private void ComputeViewSlab()
        {
            _hasSlab = false;
            if (_view.ViewType != ViewType.Section && _view.ViewType != ViewType.Elevation &&
                _view.ViewType != ViewType.Detail) return;
            try
            {
                BoundingBoxXYZ cb = _view.CropBox;
                Transform t = cb.Transform;
                double z0 = t.OfPoint(new XYZ(0, 0, cb.Min.Z)).DotProduct(_dir);
                double z1 = t.OfPoint(new XYZ(0, 0, cb.Max.Z)).DotProduct(_dir);
                _slabMin = Math.Min(z0, z1);
                _slabMax = Math.Max(z0, z1);
                _hasSlab = _slabMax - _slabMin > 0.01 && _slabMax - _slabMin < 1.0e5;
            }
            catch { _hasSlab = false; }
        }

        /// <summary>Does the element lie (at least partly) inside the depth range this view shows?</summary>
        private bool InViewSlab(Element e)
        {
            if (!_hasSlab) return true;
            double mn, mx;
            if (!TryGetExtents(e, _dir, out mn, out mx)) return true;
            return mx >= _slabMin - DepthTolFt && mn <= _slabMax + DepthTolFt;
        }

        // =====================================================================
        //  Embed families (CMS tubes, lifters)
        // =====================================================================

        private static BeamRole RoleOf(FamilyInstance fi, out int number)
        {
            number = 0;
            if (fi == null || fi.Symbol == null) return BeamRole.None;
            string name = fi.Symbol.FamilyName + " " + fi.Symbol.Name;

            Match m = TubeRegex.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, out number)) return BeamRole.Tube;
            if (LifterRegex.IsMatch(name)) return BeamRole.Lifter;
            if (TubeWordRegex.IsMatch(name)) { number = 1; return BeamRole.Tube; }
            if (LedgeRegex.IsMatch(name)) return BeamRole.Ledge;
            if (CorbelRegex.IsMatch(name)) return BeamRole.Corbel;
            return BeamRole.None;
        }

        private List<BeamEmbedGroup> DetectEmbedGroups(List<Element> members, FamilyInstance beam)
        {
            SortedDictionary<int, BeamEmbedGroup> tubes = new SortedDictionary<int, BeamEmbedGroup>();
            BeamEmbedGroup lifters = null;

            foreach (Element m in members)
            {
                FamilyInstance fi = m as FamilyInstance;
                if (fi == null || fi.Id == beam.Id) continue;
                int n;
                BeamRole role = RoleOf(fi, out n);
                if (role != BeamRole.Tube && role != BeamRole.Lifter) continue;
                if (!InViewSlab(fi)) continue;      // not inside the depth range this view shows

                if (role == BeamRole.Tube)
                {
                    if (!tubes.ContainsKey(n))
                        tubes[n] = new BeamEmbedGroup
                        {
                            Key = "CMS" + n,
                            Title = string.Format(CmsLabelFormat, n),
                            DefaultLabel = string.Format(CmsLabelFormat, n),
                            IsLifter = false
                        };
                    tubes[n].Items.Add(fi);
                }
                else
                {
                    if (lifters == null)
                        lifters = new BeamEmbedGroup { Key = "LIFT", Title = "Lifters", DefaultLabel = LifterDefaultLabel, IsLifter = true };
                    lifters.Items.Add(fi);
                }
            }

            List<BeamEmbedGroup> list = tubes.Values.ToList();
            if (lifters != null) list.Add(lifters);
            return list;
        }

        // =====================================================================
        //  Beam geometry: side faces, ledges, corbels, levels
        // =====================================================================

        private BeamGeo AnalyseBeam(FamilyInstance beam, List<Element> members)
        {
            BeamGeo g = new BeamGeo();
            Options opts = new Options { ComputeReferences = true, View = _view };

            List<FaceInfo> aNeg = new List<FaceInfo>(), aPos = new List<FaceInfo>();
            List<FaceInfo> bNeg = new List<FaceInfo>(), bPos = new List<FaceInfo>();
            CollectFaces(beam, opts, aNeg, aPos, bNeg, bPos);
            _diag.AppendLine($"Beam '{beam.Symbol.FamilyName}': faces found - {aNeg.Count} facing -A, {aPos.Count} facing +A, " +
                             $"{bNeg.Count} facing -B, {bPos.Count} facing +B.");
            if (aNeg.Count == 0 || aPos.Count == 0) return g;

            // ---- Main side faces and extremes along A
            List<PosGroup> gNeg = GroupFaces(aNeg), gPos = GroupFaces(aPos);
            g.BodyMin = gNeg.OrderByDescending(x => x.Area).First();
            g.BodyMax = gPos.OrderByDescending(x => x.Area).First();
            double maxNegArea = gNeg.Max(x => x.Area), maxPosArea = gPos.Max(x => x.Area);
            g.ExtMin = gNeg.Where(x => x.Area >= SideFaceMinFraction * maxNegArea).OrderBy(x => x.Pos).First();
            g.ExtMax = gPos.Where(x => x.Area >= SideFaceMinFraction * maxPosArea).OrderByDescending(x => x.Pos).First();

            // ---- Separate ledge / corbel / projecting members
            List<FaceInfo> xANeg = new List<FaceInfo>(), xAPos = new List<FaceInfo>();
            List<FaceInfo> xBNeg = new List<FaceInfo>(), xBPos = new List<FaceInfo>();
            List<FaceInfo> cANeg = new List<FaceInfo>(), cAPos = new List<FaceInfo>();
            List<FaceInfo> cBNeg = new List<FaceInfo>(), cBPos = new List<FaceInfo>();
            int extras = 0, corbels = 0;
            foreach (Element m in members)
            {
                FamilyInstance fi = m as FamilyInstance;
                if (fi == null || fi.Id == beam.Id) continue;
                int n;
                BeamRole role = RoleOf(fi, out n);
                if (role == BeamRole.Tube || role == BeamRole.Lifter) continue;

                if (role == BeamRole.Corbel)
                {
                    CollectFaces(fi, opts, cANeg, cAPos, cBNeg, cBPos);
                    corbels++;
                    _diag.AppendLine($"Corbel member: '{fi.Symbol.FamilyName}'.");
                    continue;
                }

                bool isLedge = role == BeamRole.Ledge;
                if (!isLedge && DetectLedgesByProtrusion)
                {
                    double mn, mx;
                    if (TryGetExtents(fi, _A, out mn, out mx))
                        isLedge = mn < g.ExtMin.Pos - ProtrusionTolFt || mx > g.ExtMax.Pos + ProtrusionTolFt;
                }
                if (!isLedge) continue;

                CollectFaces(fi, opts, xANeg, xAPos, xBNeg, xBPos);
                extras++;
                _diag.AppendLine($"Projecting member used for the LEDGE: '{fi.Symbol.FamilyName}'.");
            }

            List<PosGroup> xNeg = GroupFaces(xANeg), xPosG = GroupFaces(xAPos);
            if (xNeg.Count > 0)
            {
                PosGroup m0 = xNeg.OrderBy(x => x.Pos).First();
                if (m0.Pos < g.ExtMin.Pos - PosTolFt) g.ExtMin = m0;
            }
            if (xPosG.Count > 0)
            {
                PosGroup m1 = xPosG.OrderByDescending(x => x.Pos).First();
                if (m1.Pos > g.ExtMax.Pos + PosTolFt) g.ExtMax = m1;
            }

            // ---- Corbel positions (faces facing +/-A), without the body faces
            bool elevKind = _kind == ViewKind.Elevation;
            foreach (PosGroup c in GroupFaces(cANeg.Concat(cAPos)).OrderBy(x => x.Pos))
            {
                bool onBodyFace = Math.Abs(c.Pos - g.BodyMin.Pos) <= PosTolFt ||
                                  Math.Abs(c.Pos - g.BodyMax.Pos) <= PosTolFt;
                bool outside = c.Pos < g.BodyMin.Pos - PosTolFt || c.Pos > g.BodyMax.Pos + PosTolFt;
                if (onBodyFace) continue;
                if (!elevKind && !outside) continue;   // section: only a corbel's projecting face counts
                g.CorbelPos.Add(c);
            }

            // ---- Levels (horizontal faces along B)
            List<FaceInfo> bAll = new List<FaceInfo>(bNeg);
            bAll.AddRange(bPos);
            List<PosGroup> rawLevels = GroupFaces(bAll);
            _bodyNear = double.MaxValue;
            if (rawLevels.Count > 0)
            {
                double maxLevelArea = rawLevels.Max(x => x.Area);
                List<PosGroup> rawMain = rawLevels.Where(x => x.Area >= MainLevelFraction * maxLevelArea).ToList();
                if (rawMain.Count > 0)
                    _bodyNear = Math.Max(rawMain.First().Best.DMax, rawMain.Last().Best.DMax);
                if (_hasSlab) _bodyNear = Math.Min(_bodyNear, _slabMax);
            }

            List<PosGroup> levels = GroupFaces(bAll.Where(fc => FaceVisible(fc)).ToList());
            if (levels.Count > 0)
            {
                double maxArea = levels.Max(x => x.Area);
                g.LevelsMain = levels.Where(x => x.Area >= MainLevelFraction * maxArea).OrderBy(x => x.Pos).ToList();
                List<PosGroup> all = levels.Where(x => x.Area >= LevelMinFraction * maxArea).ToList();

                List<FaceInfo> xB = new List<FaceInfo>(xBNeg);
                xB.AddRange(xBPos);
                xB.AddRange(cBNeg);   // the corbel's horizontal faces also feed Tier 1
                xB.AddRange(cBPos);
                foreach (PosGroup xg in GroupFaces(xB.Where(fc => FaceVisible(fc)).ToList()))
                    if (!all.Any(l => Math.Abs(l.Pos - xg.Pos) <= PosTolFt)) all.Add(xg);
                g.LevelsAll = all.OrderBy(x => x.Pos).ToList();
            }

            _diag.AppendLine("Side faces along A (mm from the body's first face): body 0 .. " +
                             $"{(g.BodyMax.Pos - g.BodyMin.Pos) * 304.8:0}, " +
                             $"extremes {(g.ExtMin.Pos - g.BodyMin.Pos) * 304.8:0} .. {(g.ExtMax.Pos - g.BodyMin.Pos) * 304.8:0}" +
                             (extras > 0 ? $" ({extras} separate projecting member(s))." : "."));
            if (g.LevelsAll.Count > 0)
            {
                double b0 = g.LevelsAll.First().Pos;
                _diag.AppendLine("Levels (mm from the lowest): all = " +
                                 string.Join(" ", g.LevelsAll.Select(l => ((l.Pos - b0) * 304.8).ToString("0"))) +
                                 "; main = " +
                                 string.Join(" ", g.LevelsMain.Select(l => ((l.Pos - b0) * 304.8).ToString("0"))));
            }
            _diag.AppendLine($"Corbel: {corbels} member(s); face positions (mm from the body's first face): " +
                             (g.CorbelPos.Count == 0 ? "none" :
                              string.Join(" ", g.CorbelPos.Select(c => ((c.Pos - g.BodyMin.Pos) * 304.8).ToString("0")))));
            return g;
        }

        private void CollectFaces(Element e, Options opts, List<FaceInfo> aNeg, List<FaceInfo> aPos,
            List<FaceInfo> bNeg, List<FaceInfo> bPos)
        {
            GeometryElement ge = e.get_Geometry(opts);
            foreach (GeometryObject o in FlattenGeometry(ge))
            {
                Solid s = o as Solid;
                if (s == null || s.Faces.Size == 0) continue;
                foreach (Face f in s.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null || pf.Reference == null) continue;

                    double dA = pf.FaceNormal.DotProduct(_A);
                    double dB = pf.FaceNormal.DotProduct(_B);
                    List<FaceInfo> target = null;
                    double pos = 0;
                    if (dA < -0.95) { target = aNeg; pos = pf.Origin.DotProduct(_A); }
                    else if (dA > 0.95) { target = aPos; pos = pf.Origin.DotProduct(_A); }
                    else if (dB < -0.95) { target = bNeg; pos = pf.Origin.DotProduct(_B); }
                    else if (dB > 0.95) { target = bPos; pos = pf.Origin.DotProduct(_B); }
                    if (target == null) continue;

                    FaceInfo info = new FaceInfo { Ref = pf.Reference, Area = pf.Area, Pos = pos };
                    DepthExtents(pf, out info.DMin, out info.DMax);
                    target.Add(info);
                }
            }
        }

        private void DepthExtents(PlanarFace pf, out double dMin, out double dMax)
        {
            dMin = double.MaxValue;
            dMax = double.MinValue;
            try
            {
                foreach (CurveLoop loop in pf.GetEdgesAsCurveLoops())
                    foreach (Curve c in loop)
                        foreach (XYZ p in c.Tessellate())
                        {
                            double d = p.DotProduct(_dir);
                            if (d < dMin) dMin = d;
                            if (d > dMax) dMax = d;
                        }
            }
            catch { }
            if (dMin > dMax)
            {
                double d0 = pf.Origin.DotProduct(_dir);
                dMin = dMax = d0;
            }
        }

        private static List<PosGroup> GroupFaces(IEnumerable<FaceInfo> faces)
        {
            List<PosGroup> groups = new List<PosGroup>();
            foreach (FaceInfo f in faces.OrderBy(x => x.Pos))
            {
                PosGroup g = groups.FirstOrDefault(x => Math.Abs(x.Pos - f.Pos) <= PosTolFt);
                if (g == null)
                {
                    g = new PosGroup { Pos = f.Pos, Best = f };
                    groups.Add(g);
                }
                g.Area += f.Area;
                if (f.Area > g.Best.Area) g.Best = f;
            }
            return groups;
        }

        /// <summary>Is a horizontal face really seen in this view (inside the depth range and not hidden behind the beam)?</summary>
        private bool FaceVisible(FaceInfo f)
        {
            if (_hasSlab && (f.DMax < _slabMin - DepthTolFt || f.DMin > _slabMax + DepthTolFt)) return false;
            if (_kind == ViewKind.Elevation && _opts.VisibleLevelsOnly &&
                _bodyNear < double.MaxValue / 2 && f.DMax < _bodyNear - DepthTolFt) return false;
            return true;
        }

        private static bool SameLevels(List<PosGroup> a, List<PosGroup> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (Math.Abs(a[i].Pos - b[i].Pos) > PosTolFt) return false;
            return true;
        }

        // =====================================================================
        //  Placement
        // =====================================================================

        private Result Place(Document doc, FamilyInstance beam, List<Element> members, List<BeamEmbedGroup> groups,
            BeamGeo geo, BeamDimOptions o)
        {
            bool elev = _kind == ViewKind.Elevation;
            FaceInfo lo = geo.BodyMin.Best, hi = geo.BodyMax.Best;
            _bodyMinA = geo.BodyMin.Pos;
            _bodyMaxA = geo.BodyMax.Pos;
            double extMinA = geo.ExtMin.Pos;
            _labelColA = Math.Max(geo.ExtMax.Pos, _bodyMaxA);     // all row labels line up in one column
            if (geo.CorbelPos.Count > 0) _labelColA = Math.Max(_labelColA, geo.CorbelPos.Last().Pos);

            List<PosGroup> mainL = geo.LevelsMain, allL = geo.LevelsAll;
            double botB = mainL.Count > 0 ? mainL.First().Pos : (allL.Count > 0 ? allL.First().Pos : 0.0);
            double topB = mainL.Count > 0 ? mainL.Last().Pos : (allL.Count > 0 ? allL.Last().Pos : 0.0);

            double rowsBaseB = topB;
            double tiersBaseA = extMinA;
            foreach (Element m in members)
            {
                double mn, mx;
                if (TryGetExtents(m, _B, out mn, out mx) && mx > rowsBaseB) rowsBaseB = mx;
                if (TryGetExtents(m, _A, out mn, out mx) && mn < tiersBaseA) tiersBaseA = mn;
            }

            string tier2Label = _isPlan ? o.PlanTierLabel : o.DepthLabel;

            _diag.AppendLine($"View kind: {(_kind == ViewKind.Section ? "SECTION (across the beam)" : (_isPlan ? "PLAN (along the beam)" : "ELEVATION (along the beam)"))}. " +
                             $"Body {(_bodyMaxA - _bodyMinA) * 304.8:0} mm in the row direction, {(topB - botB) * 304.8:0} mm in the tier direction.");

            // ---- Embed positions (outside the transaction)
            Dictionary<string, List<EmbedPt>> embedPts = new Dictionary<string, List<EmbedPt>>();
            foreach (BeamEmbedGroup g in groups)
            {
                bool on;
                if (!o.EmbedOn.TryGetValue(g.Key, out on)) on = true;
                if (!on) continue;

                List<EmbedPt> pts = Cluster(GetEmbedPoints(g));
                embedPts[g.Key] = pts;
                _diag.AppendLine($"{g.Title}: {g.Items.Count} found, {pts.Count} distinct position(s), mm from the body's first face: " +
                                 string.Join(" ", pts.Select(p => ((p.A - _bodyMinA) * 304.8).ToString("0"))) +
                                 (pts.Count > 0 ? $" [{pts[0].Source}]" : ""));
            }

            using (Transaction t = new Transaction(doc, "Beam Dimensioning"))
            {
                t.Start();

                if (o.ClearPrevious) ClearPrevious();

                bool needMarkers = embedPts.Count > 0 || o.Centre;
                if (needMarkers)
                    ChooseMarkerStyle(lo, hi, (topB + botB) / 2.0, rowsBaseB + Off(RowFirstMm));

                int rows = 0;
                double firstRowB = double.NaN, firstRowStartA = double.NaN;
                double RowBAt(int i) => rowsBaseB + Off(RowFirstMm + i * RowStepMm);
                void NoteRow(bool placed, double rowB, double startA)
                {
                    if (!placed) return;
                    rows++;
                    if (double.IsNaN(firstRowB)) { firstRowB = rowB; firstRowStartA = startA; }
                }

                // ---------------- Embed rows: CMS groups, then lifters (nearest first)
                foreach (BeamEmbedGroup g in groups)
                {
                    if (!embedPts.ContainsKey(g.Key)) continue;
                    List<EmbedPt> pts = embedPts[g.Key];
                    if (pts.Count == 0)
                    {
                        _diag.AppendLine($"{g.Title} skipped: no centre could be found.");
                        continue;
                    }
                    string label = o.EmbedLabels.ContainsKey(g.Key) ? o.EmbedLabels[g.Key] : g.DefaultLabel;
                    double rb = RowBAt(rows);
                    bool placed = false;
                    string rowName = g.Title + " row";

                    TryDim(rowName, () =>
                    {
                        if (_bodyMaxA - _bodyMinA < 0.05) return;
                        Line line = Line.CreateBound(P(_bodyMinA, rb), P(_bodyMaxA, rb));
                        List<Cand> cands = MakeEmbedCands(pts, line, lo, hi);
                        placed = PlaceRow(rowName, new[] { lo.Ref, hi.Ref }, cands, line, rb, _bodyMaxA, label, 1);
                        DeleteUnused(cands);
                    });
                    NoteRow(placed, rb, _bodyMinA);
                }

                // ---------------- CORBEL row: [corbel | ] body-start | every corbel face | body-end
                bool envelopePlaced = false;
                if (o.Corbel)
                {
                    List<PosGroup> corbChain = new List<PosGroup>(geo.CorbelPos);
                    corbChain.Add(geo.BodyMin);
                    corbChain.Add(geo.BodyMax);
                    corbChain = corbChain.OrderBy(c => c.Pos).ToList();

                    if (geo.CorbelPos.Count == 0)
                    {
                        _diag.AppendLine("CORBEL skipped: no corbel faces found.");
                    }
                    else
                    {
                        double rb = RowBAt(rows);
                        bool placed = false;
                        TryDim("Corbel row", () =>
                        {
                            Line line = Line.CreateBound(P(corbChain.First().Pos, rb), P(corbChain.Last().Pos, rb));
                            List<Cand> cands = corbChain.Skip(1).Take(corbChain.Count - 2)
                                .Select(c => new Cand { Ref = c.Best.Ref, How = "face" }).ToList();
                            placed = PlaceRow("Corbel row", new[] { corbChain.First().Best.Ref, corbChain.Last().Best.Ref },
                                cands, line, rb, corbChain.Last().Pos, o.CorbelLabel, 1);
                        });
                        NoteRow(placed, rb, corbChain.First().Pos);
                    }
                }

                // ---------------- Corbel envelope row (elevation): ledge | corbel start | corbel end | body end
                if (o.CorbelEnvelope && elev && geo.CorbelPos.Count > 0)
                {
                    List<PosGroup> envChain = new List<PosGroup>();
                    foreach (PosGroup pg in new[] { geo.ExtMin, geo.BodyMin, geo.CorbelPos.First(), geo.CorbelPos.Last(), geo.BodyMax })
                        if (pg != null && !envChain.Any(c => Math.Abs(c.Pos - pg.Pos) <= PosTolFt)) envChain.Add(pg);
                    envChain = envChain.OrderBy(c => c.Pos).ToList();

                    if (envChain.Count >= 3)
                    {
                        double rb = RowBAt(rows);
                        bool placed = false;
                        TryDim("Corbel envelope row", () =>
                        {
                            Line line = Line.CreateBound(P(envChain.First().Pos, rb), P(envChain.Last().Pos, rb));
                            List<Cand> cands = envChain.Skip(1).Take(envChain.Count - 2)
                                .Select(c => new Cand { Ref = c.Best.Ref, How = "face" }).ToList();
                            placed = PlaceRow("Corbel envelope row", new[] { envChain.First().Best.Ref, envChain.Last().Best.Ref },
                                cands, line, rb, envChain.Last().Pos, o.CorbelEnvelopeLabel, 1);
                        });
                        NoteRow(placed, rb, envChain.First().Pos);
                        envelopePlaced = placed;
                    }
                }

                // ---------------- LEDGE row: projection | body (| projection)
                if (o.Ledge)
                {
                    List<PosGroup> chain = new List<PosGroup>();
                    foreach (PosGroup pg in new[] { envelopePlaced ? null : geo.ExtMin, geo.BodyMin, geo.BodyMax, geo.ExtMax })
                        if (pg != null && !chain.Any(c => Math.Abs(c.Pos - pg.Pos) <= PosTolFt)) chain.Add(pg);
                    chain = chain.OrderBy(c => c.Pos).ToList();

                    if (chain.Count < 3)
                    {
                        _diag.AppendLine("LEDGE skipped: no projecting ledge was found beyond the beam body.");
                    }
                    else
                    {
                        double rb = RowBAt(rows);
                        bool placed = false;
                        TryDim("Ledge row", () =>
                        {
                            Line line = Line.CreateBound(P(chain.First().Pos, rb), P(chain.Last().Pos, rb));
                            List<Cand> cands = chain.Skip(1).Take(chain.Count - 2)
                                .Select(c => new Cand { Ref = c.Best.Ref, How = "face" }).ToList();
                            placed = PlaceRow("Ledge row", new[] { chain.First().Best.Ref, chain.Last().Best.Ref },
                                cands, line, rb, chain.Last().Pos, o.LedgeLabel, 1);
                        });
                        NoteRow(placed, rb, chain.First().Pos);
                    }
                }

                // ---------------- Centre dimension (left | centre | right)
                if (o.Centre)
                {
                    double rb = RowBAt(rows);
                    bool placed = false;
                    TryDim("Centre", () =>
                    {
                        Line line = Line.CreateBound(P(_bodyMinA, rb), P(_bodyMaxA, rb));
                        List<EmbedPt> mid = new List<EmbedPt>
                        {
                            new EmbedPt { Inst = beam, A = (_bodyMinA + _bodyMaxA) / 2.0, B = (topB + botB) / 2.0, Source = "mid" }
                        };
                        List<Cand> cands = MakeEmbedCands(mid, line, lo, hi);
                        placed = PlaceRow("Centre", new[] { lo.Ref, hi.Ref }, cands, line, rb, _bodyMaxA, o.CentreLabel, 1);
                        DeleteUnused(cands);
                    });
                    NoteRow(placed, rb, _bodyMinA);
                }

                // ---------------- Overall length (elevation / plan) or width (section) - outermost row
                bool overall = elev ? o.OverallLength : o.OverallWidth;
                bool includeLedge = elev ? o.LengthIncludesLedge : o.WidthIncludesLedge;
                string overallLabel = elev ? o.LengthLabel : o.WidthLabel;
                if (overall)
                {
                    PosGroup first = includeLedge ? geo.ExtMin : geo.BodyMin;
                    PosGroup last = includeLedge ? geo.ExtMax : geo.BodyMax;
                    double rb = RowBAt(rows);
                    bool placed = false;
                    TryDim("Overall", () =>
                    {
                        if (last.Pos - first.Pos < 0.05) return;
                        Line line = Line.CreateBound(P(first.Pos, rb), P(last.Pos, rb));
                        placed = PlaceRow("Overall " + (elev ? "length" : "width"),
                            new[] { first.Best.Ref, last.Best.Ref }, new List<Cand>(), line, rb, last.Pos, overallLabel, 0);
                    });
                    NoteRow(placed, rb, first.Pos);
                }

                // ---------------- Vertical tiers (left of the beam), nearest first
                int tiers = 0;
                double nearTierA = double.NaN;
                double TierAAt(int i) => tiersBaseA - Off(RowFirstMm + i * RowStepMm);

                bool dupe = SameLevels(allL, mainL);
                bool doAll = o.TierLevels && allL.Count >= 2 && !(dupe && o.TierMain);
                bool doMain = o.TierMain && mainL.Count >= 2;

                if (doAll)
                {
                    double ta = TierAAt(tiers);
                    bool placed = false;
                    TryDim("Tier 1", () => { placed = PlaceTier("Tier 1 (all levels)", allL, ta, null); });
                    if (placed) { if (double.IsNaN(nearTierA)) nearTierA = ta; tiers++; }
                }
                if (doMain)
                {
                    double ta = TierAAt(tiers);
                    bool placed = false;
                    TryDim("Tier 2", () => { placed = PlaceTier("Tier 2 (main levels)", mainL, ta, tier2Label); });
                    if (placed) { if (double.IsNaN(nearTierA)) nearTierA = ta; tiers++; }
                }
                if ((o.TierLevels || o.TierMain) && !doAll && !doMain)
                    _diag.AppendLine("Vertical tiers skipped: fewer than two horizontal levels were found.");

                // ---------------- Link lines: first tier -> first row
                if (o.LinkLines && !double.IsNaN(nearTierA) && !double.IsNaN(firstRowB))
                    TryDim("Link lines", () => DrawLink(nearTierA, topB, firstRowB, firstRowStartA));

                t.Commit();
            }

            _history[HistKey()] = new List<ElementId>(_tracked);

            foreach (var item in _created)
                if (!item.Item1.IsValidObject)
                    _diag.AppendLine($"WARNING: '{item.Item2}' was removed by Revit when the transaction committed.");

            string report = "";
            if (!string.IsNullOrEmpty(_warnings)) report += "Failures:\n" + _warnings + "\n";
            if (ShowDiagnostics && _diag.Length > 0) report += "Diagnostics:\n" + _diag;
            if (!string.IsNullOrEmpty(report)) TaskDialog.Show("Beam Dimensioning", report);
            return Result.Succeeded;
        }

        // =====================================================================
        //  Row / tier placement helpers
        // =====================================================================

        private bool PlaceRow(string name, IEnumerable<Reference> required, List<Cand> cands, Line line,
            double rowB, double rowEndA, string label, int minExtra)
        {
            List<Reference> req = required.Where(r => r != null).ToList();
            ReferenceArray arr = BuildValidArray(req, cands, line, name);
            if (req.Count < 2 || arr.Size < req.Count + minExtra)
            {
                _diag.AppendLine($"{name} skipped: nothing usable to dimension to.");
                return false;
            }
            Dimension d = _doc.Create.NewDimension(_view, line, arr);
            LogDim(d, name);
            if (d == null) return false;
            if (!string.IsNullOrWhiteSpace(label)) AddRowLabel(rowB, rowEndA, label);
            return true;
        }

        private bool PlaceTier(string name, List<PosGroup> levels, double tierA, string label)
        {
            if (levels.Count < 2) return false;
            double b0 = levels.First().Pos, b1 = levels.Last().Pos;
            if (b1 - b0 < 0.05) return false;

            Line line = Line.CreateBound(P(tierA, b0), P(tierA, b1));
            List<Reference> req = new List<Reference> { levels.First().Best.Ref, levels.Last().Best.Ref };
            List<Cand> cands = levels.Skip(1).Take(levels.Count - 2)
                .Select(l => new Cand { Ref = l.Best.Ref, How = "face" }).ToList();

            ReferenceArray arr = BuildValidArray(req, cands, line, name);
            if (arr.Size < 2)
            {
                _diag.AppendLine($"{name} skipped: nothing usable to dimension to.");
                return false;
            }
            Dimension d = _doc.Create.NewDimension(_view, line, arr);
            LogDim(d, name);
            if (d == null) return false;
            if (!string.IsNullOrWhiteSpace(label)) AddTierLabel(tierA, b0, label);
            return true;
        }

        private void DrawLink(double tierA, double startB, double rowB, double endA)
        {
            _diag.AppendLine("Link lines drawn between the first tier and the first row.");
            DrawLine(P(tierA, startB), P(tierA, rowB));
            DrawLine(P(tierA, rowB), P(endA, rowB));
        }

        // =====================================================================
        //  Embed centres
        // =====================================================================

        private List<EmbedPt> GetEmbedPoints(BeamEmbedGroup g)
        {
            List<EmbedPt> pts = new List<EmbedPt>();
            foreach (FamilyInstance inst in g.Items)
            {
                double a, b;
                string src;
                bool ok = g.IsLifter ? TryGetLifterCentre(inst, out a, out b, out src)
                                     : TryGetCenter(inst, out a, out b, out src);
                if (!ok)
                {
                    _diag.AppendLine($"Could not find a centre for '{inst.Symbol.FamilyName}' (id {inst.Id.Value}).");
                    continue;
                }
                pts.Add(new EmbedPt { Inst = inst, A = a, B = b, Source = src });
            }
            return pts;
        }

        private static List<EmbedPt> Cluster(List<EmbedPt> list)
        {
            List<EmbedPt> result = new List<EmbedPt>();
            List<EmbedPt> cluster = new List<EmbedPt>();
            double last = 0;
            foreach (EmbedPt c in list.OrderBy(p => p.A))
            {
                if (cluster.Count > 0 && c.A - last > ClusterTolFt)
                {
                    result.Add(cluster.OrderByDescending(p => p.B).First());
                    cluster.Clear();
                }
                cluster.Add(c);
                last = c.A;
            }
            if (cluster.Count > 0) result.Add(cluster.OrderByDescending(p => p.B).First());
            return result;
        }

        /// <summary>Centre of a tube / sleeve: circle facing the view -> solid extents -> bounding box -> location point.</summary>
        private bool TryGetCenter(FamilyInstance inst, out double ca, out double cb, out string source)
        {
            ca = cb = 0;
            source = "";
            try
            {
                Options[] optionSets =
                {
                    new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = true },
                    new Options { View = _view, ComputeReferences = false, IncludeNonVisibleObjects = true }
                };

                bool haveExtents = false;
                double eMinA = 0, eMaxA = 0, eMinB = 0, eMaxB = 0;

                foreach (Options opts in optionSets)
                {
                    GeometryElement ge = inst.get_Geometry(opts);
                    Arc best = null;
                    double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                    bool anyPoint = false;

                    foreach (GeometryObject o in FlattenGeometry(ge))
                    {
                        Arc arc = o as Arc;
                        Solid s = o as Solid;
                        if (arc != null) Consider(arc, ref best);
                        else if (s != null && s.Faces.Size > 0)
                        {
                            foreach (Edge edge in s.Edges)
                            {
                                Arc ea = edge.AsCurve() as Arc;
                                if (ea != null) Consider(ea, ref best);
                                foreach (XYZ p in edge.Tessellate())
                                {
                                    double x = p.DotProduct(_A), y = p.DotProduct(_B);
                                    if (x < minA) minA = x;
                                    if (x > maxA) maxA = x;
                                    if (y < minB) minB = y;
                                    if (y > maxB) maxB = y;
                                    anyPoint = true;
                                }
                            }
                        }
                    }

                    if (best != null)
                    {
                        ca = best.Center.DotProduct(_A);
                        cb = best.Center.DotProduct(_B);
                        source = "circle centre";
                        return true;
                    }
                    if (anyPoint && !haveExtents)
                    {
                        haveExtents = true;
                        eMinA = minA; eMaxA = maxA; eMinB = minB; eMaxB = maxB;
                    }
                }

                if (haveExtents)
                {
                    ca = (eMinA + eMaxA) / 2.0;
                    cb = (eMinB + eMaxB) / 2.0;
                    source = "solid extents";
                    return true;
                }

                double x0, x1, y0, y1;
                if (TryGetViewExtents(inst, out x0, out x1, out y0, out y1))
                {
                    ca = (x0 + x1) / 2.0;
                    cb = (y0 + y1) / 2.0;
                    source = "bounding box";
                    return true;
                }

                LocationPoint lp = inst.Location as LocationPoint;
                if (lp != null)
                {
                    ca = lp.Point.DotProduct(_A);
                    cb = lp.Point.DotProduct(_B);
                    source = "location point";
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Centre of a lifter: middle of its 2D symbolic lines in this view, otherwise its bounding box.</summary>
        private bool TryGetLifterCentre(FamilyInstance inst, out double ca, out double cb, out string source)
        {
            ca = cb = 0;
            source = "";
            double x0, x1, y0, y1;
            if (TryGetSymbolExtents(inst, _kind == ViewKind.Elevation, out x0, out x1, out y0, out y1))
            {
                ca = (x0 + x1) / 2.0;
                cb = (y0 + y1) / 2.0;
                source = "lifter symbol in view";
                return true;
            }
            if (TryGetViewExtents(inst, out x0, out x1, out y0, out y1))
            {
                ca = (x0 + x1) / 2.0;
                cb = (y0 + y1) / 2.0;
                source = "lifter bounding box";
                return true;
            }
            return false;
        }

        private void Consider(Arc arc, ref Arc best)
        {
            if (Math.Abs(arc.Normal.DotProduct(_dir)) < 0.95) return;
            if (best == null || arc.Radius > best.Radius) best = arc;
        }

        private bool TryGetSymbolExtents(FamilyInstance inst, bool includeNonVisible,
            out double x0, out double x1, out double y0, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
            bool any = false;
            try
            {
                Options opts = new Options { View = _view, ComputeReferences = false, IncludeNonVisibleObjects = includeNonVisible };
                GeometryElement ge = inst.get_Geometry(opts);
                foreach (GeometryObject o in FlattenGeometry(ge))
                {
                    IEnumerable<XYZ> pts = null;
                    Curve curve = o as Curve;
                    PolyLine poly = o as PolyLine;
                    if (curve != null) pts = curve.Tessellate();
                    else if (poly != null) pts = poly.GetCoordinates();
                    if (pts == null) continue;

                    foreach (XYZ q in pts)
                    {
                        double qa = q.DotProduct(_A), qb = q.DotProduct(_B);
                        if (qa < x0) x0 = qa;
                        if (qa > x1) x1 = qa;
                        if (qb < y0) y0 = qb;
                        if (qb > y1) y1 = qb;
                        any = true;
                    }
                }
            }
            catch { }
            return any;
        }

        private bool TryGetViewExtents(Element e, out double x0, out double x1, out double y0, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
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
                        double pa = pt.DotProduct(_A), pb = pt.DotProduct(_B);
                        if (pa < x0) x0 = pa;
                        if (pa > x1) x1 = pa;
                        if (pb < y0) y0 = pb;
                        if (pb > y1) y1 = pb;
                    }
            return true;
        }

        private bool TryGetExtents(Element e, XYZ axis, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
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

        // =====================================================================
        //  Centre references: family centre planes first, marker lines as the fallback
        // =====================================================================

        private Reference CentrePlaneRef(FamilyInstance inst, double centreA, Line line, FaceInfo lo, FaceInfo hi)
        {
            try
            {
                bool leftRightAlongA = Math.Abs(inst.HandOrientation.DotProduct(_A)) >=
                                       Math.Abs(inst.FacingOrientation.DotProduct(_A));
                IList<Reference> list = inst.GetReferences(leftRightAlongA
                    ? FamilyInstanceReferenceType.CenterLeftRight
                    : FamilyInstanceReferenceType.CenterFrontBack);
                if (list == null) return null;

                double expected = centreA - _bodyMinA;
                foreach (Reference r in list)
                {
                    ReferenceArray arr = new ReferenceArray();
                    arr.Append(lo.Ref);
                    arr.Append(r);
                    arr.Append(hi.Ref);

                    List<double> segs;
                    if (!TryDimensionSegments(arr, line, out segs) || segs.Count != 2) continue;
                    if (segs.Any(v => v < MinSegmentFt)) continue;
                    if (Math.Abs(segs[0] - expected) <= PlaneTolFt || Math.Abs(segs[1] - expected) <= PlaneTolFt) return r;
                }
            }
            catch { }
            return null;
        }

        private List<Cand> MakeEmbedCands(List<EmbedPt> pts, Line line, FaceInfo lo, FaceInfo hi)
        {
            List<Cand> cands = new List<Cand>();
            List<Tuple<Cand, DetailCurve>> pending = new List<Tuple<Cand, DetailCurve>>();

            foreach (EmbedPt p in pts)
            {
                Cand c = new Cand();
                if (_opts.PreferPlanes && p.Inst != null)
                {
                    Reference pr = CentrePlaneRef(p.Inst, p.A, line, lo, hi);
                    if (pr != null)
                    {
                        c.Ref = pr;
                        c.How = "family centre plane";
                        cands.Add(c);
                        continue;
                    }
                }
                try
                {
                    DetailCurve dc = CreateMarker(p.A, p.B, _markerStyle);
                    c.Marker = dc.Id;
                    c.How = "marker line";
                    pending.Add(Tuple.Create(c, dc));
                    cands.Add(c);
                }
                catch (Exception ex) { _diag.AppendLine($"Marker failed: {ex.Message}"); }
            }

            if (pending.Count > 0)
            {
                _doc.Regenerate();
                foreach (var pm in pending) pm.Item1.Ref = MarkerRef(pm.Item2);
            }

            foreach (Cand c in cands.Where(x => x.Ref == null).ToList())
            {
                DeleteMarker(c);
                cands.Remove(c);
            }
            return cands;
        }

        private void DeleteUnused(List<Cand> cands)
        {
            foreach (Cand c in cands)
                if (!c.Used) DeleteMarker(c);
        }

        private void DeleteMarker(Cand c)
        {
            if (c.Marker == null || c.Marker == ElementId.InvalidElementId) return;
            try { if (_doc.GetElement(c.Marker) != null) _doc.Delete(c.Marker); } catch { }
        }

        // =====================================================================
        //  Marker lines
        // =====================================================================

        private DetailCurve CreateMarker(double a, double b, GraphicsStyle style)
        {
            double half = Off(MarkerHalfLengthMm);
            Line l = Line.CreateBound(P(a, b - half), P(a, b + half));
            DetailCurve dc = _doc.Create.NewDetailCurve(_view, l);
            if (style != null) dc.LineStyle = style;
            Track(dc);
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

        private void ChooseMarkerStyle(FaceInfo lo, FaceInfo hi, double sampleB, double rowB)
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
                probe = CreateMarker((_bodyMinA + _bodyMaxA) / 2.0, sampleB, inv);
                _doc.Regenerate();
                Reference pr = MarkerRef(probe);
                if (pr != null)
                {
                    ReferenceArray arr = new ReferenceArray();
                    arr.Append(lo.Ref);
                    arr.Append(pr);
                    arr.Append(hi.Ref);
                    ok = TestDimension(arr, Line.CreateBound(P(_bodyMinA, rowB), P(_bodyMaxA, rowB)));
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

        private ReferenceArray BuildValidArray(List<Reference> required, List<Cand> cands, Line line, string tag)
        {
            ReferenceArray arr = new ReferenceArray();
            foreach (Reference r in required) arr.Append(r);

            int kept = 0;
            foreach (Cand c in cands)
            {
                if (c.Ref == null) continue;
                ReferenceArray trial = new ReferenceArray();
                foreach (Reference r in arr) trial.Append(r);
                trial.Append(c.Ref);

                if (TestDimension(trial, line)) { arr = trial; kept++; c.Used = true; }
            }
            string how = string.Join(", ", cands.Where(c => c.Used).Select(c => c.How).Distinct());
            _diag.AppendLine($"{tag}: {kept} of {cands.Count} extra reference(s) accepted{(how.Length > 0 ? " (" + how + ")" : "")}.");
            return arr;
        }

        private void LogDim(Dimension dim, string tag)
        {
            if (dim == null) return;
            _created.Add(Tuple.Create(dim, tag));
            _tracked.Add(dim.Id);
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

        /// <summary>
        /// Label for a row. The row's line is carried out to the shared label column (right of the widest row),
        /// so LENGTH / LEDGE / L1 / CMS 1 line up like on the drawing.
        /// </summary>
        private void AddRowLabel(double rowB, double rowEndA, string text)
        {
            try
            {
                double h = _textHeight;
                double width = text.Length * h * 0.95;
                double textA = Math.Max(rowEndA, _labelColA);

                XYZ lineStart = P(rowEndA, rowB);
                XYZ textStart = P(textA, rowB) + _A * Off(LabelGapMm);

                CreateNote(textStart + _viewUp * (h * 1.45), text, 0.0);
                DrawLine(lineStart, textStart + _A * (width + h * 0.5));
            }
            catch (Exception ex) { _warnings += $"- Label '{text}': {ex.Message}\n"; }
        }

        /// <summary>Rotated label below the lower end of a vertical tier (DEPTH / WIDTH).</summary>
        private void AddTierLabel(double tierA, double bottomB, string text)
        {
            try
            {
                double h = _textHeight;
                double width = text.Length * h * 0.95;

                XYZ lineStart = P(tierA, bottomB);
                XYZ textStart = lineStart - _B * (Off(LabelGapMm) + width);

                CreateNote(textStart - _viewRight * (h * 1.45), text, Math.PI / 2.0);
                DrawLine(lineStart, textStart - _B * (h * 0.5));
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
            TextNote note = TextNote.Create(_doc, _view.Id, position, text, opts);
            Track(note);
        }

        private void DrawLine(XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) < 0.01) return;
            DetailCurve dc = _doc.Create.NewDetailCurve(_view, Line.CreateBound(a, b));
            Track(dc);
        }

        // =====================================================================
        //  Re-run support: remove what the previous run drew in this view
        // =====================================================================

        private void Track(Element e)
        {
            if (e != null) _tracked.Add(e.Id);
        }

        private string HistKey()
        {
            return _doc.PathName + "|" + _view.Id.Value;
        }

        private void ClearPrevious()
        {
            List<ElementId> old;
            if (!_history.TryGetValue(HistKey(), out old)) return;
            int n = 0;
            foreach (ElementId id in old)
            {
                try
                {
                    Element e = _doc.GetElement(id);
                    if (e != null && e.OwnerViewId == _view.Id) { _doc.Delete(id); n++; }
                }
                catch { }
            }
            _history.Remove(HistKey());
            if (n > 0) _diag.AppendLine($"Previous run: {n} element(s) removed from this view.");
        }

        // =====================================================================
        //  Geometry helper
        // =====================================================================

        private static IEnumerable<GeometryObject> FlattenGeometry(GeometryElement ge, int depth = 0)
        {
            if (ge == null) yield break;
            foreach (GeometryObject go in ge)
            {
                GeometryInstance gi = go as GeometryInstance;
                if (gi != null)
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
    }

    // =========================================================================
    //  Pop-up options
    // =========================================================================

    /// <summary>A set of identical embeds (CMS 1 tubes, lifters) found in the view.</summary>
    internal class BeamEmbedGroup
    {
        public string Key;
        public string Title;
        public string DefaultLabel;
        public bool IsLifter;
        public List<FamilyInstance> Items = new List<FamilyInstance>();
    }

    /// <summary>What the user ticked in the pop-up (also usable directly by the automated pipeline).</summary>
    public class BeamDimOptions
    {
        // ---- horizontal rows
        public bool OverallLength = true;                // elevation / plan
        public string LengthLabel = "LENGTH";
        public bool LengthIncludesLedge = false;         // the drawing's LENGTH is the body only

        public bool OverallWidth = true;                 // section
        public string WidthLabel = "WIDTH";
        public bool WidthIncludesLedge = false;

        public bool Ledge = true;
        public string LedgeLabel = "LEDGE";

        public bool Corbel = true;
        public string CorbelLabel = "CORBEL";
        public bool CorbelEnvelope = true;               // elevation only: ledge | corbel start | corbel end | body end
        public string CorbelEnvelopeLabel = "LEDGE";

        public bool Centre = false;
        public string CentreLabel = "CENTRE";

        // ---- embeds (key -> on / label); a key that is not listed counts as ticked
        public Dictionary<string, bool> EmbedOn = new Dictionary<string, bool>();
        public Dictionary<string, string> EmbedLabels = new Dictionary<string, string>();

        // ---- vertical tiers
        public bool TierLevels = true;
        public bool TierMain = true;
        public string DepthLabel = "DEPTH";              // elevation / section
        public string PlanTierLabel = "WIDTH";           // plan

        // ---- behaviour
        public bool PreferPlanes = true;
        public bool VisibleLevelsOnly = true;
        public bool LinkLines = true;
        public bool ClearPrevious = true;

        public bool AnyChosen(bool elevation)
        {
            bool overall = elevation ? OverallLength : OverallWidth;
            return overall || Ledge || Corbel || Centre || TierLevels || TierMain || EmbedOn.Values.Any(v => v);
        }

        public BeamDimOptions Clone()
        {
            BeamDimOptions o = (BeamDimOptions)MemberwiseClone();
            o.EmbedOn = new Dictionary<string, bool>(EmbedOn);
            o.EmbedLabels = new Dictionary<string, string>(EmbedLabels);
            return o;
        }
    }

    internal class BeamWin32Owner : WF.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public BeamWin32Owner(IntPtr handle) { Handle = handle; }
    }

    /// <summary>
    /// Check-box pop-up for the beam button. It lists the dimensions that make sense for the active view:
    /// elevation / plan (length) or section (width).
    /// </summary>
    internal class BeamDimensionOptionsForm : WF.Form
    {
        private readonly BeamDimOptions _start;
        private readonly bool _elevation;
        private readonly bool _plan;

        private WF.CheckBox _chkOverall, _chkIncl, _chkLedge, _chkCentre, _chkT1, _chkT2, _chkCorbel, _chkEnv;
        private WF.TextBox _txtOverall, _txtLedge, _txtCentre, _txtT2, _txtCorbel, _txtEnv;
        private WF.CheckBox _chkPlanes, _chkVisible, _chkLink, _chkClear;
        private readonly Dictionary<string, WF.CheckBox> _embedChk = new Dictionary<string, WF.CheckBox>();
        private readonly Dictionary<string, WF.TextBox> _embedTxt = new Dictionary<string, WF.TextBox>();
        private readonly List<WF.CheckBox> _all = new List<WF.CheckBox>();

        public BeamDimensionOptionsForm(BeamDimOptions start, List<BeamEmbedGroup> groups, bool elevation, bool plan)
        {
            _start = start;
            _elevation = elevation;
            _plan = plan;

            string kindName = !elevation ? "Section" : (plan ? "Plan" : "Elevation");
            Text = "Beam " + kindName + " Dimensioning";
            FormBorderStyle = WF.FormBorderStyle.FixedDialog;
            StartPosition = WF.FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int y = 14;
            AddText(kindName + " view - select the dimensions to place:", 14, y, 440, true);
            y += 30;

            // ---------------- horizontal rows
            AddText(elevation ? "Horizontal dimensions (above the beam)" : "Horizontal dimensions (above the section)", 14, y, 440, false);
            y += 24;
            if (elevation)
            {
                AddRow("Overall length (horizontal)", start.OverallLength, start.LengthLabel, y, out _chkOverall, out _txtOverall); y += 28;
                AddCheck("Overall length includes the projecting ledge", start.LengthIncludesLedge, y, out _chkIncl); y += 28;
            }
            else
            {
                AddRow("Overall width (horizontal)", start.OverallWidth, start.WidthLabel, y, out _chkOverall, out _txtOverall); y += 28;
                AddCheck("Overall width includes the projecting ledge", start.WidthIncludesLedge, y, out _chkIncl); y += 28;
            }
            AddRow("LEDGE - ledge | body", start.Ledge, start.LedgeLabel, y, out _chkLedge, out _txtLedge); y += 28;
            AddRow("CORBEL - corbel | body", start.Corbel, start.CorbelLabel, y, out _chkCorbel, out _txtCorbel); y += 28;
            if (elevation)
            {
                AddRow("Corbel envelope (ledge | corbels)", start.CorbelEnvelope, start.CorbelEnvelopeLabel, y, out _chkEnv, out _txtEnv); y += 28;
            }
            AddRow("Centre (left | centre | right)", start.Centre, start.CentreLabel, y, out _chkCentre, out _txtCentre); y += 36;

            // ---------------- embeds
            AddText(elevation ? "Embeds along the beam (centre to centre)" : "Embeds across the section (centre to centre)", 14, y, 440, false);
            y += 24;
            if (groups.Count == 0)
            {
                AddText("No CMS tubes or lifters found in this view.", 30, y, 420, false);
                y += 26;
            }
            else
            {
                foreach (BeamEmbedGroup g in groups)
                {
                    bool on;
                    if (!start.EmbedOn.TryGetValue(g.Key, out on)) on = true;
                    string label;
                    if (!start.EmbedLabels.TryGetValue(g.Key, out label)) label = g.DefaultLabel;
                    string text = g.IsLifter ? $"{g.Title}  -  {g.Items.Count} found" : $"{g.Title}  -  {g.Items.Count} tube(s) found";
                    AddEmbed(g.Key, text, on, label, ref y);
                }
            }
            y += 8;

            // ---------------- vertical tiers
            AddText(elevation ? "Vertical dimensions (left of the beam)" : "Vertical dimensions (left of the section)", 14, y, 440, false);
            y += 24;
            AddCheck("Tier 1 - every level (ledge top, steps, notches, corbels)", start.TierLevels, y, out _chkT1); y += 28;
            AddRow("Tier 2 - main levels", start.TierMain, plan ? start.PlanTierLabel : start.DepthLabel, y, out _chkT2, out _txtT2); y += 36;

            // ---------------- behaviour
            AddText("Options", 14, y, 440, false);
            y += 24;
            AddOption("Prefer family centre planes (marker lines as fallback)", start.PreferPlanes, y, out _chkPlanes); y += 28;
            AddOption("Draw link lines between the first tier and row", start.LinkLines, y, out _chkLink); y += 28;
            if (elevation)
            {
                AddOption("Ignore levels hidden behind the beam", start.VisibleLevelsOnly, y, out _chkVisible); y += 28;
            }
            AddOption("Delete the previous run's dimensions in this view", start.ClearPrevious, y, out _chkClear); y += 28;

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

        /// <summary>The user's choices. Anything not shown for this view kind keeps its previous value.</summary>
        public BeamDimOptions Read()
        {
            BeamDimOptions o = _start.Clone();

            if (_elevation)
            {
                o.OverallLength = _chkOverall.Checked;
                o.LengthLabel = _txtOverall.Text.Trim();
                o.LengthIncludesLedge = _chkIncl.Checked;
            }
            else
            {
                o.OverallWidth = _chkOverall.Checked;
                o.WidthLabel = _txtOverall.Text.Trim();
                o.WidthIncludesLedge = _chkIncl.Checked;
            }
            o.Ledge = _chkLedge.Checked;
            o.LedgeLabel = _txtLedge.Text.Trim();
            o.Corbel = _chkCorbel.Checked;
            o.CorbelLabel = _txtCorbel.Text.Trim();
            if (_chkEnv != null)
            {
                o.CorbelEnvelope = _chkEnv.Checked;
                o.CorbelEnvelopeLabel = _txtEnv.Text.Trim();
            }
            o.Centre = _chkCentre.Checked;
            o.CentreLabel = _txtCentre.Text.Trim();

            foreach (var kv in _embedChk)
            {
                o.EmbedOn[kv.Key] = kv.Value.Checked;
                o.EmbedLabels[kv.Key] = _embedTxt[kv.Key].Text.Trim();
            }

            o.TierLevels = _chkT1.Checked;
            o.TierMain = _chkT2.Checked;
            if (_plan) o.PlanTierLabel = _txtT2.Text.Trim();
            else o.DepthLabel = _txtT2.Text.Trim();

            o.PreferPlanes = _chkPlanes.Checked;
            o.LinkLines = _chkLink.Checked;
            if (_chkVisible != null) o.VisibleLevelsOnly = _chkVisible.Checked;
            o.ClearPrevious = _chkClear.Checked;
            return o;
        }

        private void AddText(string text, int x, int y, int w, bool bold)
        {
            WF.Label l = new WF.Label { Text = text, Left = x, Top = y, Width = w, Height = 20, AutoSize = false };
            if (bold) l.Font = new SD.Font(l.Font, SD.FontStyle.Bold);
            Controls.Add(l);
        }

        private void AddCheck(string text, bool isChecked, int y, out WF.CheckBox chk)
        {
            chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 424, Checked = isChecked };
            Controls.Add(chk);
            _all.Add(chk);
        }

        /// <summary>Behaviour switch: not touched by Select all / Clear all.</summary>
        private void AddOption(string text, bool isChecked, int y, out WF.CheckBox chk)
        {
            chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 424, Checked = isChecked };
            Controls.Add(chk);
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

        private void AddEmbed(string key, string text, bool isChecked, string label, ref int y)
        {
            WF.CheckBox chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 270, Checked = isChecked };
            WF.Label l = new WF.Label { Text = "Label:", Left = 306, Top = y + 3, Width = 44, Height = 20, AutoSize = false };
            WF.TextBox txt = new WF.TextBox { Left = 354, Top = y, Width = 100, Text = label ?? "" };
            Controls.Add(chk);
            Controls.Add(l);
            Controls.Add(txt);
            _embedChk[key] = chk;
            _embedTxt[key] = txt;
            _all.Add(chk);
            y += 28;
        }
    }

    public class BeamAssemblyFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            elem is FamilyInstance || elem is AssemblyInstance || elem is Autodesk.Revit.DB.Structure.Rebar;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}