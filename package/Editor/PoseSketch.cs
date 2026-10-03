using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// The rough "sketch" skeleton sequence handed to the AI as OpenPose ControlNet guidance when poses are generated. It is derived from the
    /// character's own rig proportions and varied per seed (stride, arm swing, lean) so different seeds give the AI different starting points.
    /// It is a hint, not the result: the saved poses are measured from what the AI produced (see <see cref="RigPoseFitter"/>).
    /// </summary>
    public static class PoseSketch
    {
        /// <param name="count">Number of frames to produce (includes the tail buffer: loops keep cycling, one-shots hold the last pose).</param>
        /// <param name="cycle">Frames per cycle for loops (the requested animation length).</param>
        /// <param name="variation">0 = always the same sketch, 1 = amplitudes vary by up to ±40% per limb group.</param>
        public static RigPose[] Build(string kind, int count, int cycle, float intensity, long seed, float variation)
        {
            var rng = new System.Random(unchecked((int)(seed ^ (seed >> 32))) ^ 0x5bd1e995);
            float Spread() => 1f + variation * 0.4f * (float)(rng.NextDouble() * 2 - 1);
            float legs = Spread(), arms = Spread(), body = Spread();
            float leanShift = variation * 3f * (float)(rng.NextDouble() * 2 - 1) * Mathf.Deg2Rad;

            var poses = new RigPose[count];
            for (int i = 0; i < count; i++)
            {
                RigPose p = ProceduralRigPoses.Evaluate(kind, i, cycle, intensity);
                foreach (RigPart part in new[] { RigPart.LegNearUpper, RigPart.LegNearLower, RigPart.FootNear, RigPart.LegFarUpper, RigPart.LegFarLower, RigPart.FootFar }) p[part] *= legs;
                foreach (RigPart part in new[] { RigPart.ArmNearUpper, RigPart.ArmNearLower, RigPart.ArmFarUpper, RigPart.ArmFarLower, RigPart.Weapon }) p[part] *= arms;
                p[RigPart.Body] = p[RigPart.Body] * body + leanShift;
                poses[i] = p;
            }
            return poses;
        }

        /// <summary>The sketch as OpenPose guidance images on the generation canvas (one PNG per pose).</summary>
        public static System.Collections.Generic.List<byte[]> Render(RigDefinition rig, RigPose[] sketch, PreparedInput input, bool facingLeft)
        {
            var result = new System.Collections.Generic.List<byte[]>(sketch.Length);
            int placeTop = input.Cells - input.BottomCells - input.SrcHeight;
            foreach (RigPose pose in sketch)
            {
                OpenPoseFrame f = OpenPoseMapper.Synthesize(rig, pose);
                var kp = new Vector2[OpenPoseFrame.Count];
                for (int i = 0; i < kp.Length; i++)
                {
                    Vector2 q = f.point[i];
                    float sx = facingLeft ? input.SrcWidth - q.x : q.x;     // the rig frame faces right; the canvas holds the sprite as it is
                    kp[i] = new Vector2((input.LeftCells + sx) * input.Scale, (placeTop + q.y) * input.Scale);
                }
                result.Add(PoseSequenceGenerator.EncodeSkeleton(input.Width, input.Height, kp));
            }
            return result;
        }
    }
}
