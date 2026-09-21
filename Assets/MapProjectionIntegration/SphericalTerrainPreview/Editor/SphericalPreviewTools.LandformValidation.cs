using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        const string LandformReviewAction = "landform-review";
        const string LandformReviewProtocol = "global-terrain-fixed-views-v1";
        const string LandformTerrainShader = "WW2/Spherical Terrain Preview/Terrain";
        static LandformReviewReport landformReview;

        [Serializable] sealed class LandformViewSpec
        {
            public string name, label;
            public float longitude, latitude, altitude, regionRadiusDegrees;
            public bool requiresDetail;
            public LandformViewSpec(string key, string label, float longitude, float latitude, float altitude, float radius)
            {
                name = "全球地形-" + key; this.label = "全球地形 · " + label;
                this.longitude = longitude; this.latitude = latitude; this.altitude = altitude;
                regionRadiusDegrees = radius; requiresDetail = altitude <= 200;
            }
        }

        // Fixed geographic anchors, never selected from the new classification.
        // Region statistics use spherical caps, independently of camera framing.
        static readonly LandformViewSpec[] LandformReviewViews =
        {
            new("globe-east", "全球东半球", 50, 25, 7000, 180),
            new("globe-west", "全球西半球", -90, 10, 7000, 180),
            new("tibet-interior", "青藏高原内部", 86, 33, 160, 5),
            new("himalaya", "喜马拉雅山地", 86.9f, 28.1f, 180, 4),
            new("ethiopia", "埃塞俄比亚高原", 38.5f, 10.5f, 160, 4),
            new("altiplano", "阿尔蒂普拉诺高原", -68, -18, 180, 4),
            new("andes", "安第斯山地", -70, -32, 180, 4),
            new("rockies", "落基山地", -109, 44, 180, 4),
            new("alps", "阿尔卑斯山地", 10, 46.5f, 140, 3),
            new("deccan", "德干高原", 77, 17, 180, 4),
            new("mongolia", "蒙古高原", 103, 46.5f, 180, 5),
            new("brazil", "巴西高原", -47, -16, 180, 5)
        };

        [Serializable] sealed class LandformTally
        {
            public int total, water, land, unknownRaw, unknownLocal;
            public int[] rawAll = new int[4], rawLand = new int[4], localLand = new int[4];
            public void Add(SphericalCellData cell)
            {
                total++;
                if (cell.Landform >= 0 && cell.Landform < rawAll.Length) rawAll[cell.Landform]++;
                else unknownRaw++;
                if (cell.Water) { water++; return; }
                land++;
                if (cell.Landform >= 0 && cell.Landform < rawLand.Length) rawLand[cell.Landform]++;
                int local = SphericalSurface.LocalLandform(cell.Landform);
                if (local >= 0 && local < localLand.Length) localLand[local]++; else unknownLocal++;
            }
        }
        [Serializable] sealed class LandformReviewReport
        {
            public string id, status, message, startedUtc, finishedUtc, validationReport;
            public string protocol = LandformReviewProtocol, worldClassificationSha256, worldGeometrySha256;
            public string countsNote = "Every count uses actual current SphericalWorld cell IDs, once per region. Regions are overlapping geodesic caps, not administrative boundaries. rawAll/rawLand/localLand indices: 0 Flat, 1 Hill, 2 Mountain, 3 Plateau. LocalLandform is evaluated from current runtime code; an elevated plain is distinct from a mountain. Counts establish what is present, not geographic correctness.";
            public string acceptanceNote = "Automated checks establish fixed camera poses, actual published near meshes and shader coverage at first surface hits, and clean shader diagnostics. Images still require visual acceptance; no mountain/plateau proportion is declared correct by a count threshold.";
            public int worldCells, detailSubdivisions;
            public float sphereRadius, plateauStepHeight;
            public LandformTally world = new();
            public List<LandformViewEvidence> views = new();
            public List<Check> checks = new();
            public string[] errors;
        }
        [Serializable] sealed class LandformViewEvidence
        {
            public LandformViewSpec spec;
            public LandformTally region = new(), firstHitCells = new();
            public ViewResult observation;
            public string screenshot, uiScreenshot, screenshotSha256, uiScreenshotSha256;
            public int centerCell, frustumDetailMeshes, frustumDetailVertices, boundShaders, shaderErrors;
            public int firstHitRays, dryFirstHitRays, detailedDryFirstHitRays, sampledPlateauTops;
            public float minimumBaseHeight, maximumBaseHeight, maximumLocalRelief, minimumDetailCoverage = 1;
            public float cameraFieldOfView, sunElevation;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public bool centerHasDetail;
            public List<string> shaderMessages = new();
            public string visibilityNote = "153 camera rays query the first spherical surface hit; dry hits must lie within an active nonempty detail renderer and its actual bound coverage texture must exceed 0.95 with satellite blend below 0.005. This establishes geometric/shader eligibility, not pixel-level vegetation occlusion or visual quality. Global views intentionally use the whole-Earth layer.";
        }

        static string LandformReviewDirectory(string id)
            => Path.Combine(Artifacts, "GlobalTerrainRevision20260913", id);
        static string LandformReviewPath(string id)
            => Path.Combine(LandformReviewDirectory(id), "LandformReview.json");

        static IEnumerator LandformReviewSteps()
        {
            ValidationSession session = validation;
            if (File.Exists(LandformReviewPath(session.request.id)))
                throw new InvalidOperationException("Global terrain review ID already exists; use a new ID to preserve the before/after evidence.");
            var world = session.terrain.World;
            landformReview = new LandformReviewReport
            {
                id = session.request.id, status = "running", startedUtc = DateTime.UtcNow.ToString("O"),
                validationReport = Path.Combine(LandformReviewDirectory(session.request.id), "Validation.json"),
                worldCells = world.Count, sphereRadius = world.Radius, detailSubdivisions = session.terrain.detailSubdivisions,
                plateauStepHeight = session.terrain.Surface.PlateauStepHeight
            };
            session.report.activeStage = "全球地形 · actual sphere counts"; SaveValidation(); SaveLandformReview();
            var centers = new Vector3[LandformReviewViews.Length];
            var thresholds = new float[centers.Length];
            foreach (var spec in LandformReviewViews) landformReview.views.Add(new LandformViewEvidence { spec = spec });
            for (int i = 0; i < centers.Length; i++)
            {
                centers[i] = SphericalPreviewCamera.Direction(LandformReviewViews[i].longitude, LandformReviewViews[i].latitude);
                thresholds[i] = Mathf.Cos(LandformReviewViews[i].regionRadiusDegrees * Mathf.Deg2Rad);
            }
            // Hash the actual indexed classification and geographic cohort independently.
            using (var classification = new MemoryStream())
            using (var geometry = new MemoryStream())
            using (var attributes = new BinaryWriter(classification))
            using (var directions = new BinaryWriter(geometry))
            {
                attributes.Write(world.Count); directions.Write(world.Count); directions.Write(world.Radius);
                for (int id = 0; id < world.Count; id++)
                {
                    var cell = world.Cells[id]; var direction = world.Centers[id];
                    landformReview.world.Add(cell);
                    attributes.Write(cell.Water); attributes.Write(cell.Landform); attributes.Write(cell.Biome); attributes.Write(cell.Elevation);
                    directions.Write(direction.x); directions.Write(direction.y); directions.Write(direction.z);
                    for (int i = 0; i < centers.Length; i++)
                        if (LandformReviewViews[i].regionRadiusDegrees >= 180 || Vector3.Dot(centers[i], direction) >= thresholds[i])
                            landformReview.views[i].region.Add(cell);
                    if (id % 30000 == 29999) yield return null;
                }
                attributes.Flush(); directions.Flush();
                classification.Position = geometry.Position = 0;
                landformReview.worldClassificationSha256 = LandformHash(classification);
                landformReview.worldGeometrySha256 = LandformHash(geometry);
            }
            AddCheck("全球地形 all actual sphere cells counted", landformReview.world.total == world.Count
                && landformReview.world.unknownRaw == 0 && landformReview.world.unknownLocal == 0,
                landformReview.world.total, world.Count, "Raw values and current LocalLandform mapping are reported separately; water is counted independently.");
            session.terrain.SetGridVisible(false); session.terrain.SetSelection(-1);
            if (session.navigation.hud) session.navigation.hud.GridVisible = false;
            SaveLandformReview();
            foreach (var evidence in landformReview.views)
            {
                var spec = evidence.spec;
                session.navigation.SetView(spec.longitude, spec.latitude, spec.altitude, true);
                // ObserveView exclusively owns readiness, full-duration drift checks,
                // real camera capture and native Game View capture. Do not reapply pose.
                yield return ObserveView(spec.name);
                evidence.observation = session.report.views[session.report.views.Count - 1];
                InspectLandformView(evidence);
                ArchiveLandformCapture(evidence);
                SaveLandformReview(); SaveValidation();
            }
            AddCheck("全球地形 all 12 fixed views captured", landformReview.views.Count == 12
                && landformReview.views.TrueForAll(item => !string.IsNullOrEmpty(item.screenshotSha256) && !string.IsNullOrEmpty(item.uiScreenshotSha256)),
                landformReview.views.FindAll(item => !string.IsNullOrEmpty(item.screenshotSha256)).Count, 12, "Both hemispheres plus ten regional near views; no failed or empty view is omitted.");
            AddCheck("全球地形 no Unity errors or exceptions", Errors.Count == 0, Errors.Count, 0,
                "Uses the existing owned preview log collection; every view also records compiler diagnostics from every active bound shader.");
            SaveLandformReview();
        }

        static void InspectLandformView(LandformViewEvidence evidence)
        {
            var terrain = validation.terrain; var navigation = validation.navigation; var camera = navigation.PresentationCamera;
            var world = terrain.World; var spec = evidence.spec; var view = evidence.observation;
            evidence.centerCell = world.FindContainingCell(navigation.FocusDirection);
            evidence.cameraPosition = camera.transform.position; evidence.cameraRotation = camera.transform.rotation;
            evidence.cameraFieldOfView = camera.fieldOfView; evidence.sunElevation = navigation.sunElevation;
            float requestedFocusError = (navigation.FocusDirection - SphericalPreviewCamera.Direction(spec.longitude, spec.latitude)).magnitude;
            AddCheck(spec.name + " requested geographic pose was used", requestedFocusError < .000001f
                && Mathf.Abs(navigation.Altitude - spec.altitude) < .02f && !navigation.InputEnabled,
                requestedFocusError, .000001, "Independent fixed specification compared after ObserveView; validation never rewrites the pose during observation or capture.");
            AddCheck(spec.name + " region contains actual land cells", evidence.region.land > 0
                && evidence.region.unknownRaw == 0 && evidence.region.unknownLocal == 0,
                evidence.region.land, 1, "Geodesic cap radius=" + spec.regionRadiusDegrees + " degrees; all real cells in the cap counted, without selecting a favorable landform.");
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var seenShaders = new HashSet<int>(); var details = new List<MeshRenderer>();
            foreach (var renderer in terrain.GetComponentsInChildren<MeshRenderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var filter = renderer.GetComponent<MeshFilter>(); var mesh = filter ? filter.sharedMesh : null;
                bool nonempty = mesh && mesh.vertexCount > 0 && mesh.subMeshCount > 0 && mesh.GetIndexCount(0) > 0;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || !material.shader) continue;
                    var shader = material.shader;
                    if (seenShaders.Add(shader.GetInstanceID()))
                    {
                        evidence.boundShaders++;
                        if (!shader.isSupported) { evidence.shaderErrors++; evidence.shaderMessages.Add(shader.name + ": unsupported"); }
                        foreach (var message in ShaderUtil.GetShaderMessages(shader))
                            if (message.severity.ToString() == "Error")
                            { evidence.shaderErrors++; evidence.shaderMessages.Add(shader.name + ": " + message.message); }
                    }
                    if (shader.name != LandformTerrainShader || !nonempty || !GeometryUtility.TestPlanesAABB(planes, renderer.bounds)
                        || !material.HasProperty("_SurfaceLod") || material.GetFloat("_SurfaceLod") >= .5f) continue;
                    details.Add(renderer); evidence.frustumDetailMeshes++; evidence.frustumDetailVertices += mesh.vertexCount;
                    break;
                }
            }
            var hitCells = new HashSet<int>(); bool sampled = false;
            for (int y = 0; y < 9; y++) for (int x = 0; x < 17; x++)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3((x + .5f) / 17, (y + .5f) / 9));
                if (!terrain.TryPick(ray, out int id, out Vector3 hit)) continue;
                evidence.firstHitRays++;
                if (hitCells.Add(id)) evidence.firstHitCells.Add(world.Cells[id]);
                var sample = terrain.Surface.Evaluate(hit.normalized, id);
                if (sample.Land < .95f || sample.Height < .04f) continue;
                evidence.dryFirstHitRays++;
                if (!sampled) { evidence.minimumBaseHeight = evidence.maximumBaseHeight = sample.BaseHeight; sampled = true; }
                evidence.minimumBaseHeight = Mathf.Min(evidence.minimumBaseHeight, sample.BaseHeight);
                evidence.maximumBaseHeight = Mathf.Max(evidence.maximumBaseHeight, sample.BaseHeight);
                evidence.maximumLocalRelief = Mathf.Max(evidence.maximumLocalRelief, sample.Height - sample.BaseHeight);
                if (Mathf.Abs(sample.BaseHeight - (.178f + terrain.Surface.PlateauStepHeight)) < .1f) evidence.sampledPlateauTops++;
                bool detailed = false;
                foreach (var renderer in details)
                {
                    if (!renderer.bounds.Contains(hit)) continue;
                    var material = renderer.sharedMaterial;
                    if (material.GetFloat("_SatelliteBlend") > .005f) continue;
                    float coverage = LandformCoverage(material, hit.normalized);
                    evidence.minimumDetailCoverage = Mathf.Min(evidence.minimumDetailCoverage, coverage);
                    if (coverage > .95f) { detailed = true; break; }
                }
                if (detailed) evidence.detailedDryFirstHitRays++;
                if (x == 8 && y == 4) evidence.centerHasDetail = detailed;
            }
            AddCheck(spec.name + " active bound shaders are supported and clean", evidence.boundShaders > 0 && evidence.shaderErrors == 0,
                evidence.shaderErrors, 0, string.Join("; ", evidence.shaderMessages));
            if (spec.requiresDetail)
                AddCheck(spec.name + " real detailed land is visible at first surface hits", view.detailReady && view.cameraSettled
                    && evidence.frustumDetailMeshes > 0 && evidence.frustumDetailVertices > 0
                    && evidence.centerHasDetail && evidence.detailedDryFirstHitRays >= 12,
                    evidence.detailedDryFirstHitRays, 12, "Center must be dry published detail. Active near meshes=" + evidence.frustumDetailMeshes
                    + "; near vertices=" + evidence.frustumDetailVertices + "; dry first hits=" + evidence.dryFirstHitRays
                    + ". Reads the actual bound coverage texture; neither an analytic height sample nor bounds intersection alone passes.");
            else AddCheck(spec.name + " whole Earth overview captured", view.altitude > 650 && evidence.firstHitRays > 0,
                evidence.firstHitRays, 1, "Explicit overview exception: global frames use the distant whole-Earth surface; the ten regional frames require the real detailed layer.");
        }

        static float LandformCoverage(Material material, Vector3 direction)
        {
            if (!material.HasProperty("_UseDetailCoverage") || material.GetFloat("_UseDetailCoverage") < .5f) return 0;
            var texture = material.GetTexture("_DetailCoverage") as Texture2D;
            if (!texture || !texture.isReadable) return 0;
            Vector3 focus = material.GetVector("_CoverageFocus"), east = material.GetVector("_CoverageEast"), north = material.GetVector("_CoverageNorth");
            if (Vector3.Dot(direction, focus) <= 0) return 0;
            Vector3 delta = direction - focus;
            float scale = material.GetFloat("_SphereRadius") / Mathf.Max(1, material.GetFloat("_CoverageWorldSize"));
            float u = Vector3.Dot(delta, east) * scale + .5f, v = Vector3.Dot(delta, north) * scale + .5f;
            if (u < 0 || u > 1 || v < 0 || v > 1) return 0;
            return texture.GetPixelBilinear(u, v, 0).r;
        }

        static void ArchiveLandformCapture(LandformViewEvidence evidence)
        {
            string directory = LandformReviewDirectory(validation.request.id);
            string Copy(string source)
            {
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;
                string destination = Path.Combine(directory, Path.GetFileName(source));
                File.Copy(source, destination, false); return destination;
            }
            evidence.screenshot = Copy(evidence.observation.screenshot);
            evidence.uiScreenshot = Copy(evidence.observation.uiScreenshot);
            if (evidence.screenshot != null) using (var file = File.OpenRead(evidence.screenshot)) evidence.screenshotSha256 = LandformHash(file);
            if (evidence.uiScreenshot != null) using (var file = File.OpenRead(evidence.uiScreenshot)) evidence.uiScreenshotSha256 = LandformHash(file);
        }
        static string LandformHash(Stream stream)
        { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        static void SaveLandformReview()
        {
            if (landformReview == null) return;
            Directory.CreateDirectory(LandformReviewDirectory(landformReview.id));
            if (validation != null) landformReview.checks = new List<Check>(validation.report.checks);
            WriteAtomic(LandformReviewPath(landformReview.id), JsonUtility.ToJson(landformReview, true));
        }
        static void FinishLandformReview(ValidationSession session, string message)
        {
            if (session.request.action != LandformReviewAction || landformReview == null) return;
            landformReview.status = session.report.status; landformReview.message = message;
            landformReview.finishedUtc = DateTime.UtcNow.ToString("O"); landformReview.errors = session.report.errors;
            SaveLandformReview();
            WriteAtomic(landformReview.validationReport, JsonUtility.ToJson(session.report, true));
            landformReview = null;
        }
    }
}
