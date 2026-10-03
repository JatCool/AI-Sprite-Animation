using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Command-line helpers for AI Pose + Rig that never start ComfyUI (used by <see cref="BatchRunner"/>):
    ///   preview     renders a saved pose asset (original + every frame, optional bones) to a PNG sheet and exports the cleaned poses as JSON
    ///   importjson  creates/overwrites a pose asset from pose JSON (hand-made or from another tool); malformed data is validated and rejected like AI output
    ///   openpose    maps an OpenPose keypoint JSON (e.g. ComfyUI POSE_KEYPOINT output) to rig poses and saves them as a pose asset
    /// </summary>
    public static class BatchPoseTools
    {
        /// <returns>The process exit code, or -1 when <paramref name="action"/> is not a tool action.</returns>
        public static int TryRun(string action, Func<string, string> arg, AIAnimationSettings settings, UnityEngine.Object sourceObject, AnimationPreset preset, int frames, int fps)
        {
            if (action != "preview" && action != "importjson" && action != "openpose" && action != "reclean") return -1;
            try
            {
                if (!SourceSprite.TryResolve(sourceObject, settings, out SourceSprite source, out string error)) { Debug.LogError("[AI Sprite Animation] " + error); return 1; }
                var rigAsset = SpriteRigAsset.FindFor(source, settings);
                if (rigAsset == null) { Debug.LogError("[AI Sprite Animation] The sprite has no rig."); return 1; }
                bool facingLeft = settings.facing == SpriteFacing.Left;

                AIPoseAsset asset;
                if (action == "preview" || action == "reclean")
                {
                    asset = AIPoseAsset.FindFor(source, settings, preset.name);
                    if (asset == null) { Debug.LogError($"[AI Sprite Animation] No saved poses for {source.Name} / {preset.name}."); return 1; }
                    if (action == "reclean")
                    {
                        // explicit: rebuild the final poses from the stored AI source poses with the current rig and constraint settings
                        var refPoses = ProceduralRigPoses.Instance.GetPoses(preset.poseKind, asset.sourceFrames.Count, asset.sourceFrames.Count, preset.rigIntensity);
                        asset.Reclean(rigAsset.definition, settings.poseCleanup, refPoses);
                        Debug.Log($"[AI Sprite Animation] POSE TOOL: constraints re-applied to {AssetDatabase.GetAssetPath(asset)}\n{asset.cleanupReport}");
                    }
                }
                else
                {
                    asset = AIPoseImporter.ImportFile(arg("-aiPoseFile"), source, settings, rigAsset, preset, fps);   // throws PoseRejectedException for unusable data
                    Debug.Log($"[AI Sprite Animation] POSE TOOL: saved {AssetDatabase.GetAssetPath(asset)} ({asset.FrameCount} frames)\n{asset.cleanupReport}");
                }

                var provider = new AIPoseProvider(asset, rigAsset.definition, settings.poseCleanup);
                RigPose[] poses = provider.GetPoses(asset.animation, asset.FrameCount, asset.FrameCount, 1f);
                Color32[] pixels = SpriteFrameProcessor.LoadSpritePixels(source, out _, out _);
                var player = PosePreviewPlayer.Build(rigAsset.definition, pixels, poses, asset.fps, facingLeft);
                Debug.Log($"[AI Sprite Animation] POSE PREVIEW: {player.Label}, {player.FrameCount} frames @ {player.Fps} FPS\n{provider.LastReport}");

                // exercise the preview controls exactly as the window does
                player.Playing = false; player.Seek(0); player.Step(-1);
                Debug.Log($"[AI Sprite Animation] POSE PREVIEW: previous from first frame wraps to '{player.Label}'");
                player.Step(1);
                Debug.Log($"[AI Sprite Animation] POSE PREVIEW: next wraps to '{player.Label}'");

                string sheet = arg("-aiPoseSheet");
                if (sheet != null)
                {
                    Color32[] px = player.RenderSheet(Mathf.Max(2, 6), arg("-aiPoseBones") != "0", out int w, out int h);
                    var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    tex.SetPixels32(px);
                    File.WriteAllBytes(sheet, tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                    Debug.Log("[AI Sprite Animation] POSE PREVIEW: sheet written to " + sheet);
                }
                string json = arg("-aiPoseExportJson");
                if (json != null) File.WriteAllText(json, asset.ToJson(true));
                string rawJson = arg("-aiPoseExportRaw");
                if (rawJson != null) File.WriteAllText(rawJson, asset.ToJson(false));

                // how much of this motion is the AI's own: the difference to the procedural animation of the same type
                RigPose[] reference = ProceduralRigPoses.Instance.GetPoses(preset.poseKind, asset.FrameCount, asset.FrameCount, preset.rigIntensity);
                PoseContribution contribution = PoseContribution.Compute(provider.LastCleaned, reference, asset.loop);
                Debug.Log($"[AI Sprite Animation] POSE SOURCE: {asset.MotionLabel} | {contribution.Summary}\n{PoseContribution.JointTable(provider.LastCleaned, reference, asset.loop, contribution.AlignmentShift)}");

                // Grounding as the viewer sees it: the lowest opaque row of the rendered character in every frame (stable = the same row in every frame).
                {
                    int cells = player.Cells;
                    var lowest = new int[player.FrameCount];
                    for (int f = 0; f < lowest.Length; f++)
                    {
                        int low = -1;
                        for (int y = 0; y < cells && low < 0; y++)      // rows are bottom-up in the cell buffer
                            for (int x = 0; x < cells; x++) if (player.Frames[f][y * cells + x].a > 0) { low = y; break; }
                        lowest[f] = low;
                    }
                    Debug.Log($"[AI Sprite Animation] POSE GROUNDING: lowest opaque row per frame (cell rows from the bottom): {string.Join(" ", lowest)}");
                    if (arg("-aiPoseGroundDebug") == "1")
                    {
                        // where the renderer puts the lowest foot corner: the ground shift is rounded to whole pixels, which can leave a sole corner just short of the ground row
                        var sb = new System.Text.StringBuilder("[AI Sprite Animation] POSE GROUNDING DEBUG (ground shift before rounding, lowest corner after rounding, near/far ankle y):");
                        for (int f = 0; f < poses.Length; f++)
                        {
                            var world = RigKinematics.WorldMatrices(rigAsset.definition, poses[f]);
                            float maxBottom = float.MinValue; RigPart who = RigPart.FootNear;
                            foreach (RigPart gp in SpriteRig.GroundParts)
                                for (int y = 0; y < rigAsset.definition.height; y++)
                                    for (int x = 0; x < rigAsset.definition.width; x++)
                                    {
                                        if ((rigAsset.definition.GetMask(x, y) & RigDefinition.Bit(gp)) == 0) continue;
                                        float b = Mathf.Max(world[(int)gp].Apply(x, y + 1f).y, world[(int)gp].Apply(x + 1f, y + 1f).y);
                                        if (b > maxBottom) { maxBottom = b; who = gp; }
                                    }
                            float shift = rigAsset.definition.groundY - maxBottom;
                            Vector2[] j = RigKinematics.JointPositions(rigAsset.definition, poses[f], false, true);
                            sb.Append($"\n  frame {f}: lowest part {who}, shift {shift:0.00} -> {Mathf.Round(shift):0}, corner at {maxBottom + Mathf.Round(shift):0.00} (ground {rigAsset.definition.groundY:0.00}), ankles {j[(int)RigJoint.FootNear].y:0.0}/{j[(int)RigJoint.FootFar].y:0.0}, foot angles {poses[f][RigPart.FootNear] * Mathf.Rad2Deg:0}/{poses[f][RigPart.FootFar] * Mathf.Rad2Deg:0}");
                        }
                        Debug.Log(sb.ToString());
                    }
                }

                // The numbers of the saved validation report, and the same measurements taken again from the stored final poses (they must agree).
                float[][] finalCh = PoseChannels.FromPoses(provider.LastCleaned);
                PoseMetrics measured = PoseMetrics.Measure(rigAsset.definition, finalCh, asset.loop);
                PoseQualityData q = asset.quality;
                Debug.Log($"[AI Sprite Animation] POSE VALIDATION REPORT ({asset.animation}, {asset.FrameCount} frames, saved FINAL poses):\n" +
                          $"  mean AI vs procedural pose difference: {q.meanDeviationDegrees:0.0} deg | max joint deviation: {q.maxDeviationDegrees:0.0} deg ({q.maxDeviationWhere})\n" +
                          $"  max foot displacement: {q.maxFootStep:0.0} px/frame (source {q.sourceMaxFootStep:0.0}; limit {q.footStepLimit:0.0}) | re-measured from stored poses: {measured.MaxFootStep:0.0}\n" +
                          $"  max root (hip) displacement: {q.maxRootStep:0.0} px/frame (source {q.sourceMaxRootStep:0.0}) | re-measured: {measured.MaxRootStep:0.0}\n" +
                          $"  frames corrected: {q.framesCorrected} of {q.frames} ({q.correctedPercent:0}%) | frames rejected: {q.framesRejected}\n" +
                          $"  max change of a joint by validation/constraints: {q.maxChangeFromSourceDegrees:0.0} deg ({q.maxChangeWhere})\n" +
                          $"  foot slide {q.maxStanceSlide:0.0} px | min leg extension {q.minLegExtension:P0} | max spike {q.maxSpikeDegrees:0} deg (source {q.sourceMaxSpikeDegrees:0}) | weapon gap {q.weaponGap:0.00} px (re-measured {measured.MaxWeaponGap:0.00})\n" +
                          $"  hip height range {measured.HipHeightRange:0.0} px | weapon below ground {Mathf.Max(0f, measured.MaxWeaponBelowGround):0.0} px\n" +
                          "  corrections: " + string.Join("; ", q.corrections.ConvertAll(c => $"{c.name} {c.values}/{c.frames}")));
                return 0;
            }
            catch (PoseRejectedException e)
            {
                Debug.Log("[AI Sprite Animation] POSE TOOL REJECTED: " + e.Message);
                return 3;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return 1;
            }
        }
    }
}
