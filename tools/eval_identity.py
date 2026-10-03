"""Identity/consistency metrics for generated sprite frames.

Frames are aligned to the source sprite through their (identical) pivot, so the comparison is exact:
  head_match   fraction of the source's HEAD pixels (top 30% of body rows) reproduced exactly (same colour) in the frame
  torso_match  same for the 30-55% rows (torso/arms; allowed to shift +-1px vertically)
  upper_iou    silhouette IoU of the upper 55% of the body vs the source
  consist      mean pairwise agreement between frames in the upper region (temporal stability)
  move         lower-body movement vs frame 0 (should be clearly > 0 for walk/run)
  clipped      opaque pixels touching the frame border
"""
import sys, glob, re, os
import numpy as np
from PIL import Image

folder = sys.argv[1]
src_path = sys.argv[2] if len(sys.argv) > 2 else r"C:\Users\limpo\The-legacy-of-shadows\Assets\Art\Player\player_east.png"
sheet_out = sys.argv[3] if len(sys.argv) > 3 else None
label = sys.argv[4] if len(sys.argv) > 4 else os.path.basename(folder)

fs = sorted(glob.glob(os.path.join(folder, "frame_*.png")))
fr = [np.asarray(Image.open(f).convert("RGBA")).astype(int) for f in fs]
src = np.asarray(Image.open(src_path).convert("RGBA")).astype(int)
SH, SW = src.shape[:2]
H, W = fr[0].shape[:2]

def pivot(meta_file):
    m = open(meta_file, encoding="utf-8-sig").read()
    g = re.search(r"spritePivot: \{x: (\S+), y: (\S+)\}", m)
    return float(g.group(1)), float(g.group(2))

spx, spy = pivot(src_path + ".meta")           # source pivot (normalised, y up)
fpx, fpy = pivot(fs[0] + ".meta")
# pivot position in pixels, bottom-left origin, then offset source -> frame
off_x = round(fpx * W - spx * SW)
off_y_bl = round(fpy * H - spy * SH)           # + = frame has more room below the source's bottom row

def src_to_frame(x, y_td):
    """source top-down pixel -> frame top-down pixel"""
    y_bl = SH - 1 - y_td
    fy_bl = y_bl + off_y_bl
    return x + off_x, H - 1 - fy_bl

alpha_s = src[:, :, 3] > 127
rows = np.where(alpha_s.any(1))[0]; top, bot = rows.min(), rows.max()
body = bot - top + 1
head_rows = range(top, top + int(0.30 * body))
torso_rows = range(top + int(0.30 * body), top + int(0.55 * body))

def match(frame, rws, tol_y=0):
    tot = ok = 0
    for y in rws:
        for x in range(SW):
            if not alpha_s[y, x]: continue
            tot += 1
            best = False
            for dy in range(-tol_y, tol_y + 1):
                fx, fy = src_to_frame(x, y + dy)
                if 0 <= fx < W and 0 <= fy < H and frame[fy, fx, 3] > 0 and (frame[fy, fx, :3] == src[y, x, :3]).all():
                    best = True; break
            ok += best
    return ok / max(1, tot)

def upper_iou(frame):
    inter = uni = 0
    for y in range(top, top + int(0.55 * body)):
        for x in range(SW):
            s = alpha_s[y, x]
            fx, fy = src_to_frame(x, y)
            f = 0 <= fx < W and 0 <= fy < H and frame[fy, fx, 3] > 0
            inter += s and f; uni += s or f
    return inter / max(1, uni)

head = [match(f, head_rows, 0) for f in fr]
torso = [match(f, torso_rows, 1) for f in fr]
iou = [upper_iou(f) for f in fr]

# temporal consistency of the upper region (frames aligned in their own coordinates since pivot is shared)
a = [f[:, :, 3] > 0 for f in fr]
ys = [np.where(m.any(1))[0] for m in a]
t0 = min(int(y.min()) for y in ys); b0 = max(int(y.max()) for y in ys)
ur = slice(t0, t0 + int(0.55 * (b0 - t0 + 1)))
cons = []
for i in range(1, len(fr)):
    d = (np.abs(fr[i][ur] - fr[i - 1][ur]).sum(-1) > 0).mean()
    cons.append(1 - float(d))
lr = slice(b0 - int(0.4 * (b0 - t0 + 1)), b0 + 1)
move = [float(np.abs(f[lr] - fr[0][lr]).mean()) for f in fr]
clipped = [int(m[0].sum() + m[-1].sum() + m[:, 0].sum() + m[:, -1].sum()) for m in a]
pal = {tuple(c) for c in src[alpha_s][:, :3]}
fid = float(np.mean([np.mean([tuple(c) in pal for c in f[m][:, :3]]) if m.any() else 0 for f, m in zip(fr, a)]))

print(f"[{label}] {len(fs)}f {W}x{H} | head_match {np.mean(head):.2f} | torso_match {np.mean(torso):.2f} | upper_iou {np.mean(iou):.2f} | "
      f"upper_consist {np.mean(cons):.2f} | move_max {max(move):.1f} | clipped {sum(clipped)} | palette_ok {fid:.2f}")

if sheet_out:
    z = 4
    sheet = Image.new("RGBA", ((len(fs) + 1) * (W * z + 4), H * z), (120, 160, 120, 255))
    # source (aligned) as first cell for a direct visual comparison
    canvas = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    srcim = Image.open(src_path).convert("RGBA")
    px_, py_ = src_to_frame(0, 0)
    canvas.alpha_composite(srcim, (px_, py_)) if 0 <= px_ < W and 0 <= py_ < H else None
    sheet.alpha_composite(canvas.resize((W * z, H * z), Image.NEAREST), (0, 0))
    for i, f in enumerate(fs):
        sheet.alpha_composite(Image.open(f).convert("RGBA").resize((W * z, H * z), Image.NEAREST), ((i + 1) * (W * z + 4), 0))
    sheet.save(sheet_out)
