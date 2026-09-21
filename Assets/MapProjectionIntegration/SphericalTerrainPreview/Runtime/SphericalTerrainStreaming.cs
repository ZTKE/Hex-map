using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        // Stable geographic ownership lets a location survive camera motion and
        // preset changes. Only CPU arrays are created on the worker threads.
        const int TileResolution = 96, MaxCachedChunks = 40;
        const float DetailRevealSeconds = .85f;
        readonly Dictionary<int, Chunk> chunks = new();
        readonly HashSet<int> wanted = new();
        readonly List<int> orderedWanted = new();
        readonly ConcurrentDictionary<long, Lazy<CellGeometry>> buildingCells = new();
        readonly ConcurrentQueue<long> buildingCellOrder = new();
        Dictionary<int, int[]> tileCells;
        readonly CancellationTokenSource lifetime = new();
        Task<SphericalWorld> startupWorldTask;
        Task<Dictionary<int, int[]>> startupIndexTask;
        Task<ChunkData> startupShellTask;
        SphericalVegetationField vegetation;
        SphericalDetailCoverage coverage;
        Vector3 coverageFocus, coverageEast, coverageNorth;
        bool streamStarted, coverageDirty, countsDirty, wasFar, coverageHasDetail;
        Material[] presentationMaterials, coverageMaterials;
        readonly Plane[] vegetationPlanes = new Plane[6];
        float previousPresentationAltitude = -1;
        bool previousTerrainGlobe;
        public bool NativeTerrainGlobe { get; set; }
        Vector3 appliedDetailFocus;
        float appliedDetailRadius = -1;
        double requestStarted;
        readonly System.Diagnostics.Stopwatch lifetimeClock = System.Diagnostics.Stopwatch.StartNew();
        float coverageTime;
        public int PendingChunks { get; private set; }
        public int ReadyChunks { get; private set; }
        public bool DetailRevealing { get; private set; }
        public int CacheHits { get; private set; }
        public int BuildCount { get; private set; }
        public int WorkerCount { get; private set; }
        public double FirstVisibleSeconds { get; private set; }
        public double LastLoadSeconds { get; private set; }
        public string LoadingError { get; private set; } = "";
        public int GeometryValidationErrors { get; private set; }
        public long PublishedVertexCount { get; private set; }
        public double StartupSeconds { get; private set; } = -1;
        // Low-overhead runtime measurements used by the in-game rotation probe.
        // Durations describe CPU work only; they do not claim GPU timings.
        public int RegionRequestCount { get; private set; }
        public int CoverageUploadCount { get; private set; }
        public long CoverageUploadBytes { get; private set; }
        public double LastRegionRequestMilliseconds { get; private set; }
        public double LastCoverageMilliseconds { get; private set; }
        public double LastPublicationMilliseconds { get; private set; }
        public double LastStreamingMilliseconds { get; private set; }
        public double LastVegetationMilliseconds { get; private set; }
        public int PublicationStepCount { get; private set; }
        public int CompletedPublicationCount { get; private set; }
        public double LastPublicationMaxStepMilliseconds { get; private set; }
        public int PendingPublicationCount
        { get { int count = 0; foreach (var chunk in chunks.Values) if (chunk.Publication != null) count++; return count; } }
        static long DiagnosticTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
        static double DiagnosticMilliseconds(long start) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
        public long CachedMeshPayloadBytes
        { get { long bytes = 0; foreach (var chunk in chunks.Values) bytes += chunk.Bytes; return bytes; } }
        // Preserve the same geographic cache when matching Game_2's denser near
        // mesh: payload grows approximately with subdivisions squared. The old
        // fixed 512 MiB budget evicted a complete warm view at 20 subdivisions.
        // Keep both the existing chunk-count bound and a hard 1.5 GiB ceiling.
        public long MeshCacheBudgetBytes => Math.Min(1536L * 1024 * 1024,
            512L * 1024 * 1024 * Math.Max(144, detailSubdivisions * detailSubdivisions) / 144);
        public long CachedWaterChannelSavingsBytes
        { get { long bytes = 0; foreach (var chunk in chunks.Values) bytes += chunk.WaterChannelSavings; return bytes; } }

        sealed class Chunk
        {
            public int Key;
            public int[] Cells;
            public Vector3 Center;
            public Task<ChunkData> Work;
            public CancellationTokenSource Cancellation;
            public GameObject Root, Grid;
            public ChunkPublication Publication;
            public Renderer[] Renderers;
            public bool Visible, VisibilityAssigned;
            public readonly List<TreeBatch> Trees = new();
            public int TreeCount, Rivers, Triangles;
            public long Bytes, WaterChannelSavings;
            public double LastUsed;
            public float Fade;
            public float Reveal => Fade * Fade * (3 - 2 * Fade);
        }
        sealed class ChunkData
        {
            public readonly MeshBuffer Land = new(), Water = new(), Lines = new(), Rivers = new(), ShoreWaves = new();
            public readonly List<SphericalVegetationField.Instance> Trees = new();
            public readonly List<TreeBatch> TreeBatches = new();
            public int RiverCount, ValidationErrors;
            public long Bytes, WaterChannelSavings;
        }
        // A partially uploaded tile has no Chunk.Root. Counts, ownership masks,
        // picking detail and vegetation cannot mistake it for published ground.
        sealed class ChunkPublication
        {
            public ChunkData Data;
            public GameObject Root, Grid;
            public readonly Mesh[] Meshes = new Mesh[5];
            public readonly Renderer[] Renderers = new Renderer[5];
            public int Step, TreeCursor;
            public long UploadedVertices;
        }
        sealed class CellGeometry { public readonly MeshBuffer Land = new(), Water = new(); }
        sealed class TreeBatch
        {
            public int Species;
            public Matrix4x4[] Matrices;
            public Vector4[] Tints;
            public Vector3[] RootDirections;
            public float[] RevealThresholds;
            public Matrix4x4[] DrawMatrices;
            public Vector4[] DrawTints;
            public MaterialPropertyBlock Properties;
            public Bounds Bounds;

            public int SelectVisible(Vector3 focus, float radius, float detailRadius, float publishedVisibility)
            {
                int count = 0;
                for (int i = 0; i < Matrices.Length; i++)
                {
                    float distance = (RootDirections[i] - focus).magnitude * radius;
                    float visibility = publishedVisibility *
                        (1 - SphericalSurface.Smooth(detailRadius - 30, detailRadius, distance));
                    // Reveal whole trees, with a fixed threshold tied to their
                    // planted root. Camera pixels and batch centers never clip
                    // holes through an otherwise opaque leaf or trunk.
                    if (visibility < RevealThresholds[i]) continue;
                    DrawMatrices[count] = Matrices[i]; DrawTints[count] = Tints[i]; count++;
                }
                return count;
            }

            public static float RevealThreshold(Vector3 root)
            {
                unchecked
                {
                    uint hash = SphericalSurface.Hash((uint)Mathf.RoundToInt(root.x * 128));
                    hash = SphericalSurface.Hash(hash ^ (uint)Mathf.RoundToInt(root.y * 128));
                    hash = SphericalSurface.Hash(hash ^ (uint)Mathf.RoundToInt(root.z * 128));
                    // Strictly inside (0, 1): zero hides every tree, one exposes
                    // every tree, including at the exact focus and polar seam.
                    return ((hash & 0xffffu) + .5f) / 65536f;
                }
            }
        }

        IEnumerator Start()
        {
            var initialization = InitializeStreaming();
            while (true)
            {
                bool more; object current = null;
                try { more = initialization.MoveNext(); if (more) current = initialization.Current; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception error) { FailLoading(error.ToString()); break; }
                if (!more) break;
                yield return current;
            }
        }

        IEnumerator InitializeStreaming()
        {
            if (!terrainProfile || !terrainProfile.IsReady)
            { FailLoading("Missing current near-terrain art profile"); yield break; }
            Status = SphericalWorld.HasNativeSnapshot(recursion) ? "Loading saved spherical world" : "Importing legacy geography into spherical world";
            yield return null;
            var token = lifetime.Token;
            var factory = SphericalWorld.PrepareLoad(recursion, radius, token);
            var worldTask = startupWorldTask = Task.Run(factory, token);
            ObserveStartupFault(worldTask);
            while (!worldTask.IsCompleted) { if (token.IsCancellationRequested) yield break; yield return null; }
            if (worldTask.IsCanceled || token.IsCancellationRequested) yield break;
            if (worldTask.IsFaulted) { FailLoading(worldTask.Exception.GetBaseException().ToString()); yield break; }
            World = worldTask.Result; startupWorldTask = null;
            Status = "Preparing terrain, river and forest recipes";
            yield return null;
            Surface = new SphericalSurface(World, terrainProfile, new SphericalCoastField(World, terrainStyle).Evaluate);
            Surface.PrepareForParallelEvaluation();
            vegetation = new SphericalVegetationField(World, Surface, terrainProfile);
            InitializeMaterials();
            coverage = new SphericalDetailCoverage(1024, 1024);
            MoveCoverageFrame(focus);
            Task<Dictionary<int, int[]>> indexTask;
            Task<ChunkData> shellTask;
            if (TryPrepareStartupSnapshot(out var readStartup))
            {
                Status = "Loading saved Earth surface and spatial index";
                var cached = Task.Run(readStartup, token);
                ObserveStartupFault(cached);
                indexTask = startupIndexTask = cached.ContinueWith(t => t.GetAwaiter().GetResult().Tiles, token);
                shellTask = startupShellTask = cached.ContinueWith(t => t.GetAwaiter().GetResult().Shell, token);
                LoadedStartupSnapshot = true;
            }
            else
            {
                Status = "Building complete Earth background";
                indexTask = startupIndexTask = Task.Run(() => BuildTileIndex(token), token);
                var pack = global::IcoSphere.Pack.Read(Mathf.Min(4, recursion));
                shellTask = startupShellTask = Task.Run(() => BuildShellData(pack, token), token);
            }
            ObserveStartupFault(indexTask);
            ObserveStartupFault(shellTask);
            while (!shellTask.IsCompleted || !indexTask.IsCompleted)
            { if (token.IsCancellationRequested) yield break; yield return null; }
            if (shellTask.IsCanceled || indexTask.IsCanceled || token.IsCancellationRequested) yield break;
            if (shellTask.IsFaulted || indexTask.IsFaulted)
            { FailLoading((shellTask.Exception ?? indexTask.Exception).GetBaseException().ToString()); yield break; }
            tileCells = indexTask.Result; startupIndexTask = null;
            var data = shellTask.Result; startupShellTask = null;
            Mesh landMesh = data.Land.Create("Complete spherical satellite surface");
            // Saved shell normals are the finished mesh normals from the bake.
            if (!LoadedStartupSnapshot) landMesh.RecalculateNormals();
            shell = CreateMeshObject("Whole Earth • continuous surface", landMesh, coarseMaterial, transform);
            shell.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            ocean = CreateMeshObject("Whole Earth • ocean", CreateWaterMesh(data.Water, "Spherical ocean"), waterMaterial, transform);
            ocean.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            ShellTriangleCount = data.Land.Indices.Count / 3;
            WorkerCount = Mathf.Clamp(Environment.ProcessorCount - 2, 2, 6);
            streamStarted = true;
            RequestRegion();
        }

        void InitializeMaterials()
        {
            coarseMaterial = Own(SphericalMaterials.CreateTerrain(terrainProfile));
            detailMaterial = Own(SphericalMaterials.CreateTerrain(terrainProfile));
            SphericalMaterials.CopyRiverBankResources(coarseMaterial, riverSourceMaterial);
            SphericalMaterials.CopyRiverBankResources(detailMaterial, riverSourceMaterial);
            waterMaterial = Own(SphericalMaterials.CreateWater(terrainProfile));
            fineWaterMaterial = Own(SphericalMaterials.CreateWater(terrainProfile));
            riverMaterial = Own(SphericalMaterials.CreateWater(terrainProfile));
            InitializeShoreWaves();
            coarseMaterial.SetFloat("_SurfaceLod", 1); detailMaterial.SetFloat("_SurfaceLod", 0);
            waterMaterial.SetFloat("_SurfaceLod", 1); fineWaterMaterial.SetFloat("_SurfaceLod", 0);
            riverMaterial.SetFloat("_SurfaceLod", 0); riverMaterial.SetFloat("_WaterKind", 1);
            foreach (var m in new[] { waterMaterial, fineWaterMaterial, riverMaterial })
                SphericalMaterials.CopyWaterResources(m, waterSourceMaterial, riverSourceMaterial);
            // Fine coasts now draw original crest-atlas strips on the actual
            // water-level contour. Do not also draw the old procedural foam.
            fineWaterMaterial.SetFloat("_Civ6WaterFoamStrength", 0);
            foreach (var m in new[] { coarseMaterial, detailMaterial, waterMaterial, fineWaterMaterial, riverMaterial })
            {
                if (terrainStyle) SphericalMaterials.ConfigureCoast(m, terrainStyle);
                SphericalMaterials.ConfigureSatellite(m, satelliteColor, satelliteRelief);
            }
            gridMaterial = Own(SphericalMaterials.CreateLines(new Color(.74f, .70f, .44f, .36f)));
            selectionMaterial = Own(SphericalMaterials.CreateLines(new Color(1, .84f, .38f, 1)));
            foreach (Material m in ownedMaterials) SphericalMaterials.SetSphereFrame(m, Vector3.zero, radius);
            presentationMaterials = new[] { coarseMaterial, detailMaterial, waterMaterial, fineWaterMaterial, riverMaterial, shoreWaveMaterial };
            coverageMaterials = presentationMaterials;
            InitializeFarEarth();
        }

        ChunkData BuildShellData(global::IcoSphere.Pack pack, CancellationToken token)
        {
            var data = new ChunkData(); int hint = -1;
            for (int i = 0; i < pack.verts.Length; i++)
            {
                if ((i & 511) == 0) token.ThrowIfCancellationRequested();
                Vector3 n = pack.verts[i].normalized;
                var s = Surface.Evaluate(n, hint); hint = s.Cell;
                data.Land.Add(n * (radius + s.Height), n, s);
                data.Water.AddWater(n * radius, n, new Vector4(n.x * radius * .02f, n.z * radius * .02f, 1000, -s.Height),
                    new Vector4(1 - s.Land, s.WaterLandDensity),
                    1 - SphericalSurface.Smooth(.03f, 2.4f, Mathf.Max(0, -s.Height)), WaterBiome(s));
            }
            int triangle = 0;
            foreach (var t in pack.tris)
            {
                if ((triangle++ & 511) == 0) token.ThrowIfCancellationRequested();
                data.Land.TriangleOutward(t[0], t[1], t[2]); data.Water.TriangleOutward(t[0], t[1], t[2]);
            }
            return data;
        }

        static int TileKey(Vector3 n)
        {
            float d = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            float x = n.x / d, y = n.y / d;
            if (n.z < 0)
            { float oldX = x; x = (1 - Mathf.Abs(y)) * (x >= 0 ? 1 : -1); y = (1 - Mathf.Abs(oldX)) * (y >= 0 ? 1 : -1); }
            int ix = Mathf.Clamp((int)((x * .5f + .5f) * TileResolution), 0, TileResolution - 1);
            int iy = Mathf.Clamp((int)((y * .5f + .5f) * TileResolution), 0, TileResolution - 1);
            return iy * TileResolution + ix;
        }
        Dictionary<int, int[]> BuildTileIndex(CancellationToken token)
        {
            var lists = new Dictionary<int, List<int>>();
            for (int i = 0; i < World.Count; i++)
            {
                if ((i & 511) == 0) token.ThrowIfCancellationRequested();
                int key = TileKey(World.Centers[i]);
                if (!lists.TryGetValue(key, out var list)) lists.Add(key, list = new List<int>(64));
                list.Add(i);
            }
            var result = new Dictionary<int, int[]>(lists.Count);
            int completed = 0;
            foreach (var pair in lists)
            { if ((completed++ & 511) == 0) token.ThrowIfCancellationRequested(); result.Add(pair.Key, pair.Value.ToArray()); }
            return result;
        }

        void RequestRegion()
        {
            long diagnosticStart = DiagnosticTimestamp();
            RegionRequestCount++;
            requestedFocus = focus; requestedAltitude = altitude;
            DetailFocus = focus; DetailRadius = Mathf.Clamp(altitude * 2.2f, 75, 290);
            wanted.Clear(); orderedWanted.Clear(); requestStarted = lifetimeClock.Elapsed.TotalSeconds;
            FirstVisibleSeconds = -1; LastLoadSeconds = -1;
            if (altitude <= 650)
            {
                var cells = CollectCells(focus, DetailRadius + World.CellRadius * 1.5f);
                cells.Sort((a, b) => (World.Centers[a] - focus).sqrMagnitude.CompareTo((World.Centers[b] - focus).sqrMagnitude));
                foreach (int id in cells)
                {
                    int key = TileKey(World.Centers[id]); if (!wanted.Add(key)) continue;
                    orderedWanted.Add(key);
                    if (!chunks.TryGetValue(key, out var chunk))
                    {
                        var ids = tileCells[key]; Vector3 center = Vector3.zero;
                        foreach (int cell in ids) center += World.Centers[cell];
                        chunks.Add(key, chunk = new Chunk { Key = key, Cells = ids, Center = center.normalized });
                    }
                    if (chunk.Root) CacheHits++;
                    chunk.LastUsed = lifetimeClock.Elapsed.TotalSeconds;
                }
            }
            foreach (var chunk in chunks.Values)
            {
                bool active = wanted.Contains(chunk.Key);
                if (chunk.Root) SetChunkVisible(chunk, active);
                if (!active && chunk.Work != null && !chunk.Work.IsCompleted) chunk.Cancellation.Cancel();
            }
            MoveCoverageFrame(focus);
            RefreshCounts();
            LastRegionRequestMilliseconds += DiagnosticMilliseconds(diagnosticStart);
        }

        void Update()
        {
            long start = DiagnosticTimestamp();
            LastRegionRequestMilliseconds = LastCoverageMilliseconds = LastPublicationMilliseconds = LastPublicationMaxStepMilliseconds = 0;
            UpdateStreaming();
            LastStreamingMilliseconds = DiagnosticMilliseconds(start);
        }

        void UpdateStreaming()
        {
            if (!streamStarted || LoadingError.Length > 0) return;
            UpdateFarEarth();
            if (Mathf.Abs(previousPresentationAltitude - altitude) > .02f || previousTerrainGlobe != NativeTerrainGlobe)
            {
                foreach (var m in presentationMaterials) SphericalMaterials.SetPresentationAltitude(m, altitude);
                // Keep the satellite fallback for close streaming. At globe
                // distances the optional terrain preview reuses this saved
                // coarse shell and the native near-art recipes, without asking
                // the streamer to build the whole world's detailed tiles.
                float nativeGlobe = NativeTerrainGlobe
                    ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(650, 1600, altitude)) : 0;
                coarseMaterial.SetFloat("_SatelliteBlend", 1 - nativeGlobe);
                waterMaterial.SetFloat("_SatelliteBlend", 1 - nativeGlobe);
                if (previewPipeline) previewPipeline.shadowDistance = Mathf.Clamp(altitude * 3 + 80, 180, 600);
                previousPresentationAltitude = altitude;
                previousTerrainGlobe = NativeTerrainGlobe;
            }
            bool far = altitude > 650;
            // Satellite views have no requested fine tiles. Rotating there
            // must not repeatedly rebase, clear and upload two empty 1024²
            // coverage maps (9 MiB per request). Refresh once at each distance
            // boundary; the first close view still requests its current focus.
            if (far != wasFar || (!far && ((focus - requestedFocus).magnitude * radius > 28 || Mathf.Abs(altitude - requestedAltitude) > 45)))
                RequestRegion();
            wasFar = far;
            if (appliedDetailFocus != focus || appliedDetailRadius != DetailRadius)
            {
                foreach (var material in coverageMaterials)
                {
                    material.SetVector("_DetailFocus", focus);
                    material.SetFloat("_DetailRadius", DetailRadius);
                    material.SetFloat("_DetailBlendWidth", 20);
                }
                appliedDetailFocus = focus; appliedDetailRadius = DetailRadius;
            }
            PublishCompleted();
            int running = 0;
            foreach (var chunk in chunks.Values) if (chunk.Work != null || chunk.Publication != null) running++;
            foreach (int key in orderedWanted)
            {
                if (running >= WorkerCount) break;
                var chunk = chunks[key]; if (chunk.Root || chunk.Work != null || chunk.Publication != null) continue;
                chunk.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var token = chunk.Cancellation.Token; int subdivisions = detailSubdivisions;
                chunk.Work = Task.Run(() => BuildChunk(chunk.Cells, subdivisions, token), token);
                running++; BuildCount++;
            }
            bool revealCompleted = false;
            DetailRevealing = false;
            foreach (int key in orderedWanted)
            {
                var chunk = chunks[key]; if (!chunk.Root) continue;
                SetChunkVisible(chunk, true);
                if (chunk.Fade < 1)
                {
                    chunk.Fade = Mathf.Min(1, chunk.Fade + Time.unscaledDeltaTime / DetailRevealSeconds);
                    coverageDirty = true;
                    revealCompleted |= chunk.Fade >= 1;
                    DetailRevealing |= chunk.Fade < 1;
                }
            }
            if (countsDirty) RefreshCounts();
            // Keep uploads throttled even after the last worker finishes;
            // a longer reveal must not upload both 1024² masks every frame.
            if (coverageDirty && (Time.unscaledTime >= coverageTime || revealCompleted))
            { UpdateCoverage(); coverageTime = Time.unscaledTime + .04f; }
            if (!DetailLoading && running == 0 && !buildingCells.IsEmpty)
            { buildingCells.Clear(); while (buildingCellOrder.TryDequeue(out _)) { } }
            EvictCache();
        }

        ChunkData BuildChunk(int[] core, int subdivisions, CancellationToken token)
        {
            var data = new ChunkData(); var guard = new HashSet<int>(core); var ownedCells = new HashSet<int>(core); var riverKeys = new HashSet<ulong>();
            foreach (int id in core) foreach (int neighbor in World.Neighbors[id]) guard.Add(neighbor);
            int cornerCount = 0;
            foreach (int id in guard) cornerCount = checked(cornerCount + World.Corners[id].Length);
            int vertexCount = checked(cornerCount * (subdivisions + 1) * (subdivisions + 2) / 2);
            int indexCount = checked(cornerCount * subdivisions * subdivisions * 3);
            token.ThrowIfCancellationRequested();
            data.Land.Reserve(vertexCount, indexCount);
            data.Water.Reserve(vertexCount, indexCount, water: true);
            foreach (int id in guard)
            {
                token.ThrowIfCancellationRequested();
                long key = ((long)id << 6) | (uint)subdivisions;
                var lazy = buildingCells.GetOrAdd(key, _ =>
                {
                    return new Lazy<CellGeometry>(() =>
                    {
                        buildingCellOrder.Enqueue(key);
                        var geometry = new CellGeometry(); BuildCellGeometry(geometry.Land, geometry.Water, id, subdivisions);
                        return geometry;
                    }, LazyThreadSafetyMode.ExecutionAndPublication);
                });
                var cached = lazy.Value;
                if (ownedCells.Contains(id)) AddShoreWaves(data.ShoreWaves, cached.Land, id);
                data.Land.Append(cached.Land); data.Water.Append(cached.Water);
                while (buildingCells.Count > 1024 && buildingCellOrder.TryDequeue(out long oldest)) buildingCells.TryRemove(oldest, out _);
            }
            SphericalTerraceNormals.Calculate(data.Land.Vertices, data.Land.Indices, data.Land.WeldKeys, data.Land.Normals,
                data.Land.PlateauHeights, data.Land.UV1, data.Land.Colors, Surface.PlateauStepHeight,
                data.Land.DuplicateTerraceVertex);
            // Both meshes share vertices. Interpolate the welded shore slope
            // instead of a different ddx/ddy denominator on every triangle.
            for (int vertex = 0; vertex < data.Water.Vertices.Count; vertex++)
            {
                Vector3 up = data.Water.Vertices[vertex].normalized;
                Vector3 normal = data.Land.Normals[vertex];
                float slope = Vector3.Cross(normal, up).magnitude / Mathf.Max(.08f, Vector3.Dot(normal, up));
                Vector4 coast = data.Water.UV1[vertex]; coast.z = Mathf.Clamp(slope, .08f, 8); coast.w = 1;
                data.Water.UV1[vertex] = coast;
            }
            foreach (int id in core)
            {
                token.ThrowIfCancellationRequested();
                AddGrid(data.Lines, id); AddRivers(data.Rivers, id, riverKeys, ref data.RiverCount);
                data.Trees.AddRange(vegetation.GenerateCell(id));
            }
            foreach (var buffer in new[] { data.Land, data.Water, data.Rivers, data.Lines, data.ShoreWaves })
            { data.ValidationErrors += ValidateBuffer(buffer); data.Bytes += BufferPayloadBytes(buffer); }
            // Water reads only UV0/1. Its two extra zero-filled float4 channels
            // and float32 white vertex colors previously displaced useful
            // terrain tiles from the bounded cache despite adding no detail.
            data.WaterChannelSavings = WaterChannelSavings(data.Water) + WaterChannelSavings(data.Rivers) + WaterChannelSavings(data.ShoreWaves);
            data.Bytes -= data.WaterChannelSavings;
            data.Bytes += data.Trees.Count * 80L;
            // Packet layout, indices, colors and bounds are prepared once on
            // this worker. The main-thread publication stages only upload them.
            token.ThrowIfCancellationRequested(); data.Land.PrepareUpload();
            token.ThrowIfCancellationRequested(); data.Water.PrepareUpload(water: true);
            token.ThrowIfCancellationRequested(); data.Rivers.PrepareUpload(water: true);
            token.ThrowIfCancellationRequested(); data.ShoreWaves.PrepareUpload(water: true);
            token.ThrowIfCancellationRequested(); data.Lines.PrepareUpload(lines: true);
            PrepareTreeBatches(data, token);
            token.ThrowIfCancellationRequested();
            return data;
        }

        static long WaterChannelSavings(MeshBuffer data) => 44L * data.Vertices.Count;

        static Mesh CreateWaterMesh(MeshBuffer data, string name) => data.CreateWater(name);

        void PublishCompleted()
        {
            long diagnosticStart = DiagnosticTimestamp();
            double deadline = diagnosticStart + System.Diagnostics.Stopwatch.Frequency * .003;
            // One deadline is shared by every tile. A native mesh upload is
            // indivisible, but it never brings the rest of that tile with it.
            // Work resumes at the next mesh/object/upload/tree-batch step.
            try
            {
                int steps = 0;
                while (steps++ < 512 && DiagnosticTimestamp() < deadline)
                {
                    Chunk chunk = NextPublicationChunk();
                    if (chunk == null) break;
                    long stepStart = DiagnosticTimestamp();
                    try
                    {
                        if (chunk.Publication == null) BeginChunkPublication(chunk);
                        else AdvanceChunkPublication(chunk);
                    }
                    catch (Exception error)
                    {
                        DisposePublication(chunk);
                        FailLoading("Tile " + chunk.Key + " publication failed: " + error);
                        return;
                    }
                    finally
                    {
                        PublicationStepCount++;
                        LastPublicationMaxStepMilliseconds = Math.Max(LastPublicationMaxStepMilliseconds, DiagnosticMilliseconds(stepStart));
                    }
                }
            }
            finally { LastPublicationMilliseconds += DiagnosticMilliseconds(diagnosticStart); }
        }

        Chunk NextPublicationChunk()
        {
            // Prioritize currently requested ground before finishing an old
            // hidden cache tile. Neither queue selection nor polling blocks.
            foreach (int key in orderedWanted)
            {
                Chunk chunk = chunks[key];
                if (chunk.Publication != null || (chunk.Work != null && chunk.Work.IsCompleted)) return chunk;
            }
            foreach (Chunk chunk in chunks.Values)
                if (chunk.Publication != null || (chunk.Work != null && chunk.Work.IsCompleted)) return chunk;
            return null;
        }

        void BeginChunkPublication(Chunk chunk)
        {
            Task<ChunkData> work = chunk.Work;
            chunk.Work = null;
            chunk.Cancellation?.Dispose(); chunk.Cancellation = null;
            if (work.IsCanceled) return;
            if (work.IsFaulted) throw work.Exception.GetBaseException();
            ChunkData data = work.Result;
            GeometryValidationErrors += data.ValidationErrors;
            if (data.ValidationErrors != 0) throw new InvalidOperationException("Generated invalid geometry.");
            chunk.Publication = new ChunkPublication { Data = data };
            // Include reserved native mesh payload while an incomplete tile is
            // queued, so obsolete half-built tiles remain eligible for eviction.
            chunk.Bytes = data.Bytes; chunk.WaterChannelSavings = data.WaterChannelSavings;
        }

        void AdvanceChunkPublication(Chunk chunk)
        {
            ChunkPublication publication = chunk.Publication;
            ChunkData data = publication.Data;
            if (publication.Step == 0)
            {
                publication.Root = new GameObject($"Spherical terrain tile {chunk.Key}");
                publication.Root.SetActive(false);
                publication.Root.transform.SetParent(transform, false);
                publication.Step = 1;
                return;
            }
            if (publication.Step <= 15)
            {
                int index = (publication.Step - 1) / 3, operation = (publication.Step - 1) % 3;
                MeshBuffer buffer = index == 0 ? data.Land : index == 1 ? data.Water : index == 2 ? data.ShoreWaves : index == 3 ? data.Lines : data.Rivers;
                if ((index == 2 || index == 4) && buffer.Indices.Count == 0)
                { publication.Step += 3; return; }
                if (operation == 0)
                {
                    // Managed packets and bounds were prepared in BuildChunk.
                    // Store every mesh immediately, including before it has a
                    // MeshFilter, so cancellation can dispose each exactly once.
                    publication.Meshes[index] = index == 0 ? buffer.Create("Spherical relief tile") :
                        index == 3 ? buffer.Create("Actual polygon edges", true) :
                        CreateWaterMesh(buffer, index == 1 ? "Spherical coast tile" : index == 2 ? "Spherical shore crest strips" : "Spherical river tile");
                }
                else if (operation == 1)
                {
                    string name = index == 0 ? "Spherical relief" : index == 1 ? "Fine ocean" : index == 2 ? "Source shoreline crests" :
                        index == 3 ? "Actual hexagon and pentagon edges" : "Curved shared rivers";
                    Material material = index == 0 ? detailMaterial : index == 1 ? fineWaterMaterial : index == 2 ? shoreWaveMaterial : index == 3 ? gridMaterial : riverMaterial;
                    GameObject child = CreateMeshObject(name, publication.Meshes[index], material, publication.Root.transform);
                    var renderer = child.GetComponent<MeshRenderer>(); publication.Renderers[index] = renderer;
                    if (index == 1 || index == 2 || index == 4) renderer.shadowCastingMode = ShadowCastingMode.Off;
                    if (index == 1)
                    {
                        var properties = new MaterialPropertyBlock();
                        properties.SetFloat("_OceanTileOwner", chunk.Key + 1); renderer.SetPropertyBlock(properties);
                    }
                    if (index == 3) publication.Grid = child;
                }
                else
                {
                    Mesh mesh = publication.Meshes[index];
                    publication.UploadedVertices += mesh.vertexCount;
                    mesh.UploadMeshData(true);
                }
                publication.Step++;
                return;
            }
            if (publication.TreeCursor < data.TreeBatches.Count)
            {
                TreeBatch batch = data.TreeBatches[publication.TreeCursor++];
                batch.Properties = new MaterialPropertyBlock();
                chunk.Trees.Add(batch);
                return;
            }
            // The only ready/publication point. Coverage and counts can expose
            // this tile only after every mesh, upload and tree batch is ready.
            chunk.Grid = publication.Grid;
            if (chunk.Grid) chunk.Grid.SetActive(gridVisible);
            chunk.Renderers = publication.Renderers;
            chunk.Root = publication.Root;
            chunk.Triangles = data.Land.Indices.Count / 3; chunk.Rivers = data.RiverCount;
            chunk.TreeCount = data.Trees.Count;
            if (wanted.Contains(chunk.Key)) chunk.LastUsed = lifetimeClock.Elapsed.TotalSeconds;
            SetChunkVisible(chunk, wanted.Contains(chunk.Key));
            chunk.Root.SetActive(true);
            chunk.Publication = null;
            PublishedVertexCount += publication.UploadedVertices;
            CompletedPublicationCount++;
            coverageDirty = countsDirty = true;
        }

        static void SetChunkVisible(Chunk chunk, bool visible)
        {
            if (!chunk.Root || (chunk.VisibilityAssigned && chunk.Visible == visible)) return;
            chunk.Visible = visible; chunk.VisibilityAssigned = true;
            if (chunk.Renderers != null) foreach (Renderer renderer in chunk.Renderers)
                if (renderer) renderer.forceRenderingOff = !visible;
            // Do not deactivate a mature hierarchy or reset Fade. Returning to
            // a fully revealed cached tile must not trigger another fade/upload.
        }

        void PrepareTreeBatches(ChunkData data, CancellationToken token)
        {
            // Species bounds and instance matrices are immutable snapshots.
            // No Mesh, Material or MaterialPropertyBlock is accessed here.
            var groups = new Dictionary<int, List<SphericalVegetationField.Instance>>();
            foreach (var instance in data.Trees)
            {
                if (!groups.TryGetValue(instance.SpeciesIndex, out var list)) groups.Add(instance.SpeciesIndex, list = new());
                list.Add(instance);
            }
            foreach (var pair in groups)
            {
                Bounds sourceBounds = vegetation.Species[pair.Key].Bounds;
                Vector3 sourceRoot = new(sourceBounds.center.x, sourceBounds.min.y, sourceBounds.center.z);
                // Spatially small batches preserve frustum/distance culling.
                for (int offset = 0; offset < pair.Value.Count; offset += 256)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Mathf.Min(256, pair.Value.Count - offset);
                    var matrices = new Matrix4x4[count]; var tints = new Vector4[count];
                    var roots = new Vector3[count]; var thresholds = new float[count];
                    Bounds bounds = new((Vector3)pair.Value[offset].Matrix.GetColumn(3), Vector3.one * 12);
                    for (int i = 0; i < count; i++)
                    {
                        var instance = pair.Value[offset + i]; matrices[i] = instance.Matrix; tints[i] = instance.Tint;
                        // Matrices already contain the source mesh pivot. Undo
                        // that local offset when locating the planted root.
                        Vector3 root = instance.Matrix.MultiplyPoint3x4(sourceRoot);
                        roots[i] = root.normalized; thresholds[i] = TreeBatch.RevealThreshold(root);
                        bounds.Encapsulate(root);
                    }
                    bounds.Expand(16);
                    data.TreeBatches.Add(new TreeBatch { Species = pair.Key, Matrices = matrices, Tints = tints,
                        RootDirections = roots, RevealThresholds = thresholds,
                        DrawMatrices = new Matrix4x4[count], DrawTints = new Vector4[count],
                        Bounds = bounds });
                }
            }
        }

        void RefreshCounts()
        {
            countsDirty = false;
            ReadyChunks = 0; PendingChunks = 0; TreeCount = 0; RiverSegmentCount = 0; TerrainTriangleCount = 0;
            activeIds.Clear();
            foreach (int key in orderedWanted)
            {
                var chunk = chunks[key];
                if (!chunk.Root) { PendingChunks++; continue; }
                ReadyChunks++; activeIds.AddRange(chunk.Cells); TreeCount += chunk.TreeCount;
                RiverSegmentCount += chunk.Rivers; TerrainTriangleCount += chunk.Triangles;
            }
            VisibleDetailCells = activeIds.Count; DetailLoading = PendingChunks > 0;
            double elapsed = lifetimeClock.Elapsed.TotalSeconds - requestStarted;
            if (ReadyChunks > 0 && FirstVisibleSeconds < 0) FirstVisibleSeconds = elapsed;
            if (!DetailLoading && LastLoadSeconds < 0) LastLoadSeconds = elapsed;
            IsReady = IsReady || (shell && (ReadyChunks > 0 || altitude > 650));
            if (IsReady && StartupSeconds < 0) StartupSeconds = lifetimeClock.Elapsed.TotalSeconds;
            Status = DetailLoading ? $"Terrain {ReadyChunks}/{orderedWanted.Count} chunks • {WorkerCount} workers" : "Ready • cached spherical terrain";
        }

        void MoveCoverageFrame(Vector3 n)
        {
            coverageFocus = n.normalized; SphericalSurface.Frame(coverageFocus, out coverageEast, out coverageNorth);
            // UpdateCoverage clears and publishes below in this same call.
            // Avoid clearing both CPU buffers twice during every near rebase.
            coverage.SetFrame(coverageFocus, coverageEast, coverageNorth, false); coverageDirty = true;
            // Texture and tangent basis are one publication. A stale mask in a
            // new basis would briefly remove the fallback under an unloaded tile.
            UpdateCoverage();
            foreach (var m in coverageMaterials)
                SphericalMaterials.SetDetailCoverage(m, coverage.Texture, coverageFocus, coverageEast, coverageNorth, 1024, coverage.OwnerTexture);
        }
        void UpdateCoverage()
        {
            long diagnosticStart = DiagnosticTimestamp();
            // Workers can finish after zooming out; their hidden cache entry
            // must not cause another upload of an already empty coverage map.
            if (altitude > 650 && !coverageHasDetail)
            {
                coverageDirty = false;
                LastCoverageMilliseconds += DiagnosticMilliseconds(diagnosticStart);
                return;
            }
            coverage.BeginFrame();
            coverageHasDetail = false;
            if (altitude <= 650) foreach (int key in orderedWanted)
            {
                var chunk = chunks[key]; if (!chunk.Root) continue;
                foreach (int id in chunk.Cells)
                {
                    float distance = (World.Centers[id] - focus).magnitude * radius;
                    // Retain enough ready coverage for small camera motion
                    // between region requests; the shader computes a smooth
                    // circular shoulder per pixel instead of per-cell steps.
                    if (distance > DetailRadius + 48) continue;
                    byte value = (byte)Mathf.RoundToInt(chunk.Reveal * 255);
                    if (value > 0)
                    {
                        coverageHasDetail = true;
                        coverage.AddCell(World.Centers[id], World.Corners[id], radius, value, chunk.Key);
                    }
                }
            }
            coverage.Upload(); coverageDirty = false;
            CoverageUploadCount++;
            CoverageUploadBytes += (long)coverage.Size * coverage.Size * 9;
            LastCoverageMilliseconds += DiagnosticMilliseconds(diagnosticStart);
        }

        void EvictCache()
        {
            long bytes = 0; foreach (var chunk in chunks.Values) bytes += chunk.Bytes;
            while (chunks.Count > MaxCachedChunks || bytes > MeshCacheBudgetBytes)
            {
                Chunk oldest = null;
                foreach (var chunk in chunks.Values)
                    if (!wanted.Contains(chunk.Key) && chunk.Work == null && (oldest == null || chunk.LastUsed < oldest.LastUsed)) oldest = chunk;
                if (oldest == null) break;
                bytes -= oldest.Bytes;
                DisposePublication(oldest); DisposeHierarchy(oldest.Root); oldest.Trees.Clear();
                chunks.Remove(oldest.Key);
            }
        }

        static void DisposePublication(Chunk chunk)
        {
            ChunkPublication publication = chunk.Publication;
            if (publication == null) return;
            // Some meshes exist before their MeshFilter; own the explicit mesh
            // array rather than traversing an incomplete transform hierarchy.
            foreach (Mesh mesh in publication.Meshes) if (mesh) Destroy(mesh);
            if (publication.Root) Destroy(publication.Root);
            chunk.Publication = null; chunk.Root = chunk.Grid = null;
            chunk.Renderers = null; chunk.Trees.Clear();
            chunk.Visible = chunk.VisibilityAssigned = false;
            chunk.Bytes = chunk.WaterChannelSavings = 0;
        }

        void LateUpdate() => RenderVegetation(Camera.main);
        public void RenderVegetation(Camera view)
        {
            long start = DiagnosticTimestamp();
            RenderVegetationCore(view);
            LastVegetationMilliseconds = DiagnosticMilliseconds(start);
        }

        void RenderVegetationCore(Camera view)
        {
            if (!streamStarted || !view || altitude > 570) return;
            GeometryUtility.CalculateFrustumPlanes(view, vegetationPlanes);
            Light sun = RenderSettings.sun;
            var pipeline = GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
            float shadowDistance = pipeline ? Mathf.Min(vegetation.ShadowDistance, pipeline.shadowDistance) : 0;
            bool canCast = sun && sun.enabled && sun.shadows != LightShadows.None && sun.shadowStrength > 0 &&
                pipeline && pipeline.supportsMainLightShadows && (sun.cullingMask & (1 << gameObject.layer)) != 0;
            if ((view.cullingMask & (1 << gameObject.layer)) == 0) return;
            foreach (int key in orderedWanted)
            {
                var chunk = chunks[key]; if (!chunk.Root || !chunk.Visible) continue;
                foreach (var batch in chunk.Trees)
                {
                    // Match the flat renderer's nearest-bounds LOD selection.
                    // A large instanced grove must not lose its foreground mesh
                    // merely because its batch center is farther than the trees.
                    float distance = Vector3.Distance(view.transform.position, batch.Bounds.ClosestPoint(view.transform.position));
                    if (distance > vegetation.DrawDistance) continue;
                    bool visible = GeometryUtility.TestPlanesAABB(vegetationPlanes, batch.Bounds);
                    bool shadows = canCast && distance <= shadowDistance;
                    if (!visible && (!shadows || !GeometryUtility.TestPlanesAABB(vegetationPlanes,
                        VegetationShadowReceiverBounds(batch.Bounds, sun.transform.forward, radius, shadowDistance)))) continue;
                    var species = vegetation.Species[batch.Species];
                    Mesh mesh = distance > vegetation.LodDistance ? species.LodMesh : species.Mesh;
                    int count = batch.SelectVisible(focus, radius, DetailRadius,
                        chunk.Reveal * (1 - SphericalSurface.Smooth(420, 570, altitude)));
                    if (count == 0) continue;
                    batch.Properties.SetVectorArray("_HexNearInstanceTint", batch.DrawTints);
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        var source = species.Materials[Mathf.Min(sub, species.Materials.Length - 1)]; if (!source) continue;
                        if (!treeMaterials.TryGetValue(source, out var material))
                        { material = Own(SphericalMaterials.CreateVegetation(source)); treeMaterials.Add(source, material); }
                        Graphics.DrawMeshInstanced(mesh, sub, material, batch.DrawMatrices, count,
                            batch.Properties, !visible ? ShadowCastingMode.ShadowsOnly : shadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            true, gameObject.layer, view, LightProbeUsage.Off);
                    }
                }
            }
        }

        static Bounds VegetationShadowReceiverBounds(Bounds caster, Vector3 lightDirection, float planetRadius, float shadowDistance)
        {
            Vector3 radial = caster.center.normalized;
            float maximumTravel = shadowDistance * 2 + caster.size.magnitude;
            float descent = -Vector3.Dot(lightDirection, radial);
            if (descent > .0001f)
            {
                Vector3 extent = caster.extents;
                float highest = Vector3.Dot(caster.center, radial) +
                    Mathf.Abs(radial.x) * extent.x + Mathf.Abs(radial.y) * extent.y + Mathf.Abs(radial.z) * extent.z;
                // Conservative local receiver plane below the coast. Account
                // for spherical sag over the bounded projection distance so
                // a lit tree just outside the view can still shadow the ground.
                float sag = maximumTravel * maximumTravel / Mathf.Max(planetRadius * 2, 1);
                maximumTravel = Mathf.Min(maximumTravel, Mathf.Max(0, highest - planetRadius + 5 + sag) / descent);
            }
            Vector3 offset = lightDirection * maximumTravel;
            Bounds receivers = caster;
            receivers.Encapsulate(caster.min + offset);
            receivers.Encapsulate(caster.max + offset);
            return receivers;
        }
        void FailLoading(string error)
        { LoadingError = error; DetailLoading = false; Status = "Terrain generation failed: " + error; Debug.LogError(Status, this); }
        static void ObserveStartupFault(Task task)
        {
            // Explicit scheduler and a numeric exception read: no Unity access
            // or synchronization-context dependency after the scene is destroyed.
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        void StopStreaming()
        {
            lifetime.Cancel();
            // Fault observers were attached at launch, so detaching does not
            // leave unobserved tasks. Never block scene teardown waiting on CPU jobs.
            startupWorldTask = null; startupIndexTask = null; startupShellTask = null;
            foreach (var chunk in chunks.Values)
            {
                chunk.Cancellation?.Cancel();
                if (chunk.Work != null) ObserveStartupFault(chunk.Work);
                chunk.Cancellation?.Dispose(); chunk.Cancellation = null; chunk.Work = null;
                DisposePublication(chunk);
                DisposeHierarchy(chunk.Root);
                chunk.Trees.Clear();
            }
            chunks.Clear(); buildingCells.Clear(); while (buildingCellOrder.TryDequeue(out _)) { }
            coverage?.Dispose();
        }
    }
}
