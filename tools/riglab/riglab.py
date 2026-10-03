import sys, struct, subprocess, os
import numpy as np
from PIL import Image, ImageDraw

SRC = r"C:\Users\limpo\The-legacy-of-shadows\Assets\Art\Player\player_east.png"
EXE = r"C:\AI\riglab\bin\Release\net8.0\riglab.exe"
anim = sys.argv[1]; frames = int(sys.argv[2]); k = sys.argv[3]; out = sys.argv[4]
joints = sys.argv[5] if len(sys.argv) > 5 else ""
cells = 72

im = Image.open(SRC).convert("RGBA"); w, h = im.size
a = np.asarray(im)[::-1]
open(r"C:\AI\riglab\src.raw", "wb").write(struct.pack("ii", w, h) + a.tobytes())
args = [EXE, r"C:\AI\riglab\src.raw", r"C:\AI\riglab\out.raw", anim, str(frames), str(frames), k, str(cells), "0"]
if joints: args.append(joints)
r = subprocess.run(args, capture_output=True, text=True)
if "-v" in sys.argv: print(r.stdout)
if r.returncode != 0: print(r.stdout, r.stderr[:500]); sys.exit(1)
d = open(r"C:\AI\riglab\out.raw", "rb").read()
n, f = struct.unpack("ii", d[:8])
arr = np.frombuffer(d[8:], dtype=np.uint8).reshape(f, n, n, 4)[:, ::-1]
z = 3
sheet = Image.new("RGBA", ((f + 1) * (n * z + 4), n * z), (120, 160, 120, 255))
ref = Image.new("RGBA", (n, n), (0, 0, 0, 0)); ref.alpha_composite(im, ((n - w) // 2, (n - h) // 2))
sheet.alpha_composite(ref.resize((n * z, n * z), Image.NEAREST), (0, 0))
for i in range(f):
    sheet.alpha_composite(Image.fromarray(arr[i], "RGBA").resize((n * z, n * z), Image.NEAREST), ((i + 1) * (n * z + 4), 0))
sheet.save(out)

# part map visualisation
pd = open(r"C:\AI\riglab\out.raw.parts", "rb").read()
pw, ph = struct.unpack("ii", pd[:8]); pm = np.frombuffer(pd[8:], dtype=np.uint16).reshape(ph, pw)
cols = [(200,60,60),(60,160,220),(240,200,40),(90,200,90),(40,120,40),(250,120,0),(180,90,200),(110,40,140),(0,200,200),(0,120,140),(0,70,90),(230,100,170),(150,60,100),(100,40,70)]
vis = Image.new("RGBA", (pw * 10, ph * 10), (30, 30, 30, 255))
dr = ImageDraw.Draw(vis)
for y in range(ph):
    for x in range(pw):
        m = int(pm[y, x])
        if not m: continue
        bits = [i for i in range(14) if m >> i & 1]
        c = cols[bits[0]]
        dr.rectangle([x * 10, y * 10, x * 10 + 9, y * 10 + 9], fill=c + (255,))
        if len(bits) > 1: dr.rectangle([x * 10 + 3, y * 10 + 3, x * 10 + 6, y * 10 + 6], fill=cols[bits[1]] + (255,))
vis.save(out.replace(".png", "_parts.png"))
print("saved", out)
