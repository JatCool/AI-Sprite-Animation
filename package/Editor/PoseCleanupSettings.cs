using System;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Configuration of the pose clean-up stage that sits between "AI poses" and the rig renderer: validation/repair, loop closing and smoothing.
    /// Stored on the project's settings asset; a copy is saved with every pose asset so a result can be explained.
    /// </summary>
    [Serializable]
    public class PoseCleanupSettings
    {
        [Tooltip("Master switch for smoothing. Validation and repair of invalid values (NaN, impossible angles) always runs.")]
        public bool smoothingEnabled = true;

        [Range(0f, 0.5f)]
        [Tooltip("How much a frame is pulled towards its neighbours (0 = off, 0.5 = full 1-2-1 blur). Removes jitter from the AI estimate.")]
        public float smoothing = 0.2f;

        [Range(1, 4)]
        [Tooltip("How many times the filter is applied.")]
        public int passes = 1;

        [Min(1f)]
        [Tooltip("Edge-preserving: when a joint turns by more than this many degrees between two frames the change is treated as intentional (an attack swing, a footfall) and is barely smoothed.")]
        public float preserveDegrees = 25f;

        [Range(0f, 1f)]
        [Tooltip("Extra smoothing for root movement and the head/hair, which are the usual sources of jitter (0 = same as the other joints, 1 = twice as much).")]
        public float rootAndHeadSmoothing = 0.6f;

        [Min(0f)]
        [Tooltip("Pixel-art look: snap every joint angle to a multiple of this many degrees (0 = off). 4-5 gives deliberate, stepped poses.")]
        public float stepDegrees = 0f;

        [Min(10f)]
        [Tooltip("Largest allowed change of one joint between two consecutive frames (degrees). Catches 180-degree flips of the pose estimator.")]
        public float maxDegreesPerFrame = 95f;

        [Tooltip("Looping animations: remove the jump between the last and the first frame so the cycle closes seamlessly.")]
        public bool closeLoops = true;

        [Tooltip("A sequence is rejected (not repaired) when more than this fraction of all values had to be repaired.")]
        [Range(0.05f, 1f)] public float rejectRepairFraction = 0.35f;

        [Tooltip("Constraints that only prevent obviously broken poses (single-frame spikes, feet that jump, legs folded into the body, planted feet that slide, a sword that leaves the hand). The AI still controls the motion.")]
        public PoseConstraintSettings constraints = new PoseConstraintSettings();

        public PoseCleanupSettings Clone()
        {
            var c = (PoseCleanupSettings)MemberwiseClone();
            c.constraints = constraints != null ? constraints.Clone() : new PoseConstraintSettings();
            return c;
        }
    }

    /// <summary>
    /// The constraint stage of the pose clean-up (see <see cref="PoseConstraints"/>). Limits come from the rig's own leg length and the animation type;
    /// <see cref="limitScale"/> loosens or tightens all of them at once. The stage never replaces the AI's motion with procedural motion: it only
    /// pulls a pose back inside the limit it broke, and leaves everything that is valid untouched.
    /// </summary>
    [Serializable]
    public class PoseConstraintSettings
    {
        [Tooltip("Master switch of the constraint stage. Validation of invalid values (NaN, impossible angles) always runs.")]
        public bool enabled = true;

        [Range(0.5f, 3f)]
        [Tooltip("1 = default limits, above 1 = looser, below 1 = tighter (foot step, hip step, leg compression, joint spikes).")]
        public float limitScale = 1f;

        [Min(5f)]
        [Tooltip("A joint angle that sticks out of both neighbouring frames by more than this many degrees is a single-frame spike and is pulled back.")]
        public float spikeDegrees = 18f;

        [Min(0.2f)]
        [Tooltip("Same for the root movement (pixels).")]
        public float spikeRootPixels = 1.2f;

        [Tooltip("Idle: if the generated idle moves the upper body by less than the minimum below, the coherent part of the AI's own sway (noise removed) is amplified until it is visible. The gain is reported.")]
        public bool idleMotion = true;

        [Min(0.2f)]
        [Tooltip("Idle: the neck and hands should move at least this many pixels over the loop (about 1 px is the least a pixel-art idle needs to look alive).")]
        public float idleMinMotionPixels = 1.2f;

        [Range(1f, 8f)]
        [Tooltip("Idle: largest amplification of the AI's sway.")]
        public float idleMaxGain = 4f;

        [Range(0f, 1f)]
        [Tooltip("Walk/Run: both legs swing about the same centre line and the weaker leg follows the stronger leg's waveform half a cycle later (0 = off, 1 = exactly mirror-symmetric). Fixes a lopsided stride.")]
        public float gaitSymmetry = 0.75f;
        [Tooltip("Foot lock (idle, walk, attack): the foot that stands on the ground may not skid as fast as a swinging foot, and its sole stays within 5 degrees of the ground.")]
        public bool footLock = true;

        [Range(0f, 1f)]
        [Tooltip("How strongly a planted foot is tilted back to the ground (1 = fully, 0 = off).")]
        public float footLockStrength = 1f;


        [Tooltip("A frame that is so broken that most of its values had to be repaired is rejected and replaced by the interpolation of its neighbours (counted in the report).")]
        public bool rejectBrokenFrames = true;

        [Range(0.2f, 1f)]
        [Tooltip("A frame counts as broken when more than this fraction of its joint angles had to be repaired.")]
        public float brokenFrameFraction = 0.5f;

        [Range(0f, 0.5f)]
        [Tooltip("More rejected frames than this fraction of the sequence reject the whole sequence.")]
        public float maxRejectedFrames = 0.25f;

        public PoseConstraintSettings Clone() => (PoseConstraintSettings)MemberwiseClone();
    }
}
