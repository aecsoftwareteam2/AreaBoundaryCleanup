// ============================================================================
// Revit Area Boundary Line Cleanup Tool
// ============================================================================
// External commands that scan Area Boundary Lines (OST_AreaSplitLines) and
// clean up common issues:
//   1. Duplicate lines (identical or near-identical start/end points)
//   2. Overlapping / redundant collinear segments
//   3. Small gaps between endpoints (snaps them closed within tolerance,
//      supports both straight Lines and Arcs)
//   4. Tiny / sliver segments below a minimum length threshold
//
// Two commands are provided, sharing the same cleanup engine:
//   - AreaBoundaryCleanupCommand          -> cleans the whole document
//   - AreaBoundaryCleanupSelectedCommand  -> cleans only user-picked lines
//
// A WPF dialog lets the user toggle each operation and adjust tolerances
// before running.
//
// Target: Revit 2021+ | References: RevitAPI.dll, RevitAPIUI.dll,
// PresentationFramework.dll, PresentationCore.dll, WindowsBase.dll
// ============================================================================  

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace AreaBoundaryCleanup
{
    // ==================================================================
    // COMMAND 1: Clean up the entire document
    // ==================================================================
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AreaBoundaryCleanupCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument; 
            Document doc = uidoc.Document;

            PrintString();

            try
            {
                CleanupOptions options = CleanupOptionsWindow.ShowDialogWindow();
                 
                if (options == null)
                    return Result.Cancelled; 

                List<CurveElement> boundaryLines = CleanupEngine.CollectAreaBoundaryLines(doc);

                if (boundaryLines.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Area Boundary Cleanup",
                        "No Area Boundary Lines were found in this document.");
                    return Result.Succeeded;
                }

                CleanupReport report = new CleanupReport();

                using (Transaction t = new Transaction(doc, "Cleanup Area Boundary Lines"))
                {
                    t.Start();
                    CleanupEngine.RunCleanup(doc, boundaryLines, options, report);
                    t.Commit();
                }

                CleanupEngine.ShowReport(report);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Cleanup failed:\n{ex.Message}\n\n{ex.StackTrace}");

                return Result.Failed;
            }
        }

        private static void PrintString()
        {
            System.Windows.Forms.MessageBox.Show("PrintString AreaBoundaryCleanupCommand 2");
        }
    }

    // ==================================================================
    // COMMAND 2: Clean up only the lines the user selects
    // ==================================================================
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AreaBoundaryCleanupSelectedCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;  
            Document doc = uidoc.Document;

            try
            {
                IList<Reference> refs;

                try
                {
                    refs = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        new AreaBoundaryLineFilter(),
                        "Select Area Boundary Lines to clean up, then click Finish");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                if (refs == null || refs.Count == 0)
                    return Result.Cancelled;

                List<CurveElement?> selectedLines = refs
                    .Select(r => doc.GetElement(r) as CurveElement)
                    .Where(c => c != null && c.GeometryCurve != null)
                    .ToList();

                if (selectedLines.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Area Boundary Cleanup", "No valid boundary lines were selected.");
                    return Result.Cancelled;
                }

                CleanupOptions options = CleanupOptionsWindow.ShowDialogWindow();
                if (options == null)
                    return Result.Cancelled;

                CleanupReport report = new CleanupReport();

                using (Transaction t = new Transaction(doc, "Cleanup Selected Area Boundary Lines"))
                {
                    t.Start();
                    CleanupEngine.RunCleanup(doc, selectedLines!, options, report);
                    t.Commit();
                }

                CleanupEngine.ShowReport(report);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"Cleanup failed:\n{ex.Message}\n\n{ex.StackTrace}");

                return Result.Failed;
            }
        }
    }

    internal class AreaBoundaryLineFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            return elem.Category != null &&
                   elem.Category.Id.Value == (int)BuiltInCategory.OST_AreaSchemeLines;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    // ==================================================================
    // SHARED CLEANUP ENGINE
    // ==================================================================
    internal static class CleanupEngine
    {
        public static List<CurveElement> CollectAreaBoundaryLines(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement))
                .OfCategory(BuiltInCategory.OST_AreaSchemeLines)
                .Cast<CurveElement>()
                .Where(c => c.GeometryCurve != null)
                .ToList();
        }

        public static void RunCleanup(Document doc, List<CurveElement> lines,
            CleanupOptions options, CleanupReport report)
        {
            var linesByLevel = GroupLinesByLevel(lines, doc);

            foreach (var levelGroup in linesByLevel)
            {
                ProcessLevelGroup(doc, levelGroup.Value, options, report);
            }
        }

        private static Dictionary<ElementId, List<CurveElement>> GroupLinesByLevel( 
            List<CurveElement> lines, Document doc)
        {
            var grouped = new Dictionary<ElementId, List<CurveElement>>();

            foreach (var line in lines)
            {
                ElementId levelId = GetAssociatedLevelId(line);

                if (!grouped.ContainsKey(levelId))
                    grouped[levelId] = new List<CurveElement>();

                grouped[levelId].Add(line);
            }

            return grouped;
        }

        private static ElementId GetAssociatedLevelId(CurveElement line)
        {
            Parameter levelParam = line.get_Parameter(BuiltInParameter.LEVEL_PARAM);

            if (levelParam != null && levelParam.HasValue)
                return levelParam.AsElementId();

            return ElementId.InvalidElementId;
        }

        private static void ProcessLevelGroup(Document doc, List<CurveElement> lines,
            CleanupOptions options, CleanupReport report)
        {
            var working = lines.Select(l => new LineRecord
            {
                Id = l.Id,
                Curve = l.GeometryCurve,
                Element = l
            }).ToList();

            List<ElementId> toDelete = new List<ElementId>();

            // ---- 1. Remove exact / near-duplicate lines ----
            if (options.RemoveDuplicates)
            {
                for (int i = 0; i < working.Count; i++)
                {
                    if (toDelete.Contains(working[i].Id)) continue;

                    for (int j = i + 1; j < working.Count; j++)
                    {
                        if (toDelete.Contains(working[j].Id)) continue;

                        if (AreCurvesDuplicate(working[i].Curve, working[j].Curve, options.DuplicateTolerance))
                        {
                            toDelete.Add(working[j].Id);
                            report.DuplicatesRemoved++;
                        }
                    }
                }
            }

            // ---- 2. Remove overlapping collinear segments ----
            if (options.RemoveOverlaps)
            {
                for (int i = 0; i < working.Count; i++)
                {
                    if (toDelete.Contains(working[i].Id)) continue;
                    if (!(working[i].Curve is Line lineA)) continue;

                    for (int j = 0; j < working.Count; j++)
                    {
                        if (i == j) continue;
                        if (toDelete.Contains(working[j].Id)) continue;
                        if (!(working[j].Curve is Line lineB)) continue;

                        if (IsCollinearOverlap(lineA, lineB) && lineB.Length <= lineA.Length)
                        {
                            toDelete.Add(working[j].Id);
                            report.OverlapsRemoved++;
                        }
                    }
                }
            }

            // ---- 3. Remove tiny sliver segments ----
            if (options.RemoveTinySegments)
            {
                foreach (var rec in working) 
                {
                    if (toDelete.Contains(rec.Id)) continue;
                    if (rec.Curve.Length < options.MinSegmentLength) 
                    {
                        toDelete.Add(rec.Id);
                        report.TinySegmentsRemoved++;
                    }
                }
            }

            if (toDelete.Count > 0)
            {
                doc.Delete(toDelete);
                working = working.Where(w => !toDelete.Contains(w.Id)).ToList(); 
            }

            // ---- 4. Close small gaps between endpoints (Lines + Arcs) ----
            if (options.CloseGaps)
            {
                report.GapsClosed += CloseSmallGaps(doc, working, options.GapCloseTolerance); 
            }
        }

        // --------------------------------------------------------------
        // Geometry helpers
        // --------------------------------------------------------------
        private static bool AreCurvesDuplicate(Curve a, Curve b, double tol)
        {
            if (a is Line lineA && b is Line lineB)
            {
                XYZ a1 = lineA.GetEndPoint(0);
                XYZ a2 = lineA.GetEndPoint(1);
                XYZ b1 = lineB.GetEndPoint(0);
                XYZ b2 = lineB.GetEndPoint(1);

                bool sameDirection = a1.IsAlmostEqualTo(b1, tol) && a2.IsAlmostEqualTo(b2, tol);
                bool reversed = a1.IsAlmostEqualTo(b2, tol) && a2.IsAlmostEqualTo(b1, tol);

                return sameDirection || reversed;
            }

            if (a.GetType() == b.GetType())
            {
                XYZ aMid = a.Evaluate(0.5, true);
                XYZ bMid = b.Evaluate(0.5, true);

                bool endsMatch =
                    (a.GetEndPoint(0).IsAlmostEqualTo(b.GetEndPoint(0), tol) &&
                     a.GetEndPoint(1).IsAlmostEqualTo(b.GetEndPoint(1), tol)) ||
                    (a.GetEndPoint(0).IsAlmostEqualTo(b.GetEndPoint(1), tol) &&
                     a.GetEndPoint(1).IsAlmostEqualTo(b.GetEndPoint(0), tol));   

                return endsMatch && aMid.IsAlmostEqualTo(bMid, tol);
            }

            return false;
        }

        private static bool IsCollinearOverlap(Line a, Line b)
        {
            XYZ dirA = a.Direction.Normalize();
            XYZ dirB = b.Direction.Normalize();

            double dot = Math.Abs(dirA.DotProduct(dirB));

            if (dot < 0.9999) return false;

            XYZ a1 = a.GetEndPoint(0);
            XYZ toB1 = b.GetEndPoint(0) - a1;
            XYZ cross = dirA.CrossProduct(toB1);

            if (cross.GetLength() > 0.01) return false;

            double tB1 = dirA.DotProduct(b.GetEndPoint(0) - a1);
            double tB2 = dirA.DotProduct(b.GetEndPoint(1) - a1);
            double tA1 = 0;
            double tA2 = dirA.DotProduct(a.GetEndPoint(1) - a1);

            double minA = Math.Min(tA1, tA2), maxA = Math.Max(tA1, tA2);
            double minB = Math.Min(tB1, tB2), maxB = Math.Max(tB1, tB2);

            return minB >= minA - 0.01 && maxB <= maxA + 0.01;
        }

        // ---- Gap closing (supports Line and Arc endpoints) ----
        // Preferred fix: extend both curves along their tangent direction at the
        // loose end until they meet at a true intersection point, so a corner
        // stays a clean corner instead of kinking toward an averaged midpoint.
        // Falls back to the midpoint when the curves are parallel or the
        // computed intersection is implausibly far away (bad data / near-parallel).
        private static int CloseSmallGaps(Document doc, List<LineRecord> lines, double tol)
        {
            int gapsClosed = 0;
            double maxExtension = Math.Max(tol * 15, 0.5); // sanity cap on how far we'll extend

            var endpoints = new List<(ElementId id, int index, XYZ pt, Curve curve)>();

            foreach (var rec in lines)
            {
                endpoints.Add((rec.Id, 0, rec.Curve.GetEndPoint(0), rec.Curve));
                endpoints.Add((rec.Id, 1, rec.Curve.GetEndPoint(1), rec.Curve));
            }

            var processed = new HashSet<(ElementId, int)>();

            for (int i = 0; i < endpoints.Count; i++)
            {
                var (idA, idxA, ptA, curveA) = endpoints[i];

                if (processed.Contains((idA, idxA))) continue;

                for (int j = i + 1; j < endpoints.Count; j++)
                {
                    var (idB, idxB, ptB, curveB) = endpoints[j];

                    if (idA == idB) continue;

                    if (processed.Contains((idB, idxB))) continue;

                    double dist = ptA.DistanceTo(ptB);

                    if (dist > 1e-6 && dist <= tol)
                    {
                        XYZ targetPoint = ComputeIntersectionPoint(curveA, idxA, ptA, curveB, idxB, ptB, maxExtension)
                                          ?? (ptA + ptB) * 0.5; // fallback: midpoint

                        bool okA = UpdateEndpoint(doc, idA, idxA, targetPoint);
                        bool okB = UpdateEndpoint(doc, idB, idxB, targetPoint);

                        if (okA && okB)
                        {
                            processed.Add((idA, idxA));
                            processed.Add((idB, idxB));
                            gapsClosed++;
                        }
                        break;
                    }
                }
            }

            return gapsClosed;
        }

        /// <summary>
        /// Computes the true intersection point of two curves by extending each
        /// along its tangent direction at the given loose endpoint. Returns null
        /// (signalling "use the midpoint instead") when the curves are parallel,
        /// non-coplanar, or the intersection lies further away than maxExtension
        /// — which usually means the "intersection" would be a poor/misleading fix.
        /// </summary>
        private static XYZ ComputeIntersectionPoint(Curve curveA, int endA, XYZ ptA,
            Curve curveB, int endB, XYZ ptB, double maxExtension)
        {
            XYZ dirA = GetTangentAtEnd(curveA, endA);
            XYZ dirB = GetTangentAtEnd(curveB, endB);
            if (dirA == null || dirB == null) return null;

            XYZ cross = dirA.CrossProduct(dirB);
            double crossLen = cross.GetLength();

            // Parallel (or anti-parallel) directions — no meaningful single intersection
            if (crossLen < 1e-9) return null;

            // Coplanarity check: (ptB - ptA) should lie in the plane spanned by dirA/dirB
            XYZ w = ptB - ptA;
            double planarity = Math.Abs(w.DotProduct(cross.Normalize()));

            if (planarity > 0.01) return null; // not coplanar enough to trust a 3D intersection

            // Solve for t where ptA + t*dirA meets the line through ptB with direction dirB
            double t = (w.CrossProduct(dirB)).DotProduct(cross) / (crossLen * crossLen);
            XYZ intersection = ptA + dirA.Multiply(t);

            // Sanity check: don't trust wild extrapolations from noisy/bad geometry
            if (intersection.DistanceTo(ptA) > maxExtension || intersection.DistanceTo(ptB) > maxExtension)
                return null;

            return intersection;
        }

        /// <summary>
        /// Returns the unit direction a curve is heading in, continuing outward
        /// past the given end index (0 = start, 1 = end). For a Line this is
        /// just its direction (or reverse). For an Arc, the true tangent at that
        /// endpoint is used so curved boundary segments extend naturally.
        /// </summary>
        private static XYZ GetTangentAtEnd(Curve curve, int endIndex)
        {
            if (curve is Line line)
            {
                XYZ dir = line.Direction.Normalize();
                return endIndex == 1 ? dir : dir.Negate();
            }

            if (curve is Arc)
            {
                double param = endIndex == 1 ? curve.GetEndParameter(1) : curve.GetEndParameter(0);
                Transform derivatives = curve.ComputeDerivatives(param, false);
                XYZ tangent = derivatives.BasisX.Normalize();

                return endIndex == 1 ? tangent : tangent.Negate();
            }

            return null; // unsupported curve type
        }

        /// <summary>
        /// Moves one endpoint of a curve element to newPoint. Supports straight
        /// Lines directly, and Arcs by re-deriving the arc through the new
        /// endpoint while preserving its center and radius as closely as possible.
        /// </summary>
        private static bool UpdateEndpoint(Document doc, ElementId lineId, int endIndex, XYZ newPoint)
        {
            CurveElement? ce = doc.GetElement(lineId) as CurveElement;
            if (ce == null) return false;

            Curve curve = ce.GeometryCurve;

            if (curve is Line line)
            {
                XYZ p0 = endIndex == 0 ? newPoint : line.GetEndPoint(0);
                XYZ p1 = endIndex == 1 ? newPoint : line.GetEndPoint(1);

                if (p0.DistanceTo(p1) < 0.001) return false;

                Line newLine = Line.CreateBound(p0, p1);
                ce.SetGeometryCurve(newLine, false);
                return true;
            }

            if (curve is Arc arc)
            {
                XYZ center = arc.Center;
                double radius = arc.Radius;
                XYZ xVec = arc.XDirection;
                XYZ yVec = arc.YDirection;
                XYZ normal = arc.Normal;

                // Project the new point onto the arc's circle so the radius is preserved
                XYZ toPoint = (newPoint - center);
                XYZ inPlane = toPoint - normal.Multiply(normal.DotProduct(toPoint));
                if (inPlane.GetLength() < 1e-9) return false;
                XYZ projected = center + inPlane.Normalize().Multiply(radius);

                XYZ start = endIndex == 0 ? projected : arc.GetEndPoint(0);
                XYZ end = endIndex == 1 ? projected : arc.GetEndPoint(1);
                XYZ mid = arc.Evaluate(0.5, true); // keep the existing bulge/midpoint

                if (start.DistanceTo(end) < 0.001) return false;

                try
                {
                    Arc newArc = Arc.Create(start, end, mid);
                    ce.SetGeometryCurve(newArc, false);
                    return true;
                }
                catch
                {
                    // Degenerate arc (e.g. three nearly-collinear points) — skip safely
                    return false;
                }
            }

            // Splines and other curve types are not modified — flagged only
            return false;
        }

        public static void ShowReport(CleanupReport report)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Cleanup Complete",
                $"Area Boundary Line cleanup finished:\n\n" +
                $"Duplicate lines removed:   {report.DuplicatesRemoved}\n" +
                $"Overlapping lines removed: {report.OverlapsRemoved}\n" +
                $"Tiny segments removed:     {report.TinySegmentsRemoved}\n" +
                $"Gaps closed:               {report.GapsClosed}\n\n" +
                $"Total changes: {report.TotalChanges}");
        }
    }

    // ==================================================================
    // Supporting types
    // ==================================================================
    internal class LineRecord
    {
        public ElementId Id { get; set; }
        public Curve Curve { get; set; }
        public CurveElement Element { get; set; }

        public LineRecord()
        {
            this.Id = ElementId.InvalidElementId;
            this.Curve = null!;
            this.Element = null!;
        }
    }

    internal class CleanupOptions
    {
        public bool RemoveDuplicates { get; set; } = true;
        public bool RemoveOverlaps { get; set; } = true;
        public bool RemoveTinySegments { get; set; } = true;
        public bool CloseGaps { get; set; } = true;

        // Tolerances, in feet (Revit's internal unit). Defaults ≈ 3mm / 15mm / 30mm.
        public double DuplicateTolerance { get; set; } = 0.01;
        public double GapCloseTolerance { get; set; } = 0.05;
        public double MinSegmentLength { get; set; } = 0.1;
    }

    internal class CleanupReport
    {
        public int DuplicatesRemoved { get; set; }
        public int OverlapsRemoved { get; set; }
        public int TinySegmentsRemoved { get; set; }
        public int GapsClosed { get; set; }

        public int TotalChanges =>
            DuplicatesRemoved + OverlapsRemoved + TinySegmentsRemoved + GapsClosed;
    }

    // ==================================================================
    // WPF Options Dialog (built entirely in code — no external .xaml needed)
    // ==================================================================
    internal class CleanupOptionsWindow : Window
    {
        private System.Windows.Controls.CheckBox _cbDuplicates, _cbOverlaps, _cbTiny, _cbGaps;
        private System.Windows.Controls.TextBox _tbDupTol, _tbGapTol, _tbMinLen;
        private bool _confirmed;

        public CleanupOptionsWindow()
        {
            Title = "Area Boundary Line Cleanup — Options"; 
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "Select the cleanup operations to run:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            _cbDuplicates = AddOptionRow(root, "Remove duplicate lines", true,
                out _tbDupTol, "Duplicate match tolerance (ft):", "0.01");

            _cbOverlaps = new System.Windows.Controls.CheckBox
            {
                Content = "Remove overlapping / redundant segments",
                IsChecked = true,
                Margin = new Thickness(0, 6, 0, 6)
            };
            root.Children.Add(_cbOverlaps);

            _cbTiny = AddOptionRow(root, "Remove tiny sliver segments", true,
                out _tbMinLen, "Minimum segment length (ft):", "0.1");

            _cbGaps = AddOptionRow(root, "Close small gaps between endpoints (Lines + Arcs)", true,
                out _tbGapTol, "Gap close tolerance (ft):", "0.05");

            var buttonPanel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var okBtn = new System.Windows.Controls.Button { Content = "Run Cleanup", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
            okBtn.Click += (s, e) => { _confirmed = true; Close(); };

            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 80 };
            cancelBtn.Click += (s, e) => { _confirmed = false; Close(); };

            buttonPanel.Children.Add(okBtn);
            buttonPanel.Children.Add(cancelBtn);
            root.Children.Add(buttonPanel);

            Content = root;
        }

        private System.Windows.Controls.CheckBox AddOptionRow(StackPanel root, string label, bool defaultChecked,
            out System.Windows.Controls.TextBox toleranceBox, string toleranceLabel, string defaultValue)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = label,
                IsChecked = defaultChecked,
                Margin = new Thickness(0, 6, 0, 2)
            };
            root.Children.Add(cb);

            var tolRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(20, 0, 0, 8)
            };
            tolRow.Children.Add(new TextBlock
            {
                Text = toleranceLabel,
                Width = 200,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tb = new System.Windows.Controls.TextBox { Text = defaultValue, Width = 80 };
            tolRow.Children.Add(tb);
            root.Children.Add(tolRow);

            toleranceBox = tb;
            return cb;
        }

        /// <summary>Shows the dialog modally and returns the chosen options, or null if cancelled.</summary>
        public static CleanupOptions ShowDialogWindow()
        {
            var win = new CleanupOptionsWindow();
            win.ShowDialog();

            if (!win._confirmed) return null;

            return new CleanupOptions
            {
                RemoveDuplicates = win._cbDuplicates.IsChecked == true,
                RemoveOverlaps = win._cbOverlaps.IsChecked == true,
                RemoveTinySegments = win._cbTiny.IsChecked == true,
                CloseGaps = win._cbGaps.IsChecked == true,
                DuplicateTolerance = ParseOrDefault(win._tbDupTol.Text, 0.01),
                GapCloseTolerance = ParseOrDefault(win._tbGapTol.Text, 0.05),
                MinSegmentLength = ParseOrDefault(win._tbMinLen.Text, 0.1)
            };
        }

        private static double ParseOrDefault(string text, double fallback)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)
                ? val
                : fallback;
        }        
    }
}