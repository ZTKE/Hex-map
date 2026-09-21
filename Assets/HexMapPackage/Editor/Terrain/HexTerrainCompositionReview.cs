using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fixed batch review of the shared terrain in the disposable study and Game_2.
/// Uses the existing preview bridge's owned play sessions and settings protection.
/// Run with -batchmode -force-d3d11 -executeMethod HexTerrainCompositionReview.Run
/// (without -quit or -nographics). Completion is written once; no external polling
/// of Unity compilation, import or scene loading is necessary.
/// </summary>
[InitializeOnLoad]
public static class HexTerrainCompositionReview
{
    const string Key = "HexTerrainCompositionReview.State";
    static readonly string Root = Directory.GetParent(Application.dataPath).FullName;
    static readonly string Output = Path.Combine(Root, "Artifacts", "TerrainComposition", "Review");
    static readonly string Requests = Path.Combine(Root, "Temp", "Civ6NearPreview");

    [Serializable] sealed class Step
    {
        public string action, view;
        public Step(string action, string view = null) { this.action = action; this.view = view; }
    }
    static readonly Step[] Steps = {
        new("play"), new("beauty-validate"),
        new("capture", "mountains"), new("capture", "desert"), new("capture", "snow"),
        new("capture", "coast"), new("capture", "coast"), new("stop"),
        new("gameplay-play", "coast"), new("gameplay-capture"),
        new("gameplay-view", "alps"), new("gameplay-capture"),
        new("gameplay-view", "sahara"), new("gameplay-capture"), new("stop")
    };
    [Serializable] sealed class State
    {
        public string id, pending, pendingAction, gameplayId;
        public int step, cleanupAttempt;
        public double deadline, earliest;
        public bool cleanup, exitEditor;
        public bool mountainModesValidated;
        public List<string> errors = new(), screenshots = new(), responses = new();
    }
    [Serializable] sealed class QueuedRequest { public string id, action; }
    [Serializable] sealed class Response
    {
        public string id, message, screenshot;
        public bool ok;
        public string[] shaderMessages, recentErrors;
    }
    [Serializable] sealed class GameplaySession
    {
        public string stage, message;
        public bool settingsUnchanged, originalScenesRestored, active;
    }
    [Serializable] sealed class Report
    {
        public string id, timestamp, graphicsDevice;
        public bool completed, passed;
        public string[] errors, screenshots, responses;
    }
    [Serializable] sealed class TopologyReport
    {
        public string scene, timestamp;
        public bool passed;
        public int cells, land, coast, ocean, mismatches;
        public int mountainCells, directedRidgeEdges, ridgeMismatches;
        public int desertCactusInstances;
    }

    static HexTerrainCompositionReview() { EditorApplication.update += Tick; }

