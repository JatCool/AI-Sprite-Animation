using System.Collections.Generic;

namespace AISpriteAnimation
{
    /// <summary>Everything a backend needs to generate frames. Backend-agnostic.</summary>
    public sealed class GenerationRequest
    {
        public string CharacterName;
        public string AnimationName;
        public byte[] InputPng;              // prepared source sprite (already sized to Width x Height)
        public int Width, Height;
        public int FrameCount;               // frames to generate
        public int Fps;
        public string Prompt;
        public string NegativePrompt;
        public long Seed;
        public AnimationPreset Preset;       // tuning values (steps, cfg, denoise, ...)
        public string OutputPrefix;          // unique per run

        /// <summary>Additional PNGs the workflow needs (e.g. per-frame pose images). Key = workflow placeholder, e.g. "__POSE_00__".</summary>
        public Dictionary<string, byte[]> ExtraImages = new Dictionary<string, byte[]>();
    }
}
