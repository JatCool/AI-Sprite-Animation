using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AISpriteAnimation
{
    /// <summary>
    /// Fills the __PLACEHOLDER__ tokens of a ComfyUI API-format workflow (JSON text).
    /// A token written as a whole JSON string ("__SEED__") becomes a number when it was supplied as one;
    /// a token embedded in a longer string is substituted as escaped text.
    /// </summary>
    public static class ComfyWorkflowBuilder
    {
        public const string InputImage = "__INPUT_IMAGE__";
        public const string ReferenceImage = "__REFERENCE_IMAGE__";
        public const string NoiseType = "__NOISE_TYPE__";
        /// <summary>Number of pose slots in the bundled workflow = the maximum frame count it can generate.</summary>
        public const int MaxFrames = 24;
        public const string BundledWorkflowName = "AnimateDiffSprite";
        public const int BundledWorkflowVersion = 3;
        public const string SdposeModel = "__SDPOSE_MODEL__";
        public const string Prompt = "__PROMPT__";
        public const string NegativePrompt = "__NEGATIVE_PROMPT__";
        public const string FrameCount = "__FRAME_COUNT__";
        public const string Width = "__WIDTH__";
        public const string Height = "__HEIGHT__";
        public const string Fps = "__FPS__";
        public const string Seed = "__SEED__";
        public const string Steps = "__STEPS__";
        public const string Cfg = "__CFG__";
        public const string Denoise = "__DENOISE__";
        public const string MotionScale = "__MOTION_SCALE__";
        public const string IpAdapterWeight = "__IPADAPTER_WEIGHT__";
        public const string IpAdapterPreset = "__IPADAPTER_PRESET__";
        public const string TileStrength = "__TILE_STRENGTH__";
        public const string TileEnd = "__TILE_END__";
        public const string PoseStrength = "__POSE_STRENGTH__";
        public const string PoseControlNetModel = "__POSE_CONTROLNET_MODEL__";
        /// <summary>Per-frame pose images: __POSE_00__ ... __POSE_15__ (file names of uploaded PNGs).</summary>
        public static string PoseSlot(int i) => $"__POSE_{i:00}__";
        public const string TileControlNetModel = "__TILE_CONTROLNET_MODEL__";
        public const string Checkpoint = "__CHECKPOINT__";
        public const string MotionModel = "__MOTION_MODEL__";
        public const string OutputPrefix = "__OUTPUT_PREFIX__";

        private static readonly Regex TokenRegex = new Regex("(\")?(__[A-Z0-9_]+__)(\")?", RegexOptions.Compiled);

        public readonly struct Value
        {
            public readonly string Text;
            public readonly bool IsNumber;
            private Value(string text, bool isNumber) { Text = text; IsNumber = isNumber; }
            public static Value Str(string s) => new Value(s ?? "", false);
            public static Value Num(double d) => new Value(d.ToString("R", CultureInfo.InvariantCulture), true);
            public static Value Num(float f) => new Value(f.ToString("R", CultureInfo.InvariantCulture), true);
            public static Value Num(long l) => new Value(l.ToString(CultureInfo.InvariantCulture), true);
        }

        /// <param name="requireInputImage">The AI Redraw workflow must use the source sprite; the SDPose motion workflow has no input image.</param>
        public static string Build(string template, IDictionary<string, Value> values, bool requireInputImage = true)
        {
            if (string.IsNullOrWhiteSpace(template))
                throw new ComfyUIException("The workflow JSON is empty. Assign a workflow in the AI Animation settings asset.");
            if (requireInputImage && !template.Contains(InputImage))
                throw new ComfyUIException($"The workflow has no {InputImage} placeholder, so the source sprite would never be used.");

            var unknown = new HashSet<string>();
            string result = TokenRegex.Replace(template, m =>
            {
                string token = m.Groups[2].Value;
                if (!values.TryGetValue(token, out Value v))
                {
                    unknown.Add(token);
                    return m.Value;
                }
                bool quoted = m.Groups[1].Success && m.Groups[3].Success;
                if (quoted) return v.IsNumber ? v.Text : "\"" + MiniJson.Escape(v.Text) + "\"";
                // Embedded in a longer string (or a lone quote belongs to the surrounding text): keep the quotes, escape the value.
                return m.Groups[1].Value + MiniJson.Escape(v.Text) + m.Groups[3].Value;
            });

            if (unknown.Count > 0)
                throw new ComfyUIException("The workflow contains unknown placeholders: " + string.Join(", ", unknown));

            try
            {
                MiniJson.Parse(result);
            }
            catch (FormatException e)
            {
                throw new ComfyUIException("The workflow is not valid JSON after substitution: " + e.Message, e);
            }
            return result;
        }
    }
}
