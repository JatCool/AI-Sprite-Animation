using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AISpriteAnimation
{
    /// <summary>
    /// Visual editor for a sprite's rig (Tools > AI Sprite Animation > Sprite Rig Editor).
    /// Drag the joints (neck, shoulders, elbows, hands, hips, knees, feet, hair and weapon pivots) and the ground line, paint which part each pixel
    /// belongs to, and preview the animations live. The result is saved as <c>&lt;Sprite&gt;_Rig.asset</c> and reused by every animation.
    /// </summary>
    public class SpriteRigEditorWindow : EditorWindow
    {
        private enum Tool { Joints, Paint, Fill }

        [SerializeField] private UnityEngine.Object sourceObject;
        [SerializeField] private int zoom = 10;
        [SerializeField] private Tool tool = Tool.Joints;
        [SerializeField] private RigPart paintPart = RigPart.Body;
        [SerializeField] private int brush = 1;
        [SerializeField] private bool additive;
        [SerializeField] private bool showParts = true;
        [SerializeField] private int animIndex = 1;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private AIPoseAsset poseAsset;
        [SerializeField] private bool previewAIPoses;
        [SerializeField] private bool showBones = true;

        private SourceSprite source;
        private SpriteRigAsset rig;
        private Color32[] pixels;           // bottom-left origin
        private int sw, sh;
        private Texture2D spriteTex, overlayTex;
        private bool overlayDirty = true;
        private int dragJoint = -1;
        private bool dragGround;
        private bool paintingStroke;
        private string message = "";

        // live preview
        private bool playing = true;
        private double lastTick;
        private int previewFrame;
        private List<Color32[]> previewFrames;
        private Texture2D previewTex;
        private bool previewDirty = true;
        private int previewCells;

        // AI pose preview (original sprite beside the animated rig, driven by a saved AIPoseAsset)
        private PosePreviewPlayer posePlayer;
        private Texture2D poseTex, originalTex;
        private bool poseDirty = true;
        private string poseMessage = "";

        private static readonly Color[] PartColors =
        {
            new Color(0.78f, 0.24f, 0.24f), new Color(0.24f, 0.63f, 0.86f), new Color(0.94f, 0.78f, 0.16f), new Color(0.35f, 0.78f, 0.35f),
            new Color(0.16f, 0.47f, 0.16f), new Color(0.98f, 0.47f, 0.0f), new Color(0.71f, 0.35f, 0.78f), new Color(0.43f, 0.16f, 0.55f),
            new Color(0f, 0.78f, 0.78f), new Color(0f, 0.47f, 0.55f), new Color(0f, 0.27f, 0.35f), new Color(0.9f, 0.4f, 0.67f),
            new Color(0.59f, 0.24f, 0.39f), new Color(0.39f, 0.16f, 0.27f),
        };

        private static readonly string[] PartLabels =
        {
            "Body", "Head", "Hair", "Right arm (near) upper", "Right arm (near) lower+hand", "Weapon", "Left arm (far) upper", "Left arm (far) lower+hand",
            "Right leg (near) upper", "Right leg (near) lower", "Right foot", "Left leg (far) upper", "Left leg (far) lower", "Left foot",
        };

        private static readonly string[] JointLabels =
        {
            "Neck", "Hair pivot", "Hip", "R shoulder", "R elbow", "R hand", "L shoulder", "L elbow", "L hand", "R knee", "R foot", "L knee", "L foot", "Weapon pivot", "Weapon tip",
        };

        // Lines drawn between joints (indices into RigJoint).
        private static readonly int[,] Bones =
        {
            { (int)RigJoint.Neck, (int)RigJoint.Hip },
            { (int)RigJoint.ShoulderNear, (int)RigJoint.ElbowNear }, { (int)RigJoint.ElbowNear, (int)RigJoint.HandNear },
            { (int)RigJoint.ShoulderFar, (int)RigJoint.ElbowFar }, { (int)RigJoint.ElbowFar, (int)RigJoint.HandFar },
            { (int)RigJoint.Hip, (int)RigJoint.KneeNear }, { (int)RigJoint.KneeNear, (int)RigJoint.FootNear },
            { (int)RigJoint.Hip, (int)RigJoint.KneeFar }, { (int)RigJoint.KneeFar, (int)RigJoint.FootFar },
            { (int)RigJoint.WeaponPivot, (int)RigJoint.WeaponTip },
        };

        [MenuItem("Tools/AI Sprite Animation/Sprite Rig Editor")]
        private static void OpenFromMenu() => Open(Selection.activeObject);

        /// <summary>Opens the rig editor in AI pose preview mode: the saved poses are played on the original sprite before an AnimationClip is built.</summary>
        public static void OpenPosePreview(UnityEngine.Object sprite, AIPoseAsset asset)
        {
            var w = GetWindow<SpriteRigEditorWindow>("Sprite Rig Editor");
            w.minSize = new Vector2(760, 520);
            if (sprite != null && SourceSprite.IsValidSelection(sprite)) w.SetSource(sprite);
            w.poseAsset = asset;
            w.previewAIPoses = true;
            w.poseDirty = true;
            w.Show();
        }

        public static void Open(UnityEngine.Object sprite)
        {
            var w = GetWindow<SpriteRigEditorWindow>("Sprite Rig Editor");
            w.minSize = new Vector2(760, 520);
            if (sprite != null && SourceSprite.IsValidSelection(sprite)) w.SetSource(sprite);
            w.Show();
        }

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndo;
            EditorApplication.update += Tick;
            if (sourceObject != null && source == null) SetSource(sourceObject);
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndo;
            EditorApplication.update -= Tick;
            if (spriteTex != null) DestroyImmediate(spriteTex);
            if (overlayTex != null) DestroyImmediate(overlayTex);
            if (previewTex != null) DestroyImmediate(previewTex);
            if (poseTex != null) DestroyImmediate(poseTex);
            if (originalTex != null) DestroyImmediate(originalTex);
        }

        private void OnUndo()
        {
            rig?.definition.Invalidate();
            overlayDirty = previewDirty = poseDirty = true;
            Repaint();
        }

        // ---------------------------------------------------------------- source / rig loading

        private void SetSource(UnityEngine.Object obj)
        {
            sourceObject = obj;
            message = "";
            rig = null; source = null; pixels = null;
            var settings = AIAnimationSettings.GetOrCreate();
            if (!SourceSprite.TryResolve(obj, settings, out source, out string error)) { message = error; return; }
            pixels = SpriteFrameProcessor.LoadSpritePixels(source, out sw, out sh);
            RebuildSpriteTexture();
            rig = SpriteRigAsset.FindFor(source, settings);
            overlayDirty = previewDirty = true;
        }

        private void RebuildSpriteTexture()
        {
            if (spriteTex != null) DestroyImmediate(spriteTex);
            spriteTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            spriteTex.SetPixels32(pixels);
            spriteTex.Apply(false);
        }

        private void RebuildOverlay()
        {
            if (overlayTex == null || overlayTex.width != sw || overlayTex.height != sh)
            {
                if (overlayTex != null) DestroyImmediate(overlayTex);
                overlayTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            }
            var o = new Color32[sw * sh];
            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    ushort m = rig.definition.GetMask(x, y);
                    if (m == 0) continue;
                    int first = -1, second = -1;
                    for (int p = 0; p < RigDefinition.PartCount; p++) if ((m & (1 << p)) != 0) { if (first < 0) first = p; else if (second < 0) second = p; }
                    Color c = PartColors[first];
                    // texture rows run bottom -> top
                    o[(sh - 1 - y) * sw + x] = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), (byte)(second >= 0 ? 190 : 140));
                }
            overlayTex.SetPixels32(o);
            overlayTex.Apply(false);
            overlayDirty = false;
        }

        // ---------------------------------------------------------------- GUI

        private void OnGUI()
        {
            var settings = AIAnimationSettings.GetOrCreate();
            EditorGUILayout.Space(2);
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField("Sprite", sourceObject, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck() && picked != sourceObject) { if (picked == null || SourceSprite.IsValidSelection(picked)) SetSource(picked); }

            if (source == null)
            {
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(message) ? "Select a sprite (PNG or one sprite of a sheet) in the Project window, or drop it here." : message, MessageType.Info);
                return;
            }

            if (rig == null)
            {
                EditorGUILayout.HelpBox($"'{source.Name}' has no rig yet. Create a starting rig from the sprite's shape, then drag the joints onto the character and paint the parts.", MessageType.Info);
                if (ChargenRigImporter.FindRigJson(source) != null && GUILayout.Button(new GUIContent("Import Generated Rig Data", "Use the joints, part map, hidden pixels and leg swing scale that the character generator saved in rig/rig.json next to this sprite."), GUILayout.Height(28)))
                {
                    rig = ChargenRigImporter.TryImportFor(source, settings);
                    if (rig != null) Selection.activeObject = rig;
                    else message = "The generated rig data does not fit this sprite (see the Console).";
                    overlayDirty = previewDirty = true;
                }
                if (GUILayout.Button("Create Rig (auto)", GUILayout.Height(28)))
                {
                    rig = SpriteRigAsset.CreateAuto(source, settings);
                    Selection.activeObject = rig;
                    overlayDirty = previewDirty = true;
                }
                return;
            }

            DrawToolbar(settings);
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.ExpandWidth(true));
                DrawCanvas();
                EditorGUILayout.EndScrollView();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(previewAIPoses ? 500 : 260)))
                {
                    DrawPartPicker();
                    GUILayout.Space(8);
                    DrawPreview(settings);
                }
            }
        }

        private void DrawToolbar(AIAnimationSettings settings)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                tool = (Tool)GUILayout.Toolbar((int)tool, new[] { "Joints", "Paint parts", "Fill region" }, GUILayout.Height(24));
                zoom = EditorGUILayout.IntSlider(zoom, 4, 20, GUILayout.Width(220));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                showParts = GUILayout.Toggle(showParts, "Show parts", "Button", GUILayout.Width(90));
                if (tool == Tool.Paint)
                {
                    brush = EditorGUILayout.IntSlider("Brush", brush, 1, 6);
                    additive = GUILayout.Toggle(additive, new GUIContent("Add (overlap)", "Add the part to the pixel instead of replacing (used for overlapping legs)"), "Button", GUILayout.Width(100));
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Auto-assign parts", "Re-assigns every pixel from the current joints. Overwrites your painting."), GUILayout.Width(120)))
                {
                    Undo.RecordObject(rig, "Auto-assign rig parts");
                    RigAutoBuilder.AssignParts(rig.definition, pixels, sw, sh, settings.AutoRigParams());
                    Commit("Parts re-assigned from the joints.");
                }
                if (GUILayout.Button(new GUIContent("Reset rig", "Rebuilds joints and parts from the sprite's shape"), GUILayout.Width(80)))
                {
                    if (EditorUtility.DisplayDialog("Reset rig", "Throw away your joints and painting and rebuild from the sprite's shape?", "Reset", "Cancel"))
                    {
                        Undo.RecordObject(rig, "Reset rig");
                        rig.definition = RigAutoBuilder.Build(pixels, sw, sh, settings.AutoRigParams());
                        Commit("Rig reset.");
                    }
                }
                if (GUILayout.Button("Ping asset", GUILayout.Width(80))) { Selection.activeObject = rig; EditorGUIUtility.PingObject(rig); }
            }
        }

        private void Commit(string msg)
        {
            rig.Commit();
            overlayDirty = previewDirty = poseDirty = true;
            message = msg;
            Repaint();
        }

        private Rect canvasRect;

        private void DrawCanvas()
        {
            float cw = sw * zoom, ch = sh * zoom;
            canvasRect = GUILayoutUtility.GetRect(cw + 40, ch + 40, GUILayout.ExpandWidth(false));
            var area = new Rect(canvasRect.x + 20, canvasRect.y + 20, cw, ch);
            EditorGUI.DrawRect(new Rect(area.x - 2, area.y - 2, cw + 4, ch + 4), new Color(0.15f, 0.2f, 0.15f));
            EditorGUI.DrawRect(area, new Color(0.3f, 0.38f, 0.3f));
            GUI.DrawTexture(area, spriteTex, ScaleMode.StretchToFill, true);
            if (showParts)
            {
                if (overlayDirty) RebuildOverlay();
                GUI.DrawTexture(area, overlayTex, ScaleMode.StretchToFill, true);
            }

            // grid every 8 px
            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.08f);
            for (int x = 8; x < sw; x += 8) Handles.DrawLine(new Vector3(area.x + x * zoom, area.y), new Vector3(area.x + x * zoom, area.yMax));
            for (int y = 8; y < sh; y += 8) Handles.DrawLine(new Vector3(area.x, area.y + y * zoom), new Vector3(area.xMax, area.y + y * zoom));

            // ground line
            float gy = area.y + rig.definition.groundY * zoom;
            Handles.color = new Color(1f, 0.9f, 0.2f, 0.9f);
            Handles.DrawLine(new Vector3(area.x - 12, gy), new Vector3(area.xMax + 12, gy));
            GUI.Label(new Rect(area.xMax + 14, gy - 9, 60, 18), "ground", EditorStyles.miniLabel);

            // bones and joints
            Handles.color = new Color(1, 1, 1, 0.8f);
            for (int b = 0; b < Bones.GetLength(0); b++)
                Handles.DrawLine(JointScreen(area, Bones[b, 0]), JointScreen(area, Bones[b, 1]));
            for (int j = 0; j < RigDefinition.JointCount; j++)
            {
                Vector2 p = JointScreen(area, j);
                var col = j == (int)RigJoint.WeaponPivot || j == (int)RigJoint.WeaponTip ? new Color(1f, 0.5f, 0f) :
                          j == (int)RigJoint.HairPivot ? new Color(1f, 0.85f, 0.2f) : new Color(0.2f, 0.9f, 1f);
                Handles.color = Color.black; Handles.DrawSolidDisc(p, Vector3.forward, 6.5f);
                Handles.color = j == dragJoint ? Color.white : col; Handles.DrawSolidDisc(p, Vector3.forward, 5f);
                GUI.Label(new Rect(p.x + 8, p.y - 9, 90, 18), JointLabels[j], EditorStyles.miniBoldLabel);
            }
            Handles.EndGUI();

            HandleCanvasEvents(area);
        }

        private Vector2 JointScreen(Rect area, int j) => new Vector2(area.x + rig.definition.joints[j].x * zoom, area.y + rig.definition.joints[j].y * zoom);

        private void HandleCanvasEvents(Rect area)
        {
            Event e = Event.current;
            Vector2 mouse = e.mousePosition;
            Vector2 px = new Vector2((mouse.x - area.x) / zoom, (mouse.y - area.y) / zoom);   // sprite coordinates (y down)

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (tool == Tool.Joints)
                {
                    dragJoint = -1; dragGround = false;
                    float best = 10f;
                    for (int j = 0; j < RigDefinition.JointCount; j++)
                    {
                        float d = Vector2.Distance(mouse, JointScreen(area, j));
                        if (d < best) { best = d; dragJoint = j; }
                    }
                    if (dragJoint < 0 && Mathf.Abs(mouse.y - (area.y + rig.definition.groundY * zoom)) < 6f && mouse.x > area.x - 14 && mouse.x < area.xMax + 14) dragGround = true;
                    if (dragJoint >= 0 || dragGround) { Undo.RecordObject(rig, "Move rig joint"); e.Use(); }
                }
                else if (area.Contains(mouse))
                {
                    Undo.RecordObject(rig, tool == Tool.Paint ? "Paint rig parts" : "Fill rig part");
                    if (tool == Tool.Fill) { Fill((int)px.x, (int)px.y); }
                    else { paintingStroke = true; Paint((int)px.x, (int)px.y); }
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 0)
            {
                if (dragJoint >= 0)
                {
                    rig.definition.joints[dragJoint] = new Vector2(Mathf.Clamp(Mathf.Round(px.x * 2f) / 2f, 0, sw), Mathf.Clamp(Mathf.Round(px.y * 2f) / 2f, 0, sh));
                    previewDirty = true; EditorUtility.SetDirty(rig); e.Use(); Repaint();
                }
                else if (dragGround)
                {
                    rig.definition.groundY = Mathf.Clamp(Mathf.Round(px.y), 0, sh);
                    previewDirty = true; EditorUtility.SetDirty(rig); e.Use(); Repaint();
                }
                else if (paintingStroke) { Paint((int)px.x, (int)px.y); e.Use(); }
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (dragJoint >= 0 || dragGround || paintingStroke)
                {
                    dragJoint = -1; dragGround = false; paintingStroke = false;
                    Commit("Saved.");
                    e.Use();
                }
            }
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) Repaint();
        }

        private void Paint(int cx, int cy)
        {
            ushort bit = RigDefinition.Bit(paintPart);
            int r0 = -(brush - 1) / 2, r1 = brush / 2;
            for (int dy = r0; dy <= r1; dy++)
                for (int dx = r0; dx <= r1; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= sw || y >= sh || pixels[(sh - 1 - y) * sw + x].a == 0) continue;
                    ushort m = rig.definition.GetMask(x, y);
                    rig.definition.SetMask(x, y, additive ? (ushort)(m | bit) : bit);
                }
            overlayDirty = previewDirty = true;
            Repaint();
        }

        // Re-assigns the connected region (same current parts) under the cursor to the selected part.
        private void Fill(int sx, int sy)
        {
            if (sx < 0 || sy < 0 || sx >= sw || sy >= sh || pixels[(sh - 1 - sy) * sw + sx].a == 0) return;
            ushort target = rig.definition.GetMask(sx, sy), bit = RigDefinition.Bit(paintPart);
            if (target == bit) return;
            var stack = new Stack<Vector2Int>();
            var seen = new bool[sw * sh];
            stack.Push(new Vector2Int(sx, sy)); seen[sy * sw + sx] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                rig.definition.SetMask(p.x, p.y, bit);
                foreach (var d in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
                {
                    int nx = p.x + d.x, ny = p.y + d.y;
                    if (nx < 0 || ny < 0 || nx >= sw || ny >= sh || seen[ny * sw + nx]) continue;
                    if (pixels[(sh - 1 - ny) * sw + nx].a == 0 || rig.definition.GetMask(nx, ny) != target) continue;
                    seen[ny * sw + nx] = true;
                    stack.Push(new Vector2Int(nx, ny));
                }
            }
            Commit($"Region assigned to {PartLabels[(int)paintPart]}.");
        }

        private void DrawPartPicker()
        {
            EditorGUILayout.LabelField("Part to paint / fill", EditorStyles.boldLabel);
            for (int p = 0; p < RigDefinition.PartCount; p++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var swatch = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                    EditorGUI.DrawRect(swatch, PartColors[p]);
                    bool on = paintPart == (RigPart)p;
                    if (GUILayout.Toggle(on, $"{PartLabels[p]}  ({rig.definition.CountPixels((RigPart)p)})", EditorStyles.miniButton) && !on) { paintPart = (RigPart)p; if (tool == Tool.Joints) tool = Tool.Paint; }
                }
            }
            EditorGUILayout.HelpBox("Joints: drag the dots (and the yellow ground line). Paint: pixels take the selected part; 'Add' keeps overlapping legs in both. Fill: reassigns a connected region. Hierarchy: rotating an upper arm carries the forearm and weapon along.", MessageType.None);
        }

        // ---------------------------------------------------------------- live preview (uses the real rig renderer)

        private void DrawPreview(AIAnimationSettings settings)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            int sourceKind = GUILayout.Toolbar(previewAIPoses ? 1 : 0, new[] { "Procedural preset", "AI poses" });
            if ((sourceKind == 1) != previewAIPoses) { previewAIPoses = sourceKind == 1; poseDirty = previewDirty = true; }
            if (previewAIPoses) { DrawPosePreview(settings); return; }
            string[] names = settings.PresetNames();
            if (names.Length == 0) return;
            animIndex = Mathf.Clamp(animIndex, 0, names.Length - 1);
            EditorGUI.BeginChangeCheck();
            animIndex = EditorGUILayout.Popup(animIndex, names);
            if (EditorGUI.EndChangeCheck()) previewDirty = true;
            playing = GUILayout.Toggle(playing, playing ? "Pause" : "Play", "Button");

            if (previewDirty) BuildPreview(settings);
            if (previewFrames == null || previewTex == null) return;
            int z = Mathf.Max(2, Mathf.FloorToInt(240f / previewCells));
            var r = GUILayoutUtility.GetRect(previewCells * z, previewCells * z, GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(r, new Color(0.3f, 0.38f, 0.3f));
            // crop display to the used area is not needed here; draw the whole padded canvas
            GUI.DrawTexture(r, previewTex, ScaleMode.StretchToFill, true);
            EditorGUILayout.LabelField($"frame {previewFrame + 1}/{previewFrames.Count}", EditorStyles.miniLabel);
        }

        private void BuildPreview(AIAnimationSettings settings)
        {
            previewDirty = false;
            try
            {
                var preset = settings.presets[Mathf.Clamp(animIndex, 0, settings.presets.Count - 1)];
                previewCells = Mathf.CeilToInt(Mathf.Max(sw, sh) * 1.6f);
                int left = (previewCells - sw) / 2, bottom = (previewCells - sh) / 2;
                var poses = SpriteRig.ApplyLegSwingScale(rig.definition, ProceduralRigPoses.Instance.GetPoses(preset.poseKind, Mathf.Max(preset.frames, 8), Mathf.Max(preset.frames, 8), preset.rigIntensity), preset.poseKind);
                previewFrames = SpriteRig.Render(rig.definition, pixels, poses, previewCells, left, bottom, settings.facing == SpriteFacing.Left);
                if (previewTex == null || previewTex.width != previewCells)
                {
                    if (previewTex != null) DestroyImmediate(previewTex);
                    previewTex = new Texture2D(previewCells, previewCells, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                }
                previewFrame = Mathf.Min(previewFrame, previewFrames.Count - 1);
                ShowPreviewFrame();
            }
            catch (Exception ex) { message = "Preview failed: " + ex.Message; previewFrames = null; }
        }

        private void ShowPreviewFrame()
        {
            previewTex.SetPixels32(previewFrames[previewFrame]);
            previewTex.Apply(false);
        }

        // ---------------------------------------------------------------- AI pose preview

        private void DrawPosePreview(AIAnimationSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            poseAsset = (AIPoseAsset)EditorGUILayout.ObjectField("Pose asset", poseAsset, typeof(AIPoseAsset), false);
            if (EditorGUI.EndChangeCheck()) poseDirty = true;
            if (poseAsset == null)
            {
                EditorGUILayout.HelpBox("Pick a saved AI pose asset (<Sprite>_<Animation>_AIPose.asset), or generate one in the AI Sprite Animation window (Animation Method: AI Pose + Rig > Generate Poses).", MessageType.Info);
                return;
            }
            if (poseDirty) BuildPosePreview(settings);
            if (posePlayer == null) { EditorGUILayout.HelpBox(poseMessage, MessageType.Warning); return; }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("|<", GUILayout.Width(36))) { posePlayer.Playing = false; posePlayer.Step(-1); ShowPoseFrame(); }
                if (GUILayout.Button(posePlayer.Playing ? "Pause" : "Play", GUILayout.Width(60))) posePlayer.Playing = !posePlayer.Playing;
                if (GUILayout.Button(">|", GUILayout.Width(36))) { posePlayer.Playing = false; posePlayer.Step(1); ShowPoseFrame(); }
                GUILayout.Space(8);
                GUILayout.Label(posePlayer.Label, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                showBones = GUILayout.Toggle(showBones, "Show bones", "Button", GUILayout.Width(90));
            }
            EditorGUI.BeginChangeCheck();
            int idx = EditorGUILayout.IntSlider(posePlayer.Index + 1, 1, posePlayer.FrameCount) - 1;
            if (EditorGUI.EndChangeCheck()) { posePlayer.Playing = false; posePlayer.Seek(idx); ShowPoseFrame(); }

            int z = Mathf.Max(2, Mathf.FloorToInt(230f / posePlayer.Cells));
            float size = posePlayer.Cells * z;
            var r = GUILayoutUtility.GetRect(size * 2 + 10, size, GUILayout.ExpandWidth(false));
            var left = new Rect(r.x, r.y, size, size);
            var right = new Rect(r.x + size + 10, r.y, size, size);
            EditorGUI.DrawRect(left, new Color(0.3f, 0.38f, 0.3f));
            EditorGUI.DrawRect(right, new Color(0.3f, 0.38f, 0.3f));
            GUI.DrawTexture(left, originalTex, ScaleMode.StretchToFill, true);
            GUI.DrawTexture(right, poseTex, ScaleMode.StretchToFill, true);
            GUI.Label(new Rect(left.x + 2, left.y + 2, 120, 16), "Original sprite", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(right.x + 2, right.y + 2, 160, 16), $"AI pose: {poseAsset.animation}", EditorStyles.miniBoldLabel);
            if (showBones)
            {
                Handles.BeginGUI();
                Handles.color = new Color(1f, 1f, 1f, 0.9f);
                var joints = posePlayer.Joints[posePlayer.Index];
                for (int b = 0; b < PosePreviewPlayer.Bones.GetLength(0); b++)
                {
                    Vector2 a = joints[PosePreviewPlayer.Bones[b, 0]], c = joints[PosePreviewPlayer.Bones[b, 1]];
                    Handles.DrawLine(new Vector3(right.x + a.x * z, right.y + a.y * z), new Vector3(right.x + c.x * z, right.y + c.y * z));
                }
                Handles.color = new Color(0.2f, 0.9f, 1f);
                foreach (var jp in joints) Handles.DrawSolidDisc(new Vector3(right.x + jp.x * z, right.y + jp.y * z), Vector3.forward, 2f);
                Handles.EndGUI();
            }

            EditorGUILayout.LabelField($"{poseAsset.FrameCount} frames, {poseAsset.fps} FPS, {(poseAsset.loop ? "loop" : "one-shot")}; every pixel above comes from the original sprite", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(poseAsset.MotionLabel + (string.IsNullOrEmpty(poseAsset.contributionSummary) ? "" : " - " + poseAsset.contributionSummary),
                poseAsset.isAiMotion ? MessageType.Info : MessageType.Warning);
            if (!string.IsNullOrEmpty(poseMessage)) EditorGUILayout.HelpBox(poseMessage, MessageType.None);
        }

        private void BuildPosePreview(AIAnimationSettings settings)
        {
            poseDirty = false;
            posePlayer = null;
            try
            {
                var provider = new AIPoseProvider(poseAsset, rig.definition, settings.poseCleanup);
                RigPose[] poses = SpriteRig.ApplyLegSwingScale(rig.definition, provider.GetPoses(poseAsset.animation, poseAsset.FrameCount, poseAsset.FrameCount, 1f), poseAsset.animation);
                posePlayer = PosePreviewPlayer.Build(rig.definition, pixels, poses, poseAsset.fps, settings.facing == SpriteFacing.Left);
                poseMessage = provider.LastReport.ToString();
                if (poseTex == null || poseTex.width != posePlayer.Cells)
                {
                    if (poseTex != null) DestroyImmediate(poseTex);
                    if (originalTex != null) DestroyImmediate(originalTex);
                    poseTex = new Texture2D(posePlayer.Cells, posePlayer.Cells, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                    originalTex = new Texture2D(posePlayer.Cells, posePlayer.Cells, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                }
                originalTex.SetPixels32(posePlayer.Original);
                originalTex.Apply(false);
                ShowPoseFrame();
            }
            catch (PoseRejectedException ex) { poseMessage = "These poses are rejected by validation:\n" + ex.Report; }
            catch (Exception ex) { poseMessage = "Pose preview failed: " + ex.Message; }
        }

        private void ShowPoseFrame()
        {
            poseTex.SetPixels32(posePlayer.Frames[posePlayer.Index]);
            poseTex.Apply(false);
        }

        private void Tick()
        {
            if (previewAIPoses)
            {
                if (posePlayer != null && poseTex != null && posePlayer.Tick(EditorApplication.timeSinceStartup)) { ShowPoseFrame(); Repaint(); }
                return;
            }
            if (!playing || previewFrames == null || previewTex == null || rig == null) return;
            double now = EditorApplication.timeSinceStartup;
            var settings = AIAnimationSettings.GetOrCreate();
            int fps = settings.presets.Count > 0 ? settings.presets[Mathf.Clamp(animIndex, 0, settings.presets.Count - 1)].fps : 12;
            if (now - lastTick < 1.0 / fps) return;
            lastTick = now;
            previewFrame = (previewFrame + 1) % previewFrames.Count;
            ShowPreviewFrame();
            Repaint();
        }
    }
}
