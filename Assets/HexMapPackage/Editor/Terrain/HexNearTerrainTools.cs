using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Local, fixed-purpose preview bridge. It cannot execute scripts, accept paths,
/// overwrite an existing scene, or discard unsaved user scenes.
/// </summary>
[InitializeOnLoad]
public static partial class HexNearTerrainTools
{
	public const string ScenePath = "Assets/HexMapPackage/Scenes/Civ6 Near Terrain Showcase.unity";
	const string StylePath = "Assets/HexMapPackage/Materials/Terrain/Default Hex Terrain Style.asset";
	const string OriginKey = "HexNearTerrainTools.OriginalScenes";
	const string OwnPlayKey = "HexNearTerrainTools.OwnPlay";
	const string GameplayKey = "HexNearTerrainTools.Gameplay";
	const string LaunchScenePath = "Assets/WarAndPeace/Scenes/Lanuch_0.unity";
	const string MenuScenePath = "Assets/WarAndPeace/Scenes/Menu_1.unity";
	const string GameplayScenePath = "Assets/WarAndPeace/Scenes/Game_2.unity";
	static readonly string Root = Directory.GetParent(Application.dataPath).FullName;
	static readonly string RequestDirectory = Path.Combine(Root, "Temp", "Civ6NearPreview");
	static readonly string ArtifactDirectory = Path.Combine(Root, "Artifacts", "Civ6Reference", "Preview");
	static readonly Queue<string> RecentErrors = new();
	static double nextPoll;
	static bool processing;
	static int lastCaptureWidth, lastCaptureHeight;
	static string lastCaptureCoveragePath;
	static Measurement measurement;
	static TeardownMeasurement teardown;
	static WorldReview worldReview;
	[Serializable] sealed class ReviewImage { public string view, path; public GameplayMetrics metrics; }
	[Serializable] sealed class ReviewReport { public bool completed, passed; public string id, timestamp, error; public ReviewImage[] images; }
	sealed class WorldReview
	{
		public Request request;
		public string[] views;
		public string originalView;
		public int index, appliedFrame;
		public double deadline, earliestCapture;
		public readonly List<ReviewImage> images = new();
	}
	const int MeasurementWarmupFrames = 12;
	const int MeasurementFrames = 60;

	[Serializable] sealed class Request { public string id; public string action; public string view; }
	[Serializable] sealed class SceneState { public string path; public string name; public bool isDirty; public bool isActive; }
	[Serializable] sealed class SavedScene { public string path; public bool isLoaded; public bool isActive; }
	[Serializable] sealed class SceneSetupRecord { public SavedScene[] scenes; }
	[Serializable] sealed class GameplaySession
	{
		public string id, view, stage, message, originalSettingsPath, originalSettingsHash, finalSettingsHash;
		public string startedUtc, stoppedUtc;
		public bool active, enteredPlay, redirectedSettings, settingsUnchanged, reachedNearReady, reachedViewReady, originalScenesRestored;
		public bool teardownRequested, teardownCompleted, teardownPassed;
		public string teardownPath;
		public int firstPlayFrame, targetFrameRate, vSyncCount, sleepTimeout, focusCell = -1;
		public int firstCoreReadyFrame = -1, viewAppliedFrame;
		public float requestedStickDistance, requestedZoom, appliedStickDistance, appliedZoom;
		public Vector3 appliedFocus;
		public bool runInBackground;
		public float timeScale;
		public double startedTime;
		public string[] errors;
	}
	[Serializable] sealed class GameplayMetrics
	{
		public string stage, message, view, renderTier, settingsSavePath, originalSettingsHash, finalSettingsHash;
		public bool ownedSession, mapLoaded, frameworkReady, gameplayCoreInitialized, simulationRunning, nearReady, settingsSaveRedirected;
		public bool viewReady, reachedNearReady, viewTransitioning, teardownRequested, teardownCompleted, teardownPassed;
		public string requestedRenderTier, teardownPath;
		public bool settingsUnchangedAfterStop, originalScenesRestored, hasMissingChunks, streamingCoverActive, overviewHeldUnderDetail, nearLightingActive;
		public int cellCount, cities, countries, pendingArmyEntities, activeChunks, focusCell;
		public int generatedVegetationInstances, vegetationDrawCalls, vegetationDrawnInstances, vegetationShadowedInstances;
		public int vegetationShadowOnlyInstances, vegetationShadowOnlyDrawCalls;
		public float chunkActivationProgress, stickDistance, zoom;
		public float requestedStickDistance, requestedZoom, appliedStickDistance, appliedZoom;
		public int viewAppliedFrame;
		public bool cameraChangedSinceView;
		public CameraMetrics camera;
		public string[] errors;
	}
	[Serializable] sealed class NearSettings
	{
		public string assetPath;
		public int shapeLayers, shapeResolution, materialLayers, materialResolution;
		public float mountainHeight, hillHeight, plateauHeight, plateauRelief, plateauWeathering, desertMountainHeight, mountainFootprint, hillFootprint,
			desertMountainFootprint, ridgeWidth, ridgeStrength, mountainRangeStrength, mountainRangeWidth, materialTiling, materialDetail;
	}
	[Serializable] sealed class VegetationMetrics
	{
		public int generatedInstances, activeChunkRenderers, cachedSpeciesBatches;
		public int lastCameraId, lastDrawCalls, lastDrawnInstances, lastShadowedInstances;
		public int lastShadowOnlyInstances, lastShadowOnlyDrawCalls;
		public bool lastCountersMatchPreviewCamera;
		public float lodDistance, shadowDistance, drawDistance;
	}
	[Serializable] sealed class CameraMetrics
	{
		public Vector3 position, eulerAngles, focus;
		public float viewDistance, fieldOfView;
		public int instanceId, pixelWidth, pixelHeight, cullingMask;
		public bool enabled, allowMSAA, allowHDR, useOcclusionCulling, renderShadows;
		public string graphicsPipeline, qualityPipeline;
	}
	[Serializable] sealed class Response
	{
		public string id, action, message, screenshot, timestamp, validationPath, performancePath, teardownPath, geographyPath, coveragePath;
		public bool testingTeardown;
		public bool ok, playing, transitioning, compiling, updating, showcaseReady, nearProfileReady, measuring;
		public int generatedCells, measurementFrames, measurementTargetFrames, measurementWarmupRemaining;
		public int screenshotWidth, screenshotHeight;
		public SceneState[] scenes;
		public string[] shaderMessages, recentErrors;
		public NearSettings nearSettings;
		public VegetationMetrics vegetationMetrics;
		public CameraMetrics cameraMetrics;
		public GameplayMetrics gameplay;
	}

	static HexNearTerrainTools()
	{
		InitializeStreamingPerformance();
		EditorApplication.update += Poll;
		EditorApplication.playModeStateChanged += OnPlayModeChanged;
		Application.logMessageReceived += OnLog;
		AssemblyReloadEvents.beforeAssemblyReload += () => FinishMeasurement(false, "Assembly reload interrupted measurement.", true);
		AssemblyReloadEvents.beforeAssemblyReload += () => FinishTeardown(false, "Assembly reload interrupted teardown validation.", true);
	}

	static void OnLog(string message, string stackTrace, LogType type)
	{
		if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
		if (RecentErrors.Count == 24) RecentErrors.Dequeue();
		RecentErrors.Enqueue(message.Length > 1500 ? message.Substring(0, 1500) : message);
		GameplaySession gameplay = ReadGameplaySession();
		if (gameplay != null && gameplay.active)
		{
			List<string> errors = new(gameplay.errors ?? Array.Empty<string>());
			if (errors.Count == 24) errors.RemoveAt(0);
			errors.Add(message.Length > 1500 ? message.Substring(0, 1500) : message);
			gameplay.errors = errors.ToArray();
			SaveGameplaySession(gameplay);
		}
	}

	static void Poll()
	{
		if (processing || EditorApplication.timeSinceStartup < nextPoll) return;
		nextPoll = EditorApplication.timeSinceStartup + 0.25;
		PollMeasurement();
		PollTeardown();
		PollGameplay();
		AdvanceWorldReview();
		string requestPath = Path.Combine(RequestDirectory, "request.json");
		if (!File.Exists(requestPath)) return;
		processing = true;
		Request request = null;
		try
		{
			if (new FileInfo(requestPath).Length > 4096)
				throw new InvalidOperationException("Request exceeds the 4096-byte limit.");
			string json = File.ReadAllText(requestPath);
			// The sender must finish a temporary file and rename it to request.json.
			File.Delete(requestPath);
			request = JsonUtility.FromJson<Request>(json);
			if (request == null || !Regex.IsMatch(request.id ?? "", "^[A-Za-z0-9_-]{1,80}$"))
				throw new InvalidOperationException("id must contain 1-80 letters, digits, underscores or hyphens.");
			Response response = Execute(request);
			WriteResponse(response);
		}
		catch (Exception exception)
		{
			// Consume malformed requests too; never repeatedly execute a bad file.
			if (File.Exists(requestPath)) File.Delete(requestPath);
			Response response = Status(request, false, exception.Message);
			WriteResponse(response);
		}
		finally { processing = false; }
	}

	static Response Execute(Request request)
	{
		string screenshot = null;
		string validationPath = null;
		string geographyPath = null;
		bool successful = true;
		string message;
		if (streamingPerformance != null && request.action != "status" && request.action != "gameplay-status" && request.action != "stop")
			throw new InvalidOperationException("A gameplay streaming measurement is active; await its completion report.");
		if (worldReview != null && request.action != "status" && request.action != "gameplay-status" && request.action != "stop")
			throw new InvalidOperationException("A world review is active; await its completion report.");
		if (request.action != "status" && request.action != "gameplay-status" && (EditorApplication.isCompiling || EditorApplication.isUpdating))
			throw new InvalidOperationException("Unity is compiling or importing; wait and send a new request.");
		if (measurement != null && request.action != "status" && request.action != "stop")
			throw new InvalidOperationException("Measurement is active; only status or stop is allowed until it finishes.");
		if (teardown != null && request.action != "status" && request.action != "gameplay-status" && request.action != "stop")
			throw new InvalidOperationException("Camera teardown validation is active; only status, gameplay-status or stop is allowed until it finishes.");
		switch (request.action)
		{
			case "status": message = "Status only; no scene or asset changed."; break;
			case "refresh":
				if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Refresh requires Edit Mode.");
				EditorApplication.delayCall += () => AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
				message = "Scheduled one asset refresh; wait for compilation before sending another operation.";
				break;
			case "gameplay-status": message = "Read the actual Game_2 scene, framework, terrain and camera state."; break;
			case "gameplay-measure-streaming": BeginStreamingPerformance(request); message = "Measuring fixed Game_2 cold/warm transitions and an eight-chunk return route. Completion is written to StreamingPerformance-" + request.id + ".json."; break;
			case "gameplay-measure-hierarchy": BeginStreamingHierarchyProbe(request); message = "Measuring deferred hierarchy callbacks on temporary runtime objects; await StreamingHierarchy-ID.json."; break;
			case "gameplay-play": BeginGameplay(request); message = "Starting the normal Lanuch_0 -> Menu_1 -> Game_2 flow. Poll gameplay-status until gameplay.viewReady is true."; break;
			case "gameplay-view": SetGameplayView(request.view); message = "Moved the real gameplay camera to a fixed view; wait for gameplay.viewReady before capturing."; break;
			case "gameplay-capture": screenshot = CaptureGameplay(); message = "Captured the actual Game_2 map camera, excluding its separate FairyGUI Stage Camera."; break;
			case "gameplay-review": BeginWorldReview(request); message = "Started fixed world views. Completion is written to WorldReview-" + request.id + ".json; no repeated status requests are needed."; break;
			case "beauty-validate":
				var beautyShowcase = Object.FindObjectOfType<HexNearTerrainShowcase>();
				HexMountainRangeValidation.Run(beautyShowcase, Path.Combine(ArtifactDirectory, "MountainRange-" + request.id + ".json"), out bool rangesPassed, out string rangeSummary);
				HexPlateauSurfaceValidation.Run(beautyShowcase, Path.Combine(ArtifactDirectory, "Plateau-" + request.id + ".json"), out bool plateauPassed, out string plateauSummary);
				validationPath = ValidateSurface(out bool surfacePassed, out string surfaceSummary);
				successful = rangesPassed && plateauPassed && surfacePassed;
				message = rangeSummary + "\n" + plateauSummary + "\n" + surfaceSummary;
				break;
			case "gameplay-teardown": BeginTeardown(request); message = "Destroy requested for the owned Game_2 HexMapCamera component; observing its remaining camera for 12 rendered frames. Stop afterwards to restore the saved scenes."; break;
			case "geography-validate": geographyPath = ValidateGeography(request.id, out successful, out message); break;
			case "river-validate":
				validationPath = HexWorldRiverValidation.Run(FindGameplayComponent<HexGrid>(),
					Path.Combine(ArtifactDirectory, "RiverValidation-" + request.id + ".json"), out successful, out message);
				break;
			case "river-continuity-validate":
				validationPath = HexRiverContinuityValidation.Run(FindGameplayComponent<HexGrid>(),
					Path.Combine(ArtifactDirectory, "RiverContinuity-" + request.id + ".json"), out successful, out message);
				break;
			case "import": ImportReference(); message = "Imported the local terrain reference and assigned the default profile."; break;
			case "land-import":
				if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Land import requires Edit Mode.");
				HexCiv6LandMaterialImporter.BuildAndAssignDefault();
				validationPath = HexCiv6LandMaterialImporter.ValidateImported();
				message = "Imported and validated the 18 local Civ6 land material layers without rebuilding geometry or vegetation.";
				break;
			case "river-material-validate":
				validationPath = HexCiv6RiverMaterialValidation.Validate();
				message = "Validated river source textures, fixed bindings, GPU pixel decoding and Relief shader diagnostics.";
				break;
			case "land-validate":
				validationPath = HexCiv6LandMaterialImporter.ValidateImported();
				message = "Validated live land texture formats, masks, mipmaps and shader bindings.";
				break;
			case "build": message = BuildScene(); break;
			case "open": OpenScene(); message = "Opened the saved showcase scene."; break;
			case "play":
				if (EditorApplication.isPlayingOrWillChangePlaymode)
					throw new InvalidOperationException("Unity is already playing or changing play mode.");
				CompletePreviousSession();
				OpenScene();
				SessionState.SetBool(OwnPlayKey, true);
				EditorApplication.isPlaying = true;
				message = "Showcase play requested; poll status until showcaseReady is true.";
				break;
			case "capture": screenshot = Capture(request.view); message = "Captured the showcase camera."; break;
			case "validate": validationPath = ValidateSurface(out successful, out message); break;
			case "range-validate":
				validationPath = HexMountainRangeValidation.Run(UnityEngine.Object.FindObjectOfType<HexNearTerrainShowcase>(),
					Path.Combine(ArtifactDirectory,"MountainRangeValidation.json"),out successful,out message);
				break;
			case "plateau-validate":
				validationPath = HexPlateauSurfaceValidation.Run(UnityEngine.Object.FindObjectOfType<HexNearTerrainShowcase>(),
					Path.Combine(ArtifactDirectory,"PlateauValidation.json"),out successful,out message);
				break;
			case "measure":
				BeginMeasurement(request);
				message = "Editor Play measurement accepted: 12 camera-frame warmup, then 60 rendered camera frames. Poll status until measuring is false, then read the matching response ID.";
				break;
			case "stop":
				FinishStreamingPerformance(false, "Stopped before streaming measurement completed.");
				if (worldReview != null) FinishWorldReview("Review stopped before completion.");
				FinishMeasurement(false, "Stopped before measurement completed.");
				FinishTeardown(false, "Stopped before teardown validation completed.");
				if (EditorApplication.isPlayingOrWillChangePlaymode)
				{
					if (!SessionState.GetBool(OwnPlayKey, false))
						throw new InvalidOperationException("Refusing to stop a play session this tool did not start.");
					GameplaySession gameplay = ReadGameplaySession();
					if (gameplay != null && gameplay.active && EditorApplication.isPlaying &&
						!UnityEngine.Object.FindObjectOfType<HexNearTerrainShowcase>())
					{
						RedirectGameplaySettings(gameplay);
						gameplay.stage = "stopping";
						SaveGameplaySession(gameplay);
					}
					EditorApplication.isPlaying = false;
					message = "Showcase stop requested; the previously saved scene setup will be restored.";
				}
				else
				{
					RestoreOriginalScenes();
					// Domain reload or another editor callback can interrupt the
					// deferred Play-exit callback. Stop remains an idempotent cleanup.
					FinishGameplaySession(true);
					message = "Restored the previous saved scene setup.";
				}
				break;
			default: throw new InvalidOperationException("Allowed actions: status, import, build, open, play, capture, validate, measure, gameplay-play, gameplay-status, gameplay-view, gameplay-capture, gameplay-teardown, geography-validate, stop.");
		}
		Response response = Status(request, successful, message);
		response.screenshot = screenshot;
		if (!string.IsNullOrEmpty(screenshot)) { response.screenshotWidth = lastCaptureWidth; response.screenshotHeight = lastCaptureHeight; response.coveragePath = lastCaptureCoveragePath; }
		response.validationPath = validationPath;
		response.geographyPath = geographyPath;
		if (request.action == "capture" || request.action == "validate" || request.action == "gameplay-capture" || request.action == "gameplay-status") response.shaderMessages = CollectShaderMessages();
		return response;
	}

