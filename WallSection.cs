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
    /// Dimensions an END-VIEW SECTION (view looks along the member) of a precast beam/wall
    /// to the company standard.  Run it inside the section / detail view, pick the assembly,
    /// then choose the standard:
    ///
    ///   SECTION A style
    ///     horizontal (nearest first):  CMS 1 | LEDGE (200/75/325) | WIDTH (400)
    ///     vertical   (nearest first):  1425   | 175/1000/425       | 1425 + "DEPTH"
    ///
    ///   SECTION B style
    ///     horizontal (nearest first):  T-25 DOWEL BAR | L1 | LEDGE | WIDTH
    ///     vertical   (nearest first):  175/1000/425   | 1425 + "DEPTH"
    ///
    /// Robustness: every tier runs in its own transaction and is rolled back (no popup) if Revit
    /// rejects it; it is then retried with temporary reference planes and finally invisible
    /// detail lines. A report lists everything that was skipped and why.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SectionDimensioningCommand : IExternalCommand
    {
        // ---------- Tunables (all "per scale unit" values are in model feet) ----------
        private const double FirstGapPerScale = 0.06;   // first tier ~18 mm on paper
        private const double StepPerScale = 0.04;       // tier pitch ~12 mm on paper
        private const double LabelGapPerScale = 0.05;   // label offset from the right face
        private const double DupTolerance = 0.15;       // ~45 mm: merge items closer than this
        private const double StepMinGap = 0.15;         // ignore faces this close to an outer face
        private const double MinFaceAreaSqFt = 1.0;     // ignore tiny faces (embed caps etc.)

        // Leave empty to use the project defaults; otherwise the exact type name from your template.
        private const string PreferredTextTypeName = "";
        private const string PreferredDimensionTypeName = "";

        private static readonly Regex Dia25 = new Regex(@"(^|[^0-9])25([^0-9]|$)");
        private static readonly Regex TokenL1 = new Regex(@"(^|[^A-Z0-9])L1($|[^A-Z0-9])");

        // ---------- Per-run state ----------
        private Document _doc;
        private View _view;
        private XYZ _up, _right;
        private bool _styleA;
        private int _scale = 15;
        private TextNoteType _textType;
        private DimensionType _dimType;
        private GraphicsStyle _invisibleStyle;
        private bool _labelWarned, _invisibleWarned;
        private int _markerMode;                 // 0 = reference planes, 1 = invisible detail lines
        private int _created, _labels;
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _notes = new List<string>();

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
                if (hasError) return FailureProcessingResult.ProceedWithRollBack;
                fa.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>A planar face level. Z = coordinate along the view's up (horizontal faces) or right (vertical faces) axis.</summary>
        private class StepPlane
        {
            public Reference Ref;
            public double Z;
        }

        private class Harvest
        {
            public Reference Top, Bottom, Left, Right;
            public double MaxZ = double.MinValue, MinZ = double.MaxValue;
            public double MaxX = double.MinValue, MinX = double.MaxValue;
            public List<Reference> CmsX = new List<Reference>();
            public List<Reference> LifterX = new List<Reference>();
            public List<Reference> DowelX = new List<Reference>();
            public List<XYZ> CmsPts = new List<XYZ>();
            public List<XYZ> LifterPts = new List<XYZ>();
            public List<XYZ> DowelPts = new List<XYZ>();
            public List<StepPlane> Steps = new List<StepPlane>();    // horizontal levels, high -> low (excl. top/bottom)
            public List<StepPlane> XSteps = new List<StepPlane>();   // vertical planes, left -> right (excl. left/right)
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
            if (vt != ViewType.Section && vt != ViewType.Detail && vt != ViewType.Elevation)
            {
                TaskDialog.Show("Section Dimensioning", "Please run this command inside a Section / Detail view of the member (e.g. Detail 694 or Detail 695).");
                return Result.Failed;
            }

            _up = _view.UpDirection;
            _right = _view.RightDirection;
            if (Math.Abs(_view.ViewDirection.DotProduct(XYZ.BasisZ)) > 0.95)
            {
                TaskDialog.Show("Section Dimensioning", "This looks like a plan view. Run the command in a vertical section view.");
                return Result.Failed;
            }

            _skipped.Clear(); _notes.Clear();
            _created = 0; _labels = 0;
            _labelWarned = false; _invisibleWarned = false; _markerMode = 0;
            _scale = Math.Max(1, _view.Scale);

            try
            {
                // ---- which standard? ----
                TaskDialog ask = new TaskDialog("Section Dimensioning");
                ask.MainInstruction = "Which section standard should be applied to this view?";
                ask.MainContent = "The two reference sections show different tiers, so please choose.";
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Section A style",
                    "WIDTH, LEDGE, CMS 1 + three vertical strings (1425, 175/1000/425, 1425 DEPTH)");
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Section B style",
                    "WIDTH, LEDGE, L1, T-25 DOWEL BAR + two vertical strings (175/1000/425, 1425 DEPTH)");
                ask.CommonButtons = TaskDialogCommonButtons.Cancel;
                TaskDialogResult choice = ask.Show();

                if (choice == TaskDialogResult.CommandLink1) _styleA = true;
                else if (choice == TaskDialogResult.CommandLink2) _styleA = false;
                else return Result.Cancelled;

                // ---- pick the member ----
                Reference pick = uidoc.Selection.PickObject(ObjectType.Element, "Select the Precast Beam/Wall Assembly");
                Element selected = _doc.GetElement(pick);
                if (!(selected is AssemblyInstance) && !(selected is FamilyInstance))
                {
                    TaskDialog.Show("Section Dimensioning", "Selected element is not an Assembly or Family Instance.");
                    return Result.Failed;
                }

                Harvest h = HarvestGeometry(selected);
                if (h.Top == null || h.Bottom == null || h.Left == null || h.Right == null)
                {
                    TaskDialog.Show("Section Dimensioning", "Could not find the concrete boundaries in this view. Check that the view shows the selected member.");
                    return Result.Failed;
                }

                LoadStyles();

                using (TransactionGroup tg = new TransactionGroup(_doc, "Section Dimensioning"))
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

        private void LoadStyles()
        {
            var textTypes = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            _textType = textTypes.FirstOrDefault(t => PreferredTextTypeName.Length > 0 && t.Name == PreferredTextTypeName)
                        ?? textTypes.FirstOrDefault();
            if (PreferredTextTypeName.Length > 0 && (_textType == null || _textType.Name != PreferredTextTypeName))
                _notes.Add("Text type '" + PreferredTextTypeName + "' not found - used the first available text type.");

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

            // "body left" = first vertical step plane after the foot (foot assumed on the left)
            Reference bodyLeft = h.XSteps.Count > 0 ? h.XSteps[0].Ref : null;
            Reference innerLeft = bodyLeft ?? h.Left;
            int idx = 0;

            if (_styleA)
            {
                // ---- CMS 1 : bodyLeft, sleeves, right  (150 / 100 / 150) ----
                if (h.CmsX.Count > 0 || h.CmsPts.Count > 0)
                    HTier(ref idx, "H: CMS 1", "CMS 1", topOrigin, labelDx,
                          h.CmsX.Count > 0 ? Chain(innerLeft, h.CmsX, h.Right) : null,
                          PlaneChain(h.CmsPts, true, innerLeft, h.Right, null));
                else _notes.Add("No CMS / sleeve embeds found - CMS 1 tier not created.");
            }
            else
            {
                // ---- T-25 DOWEL BAR : footLeft, dowels, bodyLeft, right  (100 / 100 / 400) ----
                if (h.DowelX.Count > 0 || h.DowelPts.Count > 0)
                {
                    List<Reference> direct = null;
                    if (h.DowelX.Count > 0)
                    {
                        var mids = new List<Reference>(h.DowelX);
                        if (bodyLeft != null) mids.Add(bodyLeft);
                        direct = Chain(h.Left, mids, h.Right);
                    }
                    HTier(ref idx, "H: T-25 dowels", "T-25 DOWEL BAR", topOrigin, labelDx,
                          direct, PlaneChain(h.DowelPts, true, h.Left, h.Right, bodyLeft));
                }
                else _notes.Add("No dowel rebar (type name containing DOWEL or 25) found - T-25 tier not created.");

                // ---- L1 : bodyLeft, lifters, right  (200 / 200) ----
                if (h.LifterX.Count > 0 || h.LifterPts.Count > 0)
                    HTier(ref idx, "H: L1", "L1", topOrigin, labelDx,
                          h.LifterX.Count > 0 ? Chain(innerLeft, h.LifterX, h.Right) : null,
                          PlaneChain(h.LifterPts, true, innerLeft, h.Right, null));
                else _notes.Add("No lifter embeds found - L1 tier not created.");
            }

            // ---- LEDGE : left, every vertical step, right  (200 / 75 / 325) ----
            if (h.XSteps.Count > 0)
                HTier(ref idx, "H: LEDGE split", "LEDGE", topOrigin, labelDx,
                      Chain(h.Left, h.XSteps.Select(s => s.Ref), h.Right),
                      StepXChain(h.XSteps, topOrigin, h.Left, h.Right));
            else _notes.Add("No vertical steps found - LEDGE tier not created (plain rectangular section).");

            // ---- WIDTH : body width  (400) ----
            HTier(ref idx, "H: WIDTH", "WIDTH", topOrigin, labelDx,
                  Chain(innerLeft, null, h.Right), null);
        }

        private void HTier(ref int idx, string name, string label, XYZ topOrigin, double labelDx,
                           List<Reference> direct, Func<List<Reference>> fallback)
        {
            double d = Off(idx);
            XYZ labelPt = LabelPoint(topOrigin, labelDx, d);
            if (TierFB(name, topOrigin + _up * d, _right, direct, fallback, () => AddLabel(label, labelPt)))
                idx++;
        }

        private void BuildVerticalTiers(Harvest h)
        {
            PlanarFace leftFace = GetFace(h.Left);
            if (leftFace == null) { _skipped.Add("Vertical tiers: left face could not be resolved."); return; }

            XYZ leftOrigin = leftFace.Origin;
            XYZ topOrigin = GetFace(h.Top) != null ? GetFace(h.Top).Origin : leftOrigin;

            // body height string = bottom -> first step (1425); plain sections use bottom -> top
            Reference bodyTop = h.Steps.Count > 0 ? h.Steps[0].Ref : h.Top;
            List<StepPlane> firstStep = h.Steps.Count > 0 ? new List<StepPlane> { h.Steps[0] } : null;
            int v = 0;

            // ---- inner body height (Section A only) : 1425 ----
            if (_styleA)
                VTier(ref v, "V: body height (inner)", leftOrigin,
                      Chain(h.Bottom, null, bodyTop),
                      firstStep != null ? StepChain(firstStep, topOrigin, h.Bottom, null) : null,
                      null);

            // ---- split : 175 / 1000 / 425 ----
            if (h.Steps.Count > 0)
                VTier(ref v, "V: step split", leftOrigin,
                      Chain(h.Bottom, h.Steps.Select(s => s.Ref), h.Top),
                      StepChain(h.Steps, topOrigin, h.Bottom, h.Top),
                      null);
            else _notes.Add("No horizontal steps found - split string not created.");

            // ---- outer body height : 1425 + DEPTH label ----
            VTier(ref v, "V: body height (outer, DEPTH)", leftOrigin,
                  Chain(h.Bottom, null, bodyTop),
                  firstStep != null ? StepChain(firstStep, topOrigin, h.Bottom, null) : null,
                  "DEPTH", h);
        }

        private void VTier(ref int v, string name, XYZ leftOrigin, List<Reference> direct,
                           Func<List<Reference>> fallback, string label, Harvest h = null)
        {
            double offset = Off(v);
            XYZ start = leftOrigin - _right * offset;
            Action after = null;
            if (label != null && h != null)
            {
                XYZ anchor = DepthLabelAnchor(leftOrigin, offset, label, h);
                after = () => AddVerticalLabel(label, anchor);
            }
            if (TierFB(name, start, _up, direct, fallback, after)) v++;
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
                if (_dimType != null) _doc.Create.NewDimension(_view, line, arr, _dimType);
                else _doc.Create.NewDimension(_view, line, arr);
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
                    _doc.Regenerate();
                    err = body();
                    if (err == null) _doc.Regenerate();
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
        //  LABELS
        // =====================================================================
        private double TextSizeFt()
        {
            if (_textType == null) return 0.0;
            Parameter p = _textType.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return p != null ? p.AsDouble() : 0.0;
        }

        private XYZ LabelPoint(XYZ topOrigin, double dx, double tierOffset)
        {
            double halfText = TextSizeFt() * _scale * 0.5;      // TextNote origin is top-left: lift by half height
            return topOrigin + _right * dx + _up * (tierOffset + halfText);
        }

        private void AddLabel(string text, XYZ pt)
        {
            if (string.IsNullOrEmpty(text) || pt == null) return;
            if (!EnsureTextType()) return;
            try
            {
                TextNoteOptions opts = new TextNoteOptions(_textType.Id) { HorizontalAlignment = HorizontalTextAlignment.Left };
                TextNote.Create(_doc, _view.Id, OnViewPlane(pt), text, opts);
                _labels++;
            }
            catch (Exception ex) { _skipped.Add("Label '" + text + "': " + FirstLine(ex.Message)); }
        }

        /// <summary>Anchor so that the rotated text sits just below the bottom end of the string, centred on it.</summary>
        private XYZ DepthLabelAnchor(XYZ leftOrigin, double stringOffset, string text, Harvest h)
        {
            double textH = TextSizeFt() * _scale;                 // text height in model feet
            double len = text.Length * textH * 0.75;              // rough text length
            double gap = 0.01 * _scale;                           // ~3 mm on paper
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

        private bool EnsureTextType()
        {
            if (_textType != null) return true;
            if (!_labelWarned) { _skipped.Add("Labels: no TextNoteType exists in this project."); _labelWarned = true; }
            return false;
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
                _doc.Regenerate();      // must exist in the graphics cache before it can be dimensioned
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

        private List<Reference> PlanesFromPoints(List<XYZ> pts)
        {
            var result = new List<Reference>();
            if (pts == null || pts.Count == 0) return result;

            double last = double.MinValue;
            foreach (var a in pts.Select(p => new { P = p, V = p.DotProduct(_right) }).OrderBy(a => a.V))
            {
                if (a.V - last <= DupTolerance) continue;
                Reference r = RefPlaneAt(a.P, _up);
                if (r != null) { result.Add(r); last = a.V; }
            }
            return result;
        }

        private Func<List<Reference>> PlaneChain(List<XYZ> pts, bool unused, Reference first, Reference last, Reference extraMid)
        {
            return () =>
            {
                List<Reference> planes = PlanesFromPoints(pts);
                if (planes.Count == 0) return null;
                if (extraMid != null) planes.Add(extraMid);
                return Chain(first, planes, last);
            };
        }

        /// <summary>Markers at horizontal levels. 'last' may be null (two-reference strings such as 1425).</summary>
        private Func<List<Reference>> StepChain(List<StepPlane> steps, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference>();
                if (first != null) l.Add(first);
                int markers = 0;
                foreach (StepPlane s in steps)
                {
                    XYZ pt = basePt + _up * (s.Z - basePt.DotProduct(_up));
                    Reference r = RefPlaneAt(pt, _right);
                    if (r != null) { l.Add(r); markers++; }
                }
                if (last != null) l.Add(last);
                return markers == steps.Count && l.Count >= 2 ? l : null;
            };
        }

        /// <summary>Markers at vertical steps (positions along the right axis).</summary>
        private Func<List<Reference>> StepXChain(List<StepPlane> steps, XYZ basePt, Reference first, Reference last)
        {
            return () =>
            {
                var l = new List<Reference>();
                if (first != null) l.Add(first);
                int markers = 0;
                foreach (StepPlane s in steps)
                {
                    XYZ pt = basePt + _right * (s.Z - basePt.DotProduct(_right));
                    Reference r = RefPlaneAt(pt, _up);
                    if (r != null) { l.Add(r); markers++; }
                }
                if (last != null) l.Add(last);
                return markers == steps.Count && l.Count >= 2 ? l : null;
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

            foreach (Element elem in elems)
            {
                // ---------- Rebar: dowels only ----------
                Autodesk.Revit.DB.Structure.Rebar rebar = elem as Autodesk.Revit.DB.Structure.Rebar;
                if (rebar != null)
                {
                    HarvestDowel(rebar, viewOpts, h, dowelX);
                    continue;
                }

                // ---------- Embeds: CMS / lifters ----------
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

                        double xVal = pt.DotProduct(_right);
                        IList<Reference> clr = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);

                        // centre plane is only usable if it is perpendicular to the dimension direction in this view
                        bool alignX = Math.Abs(fi.HandOrientation.DotProduct(_right)) > 0.95;

                        if (isCMS)
                        {
                            h.CmsPts.Add(pt);
                            if (alignX && clr != null && clr.Count > 0) cmsX.Add(Tuple.Create(clr[0], xVal));
                        }
                        else
                        {
                            h.LifterPts.Add(pt);
                            if (alignX && clr != null && clr.Count > 0) lifterX.Add(Tuple.Create(clr[0], xVal));
                        }
                        continue;   // embeds must not define the concrete boundary
                    }
                }

                // ---------- Concrete (view geometry gives valid references) ----------
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
                                    if (pf.Area > MinFaceAreaSqFt) planes.Add(new StepPlane { Ref = pf.Reference, Z = z });
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

                                if ((rightDot > 0.95 || rightDot < -0.95) && pf.Area > MinFaceAreaSqFt)
                                    xplanes.Add(new StepPlane { Ref = pf.Reference, Z = pf.Origin.DotProduct(_right) });
                            }
                        }
                    }
                }

                // ---------- Concrete (Fine model geometry: levels the view may have simplified away) ----------
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
                                if (pf != null && pf.FaceNormal.DotProduct(_up) > 0.95 && pf.Area > MinFaceAreaSqFt)
                                    planes.Add(new StepPlane { Ref = pf.Reference, Z = pf.Origin.DotProduct(_up) });
                            }
                        }
                    }
                }
            }

            // horizontal levels strictly between bottom and top (view-derived references are listed first)
            if (h.MaxZ > double.MinValue && h.MinZ < double.MaxValue)
            {
                h.Steps = planes
                    .GroupBy(p => Math.Round(p.Z, 2))
                    .Select(g => g.First())
                    .Where(p => h.MaxZ - p.Z > StepMinGap && p.Z - h.MinZ > StepMinGap)
                    .OrderByDescending(p => p.Z)
                    .ToList();
            }

            // vertical planes strictly between left and right
            if (h.MaxX > double.MinValue && h.MinX < double.MaxValue)
            {
                h.XSteps = xplanes
                    .GroupBy(p => Math.Round(p.Z, 2))
                    .Select(g => g.First())
                    .Where(p => p.Z - h.MinX > StepMinGap && h.MaxX - p.Z > StepMinGap)
                    .OrderBy(p => p.Z)
                    .ToList();
            }

            h.DowelX = FilterDuplicates(dowelX);
            h.CmsX = FilterDuplicates(cmsX);
            h.LifterX = FilterDuplicates(lifterX);
            return h;
        }

        private void HarvestDowel(Autodesk.Revit.DB.Structure.Rebar rebar, Options viewOpts, Harvest h,
                                  List<Tuple<Reference, double>> dowelX)
        {
            Element type = _doc.GetElement(rebar.GetTypeId());
            if (type == null) return;
            string n = type.Name.ToUpperInvariant();
            if (!(n.Contains("DOWEL") || Dia25.IsMatch(n))) return;

            // centre point of every bar position (dowels are seen end-on in a section)
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
                    h.DowelPts.Add((pick.GetEndPoint(0) + pick.GetEndPoint(1)) * 0.5);
                }
            }
            catch (Exception ex) { _notes.Add("Dowel centre points skipped for one rebar: " + FirstLine(ex.Message)); }

            // vertical bar lines in the view carry valid references
            GeometryElement rv = rebar.get_Geometry(viewOpts);
            if (rv == null) return;
            foreach (GeometryObject obj in rv)
            {
                Line line = obj as Line;
                if (line != null && Math.Abs(line.Direction.DotProduct(_up)) > 0.95 && line.Reference != null)
                    dowelX.Add(Tuple.Create(line.Reference, line.Origin.DotProduct(_right)));
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
            sb.AppendLine("Standard applied: " + (_styleA ? "Section A style" : "Section B style"));
            sb.AppendLine("View: " + _view.Name + "  (scale 1:" + _scale + ")");
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

            TaskDialog td = new TaskDialog("Section Dimensioning");
            td.MainInstruction = _skipped.Count == 0 ? "Section dimensioned successfully" : "Section dimensioned with some items skipped";
            td.MainContent = sb.ToString();
            td.Show();
        }
    }
}