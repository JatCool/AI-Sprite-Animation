using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace AISpriteAnimation
{
    /// <summary>One frame of saved pose data: a rotation per rig part (degrees, relative to the parent bone) plus the root offset.</summary>
    [Serializable]
    public class PoseFrameData
    {
        [Tooltip("Rotation per RigPart in the enum order (Body, Head, Hair, ArmNearUpper, ...), degrees, counter-clockwise on screen = positive.")]
        public float[] degrees = new float[RigDefinition.PartCount];
        public float rootX, rootY, hop;

        public static PoseFrameData From(RigPose pose)
        {
            var d = new PoseFrameData { rootX = pose.root.x, rootY = pose.root.y, hop = pose.hop };
            for (int i = 0; i < d.degrees.Length; i++) d.degrees[i] = pose.angle[i] * Mathf.Rad2Deg;
            return d;
        }

        public RigPose ToPose()
        {
            var p = new RigPose { root = new Vector2(rootX, rootY), hop = hop };
            for (int i = 0; i < RigDefinition.PartCount; i++) p.angle[i] = (degrees != null && i < degrees.Length ? degrees[i] : 0f) * Mathf.Deg2Rad;
            return p;
        }
    }

    /// <summary>The quantitative validation report of a pose asset, in a form that is saved with it.</summary>
    [Serializable]
    public class PoseQualityData
    {
        public bool valid;
        public string kind;
        public int frames, framesCorrected, framesRejected;
        public float correctedPercent;
        [Tooltip("Mean and maximum difference of a joint angle to the procedural animation of the same type (degrees).")]
        public float meanDeviationDegrees, maxDeviationDegrees;
        public string maxDeviationWhere;
        [Tooltip("Largest rendered displacement of a foot / of the hip between two consecutive frames (pixels): final poses and the AI source.")]
        public float maxFootStep, sourceMaxFootStep, footStepLimit, maxRootStep, sourceMaxRootStep, rootStepLimit;
        [Tooltip("Largest change of one joint angle made by validation and constraints (degrees).")]
        public float maxChangeFromSourceDegrees;
        public string maxChangeWhere;
        public float maxStanceSlide, sourceMaxStanceSlide, minLegExtension, sourceMinLegExtension, maxSpikeDegrees, sourceMaxSpikeDegrees, weaponGap;
        public List<CorrectionCount> corrections = new List<CorrectionCount>();

        public static PoseQualityData From(PoseReport r)
        {
            var q = new PoseQualityData
            {
                valid = r.FinalMetrics != null, kind = r.Kind, frames = r.FrameCount, framesCorrected = r.CorrectedFrameCount, framesRejected = r.RejectedFrameCount,
                correctedPercent = r.CorrectedFramePercent, meanDeviationDegrees = r.MeanDeviationDegrees, maxDeviationDegrees = r.MaxDeviationDegrees, maxDeviationWhere = r.MaxDeviationWhere,
                maxChangeFromSourceDegrees = r.MaxChangeFromSource, maxChangeWhere = r.MaxChangeWhere,
            };
            if (r.FinalMetrics != null && r.SourceMetrics != null)
            {
                q.maxFootStep = r.FinalMetrics.MaxFootStep; q.sourceMaxFootStep = r.SourceMetrics.MaxFootStep;
                q.maxRootStep = r.FinalMetrics.MaxRootStep; q.sourceMaxRootStep = r.SourceMetrics.MaxRootStep;
                q.maxStanceSlide = r.FinalMetrics.MaxStanceSlide; q.sourceMaxStanceSlide = r.SourceMetrics.MaxStanceSlide;
                q.minLegExtension = r.FinalMetrics.MinLegExtension; q.sourceMinLegExtension = r.SourceMetrics.MinLegExtension;
                q.maxSpikeDegrees = r.FinalMetrics.MaxSpike; q.sourceMaxSpikeDegrees = r.SourceMetrics.MaxSpike; q.weaponGap = r.FinalMetrics.MaxWeaponGap;
            }
            if (r.Limits != null) { q.footStepLimit = r.Limits.FootStep; q.rootStepLimit = r.Limits.RootXStep; }
            foreach (var c in r.Corrections) q.corrections.Add(new CorrectionCount { name = c.name, values = c.values, frames = c.frames });
            return q;
        }
    }

    /// <summary>
    /// AI-generated animation poses for one character and one animation type, saved as an asset (e.g. <c>player_east_Walk_AIPose.asset</c>).
    /// It holds only motion (joint rotations), never pixels, so the animation can be rebuilt from the original sprite at any time without running the AI again.
    /// <see cref="frames"/> are the FINAL poses (after validation and constraints); that is what is built. The poses as the AI delivered them are kept in
    /// <see cref="sourceFrames"/> for diagnostics and for an explicit <see cref="Reclean"/> after the rig or the constraint settings changed.
    /// </summary>
    public class AIPoseAsset : ScriptableObject
    {
        public const string FileSuffix = "_AIPose.asset";

        [Header("Animation")]
        public string animation = "Walk";
        public bool loop = true;
        public int fps = 12;

        [Header("Source")]
        [Tooltip("Sprite the poses were generated for (asset path).")]
        public string sourceAssetPath;
        public string spriteName;
        [Tooltip("Rig asset used while generating (the poses are rig-independent angles, so another rig can be used to build).")]
        public string rigAssetPath;
        [Tooltip("Fingerprint of the rig at generation time; a different value only means the rig was edited since.")]
        public string rigSignature;

        [Header("Pose data")]
        [Tooltip("The FINAL poses: the AI motion after validation, retargeting constraints and smoothing. This is what Build Animation and the preview use, unchanged.")]
        public List<PoseFrameData> frames = new List<PoseFrameData>();
        [FormerlySerializedAs("rawFrames")]
        [Tooltip("The AI poses as they came out of the backend, BEFORE validation and constraints. Kept only for diagnostics and for Re-apply Constraints; never used to build an animation.")]
        public List<PoseFrameData> sourceFrames = new List<PoseFrameData>();
        [Tooltip("The clean-up settings that produced the final poses.")]
        public PoseCleanupSettings cleanup = new PoseCleanupSettings();
        [TextArea(2, 8)] public string cleanupReport;
        [Tooltip("Quantitative validation report of the final poses (corrections, foot/root displacement, difference to the procedural animation).")]
        public PoseQualityData quality = new PoseQualityData();

        [Header("Generation metadata")]
        public string backend;
        [Tooltip("SDPose, SketchEvidenceLegacy, ProceduralFallback, or Imported.")]
        public string motionSource;
        [Tooltip("True only if the motion was produced by an AI model (SDPose). Procedural fallback, the legacy sketch backend and imports are not.")]
        public bool isAiMotion;
        [Tooltip("Mean difference (degrees) between the saved poses and the procedural animation of the same type, at the best alignment.")]
        public float contributionDegrees;
        [Tooltip("Negligible (< 5 deg), Moderate (5-15) or Significant (> 15) for AI motion; NotAI for the other sources.")]
        public string contributionLevel;
        [TextArea(2, 4)] public string contributionSummary;
        public string createdUtc;
        public long seed;
        public string prompt;
        public float guidanceStrength;
        public float generationSeconds;
        public float fitError;
        [TextArea(2, 6)] public string notes;

        public int FrameCount => frames.Count > 0 ? frames.Count : sourceFrames.Count;

        /// <summary>What to call this motion in the UI: never labels procedural motion as AI.</summary>
        public string MotionLabel
        {
            get
            {
                if (string.IsNullOrEmpty(motionSource) || motionSource == "Imported") return "Imported poses";
                if (isAiMotion) return $"AI-generated ({backend}); contribution: {(string.IsNullOrEmpty(contributionLevel) ? "?" : contributionLevel.ToLowerInvariant())}";
                return $"NOT AI motion ({backend})";
            }
        }

        public static string FileNameFor(SourceSprite source, string animation) => $"{source.Name}_{SourceSprite.SanitizeName(animation)}{FileSuffix}";

        public static string AssetPathFor(SourceSprite source, AIAnimationSettings settings, string animation)
        {
            string folder = string.IsNullOrWhiteSpace(settings.poseAssetFolder) ? Path.GetDirectoryName(source.AssetPath).Replace("\\", "/") : settings.poseAssetFolder.TrimEnd('/');
            return $"{folder}/{FileNameFor(source, animation)}";
        }

        /// <summary>The saved poses for this sprite and animation type, or null.</summary>
        public static AIPoseAsset FindFor(SourceSprite source, AIAnimationSettings settings, string animation)
        {
            var direct = AssetDatabase.LoadAssetAtPath<AIPoseAsset>(AssetPathFor(source, settings, animation));
            if (direct != null && direct.Matches(source, animation)) return direct;
            foreach (string guid in AssetDatabase.FindAssets("t:AIPoseAsset"))
            {
                var a = AssetDatabase.LoadAssetAtPath<AIPoseAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (a != null && a.Matches(source, animation)) return a;
            }
            return null;
        }

        public bool Matches(SourceSprite source, string anim) =>
            sourceAssetPath == source.AssetPath && (spriteName ?? "") == (source.IsSliced ? source.SpriteName : "")
            && string.Equals(animation, anim, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A new pose asset that exists only in memory. Fill it, validate it (<see cref="Recompute"/>) and only then <see cref="SaveAs"/>:
        /// a rejected or failed run must never leave a broken file behind or damage the poses saved earlier.
        /// </summary>
        public static AIPoseAsset NewTransient(SourceSprite source, string animation)
        {
            var asset = CreateInstance<AIPoseAsset>();
            asset.animation = animation;
            asset.sourceAssetPath = source.AssetPath;
            asset.spriteName = source.IsSliced ? source.SpriteName : "";
            return asset;
        }

        /// <summary>Writes this transient asset to the project (overwriting an existing pose asset in place, which keeps its GUID) and returns the project asset.</summary>
        public AIPoseAsset SaveAs(SourceSprite source, AIAnimationSettings settings)
        {
            string path = AssetPathFor(source, settings, animation);
            var existing = AssetDatabase.LoadAssetAtPath<AIPoseAsset>(path);
            AIPoseAsset result;
            if (existing == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AssetDatabase.CreateAsset(this, path);
                result = this;
            }
            else
            {
                string keepName = existing.name;
                EditorUtility.CopySerialized(this, existing);
                existing.name = keepName;
                DestroyImmediate(this);
                result = existing;
            }
            result.Save();
            return result;
        }

        /// <summary>Stores the AI poses as delivered by the backend (before validation). They are not what gets built: see <see cref="BuildFinal"/>.</summary>
        public void SetSource(IReadOnlyList<RigPose> poses)
        {
            sourceFrames = new List<PoseFrameData>(poses.Count);
            foreach (var p in poses) sourceFrames.Add(PoseFrameData.From(p));
        }

        public RigPose[] SourcePoses()
        {
            var poses = new RigPose[sourceFrames.Count];
            for (int i = 0; i < poses.Length; i++) poses[i] = sourceFrames[i].ToPose();
            return poses;
        }

        /// <summary>The final (validated, constrained, smoothed) poses.</summary>
        public RigPose[] FinalPoses()
        {
            var poses = new RigPose[frames.Count];
            for (int i = 0; i < poses.Length; i++) poses[i] = frames[i].ToPose();
            return poses;
        }

        /// <summary>
        /// Runs validation, the constraint stage and smoothing on the source poses and stores the outcome as the final poses together with the quantitative report.
        /// Throws <see cref="PoseRejectedException"/> (nothing is changed then).
        /// </summary>
        /// <param name="reference">The procedural animation of the same type (for the AI-vs-procedural numbers of the report), or null.</param>
        public PoseCleanup.Result BuildFinal(RigDefinition rig, PoseCleanupSettings settings, IReadOnlyList<RigPose> reference = null)
        {
            var result = PoseCleanup.Run(rig, SourcePoses(), animation, loop, settings, reference);
            frames = new List<PoseFrameData>(result.Poses.Length);
            foreach (var p in result.Poses) frames.Add(PoseFrameData.From(p));
            cleanup = settings.Clone();
            cleanupReport = result.Report.ToString();
            quality = PoseQualityData.From(result.Report);
            return result;
        }

        /// <summary>Explicit "Re-apply constraints": rebuilds the final poses from the source poses with the current settings and rig, and saves.</summary>
        public PoseCleanup.Result Reclean(RigDefinition rig, PoseCleanupSettings settings, IReadOnlyList<RigPose> reference = null)
        {
            if (sourceFrames.Count == 0) throw new InvalidOperationException($"'{name}' has no source poses to re-apply the constraints to.");
            var result = BuildFinal(rig, settings, reference);
            rigSignature = SignatureOf(rig);
            Save();
            return result;
        }

        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }

        /// <summary>A short fingerprint of a rig (size, joints, pixel count per part).</summary>
        public static string SignatureOf(RigDefinition rig)
        {
            unchecked
            {
                int h = rig.width * 73856093 ^ rig.height * 19349663 ^ Mathf.RoundToInt(rig.groundY * 10) * 83492791;
                foreach (var j in rig.joints) h = h * 31 + Mathf.RoundToInt(j.x * 10) * 7 + Mathf.RoundToInt(j.y * 10);
                for (int p = 0; p < RigDefinition.PartCount; p++) h = h * 31 + rig.CountPixels((RigPart)p);
                return h.ToString("x8");
            }
        }

        // ---------------------------------------------------------------- JSON (human readable / exchangeable)

        /// <summary>
        /// The pose data as JSON using the rig's own part names, e.g.
        /// <c>{"animation":"Walk","fps":12,"frames":[{"root":{"x":0,"y":0,"hop":0},"Body":{"rotation":-1.5},"ArmNearUpper":{"rotation":20}, ...}]}</c>.
        /// </summary>
        public string ToJson(bool cleaned = true)
        {
            var list = cleaned && frames.Count > 0 ? frames : sourceFrames;
            var inv = CultureInfo.InvariantCulture;
            string F(float v) => float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("0.###", inv);
            var sb = new StringBuilder();
            sb.Append("{\n  \"animation\": \"").Append(MiniJson.Escape(animation)).Append("\",\n  \"fps\": ").Append(fps)
              .Append(",\n  \"loop\": ").Append(loop ? "true" : "false").Append(",\n  \"frames\": [\n");
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append("    {\"root\": {\"x\": ").Append(F(list[i].rootX)).Append(", \"y\": ").Append(F(list[i].rootY)).Append(", \"hop\": ").Append(F(list[i].hop)).Append('}');
                for (int p = 0; p < RigDefinition.PartCount; p++)
                    sb.Append(", \"").Append(((RigPart)p).ToString()).Append("\": {\"rotation\": ").Append(F(list[i].degrees[p])).Append('}');
                sb.Append(i < list.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Parses pose JSON (see <see cref="ToJson"/>) into raw frames. Joints that are missing or null become NaN, which validation repairs
        /// or rejects; nothing malformed is silently accepted here.
        /// </summary>
        public static List<PoseFrameData> ParseJson(string json, out string animationName, out int fpsValue, out bool loopValue)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object> ?? throw new FormatException("Pose JSON must be an object.");
            animationName = root.TryGetValue("animation", out object a) ? Convert.ToString(a, CultureInfo.InvariantCulture) : "";
            fpsValue = root.TryGetValue("fps", out object f) && f is double fd ? (int)fd : 12;
            loopValue = !root.TryGetValue("loop", out object l) || !(l is bool lb) || lb;
            if (!(root.TryGetValue("frames", out object fr) && fr is List<object> frameList)) throw new FormatException("Pose JSON has no 'frames' array.");

            float Num(object o) => o is double d ? (float)d : float.NaN;
            var result = new List<PoseFrameData>(frameList.Count);
            foreach (object fo in frameList)
            {
                var d = new PoseFrameData();
                var fdict = fo as Dictionary<string, object>;
                for (int p = 0; p < d.degrees.Length; p++) d.degrees[p] = float.NaN;
                if (fdict != null)
                {
                    for (int p = 0; p < RigDefinition.PartCount; p++)
                        if (fdict.TryGetValue(((RigPart)p).ToString(), out object po))
                            d.degrees[p] = po is Dictionary<string, object> pd && pd.TryGetValue("rotation", out object r) ? Num(r) : Num(po);
                    if (fdict.TryGetValue("root", out object ro) && ro is Dictionary<string, object> rd)
                    {
                        d.rootX = rd.TryGetValue("x", out object x) ? Num(x) : 0f;
                        d.rootY = rd.TryGetValue("y", out object y) ? Num(y) : 0f;
                        d.hop = rd.TryGetValue("hop", out object h) ? Num(h) : 0f;
                    }
                }
                else for (int p = 0; p < d.degrees.Length; p++) d.degrees[p] = float.NaN;
                result.Add(d);
            }
            return result;
        }
    }
}
