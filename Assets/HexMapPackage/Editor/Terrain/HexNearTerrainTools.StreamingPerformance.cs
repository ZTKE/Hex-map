using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

public static partial class HexNearTerrainTools
{
	const int StreamingPanFrames = 240;
	const int StreamingSettleFrames = 12;
	static StreamingPerformance streamingPerformance;

	[Serializable] sealed class StreamingCounters
	{
		public double cached, resident, cacheHits, builds, pendingBuilds, lastStreamingCpuMilliseconds, totalStreamingWindowCpuMilliseconds;
		public int active, queuedRefreshes, pendingActivationEntries, pendingInteractionEntries;
		public int cachedDictionaryEntries = -1, staticHiddenCached = -1, staticHiddenResident = -1;
	}
	[Serializable] struct StreamingFrame
	{
		public int frame, active, queuedRefreshes, profilerFrame;
		public double milliseconds, streamingCpuMilliseconds, streamingWindowCpuMilliseconds;
		public bool missing, cover, shadersCompiling, ready, moving;
	}
	[Serializable] sealed class StreamingDistribution
	{
		public int count, over16_7, over33_3, over50;
		public double mean, percentile50, percentile95, percentile99, maximum;
	}
	[Serializable] sealed class StreamingPhaseReport
	{
		public string name;
		public float distance;
		public Vector3 startFocus, endFocus;
		public double commandMilliseconds, readyMilliseconds = -1, elapsedMilliseconds;
		public long managedBytesBefore, managedBytesAfter;
		public long totalAllocatedBytesBefore, totalAllocatedBytesAfter, monoUsedBytesBefore, monoUsedBytesAfter;
		public long graphicsDriverBytesBefore, graphicsDriverBytesAfter;
		public int gen0Collections, gen1Collections, gen2Collections;
		public StreamingCounters before, after;
		public StreamingDistribution intervals;
		public StreamingFrame[] frames;
	}
	[Serializable] sealed class StreamingReport
	{
		public string id, startedUtc, finishedUtc, message, unityVersion, graphicsDevice, graphicsApi;
		public string scope = "Actual Game_2 camera frames in Unity Editor Play Mode; Editor, other cameras, VSync and shader compilation may contribute. Not standalone or isolated terrain render cost.";
		public string protocol = "Same forest focus (8.2E,48.2N): overview 1800; near 180 cold; overview 1800; near 180 warm; mid 900; near 180; eight chunk widths east over 240 rendered frames; same route back. Twelve consecutive ready frames settle every phase. No forced renders or timing/quality changes. First near is cold for this route, not guaranteed globally empty asset caches.";
		public string readiness = "No missing desired chunks, pending activations/interactions/builds, enabled active chunk refreshes, streaming cover, view transition or shader compiler work; correct distance and overview tier. readyMilliseconds is the first frame of the final 12-frame ready sequence.";
		public string metrics = "Unsupported public telemetry is -1. Getter delegates and existing collection references are bound once; no per-frame reflection, object discovery or allocation scanning. Value-type samples use preallocated lists that can grow during unusually long phases; report serialization occurs after measuring.";
		public bool completed, cameraRestored;
		public int harnessVersion = 12, pixelWidth, pixelHeight, targetFrameRate, vSyncCount, otherCameraPasses;
		public StreamingCpuProfile cpuProfile;
		public float routeMapUnits;
		public CameraMetrics originalCamera;
		public StreamingPhaseReport[] phases;
	}
	sealed class StreamingPerformance
	{
		public Request request;
		public HexGrid grid;
		public HexMapCamera rig;
		public Camera camera;
		public HexGridChunk[] chunks;
		public HashSet<int> activeIndices;
		public Dictionary<int, HexGridChunk> cachedChunks;
		public Func<HexGridChunk, bool> isStaticHidden;
		public List<int> activations, interactions;
		public Action<float> setZoom;
		public Func<double> cached, resident, hits, builds, pendingBuilds, streamCpu, streamWindowCpu;
		public readonly List<StreamingPhaseReport> phases = new();
		public List<StreamingFrame> frames;
		public StreamingPhaseReport phase;
		public StreamingReport report;
		public GameplaySession originalSession;
		public Vector3 originalPosition, origin, expectedPosition;
		public Quaternion originalRotation;
		public float originalZoom, nearStick, farStick;
		public bool originalExtraClose, originalEnabled;
		public int phaseIndex = -1, lastFrame = -1, movingFrames, readyFrames, otherCameraPasses;
		public int gc0, gc1, gc2;
		public double started, phaseStarted, lastStreamWindowCpu;
		public bool finishPending;
		public string failure;
		public bool profileRequested, profiling, originalProfilerEnabled, originalProfileEditor, originalCpuAreaEnabled;
		public int originalProfilerHistory;
		public Action<int> setProfilerHistory;
	}

	static void InitializeStreamingPerformance()
	{
		EditorApplication.delayCall += () => {
			Directory.CreateDirectory(RequestDirectory);
			File.WriteAllText(Path.Combine(RequestDirectory, "StreamingPerformanceHarnessReady-v12.json"),
				"{\"timestamp\":\"" + DateTime.UtcNow.ToString("O") + "\",\"version\":12}");
		};
		EditorApplication.update += PollStreamingPerformance;
		AssemblyReloadEvents.beforeAssemblyReload += () => FinishStreamingPerformance(false, "Assembly reload interrupted measurement.", true);
		EditorApplication.playModeStateChanged += state => {
			if (state == PlayModeStateChange.ExitingPlayMode) FinishStreamingPerformance(false, "Play mode ended before measurement completed.");
		};
	}

