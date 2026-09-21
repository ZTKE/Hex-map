using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using WW2.SphericalTerrainPreview;

namespace ZTKE.HexMap.Standalone
{
    /// <summary>Loads the paired native geography without creating a flat HexGrid or game session.</summary>
    [DefaultExecutionOrder(1000)]
    public sealed class StandaloneSphereMap : MonoBehaviour
    {
        public WW2.SphericalTerrainPreview.SphericalTerrainPreview Terrain;
        public SphericalPreviewCamera Navigation;
        [Tooltip("Retains shaders used by runtime-created sphere and city materials in player builds.")]
        public Shader[] presentationShaders;
        public Camera mapCamera => Navigation ? Navigation.PresentationCamera : null;
        public NativeGameplayMap NativeMap { get; private set; }
        public bool IsReady => NativeMap != null && Politics != null && Politics.IsInitialized;
        public SphericalGameplayPolitics Politics { get; private set; }
        public SphericalGameplayCities Cities { get; private set; }
        public string Error { get; private set; }
        readonly CancellationTokenSource lifetime = new();

        void Awake()
        {
            if (!Terrain) Terrain = GetComponent<WW2.SphericalTerrainPreview.SphericalTerrainPreview>();
            if (!Navigation) Navigation = FindObjectOfType<SphericalPreviewCamera>();
            if (Terrain) Terrain.NativeTerrainGlobe = true;
            if (Terrain) Terrain.ConfigureNaturalSatellite(new Color(1f, 1f, .97f), new Color(.035f, .16f, .36f),
                new Color(.91f, .87f, .72f), new Color(.24f, .56f, 1f), 1.08f, 1.10f, .90f, .85f, .27f, .60f);
            if (!Terrain || !Navigation || !SphericalWorld.HasNativeSnapshot(5))
            {
                Error = "The saved R5 sphere, terrain renderer and navigation camera are required.";
                if (Terrain) Terrain.enabled = false;
                Debug.LogError(Error); enabled = false;
            }
        }

        IEnumerator Start()
        {
            while (!Terrain.IsReady)
            {
                if (!string.IsNullOrEmpty(Terrain.LoadingError)) { Error = Terrain.LoadingError; yield break; }
                yield return null;
            }
            string path = SphericalGameplaySnapshot.DefaultPath;
            var world = Terrain.World;
            var token = lifetime.Token;
            Task<NativeGameplayMap> load = Task.Run(() => SphericalGameplaySnapshot.Load(path, world, token), token);
            _ = load.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            while (!load.IsCompleted) yield return null;
            if (load.IsCanceled) yield break;
            if (load.IsFaulted) { Error = load.Exception.GetBaseException().ToString(); Debug.LogError(Error); yield break; }
            NativeMap = load.Result;
            Politics = new SphericalGameplayPolitics();
            Politics.Initialize(Terrain, NativeMap, SphereCountryPalette.Create());
            Cities = gameObject.AddComponent<SphericalGameplayCities>();
            Cities.Initialize(this);
            Debug.Log($"Standalone native sphere: {NativeMap.Count} cells, {NativeMap.Cities.Length} cities, {NativeMap.Regions.Count} regions.");
        }

        void LateUpdate()
        {
            if (Politics == null) return;
            Politics.UpdatePresentation(Navigation.Altitude, Terrain.SelectedCell);
            if (!string.IsNullOrEmpty(Politics.LoadingError) && Error == null)
            { Error = Politics.LoadingError; Debug.LogError(Error); }
        }

        public bool IsVisible(Vector3 position, Camera camera)
        {
            if (!camera || !Terrain) return false;
            Vector3 origin = camera.transform.position, delta = position - origin;
            float length = delta.magnitude;
            if (length < .001f) return true;
            float b = Vector3.Dot(origin, delta / length);
            float d = b * b - (origin.sqrMagnitude - Terrain.radius * Terrain.radius);
            return !(d > 0 && -b - Mathf.Sqrt(d) > 0 && -b - Mathf.Sqrt(d) < length - .5f);
        }

        void OnGUI()
        {
            var box = new Rect(12, Screen.height - 76, 520, 64);
            string status = Error ?? (IsReady
                ? $"Native sphere: {NativeMap.Count:N0} cells | {NativeMap.Cities.Length} cities | {NativeMap.Regions.Count} regions"
                : "Loading saved native geography and borders...");
            int id = Terrain ? Terrain.SelectedCell : -1;
            if (NativeMap != null && NativeMap.Valid(id))
            {
                var tile = NativeMap.Tiles[id];
                status += $"\nCell {id} | Country {tile.Country} | Region {tile.Region} | {tile.CityName}";
            }
            GUI.Box(box, status);
        }

        void OnDestroy() { lifetime.Cancel(); Politics?.Dispose(); lifetime.Dispose(); }
    }
}
