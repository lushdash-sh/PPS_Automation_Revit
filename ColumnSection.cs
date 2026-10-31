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
    /// Smart Column Dimensioning for ANY column assembly view: elevations (back / front / right / left) and sections.
    /// Workflow: click the button -> tick the dimensions you want in the pop-up -> click the column / assembly in the view.
    ///
    /// Available dimensions:
    ///   - overall length / width of the column body
    ///   - centre dimension (left | centre | right), optionally also vertical
    ///   - CMS (corrugated metal sleeve) rows and tiers, measured to the tube centre or to the nearest rebar centre
    ///   - CORBEL: outer faces of the corbels / top and bottom blocks
    ///   - bars in the corbels, and bars in the top / bottom blocks (hexagonal end-on rebar of the chosen type, e.g. H25)
    ///
    /// Bar and tube centres are dimensioned through small marker lines drawn at each centre
    /// (invisible when Revit accepts the "&lt;Invisible lines&gt;" style).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ColumnSectionDimensioningCommand : IExternalCommand
    {
        // =====================================================================
        //  SETTINGS
        // =====================================================================

        // Spacing is PAPER millimetres (multiplied by the view scale), nearest row/tier first.
        private const double RowFirstMm = 12.0;     // outermost part of the section -> first row / tier
        private const double RowStepMm = 9.0;       // spacing between rows / tiers
        private const double LabelGapMm = 2.0;      // gap between the end of a row and its label
        private const double MarkerHalfLengthMm = 1.5; // half length of the centre marker lines (paper mm)

        // Family / type name that identifies a sleeve tube; the digits become the group number.
        // "PPS_Dowel Tube (CMS1)" -> group 1 -> label "CMS 1"
        private const string TubeNamePattern = @"CMS\s*(\d+)";
        private const string CmsLabelFormat = "CMS {0}";

        private const bool DrawLinkLines = true;    // L-shaped lines joining each vertical tier to its horizontal row
        private const bool DrawLabelLine = true;    // line carrying each row out under its label
        private const bool ShowDiagnostics = true;

        private const double MinSegmentFt = 0.03;   // reject dimension segments shorter than ~9 mm
        private const double SamePositionFt = 0.01; // centres closer than ~3 mm count as the same position
        private const bool DetectCorbelsByProtrusion = true; // elevation: also treat any embed sticking out past the column sides as a corbel
        private const double ProtrusionTolFt = 0.05;
        private const double GeomTolFt = 0.02;               // treat Z values closer than this as the same plane
        private const double SolidTolFt = 0.02;     // tolerance when classifying column solids / bars (~6 mm)
        private const double ClusterTolFt = 15.0 / 304.8;   // bars closer than ~15 mm count as one position
        private const string HookNamePattern = @"hock|lifting|lifter";   // lifting hook family names
        private const double BarMatchToleranceMm = 60.0; // max distance between a tube centre and "its" rebar

        // Only used if the circle centre cannot be read from the tube geometry and the bounding box is used instead.
        private static readonly double TubeCentreInwardCorrectionMm = 0.0;

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
        private double _colMidX, _colMidY;
        private double _colLeftX, _colRightX;
        private XYZ _testOrigin;
        private GraphicsStyle _markerStyle;   // null = default (visible) line style
        private string _warnings = "";
        private readonly StringBuilder _diag = new StringBuilder();
        private readonly List<Tuple<Dimension, string>> _created = new List<Tuple<Dimension, string>>();

        // remembered between runs in the same Revit session
        private static SectionDimOptions _last = new SectionDimOptions();

        private static readonly Regex TubeRegex = new Regex(TubeNamePattern, RegexOptions.IgnoreCase);
        private static readonly Regex HookRegex = new Regex(HookNamePattern, RegexOptions.IgnoreCase);

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
            public double Z;          // world height (hooks)
            public string Region;     // for rebar points: body / left corbel / right corbel / top block / bottom block
        }

        private class SolidInfo
        {
            public Solid Solid;
            public double Volume;
            public double MinX = double.MaxValue, MaxX = double.MinValue, MinY = double.MaxValue, MaxY = double.MinValue;
        }

        /// <summary>The column body and the projecting solids (corbels / blocks) seen in the view.</summary>
        private class ColumnParts
        {
            public FaceInfo Left, Right, Top, Bottom;                 // body faces
            public SolidInfo Body, LeftCorbel, RightCorbel, TopBlock, BottomBlock;
            public FaceInfo LeftCorbelOuter, RightCorbelOuter;        // outer faces of the side corbels
            public FaceInfo TopBlockTop, BottomBlockBottom;           // outer faces of the top / bottom blocks
            public FaceInfo TopBlockLeft, TopBlockRight, BottomBlockLeft, BottomBlockRight;
            public int OtherSolids;
            public List<FaceInfo> OtherLeftFaces = new List<FaceInfo>();   // left faces of solids that overlap the body in this view
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
                    TaskDialog.Show("Error", "Please run this command from a 2D column assembly view (elevation or section).");
                    return Result.Failed;
            }

            try
            {
                // ---- Pop-up: which dimensions should be placed?
                Dictionary<int, int> detected = DetectCmsGroups(doc, view);   // null = unknown (not an assembly view)
                bool elevationView = IsElevationView(view);
                SectionDimOptions choice;
                using (SectionDimensionOptionsForm form = new SectionDimensionOptionsForm(_last, detected, elevationView))
                {
                    if (form.ShowDialog(new Win32Owner(commandData.Application.MainWindowHandle)) != WF.DialogResult.OK)
                        return Result.Cancelled;
                    choice = form.Read();
                }
                _last = choice;
                if (!choice.AnyChosen)
                {
                    TaskDialog.Show("Smart Column Dimensioning", "No dimensions were ticked - nothing to place.");
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

                // ---- Column body, its faces, and any projecting corbels / blocks
                ColumnParts parts = AnalyseColumn(mainColumn);
                FaceInfo left = parts.Left, right = parts.Right, top = parts.Top, bottom = parts.Bottom;
                if (left == null || right == null || top == null || bottom == null)
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

                // Rows start above the highest projection, tiers start left of the left-most projection
                double rowsBaseY = parts.TopBlock != null ? Math.Max(topY, parts.TopBlock.MaxY) : topY;
                double tiersBaseX = parts.LeftCorbel != null ? Math.Min(leftX, parts.LeftCorbel.MinX) : leftX;
                double rowsRightX = parts.RightCorbel != null ? Math.Max(rightX, parts.RightCorbel.MaxX) : rightX;
                double rowsLeftX = parts.LeftCorbel != null ? Math.Min(leftX, parts.LeftCorbel.MinX) : leftX;
                double tiersTopY = rowsBaseY;

                _diag.AppendLine($"Column: {mainColumn.Symbol.FamilyName}. Body in view: " +
                                 $"{(rightX - leftX) * 304.8:0} x {(topY - bottomY) * 304.8:0} mm.");
                DescribeParts(parts, leftX, topY);

                // ---- Elevation views have their own set of dimensions
                if (choice.IsElevation)
                    return RunElevation(choice, parts, mainColumn, members, left, right, top, bottom);

                // ---- Rebar points (hexagonal end-on bars of the chosen type)
                List<TubeCenter> bars = new List<TubeCenter>();
                bool needBars = choice.CorbelBars || choice.BlockBars || choice.CmsUseBars;
                if (needBars)
                {
                    bars = GetBarPoints(members, choice.BarTypeName);
                    foreach (TubeCenter b in bars) b.Region = RegionOf(parts, b.X, b.Y);
                    DescribeBars(bars, leftX, topY, choice.BarTypeName);
                }

                // ---- Lifting hooks
                List<TubeCenter> hooks = new List<TubeCenter>();
                if (choice.Hooks)
                {
                    hooks = DropTopHooks(GetHookPoints(members, mainColumn, false));
                    int hooksBefore = hooks.Count;
                    hooks = hooks.Where(h => RegionOf(parts, h.X, h.Y) == "body").ToList();
                    if (hooksBefore != hooks.Count)
                        _diag.AppendLine($"Lifting hooks: {hooksBefore - hooks.Count} hook(s) outside the column body were ignored.");
                    string hp = string.Join("  ", hooks.OrderBy(h => h.X).ThenBy(h => h.Y)
                        .Select(h => $"({(h.X - leftX) * 304.8:0},{(topY - h.Y) * 304.8:0})"));
                    _diag.AppendLine($"Lifting hooks: {hooks.Count} used{(hooks.Count > 0 ? " [" + hooks[0].Source + "]" : "")}, mm from body left / top face: {hp}");
                }

                // ---- Tubes grouped by CMS number - only the ticked groups
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

                    // optionally dimension to the rebar sitting in / next to each tube instead of the tube itself
                    if (choice.CmsUseBars)
                    {
                        double tol = BarMatchToleranceMm / 304.8;
                        int matched = 0, total = 0;
                        foreach (var g in groups)
                            foreach (TubeCenter tc in g.Value)
                            {
                                total++;
                                TubeCenter nearest = null;
                                double nd = double.MaxValue;
                                foreach (TubeCenter b in bars)
                                {
                                    double d = Math.Sqrt((b.X - tc.X) * (b.X - tc.X) + (b.Y - tc.Y) * (b.Y - tc.Y));
                                    if (d < nd) { nd = d; nearest = b; }
                                }
                                if (nearest != null && nd <= tol)
                                {
                                    tc.X = nearest.X; tc.Y = nearest.Y; tc.Source = "rebar near tube";
                                    matched++;
                                }
                            }
                        _diag.AppendLine($"CMS rows measured to rebar: {matched} of {total} tubes had a '{choice.BarTypeName}' bar within {BarMatchToleranceMm:0} mm (the rest use the tube centre).");
                    }
                }

                foreach (var g in groups)
                {
                    string pts = string.Join("  ", g.Value
                        .OrderBy(c => c.Y).ThenBy(c => c.X)
                        .Select(c => $"({(c.X - leftX) * 304.8:0},{(topY - c.Y) * 304.8:0})"));
                    _diag.AppendLine($"CMS {g.Key}: {g.Value.Count} point(s), mm from left face / top face [{g.Value[0].Source}]: {pts}");
                }

                using (Transaction t = new Transaction(doc, "Smart Column Section Dimensioning"))
                {
                    t.Start();

                    bool needMarkers = groups.Count > 0 || choice.CentreLength || choice.CentreVertical ||
                                       choice.CorbelBars || choice.BlockBars || choice.Hooks;
                    if (needMarkers)
                        ChooseMarkerStyle(left, right, leftX, rightX, topY, _colMidX, _colMidY);

                    // Horizontal rows stack above the section, vertical tiers to its left. Nearest first:
                    // CMS groups, bottom/top block bars, corbel bars, CORBEL, centre, overall (outermost).
                    int hIdx = 0, vIdx = 0;
                    double RowYAt(int i) => rowsBaseY + Off(RowFirstMm + i * RowStepMm);
                    double TierXAt(int i) => tiersBaseX - Off(RowFirstMm + i * RowStepMm);

                    // ---------------- CMS groups
                    IEnumerable<KeyValuePair<int, List<TubeCenter>>> orderedGroups = choice.ReverseCmsOrder
                        ? groups.OrderByDescending(k => k.Key)
                        : groups.OrderBy(k => k.Key);

                    foreach (var kv in orderedGroups)
                    {
                        int n = kv.Key;
                        List<TubeCenter> centers = kv.Value;
                        double rowY = RowYAt(hIdx); hIdx++;
                        double tierX = TierXAt(vIdx); vIdx++;
                        string cmsLabel = choice.CmsLabels.ContainsKey(n) ? choice.CmsLabels[n] : string.Format(CmsLabelFormat, n);

                        bool hOk = false, vOk = false;

                        TryDim($"CMS {n} horizontal", () =>
                        {
                            hOk = PlaceHorizontal($"CMS {n} horizontal", new[] { left.Ref, right.Ref },
                                MakeMarkerRefs(HPos(centers), true), rowY, leftX, rightX, rightX, cmsLabel, 1);
                        });

                        TryDim($"CMS {n} vertical", () =>
                        {
                            vOk = PlaceVertical($"CMS {n} vertical", new[] { top.Ref, bottom.Ref },
                                MakeMarkerRefs(VPos(centers), false), tierX, bottomY, topY, null, 1);
                        });

                        if (DrawLinkLines && hOk && vOk)
                            TryDim($"CMS {n} link lines", () => DrawLink(tierX, topY, rowY, leftX));
                    }

                    // ---------------- Bars in the top / bottom blocks (one shared vertical tier, one row per block)
                    if (choice.BlockBars)
                    {
                        List<TubeCenter> topBars = bars.Where(b => b.Region == "top block").ToList();
                        List<TubeCenter> bottomBars = bars.Where(b => b.Region == "bottom block").ToList();

                        if (topBars.Count == 0 && bottomBars.Count == 0)
                        {
                            _diag.AppendLine("Block bars skipped: no matching bars were found inside the top / bottom blocks.");
                        }
                        else
                        {
                            double blockTierX = TierXAt(vIdx); vIdx++;
                            double blockLinkStartY = parts.TopBlock != null ? Math.Max(topY, parts.TopBlock.MaxY) : topY;
                            bool blockTierOk = false;

                            TryDim("Block bars vertical", () =>
                            {
                                List<Reference> cands = new List<Reference>();
                                if (parts.TopBlockTop != null) cands.Add(parts.TopBlockTop.Ref);
                                if (parts.BottomBlockBottom != null) cands.Add(parts.BottomBlockBottom.Ref);
                                List<TubeCenter> both = new List<TubeCenter>(topBars);
                                both.AddRange(bottomBars);
                                cands.AddRange(MakeMarkerRefs(VPos(both), false));
                                blockTierOk = PlaceVertical("Block bars vertical", new[] { top.Ref, bottom.Ref }, cands,
                                    blockTierX, bottomY, topY, null, 1);
                            });

                            // nearest row first: bottom block, then top block
                            if (bottomBars.Count > 0)
                            {
                                double ry = RowYAt(hIdx); hIdx++;
                                PlaceBlockRow("Bottom block bars", bottomBars, parts.BottomBlockLeft, parts.BottomBlockRight,
                                    left, right, ry, blockTierOk, blockTierX, blockLinkStartY, leftX, rightX, choice.BlockBarsLabel);
                            }
                            if (topBars.Count > 0)
                            {
                                double ry = RowYAt(hIdx); hIdx++;
                                PlaceBlockRow("Top block bars", topBars, parts.TopBlockLeft, parts.TopBlockRight,
                                    left, right, ry, blockTierOk, blockTierX, blockLinkStartY, leftX, rightX, choice.BlockBarsLabel);
                            }
                        }
                    }

                    // ---------------- Bars in the left / right corbels
                    if (choice.CorbelBars)
                    {
                        List<TubeCenter> cb = bars.Where(b => b.Region == "left corbel" || b.Region == "right corbel").ToList();
                        if (cb.Count == 0)
                        {
                            _diag.AppendLine("Corbel bars skipped: no matching bars were found inside the left / right corbels.");
                        }
                        else
                        {
                            double cbRowY = RowYAt(hIdx); hIdx++;
                            double cbTierX = TierXAt(vIdx); vIdx++;
                            bool cbH = false, cbV = false;

                            TryDim("Corbel bars horizontal", () =>
                            {
                                List<Reference> cands = new List<Reference>();
                                if (parts.LeftCorbelOuter != null) cands.Add(parts.LeftCorbelOuter.Ref);
                                if (parts.RightCorbelOuter != null) cands.Add(parts.RightCorbelOuter.Ref);
                                cands.AddRange(MakeMarkerRefs(HPos(cb), true));
                                cbH = PlaceHorizontal("Corbel bars horizontal", new[] { left.Ref, right.Ref }, cands,
                                    cbRowY, rowsLeftX, rowsRightX, rowsRightX, choice.CorbelBarsLabel, 1);
                            });

                            TryDim("Corbel bars vertical", () =>
                            {
                                cbV = PlaceVertical("Corbel bars vertical", new[] { top.Ref, bottom.Ref },
                                    MakeMarkerRefs(VPos(cb), false), cbTierX, bottomY, topY, null, 1);
                            });

                            if (DrawLinkLines && cbH && cbV)
                                TryDim("Corbel bars link lines", () => DrawLink(cbTierX, topY, cbRowY, rowsLeftX));
                        }
                    }

                    // ---------------- CORBEL: outer faces of the corbels / blocks
                    if (choice.Corbel)
                    {
                        bool haveSides = parts.LeftCorbelOuter != null || parts.RightCorbelOuter != null;
                        bool haveEnds = parts.TopBlockTop != null || parts.BottomBlockBottom != null;
                        if (!haveSides && !haveEnds)
                        {
                            _diag.AppendLine("CORBEL skipped: no projecting corbels or blocks were found on the column.");
                        }
                        else
                        {
                            double crRowY = RowYAt(hIdx);
                            double crTierX = TierXAt(vIdx);
                            if (haveSides) hIdx++;
                            if (haveEnds) vIdx++;
                            bool crH = false, crV = false;

                            if (haveSides)
                                TryDim("Corbel horizontal", () =>
                                {
                                    List<Reference> cands = new List<Reference>();
                                    if (parts.LeftCorbelOuter != null) cands.Add(parts.LeftCorbelOuter.Ref);
                                    if (parts.RightCorbelOuter != null) cands.Add(parts.RightCorbelOuter.Ref);
                                    crH = PlaceHorizontal("Corbel horizontal", new[] { left.Ref, right.Ref }, cands,
                                        crRowY, rowsLeftX, rowsRightX, rowsRightX, choice.CorbelLabel, 1);
                                });

                            if (haveEnds)
                                TryDim("Corbel vertical", () =>
                                {
                                    List<Reference> cands = new List<Reference>();
                                    if (parts.TopBlockTop != null) cands.Add(parts.TopBlockTop.Ref);
                                    if (parts.BottomBlockBottom != null) cands.Add(parts.BottomBlockBottom.Ref);
                                    crV = PlaceVertical("Corbel vertical", new[] { top.Ref, bottom.Ref }, cands,
                                        crTierX, bottomY, topY, null, 1);
                                });

                            if (DrawLinkLines && crH && crV)
                                TryDim("Corbel link lines", () => DrawLink(crTierX, tiersTopY, crRowY, rowsLeftX));
                        }
                    }

                    // ---------------- Lifting hooks (row + tier), e.g. L1
                    if (choice.Hooks)
                    {
                        if (hooks.Count == 0)
                        {
                            _diag.AppendLine("Lifting hooks skipped: no hook families were found in the assembly.");
                        }
                        else
                        {
                            double hkRowY = RowYAt(hIdx); hIdx++;
                            double hkTierX = TierXAt(vIdx); vIdx++;
                            bool hkH = false, hkV = false;

                            TryDim("Hooks horizontal", () =>
                            {
                                hkH = PlaceHorizontal("Hooks horizontal", new[] { left.Ref, right.Ref },
                                    MakeMarkerRefs(HPos(hooks), true), hkRowY, leftX, rightX, rightX, choice.HooksLabel, 1);
                            });

                            TryDim("Hooks vertical", () =>
                            {
                                hkV = PlaceVertical("Hooks vertical", new[] { top.Ref, bottom.Ref },
                                    MakeMarkerRefs(VPos(hooks), false), hkTierX, bottomY, topY, null, 1);
                            });

                            if (DrawLinkLines && hkH && hkV)
                                TryDim("Hooks link lines", () => DrawLink(hkTierX, topY, hkRowY, leftX));
                        }
                    }

                    // ---------------- Centre dimension (left | centre | right), optionally also vertical
                    if (choice.CentreLength || choice.CentreVertical)
                    {
                        double ceRowY = RowYAt(hIdx);
                        double ceTierX = TierXAt(vIdx);
                        if (choice.CentreLength) hIdx++;
                        if (choice.CentreVertical) vIdx++;
                        bool ceH = false, ceV = false;

                        if (choice.CentreLength)
                            TryDim("Centre length", () =>
                            {
                                Line probeLine = Line.CreateBound(P(leftX, ceRowY), P(rightX, ceRowY));
                                string how;
                                Reference centreRef = GetColumnCentreRef(mainColumn, left, right, probeLine, _colMidX, _colMidY, out how);
                                if (centreRef == null) { _diag.AppendLine("Centre length skipped: no centre reference available."); return; }
                                _diag.AppendLine($"Centre length uses the {how}.");
                                ceH = PlaceHorizontal("Centre length", new[] { left.Ref, right.Ref }, new[] { centreRef },
                                    ceRowY, leftX, rightX, rightX, choice.CentreLabel, 1);
                            });

                        if (choice.CentreVertical)
                            TryDim("Centre width", () =>
                            {
                                List<Reference> cands = MakeMarkerRefs(
                                    new List<TubeCenter> { new TubeCenter { X = _colMidX, Y = _colMidY } }, false);
                                ceV = PlaceVertical("Centre width", new[] { top.Ref, bottom.Ref }, cands, ceTierX, bottomY, topY, null, 1);
                            });

                        if (DrawLinkLines && ceH && ceV)
                            TryDim("Centre link lines", () => DrawLink(ceTierX, topY, ceRowY, leftX));
                    }

                    // ---------------- Overall length (outermost row) and overall width (outermost tier)
                    if (choice.OverallLength)
                    {
                        double ovRowY = RowYAt(hIdx);
                        TryDim("Overall length", () =>
                        {
                            PlaceHorizontal("Overall length", new[] { left.Ref, right.Ref }, new Reference[0],
                                ovRowY, leftX, rightX, rightX, choice.LengthLabel, 0);
                        });
                    }

                    if (choice.OverallWidth)
                    {
                        double ovTierX = TierXAt(vIdx);
                        TryDim("Overall width", () =>
                        {
                            PlaceVertical("Overall width", new[] { top.Ref, bottom.Ref }, new Reference[0],
                                ovTierX, bottomY, topY, choice.WidthLabel, 0);
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
                if (!string.IsNullOrEmpty(report)) TaskDialog.Show("Smart Column Dimensioning", report);
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
        //  Row / tier placement helpers
        // =====================================================================

        /// <summary>
        /// Horizontal row: required references plus any candidates Revit accepts. labelX = where the row's label starts.
        /// minExtra = how many candidates must be accepted for the row to be worth drawing.
        /// </summary>
        private bool PlaceHorizontal(string name, IEnumerable<Reference> required, IEnumerable<Reference> candidates,
            double rowY, double lineLeftX, double lineRightX, double labelX, string label, int minExtra)
        {
            List<Reference> req = required.ToList();
            Line line = Line.CreateBound(P(lineLeftX, rowY), P(lineRightX, rowY));
            ReferenceArray arr = BuildValidArray(req, candidates, line, name);
            if (arr.Size < req.Count + minExtra)
            {
                _diag.AppendLine($"{name} skipped: nothing usable to dimension to.");
                return false;
            }
            Dimension d = _doc.Create.NewDimension(_view, line, arr);
            LogDim(d, name);
            if (d == null) return false;
            if (!string.IsNullOrWhiteSpace(label)) AddRowLabel(rowY, labelX, label);
            return true;
        }

        /// <summary>Vertical tier: required references plus any candidates Revit accepts. Optional rotated label above the top.</summary>
        private bool PlaceVertical(string name, IEnumerable<Reference> required, IEnumerable<Reference> candidates,
            double tierX, double lineBottomY, double lineTopY, string label, int minExtra)
        {
            List<Reference> req = required.ToList();
            Line line = Line.CreateBound(P(tierX, lineBottomY), P(tierX, lineTopY));
            ReferenceArray arr = BuildValidArray(req, candidates, line, name);
            if (arr.Size < req.Count + minExtra)
            {
                _diag.AppendLine($"{name} skipped: nothing usable to dimension to.");
                return false;
            }
            Dimension d = _doc.Create.NewDimension(_view, line, arr);
            LogDim(d, name);
            if (d == null) return false;
            if (!string.IsNullOrWhiteSpace(label)) AddColumnLabel(tierX, lineTopY, label);
            return true;
        }

        private void PlaceBlockRow(string name, List<TubeCenter> blockBars, FaceInfo blockLeft, FaceInfo blockRight,
            FaceInfo left, FaceInfo right, double rowY, bool tierOk, double tierX, double linkStartY,
            double leftX, double rightX, string label)
        {
            bool ok = false;
            TryDim(name, () =>
            {
                List<Reference> cands = new List<Reference>();
                if (blockLeft != null) cands.Add(blockLeft.Ref);
                if (blockRight != null) cands.Add(blockRight.Ref);
                cands.AddRange(MakeMarkerRefs(HPos(blockBars), true));
                ok = PlaceHorizontal(name, new[] { left.Ref, right.Ref }, cands, rowY, leftX, rightX, rightX, label, 1);
            });
            if (ok && tierOk && DrawLinkLines)
                TryDim(name + " link lines", () => DrawLink(tierX, linkStartY, rowY, leftX));
        }

        /// <summary>L-shaped line: up from the tier to the row, then across to the start of the row.</summary>
        private void DrawLink(double tierX, double startY, double rowY, double endX)
        {
            _diag.AppendLine($"Link lines drawn (tier at {(tierX - _colMidX) * 304.8:0} mm from the column middle).");
            DrawLine(P(tierX, startY), P(tierX, rowY));
            DrawLine(P(tierX, rowY), P(endX, rowY));
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

        private void ApplyInwardCorrection(ref double cx, ref double cy)
        {
            if (TubeCentreInwardCorrectionMm == 0.0) return;
            double corr = TubeCentreInwardCorrectionMm / 304.8;
            double dx = cx - _colMidX, dy = cy - _colMidY;
            if (Math.Abs(dx) >= Math.Abs(dy)) cx -= Math.Sign(dx) * corr;   // tube on a left/right face
            else cy -= Math.Sign(dy) * corr;                                 // tube on a top/bottom face
        }

        private void Consider(Arc arc, ref Arc best)
        {
            if (Math.Abs(arc.Normal.DotProduct(_dir)) < 0.95) return;   // must face the view
            if (best == null || arc.Radius > best.Radius) best = arc;
        }

        /// <summary>
        /// Groups positions that lie within ~15 mm of each other and keeps ONE bar per group.
        /// Used when several bars sit at almost the same position (e.g. 85 and 90 mm, or 150 and 152 mm):
        /// the extra bars would otherwise create tiny segments or pick the wrong one.
        /// </summary>
        private static List<TubeCenter> ClusterBy(List<TubeCenter> list, Func<TubeCenter, double> key,
            Func<TubeCenter, double> preference)
        {
            List<TubeCenter> result = new List<TubeCenter>();
            List<TubeCenter> cluster = new List<TubeCenter>();
            double last = 0;
            foreach (TubeCenter c in list.OrderBy(key))
            {
                if (cluster.Count > 0 && key(c) - last > ClusterTolFt)
                {
                    result.Add(cluster.OrderBy(preference).First());
                    cluster.Clear();
                }
                cluster.Add(c);
                last = key(c);
            }
            if (cluster.Count > 0) result.Add(cluster.OrderBy(preference).First());
            return result;
        }

        /// <summary>Positions along X (for horizontal rows). Within a group, the top-most bar wins.</summary>
        private static List<TubeCenter> HPos(List<TubeCenter> list)
        {
            return ClusterBy(list, c => c.X, c => -c.Y);
        }

        /// <summary>Positions along Y (for vertical tiers). Within a group, the left-most bar wins.</summary>
        private static List<TubeCenter> VPos(List<TubeCenter> list)
        {
            return ClusterBy(list, c => c.Y, c => c.X);
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

        /// <summary>Elevation = the view looks sideways at a vertical column; section / plan = it looks along the column.</summary>
        private static bool IsElevationView(View v)
        {
            return Math.Abs(v.ViewDirection.Z) < 0.7;
        }

        // =====================================================================
        //  ELEVATION VIEWS (back / front / right / left)
        // =====================================================================

        /// <summary>
        /// Dimensions an elevation view. Horizontal rows go above the highest bar, vertical tiers to the left,
        /// and CMS heights to the right of the column.
        /// Rows (nearest first): CORBEL, centre, lifting hooks, overall length.
        /// Tiers (nearest first): tier 1 corbel / head / top-hook levels, tier 2 hook levels,
        /// tier 3 overall height (+ dowel), tier 4 height including dowel.
        /// </summary>
        private Result RunElevation(SectionDimOptions choice, ColumnParts parts, FamilyInstance mainColumn,
            List<Element> members, FaceInfo left, FaceInfo right, FaceInfo top, FaceInfo bottom)
        {
            double leftX = left.Origin.DotProduct(_right);
            double rightX = right.Origin.DotProduct(_right);
            double topY = top.Origin.DotProduct(_up);
            double bottomY = bottom.Origin.DotProduct(_up);
            double midX = (leftX + rightX) / 2.0;
            double midY = (topY + bottomY) / 2.0;
            _colLeftX = leftX;
            _colRightX = rightX;

            double tiersBaseX = parts.LeftCorbel != null ? Math.Min(leftX, parts.LeftCorbel.MinX) : leftX;
            double rowsLeftX = tiersBaseX;
            double rowsRightX = parts.RightCorbel != null ? Math.Max(rightX, parts.RightCorbel.MaxX) : rightX;
            double rowsBaseY = Math.Max(topY, ComputeAssemblyTopZ(members, _up, topY));
            _testOrigin = P(tiersBaseX - Off(RowFirstMm), midY);

            _diag.AppendLine($"Elevation view: body {(rightX - leftX) * 304.8:0} wide x {(topY - bottomY) * 304.8:0} high.");

            // ---- Lifting hooks (the top hook sits on the centre line and is left out)
            List<TubeCenter> hooks = new List<TubeCenter>();
            if (choice.Hooks)
            {
                hooks = DropTopHooks(GetHookPoints(members, mainColumn, true));
                string hp = string.Join("  ", hooks.OrderBy(h => h.X).ThenBy(h => h.Y)
                    .Select(h => $"({(h.X - leftX) * 304.8:0},{(topY - h.Y) * 304.8:0})"));
                _diag.AppendLine($"Lifting hooks: {hooks.Count} used{(hooks.Count > 0 ? " [" + hooks[0].Source + "]" : "")}, " +
                                 $"mm from the left face / top face: {hp}");
            }

            // ---- CMS tubes: height of the tube top above the column bottom
            SortedDictionary<int, List<TubeCenter>> groups = new SortedDictionary<int, List<TubeCenter>>();
            if (choice.CmsGroups.Count > 0)
            {
                foreach (Element m in members)
                {
                    FamilyInstance fi = m as FamilyInstance;
                    if (fi == null || ReferenceEquals(fi, mainColumn)) continue;
                    Match match = TubeRegex.Match(fi.Symbol.FamilyName + " " + fi.Symbol.Name);
                    int n;
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out n)) continue;
                    if (!choice.CmsGroups.Contains(n)) continue;

                    double tubeMin, tubeMax;
                    if (!TryGetExtents(fi, _up, out tubeMin, out tubeMax)) continue;
                    double cx, cy; string src;
                    if (!TryGetCenter(fi, out cx, out cy, out src)) cx = midX;

                    if (!groups.ContainsKey(n)) groups[n] = new List<TubeCenter>();
                    groups[n].Add(new TubeCenter { Tube = fi, X = cx, Y = tubeMax, Source = "tube top" });
                }
                foreach (int wanted in choice.CmsGroups)
                    if (!groups.ContainsKey(wanted))
                        _diag.AppendLine($"CMS {wanted} was ticked, but no CMS {wanted} tubes were found in the selected assembly.");
                foreach (var g in groups)
                    _diag.AppendLine($"CMS {g.Key}: {g.Value.Count} tube(s), top height above the bottom face (mm): " +
                        string.Join(" ", g.Value.Select(c => ((c.Y - bottomY) * 304.8).ToString("0")).Distinct()));
            }

            bool needTiers = choice.ElevTier1 || choice.ElevTier2 || choice.ElevTier3 || choice.ElevTier4;
            bool needMarkers = choice.Hooks || choice.CentreLength || groups.Count > 0;

            using (Transaction t = new Transaction(_doc, "Smart Column Elevation Dimensioning"))
            {
                t.Start();

                if (needMarkers)
                    ChooseMarkerStyle(left, right, leftX, rightX, topY, midX, midY);

                // ---- Embeds that define the vertical tiers (corbels, hooks, dowel)
                List<Reference> topHookRef = new List<Reference>();
                List<Reference> lowerHookRefs = new List<Reference>();
                List<Reference> corbelRefs = new List<Reference>();
                Reference extremeBottomDowel = null;
                if (needTiers)
                    HarvestBackViewEmbeds(members, mainColumn, bottomY, topY, bottom.Ref, top.Ref,
                        out topHookRef, out lowerHookRefs, out corbelRefs, out extremeBottomDowel);

                int hIdx = 0;
                double RowYAt(int i) => rowsBaseY + Off(RowFirstMm + i * RowStepMm);
                double firstRowY = double.NaN, firstRowEndX = leftX, hooksRowY = double.NaN;

                // ---------------- CORBEL row: corbel outer faces (and the face of any block that shows in the view)
                if (choice.Corbel)
                {
                    List<Reference> cands = new List<Reference>();
                    if (parts.LeftCorbelOuter != null) cands.Add(parts.LeftCorbelOuter.Ref);
                    if (parts.RightCorbelOuter != null) cands.Add(parts.RightCorbelOuter.Ref);
                    foreach (FaceInfo f in parts.OtherLeftFaces) cands.Add(f.Ref);

                    if (cands.Count == 0)
                    {
                        _diag.AppendLine("CORBEL skipped: no projecting corbels were found on the column.");
                    }
                    else
                    {
                        double ry = RowYAt(hIdx);
                        bool ok = false;
                        TryDim("Corbel row", () =>
                        {
                            ok = PlaceHorizontal("Corbel row", new[] { left.Ref, right.Ref }, cands, ry,
                                rowsLeftX, rowsRightX, rowsRightX, choice.CorbelLabel, 1);
                        });
                        if (ok)
                        {
                            hIdx++;
                            if (double.IsNaN(firstRowY)) { firstRowY = ry; firstRowEndX = rowsLeftX; }
                        }
                    }
                }

                // ---------------- Centre dimension (left | centre | right)
                if (choice.CentreLength)
                {
                    double ry = RowYAt(hIdx);
                    bool ok = false;
                    TryDim("Centre length", () =>
                    {
                        Line probeLine = Line.CreateBound(P(leftX, ry), P(rightX, ry));
                        string how;
                        Reference centreRef = GetColumnCentreRef(mainColumn, left, right, probeLine, midX, midY, out how);
                        if (centreRef == null) { _diag.AppendLine("Centre length skipped: no centre reference available."); return; }
                        _diag.AppendLine($"Centre length uses the {how}.");
                        ok = PlaceHorizontal("Centre length", new[] { left.Ref, right.Ref }, new[] { centreRef },
                            ry, leftX, rightX, rightX, choice.CentreLabel, 1);
                    });
                    if (ok)
                    {
                        hIdx++;
                        if (double.IsNaN(firstRowY)) { firstRowY = ry; firstRowEndX = leftX; }
                    }
                }

                // ---------------- Lifting hooks row (e.g. 155 | 440 | 155)
                if (choice.Hooks)
                {
                    if (hooks.Count == 0)
                    {
                        _diag.AppendLine("Lifting hooks skipped: no hook families were found in the assembly.");
                    }
                    else
                    {
                        double ry = RowYAt(hIdx);
                        bool ok = false;
                        TryDim("Hooks row", () =>
                        {
                            ok = PlaceHorizontal("Hooks row", new[] { left.Ref, right.Ref },
                                MakeMarkerRefs(HPos(hooks), true), ry, leftX, rightX, rightX, choice.HooksLabel, 1);
                        });
                        if (ok)
                        {
                            hIdx++;
                            hooksRowY = ry;
                            if (double.IsNaN(firstRowY)) { firstRowY = ry; firstRowEndX = leftX; }
                        }
                    }
                }

                // ---------------- Overall length (outermost row)
                if (choice.OverallLength)
                {
                    double ry = RowYAt(hIdx);
                    TryDim("Overall length", () =>
                    {
                        PlaceHorizontal("Overall length", new[] { left.Ref, right.Ref }, new Reference[0],
                            ry, leftX, rightX, rightX, choice.LengthLabel, 0);
                    });
                }

                // ---------------- Vertical tiers (left of the column), nearest first
                int vIdx = 0;
                double TierXAt(int i) => tiersBaseX - Off(RowFirstMm + i * RowStepMm);
                double tier1X = double.NaN, tier2X = double.NaN;

                if (choice.ElevTier1)
                {
                    double tx = TierXAt(vIdx);
                    bool ok = false;
                    TryDim("Tier 1", () =>
                    {
                        List<Reference> cands = new List<Reference>();
                        cands.AddRange(corbelRefs);
                        cands.AddRange(topHookRef);
                        ok = PlaceVertical("Tier 1", new[] { bottom.Ref, top.Ref }, cands, tx, bottomY, topY, null, 1);
                    });
                    if (ok) { vIdx++; tier1X = tx; }
                }

                if (choice.ElevTier2)
                {
                    double tx = TierXAt(vIdx);
                    bool ok = false;
                    TryDim("Tier 2", () =>
                    {
                        ok = PlaceVertical("Tier 2", new[] { bottom.Ref, top.Ref }, lowerHookRefs, tx, bottomY, topY,
                            choice.ElevTier2Label, 1);
                    });
                    if (ok) { vIdx++; tier2X = tx; }
                }

                if (choice.ElevTier3)
                {
                    double tx = TierXAt(vIdx);
                    bool ok = false;
                    TryDim("Tier 3", () =>
                    {
                        List<Reference> cands = new List<Reference>();
                        if (extremeBottomDowel != null) cands.Add(extremeBottomDowel);
                        ok = PlaceVertical("Tier 3", new[] { bottom.Ref, top.Ref }, cands, tx, bottomY, topY,
                            choice.ElevTier3Label, 0);
                    });
                    if (ok) vIdx++;
                }

                if (choice.ElevTier4)
                {
                    if (extremeBottomDowel == null)
                    {
                        _diag.AppendLine("Tier 4 skipped: no dowel / receptor found below the column bottom.");
                    }
                    else
                    {
                        double tx = TierXAt(vIdx);
                        bool ok = false;
                        TryDim("Tier 4", () =>
                        {
                            ok = PlaceVertical("Tier 4", new[] { top.Ref }, new[] { extremeBottomDowel }, tx, bottomY, topY, null, 1);
                        });
                        if (ok) vIdx++;
                    }
                }

                // ---------------- Link lines: tier 1 -> nearest row, tier 2 -> hooks row
                if (DrawLinkLines)
                {
                    if (!double.IsNaN(tier1X) && !double.IsNaN(firstRowY))
                        TryDim("Tier 1 link lines", () => DrawLink(tier1X, topY, firstRowY, firstRowEndX));
                    if (!double.IsNaN(tier2X) && !double.IsNaN(hooksRowY))
                        TryDim("Tier 2 link lines", () => DrawLink(tier2X, topY, hooksRowY, leftX));
                }

                // ---------------- CMS heights (right of the column): bottom face -> top of the tubes
                int rIdx = 0;
                IEnumerable<KeyValuePair<int, List<TubeCenter>>> orderedGroups = choice.ReverseCmsOrder
                    ? groups.OrderByDescending(k => k.Key)
                    : groups.OrderBy(k => k.Key);

                foreach (var kv in orderedGroups)
                {
                    int n = kv.Key;
                    List<TubeCenter> tops = VPos(kv.Value);
                    double tx = rightX + Off(RowFirstMm + rIdx * RowStepMm);
                    rIdx++;
                    double lineTop = tops.Max(c => c.Y);
                    string cmsLabel = choice.CmsLabels.ContainsKey(n) ? choice.CmsLabels[n] : string.Format(CmsLabelFormat, n);

                    TryDim($"CMS {n} height", () =>
                    {
                        PlaceVertical($"CMS {n} height", new[] { bottom.Ref }, MakeMarkerRefs(tops, false),
                            tx, bottomY, lineTop, cmsLabel, 1);
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
            if (!string.IsNullOrEmpty(report)) TaskDialog.Show("Smart Column Dimensioning", report);
            return Result.Succeeded;
        }

        // ---- trial-dimension overloads that take a point instead of a line (used by the elevation harvesting)

        private bool TryDimensionSegments(ReferenceArray refs, XYZ origin, out List<double> segs)
        {
            return TryDimensionSegments(refs, Line.CreateBound(origin, origin + _up), out segs);
        }

        private bool TestDimension(ReferenceArray refs, XYZ origin)
        {
            return TestDimension(refs, Line.CreateBound(origin, origin + _up));
        }

        /// <summary>Drops the highest hook(s): the top hook sits on the centre line and is not part of the side-hook rows.</summary>
        private static List<TubeCenter> DropTopHooks(List<TubeCenter> hooks)
        {
            if (hooks.Count < 2) return hooks;
            double maxZ = hooks.Max(h => h.Z);
            List<TubeCenter> lower = hooks.Where(h => maxZ - h.Z > 0.2).ToList();   // more than ~60 mm below the highest
            return lower.Count > 0 ? lower : hooks;
        }

        private static double HookWorldZ(FamilyInstance fi)
        {
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            if (bb == null) return 0;
            Transform tf = bb.Transform ?? Transform.Identity;
            return tf.OfPoint((bb.Min + bb.Max) / 2.0).Z;
        }

        // =====================================================================
        //  Elevation helpers (ported from the elevation tool)
        // =====================================================================

        /// <summary>If the reference lies below the column bottom, returns how far (feet); otherwise 0.</summary>
        private double BelowBottomDistance(Reference r, Reference bottomFace, Reference topFace, double colHeightFt)
        {
            ReferenceArray arr = new ReferenceArray();
            arr.Append(r);
            arr.Append(bottomFace);
            arr.Append(topFace);
            List<double> segs;
            if (!TryDimensionSegments(arr, _testOrigin, out segs) || segs.Count != 2) return 0;
            // Revit orders segments bottom-to-top: [r -> bottom, bottom -> top]
            if (Math.Abs(segs[1] - colHeightFt) < 0.01 && segs[0] >= MinSegmentFt) return segs[0];
            return 0;
        }


        private bool IsUsable(Reference candidate, Reference bottomFace, Reference topFace)
        {
            ReferenceArray arr = new ReferenceArray();
            arr.Append(bottomFace);
            arr.Append(topFace);
            arr.Append(candidate);
            return TestDimension(arr, _testOrigin);
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
        //  Elevation harvesting (corbels, hooks, dowels)
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
            List<FamilyInstance> dowelInsts = new List<FamilyInstance>();
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
                        dowelInsts.Add(inst);
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

            // ---- Fallback 0: dowel family reference planes (DimRef / Bottom). Only accepted if truly BELOW the column bottom.
            if (extremeBottomDowel == null && dowelInsts.Count > 0)
            {
                double best = 0, colH = colTopZ - colBottomZ;
                foreach (FamilyInstance di in dowelInsts)
                {
                    List<Reference> planeRefs = new List<Reference>();
                    try { Reference nm0 = di.GetReferenceByName("DimRef"); if (nm0 != null) planeRefs.Add(nm0); } catch { }
                    try
                    {
                        IList<Reference> bt = di.GetReferences(FamilyInstanceReferenceType.Bottom);
                        if (bt != null && bt.Count > 0) planeRefs.Add(bt[0]);
                    }
                    catch { }

                    foreach (Reference pr in planeRefs)
                    {
                        double dist = BelowBottomDistance(pr, bottomFace, topFace, colH);
                        if (dist > best) { best = dist; extremeBottomDowel = pr; }
                    }
                }
                _diag.AppendLine(extremeBottomDowel != null
                    ? $"Dowel reference plane found {best * 304.8:0.#} mm below the column bottom."
                    : "No dowel DimRef/Bottom plane lies below the column bottom (dowels appear flush with it).");
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
        //  Lifting hooks
        // =====================================================================

        /// <summary>
        /// Centre of every lifting hook that is DRAWN in this view. The centre is taken from the hook's
        /// symbolic (2D) lines, i.e. the slot symbol you see in the section, not from its 3D bounding box
        /// (which also covers the loop and any hidden parts, so its middle is not where the hook is drawn).
        /// Hooks with no symbol in this view are ignored; if no hook has one, the bounding box is used instead.
        /// </summary>
        private List<TubeCenter> GetHookPoints(List<Element> members, FamilyInstance mainColumn, bool includeNonVisible)
        {
            List<TubeCenter> drawn = new List<TubeCenter>();
            List<TubeCenter> fallback = new List<TubeCenter>();

            foreach (Element m in members)
            {
                FamilyInstance fi = m as FamilyInstance;
                if (fi == null || ReferenceEquals(fi, mainColumn)) continue;
                if (!HookRegex.IsMatch(fi.Symbol.FamilyName)) continue;

                double x0, x1, y0, y1;
                if (TryGetSymbolExtents(fi, includeNonVisible, out x0, out x1, out y0, out y1))
                {
                    drawn.Add(new TubeCenter { Tube = fi, X = (x0 + x1) / 2.0, Y = (y0 + y1) / 2.0, Z = HookWorldZ(fi), Source = "hook symbol in view" });
                }
                else if (TryGetViewExtents(fi, out x0, out x1, out y0, out y1))
                {
                    fallback.Add(new TubeCenter { Tube = fi, X = (x0 + x1) / 2.0, Y = (y0 + y1) / 2.0, Z = HookWorldZ(fi), Source = "hook bounding box" });
                }
            }

            if (drawn.Count > 0)
            {
                if (fallback.Count > 0)
                    _diag.AppendLine($"Lifting hooks: {fallback.Count} hook(s) have no symbol in this view and were ignored.");
                return drawn;
            }
            return fallback;
        }

        /// <summary>Extents of the 2D symbolic curves a family instance shows in this view (solids are ignored).</summary>
        private bool TryGetSymbolExtents(FamilyInstance inst, bool includeNonVisible, out double x0, out double x1, out double y0, out double y1)
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
                        double qx = q.DotProduct(_right), qy = q.DotProduct(_up);
                        if (qx < x0) x0 = qx;
                        if (qx > x1) x1 = qx;
                        if (qy < y0) y0 = qy;
                        if (qy > y1) y1 = qy;
                        any = true;
                    }
                }
            }
            catch { }
            return any;
        }

        // =====================================================================
        //  Column analysis: body, corbels and blocks
        // =====================================================================

        /// <summary>
        /// Splits the column geometry into the body (largest solid) and any projecting solids
        /// (side corbels / top and bottom blocks), and finds the faces to dimension to.
        /// </summary>
        private ColumnParts AnalyseColumn(FamilyInstance column)
        {
            ColumnParts p = new ColumnParts();
            try
            {
                Options opts = new Options { ComputeReferences = true, View = _view };
                GeometryElement ge = column.get_Geometry(opts);
                List<SolidInfo> solids = new List<SolidInfo>();

                foreach (GeometryObject o in FlattenGeometry(ge))
                {
                    Solid s = o as Solid;
                    if (s == null || s.Faces.Size == 0 || s.Volume <= 0) continue;

                    SolidInfo si = new SolidInfo { Solid = s, Volume = s.Volume };
                    foreach (Edge e in s.Edges)
                        foreach (XYZ q in e.Tessellate())
                        {
                            double qx = q.DotProduct(_right), qy = q.DotProduct(_up);
                            if (qx < si.MinX) si.MinX = qx;
                            if (qx > si.MaxX) si.MaxX = qx;
                            if (qy < si.MinY) si.MinY = qy;
                            if (qy > si.MaxY) si.MaxY = qy;
                        }
                    if (si.MinX <= si.MaxX && si.MinY <= si.MaxY) solids.Add(si);
                }
                if (solids.Count == 0) return p;

                SolidInfo body = solids.OrderByDescending(sol => sol.Volume).First();
                p.Body = body;
                p.Left = FaceOf(body.Solid, -_right, false, _right);
                p.Right = FaceOf(body.Solid, _right, true, _right);
                p.Top = FaceOf(body.Solid, _up, true, _up);
                p.Bottom = FaceOf(body.Solid, -_up, false, _up);

                // sanity: the body must have a real width and height in this view
                if (p.Left != null && p.Right != null &&
                    p.Right.Origin.DotProduct(_right) - p.Left.Origin.DotProduct(_right) < 0.1)
                {
                    _diag.AppendLine("Body faces look degenerate (zero width) - falling back to the largest faces.");
                    p.Left = p.Right = p.Top = p.Bottom = null;
                }

                double minVolume = body.Volume * 0.01;
                foreach (SolidInfo si in solids)
                {
                    if (ReferenceEquals(si, body)) continue;
                    if (si.Volume < minVolume) { p.OtherSolids++; continue; }

                    if (si.MaxX <= body.MinX + SolidTolFt) p.LeftCorbel = Larger(p.LeftCorbel, si);
                    else if (si.MinX >= body.MaxX - SolidTolFt) p.RightCorbel = Larger(p.RightCorbel, si);
                    else if (si.MinY >= body.MaxY - SolidTolFt) p.TopBlock = Larger(p.TopBlock, si);
                    else if (si.MaxY <= body.MinY + SolidTolFt) p.BottomBlock = Larger(p.BottomBlock, si);
                    else
                    {
                        p.OtherSolids++;
                        FaceInfo otherFace = FaceOf(si.Solid, -_right, false, _right);
                        if (otherFace != null) p.OtherLeftFaces.Add(otherFace);
                    }
                }

                if (p.LeftCorbel != null) p.LeftCorbelOuter = FaceOf(p.LeftCorbel.Solid, -_right, false, _right);
                if (p.RightCorbel != null) p.RightCorbelOuter = FaceOf(p.RightCorbel.Solid, _right, true, _right);
                if (p.TopBlock != null)
                {
                    p.TopBlockTop = FaceOf(p.TopBlock.Solid, _up, true, _up);
                    p.TopBlockLeft = FaceOf(p.TopBlock.Solid, -_right, false, _right);
                    p.TopBlockRight = FaceOf(p.TopBlock.Solid, _right, true, _right);
                }
                if (p.BottomBlock != null)
                {
                    p.BottomBlockBottom = FaceOf(p.BottomBlock.Solid, -_up, false, _up);
                    p.BottomBlockLeft = FaceOf(p.BottomBlock.Solid, -_right, false, _right);
                    p.BottomBlockRight = FaceOf(p.BottomBlock.Solid, _right, true, _right);
                }

                // keep a record of every solid, to diagnose odd families
                _diag.AppendLine($"Column geometry: {solids.Count} solid(s).");
                int shown = 0;
                foreach (SolidInfo si in solids.OrderByDescending(sol => sol.Volume))
                {
                    if (shown++ >= 10) break;
                    _diag.AppendLine($"  solid {(ReferenceEquals(si, body) ? "(body) " : "")}volume {si.Volume * 28316.8466:0} L: " +
                                     $"x {(si.MinX - body.MinX) * 304.8:0}..{(si.MaxX - body.MinX) * 304.8:0}, " +
                                     $"y {(body.MaxY - si.MaxY) * 304.8:0}..{(body.MaxY - si.MinY) * 304.8:0} mm from the body's left/top.");
                }
            }
            catch (Exception ex)
            {
                _diag.AppendLine($"Column analysis failed: {ex.Message}");
            }
            return p;
        }

        private static SolidInfo Larger(SolidInfo current, SolidInfo candidate)
        {
            return current == null || candidate.Volume > current.Volume ? candidate : current;
        }

        /// <summary>
        /// The planar face of a solid whose normal points along normalDir, taken at the minimum or maximum
        /// position along axis (so for the "left" face: normal = -right, minimum position along right).
        /// </summary>
        private static FaceInfo FaceOf(Solid solid, XYZ normalDir, bool maxPosition, XYZ axis)
        {
            FaceInfo best = null;
            double bestPos = 0;
            foreach (Face f in solid.Faces)
            {
                PlanarFace pf = f as PlanarFace;
                if (pf == null || pf.Reference == null) continue;
                if (pf.FaceNormal.DotProduct(normalDir) < 0.95) continue;

                double pos = pf.Origin.DotProduct(axis);
                bool better = best == null
                    || (maxPosition ? pos > bestPos + 1e-6 : pos < bestPos - 1e-6)
                    || (Math.Abs(pos - bestPos) <= 1e-6 && pf.Area > best.Area);
                if (better)
                {
                    best = new FaceInfo { Ref = pf.Reference, Area = pf.Area, Origin = pf.Origin };
                    bestPos = pos;
                }
            }
            return best;
        }

        private void DescribeParts(ColumnParts p, double leftX, double topY)
        {
            if (p.Body == null)
            {
                _diag.AppendLine("Column solids could not be analysed - using the largest column faces.");
                return;
            }
            _diag.AppendLine("Projections found: " +
                $"left corbel {(p.LeftCorbel != null ? "yes" : "no")}, right corbel {(p.RightCorbel != null ? "yes" : "no")}, " +
                $"top block {(p.TopBlock != null ? "yes" : "no")}, bottom block {(p.BottomBlock != null ? "yes" : "no")}" +
                (p.OtherSolids > 0 ? $", {p.OtherSolids} other solid(s) ignored." : "."));
        }

        // =====================================================================
        //  Rebar points (hexagonal end-on bars)
        // =====================================================================

        /// <summary>
        /// Every vertical (end-on) bar of the chosen rebar type, as a point in view coordinates.
        /// Matches the rebar type name, e.g. "H25". Each bar position of a rebar set is read separately.
        /// </summary>
        private List<TubeCenter> GetBarPoints(List<Element> members, string typeContains)
        {
            List<TubeCenter> pts = new List<TubeCenter>();
            string filter = (typeContains ?? "").Trim();

            foreach (Element m in members)
            {
                Autodesk.Revit.DB.Structure.Rebar rb = m as Autodesk.Revit.DB.Structure.Rebar;
                if (rb == null) continue;

                Element typeElem = _doc.GetElement(rb.GetTypeId());
                string typeName = typeElem != null ? typeElem.Name : "";
                if (filter.Length > 0 && typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                int count = 1;
                try { count = Math.Max(1, rb.NumberOfBarPositions); } catch { }

                for (int i = 0; i < count; i++)
                {
                    IList<Curve> curves = null;
                    try
                    {
                        curves = rb.GetCenterlineCurves(false, false, false, Autodesk.Revit.DB.Structure.MultiplanarOption.IncludeOnlyPlanarCurves, i);
                    }
                    catch { continue; }
                    if (curves == null) continue;

                    foreach (Curve c in curves)
                    {
                        Line ln = c as Line;
                        if (ln == null) continue;
                        if (Math.Abs(ln.Direction.DotProduct(_dir)) < 0.98) continue;   // only bars running along the view direction

                        XYZ pt = ln.GetEndPoint(0);
                        pts.Add(new TubeCenter { X = pt.DotProduct(_right), Y = pt.DotProduct(_up), Source = "rebar" });
                    }
                }
            }

            // one point per position
            List<TubeCenter> unique = new List<TubeCenter>();
            foreach (TubeCenter c in pts)
                if (!unique.Any(u => Math.Abs(u.X - c.X) < SamePositionFt && Math.Abs(u.Y - c.Y) < SamePositionFt))
                    unique.Add(c);
            return unique;
        }

        private static bool InsideSolid(SolidInfo s, double x, double y)
        {
            return s != null &&
                   x >= s.MinX - SolidTolFt && x <= s.MaxX + SolidTolFt &&
                   y >= s.MinY - SolidTolFt && y <= s.MaxY + SolidTolFt;
        }

        private static string RegionOf(ColumnParts p, double x, double y)
        {
            if (InsideSolid(p.LeftCorbel, x, y)) return "left corbel";
            if (InsideSolid(p.RightCorbel, x, y)) return "right corbel";
            if (InsideSolid(p.TopBlock, x, y)) return "top block";
            if (InsideSolid(p.BottomBlock, x, y)) return "bottom block";
            return "body";
        }

        private void DescribeBars(List<TubeCenter> bars, double leftX, double topY, string typeName)
        {
            _diag.AppendLine($"Rebar of type containing '{typeName}': {bars.Count} vertical bar point(s).");
            foreach (string region in new[] { "left corbel", "right corbel", "top block", "bottom block", "body" })
            {
                List<TubeCenter> inRegion = bars.Where(b => b.Region == region).ToList();
                if (inRegion.Count == 0) continue;
                string list = string.Join(" ", inRegion
                    .OrderBy(b => b.Y).ThenBy(b => b.X)
                    .Take(24)
                    .Select(b => $"({(b.X - leftX) * 304.8:0},{(topY - b.Y) * 304.8:0})"));
                _diag.AppendLine($"  {region}: {inRegion.Count} - mm from body left / top face: {list}{(inRegion.Count > 24 ? " ..." : "")}");
            }
        }
    }

    /// <summary>What the user ticked in the pop-up.</summary>
    internal class SectionDimOptions
    {
        public bool IsElevation = false;   // set from the active view: elevation (true) or section / plan (false)

        // ---- shared by both view kinds
        public bool OverallLength = true;
        public string LengthLabel = "LENGTH";

        public bool CentreLength = false;
        public string CentreLabel = "L2";

        public bool Corbel = false;
        public string CorbelLabel = "CORBEL";

        public bool Hooks = false;
        public string HooksLabel = "L1";

        public HashSet<int> CmsGroups = new HashSet<int>();
        public Dictionary<int, string> CmsLabels = new Dictionary<int, string>();   // editable label per CMS group
        public bool ReverseCmsOrder = false;                                         // true = highest CMS number nearest

        // ---- section view only
        public bool OverallWidth = true;
        public string WidthLabel = "WIDTH";
        public bool CentreVertical = false;

        public bool CorbelBars = false;
        public string CorbelBarsLabel = "T25 DOWEL BARS";

        public bool BlockBars = false;
        public string BlockBarsLabel = "T25 DOWEL BARS";

        public string BarTypeName = "H25";
        public bool CmsUseBars = false;                                              // measure CMS rows to the nearest rebar centre

        // ---- elevation view only
        public bool ElevTier1 = true;                 // corbel / head / top-hook levels
        public bool ElevTier2 = true;                 // lifting hook levels
        public string ElevTier2Label = "L2";
        public bool ElevTier3 = true;                 // overall height (+ dowel)
        public string ElevTier3Label = "HEIGHT";
        public bool ElevTier4 = true;                 // overall height including dowel

        public bool AnyChosen => IsElevation
            ? OverallLength || CentreLength || Corbel || Hooks || ElevTier1 || ElevTier2 || ElevTier3 || ElevTier4 || CmsGroups.Count > 0
            : OverallLength || OverallWidth || CentreLength || CentreVertical ||
              Corbel || CorbelBars || BlockBars || Hooks || CmsGroups.Count > 0;

        public SectionDimOptions Clone()
        {
            SectionDimOptions o = (SectionDimOptions)MemberwiseClone();
            o.CmsGroups = new HashSet<int>(CmsGroups);
            o.CmsLabels = new Dictionary<int, string>(CmsLabels);
            return o;
        }
    }

    internal class Win32Owner : WF.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public Win32Owner(IntPtr handle) { Handle = handle; }
    }

    /// <summary>
    /// Check-box pop-up for the Smart Column Dimensioning button. It lists the dimensions that make sense
    /// for the active view: elevation dimensions in an elevation view, section dimensions in a section view.
    /// </summary>
    internal class SectionDimensionOptionsForm : WF.Form
    {
        private readonly SectionDimOptions _start;
        private readonly bool _elevation;

        private WF.CheckBox _chkLength, _chkWidth, _chkCentre, _chkCentreV, _chkCorbel, _chkCorbelBars, _chkBlockBars, _chkHooks;
        private WF.TextBox _txtLength, _txtWidth, _txtCentre, _txtCorbel, _txtCorbelBars, _txtBlockBars, _txtHooks, _txtBarType;
        private WF.CheckBox _chkT1, _chkT2, _chkT3, _chkT4;
        private WF.TextBox _txtT2, _txtT3;
        private WF.CheckBox _chkCmsBars, _chkReverse;
        private readonly Dictionary<int, WF.CheckBox> _cms = new Dictionary<int, WF.CheckBox>();
        private readonly Dictionary<int, WF.TextBox> _cmsText = new Dictionary<int, WF.TextBox>();
        private readonly List<WF.CheckBox> _all = new List<WF.CheckBox>();

        public SectionDimensionOptionsForm(SectionDimOptions start, Dictionary<int, int> cmsCounts, bool elevation)
        {
            _start = start;
            _elevation = elevation;

            Text = "Smart Column Dimensioning";
            FormBorderStyle = WF.FormBorderStyle.FixedDialog;
            StartPosition = WF.FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            int y = 14;
            AddText(elevation ? "Elevation view - select the dimensions to place:"
                              : "Section view - select the dimensions to place:", 14, y, 440, true);
            y += 30;

            if (elevation)
            {
                AddText("Horizontal dimensions (above the column)", 14, y, 440, false);
                y += 24;
                AddRow("Overall length", start.OverallLength, start.LengthLabel, y, out _chkLength, out _txtLength); y += 28;
                AddRow("Centre dimension (left | centre | right)", start.CentreLength, start.CentreLabel, y, out _chkCentre, out _txtCentre); y += 28;
                AddRow("Lifting hooks (e.g. 155 | 440 | 155)", start.Hooks, start.HooksLabel, y, out _chkHooks, out _txtHooks); y += 28;
                AddRow("CORBEL - corbel outer faces", start.Corbel, start.CorbelLabel, y, out _chkCorbel, out _txtCorbel); y += 36;

                AddText("Vertical dimensions (left of the column)", 14, y, 440, false);
                y += 24;
                AddCheck("Tier 1 - corbel, head and top-hook levels", start.ElevTier1, y, out _chkT1); y += 28;
                AddRow("Tier 2 - lifting hook levels", start.ElevTier2, start.ElevTier2Label, y, out _chkT2, out _txtT2); y += 28;
                AddRow("Tier 3 - overall height (+ dowel)", start.ElevTier3, start.ElevTier3Label, y, out _chkT3, out _txtT3); y += 28;
                AddCheck("Tier 4 - overall height including dowel", start.ElevTier4, y, out _chkT4); y += 36;

                AddText("Corrugated metal sleeves - height of the tubes above the bottom", 14, y, 440, false);
                y += 24;
            }
            else
            {
                AddText("Overall and centre dimensions", 14, y, 440, false);
                y += 24;
                AddRow("Overall length (horizontal)", start.OverallLength, start.LengthLabel, y, out _chkLength, out _txtLength); y += 28;
                AddRow("Overall width (vertical)", start.OverallWidth, start.WidthLabel, y, out _chkWidth, out _txtWidth); y += 28;
                AddRow("Centre dimension (left | centre | right)", start.CentreLength, start.CentreLabel, y, out _chkCentre, out _txtCentre); y += 28;
                AddCheck("Centre dimension also vertical (top | centre | bottom)", start.CentreVertical, y, out _chkCentreV); y += 36;

                AddText("Corbels, dowel bars and lifting hooks", 14, y, 440, false);
                y += 24;
                AddRow("CORBEL - outer faces of corbels / blocks", start.Corbel, start.CorbelLabel, y, out _chkCorbel, out _txtCorbel); y += 28;
                AddRow("Bars in the left / right corbels", start.CorbelBars, start.CorbelBarsLabel, y, out _chkCorbelBars, out _txtCorbelBars); y += 28;
                AddRow("Bars in the top / bottom blocks", start.BlockBars, start.BlockBarsLabel, y, out _chkBlockBars, out _txtBlockBars); y += 28;
                AddRow("Lifting hooks (row + vertical tier)", start.Hooks, start.HooksLabel, y, out _chkHooks, out _txtHooks); y += 28;
                AddText("Rebar type name contains:", 30, y + 3, 270, false);
                _txtBarType = new WF.TextBox { Left = 354, Top = y, Width = 100, Text = start.BarTypeName ?? "" };
                Controls.Add(_txtBarType);
                y += 36;

                AddText("Corrugated metal sleeves (centre to centre)", 14, y, 440, false);
                y += 24;
            }

            if (cmsCounts == null)
            {
                // not an assembly view: we cannot look ahead, so offer the usual groups
                foreach (int n in new[] { 1, 2 })
                    AddCms(n, $"CMS {n}", start.CmsGroups.Contains(n), LabelFor(start, n), true, ref y);
            }
            else if (cmsCounts.Count == 0)
            {
                AddText("No CMS tubes found in this view.", 30, y, 420, false);
                y += 26;
            }
            else
            {
                foreach (var kv in cmsCounts.OrderBy(k => k.Key))
                    AddCms(kv.Key, $"CMS {kv.Key}  -  {kv.Value} tube(s) found", start.CmsGroups.Contains(kv.Key),
                           LabelFor(start, kv.Key), true, ref y);
            }

            if (!elevation)
            {
                AddCheck("Measure CMS rows to the nearest rebar centre", start.CmsUseBars, y, out _chkCmsBars);
                y += 28;
            }

            if (cmsCounts == null || cmsCounts.Count > 1)
            {
                AddCheck("Place the highest CMS number nearest the column", start.ReverseCmsOrder, y, out _chkReverse);
                y += 28;
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

        /// <summary>The user's choices. Anything not shown for this view kind keeps its previous value.</summary>
        public SectionDimOptions Read()
        {
            SectionDimOptions o = _start.Clone();
            o.IsElevation = _elevation;

            if (_chkLength != null) { o.OverallLength = _chkLength.Checked; o.LengthLabel = _txtLength.Text.Trim(); }
            if (_chkWidth != null) { o.OverallWidth = _chkWidth.Checked; o.WidthLabel = _txtWidth.Text.Trim(); }
            if (_chkCentre != null) { o.CentreLength = _chkCentre.Checked; o.CentreLabel = _txtCentre.Text.Trim(); }
            if (_chkCentreV != null) o.CentreVertical = _chkCentreV.Checked;
            if (_chkCorbel != null) { o.Corbel = _chkCorbel.Checked; o.CorbelLabel = _txtCorbel.Text.Trim(); }
            if (_chkCorbelBars != null) { o.CorbelBars = _chkCorbelBars.Checked; o.CorbelBarsLabel = _txtCorbelBars.Text.Trim(); }
            if (_chkBlockBars != null) { o.BlockBars = _chkBlockBars.Checked; o.BlockBarsLabel = _txtBlockBars.Text.Trim(); }
            if (_chkHooks != null) { o.Hooks = _chkHooks.Checked; o.HooksLabel = _txtHooks.Text.Trim(); }
            if (_txtBarType != null) o.BarTypeName = _txtBarType.Text.Trim();
            if (_chkCmsBars != null) o.CmsUseBars = _chkCmsBars.Checked;

            if (_chkT1 != null) o.ElevTier1 = _chkT1.Checked;
            if (_chkT2 != null) { o.ElevTier2 = _chkT2.Checked; o.ElevTier2Label = _txtT2.Text.Trim(); }
            if (_chkT3 != null) { o.ElevTier3 = _chkT3.Checked; o.ElevTier3Label = _txtT3.Text.Trim(); }
            if (_chkT4 != null) o.ElevTier4 = _chkT4.Checked;

            o.CmsGroups.Clear();
            foreach (var kv in _cms)
            {
                if (kv.Value.Checked && kv.Value.Enabled) o.CmsGroups.Add(kv.Key);
                o.CmsLabels[kv.Key] = _cmsText[kv.Key].Text.Trim();
            }
            if (_chkReverse != null) o.ReverseCmsOrder = _chkReverse.Checked;
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

        private static string LabelFor(SectionDimOptions o, int n)
        {
            string text;
            return o.CmsLabels.TryGetValue(n, out text) ? text : "CMS " + n;
        }

        private void AddCms(int number, string text, bool isChecked, string label, bool enabled, ref int y)
        {
            WF.CheckBox chk = new WF.CheckBox { Text = text, Left = 30, Top = y, Width = 270, Checked = isChecked, Enabled = enabled };
            WF.Label l = new WF.Label { Text = "Label:", Left = 306, Top = y + 3, Width = 44, Height = 20, AutoSize = false };
            WF.TextBox txt = new WF.TextBox { Left = 354, Top = y, Width = 100, Text = label ?? "", Enabled = enabled };
            Controls.Add(chk);
            Controls.Add(l);
            Controls.Add(txt);
            _cms[number] = chk;
            _cmsText[number] = txt;
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