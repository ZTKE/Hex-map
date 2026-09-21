using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace ZTKE.HexMap.Standalone
{
    /// <summary>Displays migrated cities at their authoritative native cell centers.
    /// Markers reuse Game_2's original screen-sized city shader.</summary>
    [DefaultExecutionOrder(1050)]
    public sealed class SphericalGameplayCities : MonoBehaviour
    {
        const int NameFontSize = 14;
        const int MaximumVisibleNames = 220;
        const float NameOffsetPixels = 7f;
        StandaloneSphereMap controller;
        GameObject markers;
        Mesh mesh;
        Material material;
        Canvas canvas;
        Font font;
        readonly List<Text> labels = new();
        readonly List<Rect> occupied = new();
        Vector3[] positions;
        string[] names;
        Vector2[] nameSizes;
        public int CityCount => names?.Length ?? 0;
        public int VisibleNameCount { get; private set; }
        public double TerrainAnchorMilliseconds { get; private set; }
        public double NamePreparationMilliseconds { get; private set; }

        public void Initialize(StandaloneSphereMap owner)
        {
            controller = owner;
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, NameFontSize);
            var root = new GameObject("Spherical city names", typeof(RectTransform), typeof(Canvas));
            root.transform.SetParent(transform, false);
            canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = controller.mapCamera;
            canvas.overrideSorting = true; canvas.sortingOrder = -100; canvas.pixelPerfect = false;
            markers = new GameObject("Spherical city markers", typeof(MeshFilter), typeof(MeshRenderer));
            markers.transform.SetParent(transform, false);
            material = new Material(Shader.Find("Hex Map/World City Marker")) { name = "Game city markers on Earth", hideFlags = HideFlags.DontSave };
            var renderer = markers.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            mesh = new Mesh { name = "Game cities • original IDs on spherical surface", indexFormat = IndexFormat.UInt32 };
            markers.GetComponent<MeshFilter>().sharedMesh = mesh;
            Rebuild();
        }

        public void Rebuild()
        {
            if (!controller || !mesh) return;
            var native = controller.NativeMap;
            if (native == null) return;
            var preparation = System.Diagnostics.Stopwatch.StartNew();
            int count = native.Cities.Length;
            positions = new Vector3[count]; names = new string[count]; nameSizes = new Vector2[count];
            var vertices = new Vector3[count * 4]; var uvs = new Vector2[count * 4]; var triangles = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                var city = native.Cities[i];
                names[i] = city.Name;
                // Evaluate once even when initialized at satellite altitude.
                Vector3 n = native.Centers[city.TileId];
                positions[i] = n * (controller.Terrain.radius + Mathf.Max(0, controller.Terrain.Surface.Evaluate(n).Height) + 1.35f);
                int v = i * 4, t = i * 6;
                for (int k = 0; k < 4; k++) vertices[v + k] = positions[i];
                uvs[v] = new Vector2(-1, -1); uvs[v + 1] = new Vector2(1, -1);
                uvs[v + 2] = new Vector2(1, 1); uvs[v + 3] = new Vector2(-1, 1);
                triangles[t] = v; triangles[t + 1] = v + 2; triangles[t + 2] = v + 1;
                triangles[t + 3] = v; triangles[t + 4] = v + 3; triangles[t + 5] = v + 2;
            }
            mesh.Clear(); mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles; mesh.RecalculateBounds();
            TerrainAnchorMilliseconds = preparation.Elapsed.TotalMilliseconds;
            // Populate this layer's private font before rotating into new cities.
            // Keep Text's normal FontUpdateTracker for any later atlas rebuild.
            if (count > 0)
            {
                font.RequestCharactersInTexture(string.Join(" ", names), NameFontSize, FontStyle.Bold);
                Text measure = GetLabel(0);
                for (int i = 0; i < count; i++)
                {
                    measure.text = names[i];
                    // Overflow is intentional: reserve the actual full name,
                    // including its shadow, instead of capping only its bounds.
                    nameSizes[i] = new Vector2(measure.preferredWidth + 6f,
                        Mathf.Max(20f, measure.preferredHeight + 4f));
                }
            }
            HideUnusedNames(0);
            NamePreparationMilliseconds = preparation.Elapsed.TotalMilliseconds - TerrainAnchorMilliseconds;
        }

        void LateUpdate()
        {
            if (!controller || !canvas || positions == null) return;
            Camera camera = controller.mapCamera;
            bool visible = camera && controller.IsReady && controller.Navigation.Altitude < 1800f;
            markers.SetActive(visible); canvas.enabled = visible; VisibleNameCount = 0;
            if (!visible) { HideUnusedNames(0); return; }
            canvas.planeDistance = Mathf.Max(1, camera.nearClipPlane + .5f);
            Rect viewport = camera.pixelRect;
            if (viewport.width <= 0f || viewport.height <= 0f) { HideUnusedNames(0); return; }
            float pixelsPerUnit = Mathf.Max(.001f, canvas.scaleFactor);
            occupied.Clear();
            for (int i = 0; i < positions.Length && VisibleNameCount < MaximumVisibleNames; i++)
            {
                if (string.IsNullOrEmpty(names[i]) || !controller.IsVisible(positions[i], camera)) continue;
                Vector3 screen = camera.WorldToScreenPoint(positions[i]);
                if (screen.z <= 0f || !viewport.Contains(screen)) continue;
                Vector2 size = nameSizes[i] * pixelsPerUnit;
                var rect = new Rect(screen.x + NameOffsetPixels, screen.y - size.y * .5f, size.x, size.y);
                var padded = new Rect(rect.x - 3f, rect.y - 1f, rect.width + 6f, rect.height + 2f);
                bool overlaps = false;
                foreach (var used in occupied) if (used.Overlaps(padded)) { overlaps = true; break; }
                if (overlaps) continue;
                occupied.Add(rect);
                // Only accepted names touch the Text pool; rejected candidates
                // must not regenerate text or request glyphs every camera frame.
                Text label = GetLabel(VisibleNameCount);
                if (label.text != names[i]) label.text = names[i];
                RectTransform rectTransform = label.rectTransform;
                // Camera LateUpdate precedes this layer, but ScreenSpaceCamera
                // Canvas geometry is synchronized later. Ray/plane conversion
                // would intersect the current ray with the preceding camera's
                // Canvas plane. Convert pixels from the viewport center into
                // Canvas units directly instead. Keep centered anchors fixed:
                // moving anchorMin/Max separately would momentarily stretch Text
                // and dirty its glyph geometry on every frame of a rotation.
                rectTransform.anchoredPosition = new Vector2(
                    (screen.x - viewport.center.x + NameOffsetPixels) / pixelsPerUnit,
                    (screen.y - viewport.center.y) / pixelsPerUnit);
                rectTransform.sizeDelta = nameSizes[i];
                label.enabled = true;
                VisibleNameCount++;
            }
            HideUnusedNames(VisibleNameCount);
        }

        void HideUnusedNames(int firstUnused)
        {
            for (int i = firstUnused; i < labels.Count; i++) labels[i].enabled = false;
        }

        void OnDisable()
        {
            if (markers) markers.SetActive(false);
            if (canvas) canvas.enabled = false;
            HideUnusedNames(0);
            VisibleNameCount = 0;
        }

        Text GetLabel(int index)
        {
            if (index < labels.Count) return labels[index];
            var root = new GameObject("City name", typeof(RectTransform), typeof(Text), typeof(Shadow));
            root.transform.SetParent(canvas.transform, false);
            var label = root.GetComponent<Text>(); label.font = font; label.fontSize = NameFontSize;
            label.fontStyle = FontStyle.Bold; label.color = new Color(1, .93f, .72f);
            label.raycastTarget = false; label.supportRichText = false; label.maskable = false;
            label.alignment = TextAnchor.MiddleLeft; label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(.5f, .5f);
            label.rectTransform.pivot = new Vector2(0, .5f);
            var shadow = root.GetComponent<Shadow>(); shadow.effectColor = new Color(.04f, .035f, .03f, .96f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f); labels.Add(label); return label;
        }

        void OnDestroy()
        {
            if (markers) Destroy(markers);
            if (canvas) Destroy(canvas.gameObject);
            if (mesh) Destroy(mesh); if (material) Destroy(material); if (font) Destroy(font);
        }
    }
}
