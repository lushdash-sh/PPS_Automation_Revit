using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing
{
    /// <summary>
    /// Smart dimensioning for precast walls / beams in a manually created
    /// Elevation, Section, Detail or Plan view.
    ///
    /// Features:
    ///  1. Skip logging: a report lists every tier that was skipped and why.
    ///  2. View-scale-aware tier spacing (identical to the old hard-coded values at 1:20).
    ///  3. Horizontal steps (e.g. 1425 / 425 levels) -> "175 / 1425" and "175 / 1000 / 425" tiers.
    ///  4. Every tier runs in its own transaction; if Revit rejects it at commit only that tier is
    ///     rolled back (no popup) and the tier is retried with marker fallbacks:
    ///        direct references -> temporary reference planes -> invisible detail lines.
    ///  5. Tier labels via TextNote.
    ///  6. Strict keyword matching (regex tokens for "25" and "L1").
    ///  7. SECTION / END-VIEW STANDARD (view looks along the member): horizontal strings are
    ///        CMS 1 | T-25 DOWEL BAR | L1 | LEDGE (split across vertical steps) | WIDTH
    ///     and only embeds that lie inside the view's depth range are dimensioned.
    ///  8. Reference planes are regenerated before use (fixes "not geometric references").
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WallDimensioningCommand : IExternalCommand
    {
        // ---------- Tunables (all "per scale unit" values are in model feet) ----------
        private const double FirstGapPerScale = 0.06;   // first tier ~18 mm on paper (1.2 ft at 1:20)
        private const double StepPerScale = 0.04;       // tier pitch ~12 mm on paper (0.8 ft at 1:20)
        private const double LabelGapPerScale = 0.05;   // label offset from the end of the concrete
        private const double DupTolerance = 0.15;       // ~45 mm: merge items closer than this
        private const double StepMinGap = 0.15;         // ignore faces this close to top/bottom/left/right
        private const double MinStepAreaSqFt = 1.0;     // ignore tiny faces (embed caps etc.)
        private const double DepthTolerance = 0.05;     // ft tolerance for the view-depth test

        private static readonly Regex Dia25 = new Regex(@"(^|[^0-9])25([^0-9]|$)");
        private static readonly Regex TokenL1 = new Regex(@"(^|[^A-Z0-9])L1($|[^A-Z0-9])");

        // ---------- Per-run state ----------
        private Document _doc;
        private View _view;
        private XYZ _up, _right;
        private bool _isPlan;
        private bool _sectionStyle;          // view looks along the member's long axis
        private int _scale = 20;
        private TextNoteType _textType;
        private GraphicsStyle _invisibleStyle;
        private bool _labelWarned, _invisibleWarned;
        private int _markerMode;             // 0 = reference planes, 1 = invisible detail lines
        private int _outsideDepth;
        private int _created, _labels;
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _notes = new List<string>();

        /// <summary>
        /// Revit validates dimension references only at regeneration/commit. If an attempt ends in an
        /// error, roll back just that attempt (no popup) and remember the reason.
        /// </summary>
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

        /// <summary>A planar face level. Z holds the coordinate along the relevant view axis (up or right).</summary>
        private class StepPlane
        {
            public Reference Ref;
            public double Z;
            public bool FromView;
        }

        private class Harvest
        {
            public Reference Top, Bottom, Left, Right, DowelTop;
            public double MaxZ = double.MinValue, MinZ = double.MaxValue;
            public double MaxX = double.MinValue, MinX = double.MaxValue;
            public double DowelTopZ = double.NaN;
            public List<Reference> CmsX = new List<Reference>();
            public List<Reference> LifterX = new List<Reference>();
            public List<Reference> DowelX = new List<Reference>();
            public List<Reference> CmsY = new List<Reference>();
            public List<Reference> LifterY = new List<Reference>();
            public List<XYZ> CmsPts = new List<XYZ>();
            public List<XYZ> LifterPts = new List<XYZ>();
            public List<XYZ> DowelPts = new List<XYZ>();
            public List<StepPlane> Steps = new List<StepPlane>();    // horizontal faces, high -> low, excl. top/bottom
            public List<StepPlane> XSteps = new List<StepPlane>();   // vertical faces, left -> right, excl. left/right
        }

        // =====================================================================
        //  ENTRY POINT
        // =====================================================================
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
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
            _created = 0; _labels = 0; _outsideDepth = 0;
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
                _isPlan = Math.Abs(_view.ViewDirection.DotProduct(XYZ.BasisZ)) > 0.95;
                _scale = Math.Max(1, _view.Scale);
                _sectionStyle = DetectSectionStyle(selected);

                Harvest h = HarvestGeometry(selected);

                if (h.Top == null || h.Bottom == null || h.Left == null || h.Right == null)
                {
                    TaskDialog.Show("Error", "Could not find the primary concrete boundaries in this view.");
                    return Result.Failed;
                }

                if (_outsideDepth > 0)
                    _notes.Add(_outsideDepth + " embed/dowel position(s) lie outside this view's depth range and were ignored.");

                _textType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().FirstOrDefault();

                _invisibleStyle = new FilteredElementCollector(_doc)
                    .OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>()
                    .FirstOrDefault(g => g.GraphicsStyleCategory != null
                                         && g.GraphicsStyleCategory.Name == "<Invisible lines>");

                // One group = one undo step; every tier runs in its own transaction (see Attempt)
                using (TransactionGroup tg = new TransactionGroup(_doc, "Multi-Tier Smart Dimensioning"))
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

        /// <summary>True when the view looks along the member's long axis (e.g. "Elevation Right" of a beam).</summary>
        private bool DetectSectionStyle(Element root)
        {
            if (_isPlan) return false;
            try
            {
                BoundingBoxXYZ bb = root.get_BoundingBox(null);
                if (bb == null) return false;
                XYZ d = _view.ViewDirection;
                XYZ ext = bb.Max - bb.Min;
                double depth = Math.Abs(d.X) * ext.X + Math.Abs(d.Y) * ext.Y + Math.Abs(d.Z) * ext.Z;
                double horiz = Math.Abs(_right.X) * ext.X + Math.Abs(_right.Y) * ext.Y + Math.Abs(_right.Z) * ext.Z;
                return depth > horiz * 1.5;
            }
            catch { return false; }
        }

        /// <summary>Is the point inside the view's crop-box depth range? (only used for section-style views)</summary>
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
        //  TIER BUILDERS
        // =====================================================================
        private double Off(int index) { return (FirstGapPerScale + StepPerScale * index) * _scale; }

        private void BuildHorizontalTiers(Harvest h)
        {
            PlanarFace topFace = GetFace(h.Top);
            if (topFace == null) { _skipped.Add("Horizontal tiers: top face could not be resolved."); return; }

            XYZ topOrigin = topFace.Origin;
            double labelDx = (h.MaxX - topOrigin.DotProduct(_right)) + LabelGapPerScale * _scale;

            if (_sectionStyle) { BuildSectionHorizontalTiers(h, topOrigin, labelDx); return; }

            int idx = 0;

            // --- CMS ---
            if (h.CmsX.Count > 0 || h.CmsPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = h.CmsX.Count > 0 ? Chain(h.Left, h.CmsX, h.Right) : null;
                if (TierFB("H: CMS 1", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.CmsPts, true, h.Left, h.Right, null),
                           "CMS 1", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No CMS / sleeve / grout-coupler embeds found - CMS tier not created.");

            // --- Lifters ---
            if (h.LifterX.Count > 0 || h.LifterPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = h.LifterX.Count > 0 ? Chain(h.Left, h.LifterX, h.Right) : null;
                if (TierFB("H: L1 (lifters)", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.LifterPts, true, h.Left, h.Right, null),
                           "L1", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No lifter embeds found - L1 tier not created.");

            // --- Overall (LEDGE string) ---
            {
                double d = Off(idx);
                if (TierFB("H: overall (LEDGE)", topOrigin + _up * d, _right,
                           Chain(h.Left, null, h.Right), null,
                           "LEDGE", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }

            // --- Dowels ---
            if (h.DowelX.Count > 0 || h.DowelPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = h.DowelX.Count > 0 ? Chain(h.Left, h.DowelX, h.Right) : null;
                if (TierFB("H: T-25 dowels", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.DowelPts, true, h.Left, h.Right, null),
                           "T-25 DOWEL BAR", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No dowel rebar (type name containing DOWEL or 25) found - dowel tier not created.");

            // --- Overall (LENGTH string) ---
            {
                double d = Off(idx);
                TierFB("H: overall (LENGTH)", topOrigin + _up * d, _right,
                       Chain(h.Left, null, h.Right), null,
                       "LENGTH", LabelPoint(topOrigin, labelDx, d));
            }
        }

        /// <summary>
        /// Section / end-view standard, nearest tier first:
        ///   CMS 1 | T-25 DOWEL BAR | L1 | LEDGE (split at every vertical step) | WIDTH (body width)
        /// Assumes the stepped foot is on the left; "body left" = first vertical step plane from the left.
        /// </summary>
        private void BuildSectionHorizontalTiers(Harvest h, XYZ topOrigin, double labelDx)
        {
            int idx = 0;
            Reference bodyLeft = h.XSteps.Count > 0 ? h.XSteps[0].Ref : null;
            Reference innerLeft = bodyLeft ?? h.Left;

            // --- CMS 1 ---
            if (h.CmsX.Count > 0 || h.CmsPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = h.CmsX.Count > 0 ? Chain(innerLeft, h.CmsX, h.Right) : null;
                if (TierFB("H(section): CMS 1", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.CmsPts, true, innerLeft, h.Right, null),
                           "CMS 1", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No CMS embeds inside this view - CMS 1 tier not created.");

            // --- T-25 DOWEL BAR (foot-left, dowels, body-left, right) ---
            if (h.DowelX.Count > 0 || h.DowelPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = null;
                if (h.DowelX.Count > 0)
                {
                    var mids = new List<Reference>(h.DowelX);
                    if (bodyLeft != null) mids.Add(bodyLeft);
                    direct = Chain(h.Left, mids, h.Right);
                }
                if (TierFB("H(section): T-25 dowels", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.DowelPts, true, h.Left, h.Right, bodyLeft),
                           "T-25 DOWEL BAR", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No dowels inside this view - T-25 tier not created.");

            // --- L1 ---
            if (h.LifterX.Count > 0 || h.LifterPts.Count > 0)
            {
                double d = Off(idx);
                List<Reference> direct = h.LifterX.Count > 0 ? Chain(innerLeft, h.LifterX, h.Right) : null;
                if (TierFB("H(section): L1", topOrigin + _up * d, _right, direct,
                           PlaneChain(h.LifterPts, true, innerLeft, h.Right, null),
                           "L1", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No lifters inside this view - L1 tier not created.");

            // --- LEDGE: left, every vertical step, right  (e.g. 200 / 75 / 325) ---
            if (h.XSteps.Count > 0)
            {
                double d = Off(idx);
                if (TierFB("H(section): LEDGE split", topOrigin + _up * d, _right,
                           Chain(h.Left, h.XSteps.Select(s => s.Ref), h.Right),
                           StepXChain(h.XSteps, topOrigin, h.Left, h.Right),
                           "LEDGE", LabelPoint(topOrigin, labelDx, d)))
                    idx++;
            }
            else _notes.Add("No vertical steps found in this view - LEDGE split tier not created.");

            // --- WIDTH (body width) ---
            {
                double d = Off(idx);
                TierFB("H(section): WIDTH", topOrigin + _up * d, _right,
                       Chain(innerLeft, null, h.Right), null,
                       "WIDTH", LabelPoint(topOrigin, labelDx, d));
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

            if (_isPlan)
            {
                // ---------------- PLAN VIEW ----------------
                if (h.CmsY.Count > 0 || h.CmsPts.Count > 0)
                {
                    List<Reference> direct = h.CmsY.Count > 0 ? Chain(h.Bottom, h.CmsY, h.Top) : null;
                    if (TierFB("V(plan): CMS", leftOrigin - _right * Off(v), _up, direct,
                               PlaneChain(h.CmsPts, false, h.Bottom, h.Top, null), null, null))
                        v++;
                }

                if (h.LifterY.Count > 0 || h.LifterPts.Count > 0)
                {
                    List<Reference> direct = h.LifterY.Count > 0 ? Chain(h.Bottom, h.LifterY, h.Top) : null;
                    if (TierFB("V(plan): lifters", leftOrigin - _right * Off(v), _up, direct,
                               PlaneChain(h.LifterPts, false, h.Bottom, h.Top, null), null, null))
                        v++;
                }

                if (h.Steps.Count > 0)
                {
                    if (TierFB("V(plan): ledge split", leftOrigin - _right * Off(v), _up,
                               Chain(h.Bottom, h.Steps.Select(s => s.Ref), h.Top),
                               StepChain(h.Steps, topOrigin, h.Bottom, h.Top), null, null))
                        v++;
                }

                TierFB("V(plan): overall width", leftOrigin - _right * Off(v), _up,
                       Chain(h.Bottom, null, h.Top), null, null, null);
                return;
            }

            // ---------------- ELEVATION / SECTION ----------------
            if (h.Steps.Count >= 1)
            {
                // e.g. 175 / 1425
                var first = new List<StepPlane> { h.Steps[0] };
                if (TierFB("V: top step (bottom -> step -> top)", leftOrigin - _right * Off(v), _up,
                           Chain(h.Bottom, new[] { h.Steps[0].Ref }, h.Top),
                           StepChain(first, topOrigin, h.Bottom, h.Top), null, null))
                    v++;
            }

            if (h.Steps.Count >= 2)
            {
                // e.g. 175 / 1000 / 425
                if (TierFB("V: all steps split", leftOrigin - _right * Off(v), _up,
                           Chain(h.Bottom, h.Steps.Select(s => s.Ref), h.Top),
                           StepChain(h.Steps, topOrigin, h.Bottom, h.Top), null, null))
                    v++;
            }

            if (h.DowelTop != null || !double.IsNaN(h.DowelTopZ))
            {
                List<Reference> split = h.DowelTop != null
                    ? new List<Reference> { h.Bottom, h.Top, h.DowelTop } : null;
                List<Reference> overall = h.DowelTop != null
                    ? new List<Reference> { h.Bottom, h.DowelTop } : null;

                if (TierFB("V: bottom -> top -> dowel top", leftOrigin - _right * Off(v), _up, split,
                           DowelTopChain(h, topOrigin, true), null, null))
                    v++;

                TierFB("V: overall to dowel top", leftOrigin - _right * Off(v), _up, overall,
                       DowelTopChain(h, topOrigin, false), null, null);
            }
            else
            {
                TierFB("V: overall height", leftOrigin - _right * Off(v), _up,
                       Chain(h.Bottom, null, h.Top), null, null, null);
            }
        }

        // =====================================================================
        //  DIMENSION + FALLBACK + LABEL CORE
        // =====================================================================
        private bool TryTier(XYZ start, XYZ dir, List<Reference> refs, out string err)
        {
            err = null;
            if (refs == null || refs.Count < 2) { err = "fewer than 2 references"; return false; }
            try
            {
                ReferenceArray arr = new ReferenceArray();
                foreach (Reference r in refs) arr.Append(r);
                _doc.Create.NewDimension(_view, Line.CreateBound(start, start + dir), arr);
                _created++;
                return true;
            }
            catch (Exception ex)
            {
                err = FirstLine(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Runs 'body' in its own transaction. Returns null on success, otherwise the reason.
        /// If Revit objects at commit, only this attempt (dimension, label, temporary markers)
        /// is rolled back and no popup is shown.
        /// </summary>
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

        private string PlaceTier(XYZ start, XYZ dir, List<Reference> refs, string label, XYZ labelPt)
        {
            string err;
            if (!TryTier(start, dir, refs, out err)) return err;
            AddLabel(label, labelPt);
            return null;
        }

        /// <summary>
        /// Tier with fallbacks: direct references -> reference planes -> invisible detail lines.
        /// </summary>
        private bool TierFB(string name, XYZ start, XYZ dir, List<Reference> direct,
                            Func<List<Reference>> fallback, string label, XYZ labelPt)
        {
            string err = "no direct references available";

            if (direct != null)
            {
                err = Attempt(name, () => PlaceTier(start, dir, direct, label, labelPt));
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
                        return PlaceTier(start, dir, fb, label, labelPt);
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

        private void AddLabel(string text, XYZ pt)
        {
            if (string.IsNullOrEmpty(text) || pt == null) return;
            if (_textType == null)
            {
                if (!_labelWarned) { _skipped.Add("Tier labels: no TextNoteType exists in this project."); _labelWarned = true; }
                return;
            }
            try
            {
                TextNoteOptions opts = new TextNoteOptions(_textType.Id)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Left
                };
                TextNote.Create(_doc, _view.Id, OnViewPlane(pt), text, opts);
                _labels++;
            }
            catch (Exception ex) { _skipped.Add("Label '" + text + "': " + FirstLine(ex.Message)); }
        }

        private XYZ LabelPoint(XYZ topOrigin, double dx, double tierOffset)
        {
            double textSize = 0.0;
            if (_textType != null)
            {
                Parameter p = _textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (p != null) textSize = p.AsDouble();
            }
            double halfText = textSize * _scale * 0.5;      // TextNote origin is top-left: lift by half height
            return topOrigin + _right * dx + _up * (tierOffset + halfText);
        }

        // =====================================================================
        //  MARKERS (reference planes / invisible detail lines)
        // =====================================================================
        /// <summary>Creates a marker through 'pt' running along 'lineDir' and returns its reference.</summary>
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

        /// <summary>Markers through each point. alongX=true: distinct X positions (for horizontal strings).</summary>
        private List<Reference> PlanesFromPoints(List<XYZ> pts, bool alongX)
        {
            var result = new List<Reference>();
            if (pts == null || pts.Count == 0) return result;

            var sorted = pts
                .Select(p => new { P = p, V = alongX ? p.DotProduct(_right) : p.DotProduct(_up) })
                .OrderBy(a => a.V).ToList();

            double last = double.MinValue;
            foreach (var a in sorted)
            {
                if (a.V - last <= DupTolerance) continue;
                Reference r = RefPlaneAt(a.P, alongX ? _up : _right);
                if (r != null) { result.Add(r); last = a.V; }
            }
            return result;
        }

        private Func<List<Reference>> PlaneChain(List<XYZ> pts, bool alongX, Reference first, Reference last, Reference extraMid)
        {
            return () =>
            {
                List<Reference> planes = PlanesFromPoints(pts, alongX);
                if (planes.Count == 0) return null;
                if (extraMid != null) planes.Add(extraMid);
                return Chain(first, planes, last);
            };
        }

        /// <summary>Markers at horizontal levels (steps along the up axis).</summary>
        private Func<List<Reference>> StepChain(List<StepPlane> steps, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference> { first };
                foreach (StepPlane s in steps)
                {
                    XYZ pt = basePt + _up * (s.Z - basePt.DotProduct(_up));
                    Reference r = RefPlaneAt(pt, _right);
                    if (r != null) l.Add(r);
                }
                l.Add(last);
                return l.Count > 2 ? l : null;
            };
        }

        /// <summary>Markers at vertical steps (positions along the right axis).</summary>
        private Func<List<Reference>> StepXChain(List<StepPlane> steps, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference> { first };
                foreach (StepPlane s in steps)
                {
                    XYZ pt = basePt + _right * (s.Z - basePt.DotProduct(_right));
                    Reference r = RefPlaneAt(pt, _up);
                    if (r != null) l.Add(r);
                }
                l.Add(last);
                return l.Count > 2 ? l : null;
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
            var dowelX = new List<Tuple<Reference, double>>();
            var cmsX = new List<Tuple<Reference, double>>();
            var lifterX = new List<Tuple<Reference, double>>();
            var cmsY = new List<Tuple<Reference, double>>();
            var lifterY = new List<Tuple<Reference, double>>();
            var planes = new List<StepPlane>();
            var xplanes = new List<StepPlane>();

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

            Options viewOpts = new Options { ComputeReferences = true, View = _view };
            Options modelOpts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            double maxDowelZ = double.MinValue;

            foreach (Element elem in elems)
            {
                // ---------- Rebar (dowels only) ----------
                Autodesk.Revit.DB.Structure.Rebar rebar = elem as Autodesk.Revit.DB.Structure.Rebar;
                if (rebar != null)
                {
                    HarvestDowel(rebar, viewOpts, modelOpts, h, dowelX, ref maxDowelZ);
                    continue;
                }

                // ---------- Embeds ----------
                FamilyInstance fi = elem as FamilyInstance;
                if (fi != null && fi.Symbol != null)
                {
                    string fam = fi.Symbol.FamilyName.ToUpperInvariant();
                    bool isCMS = fam.Contains("CMS") || fam.Contains("SLEEVE") || fam.Contains("GROUTEC");
                    bool isLifter = !isCMS && (fam.Contains("LIFTING") || fam.Contains("LIFTER") || TokenL1.IsMatch(fam));

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

                        // Section-style views only show what the cut plane actually passes through
                        if (_sectionStyle && !InViewDepth(pt)) { _outsideDepth++; continue; }

                        double xVal = pt.DotProduct(_right);
                        double yVal = pt.DotProduct(_up);

                        IList<Reference> clr = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                        IList<Reference> cfb = fi.GetReferences(FamilyInstanceReferenceType.CenterFrontBack);

                        // A family's centre planes are only usable if they are perpendicular to the
                        // direction being dimensioned in THIS view; otherwise Revit invalidates the
                        // dimension at commit. When misaligned we skip the direct reference and the
                        // tier falls back to markers built from the embed point.
                        bool alignX = Math.Abs(fi.HandOrientation.DotProduct(_right)) > 0.95;
                        bool alignY = Math.Abs(fi.FacingOrientation.DotProduct(_up)) > 0.95;

                        if (isCMS)
                        {
                            h.CmsPts.Add(pt);
                            if (alignX && clr != null && clr.Count > 0) cmsX.Add(Tuple.Create(clr[0], xVal));
                            if (alignY && cfb != null && cfb.Count > 0) cmsY.Add(Tuple.Create(cfb[0], yVal));
                        }
                        else
                        {
                            h.LifterPts.Add(pt);
                            if (alignX && clr != null && clr.Count > 0) lifterX.Add(Tuple.Create(clr[0], xVal));
                            if (alignY && cfb != null && cfb.Count > 0) lifterY.Add(Tuple.Create(cfb[0], yVal));
                        }
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
                                    if (pf.Area > MinStepAreaSqFt)
                                        planes.Add(new StepPlane { Ref = pf.Reference, Z = z, FromView = true });
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

                                // every sizeable vertical face is a candidate "vertical step" (section standard)
                                if ((rightDot > 0.95 || rightDot < -0.95) && pf.Area > MinStepAreaSqFt)
                                    xplanes.Add(new StepPlane { Ref = pf.Reference, Z = pf.Origin.DotProduct(_right), FromView = true });
                            }
                        }
                    }
                }

                // ---------- Concrete: Fine model geometry (hidden / simplified ledges) ----------
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
                                if (pf != null && pf.FaceNormal.DotProduct(_up) > 0.95 && pf.Area > MinStepAreaSqFt)
                                    planes.Add(new StepPlane { Ref = pf.Reference, Z = pf.Origin.DotProduct(_up), FromView = false });
                            }
                        }
                    }
                }
            }

            // ---------- Horizontal steps: unique levels strictly between bottom and top ----------
            if (h.MaxZ > double.MinValue && h.MinZ < double.MaxValue)
            {
                h.Steps = planes
                    .GroupBy(p => Math.Round(p.Z, 2))
                    .Select(g => g.OrderByDescending(p => p.FromView).First())   // prefer view-valid reference
                    .Where(p => h.MaxZ - p.Z > StepMinGap && p.Z - h.MinZ > StepMinGap)
                    .OrderByDescending(p => p.Z)
                    .ToList();
            }

            // ---------- Vertical steps: unique positions strictly between left and right ----------
            if (h.MaxX > double.MinValue && h.MinX < double.MaxValue)
            {
                h.XSteps = xplanes
                    .GroupBy(p => Math.Round(p.Z, 2))
                    .Select(g => g.First())
                    .Where(p => p.Z - h.MinX > StepMinGap && h.MaxX - p.Z > StepMinGap)
                    .OrderBy(p => p.Z)
                    .ToList();
            }

            if (maxDowelZ > double.MinValue) h.DowelTopZ = maxDowelZ;

            h.DowelX = FilterDuplicates(dowelX);
            h.CmsX = FilterDuplicates(cmsX);
            h.LifterX = FilterDuplicates(lifterX);
            h.CmsY = FilterDuplicates(cmsY);
            h.LifterY = FilterDuplicates(lifterY);
            return h;
        }

        private void HarvestDowel(Autodesk.Revit.DB.Structure.Rebar rebar, Options viewOpts, Options modelOpts,
                                  Harvest h, List<Tuple<Reference, double>> dowelX, ref double maxDowelZ)
        {
            Element type = _doc.GetElement(rebar.GetTypeId());
            if (type == null) return;
            string n = type.Name.ToUpperInvariant();
            if (!(n.Contains("DOWEL") || Dia25.IsMatch(n))) return;

            // Centre point of every bar position (needed where dowels are seen end-on)
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

                    if (_sectionStyle && !InViewDepth(pt)) { _outsideDepth++; continue; }
                    h.DowelPts.Add(pt);
                }
            }
            catch (Exception ex) { _notes.Add("Dowel centre points skipped for one rebar: " + FirstLine(ex.Message)); }

            // Elevation / section: vertical bar lines carry valid view references
            GeometryElement rv = rebar.get_Geometry(viewOpts);
            if (rv != null)
            {
                foreach (GeometryObject obj in rv)
                {
                    Line line = obj as Line;
                    if (line != null && Math.Abs(line.Direction.DotProduct(_up)) > 0.95 && line.Reference != null)
                        dowelX.Add(Tuple.Create(line.Reference, line.Origin.DotProduct(_right)));
                }
            }

            // Top of the highest dowel (elevation only: in plan 'up' is Y, not height)
            if (_isPlan) return;
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
            sb.AppendLine("Dimension strings created: " + _created);
            sb.AppendLine("Tier labels created: " + _labels);
            sb.AppendLine("View: " + _view.Name + "  (scale 1:" + _scale + ", "
                          + (_isPlan ? "plan" : _sectionStyle ? "section / end view" : "front elevation") + ")");

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
    }
}
