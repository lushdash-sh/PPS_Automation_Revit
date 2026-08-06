using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing
{
    [Transaction(TransactionMode.Manual)]
    public class SmartDimensioningCommand : IExternalCommand
    {
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
                Reference colRef = uidoc.Selection.PickObject(ObjectType.Element, new ColumnSelectionFilter(), "Select the Precast Column");
                FamilyInstance column = doc.GetElement(colRef) as FamilyInstance;

                // 1. Harvest Concrete Faces 
                Reference[] sideFaces = GetLeftAndRightColumnFaces(doc, activeView, column);
                Reference bottomFace = GetLowestColumnFace(doc, activeView, column);
                Reference topFace = GetHighestColumnFace(doc, activeView, column);

                if (bottomFace == null || topFace == null || sideFaces[0] == null || sideFaces[1] == null)
                {
                    TaskDialog.Show("Debug Error", "Could not find primary concrete faces. Cannot dimension.");
                    return Result.Failed;
                }

                // 2. Harvest Embeds (Now with Geometric Fallback!)
                List<Reference> anchorCenterRefs, corbelRefs;
                Reference extremeBottomDowel;
                
                HarvestEmbedData(doc, activeView, column, out anchorCenterRefs, out corbelRefs, out extremeBottomDowel);

                using (Transaction t = new Transaction(doc, "Smart Dimensioning"))
                {
                    t.Start();
                    PlanarFace topPlanarFace = column.GetGeometryObjectFromReference(topFace) as PlanarFace;
                    PlanarFace leftPlanarFace = column.GetGeometryObjectFromReference(sideFaces[0]) as PlanarFace;

                    // ==========================================
                    // TOP HORIZONTAL DIMENSIONS
                    // ==========================================
                    Reference centerRef = null;
                    IList<Reference> centerRefs = column.GetReferences(FamilyInstanceReferenceType.CenterLeftRight);
                    if (centerRefs != null && centerRefs.Count > 0) centerRef = centerRefs[0];

                    try 
                    {
                        if (centerRef != null)
                        {
                            ReferenceArray topPartialRefs = new ReferenceArray();
                            topPartialRefs.Append(sideFaces[0]); 
                            topPartialRefs.Append(centerRef);    
                            topPartialRefs.Append(sideFaces[1]); 
                            XYZ partialDimOrigin = topPlanarFace.Origin + (activeView.UpDirection * 7.0);
                            doc.Create.NewDimension(activeView, Line.CreateBound(partialDimOrigin, partialDimOrigin + activeView.RightDirection), topPartialRefs);
                        }
                    } catch { }

                    try 
                    {
                        ReferenceArray topOverallRefs = new ReferenceArray();
                        topOverallRefs.Append(sideFaces[0]); 
                        topOverallRefs.Append(sideFaces[1]); 
                        XYZ overallDimOrigin = topPlanarFace.Origin + (activeView.UpDirection * 8.0);
                        doc.Create.NewDimension(activeView, Line.CreateBound(overallDimOrigin, overallDimOrigin + activeView.RightDirection), topOverallRefs);
                    } catch { }

                    // ==========================================
                    // LEFT VERTICAL DIMENSIONS (4 TIERS)
                    // ==========================================
                    
                    // TIER 1: L2 (All Anchors & Corbels) - Offset -1.5
                    try 
                    {
                        if (anchorCenterRefs.Count > 0)
                        {
                            ReferenceArray r1 = new ReferenceArray();
                            r1.Append(bottomFace);
                            foreach (Reference r in anchorCenterRefs) r1.Append(r);
                            r1.Append(topFace);
                            
                            XYZ origin1 = leftPlanarFace.Origin + (activeView.RightDirection * -1.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin1, origin1 + activeView.UpDirection), r1);
                        }
                    } catch { }

                    // TIER 2: Corbels Only - Offset -2.5
                    try 
                    {
                        if (corbelRefs.Count > 0)
                        {
                            ReferenceArray r2 = new ReferenceArray();
                            r2.Append(bottomFace);
                            foreach (Reference r in corbelRefs) r2.Append(r);
                            r2.Append(topFace);
                            
                            XYZ origin2 = leftPlanarFace.Origin + (activeView.RightDirection * -2.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin2, origin2 + activeView.UpDirection), r2);
                        }
                    } catch { }

                    // TIER 3: Overall Concrete (HEIGHT 4225) - Offset -3.5
                    try 
                    {
                        ReferenceArray r3 = new ReferenceArray();
                        r3.Append(bottomFace);
                        r3.Append(topFace);
                        
                        XYZ origin3 = leftPlanarFace.Origin + (activeView.RightDirection * -3.5);
                        doc.Create.NewDimension(activeView, Line.CreateBound(origin3, origin3 + activeView.UpDirection), r3);
                    } catch { }

                    // TIER 4: Outer Extremes (Dowel Tube 4250) - Offset -4.5
                    try 
                    {
                        if (extremeBottomDowel != null)
                        {
                            ReferenceArray r4 = new ReferenceArray();
                            r4.Append(extremeBottomDowel); // Bottom tip of dowel tube
                            r4.Append(topFace);            // Top of concrete
                            
                            XYZ origin4 = leftPlanarFace.Origin + (activeView.RightDirection * -4.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin4, origin4 + activeView.UpDirection), r4);
                        }
                    } catch { }

                    t.Commit();
                }

                TaskDialog.Show("Success", $"Dimensions placed! Found {corbelRefs.Count} corbels and {anchorCenterRefs.Count} total embeds.");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex)
            {
                TaskDialog.Show("Revit API Error", ex.Message);
                return Result.Failed;
            }
        }

        // --- HARVESTER 1: LEFT & RIGHT FACES ---
        private Reference[] GetLeftAndRightColumnFaces(Document doc, View activeView, FamilyInstance column)
        {
            Reference leftRef = null; Reference rightRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return new Reference[] { null, null };

            XYZ viewRight = activeView.RightDirection;
            double maxLeftArea = -1.0; double maxRightArea = -1.0;

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
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace)
                        {
                            double dot = planarFace.FaceNormal.DotProduct(viewRight);
                            if (dot < -0.95 && planarFace.Area > maxLeftArea) { maxLeftArea = planarFace.Area; leftRef = planarFace.Reference; }
                            else if (dot > 0.95 && planarFace.Area > maxRightArea) { maxRightArea = planarFace.Area; rightRef = planarFace.Reference; }
                        }
                    }
                }
            }
            return new Reference[] { leftRef, rightRef };
        }

        // --- HARVESTER 2: LOWEST FACE ---
        private Reference GetLowestColumnFace(Document doc, View activeView, FamilyInstance column)
        {
            Reference bottomRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return null;

            XYZ viewUp = activeView.UpDirection;
            double minElevation = double.MaxValue;

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
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.95)
                        {
                            double elevation = planarFace.Origin.DotProduct(viewUp);
                            if (elevation < minElevation) { minElevation = elevation; bottomRef = planarFace.Reference; }
                        }
                    }
                }
            }
            return bottomRef;
        }

        // --- HARVESTER 3: HIGHEST FACE ---
        private Reference GetHighestColumnFace(Document doc, View activeView, FamilyInstance column)
        {
            Reference topRef = null;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };
            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return null;

            XYZ viewUp = activeView.UpDirection;
            double maxElevation = double.MinValue;

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
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.95)
                        {
                            double elevation = planarFace.Origin.DotProduct(viewUp);
                            if (elevation > maxElevation) { maxElevation = elevation; topRef = planarFace.Reference; }
                        }
                    }
                }
            }
            return topRef;
        }

        // --- HARVESTER 4: THE HYBRID EMBED EXTRACTOR ---
        private void HarvestEmbedData(Document doc, View activeView, FamilyInstance column, 
            out List<Reference> anchorCenterRefs, out List<Reference> corbelRefs, out Reference extremeBottomDowel)
        {
            anchorCenterRefs = new List<Reference>();
            corbelRefs = new List<Reference>();
            extremeBottomDowel = null;

            FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType();

            XYZ viewUp = activeView.UpDirection;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView };

            foreach (FamilyInstance inst in collector)
            {
                if (inst.Id == column.Id) continue; 

                string famName = inst.Symbol.FamilyName.ToLower();

                // Identify exactly what this element is based on your properties panel!
                bool isCorbel = famName.Contains("corbel");
                bool isBottomDowel = famName.Contains("dowel tube");
                bool isLiftingHook = famName.Contains("hock") || famName.Contains("lifting");

                Reference targetCenterRef = null;
                Reference targetBottomRef = null;

                // ATTEMPT 1: Try to grab the built-in reference planes first
                IList<Reference> centerRefs = inst.GetReferences(FamilyInstanceReferenceType.CenterElevation);
                if (centerRefs != null && centerRefs.Count > 0) targetCenterRef = centerRefs[0];

                IList<Reference> bottomRefs = inst.GetReferences(FamilyInstanceReferenceType.Bottom);
                if (bottomRefs != null && bottomRefs.Count > 0) targetBottomRef = bottomRefs[0];

                // ATTEMPT 2: The Geometric Fallback (If the family wasn't built with reference planes)
                if (targetCenterRef == null || (isBottomDowel && targetBottomRef == null))
                {
                    GeometryElement geomElem = inst.get_Geometry(geomOptions);
                    if (geomElem != null)
                    {
                        Reference highestFlatFace = null;
                        Reference lowestFlatFace = null;
                        double maxZ = double.MinValue;
                        double minZ = double.MaxValue;

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
                                foreach (Face face in solid.Faces)
                                {
                                    if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                    {
                                        double z = planarFace.Origin.DotProduct(viewUp);
                                        // Track the absolute top shelf and bottom face of the embed
                                        if (z > maxZ) { maxZ = z; highestFlatFace = planarFace.Reference; }
                                        if (z < minZ) { minZ = z; lowestFlatFace = planarFace.Reference; }
                                    }
                                }
                            }
                        }
                        
                        // If no center plane exists, snap to the top shelf of the corbel/anchor
                        if (targetCenterRef == null) targetCenterRef = highestFlatFace; 
                        
                        // If no bottom plane exists, snap to the absolute lowest face of the tube
                        if (targetBottomRef == null && isBottomDowel) targetBottomRef = lowestFlatFace; 
                    }
                }

                // Sort the found references into their respective Dimension Lines
                if (targetCenterRef != null)
                {
                    if (isLiftingHook || isCorbel) anchorCenterRefs.Add(targetCenterRef);
                    if (isCorbel) corbelRefs.Add(targetCenterRef);
                }
                
                if (isBottomDowel && targetBottomRef != null)
                {
                    extremeBottomDowel = targetBottomRef;
                }
            }
        }
    }

    public class ColumnSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is FamilyInstance;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}