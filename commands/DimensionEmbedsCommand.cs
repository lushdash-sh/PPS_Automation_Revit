using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace StructAutoDetailing.Commands
{
    // TransactionMode.Manual means we control when the database is modified.
    [Transaction(TransactionMode.Manual)]
    public class DimensionEmbedsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // 1. Guard Clause: Make sure we are in a 2D view before we try to dimension!
            if (activeView.ViewType != ViewType.Elevation && activeView.ViewType != ViewType.Section)
            {
                TaskDialog.Show("Error", "Please run this command from an isolated Elevation or Section view.");
                return Result.Failed;
            }

            try
            {
                // 2. Prompt the user to select the column. We use a custom filter (defined at the bottom) 
                // so they can ONLY click on FamilyInstances, preventing them from accidentally clicking a grid line.
                Reference colRef = uidoc.Selection.PickObject(ObjectType.Element, new ColumnSelectionFilter(), "Select the Precast Column");
                FamilyInstance column = doc.GetElement(colRef) as FamilyInstance;

                // 3. Harvest the concrete edges!
                Reference[] faces = GetLeftAndRightColumnFaces(doc, activeView, column);

                // 4. Test our result
                if (faces[0] != null && faces[1] != null)
                {
                    TaskDialog.Show("Success!", "Left and Right faces successfully harvested! We are ready to place dimensions.");
                }
                else
                {
                    TaskDialog.Show("Warning", "Could not find both Left and Right faces. Check the view crop or geometry.");
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // The user pressed ESC to cancel the selection. This is normal.
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", ex.Message);
                return Result.Failed;
            }
        }

        // --- THE HARVESTER METHOD ---
        private Reference[] GetLeftAndRightColumnFaces(Document doc, View activeView, FamilyInstance column)
        {
            Reference leftRef = null;
            Reference rightRef = null;

            // To get dimensionable edges, we MUST tell Revit to "ComputeReferences" for this specific view.
            Options geomOptions = new Options
            {
                ComputeReferences = true,
                View = activeView,
                IncludeNonVisibleObjects = false
            };

            GeometryElement geomElem = column.get_Geometry(geomOptions);
            if (geomElem == null) return new Reference[] { null, null };

            // In Revit API, RightDirection is a vector pointing "Right" on your computer screen.
            XYZ viewRight = activeView.RightDirection;

            // Loop through the geometry to find our flat planes
            foreach (GeometryObject geomObj in geomElem)
            {
                if (geomObj is Solid solid && solid.Faces.Size > 0)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is PlanarFace planarFace)
                        {
                            // If the face's normal is perpendicular to our screen's "Right", it's a vertical edge!
                            // (Dot product of 1 or -1 means parallel vectors, 0 means perpendicular).
                            double dotProduct = Math.Abs(planarFace.FaceNormal.DotProduct(viewRight));

                            if (dotProduct > 0.99) // Almost 1 means it's facing straight left or straight right
                            {
                                // Is it the left edge or right edge? Compare its location to the center of the column.
                                BoundingBoxXYZ bbox = column.get_BoundingBox(activeView);
                                XYZ center = (bbox.Min + bbox.Max) / 2.0;
                                XYZ faceOrigin = planarFace.Origin;
                                
                                // A negative result means it's sitting to the left of the center.
                                double directionCheck = (faceOrigin - center).DotProduct(viewRight);

                                if (directionCheck < 0)
                                    leftRef = planarFace.Reference;
                                else
                                    rightRef = planarFace.Reference;
                            }
                        }
                    }
                }
            }

            return new Reference[] { leftRef, rightRef };
        }
    }

    // --- SELECTION FILTER ---
    public class ColumnSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            // Only allow the user to select FamilyInstances (which is what a precast column is)
            return elem is FamilyInstance;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false;
        }
    }
}