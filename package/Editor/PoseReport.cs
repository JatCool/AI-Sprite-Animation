using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AISpriteAnimation
{
    /// <summary>One kind of correction and how much work it did.</summary>
    [Serializable]
    public sealed class CorrectionCount
    {
        public string name;
        public int values;     // joint angles / root values changed
        public int frames;     // frames touched
    }

    /// <summary>
    /// What validation and the constraint stage found and what they changed. The numbers are the quantitative validation report: they are saved with the
    /// pose asset so a result can be explained, and printed by the batch tools.
    /// </summary>
    public sealed class PoseReport
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Repairs = new List<string>();
        public int RepairedValues, TotalValues;
        /// <summary>True when the input was so broken that it must not be used at all.</summary>
        public bool Rejected;

        // ---- quantitative report
        public string Kind = "";
        public int FrameCount;
        /// <summary>Per frame: changed by a repair or a constraint (smoothing and loop closing do not count).</summary>
        public bool[] CorrectedFrames = new bool[0];
        /// <summary>Frames the AI got so wrong that they were discarded and rebuilt from their neighbours.</summary>
        public readonly List<int> RejectedFrameList = new List<int>();
        public readonly List<CorrectionCount> Corrections = new List<CorrectionCount>();
        /// <summary>Measurements of the AI poses as they came out of the backend (gaps filled) and of the final poses.</summary>
        public PoseMetrics SourceMetrics, FinalMetrics;
        public PoseLimitsInfo Limits;
        /// <summary>Idle: how visibly the upper body moved before the constraint stage (px) and the gain that was applied to the AI's coherent sway (0 = none).</summary>
        public float VisibleMotionBefore, IdleMotionGain;
        /// <summary>Largest change of a joint angle between the source poses and the final poses (degrees), and where.</summary>
        public float MaxChangeFromSource;
        public string MaxChangeWhere = "";
        /// <summary>Difference to the procedural animation of the same type (set by whoever knows the reference).</summary>
        public bool HasReference;
        public float MeanDeviationDegrees, MaxDeviationDegrees;
        public string MaxDeviationWhere = "";

        public bool Ok => !Rejected && Errors.Count == 0;
        public float RepairFraction => TotalValues == 0 ? 0f : RepairedValues / (float)TotalValues;
        public int CorrectedFrameCount { get { int c = 0; foreach (bool b in CorrectedFrames) if (b) c++; return c; } }
        public int RejectedFrameCount => RejectedFrameList.Count;
        public float CorrectedFramePercent => FrameCount == 0 ? 0f : 100f * CorrectedFrameCount / FrameCount;

        public void BeginFrames(int n)
        {
            FrameCount = n;
            if (CorrectedFrames.Length != n) CorrectedFrames = new bool[n];
        }

        public void MarkCorrected(int frame) { if (frame >= 0 && frame < CorrectedFrames.Length) CorrectedFrames[frame] = true; }

        /// <summary>Adds the work of one correction step; steps with the same name are summed.</summary>
        public void AddCorrection(string name, int values, int frames)
        {
            if (values <= 0 && frames <= 0) return;
            foreach (var c in Corrections)
                if (c.name == name) { c.values += values; c.frames += frames; return; }
            Corrections.Add(new CorrectionCount { name = name, values = values, frames = frames });
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Rejected ? "REJECTED" : Errors.Count > 0 ? "ERRORS" : Warnings.Count > 0 || Repairs.Count > 0 || Corrections.Count > 0 ? "OK (corrected)" : "OK");
            sb.Append($": {RepairedValues}/{TotalValues} values repaired");
            foreach (string s in Errors) sb.Append("\n  error: ").Append(s);
            foreach (string s in Repairs) sb.Append("\n  repaired: ").Append(s);
            foreach (string s in Warnings) sb.Append("\n  warning: ").Append(s);
            string q = QuantitativeReport();
            if (q.Length > 0) sb.Append('\n').Append(q);
            return sb.ToString();
        }

        /// <summary>The quantitative validation report as text (empty when the pipeline did not measure anything).</summary>
        public string QuantitativeReport()
        {
            if (FrameCount == 0 && SourceMetrics == null) return "";
            var inv = CultureInfo.InvariantCulture;
            string F(float v, string f = "0.0") => v.ToString(f, inv);
            var sb = new StringBuilder();
            sb.Append("  validation report").Append(string.IsNullOrEmpty(Kind) ? "" : " (" + Kind + ", " + FrameCount + " frames)").Append(":");
            if (HasReference)
                sb.Append($"\n    AI vs procedural pose difference: mean {F(MeanDeviationDegrees)} deg, max joint deviation {F(MaxDeviationDegrees)} deg ({MaxDeviationWhere})");
            if (SourceMetrics != null && FinalMetrics != null)
            {
                string lim(float v) => Limits != null ? $", limit {F(v)}" : "";
                sb.Append($"\n    max foot displacement: {F(FinalMetrics.MaxFootStep)} px/frame (AI source {F(SourceMetrics.MaxFootStep)}{lim(Limits?.FootStep ?? 0)})");
                sb.Append($"\n    max root (hip) displacement: {F(FinalMetrics.MaxRootStep)} px/frame (AI source {F(SourceMetrics.MaxRootStep)}); root-x step limit {F(Limits?.RootXStep ?? 0)}");
                sb.Append($"\n    foot slide while planted: {F(FinalMetrics.MaxStanceSlide)} px/frame (AI source {F(SourceMetrics.MaxStanceSlide)}); leg extension min {FinalMetrics.MinLegExtension:P0} (source {SourceMetrics.MinLegExtension:P0}); largest single-frame spike {F(FinalMetrics.MaxSpike, "0")} deg (source {F(SourceMetrics.MaxSpike, "0")}); weapon gap {F(FinalMetrics.MaxWeaponGap, "0.00")} px; visible upper-body motion {F(FinalMetrics.VisibleMotion)} px (AI source {F(SourceMetrics.VisibleMotion)})");
            }
            if (!string.IsNullOrEmpty(MaxChangeWhere))
                sb.Append($"\n    largest change of a joint by validation/constraints: {F(MaxChangeFromSource)} deg ({MaxChangeWhere})");
            sb.Append($"\n    frames corrected: {CorrectedFrameCount} of {FrameCount} ({F(CorrectedFramePercent, "0")}%), frames rejected: {RejectedFrameCount}");
            if (RejectedFrameCount > 0) sb.Append(" (" + string.Join(", ", RejectedFrameList) + ")");
            foreach (var c in Corrections) sb.Append($"\n    - {c.name}: {c.values} value(s) in {c.frames} frame(s)");
            return sb.ToString();
        }
    }

    /// <summary>The limits the constraint stage worked with (for the report).</summary>
    public sealed class PoseLimitsInfo
    {
        public float FootStep, RootXStep, SpikeDegrees, JointStepDegrees, MinLegExtension;
    }
}