    public static void Run()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("Run this fixed review in its own batchmode editor.");
        Begin(true);
    }

    [MenuItem("Tools/Hex Map/Near Terrain/Review Mountain And Coast Composition")]
    public static void RunInEditor() => Begin(false);

    static void Begin(bool exitEditor)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode ||
            !string.IsNullOrEmpty(SessionState.GetString(Key, "")))
            throw new InvalidOperationException("A review or Play session is already active.");
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(Requests);
        HexDesertVegetationImporter.Build();
        var state = new State { id = "composition-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), exitEditor = exitEditor,
            deadline = EditorApplication.timeSinceStartup + 900 };
        Save(state);
        Debug.Log("Terrain composition review started: " + state.id);
    }

    static void Save(State state) => SessionState.SetString(Key, JsonUtility.ToJson(state));
    static GameplaySession Session()
    {
        string json = SessionState.GetString("HexNearTerrainTools.Gameplay", "");
        return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<GameplaySession>(json);
    }

    static void Tick()
    {
        string json = SessionState.GetString(Key, "");
        if (string.IsNullOrEmpty(json))
        {
            // Fixed-purpose opt-in, so a background editor can accept the same
            // review as the menu. No paths, scripts or arbitrary actions enter it.
            string start = Path.Combine(Requests, "composition-review.request");
            if (!File.Exists(start) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                string command = File.ReadAllText(start);
                // Consume before Begin so a rejected startup cannot replay later.
                File.Delete(start);
                if (command.Trim() != "run") throw new InvalidOperationException("Expected the fixed 'run' request.");
                Begin(false);
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(Output);
                File.WriteAllText(Path.Combine(Output, "start-error.json"), JsonUtility.ToJson(new Report {
                    completed = true, passed = false, timestamp = DateTime.UtcNow.ToString("O"),
                    errors = new[] { exception.Message } }, true));
            }
            return;
        }
        State state = JsonUtility.FromJson<State>(json);
        try
        {
            double now = EditorApplication.timeSinceStartup;
            if (now > state.deadline) throw new TimeoutException("Review step timed out: " + state.step);
            if (!string.IsNullOrEmpty(state.pending))
            {
                string path = Path.Combine(Requests, "response_" + state.pending + ".json");
                if (!File.Exists(path)) return;
                string text = File.ReadAllText(path);
                Response response = JsonUtility.FromJson<Response>(text);
                if (response.id != state.pending) return;
                string archived = Path.Combine(Output, state.pending + ".json");
                File.WriteAllText(archived, text);
                state.responses.Add(archived);
                if (!response.ok) state.errors.Add(state.pending + ": " + response.message);
                if (!string.IsNullOrEmpty(response.screenshot)) state.screenshots.Add(response.screenshot);
                foreach (string error in response.recentErrors ?? Array.Empty<string>())
                    if (!state.errors.Contains(error)) state.errors.Add(error);
                foreach (string diagnostic in response.shaderMessages ?? Array.Empty<string>())
                    if (diagnostic.StartsWith("Error:", StringComparison.Ordinal) && !state.errors.Contains(diagnostic))
                        state.errors.Add(diagnostic);
                state.pending = state.pendingAction = null;
                state.earliest = now + 3;
                if (!state.cleanup) state.deadline = now + 900;
                state.step++;
                Save(state);
                if (!response.ok && !state.cleanup && Steps[state.step - 1].action != "beauty-validate")
                    throw new InvalidOperationException(response.message);
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                ShaderUtil.anythingCompiling || now < state.earliest) return;
            if (state.cleanup || state.step >= Steps.Length)
            {
                if (RestorationPending())
                {
                    // A request that occupied the queue may have finished since
                    // cleanup started. Retry once it clears, using a fresh ID.
                    state.cleanup = true;
                    Save(state);
                    Send(state, new Step("stop"));
                    return;
                }
                GameplaySession finalSession = Session();
                if (!string.IsNullOrEmpty(state.gameplayId) && (finalSession == null ||
                    !finalSession.settingsUnchanged || !finalSession.originalScenesRestored))
                    state.errors.Add("Gameplay settings or original scene restoration failed.");
                Finish(state);
                return;
            }
            Step step = Steps[state.step];
            if (step.action == "play" || step.action == "gameplay-play")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            }
            else if (step.action == "beauty-validate" || step.action == "capture")
            {
                var showcase = UnityEngine.Object.FindObjectOfType<HexNearTerrainShowcase>();
                if (!EditorApplication.isPlaying || !showcase || !showcase.Ready) return;
            }
            else if (step.action.StartsWith("gameplay-", StringComparison.Ordinal))
            {
                GameplaySession session = Session();
                if (session != null && session.stage == "failed")
                    throw new InvalidOperationException(session.message);
                if (session == null || session.stage != "running") return;
            }
            if (step.action == "beauty-validate" && !state.mountainModesValidated)
            {
                var showcase = UnityEngine.Object.FindObjectOfType<HexNearTerrainShowcase>();
                string modesPath = Path.Combine(Output, state.id + "-mountain-modes.json");
                HexMountainModeValidation.Run(showcase, modesPath, out bool modesPassed, out string modesSummary);
                state.responses.Add(modesPath);
                if (!modesPassed) state.errors.Add(modesSummary);
                state.mountainModesValidated = true;
                state.earliest = now + 3;
                Save(state);
                return;
            }
            if (step.action == "beauty-validate" || step.action == "gameplay-capture") ValidateTopology(state);
            Send(state, step);
        }
        catch (Exception exception)
        {
            string error = exception.ToString();
            if (!state.errors.Contains(error)) state.errors.Add(error);
            if (exception is TimeoutException)
            {
                try { RemoveOwnedTimedOutRequest(state); }
                catch (Exception cleanupError)
                {
                    state.errors.Add(cleanupError.Message);
                    state.cleanup = true;
                    state.deadline = EditorApplication.timeSinceStartup + 180;
                    Save(state); // Keep pending ownership until cancellation succeeds.
                    return;
                }
            }
            state.pending = state.pendingAction = null;
            if (state.cleanup) { Finish(state); return; }
            state.cleanup = true;
            state.deadline = EditorApplication.timeSinceStartup + 180;
            Save(state);
            if (!File.Exists(Path.Combine(Requests, "request.json"))) Send(state, new Step("stop"));
        }
    }

    static bool RestorationPending() => EditorApplication.isPlayingOrWillChangePlaymode ||
        Session()?.active == true ||
        !string.IsNullOrEmpty(SessionState.GetString("HexNearTerrainTools.OriginalScenes", ""));

    static void RemoveOwnedTimedOutRequest(State state)
    {
        string path = Path.Combine(Requests, "request.json");
        if (string.IsNullOrEmpty(state.pending) || string.IsNullOrEmpty(state.pendingAction) ||
            !state.pending.StartsWith(state.id + "-", StringComparison.Ordinal) || !File.Exists(path)) return;
        QueuedRequest queued = JsonUtility.FromJson<QueuedRequest>(File.ReadAllText(path));
        // Never delete another sender's request, even when it blocks our cleanup.
        if (queued != null && queued.id == state.pending && queued.action == state.pendingAction)
            File.Delete(path);
    }

    static void ValidateTopology(State state)
    {
        var grid = UnityEngine.Object.FindObjectOfType<HexGrid>();
        var texture = Shader.GetGlobalTexture("_HexWaterTopologyData") as Texture2D;
        var ridges = Shader.GetGlobalTexture("_HexMountainRidgeData") as Texture2D;
        if (!grid || !texture) throw new InvalidOperationException("Water topology texture or grid is missing.");
        if (!ridges) throw new InvalidOperationException("Mountain ridge topology texture is missing.");
        var bytes = texture.GetRawTextureData<byte>();
        var ridgeBytes = ridges.GetRawTextureData<byte>();
        if (texture.width != grid.CellCountX || texture.height != grid.CellCountZ ||
            texture.format != TextureFormat.R8 || bytes.Length != grid.CellData.Length)
            throw new InvalidOperationException("Water topology dimensions/format disagree with grid.");
        if (ridges.width != grid.CellCountX || ridges.height != grid.CellCountZ ||
            ridges.format != TextureFormat.R8 || ridgeBytes.Length != grid.CellData.Length)
            throw new InvalidOperationException("Mountain ridge topology dimensions/format disagree with grid.");
        var report = new TopologyReport { cells = grid.CellData.Length, scene = grid.gameObject.scene.path,
            timestamp = DateTime.UtcNow.ToString("O") };
        foreach (var vegetation in UnityEngine.Object.FindObjectsOfType<HexNearVegetationMesh>(true))
            report.desertCactusInstances += vegetation.DesertInstanceCount;
        for (int index = 0; index < grid.CellData.Length; index++)
        {
            HexCellData cell = grid.CellData[index];
            byte expected = 255;
            if (cell.IsUnderwater)
            {
                expected = 0;
                for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
                    if (grid.TryGetCellIndex(cell.coordinates.Step(d), out int adjacent) &&
                        !grid.CellData[adjacent].IsUnderwater) { expected = 128; break; }
            }
            if (expected == 255) report.land++;
            else if (expected == 128) report.coast++;
            else report.ocean++;
            if (bytes[index] != expected) report.mismatches++;
            int expectedRidge = HexMountainRidgeGraph.ComputeMask(grid, index % grid.CellCountX, index / grid.CellCountX) | ((int)cell.mountainMode << 6);
            if (ridgeBytes[index] != expectedRidge) report.ridgeMismatches++;
            if (!cell.IsUnderwater && cell.landform == HexLandform.Mountain) report.mountainCells++;
            for (int d = 0; d < 6; d++) report.directedRidgeEdges += (ridgeBytes[index] >> d) & 1;
        }
        report.passed = report.mismatches == 0 && report.ridgeMismatches == 0 &&
            report.coast > 0 && report.ocean > 0 && report.mountainCells > 0 && report.directedRidgeEdges > 0;
        if (grid.gameObject.scene.name == HexNearTerrainShowcase.SceneName && report.desertCactusInstances == 0)
            report.passed = false;
        string path = Path.Combine(Output, state.id + "-topology-" + state.step + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
        state.responses.Add(path);
        if (!report.passed) state.errors.Add("Water or mountain ridge topology validation failed: " + path);
        Save(state);
    }

    static void Send(State state, Step step)
    {
        string requestPath = Path.Combine(Requests, "request.json");
        if (File.Exists(requestPath)) return;
        state.pending = state.id + "-" + (state.cleanup ? "cleanup-" + (++state.cleanupAttempt) : state.step.ToString("D2"));
        state.pendingAction = step.action;
        if (step.action == "gameplay-play") state.gameplayId = state.pending;
        string pendingPath = Path.Combine(Requests, state.pending + ".pending");
        string request = "{\"id\":\"" + state.pending + "\",\"action\":\"" + step.action +
            "\",\"view\":\"" + (step.view ?? "") + "\"}";
        File.WriteAllText(pendingPath, request);
        Save(state);
        File.Move(pendingPath, requestPath);
    }

    static void Finish(State state)
    {
        bool completed = !RestorationPending();
        const string incomplete = "Review failed; owned Play or original scene restoration is still pending. Cleanup will retry.";
        if (!completed && !state.errors.Contains(incomplete)) state.errors.Add(incomplete);
        if (completed) foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/HexMapPackage" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (!shader) continue;
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    state.errors.Add(path + ":" + message.line + " " + message.message);
        }
        var report = new Report { id = state.id, completed = completed, passed = completed && state.errors.Count == 0,
            timestamp = DateTime.UtcNow.ToString("O"), graphicsDevice = SystemInfo.graphicsDeviceName,
            errors = state.errors.ToArray(), screenshots = state.screenshots.ToArray(), responses = state.responses.ToArray() };
        File.WriteAllText(Path.Combine(Output, state.id + ".json"), JsonUtility.ToJson(report, true));
        if (!completed)
        {
            // Keep cleanup alive and leave a visible incomplete report. Never
            // erase ownership or exit the Editor while restoration is pending.
            state.deadline = EditorApplication.timeSinceStartup + 180;
            state.earliest = EditorApplication.timeSinceStartup + 3;
            Save(state);
            return;
        }
        SessionState.EraseString(Key);
        Debug.Log("Terrain composition review completed: " + report.passed);
        if (state.exitEditor) EditorApplication.Exit(report.passed ? 0 : 1);
    }
}
