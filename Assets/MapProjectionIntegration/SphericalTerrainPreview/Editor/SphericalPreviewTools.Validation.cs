using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        static ValidationSession validation;
        [Serializable] sealed class Check { public string name, detail; public bool passed; public double actual, limit; }
        [Serializable] sealed class ValidationReport
        {
            public string id, status, message, startedUtc, finishedUtc, activeStage, graphicsDevice, graphicsApi, unityVersion;
            public string gameViewResolutionRequest;
            public bool gameViewSelectionRestored;
            public double startupFirstVisibleSeconds;
            public int workerCount;
            public long peakAllocatedMemoryBytes, peakReservedMemoryBytes, peakGraphicsDriverAllocatedBytes;
            public string memoryNote = "Unity allocation counters. Graphics-driver allocation is an estimate, not exclusive process VRAM. Peaks include terrain warmup and exclude screenshot allocations where possible.";
            public List<Check> checks = new();
            public List<ViewResult> views = new();
            public List<StreamingResult> streaming = new();
            public string[] errors;
        }
        [Serializable] sealed class ViewResult
        {
            public string name, screenshot, uiScreenshot, loadingError;
            public int renderedFrames, frameSamples, screenWidth, screenHeight, detailCells, trees, riverSegments, meshVertices;
            public int pendingChunks, readyChunks, cacheHits, buildCount, workerCount;
            public bool targetTextureAssigned, detailReady, cameraSettled;
            public float longitude, latitude, altitude;
            public float expectedLongitude, expectedLatitude, expectedAltitude, maximumFocusDriftDegrees, maximumAltitudeDrift;
            public double warmupWallSeconds, sampleWallSeconds, meanFrameMs, p95FrameMs, maximumFrameMs;
            public double firstVisibleSeconds, lastLoadSeconds;
            public double cpuFrameMs = -1, gpuFrameMs = -1;
            public string timingNote = "Time.unscaledDeltaTime sampled once per actual rendered frame after readiness and warmup. Includes editor/background throttling. CPU/GPU values are -1 when no valid independent timing samples were collected.";
            public long allocatedMemoryBytes, graphicsDriverAllocatedBytes;
        }
        sealed class ValidationSession
        {
            public Request request;
            public ValidationReport report;
            public SphericalTerrainPreview terrain;
            public SphericalPreviewCamera navigation;
            public bool oldInput, oldGrid;
            public int oldSelection;
            public Vector3 oldFocus;
            public float oldAltitude;
            public GameViewSizeScope gameViewSize;
            public readonly Stack<IEnumerator> routines = new();
        }
        static void BeginValidation(Request request, SphericalTerrainPreview terrain, SphericalPreviewCamera navigation)
        {
            validation = new ValidationSession
            {
                request = request, terrain = terrain, navigation = navigation, oldInput = navigation.InputEnabled, oldSelection = terrain.SelectedCell,
                oldFocus = navigation.FocusDirection, oldAltitude = navigation.Altitude, oldGrid = navigation.hud && navigation.hud.GridVisible,
                report = new ValidationReport
                {
                    id = request.id, status = "running", startedUtc = DateTime.UtcNow.ToString("O"), graphicsDevice = SystemInfo.graphicsDeviceName,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(), unityVersion = Application.unityVersion,
                    startupFirstVisibleSeconds = terrain.FirstVisibleSeconds, workerCount = terrain.WorkerCount
                }
            };
            navigation.InputEnabled = false;
            validation.gameViewSize = GameViewSizeScope.TryFullHd();
            validation.report.gameViewResolutionRequest = validation.gameViewSize.message;
            validation.routines.Push(request.action == LandformReviewAction ? LandformReviewSteps() : request.action == "water-review" ? WaterReviewSteps() : ValidationSteps()); SaveValidation();
        }
        static void AdvanceValidation()
        {
            if (validation == null) return;
            // Unity displays a diagnostic replacement while asynchronous shader
            // variants compile. Never count or capture those as final art.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || ShaderUtil.anythingCompiling) return;
            if (!EditorApplication.isPlaying || !validation.terrain || !validation.navigation)
            { FinishValidation(false, "The isolated preview is no longer running."); return; }
            try
            {
                if (!string.IsNullOrEmpty(validation.terrain.LoadingError))
                    throw new InvalidOperationException("Terrain streaming failed: " + validation.terrain.LoadingError);
                ValidationReport report = validation.report;
                report.peakAllocatedMemoryBytes = Math.Max(report.peakAllocatedMemoryBytes, Profiler.GetTotalAllocatedMemoryLong());
                report.peakReservedMemoryBytes = Math.Max(report.peakReservedMemoryBytes, Profiler.GetTotalReservedMemoryLong());
                report.peakGraphicsDriverAllocatedBytes = Math.Max(report.peakGraphicsDriverAllocatedBytes, Profiler.GetAllocatedMemoryForGraphicsDriver());
                while (validation.routines.Count > 0)
                {
                    IEnumerator routine = validation.routines.Peek();
                    if (!routine.MoveNext()) { validation.routines.Pop(); continue; }
                    if (routine.Current is IEnumerator nested) { validation.routines.Push(nested); continue; }
                    return;
                }
                FinishValidation(true, validation.request.action == "water-review"
                    ? "Completed fixed coast and river captures, source water binding checks and real-time motion recordings. Inspect the images and videos for visual acceptance."
                    : validation.request.action == LandformReviewAction ? "Completed twelve fixed global terrain views, regional landform counts and actual visible detail checks."
                    : "Completed full-world topology checks, visible geometry checks, spherical view captures and camera round trips.");
            }
            catch (Exception exception) { FinishValidation(false, exception.ToString()); }
        }
        static void AddCheck(string name, bool passed, double actual, double limit, string detail)
            => validation.report.checks.Add(new Check { name = name, passed = passed, actual = actual, limit = limit, detail = detail });
        static bool Finite(Vector3 point)
            => !float.IsNaN(point.x) && !float.IsNaN(point.y) && !float.IsNaN(point.z)
            && !float.IsInfinity(point.x) && !float.IsInfinity(point.y) && !float.IsInfinity(point.z);
        static IEnumerator ValidationSteps()
        {
            SphericalTerrainPreview terrain = validation.terrain; SphericalPreviewCamera navigation = validation.navigation;
            validation.report.activeStage = "full topology"; SaveValidation();
            SphericalWorld world = terrain.World;
            int pentagons = 0, malformed = 0, invalidDirections = 0, invalidWinding = 0, invalidAdjacency = 0;
            int land = 0, water = 0; long directedEdges = 0;
            for (int cell = 0; cell < world.Count; cell++)
            {
                Vector3 center = world.Centers[cell]; Vector3[] corners = world.Corners[cell]; int[] neighbors = world.Neighbors[cell];
                if (corners.Length == 5) pentagons++; else if (corners.Length != 6) malformed++;
                if (corners.Length != neighbors.Length || world.CornerIds[cell].Length != corners.Length) malformed++;
                if (!Finite(center) || Mathf.Abs(center.sqrMagnitude - 1f) > .0001f) invalidDirections++;
                if (world.Cells[cell].Water) water++; else land++;
                directedEdges += corners.Length;
                for (int edge = 0; edge < corners.Length; edge++)
                {
                    Vector3 a = corners[edge], b = corners[(edge + 1) % corners.Length];
                    if (!Finite(a) || Mathf.Abs(a.sqrMagnitude - 1f) > .0001f) invalidDirections++;
                    if (Vector3.Dot(Vector3.Cross(a - center, b - center), center) <= 0f) invalidWinding++;
                    int other = neighbors[edge]; bool reciprocal = false, edgeMatches = false;
                    if (other >= 0 && other < world.Count)
                    {
                        int ca = world.CornerIds[cell][edge], cb = world.CornerIds[cell][(edge + 1) % corners.Length];
                        int[] otherNeighbors = world.Neighbors[other], otherCorners = world.CornerIds[other];
                        for (int j = 0; j < otherNeighbors.Length; j++)
                        {
                            if (otherNeighbors[j] != cell) continue;
                            reciprocal = true;
                            edgeMatches |= otherCorners[j] == cb && otherCorners[(j + 1) % otherCorners.Length] == ca;
                        }
                    }
                    if (!reciprocal || !edgeMatches) invalidAdjacency++;
                }
                if (cell % 40000 == 39999) yield return null;
            }
            AddCheck("Exactly 12 real pentagons", pentagons == 12 && world.Pentagons.Length == 12, pentagons, 12, "Counted actual cell corner arrays across the complete IcoSphere pack.");
            AddCheck("All other cells have six corners", malformed == 0, malformed, 0, "Neighbor and corner counts agree; no six-corner template is imposed on a pentagon.");
            AddCheck("Unit directions are finite", invalidDirections == 0, invalidDirections, 0, "All world centers and dual corners checked.");
            AddCheck("All polygons wind outward", invalidWinding == 0, invalidWinding, 0, "Each center/edge fan triangle faces away from the sphere center.");
            AddCheck("Every edge has a reciprocal neighbor and identical shared corners", invalidAdjacency == 0, invalidAdjacency, 0, "Both cells reference the same two corner IDs in reverse winding order.");
            long euler = world.CornerDirections.Length - directedEdges / 2 + world.Count;
            AddCheck("Closed-sphere Euler characteristic", euler == 2, euler, 2, "Dual vertex count − shared-edge count + cell count = 2.");
            AddCheck("Current world has both land and water", land > 0 && water > 0, land, 1, "Sampled from the existing authored map. Water cells=" + water + ".");
            float maximumHeightError = 0f, maximumNormalError = 0f;
            int seamSamples = 0;
            foreach (int pentagon in world.Pentagons)
            {
                foreach (int cell in world.Neighbors[pentagon])
                {
                    for (int edge = 0; edge < world.Corners[cell].Length; edge++)
                    {
                        int other = world.Neighbors[cell][edge];
                        Vector3 a = world.Corners[cell][edge], b = world.Corners[cell][(edge + 1) % world.Corners[cell].Length];
                        for (int point = 0; point < 3; point++)
                        {
                            Vector3 direction = Vector3.Lerp(a, b, point * .5f).normalized;
                            maximumHeightError = Mathf.Max(maximumHeightError, Mathf.Abs(terrain.Surface.Evaluate(direction, cell).Height - terrain.Surface.Evaluate(direction, other).Height));
                            maximumNormalError = Mathf.Max(maximumNormalError, Vector3.Angle(terrain.Surface.Normal(direction, cell), terrain.Surface.Normal(direction, other)));
                            seamSamples++;
                        }
                    }
                }
                yield return null;
            }
            AddCheck("Shared relief heights agree around every pentagon", maximumHeightError < .001f, maximumHeightError, .001f,
                "Evaluated " + seamSamples + " shared-edge points with both incident-cell hints, including corners.");
            AddCheck("Shared relief normals agree around every pentagon", maximumNormalError < .12f, maximumNormalError, .12f,
                "Both incident cells evaluate the same radial height field; angular difference is in degrees.");
            AddCheck("Uses the existing near-terrain art profile", terrain.terrainProfile && terrain.terrainProfile.IsReady, terrain.terrainProfile ? 1 : 0, 1, "Uses the current readable shape atlas and existing albedo atlas.");
            Light sun = navigation.sun;
            AddCheck("Real directional sunlight with shadows", sun && sun.type == LightType.Directional && sun.shadows != LightShadows.None && sun.intensity > 0,
                sun ? sun.intensity : 0, 0, "The scene contains a real Directional Light referenced by the orbit camera.");

            yield return ValidateRevisionGeography();
            yield return ValidateNearArtInputs();
            foreach (string preset in new[] { "globe", "continent", "europe", "eastasia", "forest", "desert", "tibet", "tibet-wide", "plateau-river", "plateau-edge", "desert-plateau", "single-mountain", "coast", "river", "pentagon" })
            {
                terrain.SetGridVisible(preset == "pentagon"); if (navigation.hud) navigation.hud.GridVisible = preset == "pentagon";
                navigation.ApplyPreset(preset, true); yield return ObserveView(preset);
            }
            terrain.SetGridVisible(false); if (navigation.hud) navigation.hud.GridVisible = false;
            navigation.SetView(179.9f, 20f, 180f, true); yield return ObserveView("east-dateline");
            navigation.SetView(-179.9f, 20f, 180f, true); yield return ObserveView("west-dateline");
            navigation.SetView(0f, 90f, 150f, true); yield return ObserveView("north-pole");
            navigation.SetView(6.1f, 43.3f, 20f, true); yield return ObserveView("close-coast");
            navigation.SetView(37.30833f, 27.30686f, 136.4188f, true); yield return ObserveView("reported-desert");
            navigation.SetView(10f, 46f, 7000f, true);
            Vector3 focus = navigation.FocusDirection;
            navigation.SetView(10f, 46f, 85f, false); yield return ObserveCamera("Globe to terrain", 85f, focus);
            navigation.SetView(10f, 46f, 7000f, false); yield return ObserveCamera("Terrain to globe", 7000f, focus);
            navigation.SetView(10f, 46f, 85f, false); yield return WaitSeconds(.12);
            navigation.SetView(10f, 46f, 7000f, false); yield return WaitSeconds(.12);
            navigation.SetView(10f, 46f, 100f, false); yield return ObserveCamera("Fast reversal ends at the latest target", 100f, focus);
            navigation.SetView(10f, 46f, 85f, true);
            Vector2 screenAnchor = new(navigation.PresentationCamera.pixelWidth * .62f, navigation.PresentationCamera.pixelHeight * .56f);
            Ray anchorRay = navigation.PresentationCamera.ScreenPointToRay(screenAnchor);
            float qb = Vector3.Dot(anchorRay.origin, anchorRay.direction);
            float qc = anchorRay.origin.sqrMagnitude - terrain.radius * terrain.radius;
            float discriminant = qb * qb - qc;
            if (discriminant > 0f)
            {
                Vector3 anchorPoint = anchorRay.GetPoint(-qb - Mathf.Sqrt(discriminant)).normalized * terrain.radius;
                navigation.ApplyScroll(-4f, screenAnchor);
                double until = EditorApplication.timeSinceStartup + 15;
                while (!navigation.IsSettled && EditorApplication.timeSinceStartup < until) yield return null;
                yield return WaitSeconds(.25);
                Vector3 screenAfter = navigation.PresentationCamera.WorldToScreenPoint(anchorPoint);
                float anchorError = Vector2.Distance(screenAnchor, new Vector2(screenAfter.x, screenAfter.y));
                AddCheck("Mouse anchor stays under the cursor during damped zoom", anchorError < 2f, anchorError, 2,
                    "Runs the actual ApplyScroll path at an off-center screen point; error measured in native camera pixels.");
            }
            else AddCheck("Mouse anchor stays under the cursor during damped zoom", false, -1, 2, "The reference sphere anchor ray missed.");
            yield return ValidateRevisionCamera();
            yield return ValidateRevisionStreaming();
            AddCheck("At least one detailed view contains existing tree meshes", validation.report.views.Exists(view => view.trees > 0),
                validation.report.views.FindAll(view => view.trees > 0).Count, 1, "Counts actual terrain.TreeCount after view readiness.");
            AddCheck("At least one detailed view contains river geometry", validation.report.views.Exists(view => view.riverSegments > 0),
                validation.report.views.FindAll(view => view.riverSegments > 0).Count, 1, "Counts actual terrain.RiverSegmentCount after view readiness.");
            int shaderErrors = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { AssetRoot }))
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var message in ShaderUtil.GetShaderMessages(shader)) if (message.severity.ToString() == "Error") shaderErrors++;
            }
            AddCheck("Preview shaders have no compiler errors", shaderErrors == 0, shaderErrors, 0, "Queries ShaderUtil compiler messages for each shader in the isolated preview folder.");
            AddCheck("No Unity error or exception logged", Errors.Count == 0, Errors.Count, 0, "Errors are collected from owned Play startup through validation.");
        }

        static IEnumerator ObserveView(string name)
        {
            SphericalTerrainPreview terrain = validation.terrain; SphericalPreviewCamera navigation = validation.navigation;
            Vector3 expectedFocus = navigation.FocusDirection;
            float expectedAltitude = navigation.Altitude;
            float expectedLongitude = navigation.Longitude, expectedLatitude = navigation.Latitude;
            float maximumFocusDrift = 0f, maximumAltitudeDrift = 0f;
            validation.report.activeStage = name; SaveValidation();
            double start = EditorApplication.timeSinceStartup, stableSince = start;
            int previousCount = -1, renderedFrames = 0, lastFrame = Time.frameCount;
            while (EditorApplication.timeSinceStartup - start < 180)
            {
                ObservePose(navigation, expectedFocus, expectedAltitude, ref maximumFocusDrift, ref maximumAltitudeDrift);
                if (Time.frameCount != lastFrame) { renderedFrames++; lastFrame = Time.frameCount; }
                if (terrain.VisibleDetailCells != previousCount || !DetailReady(terrain, navigation) || !navigation.IsSettled)
                { stableSince = EditorApplication.timeSinceStartup; previousCount = terrain.VisibleDetailCells; }
                if (DetailReady(terrain, navigation) && navigation.IsSettled && renderedFrames >= 60 && EditorApplication.timeSinceStartup - stableSince > 3) break;
                yield return null;
            }
            ViewResult view = new()
            {
                name = name, warmupWallSeconds = EditorApplication.timeSinceStartup - start,
                longitude = navigation.Longitude, latitude = navigation.Latitude, altitude = navigation.Altitude,
                expectedLongitude = expectedLongitude, expectedLatitude = expectedLatitude, expectedAltitude = expectedAltitude,
                detailReady = DetailReady(terrain, navigation), cameraSettled = navigation.IsSettled,
                detailCells = terrain.VisibleDetailCells, trees = terrain.TreeCount, riverSegments = terrain.RiverSegmentCount,
                screenWidth = navigation.PresentationCamera.pixelWidth, screenHeight = navigation.PresentationCamera.pixelHeight,
                targetTextureAssigned = navigation.PresentationCamera.targetTexture
            };
            RecordLoading(view, terrain);
            AddCheck(name + " camera and world ready", view.detailReady && view.cameraSettled, view.warmupWallSeconds, 180, "Waited for camera settlement, current-focus detail completion and stable detail count across at least 60 rendered frames.");
            AddCheck(name + " native 1080p rendering", view.screenWidth == 1920 && view.screenHeight == 1080 && !view.targetTextureAssigned,
                view.screenWidth, 1920, "Measured camera.pixelWidth=" + view.screenWidth + ", pixelHeight=" + view.screenHeight + ". Screenshot resolution is separate.");
            int invalidVertices = 0, invalidTriangles = 0, readableVertices = 0;
            foreach (MeshFilter filter in terrain.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter.sharedMesh; if (!mesh) continue;
                view.meshVertices += mesh.vertexCount;
                if (!mesh.isReadable) continue;
                Vector3[] vertices = mesh.vertices; readableVertices += vertices.Length;
                foreach (Vector3 vertex in vertices) if (!Finite(vertex)) invalidVertices++;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    foreach (int index in mesh.GetIndices(submesh)) if (index < 0 || index >= vertices.Length) invalidTriangles++;
            }
            AddCheck(name + " finite mesh geometry", invalidVertices == 0 && invalidTriangles == 0 && terrain.GeometryValidationErrors == 0 && view.meshVertices > 0,
                invalidVertices + invalidTriangles + terrain.GeometryValidationErrors, 0,
                "Visible mesh vertices=" + view.meshVertices + "; directly checked readable vertices=" + readableVertices
                    + ". Released CPU mesh arrays were checked by the terrain publisher before GPU upload; total published vertices="
                    + terrain.PublishedVertexCount + ", publisher geometry errors=" + terrain.GeometryValidationErrors + ".");
            if (terrain.TryPick(navigation.PresentationCamera.ViewportPointToRay(new Vector3(.5f, .5f)), out int picked, out Vector3 hit))
            {
                terrain.SetSelection(picked);
                AddCheck(name + " spherical cell selection", picked >= 0 && picked < terrain.World.Count && Finite(hit) && terrain.SelectedCell == picked,
                    picked, terrain.World.Count, "Ray-picked the center of the actual visible spherical view and stored the sphere cell ID.");
            }
            else AddCheck(name + " spherical cell selection", false, -1, 0, "The center ray missed the visible sphere.");
            List<float> frames = new(180); double sampling = EditorApplication.timeSinceStartup; lastFrame = Time.frameCount;
            while (frames.Count < 120 && EditorApplication.timeSinceStartup - sampling < 90)
            {
                ObservePose(navigation, expectedFocus, expectedAltitude, ref maximumFocusDrift, ref maximumAltitudeDrift);
                if (Time.frameCount != lastFrame)
                {
                    lastFrame = Time.frameCount; frames.Add(Time.unscaledDeltaTime * 1000f);
                }
                yield return null;
            }
            view.renderedFrames = renderedFrames + frames.Count; view.frameSamples = frames.Count; view.sampleWallSeconds = EditorApplication.timeSinceStartup - sampling;
            if (frames.Count > 0)
            {
                double sum = 0; foreach (float frame in frames) sum += frame; frames.Sort();
                view.meanFrameMs = sum / frames.Count; view.p95FrameMs = frames[Mathf.Clamp(Mathf.CeilToInt(frames.Count * .95f) - 1, 0, frames.Count - 1)];
                view.maximumFrameMs = frames[frames.Count - 1];
            }
            view.allocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong(); view.graphicsDriverAllocatedBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
            RecordLoading(view, terrain);
            ObservePose(navigation, expectedFocus, expectedAltitude, ref maximumFocusDrift, ref maximumAltitudeDrift);
            view.longitude = navigation.Longitude; view.latitude = navigation.Latitude; view.altitude = navigation.Altitude;
            if (validation.request.action == "water-review" || validation.request.action == LandformReviewAction) terrain.SetSelection(-1);
            view.screenshot = Capture(navigation.PresentationCamera, validation.request.id + "-" + name);
            view.uiScreenshot = Path.Combine(Artifacts, validation.request.id + "-" + name + "-ui.png"); ScreenCapture.CaptureScreenshot(view.uiScreenshot);
            double captureStart = EditorApplication.timeSinceStartup;
            while (!File.Exists(view.uiScreenshot) && EditorApplication.timeSinceStartup - captureStart < 30)
            {
                ObservePose(navigation, expectedFocus, expectedAltitude, ref maximumFocusDrift, ref maximumAltitudeDrift);
                yield return null;
            }
            ObservePose(navigation, expectedFocus, expectedAltitude, ref maximumFocusDrift, ref maximumAltitudeDrift);
            view.maximumFocusDriftDegrees = maximumFocusDrift; view.maximumAltitudeDrift = maximumAltitudeDrift;
            AddCheck(name + " retained the requested capture pose", maximumFocusDrift < .001f && maximumAltitudeDrift < expectedAltitude * .001f,
                maximumFocusDrift, .001f, "Expected longitude=" + expectedLongitude + ", latitude=" + expectedLatitude + ", altitude=" + expectedAltitude
                    + "; maximum altitude drift=" + maximumAltitudeDrift + ". Observed from stage start through both captures; any interrupted pose fails this view.");
            AddCheck(name + " captures completed", File.Exists(view.screenshot) && File.Exists(view.uiScreenshot), File.Exists(view.uiScreenshot) ? 1 : 0, 1, "Actual Unity camera and native Game View images.");
            ValidatePresentationForView(name, terrain, navigation);
            validation.report.views.Add(view); SaveValidation();
        }

        static void ObservePose(SphericalPreviewCamera navigation, Vector3 expectedFocus, float expectedAltitude, ref float maximumFocusDrift, ref float maximumAltitudeDrift)
        {
            float chord = (navigation.FocusDirection - expectedFocus).magnitude;
            float degrees = 2f * Mathf.Asin(Mathf.Min(1f, chord * .5f)) * Mathf.Rad2Deg;
            maximumFocusDrift = Mathf.Max(maximumFocusDrift, degrees);
            maximumAltitudeDrift = Mathf.Max(maximumAltitudeDrift, Mathf.Abs(navigation.Altitude - expectedAltitude));
        }

        static IEnumerator ObserveCamera(string name, float target, Vector3 focus)
        {
            SphericalPreviewCamera navigation = validation.navigation;
            double start = EditorApplication.timeSinceStartup; int invalid = 0; float maximumFocusError = 0;
            while (EditorApplication.timeSinceStartup - start < 15)
            {
                if (!Finite(navigation.transform.position) || !Finite(navigation.transform.forward)) invalid++;
                maximumFocusError = Mathf.Max(maximumFocusError, Vector3.Angle(navigation.FocusDirection, focus));
                if (navigation.IsSettled) break;
                yield return null;
            }
            AddCheck(name, invalid == 0 && Mathf.Abs(navigation.Altitude - target) < target * .003f && maximumFocusError < .05f,
                navigation.Altitude, target, "Finite spherical camera transform; maximum focus drift=" + maximumFocusError.ToString("F4") + " degrees.");
        }
        static bool DetailReady(SphericalTerrainPreview terrain, SphericalPreviewCamera navigation)
            => terrain.IsReady && (navigation.Altitude > 650f || (!terrain.DetailLoading && (navigation.FocusDirection - terrain.DetailFocus).magnitude * terrain.radius < 42f));
        static IEnumerator WaitSeconds(double seconds)
        {
            double until = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < until) yield return null;
        }
        static void SaveValidation()
        {
            if (validation == null) return;
            Directory.CreateDirectory(Artifacts);
            WriteAtomic(Path.Combine(Artifacts, "Validation-" + validation.request.id + ".json"), JsonUtility.ToJson(validation.report, true));
        }
        static void FinishValidation(bool completed, string message)
        {
            if (validation == null) return;
            ValidationSession session = validation;
            session.report.errors = Errors.ToArray(); session.report.finishedUtc = DateTime.UtcNow.ToString("O"); session.report.message = message;
            bool passed = completed && session.report.checks.TrueForAll(check => check.passed);
            session.report.status = passed ? "passed" : completed ? "needs-review" : "failed";
            if (session.navigation)
            {
                session.navigation.InputEnabled = session.oldInput;
                session.navigation.SetDirectionView(session.oldFocus, session.oldAltitude, true);
                if (session.navigation.hud) session.navigation.hud.GridVisible = session.oldGrid;
            }
            if (session.terrain) { session.terrain.SetGridVisible(session.oldGrid); session.terrain.SetSelection(session.oldSelection); }
            session.gameViewSize?.Dispose(); session.report.gameViewSelectionRestored = session.gameViewSize?.restored ?? false;
            FinishWaterReview(session, message);
            FinishLandformReview(session, message);
            SaveValidation();
            Response response = Status(session.request, passed, message, session.report.status);
            response.report = Path.Combine(Artifacts, "Validation-" + session.request.id + ".json");
            if (session.request.action == LandformReviewAction) response.report = LandformReviewPath(session.request.id);
            validation = null; WriteResponse(response);
        }

        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        sealed class GameViewSizeScope : IDisposable
        {
            public string message;
            public bool requested, restored;
            EditorWindow window; MethodInfo select; PropertyInfo selected; int previous;
            public static GameViewSizeScope TryFullHd()
            {
                GameViewSizeScope scope = new();
                try
                {
                    Assembly editor = typeof(EditorWindow).Assembly;
                    Type viewType = editor.GetType("UnityEditor.GameView", true);
                    Object[] windows = Resources.FindObjectsOfTypeAll(viewType);
                    if (windows.Length == 0) throw new InvalidOperationException("No existing Game View is open.");
                    scope.window = windows[0] as EditorWindow;
                    Type sizesType = editor.GetType("UnityEditor.GameViewSizes", true);
                    object sizes = sizesType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy).GetValue(null);
                    object group = sizesType.GetProperty("currentGroup", InstanceMembers).GetValue(sizes);
                    Type groupType = group.GetType(); int count = (int)groupType.GetMethod("GetTotalCount", InstanceMembers).Invoke(group, null);
                    MethodInfo getSize = groupType.GetMethod("GetGameViewSize", InstanceMembers); int fullHd = -1;
                    for (int i = 0; i < count; i++)
                    {
                        object size = getSize.Invoke(group, new object[] { i }); Type type = size.GetType();
                        int width = (int)type.GetProperty("width", InstanceMembers).GetValue(size), height = (int)type.GetProperty("height", InstanceMembers).GetValue(size);
                        if (width == 1920 && height == 1080 && type.GetProperty("sizeType", InstanceMembers).GetValue(size).ToString() == "FixedResolution") { fullHd = i; break; }
                    }
                    if (fullHd < 0) throw new InvalidOperationException("No existing fixed 1920x1080 Game View entry.");
                    scope.selected = viewType.GetProperty("selectedSizeIndex", InstanceMembers); scope.select = viewType.GetMethod("SizeSelectionCallback", InstanceMembers);
                    scope.previous = (int)scope.selected.GetValue(scope.window); scope.requested = true;
                    scope.select.Invoke(scope.window, new object[] { fullHd, null }); scope.window.Repaint(); EditorApplication.QueuePlayerLoopUpdate();
                    scope.message = "Temporarily selected the existing fixed 1920x1080 Game View. Native camera pixel dimensions are verified separately from screenshots; original selection is restored.";
                }
                catch (Exception exception) { scope.Dispose(); scope.message = "Fixed Full HD selection unavailable: " + exception.GetBaseException().Message; }
                return scope;
            }
            public void Dispose()
            {
                if (!requested || restored || !window) return;
                try { select.Invoke(window, new object[] { previous, null }); window.Repaint(); restored = (int)selected.GetValue(window) == previous; }
                catch (Exception exception) { Debug.LogWarning("Could not restore spherical preview Game View selection: " + exception.GetBaseException().Message); }
            }
        }
    }
}
