using AISpriteAnimation;
using UnityEngine;
// usage: riglab src.raw out.raw anim frames cycle intensity cells facingLeft [joints.txt]
// joints.txt lines: '<Joint> x y', 'ground y', and 'remap <FromPart> <ToPart> x0 y0 x1 y1' (pixels of FromPart inside the box (top-left origin, inclusive) become ToPart: a rig refinement without the editor)
var a = args;
var raw = File.ReadAllBytes(a[0]);
int sw = BitConverter.ToInt32(raw, 0), sh = BitConverter.ToInt32(raw, 4);
var sprite = new Color32[sw * sh];
for (int i = 0; i < sprite.Length; i++) sprite[i] = new Color32(raw[8 + i*4], raw[9 + i*4], raw[10 + i*4], raw[11 + i*4]);
string anim = a[2]; int frames = int.Parse(a[3]), cycle = int.Parse(a[4]);
float k = float.Parse(a[5], System.Globalization.CultureInfo.InvariantCulture); int cells = int.Parse(a[6]); bool left = a[7] == "1";
var rig = RigAutoBuilder.Build(sprite, sw, sh, RigAutoParams.Default);
if (a.Length > 8 && File.Exists(a[8]))
{
    foreach (var line in File.ReadAllLines(a[8]))
    {
        var t = line.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 3 && Enum.TryParse<RigJoint>(t[0], out var j)) rig.SetJoint(j, new Vector2(float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture)));
        if (t.Length == 2 && t[0] == "ground") rig.groundY = float.Parse(t[1], System.Globalization.CultureInfo.InvariantCulture);
    }
    RigAutoBuilder.AssignParts(rig, sprite, sw, sh, RigAutoParams.Default);
    foreach (var line in File.ReadAllLines(a[8]))
    {
        var t = line.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 7 && t[0] == "remap" && Enum.TryParse<RigPart>(t[1], out var from) && Enum.TryParse<RigPart>(t[2], out var to))
        {
            int x0 = int.Parse(t[3]), y0 = int.Parse(t[4]), x1 = int.Parse(t[5]), y1 = int.Parse(t[6]);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                if (rig.HasPart(x, y, from)) rig.SetMask(x, y, (ushort)(1 << (int)to));
        }
    }
}
var poses = ProceduralRigPoses.Instance.GetPoses(anim, frames, cycle, k);
int l = (cells - sw) / 2, b = (cells - sh) / 2;
var res = SpriteRig.Render(rig, sprite, poses, cells, l, b, left);
using var o = new BinaryWriter(File.Create(a[1]));
o.Write(cells); o.Write(frames);
foreach (var f in res) foreach (var c in f) { o.Write(c.r); o.Write(c.g); o.Write(c.b); o.Write(c.a); }
// part map dump for inspection
using var pm = new BinaryWriter(File.Create(a[1] + ".parts"));
pm.Write(sw); pm.Write(sh);
for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) pm.Write(rig.GetMask(x, y));
Console.WriteLine($"ok {frames} frames; joints:");
foreach (RigJoint j in Enum.GetValues(typeof(RigJoint))) Console.WriteLine($"  {j} {rig.GetJoint(j).x:0.0} {rig.GetJoint(j).y:0.0}");
Console.WriteLine($"  ground {rig.groundY}");
