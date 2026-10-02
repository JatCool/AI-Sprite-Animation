using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Body parts of the 2D rig. Every opaque pixel of the source sprite belongs to one (or, for overlapping legs, two) of these parts.
    /// "Near" = the side facing the camera (drawn in front), "Far" = the other side.
    /// </summary>
    public enum RigPart
    {
        Body, Head, Hair,
        ArmNearUpper, ArmNearLower, Weapon,
        ArmFarUpper, ArmFarLower,
        LegNearUpper, LegNearLower, FootNear,
        LegFarUpper, LegFarLower, FootFar,
    }

    /// <summary>Joints / pivots of the rig, in sprite pixel coordinates (origin top-left, y down).</summary>
    public enum RigJoint
    {
        Neck, HairPivot, Hip,
        ShoulderNear, ElbowNear, HandNear,
        ShoulderFar, ElbowFar, HandFar,
        KneeNear, FootNear, KneeFar, FootFar,
        WeaponPivot, WeaponTip,
    }

    /// <summary>
    /// The rig of one sprite: joint positions plus the part each pixel belongs to. Pure data (no Unity objects), so it can be used from
    /// the editor window, the generator, tests and batch tools alike. Stored inside a <see cref="SpriteRigAsset"/>.
    /// </summary>
    [Serializable]
    public class RigDefinition
    {
        public const int PartCount = 14;
        public const int JointCount = 15;

        public int width, height;                        // sprite size in pixels
        public Vector2[] joints = new Vector2[JointCount];
        public float groundY;                            // ground line: sprite y (down) the lowest foot pixel rests on
        [SerializeField] private byte[] maskBytes = new byte[0];   // ushort per pixel, little endian; bit i = RigPart i; 0 = no part
        [NonSerialized] private ushort[] mask;

        public RigDefinition() { }

        public RigDefinition(int width, int height)
        {
            this.width = width;
            this.height = height;
            mask = new ushort[width * height];
            maskBytes = new byte[width * height * 2];
        }

        public Vector2 GetJoint(RigJoint j) => joints[(int)j];
        public void SetJoint(RigJoint j, Vector2 p) => joints[(int)j] = p;

        /// <summary>Bit mask of the parts of pixel (x, y) (y down). 0 = the pixel has no part (transparent).</summary>
        public ushort GetMask(int x, int y)
        {
            EnsureMask();
            return mask[y * width + x];
        }

        public void SetMask(int x, int y, ushort value)
        {
            EnsureMask();
            mask[y * width + x] = value;
        }

        public static ushort Bit(RigPart p) => (ushort)(1 << (int)p);

        public bool HasPart(int x, int y, RigPart p) => (GetMask(x, y) & Bit(p)) != 0;

        public int CountPixels(RigPart p)
        {
            EnsureMask();
            int n = 0, bit = 1 << (int)p;
            for (int i = 0; i < mask.Length; i++) if ((mask[i] & bit) != 0) n++;
            return n;
        }

        /// <summary>Writes the in-memory mask back to the serialized bytes (call before saving the asset).</summary>
        public void Flush()
        {
            if (mask == null) return;
            if (maskBytes == null || maskBytes.Length != mask.Length * 2) maskBytes = new byte[mask.Length * 2];
            for (int i = 0; i < mask.Length; i++)
            {
                maskBytes[i * 2] = (byte)(mask[i] & 0xFF);
                maskBytes[i * 2 + 1] = (byte)(mask[i] >> 8);
            }
        }

        /// <summary>Drops the in-memory mask so it is re-read from the serialized bytes (after undo/redo).</summary>
        public void Invalidate() => mask = null;

        public RigDefinition Clone()
        {
            Flush();
            var c = new RigDefinition { width = width, height = height, groundY = groundY, joints = (Vector2[])joints.Clone() };
            c.maskBytes = (byte[])maskBytes.Clone();
            return c;
        }

        public bool IsValidFor(int w, int h) => width == w && height == h && joints != null && joints.Length == JointCount;

        private void EnsureMask()
        {
            if (mask != null && mask.Length == width * height) return;
            mask = new ushort[width * height];
            if (maskBytes != null && maskBytes.Length == mask.Length * 2)
                for (int i = 0; i < mask.Length; i++) mask[i] = (ushort)(maskBytes[i * 2] | (maskBytes[i * 2 + 1] << 8));
        }
    }
}