	static Response Status(Request request, bool ok, string message)
	{
		List<SceneState> scenes = new();
		Scene active = SceneManager.GetActiveScene();
		for (int i = 0; i < SceneManager.sceneCount; i++)
		{
			Scene scene = SceneManager.GetSceneAt(i);
			scenes.Add(new SceneState { path = scene.path, name = scene.name,
				isDirty = scene.isDirty, isActive = scene == active });
		}
		HexNearTerrainShowcase showcase = Object.FindObjectOfType<HexNearTerrainShowcase>();
		HexTerrainStyle style = showcase ? showcase.terrainStyle : AssetDatabase.LoadAssetAtPath<HexTerrainStyle>(StylePath);
		Response response = new() { id = request?.id ?? "invalid", action = request?.action ?? "invalid",
			ok = ok, message = message, timestamp = DateTime.UtcNow.ToString("O"),
			playing = EditorApplication.isPlaying,
			transitioning = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying,
			compiling = EditorApplication.isCompiling, updating = EditorApplication.isUpdating,
			showcaseReady = showcase && showcase.Ready,
			nearProfileReady = style && style.nearTerrainProfile && style.nearTerrainProfile.IsReady,
			generatedCells = showcase ? showcase.GeneratedCellCount : 0,
			measuring = measurement != null, measurementFrames = measurement?.frames.Count ?? 0,
			testingTeardown = teardown != null,
			measurementTargetFrames = measurement != null ? MeasurementFrames : 0,
			measurementWarmupRemaining = measurement != null ? Math.Max(0, MeasurementWarmupFrames - measurement.warmup) : 0,
			scenes = scenes.ToArray(), recentErrors = RecentErrors.ToArray(), shaderMessages = Array.Empty<string>() };
		HexNearTerrainProfile profile = style ? style.nearTerrainProfile : null;
		if (profile) response.nearSettings = new NearSettings {
			assetPath = AssetDatabase.GetAssetPath(profile),
			shapeLayers = profile.shapeAtlas ? profile.shapeAtlas.depth : 0,
			shapeResolution = profile.shapeAtlas ? profile.shapeAtlas.width : 0,
			materialLayers = profile.albedoAtlas ? profile.albedoAtlas.depth : 0,
			materialResolution = profile.albedoAtlas ? profile.albedoAtlas.width : 0,
			mountainHeight = profile.mountainHeight, hillHeight = profile.hillHeight,
			desertMountainHeight = profile.desertMountainHeight, mountainFootprint = profile.mountainFootprint,
			hillFootprint = profile.hillFootprint, desertMountainFootprint = profile.desertMountainFootprint,
			ridgeWidth = profile.ridgeWidth, ridgeStrength = profile.ridgeStrength,
			mountainRangeStrength = profile.mountainRangeStrength, mountainRangeWidth = profile.mountainRangeWidth,
			plateauHeight = profile.plateauHeight, plateauRelief = profile.plateauRelief, plateauWeathering = profile.plateauWeathering,
			materialTiling = profile.materialTiling, materialDetail = profile.materialDetail };
		if (showcase && showcase.grid)
		{
			VegetationMetrics vegetation = new() {
				lastCameraId = HexNearVegetationMesh.LastCameraId,
				lastDrawCalls = HexNearVegetationMesh.LastDrawCallCount,
				lastDrawnInstances = HexNearVegetationMesh.LastDrawnInstanceCount,
				lastShadowedInstances = HexNearVegetationMesh.LastShadowedInstanceCount,
				lastShadowOnlyInstances = ReadStaticInt(typeof(HexNearVegetationMesh), "LastShadowOnlyInstanceCount"),
				lastShadowOnlyDrawCalls = ReadStaticInt(typeof(HexNearVegetationMesh), "LastShadowOnlyDrawCallCount"),
				lastCountersMatchPreviewCamera = showcase.previewCamera &&
					HexNearVegetationMesh.LastCameraId == showcase.previewCamera.GetInstanceID() };
			foreach (HexNearVegetationMesh vegetationChunk in showcase.grid.GetComponentsInChildren<HexNearVegetationMesh>())
			{
				vegetation.activeChunkRenderers++;
				vegetation.generatedInstances += vegetationChunk.InstanceCount;
				vegetation.cachedSpeciesBatches += vegetationChunk.BatchCount;
			}
			if (profile && profile.vegetation)
			{
				vegetation.lodDistance = profile.vegetation.lodDistance;
				vegetation.shadowDistance = profile.vegetation.shadowDistance;
				vegetation.drawDistance = profile.vegetation.drawDistance;
			}
			response.vegetationMetrics = vegetation;
		}
		if (showcase && showcase.previewCamera) response.cameraMetrics = ReadActualCameraMetrics(showcase.previewCamera, showcase.ViewFocus, showcase.ViewDistance);
		response.gameplay = ReadGameplayMetrics();
		return response;
	}

	static void ImportReference()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
			throw new InvalidOperationException("Reference import requires Edit Mode.");
		// A fixed method in this editor assembly, never a name provided by JSON.
		// This also lets the bridge compile while the optional importer is absent.
		Type importer = typeof(HexNearTerrainTools).Assembly.GetType("HexCiv6ReferenceImporter", false);
		var method = importer?.GetMethod("BuildAndAssignDefault", System.Reflection.BindingFlags.Public |
			System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
		if (method == null) throw new InvalidOperationException("The reference importer is not installed or has not compiled yet.");
		try { method.Invoke(null, null); }
		catch (System.Reflection.TargetInvocationException exception)
		{
			throw new InvalidOperationException(exception.InnerException?.Message ?? exception.Message, exception.InnerException);
		}
	}

	static void WriteResponse(Response response)
	{
		Directory.CreateDirectory(RequestDirectory);
		string json = JsonUtility.ToJson(response, true);
		File.WriteAllText(Path.Combine(RequestDirectory, "response.json"), json);
		if (Regex.IsMatch(response.id ?? "", "^[A-Za-z0-9_-]{1,80}$"))
			File.WriteAllText(Path.Combine(RequestDirectory, "response_" + response.id + ".json"), json);
	}

	static void RequireSavedScenes()
	{
		for (int i = 0; i < SceneManager.sceneCount; i++)
		{
			Scene scene = SceneManager.GetSceneAt(i);
			if (scene.isDirty || string.IsNullOrEmpty(scene.path))
				throw new InvalidOperationException("Unsaved scene detected: " + scene.name +
					". Save it manually first; no scenes were closed.");
		}
	}

	static void RememberOriginalScenes()
	{
		if (!string.IsNullOrEmpty(SessionState.GetString(OriginKey, ""))) return;
		if (SceneManager.sceneCount == 1 && SceneManager.GetActiveScene().path == ScenePath) return;
		SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
		SavedScene[] scenes = new SavedScene[setup.Length];
		for (int i = 0; i < setup.Length; i++) scenes[i] = new SavedScene {
			path = setup[i].path, isLoaded = setup[i].isLoaded, isActive = setup[i].isActive };
		SessionState.SetString(OriginKey, JsonUtility.ToJson(new SceneSetupRecord { scenes = scenes }));
	}

