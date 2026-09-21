using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WW2.SphericalTerrainPreview.Editor
{
    /// <summary>Fixed local scene operations. Requests never supply executable code or output paths.</summary>
    [InitializeOnLoad]
    public static partial class SphericalPreviewTools
    {
        public const string AssetRoot = "Assets/MapProjectionIntegration/SphericalTerrainPreview";
        public const string ScenePath = AssetRoot + "/Scenes/Spherical Terrain Preview.unity";
        const string SavedKey = "SphericalPreview.SavedScenes", OwnedKey = "SphericalPreview.OwnedPlay";
        const string StartupKey = "SphericalPreview.Startup", StopKey = "SphericalPreview.Stop";
        static readonly string Root = Directory.GetParent(Application.dataPath).FullName;
        static readonly string Requests = Path.Combine(Root, "Temp/SphericalTerrainPreview");
        static readonly string Artifacts = Path.Combine(Root, "Artifacts/SphericalTerrainPreview");
        static readonly Queue<string> Errors = new();
        static double nextRequest;
        static bool processing;
        static Response pendingCapture;
        static double captureSince;
        static readonly Dictionary<string, PendingWrite> PendingWrites = new(StringComparer.OrdinalIgnoreCase);
        sealed class PendingWrite
        {
            public string text;
            public int attempts;
            public double since, nextAttempt;
        }

        [Serializable] public sealed class Request
        {
            public string id, action, view;
            public float longitude, latitude, distance;
            public bool immediate = true;
        }
        [Serializable] public sealed class Response
        {
            public string id, action, phase, message, screenshot, uiScreenshot, report, status, loadingError;
            public bool ok, playing, compiling, importing, ready, detailLoading, detailReady, cameraSettled, presentationCameraHasTargetTexture;
            public int cells, pentagons, detailCells, trees, riverSegments, selectedCell, screenWidth, screenHeight;
            public int pendingChunks, readyChunks, cacheHits, buildCount, workerCount;
            public double firstVisibleSeconds, lastLoadSeconds;
            public float longitude, latitude, distance, targetDistance, detailFocusDistance;
            public long allocatedMemoryBytes, reservedMemoryBytes, graphicsDriverAllocatedBytes;
            public string graphicsDevice, graphicsApi, unityVersion;
            public string[] errors;
        }
        [Serializable] sealed class SavedScene { public string path; public bool isLoaded, isActive; }
        [Serializable] sealed class SavedSetup { public SavedScene[] scenes; }
        [Serializable] sealed class Startup { public Request request; public double since; }

        static SphericalPreviewTools()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Application.logMessageReceived += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += () => FinishValidation(false, "Assembly reload interrupted validation.");
        }

        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Build Scene")]
        static void BuildMenu() => RunMenu("build");
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Open Scene")]
        static void OpenMenu() => RunMenu("open");
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Play Preview")]
        static void PlayMenu() => RunMenu("play");
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Validate and Capture")]
        static void ValidateMenu() => RunMenu("validate");
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Review Water and Record Motion")]
        static void WaterReviewMenu() => RunMenu("water-review");
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Review Global Landforms")]
        static void LandformReviewMenu() => RunMenu(LandformReviewAction);
        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Stop and Restore Scenes")]
        static void StopMenu() => RunMenu("stop");

        static void RunMenu(string action)
        {
            Request request = new() { id = "menu-" + DateTime.UtcNow.Ticks, action = action };
            try { WriteResponse(Execute(request)); }
            catch (Exception exception) { WriteResponse(Status(request, false, exception.Message, "failed")); Debug.LogException(exception); }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Assert && type != LogType.Exception) return;
            if (Errors.Count >= 30) Errors.Dequeue();
            Errors.Enqueue(message.Length > 2000 ? message.Substring(0, 2000) : message);
        }

        static void Update()
        {
            AdvancePendingWrites();
            AdvanceStop(); AdvanceStartup(); AdvanceValidation(); AdvanceCapture();
            if (processing || EditorApplication.timeSinceStartup < nextRequest) return;
            nextRequest = EditorApplication.timeSinceStartup + .25;
            string path = Path.Combine(Requests, "request.json");
            if (!File.Exists(path)) return;
            processing = true; Request request = null;
            try
            {
                if (new FileInfo(path).Length > 4096) throw new InvalidOperationException("Request exceeds 4096 bytes.");
                string json = File.ReadAllText(path);
                request = JsonUtility.FromJson<Request>(json);
                if (request == null || !Regex.IsMatch(request.id ?? "", "^[A-Za-z0-9_-]{1,80}$"))
                    throw new InvalidOperationException("A unique id with 1–80 letters, digits, underscores or hyphens is required.");
                // Keep a valid asynchronous request queued through asset import
                // and compilation. Completion watchers need no repeated probes.
                if (request.action != "status" && request.action != "stop" && (EditorApplication.isCompiling || EditorApplication.isUpdating)) return;
                File.Delete(path);
                if (!Regex.IsMatch(json, "\"immediate\"\\s*:")) request.immediate = true;
                WriteResponse(Execute(request));
            }
            catch (Exception exception)
            {
                if (File.Exists(path)) File.Delete(path);
                WriteResponse(Status(request, false, exception.Message, "failed"));
            }
            finally { processing = false; }
        }

        static Response Execute(Request request)
        {
            if (request.action != "status" && request.action != "stop" && (EditorApplication.isCompiling || EditorApplication.isUpdating))
                throw new InvalidOperationException("Unity is compiling or importing; wait for completion before another operation.");
            if ((validation != null || pendingCapture != null) && request.action != "status" && request.action != "stop")
                throw new InvalidOperationException("Validation or capture currently owns the preview until its completion response.");
            switch (request.action)
            {
                case "status": return Status(request, true, "Current isolated spherical terrain preview state.");
                case "art-report":
                    Response art = Status(request, true, "Read actual live material bindings, mesh vertex attributes and surface contributions.");
                    art.report = CaptureArtDiagnostics(); return art;
                case "build": return Status(request, true, BuildScene());
                case "open":
                    if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Opening the scene requires Edit Mode.");
                    RequireSavedScenes();
                    if (!File.Exists(Path.Combine(Root, ScenePath))) BuildScene();
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    return Status(request, true, "Opened the isolated spherical terrain preview scene.");
                case "play":
                    StartPlay(request); return Status(request, true, "Owned preview startup scheduled; this response updates when the world is ready.", "starting");
                case "stop": return Stop(request);
                case "view":
                    RequirePreview(); ApplyView(RequireNavigation(), request);
                    return Status(request, true, "Applied the requested spherical camera view.");
                case "capture":
                    SphericalTerrainPreview terrain = RequirePreview();
                    Response capture = Status(request, true, "Captured the 1920x1080 camera. Waiting for the native Game View image.", "capturing");
                    capture.screenshot = Capture(RequireNavigation().PresentationCamera, request.id);
                    capture.uiScreenshot = Path.Combine(Artifacts, request.id + "-ui.png");
                    ScreenCapture.CaptureScreenshot(capture.uiScreenshot);
                    pendingCapture = capture; captureSince = EditorApplication.timeSinceStartup;
                    return capture;
                case "validate":
                    BeginValidation(request, RequirePreview(), RequireNavigation());
                    return Status(request, true, "Topology, geometry, view and camera checks started; completion updates this response and the report.", "validating");
                case "landform-review":
                    BeginValidation(request, RequirePreview(), RequireNavigation());
                    return Status(request, true, "Twelve fixed global terrain views and regional classification checks started.", "validating");
                case "water-review":
                    BeginValidation(request, RequirePreview(), RequireNavigation());
                    return Status(request, true, "Fixed coast and river views, source crest bindings and real-time animation recording started; completion updates this response and the reports.", "validating");
                default: throw new InvalidOperationException("Allowed actions: build, open, play, status, stop, view, capture, validate, water-review, landform-review, art-report.");
            }
        }

        static SphericalTerrainPreview FindPreview()
        {
            foreach (SphericalTerrainPreview item in Object.FindObjectsOfType<SphericalTerrainPreview>())
                if (item.gameObject.scene.path == ScenePath) return item;
            return null;
        }
        static SphericalTerrainPreview RequirePreview()
        {
            SphericalTerrainPreview terrain = FindPreview();
            if (!EditorApplication.isPlaying || !terrain || !terrain.IsReady)
                throw new InvalidOperationException("The isolated spherical terrain preview is not running and ready.");
            return terrain;
        }
        static SphericalPreviewCamera FindNavigation()
        {
            foreach (SphericalPreviewCamera item in Object.FindObjectsOfType<SphericalPreviewCamera>())
                if (item.gameObject.scene.path == ScenePath) return item;
            return null;
        }
        static SphericalPreviewCamera RequireNavigation()
        {
            SphericalPreviewCamera navigation = FindNavigation();
            if (!navigation) throw new InvalidOperationException("The preview camera is unavailable.");
            return navigation;
        }

        static Response Status(Request request, bool ok, string message, string phase = null)
        {
            SphericalTerrainPreview terrain = FindPreview(); SphericalPreviewCamera navigation = FindNavigation();
            Camera camera = navigation ? navigation.PresentationCamera : null;
            Response response = new()
            {
                id = request?.id ?? "invalid", action = request?.action ?? "invalid", ok = ok, message = message,
                phase = phase ?? (validation != null ? "validating" : terrain && terrain.IsReady ? "ready" : "idle"),
                playing = EditorApplication.isPlaying, compiling = EditorApplication.isCompiling, importing = EditorApplication.isUpdating,
                ready = terrain && terrain.IsReady, status = terrain ? terrain.Status : "No active sphere preview.",
                graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                allocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong(), reservedMemoryBytes = Profiler.GetTotalReservedMemoryLong(),
                graphicsDriverAllocatedBytes = Profiler.GetAllocatedMemoryForGraphicsDriver(),
                screenWidth = camera ? camera.pixelWidth : 0, screenHeight = camera ? camera.pixelHeight : 0,
                presentationCameraHasTargetTexture = camera && camera.targetTexture, errors = Errors.ToArray()
            };
            if (terrain)
            {
                response.detailCells = terrain.VisibleDetailCells; response.trees = terrain.TreeCount; response.riverSegments = terrain.RiverSegmentCount;
                response.selectedCell = terrain.SelectedCell;
                response.detailLoading = terrain.DetailLoading;
                response.pendingChunks = terrain.PendingChunks; response.readyChunks = terrain.ReadyChunks;
                response.cacheHits = terrain.CacheHits; response.buildCount = terrain.BuildCount; response.workerCount = terrain.WorkerCount;
                response.firstVisibleSeconds = terrain.FirstVisibleSeconds; response.lastLoadSeconds = terrain.LastLoadSeconds;
                response.loadingError = terrain.LoadingError;
                if (terrain.World != null) { response.cells = terrain.World.Count; response.pentagons = terrain.World.Pentagons.Length; }
            }
            if (navigation)
            {
                response.longitude = navigation.Longitude; response.latitude = navigation.Latitude;
                response.distance = navigation.Altitude; response.targetDistance = navigation.TargetAltitude; response.cameraSettled = navigation.IsSettled;
                response.detailFocusDistance = terrain ? (navigation.FocusDirection - terrain.DetailFocus).magnitude * terrain.radius : 0;
                response.detailReady = terrain && terrain.IsReady && (navigation.Altitude > 650f || (!terrain.DetailLoading && response.detailFocusDistance < 42f));
            }
            return response;
        }

        static void WriteResponse(Response response)
        {
            Directory.CreateDirectory(Requests);
            string json = JsonUtility.ToJson(response, true);
            string id = Regex.IsMatch(response.id ?? "", "^[A-Za-z0-9_-]{1,80}$") ? response.id : "invalid";
            WriteAtomic(Path.Combine(Requests, "response_" + id + ".json"), json);
            WriteAtomic(Path.Combine(Requests, "response.json"), json);
        }
        static void WriteAtomic(string path, string text)
        {
            try
            {
                WriteAtomicNow(path, text);
                PendingWrites.Remove(path);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (!PendingWrites.TryGetValue(path, out PendingWrite pending))
                {
                    pending = new PendingWrite { since = EditorApplication.timeSinceStartup };
                    PendingWrites.Add(path, pending);
                }
                // A newer state replaces an older queued snapshot for the same destination.
                pending.text = text;
                pending.nextAttempt = EditorApplication.timeSinceStartup + .1;
            }
        }
        static void WriteAtomicNow(string path, string text)
        {
            string temporary = path + ".writing";
            File.WriteAllText(temporary, text);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        static bool IsSharingViolation(IOException exception)
        {
            int code = exception.HResult & 0xffff;
            return code == 32 || code == 33;
        }
        static void AdvancePendingWrites()
        {
            if (PendingWrites.Count == 0) return;
            double now = EditorApplication.timeSinceStartup;
            foreach (string path in new List<string>(PendingWrites.Keys))
            {
                PendingWrite pending = PendingWrites[path];
                if (now < pending.nextAttempt) continue;
                try
                {
                    WriteAtomicNow(path, pending.text);
                    PendingWrites.Remove(path);
                }
                catch (IOException exception) when (IsSharingViolation(exception) && ++pending.attempts < 20 && now - pending.since < 20)
                {
                    // Retry on later editor updates; never sleep or interrupt validation for a reader's short lock.
                    pending.nextAttempt = now + Math.Min(1.0, .1 * pending.attempts);
                }
                catch (Exception exception)
                {
                    PendingWrites.Remove(path);
                    string recovery = path + ".unpublished-" + DateTime.UtcNow.Ticks + ".json";
                    try { File.WriteAllText(recovery, pending.text); }
                    catch (Exception recoveryError) { Debug.LogError("Spherical preview report recovery failed: " + recoveryError.Message); }
                    Debug.LogError("Could not publish spherical preview report after bounded retries: " + path
                        + ". Latest snapshot retained at " + recovery + ". " + exception.Message);
                }
            }
        }
        static void RequireSavedScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isDirty || string.IsNullOrEmpty(scene.path))
                    throw new InvalidOperationException("Unsaved scene: " + scene.name + ". No scenes were saved or closed automatically.");
            }
        }
        static void RememberScenes()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(SavedKey, ""))) return;
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            SavedSetup saved = new() { scenes = new SavedScene[setup.Length] };
            for (int i = 0; i < setup.Length; i++) saved.scenes[i] = new SavedScene { path = setup[i].path, isLoaded = setup[i].isLoaded, isActive = setup[i].isActive };
            SessionState.SetString(SavedKey, JsonUtility.ToJson(saved));
        }
        static void RestoreScenes()
        {
            string json = SessionState.GetString(SavedKey, ""); if (string.IsNullOrEmpty(json)) return;
            RequireSavedScenes(); SavedSetup saved = JsonUtility.FromJson<SavedSetup>(json);
            if (saved?.scenes == null || saved.scenes.Length == 0) throw new InvalidOperationException("Saved scene setup is unavailable.");
            SceneSetup[] setup = new SceneSetup[saved.scenes.Length];
            for (int i = 0; i < setup.Length; i++)
            {
                SavedScene item = saved.scenes[i];
                if (!File.Exists(Path.Combine(Root, item.path))) throw new InvalidOperationException("Saved scene no longer exists: " + item.path);
                setup[i] = new SceneSetup { path = item.path, isLoaded = item.isLoaded, isActive = item.isActive };
            }
            EditorSceneManager.RestoreSceneManagerSetup(setup); SessionState.EraseString(SavedKey);
        }
        static void StartPlay(Request request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("An existing Play session is active.");
            RequireSavedScenes();
            if (!File.Exists(Path.Combine(Root, ScenePath))) BuildScene();
            RememberScenes(); Errors.Clear();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(OwnedKey, true);
            SessionState.SetString(StartupKey, JsonUtility.ToJson(new Startup { request = request, since = EditorApplication.timeSinceStartup }));
            EditorApplication.isPlaying = true;
        }
        static void AdvanceStartup()
        {
            string json = SessionState.GetString(StartupKey, ""); if (string.IsNullOrEmpty(json)) return;
            Startup startup = JsonUtility.FromJson<Startup>(json); SphericalTerrainPreview terrain = FindPreview();
            if (terrain && !string.IsNullOrEmpty(terrain.LoadingError))
            {
                SessionState.EraseString(StartupKey);
                WriteResponse(Status(startup.request, false, "Spherical terrain startup failed: " + terrain.LoadingError, "failed"));
                return;
            }
            if (EditorApplication.isPlaying && terrain && terrain.IsReady)
            {
                SessionState.EraseString(StartupKey); ApplyView(RequireNavigation(), startup.request);
                WriteResponse(Status(startup.request, true, "The spherical world and initial terrain are ready.", "ready"));
            }
            else if (EditorApplication.timeSinceStartup - startup.since > 900)
            {
                SessionState.EraseString(StartupKey);
                WriteResponse(Status(startup.request, false, "The spherical preview did not become ready within 15 minutes.", "failed"));
            }
        }
        static Response Stop(Request request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode && !SessionState.GetBool(OwnedKey, false))
                throw new InvalidOperationException("This bridge does not own the current Play session.");
            FinishValidation(false, "Stopped by request."); pendingCapture = null; SessionState.EraseString(StartupKey);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetString(StopKey, JsonUtility.ToJson(request)); EditorApplication.isPlaying = false;
                return Status(request, true, "Owned preview stopping; completion follows saved-scene restoration.", "stopping");
            }
            RestoreScenes(); SessionState.SetBool(OwnedKey, false); SessionState.EraseString(StopKey);
            return Status(request, true, "Owned preview stopped and saved scenes restored.", "idle");
        }
        static void AdvanceStop()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string json = SessionState.GetString(StopKey, ""); if (string.IsNullOrEmpty(json)) return;
            Request request = JsonUtility.FromJson<Request>(json);
            try { RestoreScenes(); SessionState.SetBool(OwnedKey, false); WriteResponse(Status(request, true, "Owned preview stopped and saved scenes restored.", "idle")); }
            catch (Exception exception) { WriteResponse(Status(request, false, "Preview stopped; scene restoration failed: " + exception.Message, "failed")); }
            finally { SessionState.EraseString(StopKey); }
        }
        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) FinishValidation(false, "Play Mode ended before validation completed.");
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(OwnedKey, false)) return;
            SessionState.EraseString(StartupKey);
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (!string.IsNullOrEmpty(SessionState.GetString(StopKey, ""))) { AdvanceStop(); return; }
                try { RestoreScenes(); SessionState.SetBool(OwnedKey, false); }
                catch (Exception exception) { Debug.LogWarning("Spherical preview stopped; " + exception.Message); }
            };
        }
        static void ApplyView(SphericalPreviewCamera navigation, Request request)
        {
            if (!string.IsNullOrEmpty(request.view))
            {
                if (!Regex.IsMatch(request.view, "^(globe|europe|eastasia|coast|river|continent|pentagon|tibet|tibet-wide|forest|desert|plateau-river|plateau-edge|desert-plateau|single-mountain)$"))
                    throw new InvalidOperationException("Unknown fixed spherical view: " + request.view);
                navigation.ApplyPreset(request.view, request.immediate);
            }
            else if (request.distance > 0)
            {
                if (float.IsNaN(request.longitude) || float.IsInfinity(request.longitude) || float.IsNaN(request.latitude)
                    || float.IsInfinity(request.latitude) || float.IsNaN(request.distance) || float.IsInfinity(request.distance))
                    throw new InvalidOperationException("The requested camera coordinates must be finite.");
                navigation.SetView(request.longitude, request.latitude, request.distance, request.immediate);
            }
        }
        static void AdvanceCapture()
        {
            if (pendingCapture == null) return;
            if (File.Exists(pendingCapture.uiScreenshot))
            {
                pendingCapture.phase = "complete"; pendingCapture.message = "Captured the 1920x1080 camera and native Game View image.";
                WriteResponse(pendingCapture); pendingCapture = null;
            }
            else if (EditorApplication.timeSinceStartup - captureSince > 60)
            {
                pendingCapture.phase = "failed"; pendingCapture.ok = false;
                pendingCapture.message = "The camera image was captured, but the native Game View capture did not complete within 60 seconds.";
                WriteResponse(pendingCapture); pendingCapture = null;
            }
        }
        static string Capture(Camera camera, string name)
        {
            Directory.CreateDirectory(Artifacts); string path = Path.Combine(Artifacts, name + "-camera.png");
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(1920, 1080, 24, RenderTextureFormat.ARGB32);
            Texture2D image = null;
            try
            {
                SphericalTerrainPreview terrain = FindPreview();
                if (terrain) terrain.RenderVegetation(camera);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target; image = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0, false); image.Apply(false, false);
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); if (image) Object.DestroyImmediate(image); }
            return path;
        }
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        static T Load<T>(string path) where T : Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!asset) throw new InvalidOperationException("Required preview asset is missing: " + path);
            return asset;
        }
        static string BuildScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Building requires Edit Mode.");
            HexTerrainStyle style = Load<HexTerrainStyle>("Assets/HexMapPackage/Materials/Terrain/Default Hex Terrain Style.asset");
            if (!style.nearTerrainProfile || !style.nearTerrainProfile.IsReady) throw new InvalidOperationException("The current terrain style has no ready near-terrain profile.");
            EnsureFolder(AssetRoot + "/Scenes");
            Scene previous = SceneManager.GetActiveScene();
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.IsValid() || !scene.isLoaded;
            bool exists = File.Exists(Path.Combine(Root, ScenePath));
            if (opened) scene = exists ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive) : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                if (scene.isDirty && exists) throw new InvalidOperationException("The preview has unsaved scene edits; no builder changes were applied.");
                SceneManager.SetActiveScene(scene);
                SphericalTerrainPreview terrain = null;
                foreach (GameObject item in scene.GetRootGameObjects()) { terrain = item.GetComponentInChildren<SphericalTerrainPreview>(true); if (terrain) break; }
                if (!terrain) terrain = new GameObject("Spherical Terrain Preview").AddComponent<SphericalTerrainPreview>();
                terrain.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); terrain.transform.localScale = Vector3.one;
                terrain.radius = 3300f; terrain.recursion = 5; terrain.terrainStyle = style; terrain.terrainProfile = style.nearTerrainProfile;
                terrain.waterSourceMaterial = Load<Material>("Assets/HexMapPackage/Materials/Water.mat");
                terrain.riverSourceMaterial = Load<Material>("Assets/HexMapPackage/Materials/Hex Relief.mat");
                terrain.satelliteColor = Load<Texture2D>("Assets/MapProjectionIntegration/SatellitePreview/Data/SatelliteEarthColor.png");
                terrain.satelliteRelief = Load<Texture2D>("Assets/MapProjectionIntegration/SatellitePreview/Data/SatelliteEarthRelief.png");
                SphericalPreviewCamera navigation = terrain.GetComponentInChildren<SphericalPreviewCamera>(true);
                if (!navigation)
                {
                    GameObject go = new("Spherical Preview Camera"); go.transform.SetParent(terrain.transform, false);
                    Camera camera = go.AddComponent<Camera>(); camera.tag = "MainCamera";
                    camera.fieldOfView = 45; camera.nearClipPlane = .12f; camera.farClipPlane = 20000;
                    camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.014f, .028f, .041f);
                    camera.allowHDR = false; camera.allowMSAA = true;
                    UniversalAdditionalCameraData data = go.AddComponent<UniversalAdditionalCameraData>(); data.renderShadows = true; data.renderPostProcessing = false;
                    navigation = go.AddComponent<SphericalPreviewCamera>();
                }
                navigation.terrain = terrain;
                Light sun = null;
                foreach (Light light in terrain.GetComponentsInChildren<Light>(true)) if (light.type == LightType.Directional) { sun = light; break; }
                if (!sun)
                {
                    GameObject go = new("Sun · 太阳光"); go.transform.SetParent(terrain.transform, false); sun = go.AddComponent<Light>();
                }
                // Same art lighting as HexNearTerrainLighting; only its up axis is radial.
                sun.type = LightType.Directional; sun.color = new Color(1f, .965f, .91f); sun.intensity = 1.06f;
                sun.shadows = LightShadows.Soft; sun.shadowStrength = .78f; sun.shadowBias = .06f; sun.shadowNormalBias = .32f;
                navigation.sunElevation = 48f;
                navigation.sun = sun;
                SphericalPreviewHUD hud = terrain.GetComponent<SphericalPreviewHUD>(); if (!hud) hud = terrain.gameObject.AddComponent<SphericalPreviewHUD>();
                hud.terrain = terrain; hud.navigation = navigation; navigation.hud = hud;
                hud.uiFont = Load<Font>("Assets/Plugins/Words/SongTi/1_Asset/4_Font/sarasa-gothic-sc-regular.ttf");
                hud.frameTexture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/MapProjectionIntegration/SatellitePreview/UI/BrassInstrumentFrame.png");
                RenderSettings.sun = sun; RenderSettings.skybox = null; RenderSettings.fog = false; RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(.48f, .57f, .68f); RenderSettings.ambientEquatorColor = new Color(.30f, .34f, .36f);
                RenderSettings.ambientGroundColor = new Color(.18f, .17f, .15f);
                // Initialize a useful scene-view pose without running runtime terrain generation in Edit Mode.
                Vector3 direction = SphericalPreviewCamera.Direction(navigation.initialLongitude, navigation.initialLatitude);
                Vector3 north = Vector3.ProjectOnPlane(Vector3.up, direction).normalized;
                Vector3 offset = direction * navigation.initialAltitude - north * navigation.initialAltitude * .75f;
                navigation.transform.SetPositionAndRotation(direction * (terrain.radius + 1.5f) + offset, Quaternion.LookRotation(-offset, north));
                sun.transform.rotation = Quaternion.LookRotation(-(direction + north).normalized, north);
                EditorUtility.SetDirty(terrain); EditorUtility.SetDirty(navigation); EditorUtility.SetDirty(hud);
                if (!EditorSceneManager.SaveScene(scene, ScenePath, false)) throw new InvalidOperationException("Could not save the isolated spherical terrain scene.");
                return "Built the isolated true-sphere terrain scene with the existing art profiles, camera controls and a shadow-casting sun.";
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (opened) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
