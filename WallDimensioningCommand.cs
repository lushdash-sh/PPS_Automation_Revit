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
    /// SMART WALL / BEAM DIMENSIONING - one command for every kind of 2D view.
    ///
    /// Run it inside an Elevation / Section / Detail / Plan view, pick the assembly, then choose
    /// what to place in the popup. The view kind is detected from the real concrete geometry and can
    /// be overridden in the popup ("View kind").
    ///
    ///   ELEVATION  (view looks at the long side)
    ///       horizontal, nearest first : CMS 1 | L1 | LEDGE (overall) | T-25 DOWEL BAR | LENGTH (overall)
    ///       vertical,   nearest first : step profile (175/1425) | all steps (175/1000/425) | overall height
    ///
    ///   PLAN / BOTTOM  (view looks along Z)
    ///       horizontal : same as elevation
    ///       vertical   : CMS rows | lifter rows | step split | overall width
    ///
    ///   SECTION / END VIEW  (view looks along the member)
    ///       horizontal, nearest first : CMS 1 | T-25 DOWEL BAR | L1 | LEDGE (every vertical step) | WIDTH (body)
    ///       vertical,   nearest first : inner body height | step split | outer body height + DEPTH
    ///       leader lines              : CMS tier -> inner string, LEDGE tier -> split string
    ///                                   (or L1 tier -> split string when there is no CMS tier)
    ///       tiers are ticked from what the cut actually passes through (embeds inside the view depth).
    ///
    /// Robustness
    ///   * every tier runs in its own transaction; a tier Revit rejects (or that would not be visible)
    ///     is rolled back without a popup and retried with temporary reference planes, then invisible
    ///     detail lines;
    ///   * nothing is wired to one model: embeds / dowels are found with keywords editable in the popup,
    ///     the body of a section is found from face areas, and every tier is skipped (and reported)
    ///     when its data is not present.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WallDimensioningCommand : IExternalCommand
    {
        // ---------- Tunables (all "per scale unit" values are in model feet) ----------
        private const double FirstGapPerScale = 0.06;   // first tier ~18 mm on paper (1.2 ft at 1:20)
        private const double StepPerScale = 0.04;       // tier pitch ~12 mm on paper (0.8 ft at 1:20)
        private const double LabelGapPerScale = 0.05;   // label offset from the end of the member
        private const double DupTolerance = 0.15;       // ~45 mm: merge items closer than this
        private const double StepMinGap = 0.15;         // ignore faces this close to an outer face
        private const double MinFaceAreaSqFt = 0.3;     // ignore tiny faces (pockets, caps); corbel / ledge elements are exempt
        private const double MinDowelLegFt = 0.5;       // a dowel has a vertical leg at least this long (~150 mm)
        private const double MajorFaceRatio = 0.5;      // a face plane is "major" if its area >= 50% of the largest
        private const double DepthTolerance = 0.05;     // ft tolerance for the view-depth test
        private const double SectionRatio = 1.5;        // depth / width above which a view is a section (end view)

        // Leave empty to use the project defaults; otherwise the exact type name from your template.
        private const string PreferredTextTypeName = "";
        private const string PreferredDimensionTypeName = "";

        private enum Mode { Elevation = 0, Plan = 1, Section = 2 }

        // ---------- Per-run state ----------
        private Document _doc;
        private View _view;
        private XYZ _up, _right;
        private Mode _mode;
        private int _scale = 20;
        private DimSettings _set;
        private List<string> _cmsKeys = new List<string>();
        private List<string> _lifterKeys = new List<string>();
        private List<string> _dowelKeys = new List<string>();
        private List<string> _corbelKeys = new List<string>();
        private TextNoteType _textType;
        private DimensionType _dimType;
        private GraphicsStyle _invisibleStyle;
        private bool _labelWarned, _invisibleWarned;
        private int _markerMode;                 // 0 = reference planes, 1 = invisible detail lines
        private int _created, _labels;
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _notes = new List<string>();
        private readonly Dictionary<string, TierInfo> _hTiers = new Dictionary<string, TierInfo>();
        private readonly Dictionary<string, StringInfo> _vStrings = new Dictionary<string, StringInfo>();

        // =====================================================================
        //  SETTINGS (filled by the popup)
        // =====================================================================
        private class DimSettings
        {
            // horizontal tiers
            public bool HCms = true, HLifter = true, HLedge = true, HDowel = true, HOverall = true;
            // vertical tiers (meaning depends on the view kind, see BuildVerticalTiers)
            public bool V1 = true, V2 = true, V3 = true, V4 = true;
            public bool Underline = true;
            public bool DepthFilter = true;
            public bool Leaders = false;

            public string LblCms = "CMS 1";
            public string LblLifter = "L1";
            public string LblLedge = "LEDGE";
            public string LblDowel = "T-25 DOWEL BAR";
            public string LblOverall = "LENGTH";
            public string LblDepth = "DEPTH";

            public string KeysCms = "CMS, SLEEVE, GROUTEC";
            public string KeysLifter = "LIFTER, LIFTING, L1";
            public string KeysDowel = "DOWEL, 25";
            public string KeysCorbel = "CORBEL, LEDGE";
        }

        // =====================================================================
        //  HELPER TYPES
        // =====================================================================
        private class RollbackOnError : IFailuresPreprocessor
        {
            public string Message;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                bool hasError = false;
                foreach (FailureMessageAccessor m in fa.GetFailureMessages())
                {
                    FailureSeverity sev = m.GetSeverity();
                    if (sev != FailureSeverity.Warning && sev != FailureSeverity.None)
                    {
                        hasError = true;
                        Message = m.GetDescriptionText();
                    }
                }
                if (hasError) return FailureProcessingResult.ProceedWithRollBack;   // undo this tier only
                fa.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>A placed horizontal tier: its offset above the top face and the right-axis coordinate where it starts.</summary>
        private class TierInfo
        {
            public double Offset;
            public double LeftX;
        }

        /// <summary>A placed vertical string: its offset left of the member and the up-axis coordinate of its top end.</summary>
        private class StringInfo
        {
            public double Offset;
            public double TopZ;
        }

        /// <summary>A planar-face level: Value = coordinate along the view's up (horizontal faces) or right (vertical faces) axis.</summary>
        private class Level
        {
            public Reference Ref;
            public double Value;
            public double Area;
            public bool FromView;
            public bool Force;      // face of a corbel / ledge element: kept whatever its size
        }

        /// <summary>One embed (CMS / lifter) or one dowel bar position.</summary>
        private class Embed
        {
            public XYZ Pt;
            public Reference XRef, YRef;     // aligned centre-plane references (may be null)
            public bool InDepth = true;
        }

        private class Harvest
        {
            public Reference Top, Bottom, Left, Right, DowelTop;
            public double MaxZ = double.MinValue, MinZ = double.MaxValue;
            public double MaxX = double.MinValue, MinX = double.MaxValue;
            public double DowelTopZ = double.NaN;
            public int DowelDiaMm;                            // nominal diameter of the dowel bars (0 = unknown)

            // raw items found in the model
            public List<Embed> CmsAll = new List<Embed>();
            public List<Embed> LifterAll = new List<Embed>();
            public List<Embed> DowelAll = new List<Embed>();
            public List<Tuple<Reference, double>> DowelLines = new List<Tuple<Reference, double>>();

            // face levels
            public List<Level> ZLevels = new List<Level>();   // all upward-facing levels, ascending
            public List<Level> XLevels = new List<Level>();   // all vertical levels incl. both extremes, ascending
            public List<Level> Steps = new List<Level>();     // upward levels strictly between bottom and top, HIGH -> LOW
            public List<Level> XSteps = new List<Level>();    // vertical levels strictly between left and right, ascending

            // resolved after the popup (see Prepare)
            public List<XYZ> CmsPts = new List<XYZ>(), LifterPts = new List<XYZ>(), DowelPts = new List<XYZ>();
            public List<Reference> CmsX = new List<Reference>(), LifterX = new List<Reference>(), DowelX = new List<Reference>();
            public List<Reference> CmsY = new List<Reference>(), LifterY = new List<Reference>();
        }

        // =====================================================================
        //  ENTRY POINT
        // =====================================================================
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            _doc = uidoc.Document;
            _view = _doc.ActiveView;

            ViewType vt = _view.ViewType;
            if (vt != ViewType.Elevation && vt != ViewType.Section && vt != ViewType.Detail
                && vt != ViewType.FloorPlan && vt != ViewType.EngineeringPlan)
            {
                TaskDialog.Show("Error", "Please run this command directly inside your manually created 2D Elevation, Section, Detail or Plan view.");
                return Result.Failed;
            }

            _skipped.Clear(); _notes.Clear();
            _hTiers.Clear(); _vStrings.Clear();
            _created = 0; _labels = 0;
            _labelWarned = false; _invisibleWarned = false; _markerMode = 0;

            try
            {
                Reference pick = uidoc.Selection.PickObject(ObjectType.Element, "Select the Precast Beam/Wall Assembly");
                Element selected = _doc.GetElement(pick);

                if (!(selected is AssemblyInstance) && !(selected is FamilyInstance))
                {
                    TaskDialog.Show("Error", "Selected element is not an Assembly or Family Instance.");
                    return Result.Failed;
                }

                _up = _view.UpDirection;
                _right = _view.RightDirection;
                _scale = Math.Max(1, _view.Scale);

                _set = new DimSettings();
                ParseKeys();

                bool isPlan = Math.Abs(_view.ViewDirection.DotProduct(XYZ.BasisZ)) > 0.95;
                _mode = isPlan ? Mode.Plan : (DetectSectionStyle(selected) ? Mode.Section : Mode.Elevation);
                SetModeDefaults();

                // ---- first pass: find out what exists in this view ----
                Harvest h = HarvestGeometry(selected);
                if (!HasBoundaries(h))
                {
                    TaskDialog.Show("Error", "Could not find the primary concrete boundaries in this view.");
                    return Result.Failed;
                }

                // ---- popup (re-opened when the user changes the view kind) ----
                while (true)
                {
                    ApplyDefaults(h);

                    string keysBefore = _set.KeysCms + "|" + _set.KeysLifter + "|" + _set.KeysDowel + "|" + _set.KeysCorbel;
                    WF.DialogResult dr;
                    Mode requested;
                    using (SettingsForm form = new SettingsForm(_set, _mode, DescribeFindings(h)))
                    {
                        dr = form.ShowDialog(new Win32Owner(uiapp.MainWindowHandle));
                        requested = form.RequestedMode;
                    }

                    if (dr == WF.DialogResult.Retry)
                    {
                        _mode = requested;
                        SetModeDefaults();
                        ParseKeys();
                        h = HarvestGeometry(selected);
                        if (!HasBoundaries(h))
                        {
                            TaskDialog.Show("Error", "Could not find the primary concrete boundaries in this view.");
                            return Result.Failed;
                        }
                        continue;
                    }

                    if (dr != WF.DialogResult.OK) return Result.Cancelled;

                    // keywords changed in the popup? harvest again
                    string keysAfter = _set.KeysCms + "|" + _set.KeysLifter + "|" + _set.KeysDowel + "|" + _set.KeysCorbel;
                    if (keysAfter != keysBefore)
                    {
                        ParseKeys();
                        h = HarvestGeometry(selected);
                        if (!HasBoundaries(h))
                        {
                            TaskDialog.Show("Error", "Could not find the primary concrete boundaries in this view.");
                            return Result.Failed;
                        }
                    }
                    break;
                }

                LoadStyles();
                Prepare(h);

                // One group = one undo step; every tier runs in its own transaction (see Attempt)
                using (TransactionGroup tg = new TransactionGroup(_doc, "Smart Wall Dimensioning"))
                {
                    tg.Start();
                    BuildHorizontalTiers(h);
                    BuildVerticalTiers(h);
                    if (_created > 0) tg.Assimilate(); else tg.RollBack();
                }

                ShowReport();
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        private static bool HasBoundaries(Harvest h)
        {
            return h.Top != null && h.Bottom != null && h.Left != null && h.Right != null;
        }

        private void SetModeDefaults()
        {
            _set.LblOverall = _mode == Mode.Section ? "WIDTH" : "LENGTH";
            _set.Leaders = _mode == Mode.Section;
        }

        // =====================================================================
        //  MEMBERS / VIEW KIND DETECTION
        // =====================================================================
        private List<Element> GetMembers(Element root)
        {
            var elems = new List<Element>();
            if (root is AssemblyInstance asm)
            {
                foreach (ElementId id in asm.GetMemberIds())
                {
                    Element e = _doc.GetElement(id);
                    if (e != null) elems.Add(e);
                }
            }
            else
            {
                elems.Add(root);
                // rebar hosted directly in a family instance is not an assembly member
                try
                {
                    var hostData = Autodesk.Revit.DB.Structure.RebarHostData.GetRebarHostData(root);
                    if (hostData != null)
                        foreach (Autodesk.Revit.DB.Structure.Rebar rb in hostData.GetRebarsInHost())
                            elems.Add(rb);
                }
                catch (Exception ex) { _notes.Add("Hosted rebar lookup skipped: " + FirstLine(ex.Message)); }
            }
            return elems;
        }

        /// <summary>
        /// A view is a section / end view when the concrete is much longer along the view direction
        /// than it is wide across the view. Measured from the real edges of the concrete solids.
        /// </summary>
        private bool DetectSectionStyle(Element root)
        {
            double minD = double.MaxValue, maxD = double.MinValue;
            double minR = double.MaxValue, maxR = double.MinValue;
            XYZ vd = _view.ViewDirection;

            try
            {
                Options opts = new Options { DetailLevel = ViewDetailLevel.Fine };
                foreach (Element e in GetMembers(root))
                {
                    if (e is Autodesk.Revit.DB.Structure.Rebar) continue;

                    FamilyInstance fi = e as FamilyInstance;
                    if (fi != null && fi.Symbol != null)
                    {
                        string nm = (fi.Symbol.FamilyName + " " + fi.Symbol.Name).ToUpperInvariant();
                        if (Matches(nm, _cmsKeys) || Matches(nm, _lifterKeys)) continue;
                    }

                    GeometryElement ge = e.get_Geometry(opts);
                    if (ge == null) continue;
                    foreach (GeometryObject go in ge)
                    {
                        foreach (Solid s in SolidsOf(go))
                        {
                            foreach (Edge edge in s.Edges)
                            {
                                Curve c = edge.AsCurve();
                                if (c == null) continue;
                                foreach (XYZ p in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                                {
                                    double d = p.DotProduct(vd);
                                    double r = p.DotProduct(_right);
                                    if (d < minD) minD = d;
                                    if (d > maxD) maxD = d;
                                    if (r < minR) minR = r;
                                    if (r > maxR) maxR = r;
                                }
                            }
                        }
                    }
                }
            }
            catch { /* fall through to the bounding-box test */ }

            if (maxD > minD && maxR > minR)
                return (maxD - minD) > (maxR - minR) * SectionRatio;

            try
            {
                BoundingBoxXYZ bb = root.get_BoundingBox(null);
                if (bb == null) return false;
                XYZ ext = bb.Max - bb.Min;
                double depth = Math.Abs(vd.X) * ext.X + Math.Abs(vd.Y) * ext.Y + Math.Abs(vd.Z) * ext.Z;
                double horiz = Math.Abs(_right.X) * ext.X + Math.Abs(_right.Y) * ext.Y + Math.Abs(_right.Z) * ext.Z;
                return depth > horiz * SectionRatio;
            }
            catch { return false; }
        }

        /// <summary>Is the point inside the view's crop-box depth range?</summary>
        private bool InViewDepth(XYZ p)
        {
            try
            {
                if (!_view.CropBoxActive) return true;
                BoundingBoxXYZ cb = _view.CropBox;
                XYZ local = cb.Transform.Inverse.OfPoint(p);
                return local.Z >= cb.Min.Z - DepthTolerance && local.Z <= cb.Max.Z + DepthTolerance;
            }
            catch { return true; }
        }

        // =====================================================================
        //  KEYWORDS / DEFAULTS / PREPARE
        // =====================================================================
        private void ParseKeys()
        {
            _cmsKeys = SplitKeys(_set.KeysCms);
            _lifterKeys = SplitKeys(_set.KeysLifter);
            _dowelKeys = SplitKeys(_set.KeysDowel);
            _corbelKeys = SplitKeys(_set.KeysCorbel);
        }

        private static List<string> SplitKeys(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim().ToUpperInvariant())
                    .Where(k => k.Length > 0)
                    .ToList();
        }

        /// <summary>
        /// Short keys (up to 3 characters, e.g. "L1", "25") must match as a whole token so that
        /// "125" or "BEAML1" are not matched; longer keys match as a substring.
        /// </summary>
        private static bool Matches(string nameUpper, List<string> keys)
        {
            if (string.IsNullOrEmpty(nameUpper)) return false;
            foreach (string k in keys)
            {
                if (k.Length == 0) continue;
                if (k.Length <= 3)
                {
                    bool digitsOnly = k.All(char.IsDigit);
                    string cls = digitsOnly ? "0-9" : "A-Z0-9";
                    if (Regex.IsMatch(nameUpper, "(^|[^" + cls + "])" + Regex.Escape(k) + "($|[^" + cls + "])"))
                        return true;
                }
                else if (nameUpper.Contains(k)) return true;
            }
            return false;
        }

        private void ApplyDefaults(Harvest h)
        {
            _set.HCms = h.CmsAll.Count > 0;
            _set.HLifter = h.LifterAll.Count > 0;
            _set.HDowel = h.DowelAll.Count > 0 || h.DowelLines.Count > 0;
            _set.HLedge = _mode != Mode.Section || h.XSteps.Count > 0;
            _set.HOverall = true;

            switch (_mode)
            {
                case Mode.Plan:
                    _set.V1 = h.CmsAll.Count > 0;
                    _set.V2 = h.LifterAll.Count > 0;
                    _set.V3 = h.Steps.Count > 0;
                    _set.V4 = true;
                    break;
                case Mode.Section:
                    _set.V1 = true;
                    _set.V2 = h.Steps.Count > 0;
                    _set.V3 = true;
                    break;
                default:
                    _set.V1 = h.Steps.Count >= 1;
                    _set.V2 = h.Steps.Count >= 2;
                    _set.V3 = true;
                    break;
            }
        }

        private string DescribeFindings(Harvest h)
        {
            var sb = new StringBuilder();
            sb.Append("Found in this view:  CMS " + h.CmsAll.Count + "  |  lifters " + h.LifterAll.Count
                      + "  |  dowel bars " + h.DowelAll.Count
                      + "  |  horizontal steps " + h.Steps.Count + "  |  vertical steps " + h.XSteps.Count);
            if (h.CmsAll.Count == 0) sb.Append("\nNo CMS tubes found with the current keywords.");
            if (h.LifterAll.Count == 0) sb.Append("\nNo lifters found with the current keywords.");
            if (h.DowelAll.Count == 0) sb.Append("\nNo dowel rebar found with the current keyword.");
            return sb.ToString();
        }

        private void LoadStyles()
        {
            var textTypes = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            _textType = textTypes.FirstOrDefault(t => PreferredTextTypeName.Length > 0 && t.Name == PreferredTextTypeName)
                        ?? textTypes.FirstOrDefault();
            if (PreferredTextTypeName.Length > 0 && (_textType == null || _textType.Name != PreferredTextTypeName))
                _notes.Add("Text type '" + PreferredTextTypeName + "' not found - used the first available text type.");

            _dimType = null;
            if (PreferredDimensionTypeName.Length > 0)
            {
                _dimType = new FilteredElementCollector(_doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .FirstOrDefault(t => t.Name == PreferredDimensionTypeName);
                if (_dimType == null)
                    _notes.Add("Dimension type '" + PreferredDimensionTypeName + "' not found - used the project default.");
            }

            _invisibleStyle = new FilteredElementCollector(_doc).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>()
                .FirstOrDefault(g => g.GraphicsStyleCategory != null && g.GraphicsStyleCategory.Name == "<Invisible lines>");
        }

        /// <summary>Turns the raw findings into the lists the tier builders use (depth filter, de-duplication).</summary>
        private void Prepare(Harvest h)
        {
            List<Embed> cms = PickItems(h.CmsAll, "CMS");
            List<Embed> lif = PickItems(h.LifterAll, "Lifters");
            List<Embed> dow = PickItems(h.DowelAll, "Dowels");

            h.CmsPts = cms.Select(e => e.Pt).ToList();
            h.LifterPts = lif.Select(e => e.Pt).ToList();
            h.DowelPts = dow.Select(e => e.Pt).ToList();

            h.CmsX = FilterDuplicates(cms.Where(e => e.XRef != null).Select(e => Tuple.Create(e.XRef, e.Pt.DotProduct(_right))).ToList());
            h.CmsY = FilterDuplicates(cms.Where(e => e.YRef != null).Select(e => Tuple.Create(e.YRef, e.Pt.DotProduct(_up))).ToList());
            h.LifterX = FilterDuplicates(lif.Where(e => e.XRef != null).Select(e => Tuple.Create(e.XRef, e.Pt.DotProduct(_right))).ToList());
            h.LifterY = FilterDuplicates(lif.Where(e => e.YRef != null).Select(e => Tuple.Create(e.YRef, e.Pt.DotProduct(_up))).ToList());
            h.DowelX = FilterDuplicates(h.DowelLines);
        }

        /// <summary>In a section view optionally keep only items that lie inside the view's depth range.</summary>
        private List<Embed> PickItems(List<Embed> all, string what)
        {
            if (all.Count == 0 || _mode != Mode.Section || !_set.DepthFilter) return all;

            List<Embed> inside = all.Where(e => e.InDepth).ToList();
            if (inside.Count > 0)
            {
                if (inside.Count < all.Count)
                    _notes.Add(what + ": " + (all.Count - inside.Count) + " position(s) outside this view's depth range were ignored.");
                return inside;
            }

            _notes.Add(what + ": none lies inside this view's depth range - all positions were used instead.");
            return all;
        }

        // =====================================================================
        //  TIER BUILDERS
        // =====================================================================
        private double Off(int index) { return (FirstGapPerScale + StepPerScale * index) * _scale; }

        private static List<Reference> Direct(List<Reference> mids, Reference first, Reference last)
        {
            return mids != null && mids.Count > 0 ? Chain(first, mids, last) : null;
        }

        private void BuildHorizontalTiers(Harvest h)
        {
            PlanarFace topFace = GetFace(h.Top);
            if (topFace == null) { _skipped.Add("Horizontal tiers: top face could not be resolved."); return; }

            XYZ topOrigin = topFace.Origin;
            double labelDx = (h.MaxX - topOrigin.DotProduct(_right)) + LabelGapPerScale * _scale;

            if (_mode == Mode.Section) BuildSectionHorizontal(h, topOrigin, labelDx);
            else BuildElevationHorizontal(h, topOrigin, labelDx);
        }

        // ----- Elevation / Plan -----
        private void BuildElevationHorizontal(Harvest h, XYZ topOrigin, double labelDx)
        {
            int idx = 0;

            if (_set.HCms)
            {
                if (h.CmsX.Count > 0 || h.CmsPts.Count > 0)
                    HTier(ref idx, "CMS", h.MinX, "H: CMS", _set.LblCms, topOrigin, labelDx,
                          Direct(h.CmsX, h.Left, h.Right),
                          PlaneChain(h.CmsPts, true, h.Left, h.Right, null));
                else _notes.Add("No CMS embeds found - CMS tier not created.");
            }

            if (_set.HLifter)
            {
                if (h.LifterX.Count > 0 || h.LifterPts.Count > 0)
                    HTier(ref idx, "LIFTER", h.MinX, "H: lifters", _set.LblLifter, topOrigin, labelDx,
                          Direct(h.LifterX, h.Left, h.Right),
                          PlaneChain(h.LifterPts, true, h.Left, h.Right, null));
                else _notes.Add("No lifter embeds found - lifter tier not created.");
            }

            if (_set.HLedge)
                HTier(ref idx, "LEDGE", h.MinX, "H: overall (ledge string)", _set.LblLedge, topOrigin, labelDx,
                      Chain(h.Left, null, h.Right), null);

            if (_set.HDowel)
            {
                if (h.DowelX.Count > 0 || h.DowelPts.Count > 0)
                    HTier(ref idx, "DOWEL", h.MinX, "H: dowels", _set.LblDowel, topOrigin, labelDx,
                          Direct(h.DowelX, h.Left, h.Right),
                          PlaneChain(h.DowelPts, true, h.Left, h.Right, null));
                else _notes.Add("No dowel rebar found - dowel tier not created.");
            }

            if (_set.HOverall)
                HTier(ref idx, "OVERALL", h.MinX, "H: overall", _set.LblOverall, topOrigin, labelDx,
                      Chain(h.Left, null, h.Right), null);
        }

        // ----- Section / end view -----
        /// <summary>
        /// The body of the section is found from face areas: the tall vertical planes are "major",
        /// the small ones (foot, ear) are steps. This works whichever side the foot is on.
        /// </summary>
        private void BuildSectionHorizontal(Harvest h, XYZ topOrigin, double labelDx)
        {
            int idx = 0;

            Reference bodyLeft = h.Left, bodyRight = h.Right;
            double bodyLeftX = h.MinX;
            var insideMajors = new List<Reference>();
            List<Level> xl = h.XLevels;
            if (xl.Count >= 2)
            {
                double maxA = xl.Max(l => l.Area);
                List<Level> majors = xl.Where(l => l.Area >= MajorFaceRatio * maxA).ToList();
                if (majors.Count >= 2)
                {
                    bodyLeft = majors.First().Ref;
                    bodyRight = majors.Last().Ref;
                    bodyLeftX = majors.First().Value;
                    foreach (Level l in majors)
                        if (l != xl.First() && l != xl.Last()) insideMajors.Add(l.Ref);
                }
            }

            // ---- CMS : bodyLeft, sleeves, bodyRight  (150 / 100 / 150) ----
            if (_set.HCms)
            {
                if (h.CmsX.Count > 0 || h.CmsPts.Count > 0)
                    HTier(ref idx, "CMS", bodyLeftX, "H(section): CMS", _set.LblCms, topOrigin, labelDx,
                          Direct(h.CmsX, bodyLeft, bodyRight),
                          PlaneChain(h.CmsPts, true, bodyLeft, bodyRight, null));
                else _notes.Add("No CMS embeds found - CMS tier not created.");
            }

            // ---- Dowels : left, dowels, inner major planes, right  (100 / 100 / 400) ----
            if (_set.HDowel)
            {
                if (h.DowelX.Count > 0 || h.DowelPts.Count > 0)
                {
                    List<Reference> direct = null;
                    if (h.DowelX.Count > 0)
                    {
                        var mids = new List<Reference>(h.DowelX);
                        mids.AddRange(insideMajors);
                        direct = Chain(h.Left, mids, h.Right);
                    }
                    HTier(ref idx, "DOWEL", h.MinX, "H(section): dowels", _set.LblDowel, topOrigin, labelDx,
                          direct, PlaneChain(h.DowelPts, true, h.Left, h.Right, insideMajors));
                }
                else _notes.Add("No dowel rebar found - dowel tier not created.");
            }

            // ---- Lifters : bodyLeft, lifters, bodyRight  (200 / 200) ----
            if (_set.HLifter)
            {
                if (h.LifterX.Count > 0 || h.LifterPts.Count > 0)
                    HTier(ref idx, "LIFTER", bodyLeftX, "H(section): lifters", _set.LblLifter, topOrigin, labelDx,
                          Direct(h.LifterX, bodyLeft, bodyRight),
                          PlaneChain(h.LifterPts, true, bodyLeft, bodyRight, null));
                else _notes.Add("No lifter embeds found - lifter tier not created.");
            }

            // ---- Ledge: left, every vertical step, right  (200 / 75 / 325) ----
            if (_set.HLedge)
            {
                if (h.XSteps.Count > 0)
                    HTier(ref idx, "LEDGE", h.MinX, "H(section): ledge split", _set.LblLedge, topOrigin, labelDx,
                          Chain(h.Left, h.XSteps.Select(l => l.Ref), h.Right),
                          StepXChain(h.XSteps, topOrigin, h.Left, h.Right));
                else _notes.Add("No vertical steps found - ledge tier not created (plain rectangular section).");
            }

            // ---- Body width (400) ----
            if (_set.HOverall)
                HTier(ref idx, "OVERALL", bodyLeftX, "H(section): body width", _set.LblOverall, topOrigin, labelDx,
                      Chain(bodyLeft, null, bodyRight), null);
        }

        private void HTier(ref int idx, string key, double leftX, string name, string label, XYZ topOrigin, double labelDx,
                           List<Reference> direct, Func<List<Reference>> fallback)
        {
            double d = Off(idx);
            Action after = () => PlaceLabel(label, topOrigin, labelDx, d);
            if (TierFB(name, topOrigin + _up * d, _right, direct, fallback, after))
            {
                _hTiers[key] = new TierInfo { Offset = d, LeftX = leftX };
                idx++;
            }
        }

        private void BuildVerticalTiers(Harvest h)
        {
            PlanarFace leftFace = GetFace(h.Left);
            PlanarFace topFace = GetFace(h.Top);
            if (leftFace == null || topFace == null)
            {
                _skipped.Add("Vertical tiers: left/top face could not be resolved.");
                return;
            }

            XYZ leftOrigin = leftFace.Origin;
            XYZ topOrigin = topFace.Origin;
            int v = 0;

            // ================= PLAN =================
            if (_mode == Mode.Plan)
            {
                if (_set.V1 && (h.CmsY.Count > 0 || h.CmsPts.Count > 0))
                    VTier(ref v, null, 0, "V(plan): CMS rows", leftOrigin,
                          Direct(h.CmsY, h.Bottom, h.Top),
                          PlaneChain(h.CmsPts, false, h.Bottom, h.Top, null), null);

                if (_set.V2 && (h.LifterY.Count > 0 || h.LifterPts.Count > 0))
                    VTier(ref v, null, 0, "V(plan): lifter rows", leftOrigin,
                          Direct(h.LifterY, h.Bottom, h.Top),
                          PlaneChain(h.LifterPts, false, h.Bottom, h.Top, null), null);

                if (_set.V3 && h.Steps.Count > 0)
                    VTier(ref v, null, 0, "V(plan): ledge split", leftOrigin,
                          Chain(h.Bottom, h.Steps.Select(l => l.Ref), h.Top),
                          StepChain(h.Steps, topOrigin, h.Bottom, h.Top), null);

                if (_set.V4)
                    VTier(ref v, null, 0, "V(plan): overall width", leftOrigin,
                          Chain(h.Bottom, null, h.Top), null, null);
                return;
            }

            // ================= SECTION =================
            if (_mode == Mode.Section)
            {
                // body top = the upward-facing level with the largest area (e.g. the 1425 level)
                Level bodyTopLevel = h.ZLevels.Count > 0 ? h.ZLevels.OrderByDescending(l => l.Area).First() : null;
                Reference bodyTop = bodyTopLevel != null ? bodyTopLevel.Ref : h.Top;
                double bodyTopZ = bodyTopLevel != null ? bodyTopLevel.Value : h.MaxZ;
                Func<List<Reference>> bodyTopFb = bodyTopLevel != null
                    ? StepChain(new List<Level> { bodyTopLevel }, topOrigin, h.Bottom, null) : null;

                if (_set.V1)   // inner body height
                    VTier(ref v, "V1", bodyTopZ, "V(section): inner body height", leftOrigin,
                          Chain(h.Bottom, null, bodyTop), bodyTopFb, null);

                if (_set.V2)   // step split e.g. 175 / 1000 / 425
                {
                    if (h.Steps.Count > 0)
                        VTier(ref v, "V2", h.MaxZ, "V(section): step split", leftOrigin,
                              Chain(h.Bottom, h.Steps.Select(l => l.Ref), h.Top),
                              StepChain(h.Steps, topOrigin, h.Bottom, h.Top), null);
                    else _notes.Add("No horizontal steps found - step split not created.");
                }

                if (_set.V3)   // outer body height + DEPTH label
                {
                    double off = Off(v);
                    Action after = null;
                    if (!string.IsNullOrWhiteSpace(_set.LblDepth))
                    {
                        XYZ anchor = DepthLabelAnchor(leftOrigin, off, _set.LblDepth, h);
                        string txt = _set.LblDepth;
                        after = () => AddVerticalLabel(txt, anchor);
                    }
                    VTier(ref v, "V3", bodyTopZ, "V(section): outer body height", leftOrigin,
                          Chain(h.Bottom, null, bodyTop), bodyTopFb, after);
                }

                DrawLeaders(leftOrigin, topOrigin);
                return;
            }

            // ================= ELEVATION =================
            if (_set.V1 && h.Steps.Count >= 1)
            {
                // e.g. 175 / 1425
                var first = new List<Level> { h.Steps[0] };
                VTier(ref v, null, 0, "V: top step (bottom -> step -> top)", leftOrigin,
                      Chain(h.Bottom, new[] { h.Steps[0].Ref }, h.Top),
                      StepChain(first, topOrigin, h.Bottom, h.Top), null);
            }

            if (_set.V2 && h.Steps.Count >= 2)
            {
                // e.g. 175 / 1000 / 425
                VTier(ref v, null, 0, "V: all steps split", leftOrigin,
                      Chain(h.Bottom, h.Steps.Select(l => l.Ref), h.Top),
                      StepChain(h.Steps, topOrigin, h.Bottom, h.Top), null);
            }

            if (_set.V3)
            {
                // dowels only matter when they project above the concrete
                bool dowelsProject = !double.IsNaN(h.DowelTopZ) && h.DowelTopZ > h.MaxZ + StepMinGap;
                if (dowelsProject)
                {
                    List<Reference> split = h.DowelTop != null ? new List<Reference> { h.Bottom, h.Top, h.DowelTop } : null;
                    List<Reference> overall = h.DowelTop != null ? new List<Reference> { h.Bottom, h.DowelTop } : null;

                    VTier(ref v, null, 0, "V: bottom -> top -> dowel top", leftOrigin, split, DowelTopChain(h, topOrigin, true), null);
                    VTier(ref v, null, 0, "V: overall to dowel top", leftOrigin, overall, DowelTopChain(h, topOrigin, false), null);
                }
                else
                {
                    VTier(ref v, null, 0, "V: overall height", leftOrigin, Chain(h.Bottom, null, h.Top), null, null);
                }
            }
        }

        private void VTier(ref int v, string key, double topZ, string name, XYZ leftOrigin, List<Reference> direct,
                           Func<List<Reference>> fallback, Action after)
        {
            double off = Off(v);
            XYZ start = leftOrigin - _right * off;
            if (TierFB(name, start, _up, direct, fallback, after))
            {
                if (key != null) _vStrings[key] = new StringInfo { Offset = off, TopZ = topZ };
                v++;
            }
        }

        // =====================================================================
        //  LEADER LINES (section views)
        // =====================================================================
        /// <summary>
        /// L-shaped leaders joining a horizontal tier to a vertical string, as in the company sections:
        /// the tier line is extended to the left, then drops to the top of the string.
        ///   with CMS tier : CMS -> inner body string (V1), LEDGE -> step split (V2)
        ///   otherwise     : lifter (or LEDGE) -> step split (V2)
        /// </summary>
        private void DrawLeaders(XYZ leftOrigin, XYZ topOrigin)
        {
            if (!_set.Leaders) return;

            var pairs = new List<string[]>();
            if (_hTiers.ContainsKey("CMS"))
            {
                pairs.Add(new[] { "CMS", "V1" });
                pairs.Add(new[] { "LEDGE", "V2" });
            }
            else if (_hTiers.ContainsKey("LIFTER")) pairs.Add(new[] { "LIFTER", "V2" });
            else pairs.Add(new[] { "LEDGE", "V2" });

            List<string[]> todo = pairs.Where(p => _hTiers.ContainsKey(p[0]) && _vStrings.ContainsKey(p[1])).ToList();
            if (todo.Count == 0)
            {
                _notes.Add("Leader lines: no matching tier / string pair was placed.");
                return;
            }

            string err = Attempt("Leader lines", () =>
            {
                foreach (string[] p in todo)
                {
                    TierInfo t = _hTiers[p[0]];
                    StringInfo s = _vStrings[p[1]];

                    double xs = leftOrigin.DotProduct(_right) - s.Offset;     // right-axis position of the string
                    double yt = topOrigin.DotProduct(_up) + t.Offset;         // up-axis position of the tier

                    XYZ corner = PointAt(topOrigin, xs, yt);
                    DrawLine(corner, PointAt(topOrigin, xs, s.TopZ));         // down to the top of the string
                    DrawLine(corner, PointAt(topOrigin, t.LeftX, yt));        // along the tier to its start
                }
                return null;
            });
            if (err != null) _skipped.Add("Leader lines: " + err);
        }

        private XYZ PointAt(XYZ basePt, double rightCoord, double upCoord)
        {
            return basePt + _right * (rightCoord - basePt.DotProduct(_right)) + _up * (upCoord - basePt.DotProduct(_up));
        }

        // =====================================================================
        //  DIMENSION + FALLBACK CORE
        // =====================================================================
        private bool TryTier(XYZ start, XYZ dir, List<Reference> refs, out string err)
        {
            err = null;
            if (refs == null || refs.Count < 2) { err = "fewer than 2 references"; return false; }
            try
            {
                ReferenceArray arr = new ReferenceArray();
                foreach (Reference r in refs) arr.Append(r);
                Line line = Line.CreateBound(start, start + dir);

                Dimension dim = _dimType != null
                    ? _doc.Create.NewDimension(_view, line, arr, _dimType)
                    : _doc.Create.NewDimension(_view, line, arr);

                // a dimension Revit accepted but cannot display here is as good as a failure
                _doc.Regenerate();
                BoundingBoxXYZ bb = null;
                try { bb = dim.get_BoundingBox(_view); } catch { }
                if (bb == null) { err = "dimension is not visible in this view"; return false; }

                _created++;
                return true;
            }
            catch (Exception ex) { err = FirstLine(ex.Message); return false; }
        }

        /// <summary>Runs 'body' in its own transaction; null = success, otherwise the reason. Rolls back only this attempt.</summary>
        private string Attempt(string name, Func<string> body)
        {
            int created0 = _created, labels0 = _labels;
            var guard = new RollbackOnError();

            using (Transaction t = new Transaction(_doc, name))
            {
                t.Start();
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(guard);
                fho.SetClearAfterRollback(true);
                t.SetFailureHandlingOptions(fho);

                string err;
                try
                {
                    _doc.Regenerate();                  // NFR-01
                    err = body();
                    if (err == null) _doc.Regenerate(); // NFR-01
                }
                catch (Exception ex) { err = FirstLine(ex.Message); }

                if (err != null)
                {
                    t.RollBack();
                    _created = created0; _labels = labels0;
                    return err;
                }

                TransactionStatus st = t.Commit();
                if (st != TransactionStatus.Committed)
                {
                    _created = created0; _labels = labels0;
                    return "Revit rejected it at commit: " + (guard.Message ?? "invalid references");
                }
                return null;
            }
        }

        private string PlaceTier(XYZ start, XYZ dir, List<Reference> refs, Action afterPlace)
        {
            string err;
            if (!TryTier(start, dir, refs, out err)) return err;
            if (afterPlace != null) afterPlace();
            return null;
        }

        /// <summary>direct references -> reference planes -> invisible detail lines.</summary>
        private bool TierFB(string name, XYZ start, XYZ dir, List<Reference> direct,
                            Func<List<Reference>> fallback, Action afterPlace)
        {
            string err = "no direct references available";

            if (direct != null)
            {
                err = Attempt(name, () => PlaceTier(start, dir, direct, afterPlace));
                if (err == null) return true;
            }

            if (fallback != null)
            {
                string[] modeNames = { "reference planes", "invisible detail lines" };
                for (int mode = 0; mode < modeNames.Length; mode++)
                {
                    _markerMode = mode;
                    string e2 = Attempt(name + " [" + modeNames[mode] + "]", () =>
                    {
                        List<Reference> fb = fallback();
                        if (fb == null) return "no markers could be built";
                        return PlaceTier(start, dir, fb, afterPlace);
                    });

                    if (e2 == null)
                    {
                        _markerMode = 0;
                        _notes.Add(name + ": direct references failed (" + err + "); used " + modeNames[mode] + ".");
                        return true;
                    }
                    err += " | " + modeNames[mode] + ": " + e2;
                }
                _markerMode = 0;
            }

            _skipped.Add(name + ": " + err);
            return false;
        }

        // =====================================================================
        //  LABELS AND TIER LINES
        // =====================================================================
        private double TextSizeFt()
        {
            if (_textType == null) return 0.0;
            Parameter p = _textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return p != null ? p.AsDouble() : 0.0;
        }

        private bool EnsureTextType()
        {
            if (_textType != null) return true;
            if (!_labelWarned) { _skipped.Add("Labels: no TextNoteType exists in this project."); _labelWarned = true; }
            return false;
        }

        /// <summary>Label to the right of the member, sitting on the tier line (optionally underlined).</summary>
        private void PlaceLabel(string text, XYZ topOrigin, double labelDx, double tierOffset)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!EnsureTextType()) return;

            double th = TextSizeFt() * _scale;                       // text height in model feet
            bool line = _set.Underline && th > 0;
            double lift = line ? th * 1.15 : th * 0.5;               // above the line, or centred on it

            AddLabel(text, topOrigin + _right * labelDx + _up * (tierOffset + lift));

            if (line)
            {
                double len = text.Length * th * 0.8;
                XYZ a = topOrigin + _right * (labelDx - LabelGapPerScale * _scale) + _up * tierOffset;
                XYZ b = topOrigin + _right * (labelDx + len + 0.02 * _scale) + _up * tierOffset;
                DrawLine(a, b);
            }
        }

        private void AddLabel(string text, XYZ pt)
        {
            try
            {
                TextNoteOptions opts = new TextNoteOptions(_textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Left };
                TextNote.Create(_doc, _view.Id, OnViewPlane(pt), text, opts);
                _labels++;
            }
            catch (Exception ex) { _skipped.Add("Label '" + text + "': " + FirstLine(ex.Message)); }
        }

        private void DrawLine(XYZ a, XYZ b)
        {
            try { _doc.Create.NewDetailCurve(_view, Line.CreateBound(OnViewPlane(a), OnViewPlane(b))); }
            catch (Exception ex) { _notes.Add("Detail line skipped: " + FirstLine(ex.Message)); }
        }

        /// <summary>Anchor so that the rotated text sits just below the bottom end of the string, centred on it.</summary>
        private XYZ DepthLabelAnchor(XYZ leftOrigin, double stringOffset, string text, Harvest h)
        {
            double textH = TextSizeFt() * _scale;
            double len = text.Length * textH * 0.75;
            double gap = 0.01 * _scale;                               // ~3 mm on paper
            double zBottom = h.MinZ - leftOrigin.DotProduct(_up);
            return leftOrigin
                   + _up * (zBottom - gap - len)
                   - _right * (stringOffset + textH * 0.5);
        }

        /// <summary>Text rotated 90 degrees counter-clockwise so it reads bottom-to-top.</summary>
        private void AddVerticalLabel(string text, XYZ anchor)
        {
            if (!EnsureTextType()) return;
            try
            {
                XYZ p = OnViewPlane(anchor);
                TextNoteOptions opts = new TextNoteOptions(_textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Left };
                TextNote tn = TextNote.Create(_doc, _view.Id, p, text, opts);
                Line axis = Line.CreateBound(p, p + _view.ViewDirection);
                ElementTransformUtils.RotateElement(_doc, tn.Id, axis, Math.PI / 2.0);
                _labels++;
            }
            catch (Exception ex) { _skipped.Add("Vertical label '" + text + "': " + FirstLine(ex.Message)); }
        }

        // =====================================================================
        //  MARKERS (reference planes / invisible detail lines)
        // =====================================================================
        private Reference RefPlaneAt(XYZ pt, XYZ lineDir)
        {
            return _markerMode == 0 ? RefPlaneMarker(pt, lineDir) : DetailLineMarker(pt, lineDir);
        }

        private Reference RefPlaneMarker(XYZ pt, XYZ lineDir)
        {
            try
            {
                ReferencePlane rp = _doc.Create.NewReferencePlane(pt, pt + lineDir, _view.ViewDirection, _view);
                try { rp.Name = "PPS_TMP_" + Guid.NewGuid().ToString("N").Substring(0, 6); } catch { }
                _doc.Regenerate();      // the plane must exist in the graphics cache before it can be dimensioned
                return rp.GetReference();
            }
            catch (Exception ex)
            {
                _skipped.Add("Reference plane: " + FirstLine(ex.Message));
                return null;
            }
        }

        private Reference DetailLineMarker(XYZ pt, XYZ lineDir)
        {
            try
            {
                XYZ p = OnViewPlane(pt);
                Line ln = Line.CreateBound(p - lineDir * 0.5, p + lineDir * 0.5);
                DetailCurve dc = _doc.Create.NewDetailCurve(_view, ln);

                if (_invisibleStyle != null) dc.LineStyle = _invisibleStyle;
                else if (!_invisibleWarned)
                {
                    _notes.Add("Detail-line markers are visible: no '<Invisible lines>' line style found in this project.");
                    _invisibleWarned = true;
                }

                _doc.Regenerate();
                return dc.GeometryCurve.Reference;
            }
            catch (Exception ex)
            {
                _skipped.Add("Detail line marker: " + FirstLine(ex.Message));
                return null;
            }
        }

        /// <summary>Markers through each point. alongX=true: distinct positions along the right axis (horizontal strings).</summary>
        private List<Reference> PlanesFromPoints(List<XYZ> pts, bool alongX)
        {
            var result = new List<Reference>();
            if (pts == null || pts.Count == 0) return result;

            double last = double.MinValue;
            foreach (var a in pts.Select(p => new { P = p, V = alongX ? p.DotProduct(_right) : p.DotProduct(_up) }).OrderBy(a => a.V))
            {
                if (a.V - last <= DupTolerance) continue;
                Reference r = RefPlaneAt(a.P, alongX ? _up : _right);
                if (r != null) { result.Add(r); last = a.V; }
            }
            return result;
        }

        private Func<List<Reference>> PlaneChain(List<XYZ> pts, bool alongX, Reference first, Reference last, List<Reference> extraMids)
        {
            return () =>
            {
                List<Reference> planes = PlanesFromPoints(pts, alongX);
                if (planes.Count == 0) return null;
                if (extraMids != null) planes.AddRange(extraMids);
                return Chain(first, planes, last);
            };
        }

        /// <summary>Markers at horizontal levels. 'last' may be null (two-reference strings such as 1425).</summary>
        private Func<List<Reference>> StepChain(List<Level> levels, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference>();
                if (first != null) l.Add(first);
                int markers = 0;
                foreach (Level s in levels)
                {
                    XYZ pt = basePt + _up * (s.Value - basePt.DotProduct(_up));
                    Reference r = RefPlaneAt(pt, _right);
                    if (r != null) { l.Add(r); markers++; }
                }
                if (last != null) l.Add(last);
                return markers == levels.Count && l.Count >= 2 ? l : null;
            };
        }

        /// <summary>Markers at vertical levels (positions along the right axis).</summary>
        private Func<List<Reference>> StepXChain(List<Level> levels, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference>();
                if (first != null) l.Add(first);
                int markers = 0;
                foreach (Level s in levels)
                {
                    XYZ pt = basePt + _right * (s.Value - basePt.DotProduct(_right));
                    Reference r = RefPlaneAt(pt, _up);
                    if (r != null) { l.Add(r); markers++; }
                }
                if (last != null) l.Add(last);
                return markers == levels.Count && l.Count >= 2 ? l : null;
            };
        }

        private Func<List<Reference>> DowelTopChain(Harvest h, XYZ basePt, bool includeTop)
        {
            return () =>
            {
                if (double.IsNaN(h.DowelTopZ)) return null;
                XYZ pt = basePt + _up * (h.DowelTopZ - basePt.DotProduct(_up));
                Reference r = RefPlaneAt(pt, _right);
                if (r == null) return null;
                return includeTop
                    ? new List<Reference> { h.Bottom, h.Top, r }
                    : new List<Reference> { h.Bottom, r };
            };
        }

        // =====================================================================
        //  GEOMETRY HARVEST
        // =====================================================================
        private Harvest HarvestGeometry(Element root)
        {
            var h = new Harvest();
            var zRaw = new List<Level>();
            var xRaw = new List<Level>();

            List<Element> elems = GetMembers(root);

            Options viewOpts = new Options { ComputeReferences = true, View = _view };
            Options modelOpts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            double maxDowelZ = double.MinValue;

            foreach (Element elem in elems)
            {
                // ---------- Rebar: dowels only ----------
                Autodesk.Revit.DB.Structure.Rebar rebar = elem as Autodesk.Revit.DB.Structure.Rebar;
                if (rebar != null)
                {
                    HarvestDowel(rebar, viewOpts, modelOpts, h, ref maxDowelZ);
                    continue;
                }

                // ---------- Embeds: CMS / lifters ----------
                FamilyInstance fi = elem as FamilyInstance;
                if (fi != null && fi.Symbol != null)
                {
                    string nameUp = (fi.Symbol.FamilyName + " " + fi.Symbol.Name).ToUpperInvariant();
                    bool isCMS = Matches(nameUp, _cmsKeys);
                    bool isLifter = !isCMS && Matches(nameUp, _lifterKeys);

                    if (isCMS || isLifter)
                    {
                        XYZ pt = XYZ.Zero;
                        LocationPoint lp = fi.Location as LocationPoint;
                        if (lp != null) pt = lp.Point;
                        else
                        {
                            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                            if (bb != null) pt = (bb.Min + bb.Max) * 0.5;
                        }

                        var em = new Embed { Pt = pt, InDepth = _mode != Mode.Section || InViewDepth(pt) };

                        // A family's centre planes are only usable if they are perpendicular to the
                        // direction being dimensioned in THIS view; otherwise Revit invalidates the
                        // dimension at commit. When misaligned the tier falls back to markers.
                        IList<Reference> clr = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                        IList<Reference> cfb = fi.GetReferences(FamilyInstanceReferenceType.CenterFrontBack);
                        if (Math.Abs(fi.HandOrientation.DotProduct(_right)) > 0.95 && clr != null && clr.Count > 0) em.XRef = clr[0];
                        if (Math.Abs(fi.FacingOrientation.DotProduct(_up)) > 0.95 && cfb != null && cfb.Count > 0) em.YRef = cfb[0];

                        if (isCMS) h.CmsAll.Add(em); else h.LifterAll.Add(em);
                        continue;   // embeds must not define the concrete boundary
                    }
                }

                // ---------- Concrete: view geometry (valid references for dimensioning) ----------
                GeometryElement viewGeom = elem.get_Geometry(viewOpts);
                if (viewGeom != null)
                {
                    foreach (GeometryObject go in viewGeom)
                    {
                        foreach (Solid solid in SolidsOf(go))
                        {
                            foreach (Face face in solid.Faces)
                            {
                                PlanarFace pf = face as PlanarFace;
                                if (pf == null) continue;

                                double upDot = pf.FaceNormal.DotProduct(_up);
                                double rightDot = pf.FaceNormal.DotProduct(_right);

                                if (upDot > 0.95)
                                {
                                    double z = pf.Origin.DotProduct(_up);
                                    if (z > h.MaxZ) { h.MaxZ = z; h.Top = pf.Reference; }
                                    zRaw.Add(new Level { Ref = pf.Reference, Value = z, Area = pf.Area, FromView = true });
                                }
                                else if (upDot < -0.95)
                                {
                                    double z = pf.Origin.DotProduct(_up);
                                    if (z < h.MinZ) { h.MinZ = z; h.Bottom = pf.Reference; }
                                }
                                else if (rightDot > 0.95)
                                {
                                    double x = pf.Origin.DotProduct(_right);
                                    if (x > h.MaxX) { h.MaxX = x; h.Right = pf.Reference; }
                                }
                                else if (rightDot < -0.95)
                                {
                                    double x = pf.Origin.DotProduct(_right);
                                    if (x < h.MinX) { h.MinX = x; h.Left = pf.Reference; }
                                }

                                if (rightDot > 0.95 || rightDot < -0.95)
                                    xRaw.Add(new Level { Ref = pf.Reference, Value = pf.Origin.DotProduct(_right), Area = pf.Area, FromView = true });
                            }
                        }
                    }
                }

                // ---------- Concrete: Fine model geometry (levels the view may have simplified away) ----------
                GeometryElement modelGeom = elem.get_Geometry(modelOpts);
                if (modelGeom != null)
                {
                    foreach (GeometryObject go in modelGeom)
                    {
                        foreach (Solid solid in SolidsOf(go))
                        {
                            foreach (Face face in solid.Faces)
                            {
                                PlanarFace pf = face as PlanarFace;
                                if (pf != null && pf.FaceNormal.DotProduct(_up) > 0.95)
                                    zRaw.Add(new Level { Ref = pf.Reference, Value = pf.Origin.DotProduct(_up), Area = pf.Area, FromView = false });
                            }
                        }
                    }
                }
            }

            // ---------- Horizontal levels ----------
            h.ZLevels = GroupLevels(zRaw);
            if (h.MaxZ > double.MinValue && h.MinZ < double.MaxValue)
            {
                h.Steps = h.ZLevels
                    .Where(l => h.MaxZ - l.Value > StepMinGap && l.Value - h.MinZ > StepMinGap)
                    .OrderByDescending(l => l.Value)
                    .ToList();
            }

            // ---------- Vertical levels (extremes are always present) ----------
            List<Level> xl = GroupLevels(xRaw);
            if (h.Left != null && h.Right != null)
            {
                double maxA = xl.Count > 0 ? xl.Max(l => l.Area) : 1.0;
                if (!xl.Any(l => Math.Abs(l.Value - h.MinX) < 0.02))
                    xl.Add(new Level { Ref = h.Left, Value = h.MinX, Area = maxA, FromView = true });
                if (!xl.Any(l => Math.Abs(l.Value - h.MaxX) < 0.02))
                    xl.Add(new Level { Ref = h.Right, Value = h.MaxX, Area = maxA, FromView = true });
            }
            h.XLevels = xl.OrderBy(l => l.Value).ToList();
            if (h.MaxX > double.MinValue && h.MinX < double.MaxValue)
            {
                h.XSteps = h.XLevels
                    .Where(l => l.Value - h.MinX > StepMinGap && h.MaxX - l.Value > StepMinGap)
                    .ToList();
            }

            if (maxDowelZ > double.MinValue) h.DowelTopZ = maxDowelZ;
            return h;
        }

        /// <summary>Merge faces on the same plane (rounded to ~3 mm); view-valid references are preferred.</summary>
        private static List<Level> GroupLevels(List<Level> raw)
        {
            return raw
                .GroupBy(l => Math.Round(l.Value, 2))
                .Select(g =>
                {
                    List<Level> view = g.Where(x => x.FromView).ToList();
                    List<Level> src = view.Count > 0 ? view : g.ToList();
                    Level best = src.OrderByDescending(x => x.Area).First();
                    return new Level { Ref = best.Ref, Value = best.Value, Area = src.Sum(x => x.Area), FromView = best.FromView };
                })
                .Where(l => l.Area > MinFaceAreaSqFt)
                .OrderBy(l => l.Value)
                .ToList();
        }

        private void HarvestDowel(Autodesk.Revit.DB.Structure.Rebar rebar, Options viewOpts, Options modelOpts,
                                  Harvest h, ref double maxDowelZ)
        {
            Element type = _doc.GetElement(rebar.GetTypeId());
            if (type == null) return;
            if (!Matches(type.Name.ToUpperInvariant(), _dowelKeys)) return;

            // centre point of every bar position (dowels are seen end-on in plans and sections)
            try
            {
                int count = Math.Max(1, rebar.NumberOfBarPositions);
                for (int i = 0; i < count; i++)
                {
                    IList<Curve> cs = rebar.GetTransformedCenterlineCurves(
                        false, false, false,
                        Autodesk.Revit.DB.Structure.MultiplanarOption.IncludeAllMultiplanarCurves, i);
                    if (cs == null || cs.Count == 0) continue;

                    Curve pick = cs.FirstOrDefault(c => c is Line && Math.Abs(((Line)c).Direction.DotProduct(_view.ViewDirection)) > 0.95)
                              ?? cs.FirstOrDefault(c => c is Line && Math.Abs(((Line)c).Direction.DotProduct(_up)) > 0.95)
                              ?? cs.First();
                    XYZ pt = (pick.GetEndPoint(0) + pick.GetEndPoint(1)) * 0.5;
                    h.DowelAll.Add(new Embed { Pt = pt, InDepth = _mode != Mode.Section || InViewDepth(pt) });
                }
            }
            catch (Exception ex) { _notes.Add("Dowel centre points skipped for one rebar: " + FirstLine(ex.Message)); }

            // vertical bar lines in the view carry valid references
            GeometryElement rv = rebar.get_Geometry(viewOpts);
            if (rv != null)
            {
                foreach (GeometryObject obj in rv)
                {
                    Line line = obj as Line;
                    if (line != null && Math.Abs(line.Direction.DotProduct(_up)) > 0.95 && line.Reference != null)
                        h.DowelLines.Add(Tuple.Create(line.Reference, line.Origin.DotProduct(_right)));
                }
            }

            // top of the highest dowel (elevation only: in plan 'up' is Y, not height)
            if (_mode == Mode.Plan) return;
            GeometryElement rm = rebar.get_Geometry(modelOpts);
            if (rm == null) return;
            foreach (GeometryObject obj in rm)
            {
                foreach (Solid solid in SolidsOf(obj))
                {
                    foreach (Face face in solid.Faces)
                    {
                        PlanarFace pf = face as PlanarFace;
                        if (pf != null && pf.FaceNormal.DotProduct(_up) > 0.95)
                        {
                            double z = pf.Origin.DotProduct(_up);
                            if (z > maxDowelZ) { maxDowelZ = z; h.DowelTop = pf.Reference; }
                        }
                    }
                }
            }
        }

        // =====================================================================
        //  SMALL HELPERS
        // =====================================================================
        private static IEnumerable<Solid> SolidsOf(GeometryObject obj)
        {
            Solid s = obj as Solid;
            if (s != null)
            {
                if (s.Faces.Size > 0) yield return s;
                yield break;
            }
            GeometryInstance gi = obj as GeometryInstance;
            if (gi != null)
            {
                foreach (GeometryObject io in gi.GetInstanceGeometry())
                {
                    Solid s2 = io as Solid;
                    if (s2 != null && s2.Faces.Size > 0) yield return s2;
                }
            }
        }

        private static List<Reference> Chain(Reference first, IEnumerable<Reference> mids, Reference last)
        {
            var l = new List<Reference>();
            if (first != null) l.Add(first);
            if (mids != null) l.AddRange(mids.Where(m => m != null));
            if (last != null) l.Add(last);
            return l;
        }

        private List<Reference> FilterDuplicates(List<Tuple<Reference, double>> input)
        {
            var result = new List<Reference>();
            double last = double.MinValue;
            foreach (var t in input.OrderBy(x => x.Item2))
            {
                if (t.Item2 - last > DupTolerance)
                {
                    result.Add(t.Item1);
                    last = t.Item2;
                }
            }
            return result;
        }

        private PlanarFace GetFace(Reference r)
        {
            if (r == null) return null;
            Element e = _doc.GetElement(r.ElementId);
            return e == null ? null : e.GetGeometryObjectFromReference(r) as PlanarFace;
        }

        private XYZ OnViewPlane(XYZ p)
        {
            XYZ n = _view.ViewDirection;
            double d = (p - _view.Origin).DotProduct(n);
            return p - n * d;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(no message)";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        private void ShowReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("View: " + _view.Name + "  (" + _mode + ", scale 1:" + _scale + ")");
            sb.AppendLine("Dimension strings created: " + _created);
            sb.AppendLine("Labels created: " + _labels);

            if (_notes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Notes:");
                foreach (string s in _notes.Distinct()) sb.AppendLine(" - " + s);
            }

            if (_skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Skipped (" + _skipped.Count + "):");
                foreach (string s in _skipped) sb.AppendLine(" - " + s);
            }

            TaskDialog td = new TaskDialog("Smart Wall Dimensioning");
            td.MainInstruction = _skipped.Count == 0 ? "Dimensions placed successfully" : "Dimensions placed with some tiers skipped";
            td.MainContent = sb.ToString();
            td.Show();
        }

        // =====================================================================
        //  POPUP
        // =====================================================================
        private class Win32Owner : WF.IWin32Window
        {
            public Win32Owner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }

        private class SettingsForm : WF.Form
        {
            private readonly DimSettings _s;
            private readonly Mode _mode;
            private WF.CheckBox cCms, cLifter, cLedge, cDowel, cOverall, c1, c2, c3, c4, cUnder, cDepth, cLeaders;
            private WF.TextBox tCms, tLifter, tLedge, tDowel, tOverall, tDepth, kCms, kLifter, kDowel;
            private readonly List<WF.CheckBox> _tierChecks = new List<WF.CheckBox>();

            /// <summary>Set when the user picks another view kind; the caller then re-opens the popup.</summary>
            public Mode RequestedMode;

            public SettingsForm(DimSettings s, Mode mode, string info)
            {
                _s = s;
                _mode = mode;
                RequestedMode = mode;

                Text = "Smart Wall Dimensioning";
                FormBorderStyle = WF.FormBorderStyle.FixedDialog;
                StartPosition = WF.FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                Font = new SD.Font("Segoe UI", 9f);

                int y = 12;

                // ---------- view kind (auto-detected, can be overridden) ----------
                Controls.Add(new WF.Label { Text = "View kind (auto-detected):", Left = 14, Top = y + 3, Width = 160 });
                var cbKind = new WF.ComboBox { Left = 178, Top = y, Width = 220, DropDownStyle = WF.ComboBoxStyle.DropDownList };
                cbKind.Items.AddRange(new object[] { "Elevation (long side)", "Plan / bottom view", "Section / end view" });
                cbKind.SelectedIndex = (int)mode;
                cbKind.SelectedIndexChanged += (o, e) =>
                {
                    Mode m = (Mode)cbKind.SelectedIndex;
                    if (m == _mode) return;
                    Collect();
                    RequestedMode = m;
                    DialogResult = WF.DialogResult.Retry;
                    Close();
                };
                Controls.Add(cbKind);
                y += 34;

                string kind = mode == Mode.Section ? "Section view" : mode == Mode.Plan ? "Plan / bottom view" : "Elevation view";
                Header(kind + " - select the dimensions to place:", ref y);

                // ---------- horizontal tiers ----------
                SubHeader("Horizontal tiers (nearest the member first)", ref y);
                if (mode == Mode.Section)
                {
                    cCms = Row("CMS - centre to centre", s.HCms, ref y, s.LblCms, out tCms);
                    cDowel = Row("Dowel bars", s.HDowel, ref y, s.LblDowel, out tDowel);
                    cLifter = Row("Lifting hooks - centre to centre", s.HLifter, ref y, s.LblLifter, out tLifter);
                    cLedge = Row("Ledge - every vertical step of the profile", s.HLedge, ref y, s.LblLedge, out tLedge);
                    cOverall = Row("Body width", s.HOverall, ref y, s.LblOverall, out tOverall);
                }
                else
                {
                    cCms = Row("CMS - centre to centre", s.HCms, ref y, s.LblCms, out tCms);
                    cLifter = Row("Lifting hooks - centre to centre", s.HLifter, ref y, s.LblLifter, out tLifter);
                    cLedge = Row("Overall - ledge string", s.HLedge, ref y, s.LblLedge, out tLedge);
                    cDowel = Row("Dowel bars", s.HDowel, ref y, s.LblDowel, out tDowel);
                    cOverall = Row("Overall length", s.HOverall, ref y, s.LblOverall, out tOverall);
                }
                _tierChecks.AddRange(new[] { cCms, cDowel, cLifter, cLedge, cOverall });

                // ---------- vertical tiers ----------
                SubHeader("Vertical tiers (nearest the member first)", ref y);
                WF.TextBox dummy;
                if (mode == Mode.Plan)
                {
                    c1 = Row("CMS rows", s.V1, ref y, null, out dummy);
                    c2 = Row("Lifter rows", s.V2, ref y, null, out dummy);
                    c3 = Row("Ledge / step split", s.V3, ref y, null, out dummy);
                    c4 = Row("Overall width", s.V4, ref y, null, out dummy);
                    _tierChecks.AddRange(new[] { c1, c2, c3, c4 });
                }
                else if (mode == Mode.Section)
                {
                    c1 = Row("Inner body height (e.g. 1425)", s.V1, ref y, null, out dummy);
                    c2 = Row("Step split (e.g. 175 / 1000 / 425)", s.V2, ref y, null, out dummy);
                    c3 = Row("Outer body height + rotated label", s.V3, ref y, s.LblDepth, out tDepth);
                    _tierChecks.AddRange(new[] { c1, c2, c3 });
                }
                else
                {
                    c1 = Row("Step profile (e.g. 175 / 1425)", s.V1, ref y, null, out dummy);
                    c2 = Row("All steps split (e.g. 175 / 1000 / 425)", s.V2, ref y, null, out dummy);
                    c3 = Row("Overall height (to dowel tops if they project)", s.V3, ref y, null, out dummy);
                    _tierChecks.AddRange(new[] { c1, c2, c3 });
                }

                // ---------- keywords ----------
                SubHeader("Detection keywords (comma separated, not case sensitive)", ref y);
                kCms = KeyRow("CMS family / type names contain:", s.KeysCms, ref y);
                kLifter = KeyRow("Lifter family / type names contain:", s.KeysLifter, ref y);
                kDowel = KeyRow("Dowel rebar type name contains:", s.KeysDowel, ref y);

                // ---------- options ----------
                SubHeader("Options", ref y);
                cUnder = Row("Draw a line under each tier label", s.Underline, ref y, null, out dummy);
                if (mode == Mode.Section)
                {
                    cLeaders = Row("Draw leader lines (tier line to vertical string)", s.Leaders, ref y, null, out dummy);
                    cDepth = Row("Only use embeds that lie inside this view's depth", s.DepthFilter, ref y, null, out dummy);
                }

                // ---------- findings ----------
                var infoLbl = new WF.Label
                {
                    Text = info,
                    Left = 14,
                    Top = y + 4,
                    Width = 572,
                    Height = 18 * (info.Split('\n').Length) + 6,
                    ForeColor = SD.Color.DimGray
                };
                Controls.Add(infoLbl);
                y += infoLbl.Height + 10;

                // ---------- buttons ----------
                WF.Button bAll = Btn("Select all", 14, y, 90);
                bAll.Click += (o, e) => SetAll(true);
                WF.Button bNone = Btn("Clear all", 110, y, 90);
                bNone.Click += (o, e) => SetAll(false);

                if (mode == Mode.Section)
                {
                    WF.Button bA = Btn("Section A preset", 206, y, 130);
                    bA.Click += (o, e) => Preset(true, false, false, true, true, true, true, true);
                    WF.Button bB = Btn("Section B preset", 342, y, 130);
                    bB.Click += (o, e) => Preset(false, true, true, true, true, false, true, true);
                }
                y += 38;

                WF.Button ok = Btn("OK", 400, y, 88);
                ok.DialogResult = WF.DialogResult.OK;
                ok.Click += (o, e) => Collect();
                WF.Button cancel = Btn("Cancel", 498, y, 88);
                cancel.DialogResult = WF.DialogResult.Cancel;
                AcceptButton = ok;
                CancelButton = cancel;

                ClientSize = new SD.Size(600, y + 46);
            }

            // ----- layout helpers -----
            private void Header(string text, ref int y)
            {
                Controls.Add(new WF.Label
                {
                    Text = text, Left = 14, Top = y, Width = 572, Height = 20,
                    Font = new SD.Font(Font, SD.FontStyle.Bold)
                });
                y += 26;
            }

            private void SubHeader(string text, ref int y)
            {
                y += 4;
                Controls.Add(new WF.Label { Text = text, Left = 14, Top = y, Width = 572, Height = 18 });
                y += 22;
            }

            private WF.CheckBox Row(string text, bool on, ref int y, string label, out WF.TextBox tb)
            {
                var cb = new WF.CheckBox { Text = text, Left = 28, Top = y, Width = 370, Height = 22, Checked = on };
                Controls.Add(cb);
                tb = null;
                if (label != null)
                {
                    Controls.Add(new WF.Label { Text = "Label", Left = 404, Top = y + 3, Width = 40 });
                    tb = new WF.TextBox { Left = 448, Top = y, Width = 138, Text = label };
                    Controls.Add(tb);
                }
                y += 26;
                return cb;
            }

            private WF.TextBox KeyRow(string text, string value, ref int y)
            {
                Controls.Add(new WF.Label { Text = text, Left = 28, Top = y + 3, Width = 230 });
                var tb = new WF.TextBox { Left = 262, Top = y, Width = 324, Text = value };
                Controls.Add(tb);
                y += 26;
                return tb;
            }

            private WF.Button Btn(string text, int x, int y, int w)
            {
                var b = new WF.Button { Text = text, Left = x, Top = y, Width = w, Height = 28 };
                Controls.Add(b);
                return b;
            }

            // ----- behaviour -----
            private void SetAll(bool on)
            {
                foreach (WF.CheckBox c in _tierChecks) if (c != null) c.Checked = on;
            }

            private void Preset(bool cms, bool lifter, bool dowel, bool ledge, bool overall, bool v1, bool v2, bool v3)
            {
                cCms.Checked = cms; cLifter.Checked = lifter; cDowel.Checked = dowel;
                cLedge.Checked = ledge; cOverall.Checked = overall;
                c1.Checked = v1; c2.Checked = v2; c3.Checked = v3;
                if (cLeaders != null) cLeaders.Checked = true;
            }

            private void Collect()
            {
                _s.HCms = cCms.Checked;
                _s.HLifter = cLifter.Checked;
                _s.HDowel = cDowel.Checked;
                _s.HLedge = cLedge.Checked;
                _s.HOverall = cOverall.Checked;
                _s.V1 = c1.Checked;
                _s.V2 = c2.Checked;
                _s.V3 = c3.Checked;
                _s.V4 = c4 != null && c4.Checked;

                _s.LblCms = tCms.Text.Trim();
                _s.LblLifter = tLifter.Text.Trim();
                _s.LblDowel = tDowel.Text.Trim();
                _s.LblLedge = tLedge.Text.Trim();
                _s.LblOverall = tOverall.Text.Trim();
                if (tDepth != null) _s.LblDepth = tDepth.Text.Trim();

                _s.KeysCms = kCms.Text;
                _s.KeysLifter = kLifter.Text;
                _s.KeysDowel = kDowel.Text;

                _s.Underline = cUnder.Checked;
                if (cLeaders != null) _s.Leaders = cLeaders.Checked;
                if (cDepth != null) _s.DepthFilter = cDepth.Checked;
            }
        }
    }
}