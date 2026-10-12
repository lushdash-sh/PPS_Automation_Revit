using System;
using System.Collections.Generic;
using System.Linq;
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
                Reference colRef = uidoc.Selection.PickObject(ObjectType.Element, new ColumnSelectionFilter(), "Select the Precast Column Assembly");
                Element selectedElem = doc.GetElement(colRef);

                List<Element> elementsToProcess = new List<Element>();
                FamilyInstance mainColumn = null;

                // FIXED: Restored Assembly Isolation Logic to fix Argument 3 mismatch error
                if (selectedElem is AssemblyInstance assembly)
                {
                    foreach (ElementId memberId in assembly.GetMemberIds())
                    {
                        Element member = doc.GetElement(memberId);
                        elementsToProcess.Add(member);
                        // FIXED: Replaced IntegerValue with Value for Revit 2024+
                        if (member is FamilyInstance fi && fi.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
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

                Reference[] sideFaces = GetLeftAndRightColumnFaces(doc, activeView, mainColumn);
                Reference bottomFace = GetLowestColumnFace(doc, activeView, mainColumn);
                Reference topFace = GetHighestColumnFace(doc, activeView, mainColumn);

                if (bottomFace == null || topFace == null || sideFaces[0] == null || sideFaces[1] == null)
                {
                    TaskDialog.Show("Error", "Could not find primary concrete faces.");
                    return Result.Failed;
                }

                PlanarFace topPlanarFace = mainColumn.GetGeometryObjectFromReference(topFace) as PlanarFace;
                PlanarFace leftPlanarFace = mainColumn.GetGeometryObjectFromReference(sideFaces[0]) as PlanarFace;

                double colTopZ = topPlanarFace.Origin.DotProduct(activeView.UpDirection);
                double colBottomZ = (mainColumn.GetGeometryObjectFromReference(bottomFace) as PlanarFace).Origin.DotProduct(activeView.UpDirection);

                XYZ viewDir = activeView.ViewDirection;
                XYZ colRight = mainColumn.HandOrientation; 
                XYZ colForward = mainColumn.FacingOrientation; 
                
                double dotX = Math.Abs(activeView.RightDirection.DotProduct(colRight));
                double dotY = Math.Abs(activeView.RightDirection.DotProduct(colForward));
                
                bool isSideView = Math.Abs(viewDir.X) > 0.5;
                FamilyInstanceReferenceType topCenterRefType = dotX > dotY ? FamilyInstanceReferenceType.CenterLeftRight : FamilyInstanceReferenceType.CenterFrontBack;

                string debugWarnings = "";

                using (Transaction t = new Transaction(doc, "Smart Dimensioning"))
                {
                    t.Start();
                    
                    Reference centerRef = null;
                    IList<Reference> centerRefs = mainColumn.GetReferences(topCenterRefType);
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
                    } catch (Exception ex) { debugWarnings += $"- Top Partial: {ex.Message}\n"; }

                    try 
                    {
                        ReferenceArray topOverallRefs = new ReferenceArray();
                        topOverallRefs.Append(sideFaces[0]); 
                        topOverallRefs.Append(sideFaces[1]); 
                        XYZ overallDimOrigin = topPlanarFace.Origin + (activeView.UpDirection * 8.5);
                        doc.Create.NewDimension(activeView, Line.CreateBound(overallDimOrigin, overallDimOrigin + activeView.RightDirection), topOverallRefs);
                    } catch (Exception ex) { debugWarnings += $"- Top Overall: {ex.Message}\n"; }

                    if (!isSideView)
                    {
                        List<Reference> topHookRef, lowerHookRefs, corbelRefs;
                        Reference extremeBottomDowel;
                        HarvestBackViewEmbeds(doc, activeView, elementsToProcess, colBottomZ, colTopZ, out topHookRef, out lowerHookRefs, out corbelRefs, out extremeBottomDowel);

                        try 
                        {
                            ReferenceArray r1 = new ReferenceArray();
                            r1.Append(bottomFace);
                            foreach (Reference r in corbelRefs) r1.Append(r);
                            foreach (Reference r in topHookRef) r1.Append(r);
                            r1.Append(topFace);
                            if (r1.Size > 2)
                            {
                                XYZ origin1 = leftPlanarFace.Origin + (activeView.RightDirection * -1.5);
                                doc.Create.NewDimension(activeView, Line.CreateBound(origin1, origin1 + activeView.UpDirection), r1);
                            }
                        } catch (Exception ex) { debugWarnings += $"- Back Tier 1: {ex.Message}\n"; }

                        try 
                        {
                            if (lowerHookRefs.Count > 0)
                            {
                                ReferenceArray r2 = new ReferenceArray();
                                r2.Append(bottomFace);
                                foreach (Reference r in lowerHookRefs) r2.Append(r);
                                r2.Append(topFace);
                                
                                XYZ origin2 = leftPlanarFace.Origin + (activeView.RightDirection * -2.5);
                                doc.Create.NewDimension(activeView, Line.CreateBound(origin2, origin2 + activeView.UpDirection), r2);
                            }
                        } catch (Exception ex) { debugWarnings += $"- Back Tier 2: {ex.Message}\n"; }

                        try 
                        {
                            ReferenceArray r3 = new ReferenceArray();
                            r3.Append(bottomFace);
                            r3.Append(topFace);
                            XYZ origin3 = leftPlanarFace.Origin + (activeView.RightDirection * -3.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin3, origin3 + activeView.UpDirection), r3);
                        } catch (Exception ex) { debugWarnings += $"- Back Tier 3: {ex.Message}\n"; }

                        try 
                        {
                            if (extremeBottomDowel != null)
                            {
                                ReferenceArray r4 = new ReferenceArray();
                                r4.Append(extremeBottomDowel); 
                                r4.Append(topFace);            
                                XYZ origin4 = leftPlanarFace.Origin + (activeView.RightDirection * -4.5);
                                doc.Create.NewDimension(activeView, Line.CreateBound(origin4, origin4 + activeView.UpDirection), r4);
                            }
                        } catch (Exception ex) { debugWarnings += $"- Back Tier 4: {ex.Message}\n"; }
                    }
                    else
                    {
                        List<Reference> rebarZoneRefs;
                        Reference extremeBottomDowel;
                        HarvestRightViewZones(doc, activeView, elementsToProcess, colBottomZ, colTopZ, out rebarZoneRefs, out extremeBottomDowel);

                        try 
                        {
                            if (extremeBottomDowel != null && rebarZoneRefs.Count > 0)
                            {
                                ReferenceArray r1 = new ReferenceArray();
                                r1.Append(extremeBottomDowel);
                                foreach (Reference r in rebarZoneRefs) r1.Append(r);
                                r1.Append(topFace);
                                
                                XYZ origin1 = leftPlanarFace.Origin + (activeView.RightDirection * -1.5);
                                doc.Create.NewDimension(activeView, Line.CreateBound(origin1, origin1 + activeView.UpDirection), r1);
                            }
                        } catch (Exception ex) { debugWarnings += $"- Right Tier 1 (Rebar): {ex.Message}\n"; }

                        try 
                        {
                            ReferenceArray r2 = new ReferenceArray();
                            r2.Append(bottomFace);
                            r2.Append(topFace);
                            XYZ origin2 = leftPlanarFace.Origin + (activeView.RightDirection * -2.5);
                            doc.Create.NewDimension(activeView, Line.CreateBound(origin2, origin2 + activeView.UpDirection), r2);
                        } catch (Exception ex) { debugWarnings += $"- Right Tier 2: {ex.Message}\n"; }

                        try 
                        {
                            if (extremeBottomDowel != null)
                            {
                                ReferenceArray r3 = new ReferenceArray();
                                r3.Append(extremeBottomDowel); 
                                r3.Append(topFace);            
                                XYZ origin3 = leftPlanarFace.Origin + (activeView.RightDirection * -3.5);
                                doc.Create.NewDimension(activeView, Line.CreateBound(origin3, origin3 + activeView.UpDirection), r3);
                            }
                        } catch (Exception ex) { debugWarnings += $"- Right Tier 3: {ex.Message}\n"; }
                    }

                    t.Commit();
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

        private void HarvestBackViewEmbeds(Document doc, View activeView, List<Element> elementsToProcess, double colBottomZ, double colTopZ, 
            out List<Reference> topHookRef, out List<Reference> lowerHookRefs, out List<Reference> corbelRefs, out Reference extremeBottomDowel)
        {
            topHookRef = new List<Reference>();
            lowerHookRefs = new List<Reference>();
            corbelRefs = new List<Reference>();
            extremeBottomDowel = null;
            double absoluteLowestDowelZ = double.MaxValue;

            List<Tuple<Reference, double>> tempHooks = new List<Tuple<Reference, double>>();
            List<Tuple<Reference, double>> tempCorbels = new List<Tuple<Reference, double>>();
            
            XYZ viewUp = activeView.UpDirection;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView, IncludeNonVisibleObjects = true };

            foreach (Element elem in elementsToProcess)
            {
                if (elem is FamilyInstance inst && inst.Category.Id.Value != (long)BuiltInCategory.OST_StructuralColumns)
                {
                    string famName = inst.Symbol.FamilyName.ToLower();

                    bool isDowel = famName.Contains("groutec") || famName.Contains("receptor") || famName.Contains("donor") || famName.Contains("tube");
                    bool isCorbel = (famName.Contains("corbel") || famName.Contains("conn") || famName.Contains("pic")) && !isDowel;
                    bool isHook = famName.Contains("hock") || famName.Contains("lifting") || famName.Contains("lifter");

                    GeometryElement geomElem = inst.get_Geometry(geomOptions);

                    if (isCorbel)
                    {
                        Reference validRef = null;
                        double maxZ = -9999;
                        
                        // 1. Try named reference "DimRef" first 
                        try { validRef = inst.GetReferenceByName("DimRef"); } catch { }

                        // 2. Try standard built-in Top reference plane
                        if (validRef == null)
                        {
                            IList<Reference> topRefs = inst.GetReferences(FamilyInstanceReferenceType.Top);
                            if (topRefs != null && topRefs.Count > 0) validRef = topRefs[0];
                        }

                        // 3. Fallback: Find ONLY the single highest horizontal geometric face (Top of corbel)
                        if (validRef == null && geomElem != null)
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
                                            if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                            {
                                                double z = planarFace.Origin.DotProduct(viewUp);
                                                // Grab the highest face that is within the column bounds
                                                if (z > maxZ && Math.Abs(z - colTopZ) > 0.05 && Math.Abs(z - colBottomZ) > 0.05)
                                                {
                                                    maxZ = z;
                                                    validRef = planarFace.Reference;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (validRef != null)
                        {
                            // Calculate Z-height for deduplication sorting
                            BoundingBoxXYZ bbox = inst.get_BoundingBox(activeView) ?? inst.get_BoundingBox(null);
                            double refZ = maxZ != -9999 ? maxZ : (bbox != null ? bbox.Max.DotProduct(viewUp) : 0);
                            tempCorbels.Add(new Tuple<Reference, double>(validRef, refZ));
                        }
                    }
                    if (isHook)
                    {
                        Reference validRef = null;
                        
                        // 1. BULLETPROOF METHOD: Fetch specifically by the Name we just set
                        try { validRef = inst.GetReferenceByName("DimRef"); } catch { }

                        // 2. Try built-in Reference Planes (Fallback)
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
                                if (refs != null && refs.Count > 0)
                                {
                                    validRef = refs[0];
                                    break;
                                }
                            }
                        }

                        // 3. Deep Geometry Fallback (If no planes exist, grab highest point)
                        if (validRef == null && geomElem != null)
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
                                            if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                            {
                                                validRef = planarFace.Reference;
                                                break;
                                            }
                                        }
                                    }
                                    else if (obj is Line line && Math.Abs(line.Direction.DotProduct(viewUp)) < 0.05)
                                    {
                                        validRef = line.Reference;
                                        break;
                                    }
                                }
                                if (validRef != null) break;
                            }
                        }

                        BoundingBoxXYZ bbox = inst.get_BoundingBox(activeView) ?? inst.get_BoundingBox(null);
                        if (bbox != null && validRef != null)
                        {
                            double refZ = ((bbox.Max + bbox.Min) / 2.0).DotProduct(viewUp);
                            tempHooks.Add(new Tuple<Reference, double>(validRef, refZ));
                        }
                    }
                    else if (isDowel && geomElem != null)
                    {
                        // Handle Dowels modeled as Family Instances
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
                                        if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                        {
                                            double z = planarFace.Origin.DotProduct(viewUp);
                                            if (z < absoluteLowestDowelZ && z < (colBottomZ - 0.05))
                                            {
                                                absoluteLowestDowelZ = z;
                                                extremeBottomDowel = planarFace.Reference;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else if (elem is Autodesk.Revit.DB.Structure.Rebar rebar)
                {
                    // Handle Dowels modeled as Structural Rebar
                    GeometryElement geomElem = rebar.get_Geometry(geomOptions);
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
                                        if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                        {
                                            double z = planarFace.Origin.DotProduct(viewUp);
                                            if (z < absoluteLowestDowelZ && z < (colBottomZ - 0.05))
                                            {
                                                absoluteLowestDowelZ = z;
                                                extremeBottomDowel = planarFace.Reference;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Deduplicate Corbels to prevent Revit zero-length errors
            if (tempCorbels.Count > 0)
            {
                var sortedCorbels = tempCorbels.OrderByDescending(c => c.Item2).ToList();
                double lastCorbelZ = -9999;
                foreach (var c in sortedCorbels)
                {
                    if (Math.Abs(c.Item2 - lastCorbelZ) > 0.05)
                    {
                        corbelRefs.Add(c.Item1);
                        lastCorbelZ = c.Item2;
                    }
                }
            }

            // Split Hooks into Top Hook (Tier 1) and Lower Hooks (Tier 2)
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
        }
        private void HarvestRightViewZones(Document doc, View activeView, List<Element> elementsToProcess, double colBottomZ, double colTopZ, 
            out List<Reference> rebarZoneRefs, out Reference extremeBottomDowel)
        {
            rebarZoneRefs = new List<Reference>();
            extremeBottomDowel = null;
            double absoluteLowestDowelZ = double.MaxValue;

            XYZ viewUp = activeView.UpDirection;
            Options geomOptions = new Options { ComputeReferences = true, View = activeView, IncludeNonVisibleObjects = true };
            List<Tuple<Reference, double>> allRebarPlanes = new List<Tuple<Reference, double>>();

            foreach (Element elem in elementsToProcess)
            {
                // FIXED: Explicit namespace to resolve CS0122 Inaccessible error
                if (elem is Autodesk.Revit.DB.Structure.Rebar rebar)
                {
                    GeometryElement geomElem = rebar.get_Geometry(geomOptions);
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
                                        if (face is PlanarFace planarFace && Math.Abs(planarFace.FaceNormal.DotProduct(viewUp)) > 0.99)
                                        {
                                            double z = planarFace.Origin.DotProduct(viewUp);
                                            
                                            if (z < (colTopZ - 0.05) && z > (colBottomZ + 0.05))
                                            {
                                                allRebarPlanes.Add(new Tuple<Reference, double>(planarFace.Reference, z));
                                            }
                                            
                                            if (z < absoluteLowestDowelZ && z < (colBottomZ - 0.05))
                                            {
                                                absoluteLowestDowelZ = z;
                                                extremeBottomDowel = planarFace.Reference;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            var sortedPlanes = allRebarPlanes.OrderBy(p => p.Item2).ToList();
            double lastZ = -9999;
            foreach (var plane in sortedPlanes)
            {
                if (Math.Abs(plane.Item2 - lastZ) > 10.0) 
                {
                    rebarZoneRefs.Add(plane.Item1);
                    lastZ = plane.Item2;
                }
            }
        }

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
                IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();
                
                foreach (GeometryObject obj in objectsToCheck)
                {
                    if (obj is Solid solid)
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
            }
            return new Reference[] { leftRef, rightRef };
        }

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
                IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();
                
                foreach (GeometryObject obj in objectsToCheck)
                {
                    if (obj is Solid solid)
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
            }
            return bottomRef;
        }

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
                IEnumerable<GeometryObject> objectsToCheck = new List<GeometryObject> { geomObj };
                if (geomObj is GeometryInstance geomInst) objectsToCheck = geomInst.GetInstanceGeometry();
                
                foreach (GeometryObject obj in objectsToCheck)
                {
                    if (obj is Solid solid)
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