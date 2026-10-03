using System.Collections.Generic;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// The Rotate animation: the character turns from its side view to face the viewer, stands in front for a moment, then turns to the needed side.
    ///   side -> side squashed -> front squashed -> front (held, 1 s by default) -> front squashed -> opposite side squashed -> opposite side (mirrored)
    /// The only pixels used are those of the character's own side sprite (rig-rendered) and of its front-view sprite; the squash is nearest-neighbour sampling,
    /// so no new colour and no partial alpha can appear. Pure math (no Unity assets), tested in tools/posetests.
    /// </summary>
    public static class TurnThroughFront
    {
        /// <summary>How narrow the squashed frames of the transition get (fraction of the width) at the half-way point.</summary>
        public const float NarrowestWidth = 0.55f;

        /// <param name="side">The side view on the cell grid (bottom-left origin), facing the way the character faces now.</param>
        /// <param name="front">The front view on the same grid, placed so the feet are where the side view's feet are.</param>
        /// <param name="holdSeconds">How long the character stays facing the viewer.</param>
        /// <param name="squashFrames">Squashed frames in each half of a turn (1 = two in-between frames per half turn).</param>
        public static List<Color32[]> Build(Color32[] side, Color32[] front, int cells, int fps, float holdSeconds, int squashFrames)
        {
            squashFrames = Mathf.Max(1, squashFrames);
            int hold = Mathf.Max(1, Mathf.RoundToInt(holdSeconds * fps));
            float axis = FeetAxis(side, cells);
            Color32[] opposite = Squash(side, cells, axis, -1f);

            var widths = new float[squashFrames];   // widest first: 1 - 0.45 * k / s
            for (int k = 1; k <= squashFrames; k++) widths[k - 1] = 1f - (1f - NarrowestWidth) * k / squashFrames;

            var frames = new List<Color32[]> { side };
            for (int k = 0; k < squashFrames; k++) frames.Add(Squash(side, cells, axis, widths[k]));                       // the side view narrows
            for (int k = squashFrames - 1; k >= 0; k--) frames.Add(Squash(front, cells, axis, widths[k]));                 // the front view widens
            for (int i = 0; i < hold; i++) frames.Add(front);                                                               // facing the viewer
            for (int k = 0; k < squashFrames; k++) frames.Add(Squash(front, cells, axis, widths[k]));                      // the front view narrows
            for (int k = squashFrames - 1; k >= 0; k--) frames.Add(Squash(opposite, cells, axis, widths[k]));              // the needed side widens
            frames.Add(opposite);
            return frames;
        }

        /// <summary>Horizontal centre (cell columns) of the lowest rows of the side view: where the character stands, the axis it turns about.</summary>
        public static float FeetAxis(Color32[] frame, int cells)
        {
            int low = -1;
            for (int y = 0; y < cells && low < 0; y++)               // rows are bottom-up
                for (int x = 0; x < cells; x++) if (frame[y * cells + x].a > 0) { low = y; break; }
            if (low < 0) return cells * 0.5f;
            int min = cells, max = -1;
            for (int y = low; y < Mathf.Min(cells, low + 3); y++)
                for (int x = 0; x < cells; x++) if (frame[y * cells + x].a > 0) { if (x < min) min = x; if (x > max) max = x; }
            return (min + max + 1) * 0.5f;
        }

        /// <summary>Scales the frame horizontally about <paramref name="axis"/> by <paramref name="factor"/> (negative mirrors), nearest-neighbour only.</summary>
        public static Color32[] Squash(Color32[] frame, int cells, float axis, float factor)
        {
            var output = new Color32[frame.Length];
            for (int x = 0; x < cells; x++)
            {
                int sx = Mathf.FloorToInt(axis + (x + 0.5f - axis) / factor);
                if (sx < 0 || sx >= cells) continue;
                for (int y = 0; y < cells; y++) output[y * cells + x] = frame[y * cells + sx];
            }
            return output;
        }

        /// <summary>
        /// Puts a sprite (bottom-up pixel rows, as the package loads them) on the cell grid the way the Rig renderer places the side sprite: left/bottom is the cell of
        /// the sprite's lower-left corner. A sprite of another width than the side sprite keeps the same ground line and the same horizontal centre.
        /// </summary>
        public static Color32[] PlaceOnGrid(Color32[] sprite, int sw, int sh, int cells, int left, int bottom, int sideWidth)
        {
            var grid = new Color32[cells * cells];
            int offset = (sideWidth - sw) / 2;
            for (int y = 0; y < sh; y++)
            {
                int gy = bottom + y;
                if (gy < 0 || gy >= cells) continue;
                for (int x = 0; x < sw; x++)
                {
                    int gx = left + offset + x;
                    if (gx < 0 || gx >= cells) continue;
                    grid[gy * cells + gx] = sprite[y * sw + x];
                }
            }
            return grid;
        }
    }
}
