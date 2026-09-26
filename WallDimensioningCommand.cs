using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    public class WallDimensioningCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            if (activeView.ViewType != ViewType.Elevation && activeView.ViewType != ViewType.Section && activeView.ViewType != ViewType.Detail)
            {
                TaskDialog.Show("Error", "Please run this command directly inside your manually created 2D Elevation or Detail view.");
                return Result.Failed;
            }

            try
            {
                Reference wallRef = uidoc.Selection.PickObject(ObjectType.Element, "Select the Precast Beam/Wall Assembly");
                Element selectedWall = doc.GetElement(wallRef);

                if (!(selectedWall is AssemblyInstance) && !(selectedWall is FamilyInstance))
                {
                    TaskDialog.Show("Error", "Selected element is not an Assembly or Family Instance.");
                    return Result.Failed;
                }

                GetWallExtremesAndEmbeds(doc, activeView, selectedWall, 
                    out Reference topRef, out Reference bottomRef, out Reference leftRef, out Reference rightRef,
                    out List<Reference> cmsRefs, out List<Reference> lifterRefs, out List<Reference> dowelRefs);

                if (topRef == null || leftRef == null || rightRef == null)
                {
                    TaskDialog.Show("Error", "Could not find the primary concrete boundaries in this view.");
                    return Result.Failed;
                }

                using (Transaction t = new Transaction(doc, "Multi-Tier Smart Dimensioning"))
                {
                    t.Start();

                    Element topFaceElem = doc.GetElement(topRef.ElementId);
                    PlanarFace topFace = topFaceElem.GetGeometryObjectFromReference(topRef) as PlanarFace;
                    XYZ up = activeView.UpDirection;
                    XYZ right = activeView.RightDirection;

                    // Tier 1 (Lowest): CMS / Sleeves
                    if (cmsRefs.Count > 0)
                    {
                        ReferenceArray rArray = new ReferenceArray();
                        rArray.Append(leftRef);
                        foreach (Reference r in cmsRefs) rArray.Append(r);
                        rArray.Append(rightRef);
                        
                        XYZ origin = topFace.Origin + (up * 1.2); 
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                    }

                    // Tier 2: Lifters
                    if (lifterRefs.Count > 0)
                    {
                        ReferenceArray rArray = new ReferenceArray();
                        rArray.Append(leftRef);
                        foreach (Reference r in lifterRefs) rArray.Append(r);
                        rArray.Append(rightRef);
                        
                        XYZ origin = topFace.Origin + (up * 2.0); 
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                    }
                    
                    // Tier 3 & Tier 5: Overall Lengths
                    ReferenceArray overallRefs = new ReferenceArray();
                    overallRefs.Append(leftRef);
                    overallRefs.Append(rightRef);
                    
                    XYZ overallOrigin1 = topFace.Origin + (up * 2.8); // Tier 3: Ledge Overall
                    XYZ overallOrigin2 = topFace.Origin + (up * 4.4); // Tier 5: Top Total Length
                    
                    doc.Create.NewDimension(activeView, Line.CreateBound(overallOrigin1, overallOrigin1 + right), overallRefs);
                    doc.Create.NewDimension(activeView, Line.CreateBound(overallOrigin2, overallOrigin2 + right), overallRefs);

                    // Tier 4: Dowel Bars (Slotted between the two overall dimensions)
                    if (dowelRefs.Count > 0)
                    {
                        ReferenceArray rArray = new ReferenceArray();
                        rArray.Append(leftRef);
                        foreach (Reference r in dowelRefs) rArray.Append(r);
                        rArray.Append(rightRef);
                        
                        XYZ origin = topFace.Origin + (up * 3.6); 
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin, origin + right), rArray);
                    }

                    t.Commit();
                }

                TaskDialog.Show("Success", $"Dimensions placed! Successfully isolated {dowelRefs.Count} H25 Dowel bars.");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        private void GetWallExtremesAndEmbeds(Document doc, View activeView, Element wallElement, 
            out Reference top, out Reference bottom, out Reference left, out Reference right,
            out List<Reference> cmsRefs, out List<Reference> lifterRefs, out List<Reference> dowelRefs)
        {
            top = null; bottom = null; left = null; right = null;
            cmsRefs = new List<Reference>();
            lifterRefs = new List<Reference>();
            dowelRefs = new List<Reference>();
            
            List<Tuple<Reference, double>> tempDowelRefs = new List<Tuple<Reference, double>>();
            
            List<Element> elementsToProcess = new List<Element>();
            if (wallElement is AssemblyInstance assembly)
            {
                foreach (ElementId memberId in assembly.GetMemberIds()) elementsToProcess.Add(doc.GetElement(memberId));
            }
            else elementsToProcess.Add(wallElement);

            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            XYZ viewUp = activeView.UpDirection;
            XYZ viewRight = activeView.RightDirection;

            double maxZ = double.MinValue; double minZ = double.MaxValue;
            double maxX = double.MinValue; double minX = double.MaxValue;

            foreach (Element elem in elementsToProcess)
            {
                if (elem is Rebar rebar)
                {
                    // FIX: Isolate only the specific dowel bars, ignoring the standard cage mesh
                    Element rebarType = doc.GetElement(rebar.GetTypeId());
                    if (rebarType != null && (rebarType.Name.Contains("25") || rebarType.Name.ToUpper().Contains("DOWEL")))
                    {
                        GeometryElement rebarGeom = rebar.get_Geometry(geomOptions);
                        if (rebarGeom != null)
                        {
                            foreach (GeometryObject obj in rebarGeom)
                            {
                                if (obj is Line line && Math.Abs(line.Direction.DotProduct(viewUp)) > 0.95)
                                {
                                    if (line.Reference != null) 
                                    {
                                        tempDowelRefs.Add(new Tuple<Reference, double>(line.Reference, line.Origin.DotProduct(viewRight)));
                                    }
                                }
                            }
                        }
                    }
                    continue; 
                }

                if (elem is FamilyInstance fi)
                {
                    string famName = fi.Symbol.FamilyName.ToUpper();
                    IList<Reference> centerLeftRight = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                    
                    if (centerLeftRight != null && centerLeftRight.Count > 0)
                    {
                        if (famName.Contains("CMS") || famName.Contains("SLEEVE") || famName.Contains("GROUTEC")) 
                            cmsRefs.Add(centerLeftRight[0]);
                        else if (famName.Contains("LIFTING") || famName.Contains("LIFTER") || famName.Contains("L1")) 
                            lifterRefs.Add(centerLeftRight[0]);
                    }
                }

                GeometryElement geomElem = elem.get_Geometry(geomOptions);
                if (geomElem == null) continue;

                foreach (GeometryObject geomObj in geomElem)
                {
                    List<Solid> solids = new List<Solid>();
                    if (geomObj is Solid s) solids.Add(s);
                    else if (geomObj is GeometryInstance geomInst)
                    {
                        foreach (GeometryObject instObj in geomInst.GetInstanceGeometry())
                            if (instObj is Solid instSolid) solids.Add(instSolid);
                    }

                    foreach (Solid solid in solids)
                    {
                        if (solid.Faces.Size == 0) continue;
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace planarFace)
                            {
                                double upDot = planarFace.FaceNormal.DotProduct(viewUp);
                                double rightDot = planarFace.FaceNormal.DotProduct(viewRight);

                                if (upDot > 0.95) { double z = planarFace.Origin.DotProduct(viewUp); if (z > maxZ) { maxZ = z; top = planarFace.Reference; } }
                                else if (upDot < -0.95) { double z = planarFace.Origin.DotProduct(viewUp); if (z < minZ) { minZ = z; bottom = planarFace.Reference; } }
                                else if (rightDot > 0.95) { double x = planarFace.Origin.DotProduct(viewRight); if (x > maxX) { maxX = x; right = planarFace.Reference; } }
                                else if (rightDot < -0.95) { double x = planarFace.Origin.DotProduct(viewRight); if (x < minX) { minX = x; left = planarFace.Reference; } }
                            }
                        }
                    }
                }
            }
            
            tempDowelRefs = tempDowelRefs.OrderBy(t => t.Item2).ToList();

            double lastX = double.MinValue;
            foreach (var tuple in tempDowelRefs)
            {
                if (tuple.Item2 - lastX > 0.15) 
                {
                    dowelRefs.Add(tuple.Item1);
                    lastX = tuple.Item2;
                }
            }
        }
    }
}
