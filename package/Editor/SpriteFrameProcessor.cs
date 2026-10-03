using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// The source sprite prepared for generation. The AI works on a padded square canvas; the canvas is a grid of "cells"
    /// (one cell = one pixel of the source sprite = <see cref="Scale"/> canvas pixels). Final frames live on that cell grid.
    /// </summary>
    public sealed class PreparedInput
    {
        public byte[] Png;                 // working canvas sent to the model (flat key-coloured background)
        public byte[] ReferencePng;        // tight crop of the character for IPAdapter (the character fills the view)
        public int Width, Height;          // canvas size in pixels
        public int SrcWidth, SrcHeight;    // original sprite size in sprite pixels
        public int Scale;                  // canvas pixels per sprite pixel
        public int Cells;                  // frame grid size in sprite pixels (Cells x Cells)
        public int LeftCells, BottomCells; // where the original sprite rect sits in the grid (bottom-left origin)
        public Vector2 SourcePivot01;      // source pivot, normalised to the source rect
        public Color32 Key;                // flat background colour
        public Color32[] Palette;          // distinct opaque source colours (null if too many: not pixel art)
        public Color32[] SpritePixels;     // the source sprite's pixels (bottom-left origin), used by the rig

        // Character body geometry on the canvas in image coordinates (origin top-left, y down). Used for pose guidance.
        public float BodyTop, BodyBottom, BodyCenterX;
    }

    /// <summary>A frame on the cell grid (bottom-left origin).</summary>
    public sealed class FrameBuffer
    {
        public Color32[] Pixels;
        public int Width, Height;
    }

    public sealed class FinalFrames
    {
        public List<byte[]> Pngs = new List<byte[]>();
        public Vector2 Pivot01;            // identical for every frame, derived from the source pivot
        public int Width, Height;          // final frame size in sprite pixels
        public RectInt Bounds;             // crop rectangle on the cell grid
    }

    /// <summary>
    /// Sprite -> padded generation canvas, and generated frames -> pixel-art frames:
    /// majority-vote downsampling into the source palette (no blur, no gradients), background keying, despeckling,
    /// union-bounds crop with a consistent pivot.
    /// </summary>
    public static class SpriteFrameProcessor
    {
        private const int MaxCanvas = 1280;
        private const int ReferenceTarget = 512;

        private static readonly Color32[] KeyCandidates =
        {
            new Color32(255, 255, 255, 255),
            new Color32(128, 128, 128, 255),
            new Color32(0, 255, 0, 255),
            new Color32(255, 0, 255, 255),
            new Color32(0, 255, 255, 255),
            new Color32(0, 0, 0, 255),
        };

        /// <summary>The source sprite's pixels (bottom-left origin) cropped to its rect.</summary>
        public static Color32[] LoadSpritePixels(SourceSprite source, out int sw, out int sh)
        {
            Color32[] px = LoadPixels(File.ReadAllBytes(source.AssetPath), out int texW, out int texH);
            var r = source.Rect;
            if (r.xMax > texW || r.yMax > texH) throw new InvalidOperationException("Sprite rect lies outside the texture.");
            sw = r.width; sh = r.height;
            var sprite = new Color32[sw * sh];
            for (int y = 0; y < sh; y++) Array.Copy(px, (r.y + y) * texW + r.x, sprite, y * sw, sw);
            return sprite;
        }

        public static PreparedInput PrepareInput(SourceSprite source, AIAnimationSettings settings)
        {
            Color32[] px = LoadPixels(File.ReadAllBytes(source.AssetPath), out int texW, out int texH);
            var r = source.Rect;
            if (r.xMax > texW || r.yMax > texH) throw new InvalidOperationException("Sprite rect lies outside the texture.");

            int sw = r.width, sh = r.height;
            var sprite = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
                Array.Copy(px, (r.y + y) * texW + r.x, sprite, y * sw, sw);

            Color32 key = PickKeyColor(sprite, settings.keyTolerance);

            // Canvas geometry: padded cell grid, integer upscale, nothing stretched.
            float padded = Mathf.Max(sw, sh) * (1f + 2f * settings.paddingPercent / 100f);
            int canvas = settings.generationSize;
            int scale = Mathf.FloorToInt(canvas / padded);
            if (scale < 1)
            {
                scale = 1;
                canvas = ((Mathf.CeilToInt(padded) + 7) / 8) * 8;
                if (canvas > MaxCanvas)
                    throw new InvalidOperationException($"The sprite ({sw}x{sh}) with {settings.paddingPercent}% padding needs a {canvas}px canvas (max {MaxCanvas}). Lower the padding or use a smaller source.");
            }
            int cells = canvas / scale;
            int left = (cells - sw) / 2, bottom = (cells - sh) / 2;
            int ox = left * scale, oy = bottom * scale;

            var pixels = new Color32[canvas * canvas];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = key;
            for (int sy = 0; sy < sh; sy++)
                for (int sx = 0; sx < sw; sx++)
                {
                    Color32 c = sprite[sy * sw + sx];
                    if (c.a == 0) continue;
                    float a = c.a / 255f;
                    var blended = new Color32(
                        (byte)Mathf.RoundToInt(c.r * a + key.r * (1 - a)),
                        (byte)Mathf.RoundToInt(c.g * a + key.g * (1 - a)),
                        (byte)Mathf.RoundToInt(c.b * a + key.b * (1 - a)), 255);
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            pixels[(oy + sy * scale + dy) * canvas + ox + sx * scale + dx] = blended;
                }

            // Body geometry and tight bounds from the opaque pixels (texture rows run bottom -> top).
            int minRow = int.MaxValue, maxRow = -1, minCol = int.MaxValue, maxCol = -1;
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    if (sprite[y * sw + x].a >= 128)
                    {
                        if (y < minRow) minRow = y;
                        if (y > maxRow) maxRow = y;
                        if (x < minCol) minCol = x;
                        if (x > maxCol) maxCol = x;
                    }
            if (maxRow < 0) throw new InvalidOperationException("The sprite is fully transparent.");
            float bodyTop = canvas - (oy + (maxRow + 1) * scale);
            float bodyBottom = canvas - (oy + minRow * scale);
            int midLo = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.3f), midHi = minRow + Mathf.RoundToInt((maxRow - minRow) * 0.7f);
            double sumX = 0; int count = 0;
            for (int y = midLo; y <= midHi; y++)
                for (int x = 0; x < sw; x++)
                    if (sprite[y * sw + x].a >= 128) { sumX += x + 0.5; count++; }
            float centerX = ox + (float)(sumX / Mathf.Max(1, count)) * scale;

            return new PreparedInput
            {
                Png = EncodePng(pixels, canvas, canvas),
                ReferencePng = BuildReference(sprite, sw, minCol, maxCol, minRow, maxRow, key),
                Width = canvas, Height = canvas,
                SrcWidth = sw, SrcHeight = sh,
                Scale = scale, Cells = cells, LeftCells = left, BottomCells = bottom,
                SourcePivot01 = source.Pivot01,
                SpritePixels = sprite,
                Key = key,
                Palette = ExtractPalette(sprite, 256),
                BodyTop = bodyTop, BodyBottom = bodyBottom, BodyCenterX = centerX,
            };
        }

        // The character alone, centred in a square with a small margin, scaled up with nearest-neighbour.
        // IPAdapter looks at a 224px centre crop, so the full padded canvas would shrink the character to a speck.
        private static byte[] BuildReference(Color32[] sprite, int sw, int minCol, int maxCol, int minRow, int maxRow, Color32 key)
        {
            int bw = maxCol - minCol + 1, bh = maxRow - minRow + 1;
            int margin = Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(bw, bh) * 0.12f));
            int side = Mathf.Max(bw, bh) + 2 * margin;
            int k = Mathf.Max(1, ReferenceTarget / side);
            int size = side * k;
            int offX = (side - bw) / 2, offY = (side - bh) / 2;
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = key;
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                {
                    Color32 c = sprite[(minRow + y) * sw + minCol + x];
                    if (c.a < 128) continue;
                    for (int dy = 0; dy < k; dy++)
                        for (int dx = 0; dx < k; dx++)
                            pixels[((offY + y) * k + dy) * size + (offX + x) * k + dx] = new Color32(c.r, c.g, c.b, 255);
                }
            return EncodePng(pixels, size, size);
        }

        /// <summary>
        /// Converts one generated canvas image to a pixel-art frame on the cell grid. Every cell takes the most common
        /// source-palette colour among its pixels (majority vote, so there is no blur or blending), background cells connected to the
        /// border become transparent, and stray specks / light halos are removed.
        /// </summary>
        public static FrameBuffer ProcessFrame(byte[] generatedPng, PreparedInput input, AIAnimationSettings settings)
        {
            Color32[] gen = LoadPixels(generatedPng, out int gw, out int gh);
            if (gw != input.Width || gh != input.Height) gen = ResizeNearest(gen, gw, gh, input.Width, input.Height);

            int cells = input.Cells, s = input.Scale, w = input.Width;
            float tol2 = settings.keyTolerance * settings.keyTolerance;
            var pal = input.Palette;
            bool snap = settings.snapToSourcePalette && pal != null;
            var lut = new Dictionary<int, int>();

            var colour = new Color32[cells * cells];
            var bgCandidate = new bool[cells * cells];
            var votes = snap ? new int[pal.Length] : null;
            var allVotes = snap ? new int[pal.Length] : null;

            for (int cy = 0; cy < cells; cy++)
                for (int cx = 0; cx < cells; cx++)
                {
                    int bg = 0, total = 0;
                    int r = 0, g = 0, b = 0, nonBg = 0;
                    if (snap) { Array.Clear(votes, 0, votes.Length); Array.Clear(allVotes, 0, allVotes.Length); }
                    for (int dy = 0; dy < s; dy++)
                    {
                        int row = (cy * s + dy) * w + cx * s;
                        for (int dx = 0; dx < s; dx++)
                        {
                            Color32 c = gen[row + dx];
                            total++;
                            bool isBg = Dist2(c, input.Key) <= tol2;
                            if (isBg) bg++;
                            else { r += c.r; g += c.g; b += c.b; nonBg++; }
                            if (snap)
                            {
                                int code = (c.r << 16) | (c.g << 8) | c.b;
                                if (!lut.TryGetValue(code, out int idx)) { idx = NearestPalette(c, pal); lut[code] = idx; }
                                if (!isBg) votes[idx]++;
                                allVotes[idx]++;
                            }
                        }
                    }

                    int ci = cy * cells + cx;
                    bgCandidate[ci] = bg * 2 > total;
                    if (snap)
                    {
                        int best = ArgMax(votes);
                        if (votes[best] == 0) best = ArgMax(allVotes);  // only background-like pixels in this cell
                        colour[ci] = pal[best];
                    }
                    else
                    {
                        colour[ci] = nonBg > 0 ? new Color32((byte)(r / nonBg), (byte)(g / nonBg), (byte)(b / nonBg), 255) : input.Key;
                    }
                }

            // Background = candidate cells connected to the grid border; enclosed key-coloured cells stay (they are part of the character).
            var visited = new bool[cells * cells];
            var stack = new Stack<int>();
            void Push(int x, int y)
            {
                int i = y * cells + x;
                if (visited[i] || !bgCandidate[i]) return;
                visited[i] = true;
                stack.Push(i);
            }
            for (int x = 0; x < cells; x++) { Push(x, 0); Push(x, cells - 1); }
            for (int y = 0; y < cells; y++) { Push(0, y); Push(cells - 1, y); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % cells, y = i / cells;
                colour[i] = new Color32(0, 0, 0, 0);
                if (x > 0) Push(x - 1, y);
                if (x < cells - 1) Push(x + 1, y);
                if (y > 0) Push(x, y - 1);
                if (y < cells - 1) Push(x, y + 1);
            }

            for (int pass = 0; pass < settings.fringeCleanupPasses; pass++) RemoveFringe(colour, cells, cells, input.Key, settings.keyTolerance * 1.7f);
            if (settings.removeSpecklesBelow > 0) RemoveSpeckles(colour, cells, cells, settings.removeSpecklesBelow);
            return new FrameBuffer { Pixels = colour, Width = cells, Height = cells };
        }

        /// <summary>
        /// Crops all frames to the same rectangle: the union of every pixel any frame uses plus the original sprite rect (plus a margin),
        /// so no limb is clipped. The pivot is derived from the source pivot so the character stays put across frames.
        /// </summary>
        public static FinalFrames Finalize(IReadOnlyList<FrameBuffer> frames, PreparedInput input, AIAnimationSettings settings)
        {
            int cells = input.Cells;
            int x0 = input.LeftCells, y0 = input.BottomCells;
            int x1 = x0 + input.SrcWidth - 1, y1 = y0 + input.SrcHeight - 1;

            if (settings.cropToUsedBounds)
            {
                foreach (var f in frames)
                    for (int y = 0; y < cells; y++)
                        for (int x = 0; x < cells; x++)
                            if (f.Pixels[y * cells + x].a > 0)
                            {
                                if (x < x0) x0 = x;
                                if (x > x1) x1 = x;
                                if (y < y0) y0 = y;
                                if (y > y1) y1 = y;
                            }
                int m = settings.cropMarginPixels;
                x0 = Mathf.Max(0, x0 - m); y0 = Mathf.Max(0, y0 - m);
                x1 = Mathf.Min(cells - 1, x1 + m); y1 = Mathf.Min(cells - 1, y1 + m);
            }
            else { x0 = 0; y0 = 0; x1 = cells - 1; y1 = cells - 1; }

            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            var result = new FinalFrames { Width = cw, Height = ch, Bounds = new RectInt(x0, y0, cw, ch) };
            foreach (var f in frames)
            {
                var crop = new Color32[cw * ch];
                for (int y = 0; y < ch; y++) Array.Copy(f.Pixels, (y0 + y) * cells + x0, crop, y * cw, cw);
                result.Pngs.Add(EncodePng(crop, cw, ch));
            }

            float pivotX = input.LeftCells + input.SourcePivot01.x * input.SrcWidth - x0;
            float pivotY = input.BottomCells + input.SourcePivot01.y * input.SrcHeight - y0;
            result.Pivot01 = new Vector2(pivotX / cw, pivotY / ch);
            return result;
        }

        /// <summary>
        /// Clips a generated frame to a silhouette derived from the SOURCE sprite (the rig's posed sprite), grown by <paramref name="radius"/> cells.
        /// Anything the model drew outside it (stray blobs, background bleed, halos) becomes transparent.
        /// </summary>
        public static void MaskToSilhouette(FrameBuffer frame, Color32[] silhouette, int radius)
        {
            int n = frame.Width;
            var grown = new bool[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (silhouette[y * n + x].a == 0) continue;
                    for (int dy = -radius; dy <= radius; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < n && ny < n) grown[ny * n + nx] = true;
                        }
                }
            for (int i = 0; i < grown.Length; i++)
                if (!grown[i]) frame.Pixels[i] = new Color32(0, 0, 0, 0);
        }

        /// <summary>Encodes an image given in top-down row order (row 0 = top) as PNG.</summary>
        public static byte[] EncodePngTopDown(Color32[] topDown, int w, int h)
        {
            var flipped = new Color32[topDown.Length];
            for (int y = 0; y < h; y++) Array.Copy(topDown, y * w, flipped, (h - 1 - y) * w, w);
            return EncodePng(flipped, w, h);
        }

        private static int ArgMax(int[] v)
        {
            int best = 0;
            for (int i = 1; i < v.Length; i++) if (v[i] > v[best]) best = i;
            return best;
        }

        // Background bleeds into the outline when the model blends edges: drop edge pixels that are still close to the key colour.
        private static void RemoveFringe(Color32[] px, int w, int h, Color32 key, float threshold)
        {
            float t2 = threshold * threshold;
            var drop = new List<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (px[i].a == 0 || Dist2(px[i], key) > t2) continue;
                    bool edge = (x > 0 && px[i - 1].a == 0) || (x < w - 1 && px[i + 1].a == 0) || (y > 0 && px[i - w].a == 0) || (y < h - 1 && px[i + w].a == 0);
                    if (edge) drop.Add(i);
                }
            foreach (int i in drop) px[i] = new Color32(0, 0, 0, 0);
        }

        // Removes stray AI specks: detached 8-connected groups that are small and NOT right next to the main body.
        // Small groups touching or adjacent to the body (a hand, a sword tip, a foot) are kept.
        private static void RemoveSpeckles(Color32[] px, int w, int h, int minSize)
        {
            const int NearRadius = 1;   // 'near' = touching or one empty cell away
            var label = new int[px.Length];
            var groups = new List<List<int>>();
            var stack = new Stack<int>();
            for (int start = 0; start < px.Length; start++)
            {
                if (label[start] != 0 || px[start].a == 0) continue;
                var group = new List<int>();
                groups.Add(group);
                label[start] = groups.Count;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    group.Add(i);
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int n = ny * w + nx;
                            if (label[n] != 0 || px[n].a == 0) continue;
                            label[n] = groups.Count;
                            stack.Push(n);
                        }
                }
            }
            if (groups.Count < 2) return;

            int main = 0;
            for (int g = 1; g < groups.Count; g++) if (groups[g].Count > groups[main].Count) main = g;
            int mainLabel = main + 1;

            // Detached groups are dropped when they are small (below minSize) or tiny relative to the body (a quarter of it),
            // unless they sit right next to the body.
            int dropBelow = Mathf.Max(minSize, groups[main].Count / 4);
            for (int g = 0; g < groups.Count; g++)
            {
                if (g == main || groups[g].Count >= dropBelow) continue;
                bool nearMain = false;
                foreach (int i in groups[g])
                {
                    int x = i % w, y = i / w;
                    for (int dy = -NearRadius; dy <= NearRadius && !nearMain; dy++)
                        for (int dx = -NearRadius; dx <= NearRadius && !nearMain; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h && label[ny * w + nx] == mainLabel) nearMain = true;
                        }
                    if (nearMain) break;
                }
                if (!nearMain) foreach (int i in groups[g]) px[i] = new Color32(0, 0, 0, 0);
            }
        }

        // Picks the background colour that the sprite itself uses least (so keying never eats the character):
        // the first candidate (white is the most SD-friendly) with < 2% of opaque pixels near it, else the least-used one.
        private static Color32 PickKeyColor(Color32[] sprite, float tolerance)
        {
            float near2 = (tolerance + 10f) * (tolerance + 10f);
            int opaque = 0;
            foreach (var c in sprite) if (c.a >= 128) opaque++;
            Color32 bestColor = KeyCandidates[0];
            float bestFraction = float.MaxValue;
            foreach (var cand in KeyCandidates)
            {
                int near = 0;
                foreach (var c in sprite)
                    if (c.a >= 128 && Dist2(c, cand) < near2) near++;
                float fraction = near / (float)Mathf.Max(1, opaque);
                if (fraction < 0.02f) return cand;
                if (fraction < bestFraction) { bestFraction = fraction; bestColor = cand; }
            }
            return bestColor;
        }

        private static Color32[] ExtractPalette(Color32[] sprite, int max)
        {
            var set = new HashSet<int>();
            var list = new List<Color32>();
            foreach (var c in sprite)
            {
                if (c.a < 128) continue;
                int code = (c.r << 16) | (c.g << 8) | c.b;
                if (!set.Add(code)) continue;
                if (list.Count >= max) return null; // photo-like art: don't snap
                list.Add(new Color32(c.r, c.g, c.b, 255));
            }
            return list.ToArray();
        }

        private static float Dist2(Color32 a, Color32 b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return dr * dr + dg * dg + db * db;
        }

        // Perceptually weighted ("redmean") colour distance: matches how pixel-art palettes are read better than plain RGB.
        private static int NearestPalette(Color32 c, Color32[] palette)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < palette.Length; i++)
            {
                Color32 p = palette[i];
                float rm = (c.r + p.r) * 0.5f;
                float dr = c.r - p.r, dg = c.g - p.g, db = c.b - p.b;
                float d = (2f + rm / 256f) * dr * dr + 4f * dg * dg + (2f + (255f - rm) / 256f) * db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static Color32[] ResizeNearest(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                    dst[y * dw + x] = src[Mathf.Min(sh - 1, y * sh / dh) * sw + Mathf.Min(sw - 1, x * sw / dw)];
            return dst;
        }

        private static Color32[] LoadPixels(byte[] imageBytes, out int w, out int h)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(imageBytes)) throw new InvalidOperationException("Could not decode image data (PNG/JPG expected).");
                w = tex.width; h = tex.height;
                return tex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        internal static byte[] EncodePng(Color32[] px, int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(px);
                tex.Apply(false);
                return tex.EncodeToPNG();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
