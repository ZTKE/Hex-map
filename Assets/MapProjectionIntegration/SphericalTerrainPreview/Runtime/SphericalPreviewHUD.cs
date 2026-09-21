using System.Collections.Generic;
using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    [DisallowMultipleComponent]
    public sealed class SphericalPreviewHUD : MonoBehaviour
    {
        public SphericalTerrainPreview terrain;
        public SphericalPreviewCamera navigation;
        public Font uiFont;
        public Texture2D frameTexture;
        public bool GridVisible;
        public bool Visible = true;
        readonly List<Texture2D> textures = new();
        GUIStyle title, body, small, button, section, panel, toggle;
        static readonly Color Ivory = new(.94f, .91f, .80f);
        static readonly Color Brass = new(.62f, .55f, .32f);
        static Rect Panel => new(22f, 24f, 288f, 695f);
        float Scale => Mathf.Clamp(Mathf.Min(Screen.width / 1365f, Screen.height / 768f), .65f, 1.4f);

        public bool IsPointerOverUi(Vector2 screen)
            => Visible && Panel.Contains(new Vector2(screen.x / Scale, (Screen.height - screen.y) / Scale));

        void OnGUI()
        {
            if (!Visible) return;
            EnsureStyles();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color, oldContent = GUI.contentColor;
            bool oldEnabled = GUI.enabled;
            GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1));
            GUI.color = GUI.contentColor = Color.white;
            GUI.Box(Panel, GUIContent.none, panel);
            if (frameTexture)
            {
                GUI.color = new Color(1, 1, 1, .65f);
                GUI.DrawTexture(new Rect(Panel.x, Panel.y, Panel.width, 6), frameTexture, ScaleMode.StretchToFill);
                GUI.color = Color.white;
            }
            GUI.Label(new Rect(42, 43, 245, 22), "WAR AND PEACE  /  地图观测", small);
            GUI.Label(new Rect(42, 74, 245, 38), "球体地形试验场", title);
            GUI.Label(new Rect(42, 114, 245, 44), navigation && !navigation.InputEnabled
                ? "正在自动检查地图，请稍候。\n检查完成后会恢复操作。"
                : "WASD / 方向键移动 · Q / E 旋转\n滚轮缩放 · 按住拖动 · 单击选格", small);
            Rule(170);
            GUI.Label(new Rect(42, 185, 245, 24), "01  地理视角", section);
            GUI.enabled = oldEnabled && navigation && navigation.InputEnabled;
            Preset("全球", "globe", 42, 216); Preset("欧洲山地", "europe", 166, 216);
            Preset("东亚", "eastasia", 42, 252); Preset("北欧海岸", "coast", 166, 252);
            Preset("河流", "river", 42, 288); Preset("五边形", "pentagon", 166, 288);
            Preset("青藏高原", "tibet", 42, 324); Preset("大陆卫星", "continent", 166, 324);
            Preset("森林", "forest", 42, 360); Preset("弯曲沙丘", "desert", 166, 360);
            Preset("高原河流", "plateau-river", 42, 396); Preset("高原坡缘", "plateau-edge", 166, 396);
            Preset("单体山峰", "single-mountain", 42, 432); Preset("沙丘坡缘", "desert-plateau", 166, 432);
            Rule(471);
            GUI.Label(new Rect(42, 485, 245, 24), "02  日照与网格", section);
            if (navigation)
            {
                GUI.Label(new Rect(42, 516, 245, 24), "太阳高度  " + navigation.sunElevation.ToString("F0") + "°", body);
                navigation.sunElevation = GUI.HorizontalSlider(new Rect(42, 548, 245, 20), navigation.sunElevation, 12f, 78f);
            }
            bool grid = GUI.Toggle(new Rect(42, 573, 245, 25), GridVisible, "  显示真实球格边界", toggle);
            if (grid != GridVisible) { GridVisible = grid; if (terrain) terrain.SetGridVisible(grid); }
            GUI.enabled = oldEnabled;
            Rule(613);
            string status = "正在准备地球与地形…";
            bool globe = navigation && navigation.Altitude > 650f;
            bool currentRegionReady = terrain && terrain.IsReady && !terrain.DetailLoading && navigation
                && (navigation.FocusDirection - terrain.DetailFocus).sqrMagnitude * terrain.radius * terrain.radius < 42f * 42f;
            if (terrain && !string.IsNullOrEmpty(terrain.LoadingError))
                status = "地形生成遇到错误。\n具体原因已记录在 Unity Console。";
            else if (terrain && terrain.IsReady && globe)
                status = "卫星视野  ·  " + terrain.World.Count.ToString("N0") + " 个球格\n拉近后逐步显示山体、河流与森林。";
            else if (currentRegionReady)
                status = "地形 " + terrain.VisibleDetailCells + " 格  ·  树木 " + terrain.TreeCount.ToString("N0")
                    + "\n河段 " + terrain.RiverSegmentCount + "  ·  选中 " + (terrain.SelectedCell < 0 ? "—" : terrain.SelectedCell.ToString());
            else if (terrain && terrain.World != null)
                status = terrain.VisibleDetailCells > 0
                    ? "已显示 " + terrain.VisibleDetailCells + " 格  ·  树木 " + terrain.TreeCount.ToString("N0")
                        + "\n正在准备 " + terrain.PendingChunks + " 块地形…"
                    : "正在生成山体、河岸和森林…\n已就绪 " + terrain.ReadyChunks + " 块  ·  待处理 " + terrain.PendingChunks + " 块";
            GUI.Label(new Rect(42, 628, 245, 44), status, small);
            if (navigation)
                GUI.Label(new Rect(42, 679, 245, 22), navigation.Longitude.ToString("F1") + "°  /  " + navigation.Latitude.ToString("F1")
                    + "°    高度 " + navigation.Altitude.ToString("F0"), small);
            GUI.matrix = oldMatrix; GUI.color = oldColor; GUI.contentColor = oldContent; GUI.enabled = oldEnabled;
        }

        void Preset(string label, string id, float x, float y)
        {
            if (GUI.Button(new Rect(x, y, 121, 33), label, button) && navigation) navigation.ApplyPreset(id);
        }
        void Rule(float y)
        {
            Color old = GUI.color; GUI.color = Brass * new Color(1, 1, 1, .6f);
            GUI.DrawTexture(new Rect(42, y, 245, 1), Texture2D.whiteTexture); GUI.color = old;
        }
        Texture2D Texture(Color color)
        {
            Texture2D result = new(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            result.SetPixel(0, 0, color); result.Apply(); textures.Add(result); return result;
        }
        void EnsureStyles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { font = uiFont, fontSize = 25, fontStyle = FontStyle.Bold };
            title.normal.textColor = Ivory;
            body = new GUIStyle(GUI.skin.label) { font = uiFont, fontSize = 14, wordWrap = true };
            body.normal.textColor = Ivory;
            small = new GUIStyle(body) { fontSize = 12 }; small.normal.textColor = new Color(.71f, .73f, .64f);
            section = new GUIStyle(body) { fontSize = 14, fontStyle = FontStyle.Bold }; section.normal.textColor = Brass;
            toggle = new GUIStyle(GUI.skin.toggle) { font = uiFont, fontSize = 14 }; toggle.normal.textColor = Ivory; toggle.onNormal.textColor = Ivory;
            button = new GUIStyle(GUI.skin.button) { font = uiFont, fontSize = 14, border = new RectOffset(1, 1, 1, 1) };
            button.normal.textColor = Ivory; button.normal.background = Texture(new Color(.22f, .25f, .18f, .98f));
            button.hover.textColor = Color.white; button.hover.background = Texture(new Color(.35f, .36f, .22f, 1));
            button.active.background = Texture(new Color(.42f, .40f, .24f, 1));
            panel = new GUIStyle { normal = { background = Texture(new Color(.065f, .09f, .085f, .96f)) } };
        }
        void OnDestroy()
        {
            foreach (Texture2D texture in textures) if (texture) Destroy(texture);
            textures.Clear();
        }
    }
}