	static void OpenScene()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
			throw new InvalidOperationException("Opening the showcase requires Edit Mode.");
		RequireSavedScenes();
		if (!File.Exists(Path.Combine(Root, ScenePath)))
			throw new InvalidOperationException("Build the showcase scene first.");
		RememberOriginalScenes();
		if (SceneManager.sceneCount != 1 || SceneManager.GetActiveScene().path != ScenePath)
			EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
	}

	static void OnPlayModeChanged(PlayModeStateChange state)
	{
		if (state == PlayModeStateChange.ExitingPlayMode)
			FinishMeasurement(false, "Play Mode ended before measurement completed.");
		if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(OwnPlayKey, false)) return;
		SessionState.SetBool(OwnPlayKey, false);
		EditorApplication.delayCall += () => {
			// A new owned preview may have completed the old cleanup already.
			// Never let a delayed callback restore scenes during its successor.
			if (EditorApplication.isPlayingOrWillChangePlaymode) return;
			bool restored = false;
			try { RestoreOriginalScenes(); restored = true; }
			catch (Exception exception) { Debug.LogWarning("Showcase stopped; " + exception.Message); }
			finally { FinishGameplaySession(restored); }
		};
	}

	static void CompletePreviousSession()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode) return;
		GameplaySession previous = ReadGameplaySession();
		if (previous == null || !previous.active) return;
		RestoreOriginalScenes();
		FinishGameplaySession(true);
	}

	static void RestoreOriginalScenes()
	{
		string json = SessionState.GetString(OriginKey, "");
		if (string.IsNullOrEmpty(json)) return;
		RequireSavedScenes();
		SceneSetupRecord record = JsonUtility.FromJson<SceneSetupRecord>(json);
		if (record?.scenes == null || record.scenes.Length == 0)
			throw new InvalidOperationException("The previous scene setup is unavailable.");
		SceneSetup[] setup = new SceneSetup[record.scenes.Length];
		for (int i = 0; i < setup.Length; i++)
		{
			SavedScene saved = record.scenes[i];
			if (!File.Exists(Path.Combine(Root, saved.path)))
				throw new InvalidOperationException("Previous scene no longer exists: " + saved.path);
			setup[i] = new SceneSetup { path = saved.path, isLoaded = saved.isLoaded, isActive = saved.isActive };
		}
		EditorSceneManager.RestoreSceneManagerSetup(setup);
		SessionState.EraseString(OriginKey);
	}

	static string BuildScene()
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
			throw new InvalidOperationException("Building the showcase requires Edit Mode.");
		if (File.Exists(Path.Combine(Root, ScenePath)))
			return "Showcase path already exists and was left unchanged: " + ScenePath;
		Scene previous = SceneManager.GetActiveScene();
		Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
		try
		{
			SceneManager.SetActiveScene(scene);
			GameObject root = new(HexNearTerrainShowcase.SceneName);
			HexNearTerrainShowcase showcase = root.AddComponent<HexNearTerrainShowcase>();
			GameObject gridObject = new("Showcase Hex Grid (activated at runtime)");
			gridObject.transform.SetParent(root.transform, false);
			gridObject.SetActive(false);
			HexGrid grid = gridObject.AddComponent<HexGrid>();
			SerializedObject serializedGrid = new(grid);
			SetReference(serializedGrid, "chunkPrefab", PrefabComponent<HexGridChunk>("7622d007cafd044a1b1189c0b93d2912"));
			SetReference(serializedGrid, "unitPrefab", PrefabComponent<HexUnit>("5661a1efcb9464721b4db3f694ded559"));
			GameObject label = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath("f68794ca03b6f4510b9a5bd6ee9ba2f4"));
			SetReference(serializedGrid, "cellLabelPrefab", label ? label.GetComponent("Text") : null);
			SetReference(serializedGrid, "noiseSource", AssetDatabase.LoadAssetAtPath<Texture2D>(
				AssetDatabase.GUIDToAssetPath("f5c8b3933cd94006b91d2586ec1b083c")));
			serializedGrid.FindProperty("seed").intValue = 1936;
			serializedGrid.FindProperty("showGrid").boolValue = false;
			serializedGrid.FindProperty("useStreamingSurfaceColliders").boolValue = false;
			serializedGrid.ApplyModifiedPropertiesWithoutUndo();
			showcase.grid = grid;
			showcase.terrainStyle = AssetDatabase.LoadAssetAtPath<HexTerrainStyle>(StylePath);
			if (!showcase.terrainStyle) throw new InvalidOperationException("The default terrain style could not be loaded.");

			GameObject cameraObject = new("Near Terrain Camera");
			cameraObject.tag = "MainCamera";
			cameraObject.transform.SetParent(root.transform, false);
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.fieldOfView = 45f;
			camera.nearClipPlane = 0.3f;
			camera.farClipPlane = 2000f;
			camera.clearFlags = CameraClearFlags.SolidColor;
			camera.backgroundColor = new Color(0.17f, 0.25f, 0.28f);
			camera.allowHDR = true;
			camera.allowMSAA = false;
			cameraObject.transform.SetPositionAndRotation(new Vector3(310f, 310f, -120f), Quaternion.Euler(51f, -12f, 0f));
			UniversalAdditionalCameraData data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
			data.renderShadows = true;
			data.renderPostProcessing = false;
			showcase.previewCamera = camera;

			GameObject sunObject = new("Showcase Sun");
			sunObject.transform.SetParent(root.transform, false);
			sunObject.transform.rotation = Quaternion.Euler(43f, -35f, 0f);
			Light sun = sunObject.AddComponent<Light>();
			sun.type = LightType.Directional;
			sun.color = new Color(1f, 0.94f, 0.83f);
			sun.intensity = 1.15f;
			sun.shadows = LightShadows.Soft;
			sun.shadowStrength = 0.8f;
			sun.shadowBias = 0.03f;
			sun.shadowNormalBias = 0.25f;
			RenderSettings.sun = sun;
			RenderSettings.skybox = null;
			RenderSettings.fog = false;
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = new Color(0.49f, 0.57f, 0.64f);
			RenderSettings.ambientEquatorColor = new Color(0.3f, 0.33f, 0.3f);
			RenderSettings.ambientGroundColor = new Color(0.17f, 0.16f, 0.12f);
			if (!EditorSceneManager.SaveScene(scene, ScenePath, false))
				throw new InvalidOperationException("Unity could not save the new showcase scene.");
			return "Created a separate showcase scene; existing scenes and renderer assets were preserved: " + ScenePath;
		}
		finally
		{
			if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
			EditorSceneManager.CloseScene(scene, true);
		}
	}

	static T PrefabComponent<T>(string guid) where T : Component
	{
		GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
		return prefab ? prefab.GetComponent<T>() : null;
	}

	static void SetReference(SerializedObject target, string name, Object value)
	{
		if (!value) throw new InvalidOperationException("Required existing map reference was not found: " + name);
		target.FindProperty(name).objectReferenceValue = value;
	}

	static string Capture(string view)
	{
		HexNearTerrainShowcase showcase = Object.FindObjectOfType<HexNearTerrainShowcase>();
		if (!EditorApplication.isPlaying || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.path != ScenePath || !showcase.previewCamera)
			throw new InvalidOperationException("The showcase must be running and ready before capture.");
		if (!string.IsNullOrEmpty(view)) showcase.SetView(view);
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "near-terrain-" + (string.IsNullOrEmpty(view) ? "current" : view) +
			"-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png");
		return CaptureCamera(showcase.previewCamera, path);
	}

	static string CaptureCamera(Camera camera, string path, int width = 1600, int height = 1000)
	{
		RenderTexture previous = RenderTexture.active;
		lastCaptureCoveragePath = null;
		lastCaptureWidth = Mathf.Max(1, width); lastCaptureHeight = Mathf.Max(1, height);
		RenderTexture target = RenderTexture.GetTemporary(lastCaptureWidth, lastCaptureHeight, 24, RenderTextureFormat.ARGB32);
		Texture2D image = null;
		try
		{
			RenderPipeline.SubmitRenderRequest(camera,
				new UniversalRenderPipeline.SingleCameraRequest { destination = target });
			RenderTexture.active = target;
			image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
			image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
			image.Apply(false, false);
			File.WriteAllBytes(path, image.EncodeToPNG());
			return path;
		}
		finally
		{
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
			if (image) Object.DestroyImmediate(image);
		}
	}

	static GameplaySession ReadGameplaySession()
	{
		string json = SessionState.GetString(GameplayKey, "");
		return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<GameplaySession>(json);
	}
	static void SaveGameplaySession(GameplaySession session) => SessionState.SetString(GameplayKey, JsonUtility.ToJson(session));
	static string SettingsHash(string path)
	{
		if (!File.Exists(path)) return "absent";
		using SHA256 algorithm = SHA256.Create();
		using FileStream stream = File.OpenRead(path);
		return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
	}
	static Type GameplayType(string name) => Type.GetType(name + ", WarAndPeace", false);
	static object ReadStaticProperty(Type type, string name) => type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
	static int ReadStaticInt(Type type, string name) => ReadStaticProperty(type, name) is int value ? value : -1;
	static object ReadField(object instance, string name) => instance?.GetType().GetField(name,
		BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
	static T ReadField<T>(object instance, string name, T fallback = default) => ReadField(instance, name) is T result ? result : fallback;
	static T FindGameplayComponent<T>() where T : Component
	{
		foreach (T component in Object.FindObjectsOfType<T>())
			if (component.gameObject.scene.path == GameplayScenePath) return component;
		return null;
	}
	static object GameplayManager() => ReadStaticProperty(GameplayType("GameManager"), "Instance");
	static bool GameplayFrameworkReady()
	{
		Type entry = GameplayType("GameEntry");
		return ReadStaticProperty(entry, "Entity") is Object entity && entity &&
			ReadStaticProperty(entry, "Scene") is Object scene && scene &&
			ReadStaticProperty(entry, "Procedure") is Object procedure && procedure;
	}

	static void BeginGameplay(Request request)
	{
		if (EditorApplication.isPlayingOrWillChangePlaymode)
			throw new InvalidOperationException("Gameplay validation requires Edit Mode with no Play transition in progress.");
		CompletePreviousSession();
		RequireSavedScenes();
		foreach (string path in new[] { LaunchScenePath, MenuScenePath, GameplayScenePath })
			if (!File.Exists(Path.Combine(Root, path))) throw new InvalidOperationException("Required game scene is missing: " + path);
		string view = string.IsNullOrEmpty(request.view) ? "forest" : request.view;
		ValidateFixedView(view);
		if (GameplayType("ProcedureMenu")?.GetField("RequestEnterGame", BindingFlags.Public | BindingFlags.Static) == null)
			throw new InvalidOperationException("The fixed ProcedureMenu.RequestEnterGame entry point is unavailable.");
		Type settingsHelper = Type.GetType("UnityGameFramework.Runtime.DefaultSettingHelper, UnityGameFramework.Runtime", false);
		if (settingsHelper?.GetField("m_FilePath", BindingFlags.NonPublic | BindingFlags.Instance) == null)
			throw new InvalidOperationException("The verified DefaultSettingHelper save-path guard is unavailable.");
		RememberOriginalScenes();
		string settingsPath = Path.Combine(Application.persistentDataPath, "GameFrameworkSetting.dat");
		GameplaySession session = new() { id = request.id, view = view, stage = "launch", active = true,
			startedUtc = DateTime.UtcNow.ToString("O"), startedTime = EditorApplication.timeSinceStartup,
			originalSettingsPath = settingsPath, originalSettingsHash = SettingsHash(settingsPath),
			targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
			timeScale = Time.timeScale, runInBackground = Application.runInBackground, sleepTimeout = Screen.sleepTimeout,
			message = "Loading the normal launch scene before requesting the menu's Enter Game action.", errors = Array.Empty<string>() };
		EditorSceneManager.OpenScene(LaunchScenePath, OpenSceneMode.Single);
		SaveGameplaySession(session);
		RecentErrors.Clear();
		SessionState.SetBool(OwnPlayKey, true);
		EditorApplication.isPlaying = true;
	}

	static void RedirectGameplaySettings(GameplaySession session)
	{
		Type helperType = Type.GetType("UnityGameFramework.Runtime.DefaultSettingHelper, UnityGameFramework.Runtime", false);
		if (helperType == null) throw new InvalidOperationException("The fixed settings helper type is unavailable.");
		Object[] helpers = Object.FindObjectsOfType(helperType);
		if (helpers.Length != 1) throw new InvalidOperationException("Expected exactly one active DefaultSettingHelper; found " + helpers.Length + ".");
		FieldInfo filePath = helperType.GetField("m_FilePath", BindingFlags.NonPublic | BindingFlags.Instance);
		string current = filePath?.GetValue(helpers[0]) as string;
		string temporary = Path.Combine(RequestDirectory, "GameFrameworkSetting.dat");
		if (!string.Equals(current?.Replace('\\', '/'), session.originalSettingsPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(current?.Replace('\\', '/'), temporary.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Settings helper uses an unexpected path; no path was changed.");
		Directory.CreateDirectory(RequestDirectory);
		// Start has already read the original settings. Only subsequent saves use this temporary path.
		// Do not restore this field during Play: framework shutdown is the operation that saves it.
		filePath.SetValue(helpers[0], temporary);
		session.redirectedSettings = true;
		SaveGameplaySession(session);
	}

	static void PollGameplay()
	{
		// The benchmark has its own cheap readiness checks; avoid reflection and
		// whole-scene vegetation scans contaminating its per-frame timings.
		if (streamingPerformance != null) return;
		GameplaySession session = ReadGameplaySession();
		if (session == null || !session.active || !EditorApplication.isPlaying ||
			EditorApplication.isCompiling || EditorApplication.isUpdating || session.stage == "stopping" || session.stage == "failed" || session.teardownRequested) return;
		try
		{
			if (!session.enteredPlay)
			{
				session.enteredPlay = true;
				session.firstPlayFrame = Time.frameCount;
				SaveGameplaySession(session);
				return;
			}
			// All scene Start callbacks, including the settings Load, precede this guard.
			if (Time.frameCount < session.firstPlayFrame + 2) return;
			if (!session.redirectedSettings) RedirectGameplaySettings(session);
			if (session.stage == "launch")
			{
				Scene menu = SceneManager.GetSceneByPath(MenuScenePath);
				if (GameplayFrameworkReady() && menu.IsValid() && menu.isLoaded)
				{
					GameplayType("ProcedureMenu").GetField("RequestEnterGame", BindingFlags.Public | BindingFlags.Static).SetValue(null, true);
					session.stage = "loading-game";
					session.message = "Requested normal menu entry; waiting for Game_2 initialization.";
					SaveGameplaySession(session);
				}
			}
			else if (session.stage == "loading-game")
			{
				GameplayMetrics state = ReadGameplayMetrics();
				if (state.mapLoaded && state.frameworkReady && state.gameplayCoreInitialized && state.pendingArmyEntities == 0)
				{
					if (session.firstCoreReadyFrame < 0)
					{
						session.firstCoreReadyFrame = Time.frameCount;
						SaveGameplaySession(session);
						return;
					}
					// Let startup LateUpdate / camera streaming and deferred entity initialization settle.
					if (Time.frameCount < session.firstCoreReadyFrame + 2) return;
					SetGameplayView(session.view);
					session = ReadGameplaySession();
					session.stage = "streaming-view";
					session.message = "Actual Game_2 initialized; waiting for the requested camera tier, chunks and cloud transition.";
					SaveGameplaySession(session);
				}
				else if (session.firstCoreReadyFrame >= 0)
				{
					session.firstCoreReadyFrame = -1;
					SaveGameplaySession(session);
				}
			}
			if (session.stage == "streaming-view" || session.stage == "streaming-near" || session.stage == "running")
			{
				GameplayMetrics state = ReadGameplayMetrics();
				bool firstNearReady = state.nearReady && !session.reachedNearReady;
				if (state.nearReady) session.reachedNearReady = true;
				if (state.viewReady && (session.stage != "running" || firstNearReady))
				{
					session.reachedViewReady = true;
					session.stage = "running";
					session.message = "Actual Game_2 default world, gameplay core and requested " + state.requestedRenderTier + " view are ready.";
					SaveGameplaySession(session);
					WriteResponse(Status(new Request { id = session.id, action = "gameplay-play" }, true, session.message));
				}
				else if (firstNearReady) SaveGameplaySession(session);
			}
			if (!session.reachedViewReady && EditorApplication.timeSinceStartup - session.startedTime > 240d)
				throw new InvalidOperationException("Gameplay fixed-view initialization exceeded 240 seconds. Inspect gameplay-status and errors; the owned Play session remains available for stop.");
		}
		catch (Exception exception)
		{
			session.stage = "failed"; session.message = exception.Message;
			SaveGameplaySession(session);
			WriteResponse(Status(new Request { id = session.id, action = "gameplay-play" }, false, exception.Message));
		}
	}

	static GameplayMetrics ReadGameplayMetrics()
	{
		GameplaySession session = ReadGameplaySession();
		HexGrid grid = FindGameplayComponent<HexGrid>();
		HexMapCamera rig = FindGameplayComponent<HexMapCamera>();
		Camera camera = rig ? rig.GetComponentInChildren<Camera>() : null;
		object manager = GameplayManager();
		bool core = ReadStaticProperty(GameplayType("AreaInit"), "IsInitialized") is bool initialized && initialized &&
			ReadField<bool>(manager, "runtimeInitialized") && ReadField(manager, "uipManager") is Object panel && panel;
		GameplayMetrics state = new() { stage = session?.stage ?? "none", message = session?.message ?? "No owned gameplay session.",
			view = session?.view, ownedSession = session != null && session.active && SessionState.GetBool(OwnPlayKey, false),
			mapLoaded = grid && grid.CellData != null && grid.CellData.Length > 10000 && grid.HasPoliticalData && grid.CityCount > 0,
			frameworkReady = GameplayFrameworkReady(), gameplayCoreInitialized = core, simulationRunning = ReadField<bool>(manager, "isStart"),
			cellCount = grid && grid.CellData != null ? grid.CellData.Length : 0, cities = grid ? grid.CityCount : 0,
			countries = ReadField<int>(manager, "numOfBelongs"), pendingArmyEntities = ReadField<int>(manager, "initArmyNum"),
			activeChunks = grid ? grid.ActiveChunkCount : 0, chunkActivationProgress = grid ? grid.DesiredChunkActivationProgress : 0f,
			hasMissingChunks = grid && grid.HasMissingDesiredChunks, streamingCoverActive = grid && grid.IsStreamingCoverActive,
			overviewHeldUnderDetail = grid && grid.IsOverviewHeldUnderDetail, focusCell = session?.focusCell ?? -1,
			stickDistance = rig ? HexMapCamera.CurrentStickDistance : 0f, zoom = rig ? HexMapCamera.CurrentZoom : 0f,
			requestedStickDistance = session?.requestedStickDistance ?? 0f, requestedZoom = session?.requestedZoom ?? 0f,
			appliedStickDistance = session?.appliedStickDistance ?? 0f, appliedZoom = session?.appliedZoom ?? 0f,
			viewAppliedFrame = session?.viewAppliedFrame ?? 0,
			requestedRenderTier = RequestedRenderTier(session?.requestedStickDistance ?? 0f),
			reachedNearReady = session != null && session.reachedNearReady, viewTransitioning = HexMapCamera.IsViewTransitioning,
			teardownRequested = session != null && session.teardownRequested, teardownCompleted = session != null && session.teardownCompleted,
			teardownPassed = session != null && session.teardownPassed, teardownPath = session?.teardownPath,
			cameraChangedSinceView = rig && session != null && session.viewAppliedFrame > 0 &&
				(Mathf.Abs(HexMapCamera.CurrentStickDistance - session.appliedStickDistance) > .05f ||
				Vector3.Distance(rig.transform.position, session.appliedFocus) > .05f),
			renderTier = !rig ? "unavailable" : HexMapCamera.CurrentViewMode == HexMapCamera.ViewMode.Globe ? "globe" :
				grid && grid.IsOverviewMode ? "overview" : HexMapCamera.CurrentStickDistance > HexMapCamera.NearViewMaxDistance ? "mid-near" : "near",
			settingsSaveRedirected = session != null && session.redirectedSettings,
			settingsSavePath = session != null && session.redirectedSettings ? Path.Combine(RequestDirectory, "GameFrameworkSetting.dat") : null,
			originalSettingsHash = session?.originalSettingsHash, finalSettingsHash = session?.finalSettingsHash,
			settingsUnchangedAfterStop = session != null && !session.active && session.settingsUnchanged,
			originalScenesRestored = session != null && session.originalScenesRestored, errors = session?.errors ?? Array.Empty<string>() };
		if (camera)
		{
			state.camera = ReadActualCameraMetrics(camera, rig.transform.position, HexMapCamera.CurrentStickDistance);
			state.nearLightingActive = ReadField<bool>(camera.GetComponent<HexNearTerrainLighting>(), "active");
			bool matchesCamera = HexNearVegetationMesh.LastCameraId == camera.GetInstanceID();
			state.vegetationDrawCalls = matchesCamera ? HexNearVegetationMesh.LastDrawCallCount : -1;
			state.vegetationDrawnInstances = matchesCamera ? HexNearVegetationMesh.LastDrawnInstanceCount : -1;
			state.vegetationShadowedInstances = matchesCamera ? HexNearVegetationMesh.LastShadowedInstanceCount : -1;
			state.vegetationShadowOnlyInstances = matchesCamera ? ReadStaticInt(typeof(HexNearVegetationMesh), "LastShadowOnlyInstanceCount") : -1;
			state.vegetationShadowOnlyDrawCalls = matchesCamera ? ReadStaticInt(typeof(HexNearVegetationMesh), "LastShadowOnlyDrawCallCount") : -1;
		}
		if (grid)
			foreach (HexNearVegetationMesh vegetation in grid.GetComponentsInChildren<HexNearVegetationMesh>())
				state.generatedVegetationInstances += vegetation.InstanceCount;
		state.nearReady = EditorApplication.isPlaying && state.mapLoaded && state.frameworkReady && state.gameplayCoreInitialized &&
			state.pendingArmyEntities == 0 && state.renderTier == "near" && state.nearLightingActive &&
			grid.SurfaceStyle && grid.SurfaceStyle.UsesNearTerrain && state.activeChunks > 0 &&
			!grid.IsOverviewMode && !state.hasMissingChunks && !state.streamingCoverActive && !HexMapCamera.IsViewTransitioning;
		bool commonReady = EditorApplication.isPlaying && rig && state.mapLoaded && state.frameworkReady && state.gameplayCoreInitialized &&
			state.pendingArmyEntities == 0 && !state.hasMissingChunks && !state.streamingCoverActive && !state.viewTransitioning &&
			session != null && session.viewAppliedFrame > 0 && !state.cameraChangedSinceView &&
			Mathf.Abs(state.stickDistance - state.requestedStickDistance) < .1f && !session.teardownRequested;
		state.viewReady = commonReady && state.renderTier == state.requestedRenderTier &&
			(state.requestedRenderTier == "near" ? state.nearReady :
			state.requestedRenderTier == "overview" ? !state.nearLightingActive :
			state.activeChunks > 0 && grid.SurfaceStyle && grid.SurfaceStyle.UsesNearTerrain && state.nearLightingActive);
		return state;
	}
	static string RequestedRenderTier(float distance) =>
		distance > HexMapCamera.MidViewMinDistance ? "overview" :
		distance > HexMapCamera.NearViewMaxDistance ? "mid-near" : "near";

	static void ValidateFixedView(string view)
	{
		if (view != "all" && view != "forest" && view != "mountains" && view != "coast" && view != "mid" && view != "overview" && view != "snow" &&
			view != "alps" && view != "himalaya" && view != "sahara" && view != "amazon" && view != "rockies" && view != "tibet" && view != "tibet-wide" && view != "geography-overview" &&
			view != "river-tibet" && view != "river-yangtze" && view != "river-mouth" && view != "river-wenzhou" && view != "river-china-wide" && view != "river-rhine" && view != "river-nile" &&
			view != "river-amazon-mouth" && view != "river-congo-mouth" && view != "river-rhine-mouth" && view != "river-mississippi-mouth" && view != "river-mouth-close" && view != "river-mississippi-close" &&
			view != "sichuan-west" && view != "sichuan-basin" && view != "yunnan" && view != "guizhou" && view != "southwest-wide" && view != "camera-close" && view != "near-edge" && view != "detail-edge")
			throw new InvalidOperationException("Unknown fixed gameplay view; use a named view supported by SendPreview.ps1.");
	}
	static void SetGameplayView(string view)
	{
		ValidateFixedView(view);
		GameplaySession session = ReadGameplaySession();
		if (session != null && session.active && session.teardownRequested)
			throw new InvalidOperationException("Camera teardown has been requested; stop the owned session before starting another view.");
		HexGrid grid = FindGameplayComponent<HexGrid>();
		HexMapCamera rig = FindGameplayComponent<HexMapCamera>();
		if (!EditorApplication.isPlaying || !grid || !rig || grid.CellData == null || grid.CellData.Length < 10000 ||
			HexMapCamera.CurrentViewMode != HexMapCamera.ViewMode.Flat || HexMapCamera.IsViewTransitioning)
			throw new InvalidOperationException("A loaded, non-transitioning flat Game_2 camera is required.");
		float longitude, latitude, distance;
		switch (view)
		{
			case "forest": longitude = 8.2f; latitude = 48.2f; distance = 180f; break;
			case "near-edge": longitude = 8.2f; latitude = 48.2f; distance = HexMapCamera.NearViewMaxDistance - 1f; break;
			case "detail-edge": longitude = 8.2f; latitude = 48.2f; distance = HexMapCamera.MidViewMinDistance - 1f; break;
			case "mountains": longitude = 10.5f; latitude = 46.7f; distance = 240f; break;
			case "coast": longitude = -1f; latitude = 49.5f; distance = 240f; break;
			case "mid": longitude = 8.2f; latitude = 48.2f; distance = 900f; break;
			case "overview": longitude = 8.2f; latitude = 48.2f; distance = 1800f; break;
			case "alps": longitude = 10f; latitude = 46.5f; distance = 180f; break;
			case "himalaya": longitude = 86.9f; latitude = 28f; distance = 180f; break;
			case "sahara": longitude = 15f; latitude = 24f; distance = 180f; break;
			case "snow": longitude = -42f; latitude = 66f; distance = 240f; break;
			case "amazon": longitude = -62f; latitude = -4f; distance = 180f; break;
			case "rockies": longitude = -110f; latitude = 43f; distance = 180f; break;
			case "tibet": longitude = 86f; latitude = 33f; distance = 180f; break;
			case "tibet-wide": longitude = 86f; latitude = 32f; distance = 560f; break;
			case "sichuan-west": longitude = 101.8f; latitude = 30.3f; distance = 300f; break;
			case "sichuan-basin": longitude = 104.4f; latitude = 30.5f; distance = 400f; break;
			case "yunnan": longitude = 102f; latitude = 25.1f; distance = 300f; break;
			case "guizhou": longitude = 106.6f; latitude = 26.6f; distance = 300f; break;
			case "southwest-wide": longitude = 102.5f; latitude = 29f; distance = 900f; break;
			case "river-tibet": longitude = 90f; latitude = 29.4f; distance = 260f; break;
			case "river-yangtze": longitude = 111.4f; latitude = 30.5f; distance = 300f; break;
			case "river-mouth": longitude = 120.7f; latitude = 31.6f; distance = 300f; break;
			case "river-mouth-close": longitude = 121.74f; latitude = 31.38f; distance = 120f; break;
			case "river-mississippi-close": longitude = -89.46f; latitude = 29.11f; distance = 120f; break;
			case "river-wenzhou": longitude = 120.6f; latitude = 28.1f; distance = 240f; break;
			case "river-china-wide": longitude = 117.3f; latitude = 31.8f; distance = 750f; break;
			case "river-rhine": longitude = 7.6f; latitude = 50f; distance = 260f; break;
			case "river-nile": longitude = 31.1f; latitude = 29.7f; distance = 300f; break;
			case "river-amazon-mouth": longitude = -51f; latitude = -.7f; distance = 400f; break;
			case "river-congo-mouth": longitude = 12.6f; latitude = -6f; distance = 300f; break;
			case "river-rhine-mouth": longitude = 4.4f; latitude = 51.7f; distance = 300f; break;
			case "river-mississippi-mouth": longitude = -90f; latitude = 29.7f; distance = 300f; break;
			case "camera-close": longitude = 10.5f; latitude = 46.7f; distance = 70f; break;
			case "geography-overview": longitude = 10f; latitude = 46.5f; distance = 900f; break;
			default: longitude = 10.5f; latitude = 51f; distance = 390f; break;
		}
		int cell = GeographicCell(grid, longitude, latitude);
		Vector3 position = rig.transform.localPosition;
		position.x = grid.CellPositions[cell].x; position.z = grid.CellPositions[cell].z;
		rig.transform.localPosition = position;
		MethodInfo setZoom = typeof(HexMapCamera).GetMethod("SetZoom", BindingFlags.NonPublic | BindingFlags.Instance);
		float far = ReadField<float>(rig, "stickMinZoom", -2400f), near = ReadField<float>(rig, "stickMaxZoom", -120f);
		if (setZoom == null) throw new InvalidOperationException("The fixed HexMapCamera.SetZoom entry point is unavailable.");
		bool extraClose = distance < Mathf.Abs(near) - .01f;
		float requestedZoom = extraClose ? 1f : Mathf.InverseLerp(far, near, -distance);
		setZoom.Invoke(rig, new object[] { requestedZoom });
		HexMapCamera.SetExtraCloseZoom(extraClose);
		HexMapCamera.ValidatePosition();
		if (session != null && session.active)
		{
			session.view = view; session.focusCell = cell; session.stage = "streaming-view";
			session.requestedStickDistance = distance; session.requestedZoom = requestedZoom;
			session.appliedStickDistance = HexMapCamera.CurrentStickDistance; session.appliedZoom = HexMapCamera.CurrentZoom;
			session.appliedFocus = rig.transform.position; session.viewAppliedFrame = Time.frameCount;
			SaveGameplaySession(session);
		}
	}
	static int GeographicCell(HexGrid grid, float longitude, float latitude)
	{
		// Same cropped equirectangular and odd-row convention as WorldMapGlobeTransitionController.
		int row = Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(.15f, .8888889f, latitude / 180f + .5f) * grid.CellCountZ - .5f), 0, grid.CellCountZ - 1);
		int column = Mathf.RoundToInt((longitude / 360f + .5f) * grid.CellCountX - ((row & 1) == 0 ? 0f : .5f) - .5f);
		column = (column % grid.CellCountX + grid.CellCountX) % grid.CellCountX;
		return column + row * grid.CellCountX;
	}
	static void BeginWorldReview(Request request)
	{
		GameplayMetrics state = ReadGameplayMetrics();
		if (!EditorApplication.isPlaying || !state.ownedSession || !state.viewReady || EditorApplication.isPaused)
			throw new InvalidOperationException("World review requires a ready, unpaused gameplay session owned by this interface.");
		worldReview = new WorldReview { request = request, originalView = state.view,
			views = request.view == "all" ?
				new[] { "tibet-wide", "himalaya", "sichuan-west", "sichuan-basin", "yunnan", "guizhou", "southwest-wide", "forest", "coast", "sahara", "snow", "mid", "overview" } :
				new[] { "tibet-wide", "himalaya", "sichuan-west", "sichuan-basin", "yunnan", "guizhou", "southwest-wide", "forest", "coast" } };
		ApplyNextReviewView();
	}
	static void ApplyNextReviewView()
	{
		SetGameplayView(worldReview.views[worldReview.index]);
		worldReview.appliedFrame = Time.frameCount;
		worldReview.earliestCapture = EditorApplication.timeSinceStartup + 3;
		worldReview.deadline = EditorApplication.timeSinceStartup + 600;
	}
	static void AdvanceWorldReview()
	{
		if (worldReview == null) return;
		try
		{
			if (!EditorApplication.isPlaying) { FinishWorldReview("Play mode ended."); return; }
			if (EditorApplication.timeSinceStartup > worldReview.deadline) { FinishWorldReview("A review view did not become ready within ten minutes."); return; }
			if (EditorApplication.isCompiling || EditorApplication.isUpdating || ShaderUtil.anythingCompiling || EditorApplication.isPaused ||
				Time.frameCount < worldReview.appliedFrame + 12 || EditorApplication.timeSinceStartup < worldReview.earliestCapture) return;
			GameplayMetrics state = ReadGameplayMetrics();
			if (!state.viewReady) return;
			worldReview.images.Add(new ReviewImage { view = worldReview.views[worldReview.index], path = CaptureGameplay(), metrics = state });
			worldReview.index++;
			if (worldReview.index == worldReview.views.Length) FinishWorldReview(null);
			else ApplyNextReviewView();
		}
		catch (Exception exception) { FinishWorldReview(exception.Message); }
	}
	static void FinishWorldReview(string error)
	{
		WorldReview completed = worldReview;
		if (completed == null) return;
		worldReview = null;
		ReviewReport report = new() { id = completed.request.id, timestamp = DateTime.UtcNow.ToString("O"),
			completed = true, passed = string.IsNullOrEmpty(error), error = error ?? "", images = completed.images.ToArray() };
		Directory.CreateDirectory(ArtifactDirectory);
		File.WriteAllText(Path.Combine(ArtifactDirectory, "WorldReview-" + report.id + ".json"), JsonUtility.ToJson(report, true));
		if (EditorApplication.isPlaying && !string.IsNullOrEmpty(completed.originalView)) SetGameplayView(completed.originalView);
	}
	static string CaptureGameplay()
	{
		GameplayMetrics state = ReadGameplayMetrics();
		if (!state.viewReady) throw new InvalidOperationException("Wait for gameplay.viewReady before capturing the actual Game_2 map camera at its requested tier.");
		HexMapCamera rig = FindGameplayComponent<HexMapCamera>();
		Camera camera = rig.GetComponentInChildren<Camera>();
		Directory.CreateDirectory(ArtifactDirectory);
		string path = CaptureCamera(camera, Path.Combine(ArtifactDirectory, "gameplay-near-" + (state.view ?? "current") +
			"-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png"), camera.pixelWidth, camera.pixelHeight);
		lastCaptureCoveragePath = WriteStreamingCoverage(camera, FindGameplayComponent<HexGrid>(), path + ".coverage.json");
		return path;
	}
	static void FinishGameplaySession(bool restored)
	{
		GameplaySession session = ReadGameplaySession();
		if (session == null || !session.active) return;
		Application.targetFrameRate = session.targetFrameRate;
		QualitySettings.vSyncCount = session.vSyncCount;
		Time.timeScale = session.timeScale;
		Application.runInBackground = session.runInBackground;
		Screen.sleepTimeout = session.sleepTimeout;
		session.active = false; session.stage = "stopped"; session.stoppedUtc = DateTime.UtcNow.ToString("O");
		session.originalScenesRestored = restored;
		session.finalSettingsHash = SettingsHash(session.originalSettingsPath);
		session.settingsUnchanged = session.finalSettingsHash == session.originalSettingsHash;
		session.message = session.settingsUnchanged ? "Owned gameplay Play stopped; original settings SHA-256 is unchanged and runtime timing settings were restored." :
			"Original settings SHA-256 changed during gameplay validation. No original file was overwritten or restored by this tool; inspect the difference.";
		SaveGameplaySession(session);
		Directory.CreateDirectory(ArtifactDirectory);
		File.WriteAllText(Path.Combine(ArtifactDirectory, "GameplaySession-" + session.id + ".json"), JsonUtility.ToJson(session, true));
		WriteResponse(Status(new Request { id = session.id, action = "gameplay-play" },
			session.settingsUnchanged && restored && session.reachedNearReady && (!session.teardownRequested || session.teardownPassed), session.message));
	}

	[Serializable] sealed class GeographyHistogram
	{
		public int landCells, waterCells;
		public int[] terrain = new int[5], landform = new int[4], vegetation = new int[7], tint = new int[6], rotation = new int[6];
		public int zeroDensityCells, positiveDensityCells, totalDensity;
	}
	[Serializable] sealed class GeographyAnchor
	{
		public string name;
		public float longitude, latitude;
		public int centerCell, centerRow, centerColumn, rowColumnRadius = 4;
		public bool centerIsWater;
		public int[] centerActual, centerExpected;
		public GeographyHistogram actual = new(), expected = new();
	}
	[Serializable] sealed class GeographyMismatch
	{
		public int cell, row, column;
		public int[] actual, expected;
		public int actualPlantLevel, expectedPlantLevel;
	}
	[Serializable] sealed class GeographyReport
	{
		public string id, timestamp, resourcePath = "Resources/Maps/EarthTerrain.bytes", resourceSha256, format;
		public string message;
		public bool completed, passed, geometryMatches;
		public int width, height, gridWidth, gridHeight, resourceBytes, totalCells, checkedLandCells, skippedWaterCells, cities, countries, distinctLandCountryIds;
		public float vmin, vmax;
		public int invalidRecords, mismatchedCells, plantLevelMismatches;
		public string[] fields = { "terrainIndex", "landform", "vegetation", "vegetationDensity", "vegetationTint", "terrainRotation" };
		public string[] terrainLabels = { "Desert", "Grassland", "Plains", "Tundra", "Snow" };
		public string[] landformLabels = { "Flat", "Hill", "Mountain", "Plateau" };
		public string[] vegetationLabels = { "Mixed", "Broadleaf", "Sapling", "Conifer", "Deadwood", "ColdMixed", "Jungle" };
		public int[] mismatchFields = new int[6];
		public GeographyHistogram actual = new(), expected = new();
		public GeographyMismatch[] firstMismatches;
		public GeographyAnchor[] anchors;
		public string scope = "Read-only comparison of every actual nonwater Game_2 cell against all six raw atlas fields, plus the derived PlantLevel. Water cells are counted and skipped. Regional histograms are observations, not geographic-truth or visual-quality assertions.";
	}
	static int[] GeographyFields(HexCellData cell) => new[] { cell.TerrainTypeIndex, (int)cell.landform, (int)cell.vegetation,
		(int)cell.vegetationDensity, (int)cell.vegetationTint, (int)cell.terrainRotation };
	static int[] AtlasFields(byte[] bytes, int cell)
	{
		int offset = 24 + cell * 6;
		return new[] { (int)bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3], bytes[offset + 4], bytes[offset + 5] };
	}
	static void AddGeographyHistogram(GeographyHistogram histogram, bool water, int[] fields)
	{
		if (water) { histogram.waterCells++; return; }
		histogram.landCells++;
		if ((uint)fields[0] < histogram.terrain.Length) histogram.terrain[fields[0]]++;
		if ((uint)fields[1] < histogram.landform.Length) histogram.landform[fields[1]]++;
		if ((uint)fields[2] < histogram.vegetation.Length) histogram.vegetation[fields[2]]++;
		if ((uint)fields[4] < histogram.tint.Length) histogram.tint[fields[4]]++;
		if ((uint)fields[5] < histogram.rotation.Length) histogram.rotation[fields[5]]++;
		if (fields[3] > 0) histogram.positiveDensityCells++; else histogram.zeroDensityCells++;
		histogram.totalDensity += fields[3];
	}
	static GeographyAnchor ReadGeographyAnchor(HexGrid grid, byte[] bytes, string name, float longitude, float latitude)
	{
		int center = GeographicCell(grid, longitude, latitude);
		GeographyAnchor anchor = new() { name = name, longitude = longitude, latitude = latitude, centerCell = center,
			centerRow = center / grid.CellCountX, centerColumn = center % grid.CellCountX, centerIsWater = grid.CellData[center].IsUnderwater,
			centerActual = GeographyFields(grid.CellData[center]), centerExpected = AtlasFields(bytes, center) };
		for (int row = Math.Max(0, anchor.centerRow - anchor.rowColumnRadius); row <= Math.Min(grid.CellCountZ - 1, anchor.centerRow + anchor.rowColumnRadius); row++)
			for (int column = anchor.centerColumn - anchor.rowColumnRadius; column <= anchor.centerColumn + anchor.rowColumnRadius; column++)
			{
				int wrappedColumn = (column % grid.CellCountX + grid.CellCountX) % grid.CellCountX;
				int cell = row * grid.CellCountX + wrappedColumn;
				bool water = grid.CellData[cell].IsUnderwater;
				AddGeographyHistogram(anchor.actual, water, GeographyFields(grid.CellData[cell]));
				AddGeographyHistogram(anchor.expected, water, AtlasFields(bytes, cell));
			}
		return anchor;
	}
	static string ValidateGeography(string id, out bool passed, out string message)
	{
		HexGrid grid = FindGameplayComponent<HexGrid>();
		GameplayMetrics state = ReadGameplayMetrics();
		if (!EditorApplication.isPlaying || !grid || !state.mapLoaded || !state.frameworkReady || !state.gameplayCoreInitialized || state.pendingArmyEntities != 0)
			throw new InvalidOperationException("Geography validation requires the actual initialized Game_2 world and completed gameplay/army initialization.");
		TextAsset resource = Resources.Load<TextAsset>("Maps/EarthTerrain");
		if (!resource) throw new InvalidOperationException("The fixed Resources/Maps/EarthTerrain.bytes atlas is unavailable.");
		byte[] bytes = resource.bytes;
		if (bytes.Length < 24) throw new InvalidOperationException("EarthTerrain is shorter than its fixed 24-byte header.");
		GeographyReport report = new() { id = id, timestamp = DateTime.UtcNow.ToString("O"), resourceBytes = bytes.Length,
			gridWidth = grid.CellCountX, gridHeight = grid.CellCountZ, totalCells = grid.CellData.Length, cities = grid.CityCount, countries = state.countries };
		using (SHA256 hash = SHA256.Create()) report.resourceSha256 = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
		using (BinaryReader reader = new(new MemoryStream(bytes, false)))
		{
			report.format = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
			report.width = reader.ReadInt32(); report.height = reader.ReadInt32(); report.vmin = reader.ReadSingle(); report.vmax = reader.ReadSingle();
		}
		report.geometryMatches = report.format == "HXEARTH1" && report.width == grid.CellCountX && report.height == grid.CellCountZ &&
			(long)bytes.Length == 24L + (long)grid.CellData.Length * 6 && Mathf.Abs(report.vmin - .15f) < .000001f && Mathf.Abs(report.vmax - .8888889f) < .000001f;
		List<GeographyMismatch> mismatches = new();
		HashSet<ushort> landCountries = new();
		if (report.geometryMatches)
		{
			for (int index = 0; index < grid.CellData.Length; index++)
			{
				HexCellData cell = grid.CellData[index];
				int[] expected = AtlasFields(bytes, index);
				if (expected[0] > 4 || expected[1] > 3 || expected[2] > 6 || expected[3] > 100 || expected[4] > 5 || expected[5] > 5) report.invalidRecords++;
				int[] actual = GeographyFields(cell);
				AddGeographyHistogram(report.actual, cell.IsUnderwater, actual);
				AddGeographyHistogram(report.expected, cell.IsUnderwater, expected);
				if (cell.IsUnderwater) { report.skippedWaterCells++; continue; }
				report.checkedLandCells++;
				if (cell.CountryId != 0) landCountries.Add(cell.CountryId);
				bool mismatch = false;
				for (int field = 0; field < 6; field++)
					if (actual[field] != expected[field]) { report.mismatchFields[field]++; mismatch = true; }
				int plantLevel = expected[3] <= 0 ? 0 : expected[3] <= 33 ? 1 : expected[3] <= 66 ? 2 : 3;
				if (cell.PlantLevel != plantLevel) { report.plantLevelMismatches++; mismatch = true; }
				if (mismatch)
				{
					report.mismatchedCells++;
					if (mismatches.Count < 24) mismatches.Add(new GeographyMismatch { cell = index, row = index / grid.CellCountX,
						column = index % grid.CellCountX, actual = actual, expected = expected, actualPlantLevel = cell.PlantLevel, expectedPlantLevel = plantLevel });
				}
			}
			report.anchors = new[] { ReadGeographyAnchor(grid, bytes, "alps", 10f, 46.5f), ReadGeographyAnchor(grid, bytes, "himalaya", 86.9f, 28f),
				ReadGeographyAnchor(grid, bytes, "sahara", 15f, 24f), ReadGeographyAnchor(grid, bytes, "amazon", -62f, -4f),
				ReadGeographyAnchor(grid, bytes, "rockies", -110f, 43f), ReadGeographyAnchor(grid, bytes, "tibet", 86f, 33f) };
		}
		report.distinctLandCountryIds = landCountries.Count;
		report.firstMismatches = mismatches.ToArray();
		report.completed = report.geometryMatches;
		passed = report.passed = report.completed && report.checkedLandCells > 0 && report.invalidRecords == 0 && report.mismatchedCells == 0;
		message = report.message = !report.geometryMatches ? "EarthTerrain header, dimensions, latitude crop or byte length does not match the actual world." :
			passed ? "All " + report.checkedLandCells + " nonwater Game_2 cells exactly match the six-field EarthTerrain atlas and derived PlantLevel; " + report.skippedWaterCells + " water cells were counted and skipped." :
			"EarthTerrain comparison failed: " + report.mismatchedCells + " nonwater cells differ; " + report.invalidRecords + " atlas records contain invalid field values.";
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "GeographyValidation-" + id + ".json");
		File.WriteAllText(path, JsonUtility.ToJson(report, true));
		return path;
	}

	static CameraMetrics ReadActualCameraMetrics(Camera camera, Vector3 focus, float distance)
	{
		if (!camera) return null;
		UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
		return new CameraMetrics { position = camera.transform.position, eulerAngles = camera.transform.eulerAngles,
			focus = focus, viewDistance = distance, fieldOfView = camera.fieldOfView, instanceId = camera.GetInstanceID(),
			pixelWidth = camera.pixelWidth, pixelHeight = camera.pixelHeight, cullingMask = camera.cullingMask,
			enabled = camera.isActiveAndEnabled, allowMSAA = camera.allowMSAA, allowHDR = camera.allowHDR,
			useOcclusionCulling = camera.useOcclusionCulling, renderShadows = !data || data.renderShadows,
			graphicsPipeline = ObjectIdentity(GraphicsSettings.defaultRenderPipeline), qualityPipeline = ObjectIdentity(QualitySettings.renderPipeline) };
	}
	static string ObjectIdentity(Object value) => value ? value.name + " (#" + value.GetInstanceID() + ")" : "null";
	static object RequiredField(object instance, string name)
	{
		FieldInfo field = instance?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (field == null) throw new InvalidOperationException("The fixed teardown diagnostic field is unavailable: " + name);
		return field.GetValue(instance);
	}
	[Serializable] sealed class LightingMetrics
	{
		public string graphicsPipeline, qualityPipeline, sunIdentity, shadows, ambientMode;
		public Color lightColor, ambientSky, ambientEquator, ambientGround;
		public Quaternion rotation;
		public float intensity, shadowStrength, bias, normalBias;
	}
	static LightingMetrics ReadLightingMetrics(Light sun, HexNearTerrainLighting expected = null)
	{
		LightingMetrics state = new() { sunIdentity = ObjectIdentity(sun) };
		if (expected)
		{
			state.graphicsPipeline = ObjectIdentity((Object)RequiredField(expected, "oldGraphics"));
			state.qualityPipeline = ObjectIdentity((Object)RequiredField(expected, "oldQuality"));
			state.ambientMode = ((AmbientMode)RequiredField(expected, "oldAmbientMode")).ToString();
			state.ambientSky = (Color)RequiredField(expected, "oldAmbientSky");
			state.ambientEquator = (Color)RequiredField(expected, "oldAmbientEquator");
			state.ambientGround = (Color)RequiredField(expected, "oldAmbientGround");
			if (sun)
			{
				state.shadows = ((LightShadows)RequiredField(expected, "oldShadows")).ToString();
				state.lightColor = (Color)RequiredField(expected, "oldLightColor"); state.rotation = (Quaternion)RequiredField(expected, "oldRotation");
				state.intensity = (float)RequiredField(expected, "oldIntensity"); state.shadowStrength = (float)RequiredField(expected, "oldShadowStrength");
				state.bias = (float)RequiredField(expected, "oldBias"); state.normalBias = (float)RequiredField(expected, "oldNormalBias");
			}
		}
		else
		{
			state.graphicsPipeline = ObjectIdentity(GraphicsSettings.defaultRenderPipeline); state.qualityPipeline = ObjectIdentity(QualitySettings.renderPipeline);
			state.ambientMode = RenderSettings.ambientMode.ToString(); state.ambientSky = RenderSettings.ambientSkyColor;
			state.ambientEquator = RenderSettings.ambientEquatorColor; state.ambientGround = RenderSettings.ambientGroundColor;
			if (sun)
			{
				state.shadows = sun.shadows.ToString(); state.lightColor = sun.color; state.rotation = sun.transform.rotation;
				state.intensity = sun.intensity; state.shadowStrength = sun.shadowStrength; state.bias = sun.shadowBias; state.normalBias = sun.shadowNormalBias;
			}
		}
		return state;
	}
	[Serializable] sealed class TeardownReport
	{
		public string id, startedUtc, finishedUtc, message;
		public bool completed, passed, controllerDestroyed, cameraStillRendering, nearLightingInactive, pipelinesRestored, lightingRestored, cameraSettingsRestored;
		public int requestedFrame, renderedFrames, frameTarget = 12, queuedChunkRefreshBefore, maxQueuedChunkRefreshObserved;
		public int activeChunksBefore, activeChunksAfter, vegetationInstancesBefore, vegetationInstancesAfter;
		public int matchingVegetationFrames, maxVegetationDrawCallsObserved, maxShadowOnlyDrawCallsObserved;
		public bool expectedAllowMSAA, expectedAllowHDR, expectedOcclusionCulling, expectedRenderShadows;
		public CameraMetrics cameraBefore, cameraAfter;
		public LightingMetrics lightingBefore, lightingExpected, lightingAfter;
		public string refreshObservationLimit = "Queued chunk refresh is sampled after actual camera rendering and during editor polling. This observes pending work; it is not a count of every RefreshAllChunks invocation.";
	}
	sealed class TeardownMeasurement
	{
		public Request request;
		public HexMapCamera rig;
		public Camera camera;
		public HexGrid grid;
		public HexNearTerrainLighting lighting;
		public Light sun;
		public TeardownReport report;
		public double started;
		public int lastFrame = -1;
	}
	static int QueuedChunkRefresh(HexGrid grid)
	{
		int count = 0;
		if (grid) foreach (HexGridChunk chunk in grid.GetComponentsInChildren<HexGridChunk>()) if (chunk.enabled) count++;
		return count;
	}
	static int VegetationInstanceCount(HexGrid grid)
	{
		int count = 0;
		if (grid) foreach (HexNearVegetationMesh chunk in grid.GetComponentsInChildren<HexNearVegetationMesh>()) count += chunk.InstanceCount;
		return count;
	}
	static void BeginTeardown(Request request)
	{
		GameplayMetrics state = ReadGameplayMetrics();
		GameplaySession session = ReadGameplaySession();
		if (!EditorApplication.isPlaying || EditorApplication.isPaused || !state.ownedSession || !state.viewReady || !state.nearReady || session.teardownRequested)
			throw new InvalidOperationException("Teardown requires this tool's owned, unpaused Game_2 session settled at a ready near view; run it once, after all view captures.");
		HexMapCamera rig = FindGameplayComponent<HexMapCamera>();
		HexGrid grid = FindGameplayComponent<HexGrid>();
		Camera camera = rig.GetComponentInChildren<Camera>();
		HexNearTerrainLighting lighting = camera.GetComponent<HexNearTerrainLighting>();
		if (!camera.isActiveAndEnabled || !lighting || !(bool)RequiredField(lighting, "active"))
			throw new InvalidOperationException("The actual map camera must be rendering with active saved near lighting.");
		int queued = QueuedChunkRefresh(grid);
		if (queued > 0) throw new InvalidOperationException("Wait until active chunks have no queued refresh work before teardown.");
		Light sun = (Light)RequiredField(lighting, "sun");
		TeardownReport report = new() { id = request.id, startedUtc = DateTime.UtcNow.ToString("O"), requestedFrame = Time.frameCount,
			queuedChunkRefreshBefore = queued, activeChunksBefore = grid.ActiveChunkCount, vegetationInstancesBefore = VegetationInstanceCount(grid),
			cameraBefore = ReadActualCameraMetrics(camera, rig.transform.position, HexMapCamera.CurrentStickDistance),
			lightingBefore = ReadLightingMetrics(sun), lightingExpected = ReadLightingMetrics(sun, lighting),
			expectedAllowMSAA = (bool)RequiredField(rig, "defaultAllowMSAA"), expectedAllowHDR = (bool)RequiredField(rig, "defaultAllowHDR"),
			expectedOcclusionCulling = (bool)RequiredField(rig, "defaultOcclusionCulling"), expectedRenderShadows = (bool)RequiredField(rig, "defaultRenderShadows") };
		teardown = new TeardownMeasurement { request = request, rig = rig, grid = grid, camera = camera, lighting = lighting, sun = sun,
			report = report, started = EditorApplication.timeSinceStartup };
		session.teardownRequested = true; session.stage = "testing-teardown";
		session.message = "Destroying the real map controller without disabling or restoring it first; observing the surviving camera.";
		SaveGameplaySession(session);
		RenderPipelineManager.endCameraRendering += OnTeardownCameraRendered;
		// Exercise the real lifetime path. Never directly call Apply(false), OnDestroy, or Destroy the camera GameObject here.
		Object.Destroy(rig);
	}
	static void OnTeardownCameraRendered(ScriptableRenderContext context, Camera camera)
	{
		TeardownMeasurement current = teardown;
		if (current == null || current.camera != camera || current.rig || current.lastFrame == Time.frameCount) return;
		current.lastFrame = Time.frameCount;
		current.report.renderedFrames++;
		current.report.maxQueuedChunkRefreshObserved = Math.Max(current.report.maxQueuedChunkRefreshObserved, QueuedChunkRefresh(current.grid));
		if (HexNearVegetationMesh.LastCameraId == camera.GetInstanceID())
		{
			current.report.matchingVegetationFrames++;
			current.report.maxVegetationDrawCallsObserved = Math.Max(current.report.maxVegetationDrawCallsObserved, HexNearVegetationMesh.LastDrawCallCount);
			current.report.maxShadowOnlyDrawCallsObserved = Math.Max(current.report.maxShadowOnlyDrawCallsObserved,
				ReadStaticInt(typeof(HexNearVegetationMesh), "LastShadowOnlyDrawCallCount"));
		}
	}
	static void PollTeardown()
	{
		TeardownMeasurement current = teardown;
		if (current == null) return;
		if (!EditorApplication.isPlaying || !current.camera || !current.grid)
		{ FinishTeardown(false, "Play mode, the surviving map camera or grid was lost."); return; }
		current.report.maxQueuedChunkRefreshObserved = Math.Max(current.report.maxQueuedChunkRefreshObserved, QueuedChunkRefresh(current.grid));
		if (current.report.renderedFrames >= current.report.frameTarget) FinishTeardown(true, "Observed the surviving map camera after actual HexMapCamera destruction.");
		else if (EditorApplication.timeSinceStartup - current.started > 120d) FinishTeardown(false, "No 12 rendered map-camera frames arrived within 120 seconds.");
	}
	static bool SameColor(Color a, Color b) => ((Vector4)(a - b)).sqrMagnitude < .00000001f;
	static bool SameLighting(LightingMetrics a, LightingMetrics b) => a.sunIdentity == b.sunIdentity && a.shadows == b.shadows &&
		a.ambientMode == b.ambientMode && SameColor(a.lightColor, b.lightColor) && SameColor(a.ambientSky, b.ambientSky) &&
		SameColor(a.ambientEquator, b.ambientEquator) && SameColor(a.ambientGround, b.ambientGround) &&
		(a.sunIdentity == "null" || Quaternion.Angle(a.rotation, b.rotation) < .01f) && Mathf.Abs(a.intensity - b.intensity) < .0001f &&
		Mathf.Abs(a.shadowStrength - b.shadowStrength) < .0001f && Mathf.Abs(a.bias - b.bias) < .0001f && Mathf.Abs(a.normalBias - b.normalBias) < .0001f;
	static void FinishTeardown(bool completed, string message, bool reloading = false)
	{
		TeardownMeasurement current = teardown;
		if (current == null) return;
		teardown = null;
		RenderPipelineManager.endCameraRendering -= OnTeardownCameraRendered;
		TeardownReport report = current.report;
		report.completed = completed && report.renderedFrames >= report.frameTarget;
		report.finishedUtc = DateTime.UtcNow.ToString("O"); report.message = message;
		report.controllerDestroyed = !current.rig;
		report.cameraStillRendering = current.camera && current.camera.isActiveAndEnabled && report.renderedFrames >= report.frameTarget;
		report.nearLightingInactive = !current.lighting || !ReadField<bool>(current.lighting, "active");
		report.lightingAfter = ReadLightingMetrics(current.sun);
		report.pipelinesRestored = report.lightingExpected.graphicsPipeline == report.lightingAfter.graphicsPipeline &&
			report.lightingExpected.qualityPipeline == report.lightingAfter.qualityPipeline;
		report.lightingRestored = SameLighting(report.lightingExpected, report.lightingAfter);
		report.cameraAfter = ReadActualCameraMetrics(current.camera, report.cameraBefore.focus, report.cameraBefore.viewDistance);
		report.cameraSettingsRestored = report.cameraAfter != null && report.cameraAfter.allowMSAA == report.expectedAllowMSAA &&
			report.cameraAfter.allowHDR == report.expectedAllowHDR && report.cameraAfter.useOcclusionCulling == report.expectedOcclusionCulling &&
			report.cameraAfter.renderShadows == report.expectedRenderShadows;
		report.activeChunksAfter = current.grid ? current.grid.ActiveChunkCount : -1;
		report.vegetationInstancesAfter = VegetationInstanceCount(current.grid);
		report.passed = report.completed && report.controllerDestroyed && report.cameraStillRendering && report.nearLightingInactive &&
			report.pipelinesRestored && report.lightingRestored && report.cameraSettingsRestored;
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "CameraTeardown-" + report.id + ".json");
		File.WriteAllText(path, JsonUtility.ToJson(report, true));
		GameplaySession session = ReadGameplaySession();
		if (session != null && session.active)
		{
			session.teardownCompleted = report.completed; session.teardownPassed = report.passed; session.teardownPath = path;
			session.stage = report.passed ? "teardown-complete" : "teardown-failed"; session.message = message + " Stop this owned session to restore the saved scenes.";
			SaveGameplaySession(session);
		}
		Response response = reloading ? new Response { id = report.id, action = "gameplay-teardown", ok = false, message = message } : Status(current.request, report.passed, message);
		response.teardownPath = path;
		WriteResponse(response);
	}

	[Serializable] sealed class MeasurementFrame
	{
		public int frame, batches, triangles, vegetationDrawCalls, vegetationInstances;
		public double editorLoopMilliseconds;
	}
	[Serializable] sealed class TimingSample
	{
		public ulong frameStartTimestamp;
		public double cpuMilliseconds, gpuMilliseconds;
	}
	[Serializable] sealed class Distribution
	{
		public int count;
		public double mean, median, percentile95, maximum;
	}
	[Serializable] sealed class PerformanceReport
	{
		public string id, view, startedUtc, finishedUtc, message, unityVersion, graphicsDevice, graphicsApi;
		public string scope, timingScope, unityStatsScope, frameTimingNote;
		public bool completed, frameTimingEnabled, cpuTimingAvailable, gpuTimingAvailable, cameraMoved;
		public int requestedFrames, sampledFrames, warmupFrames, otherCameraRenderPasses, pixelWidth, pixelHeight;
		public int targetFrameRate, vSyncCount, cameraId;
		public double elapsedSeconds, editorLoopFramesPerSecond;
		public CameraMetrics startCamera, endCamera;
		public Distribution editorLoopMilliseconds, cpuMilliseconds, gpuMilliseconds, batches, triangles;
		public MeasurementFrame[] frames;
		public TimingSample[] frameTimings;
	}
	sealed class Measurement
	{
		public Request request;
		public HexNearTerrainShowcase showcase;
		public Camera camera;
		public PerformanceReport report;
		public readonly List<MeasurementFrame> frames = new(MeasurementFrames);
		public readonly List<TimingSample> frameTimings = new(MeasurementFrames);
		public readonly FrameTiming[] timingBuffer = new FrameTiming[4];
		public double started;
		public int warmup, lastFrame = -1, otherCameraPasses;
		public ulong latestTiming;
		public string failure;
	}

	static CameraMetrics ReadCameraMetrics(HexNearTerrainShowcase showcase)
	{
		if (!showcase || !showcase.previewCamera) return null;
		return ReadActualCameraMetrics(showcase.previewCamera, showcase.ViewFocus, showcase.ViewDistance);
	}

	static void BeginMeasurement(Request request)
	{
		HexNearTerrainShowcase showcase = Object.FindObjectOfType<HexNearTerrainShowcase>();
		if (!EditorApplication.isPlaying || EditorApplication.isPaused || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.path != ScenePath || !showcase.previewCamera || !showcase.previewCamera.isActiveAndEnabled)
			throw new InvalidOperationException("Measurement requires the ready showcase playing with its camera enabled and Play Mode unpaused.");
		if (!string.IsNullOrEmpty(request.view)) showcase.SetView(request.view);
		Camera camera = showcase.previewCamera;
		measurement = new Measurement { request = request, showcase = showcase, camera = camera,
			started = EditorApplication.timeSinceStartup,
			report = new PerformanceReport {
				id = request.id, view = string.IsNullOrEmpty(request.view) ? "current" : request.view,
				startedUtc = DateTime.UtcNow.ToString("O"), requestedFrames = MeasurementFrames,
				unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceName,
				graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
				cameraId = camera.GetInstanceID(), pixelWidth = camera.pixelWidth, pixelHeight = camera.pixelHeight,
				targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
				frameTimingEnabled = FrameTimingManager.IsFeatureEnabled(), startCamera = ReadCameraMetrics(showcase),
				scope = "Unity Editor Play Mode, 750-cell showcase, actual Game View camera rendering. This is not standalone player performance or full-world performance.",
				timingScope = "Time.unscaledDeltaTime is the Editor game-loop interval on 60 distinct frames that rendered the showcase camera, not isolated camera render cost. Editor overhead, VSync, throttling and other views may contribute.",
				unityStatsScope = "UnityEditor.UnityStats sampled after the showcase camera renders. Counters are Editor rendering statistics, may include other cameras or editor views, and are not guaranteed camera-exclusive.",
				frameTimingNote = "FrameTimingManager returns delayed whole-frame CPU/GPU timings, independently deduplicated by frame timestamp. Missing, zero or unsupported timings are unavailable, not zero-cost frames. No frame-timing project setting, frame-rate cap, VSync or resolution is changed."
			} };
		RenderPipelineManager.endCameraRendering += OnMeasurementCameraRendered;
	}

	static void OnMeasurementCameraRendered(ScriptableRenderContext context, Camera camera)
	{
		Measurement current = measurement;
		if (current == null || current.frames.Count >= MeasurementFrames || current.failure != null) return;
		if (camera != current.camera) { current.otherCameraPasses++; return; }
		// A render request or duplicate render in the same simulation frame cannot manufacture additional samples.
		if (current.lastFrame == Time.frameCount) return;
		current.lastFrame = Time.frameCount;
		try
		{
			bool warmingUp = current.warmup < MeasurementWarmupFrames;
			if (current.report.frameTimingEnabled)
			{
				FrameTimingManager.CaptureFrameTimings();
				uint count = FrameTimingManager.GetLatestTimings((uint)current.timingBuffer.Length, current.timingBuffer);
				ulong newest = current.latestTiming;
				for (int i = 0; i < count; i++)
				{
					FrameTiming timing = current.timingBuffer[i];
					if (timing.frameStartTimestamp <= current.latestTiming) continue;
					if (timing.frameStartTimestamp > newest) newest = timing.frameStartTimestamp;
					if (!warmingUp) current.frameTimings.Add(new TimingSample { frameStartTimestamp = timing.frameStartTimestamp,
						cpuMilliseconds = PositiveFinite(timing.cpuFrameTime) ? timing.cpuFrameTime : -1d,
						gpuMilliseconds = PositiveFinite(timing.gpuFrameTime) ? timing.gpuFrameTime : -1d });
				}
				current.latestTiming = newest;
			}
			if (warmingUp) { current.warmup++; return; }
			double milliseconds = Time.unscaledDeltaTime * 1000d;
			if (!PositiveFinite(milliseconds)) return;
			current.frames.Add(new MeasurementFrame { frame = Time.frameCount, editorLoopMilliseconds = milliseconds,
				batches = UnityStats.batches, triangles = UnityStats.triangles,
				vegetationDrawCalls = HexNearVegetationMesh.LastCameraId == camera.GetInstanceID() ? HexNearVegetationMesh.LastDrawCallCount : -1,
				vegetationInstances = HexNearVegetationMesh.LastCameraId == camera.GetInstanceID() ? HexNearVegetationMesh.LastDrawnInstanceCount : -1 });
		}
		catch (Exception exception) { current.failure = "Measurement callback failed: " + exception.Message; }
	}

	static bool PositiveFinite(double value) => value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);

	static void PollMeasurement()
	{
		if (measurement == null) return;
		if (measurement.failure != null) { FinishMeasurement(false, measurement.failure); return; }
		if (measurement.frames.Count >= MeasurementFrames)
			FinishMeasurement(true, "Collected 60 actual showcase-camera frames after 12 warmup frames in Unity Editor Play Mode.");
		else if (!EditorApplication.isPlaying || !measurement.showcase || !measurement.showcase.Ready ||
			!measurement.camera || !measurement.camera.isActiveAndEnabled || measurement.showcase.gameObject.scene.path != ScenePath)
			FinishMeasurement(false, "The ready, running showcase camera became unavailable.");
		else if (EditorApplication.isCompiling || EditorApplication.isUpdating)
			FinishMeasurement(false, "Compilation or asset import interrupted measurement.");
		else if (EditorApplication.timeSinceStartup - measurement.started > 120d)
			FinishMeasurement(false, "Timed out after 120 seconds; keep the Game View visible and Play Mode unpaused. Partial samples are not a completed measurement.");
	}

	static Distribution Summarize(List<double> values)
	{
		if (values.Count == 0) return new Distribution();
		values.Sort();
		double total = 0d;
		foreach (double value in values) total += value;
		int middle = values.Count / 2;
		return new Distribution { count = values.Count, mean = total / values.Count,
			median = values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5d : values[middle],
			percentile95 = values[Math.Max(0, (int)Math.Ceiling(values.Count * 0.95d) - 1)], maximum = values[values.Count - 1] };
	}

	static void FinishMeasurement(bool complete, string message, bool reloading = false)
	{
		Measurement current = measurement;
		if (current == null) return;
		measurement = null;
		RenderPipelineManager.endCameraRendering -= OnMeasurementCameraRendered;
		try
		{
			PerformanceReport report = current.report;
			report.completed = complete && current.frames.Count == MeasurementFrames;
			report.message = message;
			report.finishedUtc = DateTime.UtcNow.ToString("O");
			report.elapsedSeconds = EditorApplication.timeSinceStartup - current.started;
			report.sampledFrames = current.frames.Count;
			report.warmupFrames = current.warmup;
			report.otherCameraRenderPasses = current.otherCameraPasses;
			report.endCamera = ReadCameraMetrics(current.showcase);
			report.cameraMoved = report.endCamera == null || Vector3.Distance(report.startCamera.position, report.endCamera.position) > 0.01f ||
				Quaternion.Angle(Quaternion.Euler(report.startCamera.eulerAngles), Quaternion.Euler(report.endCamera.eulerAngles)) > 0.01f ||
				Mathf.Abs(report.startCamera.fieldOfView - report.endCamera.fieldOfView) > 0.01f;
			List<double> intervals = new(), batches = new(), triangles = new(), cpu = new(), gpu = new();
			foreach (MeasurementFrame frame in current.frames)
			{ intervals.Add(frame.editorLoopMilliseconds); batches.Add(frame.batches); triangles.Add(frame.triangles); }
			foreach (TimingSample timing in current.frameTimings)
			{ if (PositiveFinite(timing.cpuMilliseconds)) cpu.Add(timing.cpuMilliseconds); if (PositiveFinite(timing.gpuMilliseconds)) gpu.Add(timing.gpuMilliseconds); }
			report.editorLoopMilliseconds = Summarize(intervals);
			report.batches = Summarize(batches);
			report.triangles = Summarize(triangles);
			report.cpuMilliseconds = Summarize(cpu);
			report.gpuMilliseconds = Summarize(gpu);
			report.cpuTimingAvailable = cpu.Count > 0;
			report.gpuTimingAvailable = gpu.Count > 0;
			report.editorLoopFramesPerSecond = PositiveFinite(report.editorLoopMilliseconds.mean) ? 1000d / report.editorLoopMilliseconds.mean : 0d;
			report.frames = current.frames.ToArray();
			report.frameTimings = current.frameTimings.ToArray();
			Directory.CreateDirectory(ArtifactDirectory);
			string path = Path.Combine(ArtifactDirectory, "Performance-" + current.request.id + ".json");
			File.WriteAllText(path, JsonUtility.ToJson(report, true));
			// Avoid asset discovery while Unity is unloading the scripting domain.
			Response response = reloading ? new Response { id = current.request.id, action = "measure", ok = false,
				message = message, timestamp = DateTime.UtcNow.ToString("O") } : Status(current.request, report.completed, message);
			response.performancePath = path;
			response.measurementFrames = report.sampledFrames;
			response.measurementTargetFrames = MeasurementFrames;
			WriteResponse(response);
		}
		catch (Exception exception) { Debug.LogError("Unable to save showcase measurement: " + exception.Message); }
	}

	sealed class SurfaceSample
	{
		public int cell;
		public Vector2 local;
		public Vector3 point;
		public float cpu;
		public string category;
	}
	struct SeamPair { public int first, second; }
	[Serializable] sealed class HeightExample
	{
		public int cell;
		public string category;
		public Vector3 point;
		public float cpu, gpu, absoluteError;
	}
	[Serializable] sealed class SeamExample
	{
		public int cellA, cellB;
		public Vector3 point;
		public float cpuA, cpuB, gpuA, gpuB, cpuGap, gpuGap;
	}
	[Serializable] sealed class CategoryResult
	{
		public string category;
		public int samples;
		public float meanAbsoluteError, maxAbsoluteError;
	}
	[Serializable] sealed class SurfaceReport
	{
		public string timestamp, profile, graphicsDevice, comparison;
		public bool passed;
		public int sampleCount, seamCount, readbackCount, invalidValues, missingReadbacks;
		public float heightTolerance = 0.05f, seamTolerance = 0.05f;
		public float meanAbsoluteError, maxAbsoluteError, maxCpuSeamGap, maxGpuSeamGap, maxRenderedSeabedAdjustment;
		public CategoryResult[] categories;
		public HeightExample[] worstHeights;
		public SeamExample[] worstSeams;
		public string[] shaderMessages;
	}

	static string ValidateSurface(out bool passed, out string summary)
	{
		HexNearTerrainShowcase showcase = Object.FindObjectOfType<HexNearTerrainShowcase>();
		if (!EditorApplication.isPlaying || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.path != ScenePath)
			throw new InvalidOperationException("Surface validation requires a running, ready showcase.");
		if (!showcase.terrainStyle || !showcase.terrainStyle.UsesNearTerrain)
			throw new InvalidOperationException("Import and enable a ready near terrain profile before validating it.");
		if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
			!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
			throw new InvalidOperationException("This GPU does not support the required floating point validation targets.");
		Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(
			"Assets/HexMapPackage/Editor/Terrain/HexNearSurfaceValidation.shader");
		if (!shader || !shader.isSupported)
			throw new InvalidOperationException("The surface validation shader is unavailable or unsupported.");
		HexGrid grid = showcase.grid;
		List<SurfaceSample> samples = new();
		List<SeamPair> seams = new();
		Vector2[] offsets = { Vector2.zero, new(.35f, .15f), new(-.24f, .4f), new(.15f, -.3f) };
		for (int i = 0; i < grid.CellData.Length; i++)
		{
			foreach (Vector2 offset in offsets)
				AddSurfaceSample(grid, samples, i, grid.CellPositions[i] +
					new Vector3(offset.x, 0, offset.y) * HexMetrics.outerRadius);
			HexCell cell = new(i, grid);
			for (HexDirection direction = HexDirection.NE; direction <= HexDirection.NW; direction++)
			{
				if (!cell.TryGetNeighbor(direction, out HexCell neighbor) || neighbor.Index <= i) continue;
				Vector3 middle = (grid.CellPositions[i] + grid.CellPositions[neighbor.Index]) * 0.5f;
				Vector3 delta = grid.CellPositions[neighbor.Index] - grid.CellPositions[i];
				Vector3 tangent = new Vector3(-delta.z, 0, delta.x).normalized * HexMetrics.outerRadius * 0.35f;
				int edgeSamples = i % 7 == 0 ? 3 : 1;
				for (int k = 0; k < edgeSamples; k++)
				{
					Vector3 point = middle + (k == 0 ? Vector3.zero : k == 1 ? tangent : -tangent);
					int first = samples.Count;
					AddSurfaceSample(grid, samples, i, point);
					AddSurfaceSample(grid, samples, neighbor.Index, point);
					seams.Add(new SeamPair { first = first, second = first + 1 });
				}
			}
		}

		const int width = 128;
		int height = Mathf.CeilToInt(samples.Count / (float)width);
		Color[] input = new Color[width * height];
		for (int i = 0; i < input.Length; i++) input[i] = new Color(0, 0, 0, -1);
		for (int i = 0; i < samples.Count; i++) input[i] = new Color(
			samples[i].cell, samples[i].local.x, samples[i].local.y, i);
		Texture2D points = new(width, height, TextureFormat.RGBAFloat, false, true) {
			filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
		Material material = new(shader) { hideFlags = HideFlags.DontSave };
		RenderTexture target = RenderTexture.GetTemporary(width, height, 0,
			RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
		RenderTexture previous = RenderTexture.active;
		bool previousSRGB = GL.sRGBWrite;
		Texture2D readback = null;
		Color[] output;
		try
		{
			HexReliefMesh relief = grid.GetComponentInChildren<HexReliefMesh>();
			if (relief) material.CopyPropertiesFromMaterial(relief.GetComponent<MeshRenderer>().sharedMaterial);
			showcase.terrainStyle.ApplyTo(null);
			points.SetPixels(input);
			points.Apply(false, false);
			material.SetTexture("_SamplePoints", points);
			GL.sRGBWrite = false;
			Graphics.Blit(points, target, material, 0);
			RenderTexture.active = target;
			readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
			readback.Apply(false, false);
			output = readback.GetPixels();
		}
		finally
		{
			GL.sRGBWrite = previousSRGB;
			RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target);
			Object.DestroyImmediate(points);
			Object.DestroyImmediate(material);
			if (readback) Object.DestroyImmediate(readback);
		}

		SurfaceReport report = new() { timestamp = DateTime.UtcNow.ToString("O"),
			profile = showcase.terrainStyle.nearTerrainProfile.name,
			graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
			comparison = "HF_EvaluateRelief.y versus grid.SampleSurfaceHeight(root, identical map-local XZ, true). " +
				"Shared edges are evaluated from both root cells at identical positions. " +
				"The additional rendered seabed clamp is reported separately, not hidden in the height comparison.",
			sampleCount = samples.Count, seamCount = seams.Count };
		float[] gpu = new float[samples.Count];
		bool[] seen = new bool[samples.Count];
		List<HeightExample> examples = new();
		Dictionary<string, CategoryResult> categories = new();
		foreach (Color value in output)
		{
			if (!Finite(value.g) || !Finite(value.r) || !Finite(value.b)) { report.invalidValues++; continue; }
			int id = Mathf.RoundToInt(value.g);
			if (id < 0) continue;
			if (id >= samples.Count || seen[id] || Mathf.Abs(value.g - id) > 0.001f)
			{ report.invalidValues++; continue; }
			seen[id] = true;
			gpu[id] = value.r;
			report.readbackCount++;
			SurfaceSample sample = samples[id];
			float error = Mathf.Abs(value.r - sample.cpu);
			if (!Finite(sample.cpu)) { report.invalidValues++; continue; }
			report.meanAbsoluteError += error;
			report.maxAbsoluteError = Mathf.Max(report.maxAbsoluteError, error);
			report.maxRenderedSeabedAdjustment = Mathf.Max(report.maxRenderedSeabedAdjustment, Mathf.Abs(value.b - value.r));
			if (!categories.TryGetValue(sample.category, out CategoryResult category))
			{
				category = new CategoryResult { category = sample.category };
				categories.Add(sample.category, category);
			}
			category.samples++;
			category.meanAbsoluteError += error;
			category.maxAbsoluteError = Mathf.Max(category.maxAbsoluteError, error);
			examples.Add(new HeightExample { cell = sample.cell, category = sample.category,
				point = sample.point, cpu = sample.cpu, gpu = value.r, absoluteError = error });
		}
		report.missingReadbacks = report.sampleCount - report.readbackCount;
		report.meanAbsoluteError /= Mathf.Max(1, report.readbackCount);
		foreach (CategoryResult category in categories.Values) category.meanAbsoluteError /= Mathf.Max(1, category.samples);
		report.categories = new List<CategoryResult>(categories.Values).ToArray();
		examples.Sort((a, b) => b.absoluteError.CompareTo(a.absoluteError));
		if (examples.Count > 24) examples.RemoveRange(24, examples.Count - 24);
		report.worstHeights = examples.ToArray();
		List<SeamExample> seamExamples = new();
		foreach (SeamPair seam in seams)
		{
			if (!seen[seam.first] || !seen[seam.second]) continue;
			SurfaceSample a = samples[seam.first], b = samples[seam.second];
			float cpuGap = Mathf.Abs(a.cpu - b.cpu), gpuGap = Mathf.Abs(gpu[seam.first] - gpu[seam.second]);
			report.maxCpuSeamGap = Mathf.Max(report.maxCpuSeamGap, cpuGap);
			report.maxGpuSeamGap = Mathf.Max(report.maxGpuSeamGap, gpuGap);
			seamExamples.Add(new SeamExample { cellA = a.cell, cellB = b.cell, point = a.point,
				cpuA = a.cpu, cpuB = b.cpu, gpuA = gpu[seam.first], gpuB = gpu[seam.second], cpuGap = cpuGap, gpuGap = gpuGap });
		}
		seamExamples.Sort((a, b) => Mathf.Max(b.cpuGap, b.gpuGap).CompareTo(Mathf.Max(a.cpuGap, a.gpuGap)));
		if (seamExamples.Count > 24) seamExamples.RemoveRange(24, seamExamples.Count - 24);
		report.worstSeams = seamExamples.ToArray();
		List<string> shaderMessages = new(CollectShaderMessages());
		foreach (var shaderMessage in ShaderUtil.GetShaderMessages(shader))
			shaderMessages.Add(shaderMessage.severity + ": SurfaceValidation:" + shaderMessage.line + " " + shaderMessage.message);
		report.shaderMessages = shaderMessages.ToArray();
		bool shaderError = shaderMessages.Exists(item => item.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
		report.passed = report.readbackCount == report.sampleCount && report.invalidValues == 0 && !shaderError &&
			report.maxAbsoluteError <= report.heightTolerance && report.maxCpuSeamGap <= report.seamTolerance &&
			report.maxGpuSeamGap <= report.seamTolerance;
		passed = report.passed;
		summary = $"Surface validation {(passed ? "passed" : "failed")}: {report.readbackCount}/{report.sampleCount} samples, " +
			$"max CPU/GPU error {report.maxAbsoluteError:F6}, mean {report.meanAbsoluteError:F6}, " +
			$"CPU/GPU seam gaps {report.maxCpuSeamGap:F6}/{report.maxGpuSeamGap:F6}.";
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "SurfaceValidation.json");
		File.WriteAllText(path, JsonUtility.ToJson(report, true));
		return path;
	}

	static void AddSurfaceSample(HexGrid grid, List<SurfaceSample> samples, int cellIndex, Vector3 point)
	{
		HexCellData cell = grid.CellData[cellIndex];
		Vector3 relative = point - grid.CellPositions[cellIndex];
		samples.Add(new SurfaceSample { cell = cellIndex, point = point,
			local = new Vector2(relative.x, relative.z) / HexMetrics.outerRadius,
			cpu = grid.SampleSurfaceHeight(cellIndex, point, true),
			category = "biome" + cell.TerrainTypeIndex + "/" + (cell.IsUnderwater ? "water" : cell.landform.ToString()) +
				(cell.HasHFRiver ? "/river" : "") });
	}

	static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	static string[] CollectShaderMessages()
	{
		List<string> messages = new();
		foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/HexMapPackage/Materials", "Assets/HexMapPackage/Resources", "Assets/HexMapPackage/Editor/Terrain" }))
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
			if (!shader) continue;
			foreach (var message in ShaderUtil.GetShaderMessages(shader))
			{
				messages.Add(message.severity + ": " + path + ":" + message.line + " " + message.message);
				if (messages.Count >= 80) return messages.ToArray();
			}
		}
		return messages.ToArray();
	}

	[MenuItem("Tools/Hex Map/Near Terrain/Build Showcase")]
	public static void BuildShowcase() => RunMenu("build");
	[MenuItem("Tools/Hex Map/Near Terrain/Open Showcase")]
	public static void OpenShowcase() => RunMenu("open");
	[MenuItem("Tools/Hex Map/Near Terrain/Capture Showcase")]
	public static void CaptureShowcase() => RunMenu("capture");
	static void RunMenu(string action)
	{
		try
		{
			Response response = Execute(new Request { id = "menu-" + DateTime.UtcNow.Ticks, action = action });
			WriteResponse(response);
			Debug.Log(response.message + (string.IsNullOrEmpty(response.screenshot) ? "" : " " + response.screenshot));
		}
		catch (Exception exception) { Debug.LogError(exception.Message); }
	}
}
