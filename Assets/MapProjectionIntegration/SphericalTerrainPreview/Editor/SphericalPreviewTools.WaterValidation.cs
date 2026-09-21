using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        const string ShoreWaveShaderName = "WW2/Spherical Terrain Preview/Shore Wave";
        const string WaterShaderName = "WW2/Spherical Terrain Preview/Water";
        const int WaterFrameWidth = 1280, WaterFrameHeight = 720;
        const double WaterRecordingSeconds = 12, WaterRecordingInterval = 1.0 / 6;
        static WaterReviewReport waterReview;
        static WaterRecording activeWaterRecording;

        [Serializable] sealed class WaterReviewReport
        {
            public string id, status, message, startedUtc, finishedUtc, validationReport;
            public string visualAcceptance = "Automated checks verify actual geometry, bindings, shader errors, camera stability and real elapsed frame capture. Visual quality and flow direction still require inspection of the recorded images/video.";
            public List<WaterViewEvidence> views = new();
            public List<WaterTextureImportEvidence> sourceTextureImports = new();
            public List<string> recordings = new();
            public List<Check> checks = new();
            public string[] errors;
        }
        [Serializable] sealed class WaterViewEvidence
        {
            public string name;
            public int loadedCrestMeshes, frustumCrestMeshes, frustumCrestVertices, frustumRiverMeshes, shaderErrors;
            public int visibleSeaSamples, visibleLandSamples, visibleDesertSamples;
            public List<WaterMaterialEvidence> materials = new();
            public List<string> errors = new();
            public string visibilityNote = "Active renderer bounds intersect the presentation camera frustum; this is not a pixel-level visibility or occlusion test.";
        }
        [Serializable] sealed class WaterMaterialEvidence
        {
            public string name, shader, crestAtlas, waveAux, riverWaveMoments;
            public bool supported;
            public float waterKind, surfaceLod, sourceRiverWaves, riverScrollSpeed, riverBumpStrength, crestOpacity;
            public Vector4 riverMotion;
        }
        [Serializable] sealed class WaterTextureImportEvidence
        {
            public string asset, guid, format, graphicsFormat, compression, filter, wrapU, wrapV;
            public bool available, expectedSrgb, importerSrgb, gpuSrgb, mipmaps, alphaIsTransparency, passed;
        }
        [Serializable] sealed class WaterRecording
        {
            public string id, view, status, message, startedUtc, finishedUtc, directory;
            public int width = WaterFrameWidth, height = WaterFrameHeight, jpegQuality = 88;
            public double requestedSeconds = WaterRecordingSeconds, targetFramesPerSecond = 6;
            public double wallSeconds, capturedSpanSeconds, maximumSampleGapSeconds, measuredFramesPerSecond;
            public float unityTimeAdvance, maximumFocusDriftDegrees, maximumAltitudeDrift;
            public int distinctImageHashes, missedSampleSlots;
            public float initialTimeScale;
            public int initialCaptureFramerate;
            public string provenance = "Each JPEG is a fresh URP render request from the running presentation camera, captured at most once per actual Unity frame. Time.time, timeScale and captureFramerate are never advanced or overridden. Slow frames are skipped, not synthesized. ElapsedSeconds is the real editor monotonic clock; encode using these timestamps, not an assumed constant frame rate.";
            public List<WaterFrame> frames = new();
        }
        [Serializable] sealed class WaterFrame
        {
            public string file, sha256, utc;
            public int frameCount, bytes;
            public float unityTime, unityUnscaledTime, timeScale;
            public double elapsedSeconds, captureWallSeconds;
        }

        static string WaterReviewDirectory(string id)
            => Path.Combine(Artifacts, "WaterRevision20260913", id);

        static IEnumerator WaterReviewSteps()
        {
            ValidationSession session = validation;
            waterReview = new WaterReviewReport
            {
                id = session.request.id, status = "running", startedUtc = DateTime.UtcNow.ToString("O"),
                validationReport = Path.Combine(Artifacts, "Validation-" + session.request.id + ".json")
            };
            SaveWaterReview();
            InspectWaterSourceImports();
            session.terrain.SetGridVisible(false);
            if (session.navigation.hud) session.navigation.hud.GridVisible = false;

            session.navigation.SetView(47.8f, 43f, 20f, true);
            yield return ObserveView("water-user-coast");
            InspectWaterView("water-user-coast", true, false);
            yield return RecordWaterMotion("water-user-coast");

            session.navigation.SetView(6.1f, 43.3f, 35f, true);
            yield return ObserveView("water-west-coast");
            InspectWaterView("water-west-coast", true, false);

            // True shared edge of R5 desert cell 215747 and water cell 215746.
            session.navigation.SetView(36.678532f, 25.756277f, 28f, true);
            yield return ObserveView("water-sand-coast");
            InspectWaterView("water-sand-coast", true, false);

            session.navigation.ApplyPreset("coast", true);
            yield return ObserveView("water-nordic-coast");
            InspectWaterView("water-nordic-coast", true, false);

            session.navigation.ApplyPreset("river", true);
            yield return ObserveView("water-river");
            InspectWaterView("water-river", false, true);
            yield return RecordWaterMotion("water-river");

            session.navigation.ApplyPreset("plateau-river", true);
            yield return ObserveView("water-plateau-river");
            InspectWaterView("water-plateau-river", false, true);

            AddCheck("Water review has no Unity runtime errors", Errors.Count == 0, Errors.Count, 0,
                "Errors collected by the existing preview log handler; shader compilation diagnostics are also checked for every bound water shader.");
            SaveWaterReview();
        }

        static void InspectWaterSourceImports()
        {
            foreach (string name in new[] { "WaveAtlas", "TER_Wave_Noise", "TER_Coast_H", "TER_Cliff_H", "TER_Coast_B", "TER_Cliff_B" })
            {
                string path = AssetRoot + "/Runtime/Resources/SphericalTerrainPreview/Water/" + name + ".png";
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
                bool srgb = name.EndsWith("_B", StringComparison.Ordinal);
                TextureWrapMode wrap = name == "WaveAtlas" ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                WaterTextureImportEvidence result = new() { asset = path, expectedSrgb = srgb, available = texture && importer };
                if (result.available)
                {
                    result.guid = AssetDatabase.AssetPathToGUID(path);
                    result.format = texture.format.ToString(); result.graphicsFormat = texture.graphicsFormat.ToString();
                    result.importerSrgb = importer.sRGBTexture;
                    result.gpuSrgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
                    result.compression = importer.textureCompression.ToString(); result.filter = texture.filterMode.ToString();
                    result.wrapU = texture.wrapModeU.ToString(); result.wrapV = texture.wrapModeV.ToString();
                    result.mipmaps = importer.mipmapEnabled && texture.mipmapCount > 1;
                    result.alphaIsTransparency = importer.alphaIsTransparency;
                    result.passed = importer.sRGBTexture == srgb && result.gpuSrgb == srgb
                        && texture.format == TextureFormat.RGBA32 && importer.textureCompression == TextureImporterCompression.Uncompressed
                        && texture.wrapModeU == wrap && texture.wrapModeV == wrap && importer.wrapModeU == wrap && importer.wrapModeV == wrap
                        && texture.filterMode == FilterMode.Trilinear && importer.filterMode == FilterMode.Trilinear
                        && result.mipmaps && !importer.alphaIsTransparency && importer.textureType == TextureImporterType.Default;
                }
                waterReview.sourceTextureImports.Add(result);
                AddCheck(name + " actual GPU texture follows the local source sampling contract", result.passed, result.passed ? 1 : 0, 1,
                    "Expected " + (srgb ? "sRGB color" : "linear intensity/height data") + ", uncompressed RGBA32, trilinear mips, " + wrap + ". Actual: " + JsonUtility.ToJson(result));
            }
            SaveWaterReview(); SaveValidation();
        }

        static void InspectWaterView(string name, bool expectCrests, bool expectRiver)
        {
            WaterViewEvidence evidence = new() { name = name };
            // Renderer bounds can span an entire chunk even when every crest
            // lies outside the image. Check actual first surface hits as well.
            if (expectCrests)
            {
                var terrain = validation.terrain;
                var camera = validation.navigation.PresentationCamera;
                for (int y = 0; y < 9; y++) for (int x = 0; x < 17; x++)
                {
                    Ray ray = camera.ViewportPointToRay(new Vector3((x+.5f)/17,(y+.5f)/9));
                    if (!terrain.TryPick(ray,out int cell,out Vector3 hit)) continue;
                    var sample = terrain.Surface.Evaluate(hit.normalized,cell);
                    if (sample.Height < -.015f) evidence.visibleSeaSamples++;
                    else if (sample.Height > .04f)
                    { evidence.visibleLandSamples++; if (sample.BiomeWeights.x > .7f) evidence.visibleDesertSamples++; }
                }
                AddCheck(name + " visible shore crosses the image", evidence.visibleSeaSamples >= 3 && evidence.visibleLandSamples >= 3,
                    evidence.visibleSeaSamples,3,"153 first-hit camera rays: sea="+evidence.visibleSeaSamples+", land="+evidence.visibleLandSamples+". Does not assert foam appearance.");
                if (name == "water-sand-coast") AddCheck(name + " visible desert reaches the coast",evidence.visibleDesertSamples >= 3,
                    evidence.visibleDesertSamples,3,"Desert coverage above 70% at first visible dry-surface hits.");
            }
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(validation.navigation.PresentationCamera);
            HashSet<int> materials = new(); HashSet<int> shaders = new();
            bool sourceAtlasBound = false, flowingRiverBound = false;
            foreach (MeshRenderer renderer in validation.terrain.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>(); Mesh mesh = filter ? filter.sharedMesh : null;
                if (!mesh || mesh.vertexCount == 0 || mesh.subMeshCount == 0 || mesh.GetIndexCount(0) == 0) continue;
                bool inFrustum = GeometryUtility.TestPlanesAABB(planes, renderer.bounds);
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material || !material.shader) continue;
                    string shaderName = material.shader.name;
                    if (shaderName != ShoreWaveShaderName && shaderName != WaterShaderName) continue;
                    bool crest = shaderName == ShoreWaveShaderName;
                    bool river = !crest && material.GetFloat("_WaterKind") > .5f;
                    if (crest) evidence.loadedCrestMeshes++;
                    if (!inFrustum) continue;
                    if (crest) { evidence.frustumCrestMeshes++; evidence.frustumCrestVertices += mesh.vertexCount; }
                    if (river) evidence.frustumRiverMeshes++;
                    if (shaders.Add(material.shader.GetInstanceID()))
                    {
                        if (!material.shader.isSupported) evidence.errors.Add(shaderName + " is unsupported.");
                        foreach (var error in ShaderUtil.GetShaderMessages(material.shader))
                            if (error.severity.ToString() == "Error") { evidence.shaderErrors++; evidence.errors.Add(shaderName + ": " + error.message); }
                    }
                    if (!materials.Add(material.GetInstanceID())) continue;
                    WaterMaterialEvidence binding = new()
                    { name = material.name, shader = shaderName, supported = material.shader.isSupported, surfaceLod = material.GetFloat("_SurfaceLod") };
                    if (crest)
                    {
                        Texture atlas = material.GetTexture("_CrestAtlas"), aux = material.GetTexture("_WaveAux");
                        binding.crestAtlas = atlas ? AssetDatabase.GetAssetPath(atlas) : "missing";
                        binding.waveAux = aux ? AssetDatabase.GetAssetPath(aux) : "missing";
                        binding.crestOpacity = material.GetFloat("_WaveOpacity");
                        sourceAtlasBound |= binding.crestAtlas == AssetRoot + "/Runtime/Resources/SphericalTerrainPreview/Water/WaveAtlas.png"
                            && atlas.width == 1024 && atlas.height == 1024 && aux && binding.crestOpacity > 0;
                    }
                    else
                    {
                        binding.waterKind = material.GetFloat("_WaterKind");
                        binding.sourceRiverWaves = material.GetFloat("_UseRiverWaves");
                        binding.riverScrollSpeed = material.GetFloat("_Civ6RiverScrollSpeed");
                        binding.riverBumpStrength = material.GetFloat("_Civ6RiverBumpStrength");
                        binding.riverMotion = material.GetVector("_RiverMotion");
                        Texture waveMoments = material.GetTexture("_Civ6RiverWaveMoments");
                        binding.riverWaveMoments = waveMoments ? AssetDatabase.GetAssetPath(waveMoments) : "missing";
                        flowingRiverBound |= river && binding.sourceRiverWaves > .5f && binding.riverScrollSpeed > 0
                            && binding.riverMotion.x > 0 && binding.riverMotion.z > 0 && binding.riverBumpStrength > 0 && waveMoments;
                    }
                    evidence.materials.Add(binding);
                }
            }
            AddCheck(name + " bound water shaders have no errors", evidence.errors.Count == 0, evidence.errors.Count, 0,
                string.Join("; ", evidence.errors));
            if (expectCrests)
                AddCheck(name + " contains source crest meshes and atlas", evidence.frustumCrestMeshes > 0 && sourceAtlasBound,
                    evidence.frustumCrestMeshes, 1, "Nonempty actual crest meshes intersect the camera frustum, with the original 1024x1024 atlas bound and nonzero opacity. Loaded meshes=" + evidence.loadedCrestMeshes + ".");
            if (expectRiver)
                AddCheck(name + " has moving source river material", evidence.frustumRiverMeshes > 0 && flowingRiverBound,
                    evidence.frustumRiverMeshes, 1, "Actual river meshes intersect the camera frustum; source wave texture, scroll speed, flow speed and bump strength are nonzero. Video inspection establishes visible motion and direction.");
            waterReview.views.Add(evidence); SaveWaterReview(); SaveValidation();
        }

        static IEnumerator RecordWaterMotion(string name)
        {
            ValidationSession session = validation;
            WaterRecording recording = new()
            {
                id = session.request.id, view = name, status = "recording", startedUtc = DateTime.UtcNow.ToString("O"),
                directory = Path.Combine(WaterReviewDirectory(session.request.id), "Animations", name),
                initialTimeScale = Time.timeScale, initialCaptureFramerate = Time.captureFramerate
            };
            activeWaterRecording = recording;
            Directory.CreateDirectory(recording.directory);
            waterReview.recordings.Add(Path.Combine(recording.directory, "Recording.json"));
            SaveWaterRecording(recording); SaveWaterReview();
            session.report.activeStage = name + " real-time animation"; SaveValidation();
            if (!DetailReady(session.terrain, session.navigation) || !session.navigation.IsSettled || EditorApplication.isPaused || Time.timeScale <= 0)
            {
                recording.status = "unavailable"; recording.message = "The live preview was not ready, settled and advancing; no substitute frames were generated.";
                recording.finishedUtc = DateTime.UtcNow.ToString("O"); SaveWaterRecording(recording); activeWaterRecording = null;
                AddCheck(name + " real-time animation available", false, 0, 1, recording.message); yield break;
            }
            Vector3 focus = session.navigation.FocusDirection; float altitude = session.navigation.Altitude;
            double start = EditorApplication.timeSinceStartup, nextSample = start;
            int previousFrame = -1; HashSet<string> hashes = new();
            while (EditorApplication.timeSinceStartup - start < WaterRecordingSeconds)
            {
                double now = EditorApplication.timeSinceStartup;
                if (Time.frameCount == previousFrame || now < nextSample) { yield return null; continue; }
                previousFrame = Time.frameCount;
                WaterFrame frame = CaptureWaterAnimationFrame(session.terrain, session.navigation.PresentationCamera,
                    recording.directory, recording.frames.Count, now - start);
                recording.frames.Add(frame); hashes.Add(frame.sha256);
                recording.maximumFocusDriftDegrees = Mathf.Max(recording.maximumFocusDriftDegrees, Vector3.Angle(focus, session.navigation.FocusDirection));
                recording.maximumAltitudeDrift = Mathf.Max(recording.maximumAltitudeDrift, Mathf.Abs(altitude - session.navigation.Altitude));
                if (recording.frames.Count > 1)
                    recording.maximumSampleGapSeconds = Math.Max(recording.maximumSampleGapSeconds,
                        frame.elapsedSeconds - recording.frames[recording.frames.Count - 2].elapsedSeconds);
                nextSample = now + WaterRecordingInterval;
                // Persist real progress so an interrupted recording remains usable and clearly incomplete.
                recording.wallSeconds = EditorApplication.timeSinceStartup - start;
                SaveWaterRecording(recording);
                yield return null;
            }
            recording.wallSeconds = EditorApplication.timeSinceStartup - start;
            recording.finishedUtc = DateTime.UtcNow.ToString("O"); recording.distinctImageHashes = hashes.Count;
            bool advancing = recording.frames.Count > 1;
            for (int i = 1; i < recording.frames.Count; i++)
                advancing &= recording.frames[i].frameCount > recording.frames[i - 1].frameCount
                    && recording.frames[i].unityTime > recording.frames[i - 1].unityTime;
            if (recording.frames.Count > 1)
            {
                WaterFrame first = recording.frames[0], last = recording.frames[recording.frames.Count - 1];
                recording.capturedSpanSeconds = last.elapsedSeconds - first.elapsedSeconds;
                recording.unityTimeAdvance = last.unityTime - first.unityTime;
                recording.measuredFramesPerSecond = (recording.frames.Count - 1) / Math.Max(recording.capturedSpanSeconds, .001);
            }
            recording.missedSampleSlots = Math.Max(0, (int)Math.Floor(recording.wallSeconds / WaterRecordingInterval) - recording.frames.Count);
            bool duration = recording.capturedSpanSeconds >= 10 && recording.wallSeconds <= 15;
            bool stable = recording.maximumFocusDriftDegrees < .005f && recording.maximumAltitudeDrift < .005f;
            bool changed = hashes.Count > 1;
            bool cadence = recording.measuredFramesPerSecond >= 4 && recording.maximumSampleGapSeconds <= 1;
            recording.status = advancing && duration && stable && changed && cadence ? "captured" : "needs-review";
            recording.message = "Captured " + recording.frames.Count + " real frames. Distinct full-frame hashes establish image changes, not water-only motion; inspect the clip. No missing frames were interpolated.";
            AddCheck(name + " records advancing real frames for 10–15 seconds", advancing && duration,
                recording.capturedSpanSeconds, 10, "Frame count=" + recording.frames.Count + "; Time.time advance=" + recording.unityTimeAdvance + "; maximum sample gap=" + recording.maximumSampleGapSeconds + " seconds.");
            AddCheck(name + " recording camera stays fixed", stable, recording.maximumFocusDriftDegrees, .005,
                "Input remains locked by validation; altitude drift=" + recording.maximumAltitudeDrift + ".");
            AddCheck(name + " recording has sufficient real sampling cadence", cadence, recording.measuredFramesPerSecond, 4,
                "Target 6 fps; a throttled recording below 4 fps or with a gap over one second needs review. Missing samples are never replaced.");
            AddCheck(name + " recorded pixels change over time", changed, hashes.Count, 2,
                "SHA256 over independently rendered JPEG bytes. This check alone does not establish correct water appearance or direction.");
            SaveWaterRecording(recording); activeWaterRecording = null; SaveWaterReview(); SaveValidation();
        }

        static WaterFrame CaptureWaterAnimationFrame(SphericalTerrainPreview terrain, Camera camera,
            string directory, int index, double elapsed)
        {
            double before = EditorApplication.timeSinceStartup;
            WaterFrame frame = new()
            {
                file = "frame_" + index.ToString("D6") + ".jpg", elapsedSeconds = elapsed,
                frameCount = Time.frameCount, unityTime = Time.time, unityUnscaledTime = Time.unscaledTime,
                timeScale = Time.timeScale, utc = DateTime.UtcNow.ToString("O")
            };
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(WaterFrameWidth, WaterFrameHeight, 24, RenderTextureFormat.ARGB32);
            Texture2D image = null;
            try
            {
                terrain.RenderVegetation(camera);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                image = new Texture2D(WaterFrameWidth, WaterFrameHeight, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, WaterFrameWidth, WaterFrameHeight), 0, 0, false); image.Apply(false, false);
                byte[] bytes = image.EncodeToJPG(88); frame.bytes = bytes.Length;
                File.WriteAllBytes(Path.Combine(directory, frame.file), bytes);
                using SHA256 sha = SHA256.Create();
                frame.sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target);
                if (image) Object.DestroyImmediate(image);
            }
            frame.captureWallSeconds = EditorApplication.timeSinceStartup - before;
            return frame;
        }

        static void SaveWaterRecording(WaterRecording recording)
            => WriteAtomic(Path.Combine(recording.directory, "Recording.json"), JsonUtility.ToJson(recording, true));
        static void SaveWaterReview()
        {
            if (waterReview == null) return;
            string directory = WaterReviewDirectory(waterReview.id); Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "WaterReview.json"), JsonUtility.ToJson(waterReview, true));
        }
        static void FinishWaterReview(ValidationSession session, string message)
        {
            if (session.request.action != "water-review" || waterReview == null) return;
            if (activeWaterRecording != null)
            {
                activeWaterRecording.status = "interrupted"; activeWaterRecording.message = message;
                activeWaterRecording.finishedUtc = DateTime.UtcNow.ToString("O"); SaveWaterRecording(activeWaterRecording);
                activeWaterRecording = null;
            }
            waterReview.status = session.report.status; waterReview.message = message;
            waterReview.finishedUtc = DateTime.UtcNow.ToString("O"); waterReview.errors = session.report.errors;
            waterReview.checks = new List<Check>(session.report.checks); SaveWaterReview(); waterReview = null;
        }
    }
}