	static Func<double> BindStreamingCounter(HexGrid grid, string name)
	{
		MethodInfo getter = typeof(HexGrid).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
		if (getter == null) return () => -1;
		if (getter.ReturnType == typeof(int)) { var read = (Func<int>)getter.CreateDelegate(typeof(Func<int>), grid); return () => read(); }
		if (getter.ReturnType == typeof(long)) { var read = (Func<long>)getter.CreateDelegate(typeof(Func<long>), grid); return () => read(); }
		if (getter.ReturnType == typeof(float)) { var read = (Func<float>)getter.CreateDelegate(typeof(Func<float>), grid); return () => read(); }
		if (getter.ReturnType == typeof(double)) return (Func<double>)getter.CreateDelegate(typeof(Func<double>), grid);
		return () => -1;
	}

	static void BeginStreamingPerformance(Request request)
	{
		GameplayMetrics state = ReadGameplayMetrics();
		if (streamingPerformance != null || measurement != null || worldReview != null || teardown != null ||
			!EditorApplication.isPlaying || EditorApplication.isPaused || !state.ownedSession || !state.viewReady ||
			HexMapCamera.CurrentViewMode != HexMapCamera.ViewMode.Flat)
			throw new InvalidOperationException("Streaming measurement requires a ready, unpaused, owned flat Game_2 session and no other review.");
		HexGrid grid = FindGameplayComponent<HexGrid>();
		HexMapCamera rig = FindGameplayComponent<HexMapCamera>();
		Camera camera = rig.GetComponentInChildren<Camera>();
		if (!camera || !camera.isActiveAndEnabled) throw new InvalidOperationException("Game_2 camera is unavailable.");
		bool profile = string.Equals(request.view, "profile", StringComparison.OrdinalIgnoreCase);
		if (profile && ProfilerDriver.deepProfiling) throw new InvalidOperationException("Disable Deep Profiling before the bounded CPU diagnostic.");
		var current = new StreamingPerformance {
			profileRequested = profile,
			request = request, grid = grid, rig = rig, camera = camera,
			chunks = ReadField<HexGridChunk[]>(grid, "chunks"), activeIndices = ReadField<HashSet<int>>(grid, "activeChunkIndices"),
			activations = ReadField<List<int>>(grid, "pendingChunkActivations"), interactions = ReadField<List<int>>(grid, "pendingInteractionActivations"),
			setZoom = (Action<float>)typeof(HexMapCamera).GetMethod("SetZoom", BindingFlags.NonPublic | BindingFlags.Instance).CreateDelegate(typeof(Action<float>), rig),
			cached = BindStreamingCounter(grid, "CachedChunkCount"), resident = BindStreamingCounter(grid, "ResidentChunkCount"),
			hits = BindStreamingCounter(grid, "ChunkCacheHitCount"), builds = BindStreamingCounter(grid, "ChunkBuildCount"),
			pendingBuilds = BindStreamingCounter(grid, "PendingChunkBuildCount"), streamCpu = BindStreamingCounter(grid, "LastStreamingCpuMilliseconds"),
			streamWindowCpu = BindStreamingCounter(grid, "TotalStreamingWindowCpuMilliseconds"),
			originalPosition = rig.transform.localPosition, originalRotation = rig.transform.localRotation,
			originalZoom = HexMapCamera.CurrentZoom, originalExtraClose = HexMapCamera.IsExtraCloseZoom, originalEnabled = rig.enabled,
			originalSession = ReadGameplaySession(), farStick = ReadField<float>(rig, "stickMinZoom", -2400), nearStick = ReadField<float>(rig, "stickMaxZoom", -120),
			started = EditorApplication.timeSinceStartup,
			report = new StreamingReport { id = request.id, startedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
				graphicsDevice = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
				pixelWidth = camera.pixelWidth, pixelHeight = camera.pixelHeight, targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount,
				routeMapUnits = 8 * HexMetrics.chunkSizeX * HexMetrics.innerDiameter,
				originalCamera = ReadActualCameraMetrics(camera, rig.transform.position, HexMapCamera.CurrentStickDistance) }
		};
		if (current.chunks == null || current.activeIndices == null || current.activations == null || current.interactions == null)
			throw new InvalidOperationException("The fixed streaming readiness collections are unavailable.");
		current.origin = rig.transform.localPosition;
		current.cachedChunks = ReadField<Dictionary<int, HexGridChunk>>(grid, "cachedChunks");
		MethodInfo staticHiddenGetter = typeof(HexGridChunk).GetProperty("IsStaticStreamingHidden", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
		if (staticHiddenGetter != null) current.isStaticHidden = (Func<HexGridChunk, bool>)staticHiddenGetter.CreateDelegate(typeof(Func<HexGridChunk, bool>));
		Vector3 forest = grid.CellPositions[GeographicCell(grid, 8.2f, 48.2f)];
		current.origin.x = forest.x; current.origin.z = forest.z;
		current.lastStreamWindowCpu = current.streamWindowCpu();
		streamingPerformance = current;
		RenderPipelineManager.endCameraRendering += OnStreamingPerformanceCamera;
		try { BeginNextStreamingPhase(current); }
		catch { FinishStreamingPerformance(false, "Unable to start the fixed route."); throw; }
	}

	static int CountStreamingRefreshes(StreamingPerformance current)
	{
		int queued = 0;
		foreach (int index in current.activeIndices)
		{
			HexGridChunk chunk = current.chunks[index];
			if (chunk && chunk.enabled && chunk.gameObject.activeInHierarchy) queued++;
		}
		return queued;
	}

	static StreamingCounters ReadStreamingCounters(StreamingPerformance current)
	{
		var counters = new StreamingCounters {
			cached = current.cached(), resident = current.resident(), cacheHits = current.hits(), builds = current.builds(), pendingBuilds = current.pendingBuilds(),
			lastStreamingCpuMilliseconds = current.streamCpu(), totalStreamingWindowCpuMilliseconds = current.streamWindowCpu(), active = current.grid.ActiveChunkCount,
			queuedRefreshes = CountStreamingRefreshes(current), pendingActivationEntries = current.activations.Count, pendingInteractionEntries = current.interactions.Count
		};
		// Phase boundaries only: prove shipped prefabs actually use static hiding.
		if (current.cachedChunks != null) counters.cachedDictionaryEntries = current.cachedChunks.Count;
		if (current.cachedChunks != null && current.isStaticHidden != null)
		{
			counters.staticHiddenCached = 0;
			foreach (HexGridChunk chunk in current.cachedChunks.Values)
				if (chunk && current.isStaticHidden(chunk)) counters.staticHiddenCached++;
			counters.staticHiddenResident = counters.staticHiddenCached;
			foreach (int index in current.activeIndices)
				if (current.chunks[index] && current.isStaticHidden(current.chunks[index])) counters.staticHiddenResident++;
		}
		return counters;
	}

	static void ApplyStreamingPose(StreamingPerformance current, Vector3 position, float? distance = null)
	{
		current.rig.transform.localPosition = position;
		if (distance.HasValue) current.setZoom(Mathf.InverseLerp(current.farStick, current.nearStick, -distance.Value));
		HexMapCamera.ValidatePosition();
		current.expectedPosition = current.rig.transform.localPosition;
	}

	static void BeginNextStreamingPhase(StreamingPerformance current)
	{
		current.phaseIndex++;
		if (current.phaseIndex == 8) { current.finishPending = true; return; }
		string[] names = { "setup-overview", "cold-overview-to-near", "near-to-overview", "warm-overview-to-near", "near-to-mid", "warm-mid-to-near", "pan-east-eight-chunks", "pan-back-same-route" };
		float[] distances = { 1800, 180, 1800, 180, 900, 180, 180, 180 };
		current.frames = new List<StreamingFrame>(2048);
		current.phase = new StreamingPhaseReport { name = names[current.phaseIndex], distance = distances[current.phaseIndex],
			startFocus = current.rig.transform.localPosition, before = ReadStreamingCounters(current), managedBytesBefore = GC.GetTotalMemory(false),
			totalAllocatedBytesBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
			monoUsedBytesBefore = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
			graphicsDriverBytesBefore = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() };
		current.gc0 = GC.CollectionCount(0); current.gc1 = GC.CollectionCount(1); current.gc2 = GC.CollectionCount(2);
		current.phases.Add(current.phase);
		current.movingFrames = 0; current.readyFrames = 0;
		current.phaseStarted = EditorApplication.timeSinceStartup;
		if (current.phaseIndex < 6) ApplyStreamingPose(current, current.origin, current.phase.distance);
		current.phase.commandMilliseconds = (EditorApplication.timeSinceStartup - current.phaseStarted) * 1000;
		if (current.phaseIndex == 6 && current.profileRequested) BeginStreamingCpuProfile(current);
	}

	static void OnStreamingPerformanceCamera(ScriptableRenderContext context, Camera camera)
	{
		StreamingPerformance current = streamingPerformance;
		if (current == null || current.finishPending || current.failure != null) return;
		if (camera != current.camera) { current.otherCameraPasses++; return; }
		if (current.lastFrame == Time.frameCount) return;
		current.lastFrame = Time.frameCount;
		try
		{
			if (camera.pixelWidth != current.report.pixelWidth || camera.pixelHeight != current.report.pixelHeight ||
				Application.targetFrameRate != current.report.targetFrameRate || QualitySettings.vSyncCount != current.report.vSyncCount)
				throw new InvalidOperationException("Resolution or frame-rate settings changed during the benchmark.");
			if (Vector3.Distance(current.rig.transform.localPosition, current.expectedPosition) > .05f)
				throw new InvalidOperationException("Camera input or another controller changed the deterministic benchmark pose.");
			if (Quaternion.Angle(current.rig.transform.localRotation, current.originalRotation) > .1f)
				throw new InvalidOperationException("Camera input or another controller changed the deterministic benchmark heading.");
			if (Mathf.Abs(HexMapCamera.CurrentStickDistance - current.phase.distance) > .1f)
				throw new InvalidOperationException("Camera input or another controller changed the deterministic benchmark zoom.");
			int queued = CountStreamingRefreshes(current);
			bool missing = current.grid.HasMissingDesiredChunks;
			bool shaders = ShaderUtil.anythingCompiling;
			bool moving = current.phaseIndex >= 6 && current.movingFrames < StreamingPanFrames;
			bool ready = !moving && !missing && !current.grid.IsStreamingCoverActive && !HexMapCamera.IsViewTransitioning && !shaders && queued == 0 &&
				current.activations.Count == 0 && current.interactions.Count == 0 && current.pendingBuilds() <= 0 &&
				Mathf.Abs(HexMapCamera.CurrentStickDistance - current.phase.distance) < .1f &&
				current.grid.IsOverviewMode == (current.phase.distance > HexMapCamera.MidViewMinDistance);
			double windowCpu = current.streamWindowCpu();
			double windowDelta = windowCpu >= 0 && current.lastStreamWindowCpu >= 0 ? Math.Max(0, windowCpu - current.lastStreamWindowCpu) : -1;
			current.lastStreamWindowCpu = windowCpu;
			current.frames.Add(new StreamingFrame { frame = Time.frameCount, milliseconds = Time.unscaledDeltaTime * 1000d,
				profilerFrame = current.profiling ? ProfilerDriver.lastFrameIndex : -1,
				streamingCpuMilliseconds = current.streamCpu(), streamingWindowCpuMilliseconds = windowDelta,
				active = current.grid.ActiveChunkCount, queuedRefreshes = queued,
				missing = missing, cover = current.grid.IsStreamingCoverActive, shadersCompiling = shaders, ready = ready, moving = moving });
			if (ready)
			{
				if (current.readyFrames == 0) current.phase.readyMilliseconds = (EditorApplication.timeSinceStartup - current.phaseStarted) * 1000;
				current.readyFrames++;
			}
			else { current.readyFrames = 0; current.phase.readyMilliseconds = -1; }
			if (current.readyFrames >= StreamingSettleFrames)
			{
				CompleteStreamingPhase(current);
				BeginNextStreamingPhase(current);
			}
			else if (moving)
			{
				current.movingFrames++;
				float t = current.movingFrames / (float)StreamingPanFrames;
				Vector3 position = current.origin;
				position.x += current.report.routeMapUnits * (current.phaseIndex == 6 ? t : 1 - t);
				ApplyStreamingPose(current, position);
			}
		}
		catch (Exception exception) { current.failure = exception.Message; }
	}

	static void CompleteStreamingPhase(StreamingPerformance current)
	{
		current.phase.elapsedMilliseconds = (EditorApplication.timeSinceStartup - current.phaseStarted) * 1000;
		current.phase.endFocus = current.rig.transform.localPosition;
		current.phase.after = ReadStreamingCounters(current);
		current.phase.managedBytesAfter = GC.GetTotalMemory(false);
		current.phase.totalAllocatedBytesAfter = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
		current.phase.monoUsedBytesAfter = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
		current.phase.graphicsDriverBytesAfter = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
		current.phase.gen0Collections = GC.CollectionCount(0) - current.gc0;
		current.phase.gen1Collections = GC.CollectionCount(1) - current.gc1;
		current.phase.gen2Collections = GC.CollectionCount(2) - current.gc2;
		current.phase.frames = current.frames.ToArray();
		current.phase.intervals = SummarizeStreaming(current.frames);
	}

	static StreamingDistribution SummarizeStreaming(List<StreamingFrame> frames)
	{
		var result = new StreamingDistribution { count = frames.Count };
		if (frames.Count == 0) return result;
		var values = new List<double>(frames.Count);
		foreach (StreamingFrame frame in frames)
		{
			values.Add(frame.milliseconds); result.mean += frame.milliseconds;
			if (frame.milliseconds > 1000d / 60) result.over16_7++;
			if (frame.milliseconds > 1000d / 30) result.over33_3++;
			if (frame.milliseconds > 50) result.over50++;
		}
		values.Sort(); result.mean /= values.Count;
		result.percentile50 = values[(values.Count - 1) / 2];
		result.percentile95 = values[Math.Max(0, (int)Math.Ceiling(values.Count * .95) - 1)];
		result.percentile99 = values[Math.Max(0, (int)Math.Ceiling(values.Count * .99) - 1)];
		result.maximum = values[values.Count - 1];
		return result;
	}

	static void PollStreamingPerformance()
	{
		StreamingPerformance current = streamingPerformance;
		if (current == null) return;
		if (current.failure != null) FinishStreamingPerformance(false, current.failure);
		else if (current.finishPending) FinishStreamingPerformance(true, "Completed all fixed full-game streaming phases and restored the original camera.");
		else if (!EditorApplication.isPlaying || !current.grid || !current.rig || !current.camera || !current.camera.isActiveAndEnabled)
			FinishStreamingPerformance(false, "The actual Game_2 camera became unavailable.");
		else if (EditorApplication.isCompiling || EditorApplication.isUpdating)
			FinishStreamingPerformance(false, "Script compilation or asset import interrupted measurement.");
		else if (EditorApplication.isPaused)
			FinishStreamingPerformance(false, "Play Mode was paused during measurement.");
		else if (EditorApplication.timeSinceStartup - current.started > 600)
			FinishStreamingPerformance(false, "Timed out after ten minutes. Partial phases are not a completed benchmark.");
	}

	static void FinishStreamingPerformance(bool completed, string message, bool reloading = false)
	{
		StreamingPerformance current = streamingPerformance;
		if (current == null) return;
		RenderPipelineManager.endCameraRendering -= OnStreamingPerformanceCamera;
		streamingPerformance = null;
		FinishStreamingCpuProfile(current, reloading);
		if (!current.finishPending && current.phase != null && current.phase.frames == null) CompleteStreamingPhase(current);
		current.report.completed = completed;
		current.report.finishedUtc = DateTime.UtcNow.ToString("O");
		current.report.message = message;
		current.report.otherCameraPasses = current.otherCameraPasses;
		current.report.phases = current.phases.ToArray();
		try
		{
			if (EditorApplication.isPlaying && current.rig)
			{
				current.rig.enabled = current.originalEnabled;
				current.rig.transform.localRotation = current.originalRotation;
				current.rig.transform.localPosition = current.originalPosition;
				current.setZoom(current.originalZoom);
				HexMapCamera.SetExtraCloseZoom(current.originalExtraClose);
				HexMapCamera.ValidatePosition();
				SaveGameplaySession(current.originalSession);
				current.report.cameraRestored = Vector3.Distance(current.rig.transform.localPosition, current.originalPosition) < .05f &&
					Mathf.Abs(HexMapCamera.CurrentZoom - current.originalZoom) < .0001f;
			}
		}
		catch (Exception exception) { current.report.completed = false; current.report.message += " Restore failed: " + exception.Message; }
		if (completed && !current.report.cameraRestored) { current.report.completed = false; current.report.message += " Original camera restoration could not be verified."; }
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "StreamingPerformance-" + current.request.id + ".json");
		// Publish by rename so a filesystem completion event observes complete JSON.
		string temporary = path + ".pending";
		File.WriteAllText(temporary, JsonUtility.ToJson(current.report, true));
		File.Move(temporary, path);
		Response response = reloading ? new Response { id = current.request.id, action = current.request.action, ok = false, message = message } :
			Status(current.request, current.report.completed, current.report.message);
		response.performancePath = path;
		WriteResponse(response);
	}

