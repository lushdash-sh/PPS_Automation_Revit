// =============================================================================
//  StructAuto Detailing — Shared Utilities
//  File:    Parsers/ParseUtilities.cs
//  Purpose: Unit conversion helpers and a lightweight diagnostic logger
//           used by Phase 1 (and later by Phases 2–5).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using StructAutoDetailing.Models;

namespace StructAutoDetailing.Parsers
{
    // ─────────────────────────────────────────────────────────────────────────
    //  UNIT CONVERSION  (all crossings between ft and mm happen here)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Centralises unit conversion so that changing Revit's internal unit system
    /// (unlikely, but possible in Revit 2024+) only requires edits in one place.
    /// All "Ft" methods return Revit internal decimal feet.
    /// All "Mm" methods return millimetres (display/output only).
    /// </summary>
    public static class UnitConv
    {
        // ── mm → ft  (store in PrecastColumnData) ─────────────────────────
        public static double MmToFt(double mm)  => mm / 304.8;
        public static double MToFt(double m)    => m  / 0.3048;

        // ── ft → mm  (format for drawing annotations) ─────────────────────
        public static double FtToMm(double ft)  => ft  * 304.8;
        public static double FtToM(double ft)   => ft  * 0.3048;

        // ── Revit API wrappers (preferred when a Revit UnitTypeId is available) ─
        public static double RevitMmToFt(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        public static double RevitFtToMm(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        // ── Formatting helpers for annotation text ────────────────────────

        /// <summary>Formats a feet value as "750" (integer mm, no unit suffix).</summary>
        public static string FmtMm(double ft) => $"{Math.Round(FtToMm(ft)):F0}";

        /// <summary>Formats a world-Z value (feet) as "EL 4.350 M".</summary>
        public static string FmtElevation(double worldZFt) =>
            $"EL {FtToM(worldZFt):F3} M";

        /// <summary>Formats a volume (cubic feet) as "2.44 cu.m".</summary>
        public static string FmtVolume(double volumeFt3)
        {
            double m3 = volumeFt3 * 0.0283168;
            return $"{m3:F2} cu.m";
        }

        /// <summary>Formats a weight in tonnes to 2 decimal places.</summary>
        public static string FmtWeight(double tonnes) => $"{tonnes:F2} T";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PARSE DIAGNOSTICS  (surface warnings to user via TaskDialog)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Collects warnings and errors produced during parsing and shows them to
    /// the user as a single <see cref="TaskDialog"/> at the end of the pipeline,
    /// rather than interrupting them with multiple modal dialogs mid-run.
    /// </summary>
    public static class ParseDiagnostics
    {
        /// <summary>
        /// Shows all warnings in <see cref="PrecastColumnData.ParseWarnings"/> as
        /// a summarised TaskDialog. Only displayed if warnings exist.
        /// Call at the end of Execute() before showing the success message.
        /// </summary>
        public static void ShowIfAny(UIApplication uiApp, PrecastColumnData colData)
        {
            if (colData.ParseWarnings.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"The following {colData.ParseWarnings.Count} warning(s) were generated " +
                          $"while parsing column '{colData.ElementMark}':");
            sb.AppendLine();

            for (int i = 0; i < colData.ParseWarnings.Count; i++)
                sb.AppendLine($"  {i + 1}. {colData.ParseWarnings[i]}");

            sb.AppendLine();
            sb.AppendLine("Drawings have been generated. Review flagged items manually.");

            TaskDialog td = new TaskDialog("StructAuto — Parse Warnings")
            {
                MainInstruction = $"Column '{colData.ElementMark}' — {colData.ParseWarnings.Count} Warning(s)",
                MainContent     = sb.ToString(),
                CommonButtons   = TaskDialogCommonButtons.Ok,
                DefaultButton   = TaskDialogResult.Ok
            };
            td.Show();
        }

        /// <summary>
        /// Shows a structured summary of the parsed <see cref="PrecastColumnData"/>
        /// in a TaskDialog. Useful during development / testing to verify Phase 1 output
        /// before writing any views. Remove or gate behind a debug flag in production.
        /// </summary>
        public static void ShowParseSummary(UIApplication uiApp, PrecastColumnData colData)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Element Mark   : {colData.ElementMark}");
            sb.AppendLine($"Family Type    : {colData.TypeName}");
            sb.AppendLine();
            sb.AppendLine("─── Geometry ─────────────────────────────────");
            sb.AppendLine($"Shaft (W × D)  : {UnitConv.FmtMm(colData.ShaftWidthFt)} × {UnitConv.FmtMm(colData.ShaftDepthFt)} mm");
            sb.AppendLine($"Total Height   : {UnitConv.FmtMm(colData.TotalHeightFt)} mm");
            sb.AppendLine($"Base EL        : {colData.BaseElevationM:F3} m");
            sb.AppendLine($"Top  EL        : {colData.TopElevationM:F3} m");
            sb.AppendLine($"Volume         : {colData.VolumeM3:F2} m³");
            sb.AppendLine($"Est. Weight    : {colData.WeightTonnes:F2} T");
            sb.AppendLine();
            sb.AppendLine("─── Orientation ───────────────────────────────");
            sb.AppendLine($"Status         : {colData.Orientation.Status}");
            sb.AppendLine($"Tilt Angle     : {colData.Orientation.TiltAngleDegrees:F2}°");
            sb.AppendLine($"Plan Rotation  : {colData.Orientation.PlanRotationDegrees:F1}°");
            sb.AppendLine($"AxisX (World)  : {colData.Orientation.AxisX}");
            sb.AppendLine($"AxisY (World)  : {colData.Orientation.AxisY}");
            sb.AppendLine($"AxisZ (World)  : {colData.Orientation.AxisZ}");
            sb.AppendLine();
            sb.AppendLine("─── Corbels ───────────────────────────────────");
            sb.AppendLine($"Has Corbels    : {colData.HasCorbels}  ({colData.Corbels.Count} found)");
            foreach (var c in colData.Corbels)
                sb.AppendLine($"  {c}");
            sb.AppendLine();
            sb.AppendLine("─── Embedded Elements ─────────────────────────");
            sb.AppendLine($"Corr. Sleeves  : {colData.CorrugatedSleeves.Count}");
            foreach (var e in colData.CorrugatedSleeves)
                sb.AppendLine($"  {e}");
            sb.AppendLine($"Lifters        : {colData.Lifters.Count}");
            foreach (var e in colData.Lifters)
                sb.AppendLine($"  {e}");
            sb.AppendLine($"Dowel Bars     : {colData.DowelBars.Count}");
            foreach (var e in colData.DowelBars)
                sb.AppendLine($"  {e}");
            sb.AppendLine($"Unclassified   : {colData.UnclassifiedEmbeds.Count}");

            // ── Phase 2 rebar summary (if populated) ──────────────────────
            if (colData.Rebar?.IsComplete == true)
            {
                var r = colData.Rebar;
                sb.AppendLine();
                sb.AppendLine("─── Rebar Cage (Phase 2) ───────────────────────");
                sb.AppendLine($"Longitudinal bars  : {r.LongitudinalBars.Count}");
                foreach (var b in r.LongitudinalBars.Take(8))
                    sb.AppendLine($"  {b}");
                if (r.LongitudinalBars.Count > 8)
                    sb.AppendLine($"  … +{r.LongitudinalBars.Count - 8} more");
                sb.AppendLine($"Transverse bars    : {r.TransverseBars.Count}");
                sb.AppendLine($"Tie zones          : {r.TieZones.Count}");
                foreach (var z in r.TieZones)
                    sb.AppendLine($"  {z}");
                sb.AppendLine();
                sb.AppendLine("─── Bend Schedule ──────────────────────────────");
                foreach (var row in r.BendSchedule)
                    sb.AppendLine($"  {row}");
                sb.AppendLine();
                sb.AppendLine("─── Schedule Summary ───────────────────────────");
                foreach (var kvp in r.ScheduleSummary.RowsByDiameter.OrderBy(k => k.Key))
                    sb.AppendLine($"  {kvp.Value}");
                sb.AppendLine($"  TOTAL WEIGHT : {r.ScheduleSummary.GrandTotalFormatted}");
            }

            if (colData.ParseWarnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"─── Warnings ({colData.ParseWarnings.Count}) ──────────────────────");
                foreach (var w in colData.ParseWarnings)
                    sb.AppendLine($"  ⚠ {w}");
            }

            TaskDialog td = new TaskDialog("StructAuto — Phase 1 + 2 Parse Summary (DEBUG)")
            {
                MainInstruction = $"Column '{colData.ElementMark}' parsed successfully",
                MainContent     = sb.ToString(),
                CommonButtons   = TaskDialogCommonButtons.Ok
            };
            td.Show();
        }
    }
}