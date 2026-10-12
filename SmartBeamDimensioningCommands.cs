using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    public class SmartBeamDimensioningCommand : IExternalCommand
    {
        private static readonly string[] CmsKeywords = { "cms", "sleeve", "groutec" };
        private static readonly string[] LifterKeywords = { "hock", "lifting", "lifter" };
        private static readonly string[] LedgeKeywords = { "ledge" };
        private static readonly string[] CorbelKeywords = { "corbel" };
        private static readonly string[] IgnoreKeywords = { "void", "triangle", "cutout" };

        private const string AutoDimTag = "StructAuto_BeamDim";
        private static readonly Guid AutoDimSchemaGuid = new Guid("E7C0F6A1-9B3D-4E52-8B0B-6D2E4B6F3A11");
        private static Schema _autoDimSchema;

        private static Schema GetOrCreateAutoDimSchema()
        {
            if (_autoDimSchema != null) return _autoDimSchema;
            _autoDimSchema = Schema.Lookup(AutoDimSchemaGuid);
            if (_autoDimSchema == null)
            {
                SchemaBuilder sb = new SchemaBuilder(AutoDimSchemaGuid);
                sb.SetSchemaName("StructAutoBeamDimTracker");
                sb.SetReadAccessLevel(AccessLevel.Public);
                sb.SetWriteAccessLevel(AccessLevel.Public);
                sb.AddSimpleField("Tag", typeof(string));
                _autoDimSchema = sb.Finish();
            }
            return _autoDimSchema;
        }

        private static void MarkAsAutoDim(Element e)
        {
            Schema schema = GetOrCreateAutoDimSchema();
            Entity entity = new Entity(schema);
            entity.Set("Tag", AutoDimTag);
            e.SetEntity(entity);
        }

        private static bool IsAutoDim(Element e)
        {
            Schema schema = GetOrCreateAutoDimSchema();
            Entity entity = e.GetEntity(schema);
            return entity != null && entity.IsValid();
        }

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
                Reference beamRef = uidoc.Selection.PickObject(ObjectType.Element, new BeamSelectionFilter(), "Select the Precast Beam Assembly");
                Element selectedElem = doc.GetElement(beamRef);

                List<Element> elementsToProcess = new List<Element>();
                FamilyInstance mainBeam = null;

                if (selectedElem is AssemblyInstance assembly)
                {
                    foreach (ElementId memberId in assembly.GetMemberIds())
                    {
                        Element member = doc.GetElement(memberId);
                        elementsToProcess.Add(member);
                        if (member is FamilyInstance fi && fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralFraming)
                        {
                            mainBeam = fi;
                        }
                    }
                }
                else
                {
                    elementsToProcess.Add(selectedElem);
                    mainBeam = selectedElem as FamilyInstance;
                }

                if (mainBeam == null)
                {
                    TaskDialog.Show("Error", "Could not identify the main structural beam in the selection.");
                    return Result.Failed;
                }

                // 1. Get STRICT Concrete Boundaries (Uses 0.1 sq ft filter)
                GetStrictBeamExtremes(doc, activeView, mainBeam, out Reference leftFace, out Reference rightFace, out Reference topFace, out Reference bottomFace);

                if (bottomFace == null || topFace == null || leftFace == null || rightFace == null)
                {
                    TaskDialog.Show("Error", "Could not find primary concrete boundary faces. Verify the beam has flat concrete ends.");
                    return Result.Failed;
                }

                PlanarFace topPlanarFace = mainBeam.GetGeometryObjectFromReference(topFace) as PlanarFace;
                PlanarFace leftPlanarFace = mainBeam.GetGeometryObjectFromReference(leftFace) as PlanarFace;

                double beamTopZ = topPlanarFace.Origin.DotProduct(activeView.UpDirection);
                double beamBottomZ = (mainBeam.GetGeometryObjectFromReference(bottomFace) as PlanarFace).Origin.DotProduct(activeView.UpDirection);

                // 2. Harvest Embeds for Top Dimensions
                HarvestBeamEmbeds(doc, activeView, elementsToProcess,
                    out List<Reference> cmsRefs,
                    out List<Reference> lifterRefs,
                    out List<Reference> ledgeCenterRefs,
                    out List<Reference> corbelRowRefs,
                    out Reference ledgeFlushLeft,
                    out Reference ledgeFlushRight,
                    out string unmatchedFamilyNames);

                // 3. Harvest Vertical Ledges for Left Dimensions
                HarvestVerticalLedges(doc, activeView, mainBeam, beamBottomZ, beamTopZ, out List<Reference> allLedges);

                string debugWarnings = "";

                using (Transaction t = new Transaction(doc, "Smart Beam Dimensioning"))
                {
                    t.Start();

                    DeletePreviousAutoDimensions(doc, activeView);

                    HashSet<ElementId> dimensionIdsBeforeThisRun = new HashSet<ElementId>(
                        new FilteredElementCollector(doc, activeView.Id).OfClass(typeof(Dimension)).ToElementIds());

                    XYZ up = activeView.UpDirection;
                    XYZ right = activeView.RightDirection;

                    // ==========================================
                    // TOP HORIZONTAL DIMENSIONS
                    // ==========================================
                    double tierOffset = 1.5;
                    const double tierStep = 1.0;

                    CreateTopTier(doc, activeView, leftFace, rightFace, cmsRefs, topPlanarFace, up, right,
                        ref tierOffset, tierStep, "CMS 1", ref debugWarnings);

                    CreateTopTier(doc, activeView, leftFace, rightFace, lifterRefs, topPlanarFace, up, right,
                        ref tierOffset, tierStep, "L1", ref debugWarnings);

                    CreateTopTier(doc, activeView, leftFace, rightFace, ledgeCenterRefs, topPlanarFace, up, right,
                        ref tierOffset, tierStep, "LEDGE (embed)", ref debugWarnings);

                    try
                    {
                        if (ledgeFlushLeft != null && ledgeFlushRight != null)
                        {
                            ReferenceArray rArray = new ReferenceArray();
                            rArray.Append(ledgeFlushLeft);
                            rArray.Append(ledgeFlushRight);
                            XYZ origin = topPlanarFace.Origin + (up * tierOffset);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                        }
                        tierOffset += tierStep;
                    }
                    catch (Exception ex) { debugWarnings += $"- LEDGE (flush): {ex.Message}\n"; tierOffset += tierStep; }

                    try
                    {
                        if (corbelRowRefs.Count > 0)
                        {
                            ReferenceArray rArray = new ReferenceArray();
                            rArray.Append(leftFace);
                            foreach (Reference r in corbelRowRefs) rArray.Append(r);
                            rArray.Append(rightFace);
                            XYZ origin = topPlanarFace.Origin + (up * tierOffset);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                            tierOffset += tierStep;
                        }
                    }
                    catch (Exception ex) { debugWarnings += $"- CORBEL: {ex.Message}\n"; tierOffset += tierStep; }

                    try
                    {
                        ReferenceArray rArray = new ReferenceArray();
                        rArray.Append(leftFace);
                        rArray.Append(rightFace);
                        XYZ origin = topPlanarFace.Origin + (up * tierOffset);
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                    }
                    catch (Exception ex) { debugWarnings += $"- LENGTH: {ex.Message}\n"; }

                    // ==========================================
                    // LEFT VERTICAL DIMENSIONS (DEPTH)
                    // ==========================================

                    // Left Tier 1: Detailed Ledges (Dynamic Chain)
                    try
                    {
                        if (allLedges.Count > 0)
                        {
                            ReferenceArray rArray = new ReferenceArray();
                            rArray.Append(bottomFace);
                            foreach (Reference r in allLedges) rArray.Append(r);
                            rArray.Append(topFace);
                            XYZ origin = leftPlanarFace.Origin + (right * -1.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + up), rArray);
                        }
                    }
                    catch (Exception ex) { debugWarnings += $"- Left Tier 1 (Ledges): {ex.Message}\n"; }

                    // Left Tier 2: Overall Depth
                    try
                    {
                        ReferenceArray rArray = new ReferenceArray();
                        rArray.Append(bottomFace);
                        rArray.Append(topFace);
                        XYZ origin = leftPlanarFace.Origin + (right * -2.5);
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + up), rArray);
                    }
                    catch (Exception ex) { debugWarnings += $"- Left Tier 2 (Overall Depth): {ex.Message}\n"; }

                    TagNewlyCreatedDimensions(doc, activeView, dimensionIdsBeforeThisRun);

                    t.Commit();
                }

                if (!string.IsNullOrEmpty(unmatchedFamilyNames))
                {
                    debugWarnings += "\nUnmatched nested families found:\n" + unmatchedFamilyNames;
                }

                if (!string.IsNullOrEmpty(debugWarnings)) TaskDialog.Show("Warnings", "Some dimension lines could not be drawn:\n\n" + debugWarnings);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        private void CreateTopTier(Document doc, View activeView, Reference leftFace, Reference rightFace,
            List<Reference> innerRefs, PlanarFace topPlanarFace, XYZ up, XYZ right,
            ref double tierOffset, double tierStep, string tierName, ref string debugWarnings)
        {
            try
            {
                if (innerRefs.Count > 0)
                {
                    ReferenceArray rArray = new ReferenceArray();
                    rArray.Append(leftFace);
                    foreach (Reference r in innerRefs) rArray.Append(r);
                    rArray.Append(rightFace);
                    XYZ origin = topPlanarFace.Origin + (up * tierOffset);
                    doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                }
            }
            catch (Exception ex) { debugWarnings += $"- {tierName}: {ex.Message}\n"; }
            finally { tierOffset += tierStep; }
        }

        private void DeletePreviousAutoDimensions(Document doc, View activeView)
        {
            var toDelete = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(Dimension))
                .Where(d => IsAutoDim(d))
                .Select(d => d.Id)
                .ToList();

            if (toDelete.Count > 0) doc.Delete(toDelete);
        }

        private void TagNewlyCreatedDimensions(Document doc, View activeView, HashSet<ElementId> dimensionIdsBeforeThisRun)
        {
            var allDimsNow = new FilteredElementCollector(doc, activeView.Id).OfClass(typeof(Dimension)).ToElementIds();
            foreach (ElementId id in allDimsNow)
            {
                if (!dimensionIdsBeforeThisRun.Contains(id))
                {
                    Element dim = doc.GetElement(id);
                    if (dim != null) MarkAsAutoDim(dim);
                }
            }
        }

       private void GetStrictBeamExtremes(Document doc, View activeView, FamilyInstance beam, out Reference left, out Reference right, out Reference top, out Reference bottom)
        {
            left = null; right = null; top = null; bottom = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };

            GeometryElement geomElem = beam.get_Geometry(geomOptions);
            if (geomElem == null) return;

            XYZ viewUp = activeView.UpDirection;
            XYZ viewRight = activeView.RightDirection;

            Solid mainConcrete = null;
            double maxVolume = 0;

            // 1. Find the single largest solid (Mathematically guaranteed to be the main concrete body)
            foreach (GeometryObject geomObj in geomElem)
            {
                IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();

                foreach (GeometryObject obj in objectsToCheck)
                {
                    if (obj is Solid solid && solid.Volume > maxVolume)
                    {
                        maxVolume = solid.Volume;
                        mainConcrete = solid;
                    }
                }
            }

            if (mainConcrete == null) return;

            double maxZ = double.MinValue; double minZ = double.MaxValue;
            double maxX = double.MinValue; double minX = double.MaxValue;

            // 2. Only extract faces from the main concrete body (100% ignores rebar and embeds)
            foreach (Face face in mainConcrete.Faces)
            {
                if (face is PlanarFace pf) 
                {
                    double upDot = pf.FaceNormal.DotProduct(viewUp);
                    double rightDot = pf.FaceNormal.DotProduct(viewRight);

                    if (upDot > 0.95) { double z = pf.Origin.DotProduct(viewUp); if (z > maxZ) { maxZ = z; top = pf.Reference; } }
                    else if (upDot < -0.95) { double z = pf.Origin.DotProduct(viewUp); if (z < minZ) { minZ = z; bottom = pf.Reference; } }
                    else if (rightDot > 0.95) { double x = pf.Origin.DotProduct(viewRight); if (x > maxX) { maxX = x; right = pf.Reference; } }
                    else if (rightDot < -0.95) { double x = pf.Origin.DotProduct(viewRight); if (x < minX) { minX = x; left = pf.Reference; } }
                }
            }
        }

        private void HarvestVerticalLedges(Document doc, View activeView, FamilyInstance beam, double colBottomZ, double colTopZ, out List<Reference> allLedges)
        {
            allLedges = new List<Reference>();
            List<Tuple<Reference, double>> tempPlanes = new List<Tuple<Reference, double>>();

            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = beam.get_Geometry(geomOptions);
            if (geomElem == null) return;

            XYZ viewUp = activeView.UpDirection;

            foreach (GeometryObject geomObj in geomElem)
            {
                IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();

                foreach (GeometryObject obj in objectsToCheck)
                {
                    if (obj is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace pf && pf.Area > 0.1) 
                            {
                                if (Math.Abs(pf.FaceNormal.DotProduct(viewUp)) > 0.95)
                                {
                                    double z = pf.Origin.DotProduct(viewUp);
                                    if (z < (colTopZ - 0.05) && z > (colBottomZ + 0.05))
                                    {
                                        tempPlanes.Add(new Tuple<Reference, double>(pf.Reference, z));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            var sortedPlanes = tempPlanes.OrderBy(p => p.Item2).ToList();
            double lastZ = -9999;
            foreach (var plane in sortedPlanes)
            {
                if (Math.Abs(plane.Item2 - lastZ) > 0.05) 
                {
                    allLedges.Add(plane.Item1);
                    lastZ = plane.Item2;
                }
            }
        }

       private void GetInstanceExtremeFaces(FamilyInstance inst, View activeView, out Reference leftRef, out Reference rightRef, out double leftX, out double rightX)
        {
            leftRef = null; rightRef = null;
            leftX = double.MaxValue; rightX = double.MinValue;
            XYZ viewRight = activeView.RightDirection;

            // Approximate X coordinates using BoundingBox for left-to-right sorting
            BoundingBoxXYZ bbox = inst.get_BoundingBox(activeView) ?? inst.get_BoundingBox(null);
            if (bbox != null)
            {
                leftX = bbox.Min.DotProduct(viewRight);
                rightX = bbox.Max.DotProduct(viewRight);
            }

            // 1. Try Built-in Left/Right Reference Planes first (Most reliable for Revit API)
            IList<Reference> lRefs = inst.GetReferences(FamilyInstanceReferenceType.Left);
            if (lRefs != null && lRefs.Count > 0) leftRef = lRefs[0];

            IList<Reference> rRefs = inst.GetReferences(FamilyInstanceReferenceType.Right);
            if (rRefs != null && rRefs.Count > 0) rightRef = rRefs[0];

            // 2. Fallback to scraping Geometry (If family lacks reference planes)
            if (leftRef == null || rightRef == null)
            {
                Options geomOptions = new Options { ComputeReferences = true, View = activeView, IncludeNonVisibleObjects = true };
                GeometryElement geomElem = inst.get_Geometry(geomOptions);
                if (geomElem != null)
                {
                    foreach (GeometryObject geomObj in geomElem)
                    {
                        IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                        if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();

                        foreach (GeometryObject obj in objectsToCheck)
                        {
                            if (obj is Solid solid)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    if (face is PlanarFace pf && Math.Abs(pf.FaceNormal.DotProduct(viewRight)) > 0.95)
                                    {
                                        double dot = pf.FaceNormal.DotProduct(viewRight);
                                        if (leftRef == null && dot < -0.95) leftRef = pf.Reference;
                                        if (rightRef == null && dot > 0.95) rightRef = pf.Reference;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private Reference GetBestCenterReference(FamilyInstance inst, View activeView)
        {
            Reference validRef = null;
            try { validRef = inst.GetReferenceByName("DimRef"); } catch { }
            if (validRef != null) return validRef;

            FamilyInstanceReferenceType[] refTypes = {
                FamilyInstanceReferenceType.CenterLeftRight,
                FamilyInstanceReferenceType.CenterFrontBack,
                FamilyInstanceReferenceType.CenterElevation
            };
            foreach (var rt in refTypes)
            {
                IList<Reference> refs = inst.GetReferences(rt);
                if (refs != null && refs.Count > 0) return refs[0];
            }

            GetInstanceExtremeFaces(inst, activeView, out Reference l, out Reference r, out _, out _);
            if (l != null) return l;
            if (r != null) return r;

            return null;
        }

        private void HarvestBeamEmbeds(Document doc, View activeView, List<Element> elementsToProcess,
            out List<Reference> cmsRefs, out List<Reference> lifterRefs,
            out List<Reference> ledgeCenterRefs, out List<Reference> corbelRowRefs,
            out Reference ledgeFlushLeft, out Reference ledgeFlushRight,
            out string unmatchedFamilyNames)
        {
            cmsRefs = new List<Reference>();
            lifterRefs = new List<Reference>();
            ledgeCenterRefs = new List<Reference>();
            corbelRowRefs = new List<Reference>();
            ledgeFlushLeft = null;
            ledgeFlushRight = null;

            List<Tuple<Reference, double>> tempCms = new List<Tuple<Reference, double>>();
            List<Tuple<Reference, double>> tempLifters = new List<Tuple<Reference, double>>();
            List<Tuple<Reference, double>> tempLedgeCenters = new List<Tuple<Reference, double>>();
            List<Tuple<Reference, double>> tempCorbelBreaks = new List<Tuple<Reference, double>>();

            double flushMinX = double.MaxValue, flushMaxX = double.MinValue;
            var unmatched = new HashSet<string>();

            XYZ viewRight = activeView.RightDirection;

            foreach (Element elem in elementsToProcess)
            {
                if (elem is FamilyInstance inst && inst.Category.Id.Value != (long)BuiltInCategory.OST_StructuralFraming)
                {
                    string famName = inst.Symbol.FamilyName.ToLower();

                    bool isCms = CmsKeywords.Any(k => famName.Contains(k));
                    bool isLifter = LifterKeywords.Any(k => famName.Contains(k));
                    bool isLedge = LedgeKeywords.Any(k => famName.Contains(k));
                    bool isCorbel = CorbelKeywords.Any(k => famName.Contains(k));
                    bool isIgnored = IgnoreKeywords.Any(k => famName.Contains(k));

                    if (isIgnored) continue; 

                    if (!isCms && !isLifter && !isLedge && !isCorbel)
                    {
                        unmatched.Add(inst.Symbol.FamilyName);
                        continue;
                    }

                    BoundingBoxXYZ bbox = inst.get_BoundingBox(activeView) ?? inst.get_BoundingBox(null);
                    double refX = bbox != null ? ((bbox.Max + bbox.Min) / 2.0).DotProduct(viewRight) : 0;

                    if (isCms || isLifter || isLedge)
                    {
                        Reference centerRef = GetBestCenterReference(inst, activeView);
                        if (centerRef != null)
                        {
                            if (isCms) tempCms.Add(Tuple.Create(centerRef, refX));
                            else if (isLifter) tempLifters.Add(Tuple.Create(centerRef, refX));
                            else if (isLedge) tempLedgeCenters.Add(Tuple.Create(centerRef, refX));
                        }
                    }

                    if (isCorbel)
                    {
                        GetInstanceExtremeFaces(inst, activeView, out Reference cLeft, out Reference cRight, out double cLeftX, out double cRightX);
                        if (cLeft != null) tempCorbelBreaks.Add(Tuple.Create(cLeft, cLeftX));
                        if (cRight != null) tempCorbelBreaks.Add(Tuple.Create(cRight, cRightX));
                    }

                    if (isLedge || isCorbel)
                    {
                        GetInstanceExtremeFaces(inst, activeView, out Reference exLeft, out Reference exRight, out double exLeftX, out double exRightX);
                        if (exLeft != null && exLeftX < flushMinX) { flushMinX = exLeftX; ledgeFlushLeft = exLeft; }
                        if (exRight != null && exRightX > flushMaxX) { flushMaxX = exRightX; ledgeFlushRight = exRight; }
                    }
                }
            }

            FilterAndSortHorizontally(tempCms, cmsRefs);
            FilterAndSortHorizontally(tempLifters, lifterRefs);
            FilterAndSortHorizontally(tempLedgeCenters, ledgeCenterRefs);

            var combined = new List<Tuple<Reference, double>>();
            combined.AddRange(tempLedgeCenters);
            combined.AddRange(tempCorbelBreaks);
            FilterAndSortHorizontally(combined, corbelRowRefs);

            unmatchedFamilyNames = unmatched.Count > 0 ? string.Join("\n", unmatched.Select(n => "- " + n)) : "";
        }

        private void FilterAndSortHorizontally(List<Tuple<Reference, double>> input, List<Reference> output)
        {
            if (input.Count == 0) return;
            var sorted = input.OrderBy(x => x.Item2).ToList();
            double lastX = -9999;
            foreach (var item in sorted)
            {
                if (Math.Abs(item.Item2 - lastX) > 0.05)
                {
                    output.Add(item.Item1);
                    lastX = item.Item2;
                }
            }
        }
    }

    public class BeamSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FamilyInstance || elem is AssemblyInstance;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}