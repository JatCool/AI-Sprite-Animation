using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Brings poses made elsewhere into the pipeline: pose JSON (the format of <see cref="AIPoseAsset.ToJson"/>) or OpenPose keypoint JSON
    /// (ComfyUI POSE_KEYPOINT output of SDPose/DWPose, classic OpenPose files, text-to-motion projected to a side view...).
    /// A keypoint file with enough frames is treated as a clip of a moving person, exactly like a clip the SDPose backend generates itself:
    /// facing and limb labels are repaired and the best animation is cut out (SDPose -> OpenPose JSON -> mapper -> pose asset).
    /// Imported data goes through exactly the same validation and smoothing as the output of the built-in generator, and is saved as an
    /// <see cref="AIPoseAsset"/> so Preview Poses / Build Animation work the same way.
    /// </summary>
    public static class AIPoseImporter
    {
        /// <summary>Throws <see cref="PoseRejectedException"/> when the data cannot be repaired into a usable animation (nothing is saved then).</summary>
        public static AIPoseAsset ImportFile(string path, SourceSprite source, AIAnimationSettings settings, SpriteRigAsset rigAsset, AnimationPreset preset, int fps, bool cutAnimationFromClip = true)
        {
            string text = File.ReadAllText(path);
            object root = MiniJson.Parse(text);
            bool isPoseJson = root is Dictionary<string, object> d && d.ContainsKey("frames") && !d.ContainsKey("people");

            var asset = AIPoseAsset.NewTransient(source, preset.name);
            asset.loop = preset.loop;
            asset.fps = fps;
            if (isPoseJson)
            {
                asset.sourceFrames = AIPoseAsset.ParseJson(text, out _, out int jsonFps, out bool jsonLoop);
                asset.fps = jsonFps; asset.loop = jsonLoop;
                asset.backend = "pose JSON import";
            }
            else
            {
                List<OpenPoseFrame> keypoints = OpenPoseJson.Parse(text);
                bool facingLeft = settings.facing == SpriteFacing.Left;
                if (cutAnimationFromClip && keypoints.Count >= 6)
                {
                    SDPoseCandidate clip = SDPoseAnalysis.Analyze(rigAsset.definition, keypoints, preset.poseKind, asset.loop, preset.frames, facingLeft);
                    if (clip.Extraction == null || !clip.Extraction.Ok) throw new PoseRejectedException(new PoseReport { Rejected = true, Errors = { "No usable " + preset.poseKind + " motion in the keypoint clip: " + clip.Verdict } });
                    asset.SetSource(clip.Extraction.Poses);
                    asset.notes = "Imported clip: " + clip.Verdict + ". " + clip.Extraction.Notes;
                    asset.backend = "OpenPose keypoint clip import (OpenPoseMapper + MotionCycleExtractor)";
                }
                else
                {
                    if (facingLeft) OpenPoseTracking.Mirror(keypoints);
                    asset.SetSource(OpenPoseMapper.Map(rigAsset.definition, keypoints, preset.poseKind, facingLeft, asset.loop));
                    asset.backend = "OpenPose keypoint import (OpenPoseMapper)";
                }
            }
            asset.rigAssetPath = AssetDatabase.GetAssetPath(rigAsset);
            asset.rigSignature = AIPoseAsset.SignatureOf(rigAsset.definition);
            asset.createdUtc = DateTime.UtcNow.ToString("o");
            asset.notes = (asset.notes ?? "") + " Imported from " + Path.GetFileName(path) + ".";
            asset.motionSource = "Imported";
            asset.isAiMotion = false;   // the origin of imported data is unknown to the package
            asset.BuildFinal(rigAsset.definition, settings.poseCleanup, ProceduralRigPoses.Instance.GetPoses(preset.poseKind, asset.sourceFrames.Count, asset.sourceFrames.Count, preset.rigIntensity));   // throws PoseRejectedException: nothing has been written yet
            return asset.SaveAs(source, settings);
        }
    }
}
