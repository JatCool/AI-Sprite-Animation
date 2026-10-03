using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>One frame of OpenPose body-18 keypoints in image coordinates (y down). Confidence 0 = not detected.</summary>
    public sealed class OpenPoseFrame
    {
        public const int Count = 18;
        // 0 nose, 1 neck, 2-4 right shoulder/elbow/wrist, 5-7 left shoulder/elbow/wrist, 8-10 right hip/knee/ankle, 11-13 left hip/knee/ankle, 14-17 eyes and ears.
        public readonly Vector2[] point = new Vector2[Count];
        public readonly float[] confidence = new float[Count];

        public bool Has(int i, float threshold = 0.05f) => confidence[i] >= threshold && !float.IsNaN(point[i].x) && !float.IsNaN(point[i].y);
    }

    /// <summary>
    /// Reads OpenPose keypoint JSON: the ComfyUI POSE_KEYPOINT format (a list of {canvas_width, canvas_height, people:[{pose_keypoints_2d}]}),
    /// the classic OpenPose file format ({people:[...]}) and a bare list of frames. Coordinates in 0..1 are scaled by the canvas size.
    /// </summary>
    public static class OpenPoseJson
    {
        public static List<OpenPoseFrame> Parse(string json)
        {
            object root = MiniJson.Parse(json);
            List<object> frames;
            if (root is List<object> list) frames = list;
            else if (root is Dictionary<string, object> single) frames = new List<object> { single };
            else throw new FormatException("OpenPose JSON must be an object or an array of frames.");

            var result = new List<OpenPoseFrame>(frames.Count);
            foreach (object fo in frames)
            {
                var fd = fo as Dictionary<string, object> ?? throw new FormatException("An OpenPose frame must be an object.");
                float cw = fd.TryGetValue("canvas_width", out object w) && w is double wd ? (float)wd : 1f;
                float ch = fd.TryGetValue("canvas_height", out object h) && h is double hd ? (float)hd : 1f;
                var frame = new OpenPoseFrame();
                if (fd.TryGetValue("people", out object po) && po is List<object> people && people.Count > 0 && people[0] is Dictionary<string, object> person
                    && person.TryGetValue("pose_keypoints_2d", out object kpo) && kpo is List<object> flat && flat.Count >= OpenPoseFrame.Count * 3)
                {
                    // all coordinates <= 1 means normalised
                    bool normalised = true;
                    for (int i = 0; i < OpenPoseFrame.Count; i++)
                        if (flat[i * 3] is double x && flat[i * 3 + 1] is double y && (Math.Abs(x) > 1.5 || Math.Abs(y) > 1.5)) { normalised = false; break; }
                    for (int i = 0; i < OpenPoseFrame.Count; i++)
                    {
                        double x = flat[i * 3] is double xd ? xd : double.NaN, y = flat[i * 3 + 1] is double yd ? yd : double.NaN, c = flat[i * 3 + 2] is double cd ? cd : 0;
                        if (x < 0 && y < 0) c = 0;   // some estimators mark missing joints with -1
                        frame.point[i] = normalised && cw > 0 ? new Vector2((float)x * cw, (float)y * ch) : new Vector2((float)x, (float)y);
                        frame.confidence[i] = (float)c;
                    }
                }
                result.Add(frame);   // a frame without a person has all confidences 0
            }
            return result;
        }
    }
}
