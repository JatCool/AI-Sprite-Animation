using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>Heuristics used when a rig is created automatically (all values are fractions of the character's height).</summary>
    [Serializable]
    public struct RigAutoParams
    {
        public float neckLine;        // neck position from the top
        public float hipLine;         // hip position from the top
        public float legHalfWidth;    // leg column half width; pixels further out in the leg band stay with the body (cape, weapon tip)
        public float armRadius;       // distance from the arm line within which pixels count as arm
        public bool duplicateLegs;    // leg pixels belong to BOTH legs (full-width overlapping legs), instead of being split down the middle

        public static RigAutoParams Default => new RigAutoParams { neckLine = 0.33f, hipLine = 0.65f, legHalfWidth = 0.12f, armRadius = 0.05f, duplicateLegs = true };
    }

    /// <summary>
    /// Creates a starting rig from the sprite's shape (joints from body proportions) and assigns every pixel to a part from the joints.
    /// It is a starting point: joints and the per-pixel part map can be refined in the Sprite Rig Editor.
    /// </summary>
    public static class RigAutoBuilder
    {
        /// <param name="sprite">Source pixels, bottom-left origin (Unity texture order).</param>
        public static RigDefinition Build(Color32[] sprite, int sw, int sh, RigAutoParams p)
        {
            var rig = new RigDefinition(sw, sh);
            GetBody(sprite, sw, sh, out float top, out float bottom, out float cx, out float rightEdge);
            float H = bottom - top;

            float neckY = top + p.neckLine * H, hipY = top + p.hipLine * H;
            rig.SetJoint(RigJoint.Neck, new Vector2(cx, neckY));
            rig.SetJoint(RigJoint.HairPivot, new Vector2(cx - 0.06f * H, top + (neckY - top) * 0.35f));
            rig.SetJoint(RigJoint.Hip, new Vector2(cx, hipY));

            float shoulderY = neckY + 0.04f * H;
            rig.SetJoint(RigJoint.ShoulderNear, new Vector2(cx + 0.03f * H, shoulderY));
            rig.SetJoint(RigJoint.ElbowNear, new Vector2(cx + 0.04f * H, shoulderY + 0.12f * H));
            rig.SetJoint(RigJoint.HandNear, new Vector2(cx + 0.06f * H, shoulderY + 0.23f * H));
            rig.SetJoint(RigJoint.ShoulderFar, new Vector2(cx - 0.03f * H, shoulderY));
            rig.SetJoint(RigJoint.ElbowFar, new Vector2(cx - 0.04f * H, shoulderY + 0.12f * H));
            rig.SetJoint(RigJoint.HandFar, new Vector2(cx - 0.05f * H, shoulderY + 0.23f * H));

            float legLen = bottom - hipY;
            rig.SetJoint(RigJoint.KneeNear, new Vector2(cx + 0.02f * H, hipY + legLen * 0.48f));
            rig.SetJoint(RigJoint.FootNear, new Vector2(cx + 0.02f * H, bottom - legLen * 0.1f));
            rig.SetJoint(RigJoint.KneeFar, new Vector2(cx - 0.02f * H, hipY + legLen * 0.48f));
            rig.SetJoint(RigJoint.FootFar, new Vector2(cx - 0.02f * H, bottom - legLen * 0.1f));
            rig.groundY = bottom;

            // Weapon: pixels sticking out in front of the body below the shoulders. Pivot = where it leaves the body, tip = furthest point.
            FindProtrusion(sprite, sw, sh, cx + 0.14f * H, shoulderY, out Vector2 grip, out Vector2 tip);
            rig.SetJoint(RigJoint.WeaponPivot, grip);
            rig.SetJoint(RigJoint.WeaponTip, tip);
            if (tip.x - grip.x > 0.08f * H) rig.SetJoint(RigJoint.HandNear, new Vector2(Mathf.Min(grip.x, rightEdge), grip.y));

            AssignParts(rig, sprite, sw, sh, p);
            return rig;
        }

        /// <summary>Assigns every opaque pixel to a part, using the rig's current joints.</summary>
        public static void AssignParts(RigDefinition rig, Color32[] sprite, int sw, int sh, RigAutoParams p)
        {
            GetBody(sprite, sw, sh, out float top, out float bottom, out float cx, out _);
            float H = bottom - top;
            Vector2 neck = rig.GetJoint(RigJoint.Neck), hip = rig.GetJoint(RigJoint.Hip), hairPivot = rig.GetJoint(RigJoint.HairPivot);
            Vector2 wp = rig.GetJoint(RigJoint.WeaponPivot), wt = rig.GetJoint(RigJoint.WeaponTip);
            bool hasWeapon = wt.x - wp.x > 0.08f * H;
            float armR = p.armRadius * H;
            float kneeNearY = rig.GetJoint(RigJoint.KneeNear).y, kneeFarY = rig.GetJoint(RigJoint.KneeFar).y;
            float ankleNearY = rig.GetJoint(RigJoint.FootNear).y, ankleFarY = rig.GetJoint(RigJoint.FootFar).y;

            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (sprite[(sh - 1 - y) * sw + x].a == 0) { rig.SetMask(x, y, 0); continue; }
                    var pos = new Vector2(x + 0.5f, y + 0.5f);
                    ushort mask;

                    if (hasWeapon && pos.x >= wp.x - 0.5f && DistToSegment(pos, wp, wt) <= 0.075f * H && pos.y > neck.y)
                        mask = RigDefinition.Bit(RigPart.Weapon);
                    else if (pos.y < neck.y)
                        mask = pos.x < hairPivot.x ? RigDefinition.Bit(RigPart.Hair) : RigDefinition.Bit(RigPart.Head);
                    else if (pos.y > hip.y && Mathf.Abs(pos.x - cx) <= p.legHalfWidth * H)
                    {
                        ushort near, far;
                        if (pos.y < kneeNearY) near = RigDefinition.Bit(RigPart.LegNearUpper);
                        else if (pos.y < ankleNearY) near = RigDefinition.Bit(RigPart.LegNearLower);
                        else near = RigDefinition.Bit(RigPart.FootNear);
                        if (pos.y < kneeFarY) far = RigDefinition.Bit(RigPart.LegFarUpper);
                        else if (pos.y < ankleFarY) far = RigDefinition.Bit(RigPart.LegFarLower);
                        else far = RigDefinition.Bit(RigPart.FootFar);
                        mask = p.duplicateLegs ? (ushort)(near | far) : (pos.x >= cx ? near : far);
                    }
                    else
                    {
                        mask = RigDefinition.Bit(RigPart.Body);
                        // arms: pixels close to the shoulder-elbow-hand lines (arms usually hide in front of the torso)
                        float dNearU = DistToSegment(pos, rig.GetJoint(RigJoint.ShoulderNear), rig.GetJoint(RigJoint.ElbowNear));
                        float dNearL = DistToSegment(pos, rig.GetJoint(RigJoint.ElbowNear), rig.GetJoint(RigJoint.HandNear));
                        float dFarU = DistToSegment(pos, rig.GetJoint(RigJoint.ShoulderFar), rig.GetJoint(RigJoint.ElbowFar));
                        float dFarL = DistToSegment(pos, rig.GetJoint(RigJoint.ElbowFar), rig.GetJoint(RigJoint.HandFar));
                        float best = armR;
                        if (dNearU <= best) { best = dNearU; mask = RigDefinition.Bit(RigPart.ArmNearUpper); }
                        if (dNearL <= best) { best = dNearL; mask = RigDefinition.Bit(RigPart.ArmNearLower); }
                        if (dFarU <= best) { best = dFarU; mask = RigDefinition.Bit(RigPart.ArmFarUpper); }
                        if (dFarL <= best) { best = dFarL; mask = RigDefinition.Bit(RigPart.ArmFarLower); }
                    }
                    rig.SetMask(x, y, mask);
                }
            rig.Flush();
        }

        /// <summary>Body box of the opaque pixels in sprite coordinates (y down): top edge, bottom edge, torso centre column, right edge of the torso band.</summary>
        public static void GetBody(Color32[] sprite, int sw, int sh, out float top, out float bottom, out float cx, out float rightEdge)
        {
            int minRow = int.MaxValue, maxRow = -1;                      // texture rows, bottom -> top
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    if (sprite[y * sw + x].a >= 128) { if (y < minRow) minRow = y; if (y > maxRow) maxRow = y; }
            if (maxRow < 0) throw new InvalidOperationException("The sprite is fully transparent.");
            top = sh - (maxRow + 1);
            bottom = sh - minRow;
            int lo = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.3f), hi = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.7f);
            double sum = 0; int n = 0; float maxX = 0;
            for (int y = lo; y <= hi; y++)
                for (int x = 0; x < sw; x++)
                    if (sprite[y * sw + x].a >= 128) { sum += x + 0.5; n++; if (x + 0.5f > maxX) maxX = x + 0.5f; }
            cx = (float)(sum / Mathf.Max(1, n));
            rightEdge = maxX;
        }

        // The part of the silhouette sticking out in front of the body (weapon): leftmost column's centroid = grip, furthest pixel = tip.
        private static void FindProtrusion(Color32[] sprite, int sw, int sh, float minX, float minY, out Vector2 grip, out Vector2 tip)
        {
            float gx = float.MaxValue, tx = -1; double gy = 0, ty = 0; int gn = 0, tn = 0;
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (sprite[(sh - 1 - y) * sw + x].a < 128 || x + 0.5f < minX || y + 0.5f < minY) continue;
                    if (x + 0.5f < gx) gx = x + 0.5f;
                    if (x + 0.5f > tx) tx = x + 0.5f;
                }
            if (tx < 0) { grip = tip = new Vector2(minX, minY); return; }
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    if (sprite[(sh - 1 - y) * sw + x].a < 128 || y + 0.5f < minY) continue;
                    if (x + 0.5f >= minX && x + 0.5f < gx + 2f) { gy += y + 0.5f; gn++; }
                    if (x + 0.5f > tx - 2f) { ty += y + 0.5f; tn++; }
                }
            grip = new Vector2(gx, (float)(gy / Mathf.Max(1, gn)));
            tip = new Vector2(tx, (float)(ty / Mathf.Max(1, tn)));
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }
    }
}