	[Serializable] sealed class StreamingCpuSample
	{
		public string name, parent;
		public int index, depth;
		public double inclusiveMilliseconds, selfMilliseconds;
	}
	[Serializable] sealed class StreamingCpuThread
	{
		public string name, group;
		public int index, samples;
		public double frameMilliseconds;
		public StreamingCpuSample[] topInclusive, topSelf;
	}
	[Serializable] sealed class StreamingCpuFrame
	{
		public string phase;
		public int profilerFrame, observedCameraFrame;
		public double cameraIntervalMilliseconds, profilerFrameMilliseconds;
		public StreamingCpuThread[] threads;
	}
	[Serializable] sealed class StreamingCpuProfile
	{
		public string scope = "Separate attribution run: CPU Profiler including EditorLoop is enabled for the two pan phases only, without Deep Profiling. Its timings must not be compared as clean FPS results. Per camera render only the latest completed profiler frame index is stored. The profiler index can lag the camera; ranking uses recorded CPU frame duration, not assumed exact camera/frame alignment. All sample traversal and raw-profile serialization happen after the route.";
		public string rawProfilePath, message, connection;
		public bool captured, settingsRestored;
		public int firstAvailableFrame, lastAvailableFrame, capturedFrameReferences, uniqueAvailableFrames, missingFrames;
		public StreamingCpuFrame[] worstFrames;
	}

