using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Forward kinematics of the rig without rendering: where every joint ends up for a <see cref="RigPose"/>. Uses the same hierarchy,
    /// pivots and grounding rule as <see cref="SpriteRig.Render"/>, so the results line up with rendered frames. Used by pose validation,
    /// the OpenPose mapper and the bone overlay of the pose preview. Pure math.
    /// </summary>
    public static class RigKinematics
    {
        // The part whose transform carries each joint (a joint is a point of one part).
        private static readonly RigPart[] JointPart =
        {
            RigPart.Body,          // Neck
            RigPart.Head,          // HairPivot
            RigPart.Body,          // Hip
            RigPart.Body,          // ShoulderNear
            RigPart.ArmNearUpper,  // ElbowNear
            RigPart.ArmNearLower,  // HandNear
            RigPart.Body,          // ShoulderFar
            RigPart.ArmFarUpper,   // ElbowFar
            RigPart.ArmFarLower,   // HandFar
            RigPart.LegNearUpper,  // KneeNear
            RigPart.LegNearLower,  // FootNear
            RigPart.LegFarUpper,   // KneeFar
            RigPart.LegFarLower,   // FootFar
            RigPart.ArmNearLower,  // WeaponPivot
            RigPart.Weapon,        // WeaponTip
        };

        /// <summary>The parent bone of a part (-1 = the root).</summary>
        public static int ParentOf(RigPart part) => SpriteRig.Parent[(int)part];

        /// <summary>The joint a part rotates about.</summary>
        public static RigJoint PivotOf(RigPart part) => SpriteRig.Pivot[(int)part];

        /// <summary>World matrices per part (before the root translation), y down, character facing right.</summary>
        internal static SpriteRig.Affine[] WorldMatrices(RigDefinition rig, RigPose pose)
        {
            var world = new SpriteRig.Affine[RigDefinition.PartCount];
            for (int p = 0; p < world.Length; p++)
            {
                var local = SpriteRig.Affine.RotateAbout(rig.GetJoint(SpriteRig.Pivot[p]), pose.angle[p]);
                world[p] = SpriteRig.Parent[p] < 0 ? local : world[SpriteRig.Parent[p]].Multiply(local);
            }
            return world;
        }

        /// <summary>
        /// Vertical shift the renderer applies so the lowest leg/foot pixel rests on the ground line (this is the grounding rule),
        /// plus the rounded root translation of the pose. Returned as the final root offset (x, y) in sprite pixels.
        /// </summary>
        public static Vector2 RootOffset(RigDefinition rig, RigPose pose)
        {
            var world = WorldMatrices(rig, pose);
            return RootOffset(rig, pose, world);
        }

        internal static Vector2 RootOffset(RigDefinition rig, RigPose pose, SpriteRig.Affine[] world)
        {
            float groundShift = GroundShift(rig, world, out _);
            return new Vector2(Mathf.Round(pose.root.x), Mathf.Round(groundShift + pose.root.y - pose.hop));
        }

        /// <summary>
        /// The vertical shift (before rounding to whole pixels) that puts the lowest leg/foot pixel on the ground line, and the part that is lowest.
        /// The renderer rounds this shift, so the lowest corner ends up at most half a pixel above or below the ground row.
        /// </summary>
        internal static float GroundShift(RigDefinition rig, SpriteRig.Affine[] world, out RigPart lowestPart)
        {
            float maxBottom = float.MinValue;
            lowestPart = RigPart.FootNear;
            foreach (RigPart gp in SpriteRig.GroundParts)
            {
                var m = world[(int)gp];
                for (int y = 0; y < rig.height; y++)
                    for (int x = 0; x < rig.width; x++)
                    {
                        if ((rig.GetMask(x, y) & RigDefinition.Bit(gp)) == 0) continue;
                        float b = Mathf.Max(m.Apply(x, y + 1f).y, m.Apply(x + 1f, y + 1f).y);
                        if (b > maxBottom) { maxBottom = b; lowestPart = gp; }
                    }
            }
            return maxBottom > float.MinValue ? rig.groundY - maxBottom : 0f;
        }

        /// <summary>
        /// Joint positions in sprite pixels (y down, origin top-left of the source sprite) as the renderer would place them for this pose.
        /// For a left-facing sprite pass <paramref name="facingLeft"/> to get the mirrored positions.
        /// </summary>
        public static Vector2[] JointPositions(RigDefinition rig, RigPose pose, bool facingLeft = false, bool grounded = true)
        {
            var world = WorldMatrices(rig, pose);
            Vector2 root = grounded ? RootOffset(rig, pose, world) : Vector2.zero;
            var result = new Vector2[RigDefinition.JointCount];
            for (int j = 0; j < result.Length; j++)
            {
                Vector2 p = world[(int)JointPart[j]].Apply(rig.joints[j].x, rig.joints[j].y) + root;
                result[j] = facingLeft ? new Vector2(rig.width - p.x, p.y) : p;
            }
            return result;
        }

        /// <summary>Centroid (sprite pixels, y down) of the pixels of a part, or the pivot if the part is empty.</summary>
        public static Vector2 PartCentroid(RigDefinition rig, RigPart part)
        {
            double sx = 0, sy = 0; int n = 0;
            ushort bit = RigDefinition.Bit(part);
            for (int y = 0; y < rig.height; y++)
                for (int x = 0; x < rig.width; x++)
                    if ((rig.GetMask(x, y) & bit) != 0) { sx += x + 0.5; sy += y + 0.5; n++; }
            return n == 0 ? rig.GetJoint(SpriteRig.Pivot[(int)part]) : new Vector2((float)(sx / n), (float)(sy / n));
        }
    }
}
