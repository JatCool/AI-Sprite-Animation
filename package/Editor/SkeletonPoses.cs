using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Procedural side-view skeleton animation (OpenPose body-18 keypoints, image coordinates: y down, +x = facing direction).
    /// Pure math: used to draw the OpenPose ControlNet images and to drive the sprite rig.
    /// Kinds: idle, walk, run, attack, rest (neutral standing pose). <paramref name="swing"/> scales the limb angles.
    /// </summary>
    public static class SkeletonPoses
    {
        // Angles are measured from "straight down"; positive = towards the facing direction (+x).
        private static Vector2 Seg(Vector2 p, float length, float angle) =>
            new Vector2(p.x + length * Mathf.Sin(angle), p.y + length * Mathf.Cos(angle));

        public static Vector2[] Evaluate(string kind, int t, int n, float cx, float top, float bottom, float swing = 1f, float neckLine = 0.15f, float hipLine = 0.52f)
        {
            float H = bottom - top;
            float ph = 2f * Mathf.PI * t / n;
            float D(float deg) => deg * Mathf.Deg2Rad;

            // Proportions as fractions of the body height. Defaults describe a ~7-head-tall character; a chibi sprite
            // (big head, short legs) needs a lower neck line and a higher hip line.
            float headY = top + (neckLine - 0.08f) * H;
            var neck = new Vector2(cx, top + neckLine * H);
            float shoulderY = top + (neckLine + 0.03f) * H;
            var hip = new Vector2(cx, top + hipLine * H);
            float legLen = (1f - hipLine) * 0.5f * H;
            float thigh = legLen, shin = legLen, upperArm = 0.16f * H, foreArm = 0.15f * H;

            var legs = new (float thigh, float shin)[2];
            var arms = new (float upper, float fore)[2];
            float lean = 0f;

            switch (kind)
            {
                case "walk":
                case "run":
                {
                    bool run = kind == "run";
                    float A = D(run ? 42 : 28), kneeFlex = D(run ? 95 : 50), armSwing = D(run ? 48 : 24), elbowFlex = D(run ? 85 : 25);
                    lean = run ? 0.05f * H : 0f;
                    for (int s = 0; s < 2; s++)
                    {
                        float off = s * Mathf.PI;
                        float th = A * Mathf.Sin(ph + off);
                        float flex = D(8) + kneeFlex * Mathf.Max(0f, Mathf.Cos(ph + off));
                        legs[s] = (th, th - flex);
                        float aa = -armSwing * Mathf.Sin(ph + off);
                        arms[s] = (aa, aa + elbowFlex * (0.5f + 0.5f * Mathf.Cos(ph + off + Mathf.PI)));
                    }
                    hip.y += (run ? 0.02f : 0.01f) * H * Mathf.Cos(2f * ph);
                    break;
                }
                case "idle":
                {
                    float b = Mathf.Sin(ph);
                    legs[0] = (D(6) + D(2) * b, D(6)); legs[1] = (D(-7) - D(2) * b, D(-7));
                    arms[0] = (D(5) + D(9) * b, D(14) + D(6) * b); arms[1] = (D(-5) - D(9) * b, D(10));
                    float bob = 0.010f * H * b;
                    hip.y += bob * 0.5f; neck.y += bob; shoulderY += bob;
                    lean = 0.012f * H * Mathf.Sin(ph);
                    break;
                }
                case "rest":
                case "none":
                    break;
                default: // attack
                {
                    float u = Mathf.Min(1f, t / (float)n), a;
                    if (u < 0.35f) a = Mathf.Lerp(D(10), D(-125), u / 0.35f);
                    else if (u < 0.6f) a = Mathf.Lerp(D(-125), D(105), (u - 0.35f) / 0.25f);
                    else a = Mathf.Lerp(D(105), D(10), (u - 0.6f) / 0.4f);
                    legs[0] = (D(18), D(14)); legs[1] = (D(-16), D(-22));
                    arms[0] = (a, a + D(15)); arms[1] = (a * 0.4f - D(10), a * 0.4f + D(10));
                    lean = 0.03f * H * Mathf.Sin(Mathf.PI * Mathf.Min(1f, u / 0.6f));
                    break;
                }
            }

            if (swing != 1f)
            {
                for (int k = 0; k < 2; k++)
                {
                    legs[k] = (legs[k].thigh * swing, legs[k].shin * swing);
                    arms[k] = (arms[k].upper * swing, arms[k].fore * swing);
                }
            }

            neck.x += lean * 0.5f; hip.x -= lean * 0.2f;
            var shoulder = new Vector2(neck.x, shoulderY);
            float headX = neck.x + lean * 0.5f;

            var kp = new Vector2[18];
            kp[1] = neck;
            kp[0] = new Vector2(headX + 0.05f * H, headY);
            kp[14] = new Vector2(headX + 0.025f * H, headY - 0.025f * H);
            kp[15] = new Vector2(headX + 0.045f * H, headY - 0.025f * H);
            kp[16] = new Vector2(headX - 0.015f * H, headY);
            kp[17] = new Vector2(headX - 0.005f * H, headY);
            for (int i = 0; i < 2; i++)
            {
                Vector2 elbow = Seg(shoulder, upperArm, arms[i].upper);
                Vector2 wrist = Seg(elbow, foreArm, arms[i].fore);
                kp[2 + 3 * i] = shoulder; kp[3 + 3 * i] = elbow; kp[4 + 3 * i] = wrist;
                Vector2 knee = Seg(hip, thigh, legs[i].thigh);
                Vector2 ankle = Seg(knee, shin, legs[i].shin);
                kp[8 + 3 * i] = hip; kp[9 + 3 * i] = knee; kp[10 + 3 * i] = ankle;
            }

            // Keep the feet on the ground line.
            float dy = (bottom - 0.02f * H) - Mathf.Max(kp[10].y, kp[13].y);
            for (int i = 0; i < kp.Length; i++) kp[i].y += dy;
            return kp;
        }
    }
}
