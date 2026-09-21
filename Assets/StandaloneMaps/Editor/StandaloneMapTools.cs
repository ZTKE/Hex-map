using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WW2.SphericalTerrainPreview;

namespace ZTKE.HexMap.Standalone.Editor
{
    public static class StandaloneMapTools
    {
        public const string Flat = "Assets/HexMapPackage/Scenes/Hex Map Scene.unity";
        public const string Sphere = "Assets/StandaloneMaps/Scenes/Spherical Map.unity";
        [MenuItem("Window/Hex Map/Open Flat Map")]
        public static void OpenFlat() { if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(Flat); }
        [MenuItem("Window/Hex Map/Open Spherical Map")]
        public static void OpenSphere() { if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(Sphere); }

        public static void Prepare()
        {
            Directory.CreateDirectory("Assets/StandaloneMaps/Scenes");
            var scene = EditorSceneManager.OpenScene("Assets/MapProjectionIntegration/SphericalTerrainPreview/Scenes/Spherical Terrain Preview.unity");
            var terrain = UnityEngine.Object.FindObjectOfType<WW2.SphericalTerrainPreview.SphericalTerrainPreview>();
            var map = terrain.gameObject.AddComponent<StandaloneSphereMap>();
            map.Terrain = terrain; map.Navigation = UnityEngine.Object.FindObjectOfType<SphericalPreviewCamera>();
            var shaders = new List<Shader>();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/MapProjectionIntegration/SphericalTerrainPreview" }))
                shaders.Add(AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid)));
            shaders.Add(Shader.Find("Hex Map/World City Marker"));
            map.presentationShaders = shaders.ToArray();
            EditorSceneManager.SaveScene(scene, Sphere, true);
            EditorSceneManager.OpenScene(Flat);
            AssetDatabase.SaveAssets();
            Debug.Log("Prepared independent flat and spherical map scenes.");
        }
    }

    // Batch validation continues through Play Mode/domain reloads and exits only after both maps load.
    [InitializeOnLoad]
    public static class MapMigrationValidation
    {
        const string Key = "StandaloneMigrationStage";
        static readonly List<string> Errors = new();
        static double readyAt;
        static string Root => Path.GetFullPath("Artifacts/MapMigration20260919");
        static MapMigrationValidation()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                { Errors.Add(message); if (SessionState.GetInt(Key, 0) > 0) File.AppendAllText(Path.Combine(Root,"runtime-errors.txt"), message+"\n"+stack+"\n"); }
            };
        }
        public static void Run()
        {
            Errors.Clear(); readyAt = 0;
            Directory.CreateDirectory(Root);
            StandaloneMapTools.Prepare();
            SessionState.SetInt(Key, 1);
            SessionState.SetString("StandaloneMigrationStarted", DateTime.UtcNow.ToString("O"));
            EditorSceneManager.OpenScene(StandaloneMapTools.Sphere);
            EditorApplication.isPlaying = true;
        }
        public static void RunFlatDetail()
        {
            Errors.Clear(); readyAt = 0;
            Directory.CreateDirectory(Root);
            SessionState.SetInt(Key, 3);
            SessionState.SetString("StandaloneMigrationStarted", DateTime.UtcNow.ToString("O"));
            EditorSceneManager.OpenScene(StandaloneMapTools.Flat);
            EditorApplication.isPlaying = true;
        }
        static void Tick()
        {
            int stage = SessionState.GetInt(Key, 0);
            if (stage == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (DateTime.TryParse(SessionState.GetString("StandaloneMigrationStarted", ""), out var started) &&
                (DateTime.UtcNow-started.ToUniversalTime()).TotalMinutes > 25)
            { Fail("Timed out loading map scenes."); return; }
            if (Errors.Count > 0) { Fail(string.Join("\n", Errors)); return; }
            if (stage == 2 && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                readyAt = 0;
                EditorSceneManager.OpenScene(StandaloneMapTools.Flat);
                SessionState.SetInt(Key, 3); EditorApplication.isPlaying = true; return;
            }
            if (stage == 4 && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                File.WriteAllText(Path.Combine(Root,"unity-validation-complete.txt"), "PASS: sphere and flat scenes loaded, saved native data checked, screenshots captured.\n");
                SessionState.SetInt(Key, 0); EditorApplication.Exit(0); return;
            }
            if (!EditorApplication.isPlaying) return;
            if (stage == 1)
            {
                var map = UnityEngine.Object.FindObjectOfType<StandaloneSphereMap>();
                if (!map || !map.IsReady) return;
                if (!map.Terrain.World.LoadedFromNativeSnapshot || map.NativeMap.Cities.Length != 882 || map.NativeMap.Regions.Count != 984)
                { Fail("Saved native geography counts do not match the migrated source."); return; }
                if (map.Terrain.DetailLoading || map.Terrain.ReadyChunks == 0) return;
                if (readyAt == 0) { readyAt = EditorApplication.timeSinceStartup; return; }
                if (EditorApplication.timeSinceStartup - readyAt < 8) return;
                if (!CheckScene()) return;
                Capture(map.mapCamera,"sphere.png");
                File.WriteAllText(Path.Combine(Root,"sphere-validation.json"), JsonUtility.ToJson(new SphereReport { cells=map.NativeMap.Count, cities=map.Cities.CityCount, regions=map.NativeMap.Regions.Count, native=true, startup=map.Terrain.LoadedStartupSnapshot, interaction=map.Politics.StartupDataLoadedFromDisk, chunks=map.Terrain.ReadyChunks }, true));
                map.Navigation.SetView(10,46,6500,true);
                readyAt = EditorApplication.timeSinceStartup;
                SessionState.SetInt(Key, 5);
            }
            if (stage == 5)
            {
                if (EditorApplication.timeSinceStartup - readyAt < 8) return;
                var map = UnityEngine.Object.FindObjectOfType<StandaloneSphereMap>();
                Capture(map.mapCamera,"sphere-globe.png");
                if (!CheckScene()) return;
                SessionState.SetInt(Key, 2); EditorApplication.isPlaying = false;
            }
            if (stage == 3)
            {
                var grid = UnityEngine.Object.FindObjectOfType<HexGrid>();
                if (!grid || grid.CellCountX != 1100 || grid.CityCount != 882) return;
                if (readyAt == 0) { readyAt = EditorApplication.timeSinceStartup; return; }
                if (EditorApplication.timeSinceStartup - readyAt < 15) return;
                if (!CheckScene()) return;
                Capture(Camera.main,"flat.png");
                File.WriteAllText(Path.Combine(Root,"flat-validation.txt"), $"PASS: {grid.CellCountX}x{grid.CellCountZ}, cities={grid.CityCount}, political={grid.HasPoliticalData}\n");
                var rig = UnityEngine.Object.FindObjectOfType<HexMapCamera>();
                typeof(HexMapCamera).GetMethod("SetZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(rig, new object[] { .90f });
                HexMapCamera.ValidatePosition();
                readyAt = EditorApplication.timeSinceStartup;
                SessionState.SetInt(Key, 6);
            }
            if (stage == 6)
            {
                var grid = UnityEngine.Object.FindObjectOfType<HexGrid>();
                if (EditorApplication.timeSinceStartup-readyAt < 15 || grid.IsOverviewMode || grid.ActiveChunkCount == 0 ||
                    grid.DesiredChunkActivationProgress < .999f || grid.PendingChunkBuildCount > 0) return;
                if (!CheckScene()) return;
                Capture(Camera.main,"flat-near.png");
                File.WriteAllText(Path.Combine(Root,"flat-near-validation.txt"), $"PASS: chunks={grid.ActiveChunkCount}, nearTerrain={grid.SurfaceStyle.UsesNearTerrain}, cities={grid.CityCount}\n");
                SessionState.SetInt(Key, 4); EditorApplication.isPlaying = false;
            }
        }
        static bool CheckScene()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                    { Fail("Missing script on " + transform.name); return false; }
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || !material.shader || !material.shader.isSupported)
                    { Fail("Missing or unsupported material on " + renderer.name); return false; }
                    foreach (var message in ShaderUtil.GetShaderMessages(material.shader))
                        if (message.severity.ToString() == "Error") { Fail(message.message); return false; }
                }
            return true;
        }
        static void Capture(Camera camera, string name)
        {
            if (!camera) throw new InvalidOperationException("Map camera missing.");
            var target = RenderTexture.GetTemporary(1280,720,24);
            var previous = camera.targetTexture; var active = RenderTexture.active;
            var image = new Texture2D(1280,720,TextureFormat.RGB24,false);
            try { camera.targetTexture=target; camera.Render(); RenderTexture.active=target; image.ReadPixels(new Rect(0,0,1280,720),0,0); image.Apply(); File.WriteAllBytes(Path.Combine(Root,name),image.EncodeToPNG()); }
            finally { camera.targetTexture=previous; RenderTexture.active=active; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.DestroyImmediate(image); }
        }
        static void Fail(string error) { File.WriteAllText(Path.Combine(Root,"unity-validation-failed.txt"),error); SessionState.SetInt(Key,0); EditorApplication.Exit(1); }
        [Serializable] class SphereReport { public int cells,cities,regions,chunks; public bool native,startup,interaction; }
    }
}