	static void BeginStreamingCpuProfile(StreamingPerformance current)
	{
		current.originalProfilerEnabled = ProfilerDriver.enabled;
		current.originalProfileEditor = ProfilerDriver.profileEditor;
		current.originalCpuAreaEnabled = ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
		// Unity exposes these history APIs only inside the editor assembly in 2022.3.
		const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		Type settingsType = typeof(ProfilerDriver).Assembly.GetType("UnityEditor.Profiling.ProfilerUserSettings", true);
		var readHistory = (Func<int>)settingsType.GetProperty("frameCount", staticFlags).GetGetMethod(true).CreateDelegate(typeof(Func<int>));
		current.setProfilerHistory = (Action<int>)typeof(ProfilerDriver).GetMethod("SetMaxFrameHistoryLength", staticFlags).CreateDelegate(typeof(Action<int>));
		current.originalProfilerHistory = readHistory();
		current.report.cpuProfile = new StreamingCpuProfile {
			connection = ProfilerDriver.GetConnectionIdentifier(ProfilerDriver.connectedProfiler)
		};
		current.profiling = true;
		// Preserve the user's history setting; temporarily retain the complete 480-frame route plus settling.
		current.setProfilerHistory(Math.Max(600, current.originalProfilerHistory));
		ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true);
		ProfilerDriver.profileEditor = true;
		ProfilerDriver.enabled = true;
	}

	static void FinishStreamingCpuProfile(StreamingPerformance current, bool reloading)
	{
		if (!current.profiling) return;
		StreamingCpuProfile report = current.report.cpuProfile;
		try
		{
			ProfilerDriver.enabled = false;
			if (reloading) { report.message = "Reload interrupted CPU profile extraction."; return; }
			report.firstAvailableFrame = ProfilerDriver.firstFrameIndex;
			report.lastAvailableFrame = ProfilerDriver.lastFrameIndex;
			var candidates = new List<StreamingCpuFrame>();
			var visited = new HashSet<int>();
			foreach (StreamingPhaseReport phase in current.phases)
			{
				if (phase.frames == null) continue;
				foreach (StreamingFrame sample in phase.frames)
				{
					if (sample.profilerFrame < 0) continue;
					report.capturedFrameReferences++;
					if (!visited.Add(sample.profilerFrame)) continue;
					using RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(sample.profilerFrame, 0);
					if (!raw.valid) { report.missingFrames++; continue; }
					candidates.Add(new StreamingCpuFrame { phase = phase.name, profilerFrame = sample.profilerFrame,
						observedCameraFrame = sample.frame, cameraIntervalMilliseconds = sample.milliseconds, profilerFrameMilliseconds = raw.frameTimeMs });
				}
			}
			report.uniqueAvailableFrames = candidates.Count;
			candidates.Sort((a, b) => b.profilerFrameMilliseconds.CompareTo(a.profilerFrameMilliseconds));
			if (candidates.Count > 20) candidates.RemoveRange(20, candidates.Count - 20);
			foreach (StreamingCpuFrame frame in candidates)
			{
				var threads = new List<StreamingCpuThread>();
				for (int thread = 0; thread < 64; thread++)
				{
					using RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame.profilerFrame, thread);
					if (!raw.valid) break;
					if (thread == 0 || raw.threadName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0 ||
						raw.threadName.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0)
						threads.Add(ReadStreamingCpuThread(raw));
				}
				frame.threads = threads.ToArray();
			}
			report.worstFrames = candidates.ToArray();
			Directory.CreateDirectory(ArtifactDirectory);
			report.rawProfilePath = Path.Combine(ArtifactDirectory, "StreamingCpuProfile-" + current.request.id + ".raw");
			ProfilerDriver.SaveProfile(report.rawProfilePath);
			report.captured = candidates.Count > 0;
			report.message = report.captured ? "Extracted the 20 worst available recorded pan frames and saved the raw CPU profile." : "No recorded CPU frames were available.";
		}
		catch (Exception exception) { report.message = "CPU profile extraction failed: " + exception; }
		finally
		{
			ProfilerDriver.profileEditor = current.originalProfileEditor;
			ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, current.originalCpuAreaEnabled);
			current.setProfilerHistory(current.originalProfilerHistory);
			ProfilerDriver.enabled = current.originalProfilerEnabled;
			current.profiling = false;
			report.settingsRestored = ProfilerDriver.profileEditor == current.originalProfileEditor &&
				ProfilerDriver.enabled == current.originalProfilerEnabled &&
				ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU) == current.originalCpuAreaEnabled;
		}
	}

	static StreamingCpuThread ReadStreamingCpuThread(RawFrameDataView raw)
	{
		var samples = new List<StreamingCpuSample>(raw.sampleCount);
		var parentIndices = new Stack<int>();
		var parentEnds = new Stack<int>();
		for (int index = 0; index < raw.sampleCount; index++)
		{
			while (parentEnds.Count > 0 && index > parentEnds.Peek()) { parentEnds.Pop(); parentIndices.Pop(); }
			var sample = new StreamingCpuSample { index = index, name = raw.GetSampleName(index), depth = parentIndices.Count,
				parent = parentIndices.Count > 0 ? samples[parentIndices.Peek()].name : "",
				inclusiveMilliseconds = raw.GetSampleTimeMs(index), selfMilliseconds = raw.GetSampleTimeMs(index) };
			if (parentIndices.Count > 0) samples[parentIndices.Peek()].selfMilliseconds -= sample.inclusiveMilliseconds;
			samples.Add(sample);
			int descendants = raw.GetSampleChildrenCountRecursive(index);
			if (descendants > 0) { parentIndices.Push(index); parentEnds.Push(index + descendants); }
		}
		foreach (StreamingCpuSample sample in samples) sample.selfMilliseconds = Math.Max(0, sample.selfMilliseconds);
		var bySelf = new List<StreamingCpuSample>(samples);
		bySelf.Sort((a, b) => b.selfMilliseconds.CompareTo(a.selfMilliseconds));
		samples.Sort((a, b) => b.inclusiveMilliseconds.CompareTo(a.inclusiveMilliseconds));
		if (samples.Count > 40) samples.RemoveRange(40, samples.Count - 40);
		if (bySelf.Count > 40) bySelf.RemoveRange(40, bySelf.Count - 40);
		return new StreamingCpuThread { name = raw.threadName, group = raw.threadGroupName, index = raw.threadIndex,
			frameMilliseconds = raw.frameTimeMs, samples = raw.sampleCount, topInclusive = samples.ToArray(), topSelf = bySelf.ToArray() };
	}

	[Serializable] sealed class StreamingHierarchyCase { public string action; public int callbacks; public double elapsedMilliseconds; }
	[Serializable] sealed class StreamingHierarchyReport
	{
		public string id, finishedUtc, message;
		public string scope = "Temporary objects in an owned scene with Play Mode temporarily paused to isolate runtime churn, then restored. Each mutation waits 0.4 seconds for deferred Editor hierarchy callbacks. The no-op control exposes background Editor activity. No asset changes.";
		public string[] loadedNetworkManagers;
		public bool completed, pauseStateRestored;
		public StreamingHierarchyCase[] cases;
	}
	sealed class StreamingHierarchyProbe
	{
		public GameObject parentA, parentB, child;
		public MeshRenderer renderer;
		public UnityEngine.UI.LayoutElement behaviour;
		public UnityEngine.UI.Text textWithShadow;
		public Canvas canvas;
		public MeshCollider meshCollider;
		public Mesh colliderMesh;
		public StreamingHierarchyReport report;
		public List<StreamingHierarchyCase> cases = new();
		public int phase = -1, callbacks;
		public double phaseStarted;
		public bool originalPaused;
	}
	static StreamingHierarchyProbe streamingHierarchyProbe;
	static void CountStreamingHierarchyCallback() { if (streamingHierarchyProbe != null) streamingHierarchyProbe.callbacks++; }
	static void BeginStreamingHierarchyProbe(Request request)
	{
		if (streamingHierarchyProbe != null || streamingPerformance != null || !EditorApplication.isPlaying ||
			EditorApplication.isPaused || !ReadGameplayMetrics().ownedSession)
			throw new InvalidOperationException("Hierarchy probe requires an idle, owned and unpaused gameplay session.");
		var names = new List<string>();
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type type = assembly.GetType("Unity.Netcode.NetworkManager", false);
			if (type == null) continue;
			foreach (UnityEngine.Object instance in Resources.FindObjectsOfTypeAll(type))
			{
				var component = instance as Component;
				if (component) names.Add(component.name + "; scene=" + component.gameObject.scene.path +
					"; root=" + component.transform.root.name + "; immediateChildren=" + component.transform.childCount);
			}
		}
		streamingHierarchyProbe = new StreamingHierarchyProbe {
			parentA = new GameObject("Streaming hierarchy probe A"), parentB = new GameObject("Streaming hierarchy probe B"),
			child = new GameObject("Streaming hierarchy probe child"), phaseStarted = EditorApplication.timeSinceStartup,
			report = new StreamingHierarchyReport { id = request.id, loadedNetworkManagers = names.ToArray() }
		};
		streamingHierarchyProbe.child.transform.SetParent(streamingHierarchyProbe.parentA.transform, false);
		try
		{
			streamingHierarchyProbe.renderer = streamingHierarchyProbe.child.AddComponent<MeshRenderer>();
			streamingHierarchyProbe.behaviour = streamingHierarchyProbe.child.AddComponent<UnityEngine.UI.LayoutElement>();
			streamingHierarchyProbe.canvas = streamingHierarchyProbe.parentA.AddComponent<Canvas>();
			streamingHierarchyProbe.canvas.renderMode = RenderMode.WorldSpace;
			var label = new GameObject("Streaming hierarchy probe label", typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Text), typeof(UnityEngine.UI.Shadow));
			label.transform.SetParent(streamingHierarchyProbe.parentA.transform, false);
			streamingHierarchyProbe.textWithShadow = label.GetComponent<UnityEngine.UI.Text>();
			streamingHierarchyProbe.meshCollider = streamingHierarchyProbe.child.AddComponent<MeshCollider>();
			streamingHierarchyProbe.colliderMesh = new Mesh { name = "Temporary hierarchy probe triangle" };
			streamingHierarchyProbe.colliderMesh.vertices = new[] { Vector3.zero, Vector3.forward, Vector3.right };
			streamingHierarchyProbe.colliderMesh.triangles = new[] { 0, 1, 2 };
			streamingHierarchyProbe.meshCollider.sharedMesh = streamingHierarchyProbe.colliderMesh;
			if (!streamingHierarchyProbe.renderer || !streamingHierarchyProbe.behaviour || !streamingHierarchyProbe.textWithShadow || !streamingHierarchyProbe.canvas || !streamingHierarchyProbe.meshCollider)
				throw new InvalidOperationException("Temporary hierarchy probe components could not be created.");
		}
		catch { FinishStreamingHierarchyProbe(false); throw; }
		streamingHierarchyProbe.originalPaused = EditorApplication.isPaused;
		EditorApplication.isPaused = true;
		EditorApplication.hierarchyChanged += CountStreamingHierarchyCallback;
		EditorApplication.update += PollStreamingHierarchyProbe;
		AssemblyReloadEvents.beforeAssemblyReload += AbortStreamingHierarchyProbe;
	}
	static void AbortStreamingHierarchyProbe() { FinishStreamingHierarchyProbe(false); }
	static void PollStreamingHierarchyProbe()
	{
		StreamingHierarchyProbe probe = streamingHierarchyProbe;
		if (probe == null) return;
		if (!EditorApplication.isPlaying || !EditorApplication.isPaused || !probe.child) { FinishStreamingHierarchyProbe(false); return; }
		if (EditorApplication.timeSinceStartup - probe.phaseStarted < .4) return;
		string[] actions = { "no-op control", "SetActive(false)", "SetActive(true)", "SetParent(same parent)", "SetParent(other parent)", "SetParent(original parent)",
			"SetActive(true) unchanged", "MeshRenderer.enabled=false", "MeshRenderer.enabled=true", "Renderer.forceRenderingOff=true", "Renderer.forceRenderingOff=false",
			"LayoutElement.enabled=false", "LayoutElement.enabled=true", "Text.enabled=false (Shadow attached)", "Text.enabled=true (Shadow attached)",
			"Canvas.enabled=false", "Canvas.enabled=true", "MeshCollider.enabled=false", "MeshCollider.enabled=true" };
		if (probe.phase >= 0) probe.cases.Add(new StreamingHierarchyCase { action = actions[probe.phase], callbacks = probe.callbacks,
			elapsedMilliseconds = (EditorApplication.timeSinceStartup - probe.phaseStarted) * 1000 });
		probe.phase++;
		if (probe.phase == actions.Length) { FinishStreamingHierarchyProbe(true); return; }
		probe.callbacks = 0; probe.phaseStarted = EditorApplication.timeSinceStartup;
		switch (probe.phase)
		{
			case 1: probe.child.SetActive(false); break;
			case 2: probe.child.SetActive(true); break;
			case 3: probe.child.transform.SetParent(probe.parentA.transform, false); break;
			case 4: probe.child.transform.SetParent(probe.parentB.transform, false); break;
			case 5: probe.child.transform.SetParent(probe.parentA.transform, false); break;
			case 6: probe.child.SetActive(true); break;
			case 7: probe.renderer.enabled = false; break;
			case 8: probe.renderer.enabled = true; break;
			case 9: probe.renderer.forceRenderingOff = true; break;
			case 10: probe.renderer.forceRenderingOff = false; break;
			case 11: probe.behaviour.enabled = false; break;
			case 12: probe.behaviour.enabled = true; break;
			case 13: probe.textWithShadow.enabled = false; break;
			case 14: probe.textWithShadow.enabled = true; break;
			case 15: probe.canvas.enabled = false; break;
			case 16: probe.canvas.enabled = true; break;
			case 17: probe.meshCollider.enabled = false; break;
			case 18: probe.meshCollider.enabled = true; break;
		}
	}
	static void FinishStreamingHierarchyProbe(bool completed)
	{
		StreamingHierarchyProbe probe = streamingHierarchyProbe;
		if (probe == null) return;
		streamingHierarchyProbe = null;
		EditorApplication.hierarchyChanged -= CountStreamingHierarchyCallback;
		EditorApplication.update -= PollStreamingHierarchyProbe;
		AssemblyReloadEvents.beforeAssemblyReload -= AbortStreamingHierarchyProbe;
		if (probe.child) UnityEngine.Object.DestroyImmediate(probe.child);
		if (probe.parentA) UnityEngine.Object.DestroyImmediate(probe.parentA);
		if (probe.parentB) UnityEngine.Object.DestroyImmediate(probe.parentB);
		if (probe.colliderMesh) UnityEngine.Object.DestroyImmediate(probe.colliderMesh);
		if (EditorApplication.isPlaying) EditorApplication.isPaused = probe.originalPaused;
		probe.report.pauseStateRestored = !EditorApplication.isPlaying || EditorApplication.isPaused == probe.originalPaused;
		probe.report.completed = completed; probe.report.finishedUtc = DateTime.UtcNow.ToString("O");
		probe.report.message = completed ? "Temporary runtime objects removed after all callback cases." : "Interrupted; temporary runtime objects removed.";
		probe.report.cases = probe.cases.ToArray();
		Directory.CreateDirectory(ArtifactDirectory);
		string path = Path.Combine(ArtifactDirectory, "StreamingHierarchy-" + probe.report.id + ".json");
		File.WriteAllText(path + ".pending", JsonUtility.ToJson(probe.report, true));
		File.Move(path + ".pending", path);
	}

	[Serializable] sealed class StreamingCoveragePoint
	{
		public Vector2 viewport;
		public Vector3 localGround;
		public int chunkX, chunkZ, chunkIndex = -1;
		public bool intersectsGround, insideWorld, intentionalOceanOmission, desired, active, geometryReady;
	}
	[Serializable] sealed class StreamingCoverageReport
	{
		public string timestamp, scope = "627 evenly spaced viewport rays (33x19), including exact edges/corners, on the grid base-ground plane. Captured after readiness, outside FPS measurements. Intentional global-ocean omissions are separated from missing terrain. Only the first32 failures are retained. A finite base-ground sample is evidence, not proof of all visible elevated terrain coverage.";
		public CameraMetrics camera;
		public int desiredCount, activeCount, chunkColumns, chunkRows, desiredMinX, desiredMaxX, desiredMinZ, desiredMaxZ;
		public int viewportSamples = 627, groundIntersections, outsideWorldSamples, intentionalOceanSamples;
		public int terrainSamples, samplesWithoutDesired, desiredSamplesWithoutReadyGeometry;
		public int cityLabelPoolCount, cityLabelsEnabled, cityLabelsActive, cityLabelsVisible;
		public bool cityCanvasEnabled, cityCanvasActive;
		public string[] visibleCityLabelExamples;
		public StreamingCoveragePoint[] failedPoints;
	}
	static string WriteStreamingCoverage(Camera camera, HexGrid grid, string path)
	{
		HashSet<int> desired = ReadField<HashSet<int>>(grid, "desiredChunkIndices");
		HashSet<int> active = ReadField<HashSet<int>>(grid, "activeChunkIndices");
		HexGridChunk[] chunks = ReadField<HexGridChunk[]>(grid, "chunks");
		bool globalOcean = ReadField<bool>(grid, "globalOceanMode", false);
		var isOcean = (Func<int, bool>)typeof(HexGrid).GetMethod("IsPureOceanChunk", BindingFlags.NonPublic | BindingFlags.Instance)
			.CreateDelegate(typeof(Func<int, bool>), grid);
		int columns = grid.CellCountX / HexMetrics.chunkSizeX, rows = grid.CellCountZ / HexMetrics.chunkSizeZ;
		var report = new StreamingCoverageReport { timestamp = DateTime.UtcNow.ToString("O"), desiredCount = desired.Count,
			activeCount = active.Count, chunkColumns = columns, chunkRows = rows, desiredMinX = columns, desiredMinZ = rows,
			desiredMaxX = -1, desiredMaxZ = -1,
			camera = ReadActualCameraMetrics(camera, FindGameplayComponent<HexMapCamera>().transform.position, HexMapCamera.CurrentStickDistance) };
		HexCityLayer cityLayer = FindGameplayComponent<HexCityLayer>();
		var labels = cityLayer ? ReadField<List<UnityEngine.UI.Text>>(cityLayer, "cityNameLabels") : null;
		Canvas cityCanvas = cityLayer ? ReadField<Canvas>(cityLayer, "cityNameCanvas") : null;
		report.cityCanvasEnabled = cityCanvas && cityCanvas.enabled;
		report.cityCanvasActive = cityCanvas && cityCanvas.gameObject.activeInHierarchy;
		var labelExamples = new List<string>(8);
		if (labels != null)
		{
			report.cityLabelPoolCount = labels.Count;
			foreach (UnityEngine.UI.Text label in labels)
			{
				if (!label) continue;
				if (label.enabled) report.cityLabelsEnabled++;
				if (label.gameObject.activeInHierarchy) report.cityLabelsActive++;
				if (label.enabled && label.gameObject.activeInHierarchy && report.cityCanvasEnabled && report.cityCanvasActive)
				{
					report.cityLabelsVisible++;
					if (labelExamples.Count < 8) labelExamples.Add(label.text);
				}
			}
		}
		report.visibleCityLabelExamples = labelExamples.ToArray();
		foreach (int index in desired)
		{
			report.desiredMinX = Math.Min(report.desiredMinX, index % columns); report.desiredMaxX = Math.Max(report.desiredMaxX, index % columns);
			report.desiredMinZ = Math.Min(report.desiredMinZ, index / columns); report.desiredMaxZ = Math.Max(report.desiredMaxZ, index / columns);
		}
		var failures = new List<StreamingCoveragePoint>(32);
		for (int y = 0; y < 19; y++) for (int x = 0; x < 33; x++)
		{
			var point = new StreamingCoveragePoint { viewport = new Vector2(x / 32f, y / 18f) };
			Ray ray = camera.ViewportPointToRay(new Vector3(point.viewport.x, point.viewport.y));
			if (Mathf.Abs(ray.direction.y) <= .0001f) continue;
			float distance = (grid.transform.position.y - ray.origin.y) / ray.direction.y;
			if (distance <= 0) continue;
			report.groundIntersections++;
			point.intersectsGround = true; point.localGround = grid.transform.InverseTransformPoint(ray.GetPoint(distance));
			HexCoordinates coord = HexCoordinates.FromPosition(point.localGround);
			point.chunkX = Mathf.FloorToInt((coord.X + coord.Z / 2) / (float)HexMetrics.chunkSizeX);
			point.chunkZ = Mathf.FloorToInt(coord.Z / (float)HexMetrics.chunkSizeZ);
			if (grid.Wrapping) point.chunkX = ((point.chunkX % columns) + columns) % columns;
			point.insideWorld = point.chunkX >= 0 && point.chunkX < columns && point.chunkZ >= 0 && point.chunkZ < rows;
			if (!point.insideWorld) { report.outsideWorldSamples++; continue; }
			point.chunkIndex = point.chunkX + point.chunkZ * columns;
			point.desired = desired.Contains(point.chunkIndex); point.active = active.Contains(point.chunkIndex);
			point.geometryReady = chunks[point.chunkIndex] && chunks[point.chunkIndex].HasReadyGeometry;
			point.intentionalOceanOmission = !point.desired && globalOcean && isOcean(point.chunkIndex);
			if (point.intentionalOceanOmission) { report.intentionalOceanSamples++; continue; }
			report.terrainSamples++;
			if (!point.desired) report.samplesWithoutDesired++;
			else if (!point.active || !point.geometryReady) report.desiredSamplesWithoutReadyGeometry++;
			else continue;
			if (failures.Count < 32) failures.Add(point);
		}
		report.failedPoints = failures.ToArray();
		File.WriteAllText(path, JsonUtility.ToJson(report, true));
		return path;
	}
}
