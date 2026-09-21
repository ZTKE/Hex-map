using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZTKE.HexMap.Standalone
{
    /// <summary>Live native sphere state. Geographic rasters accelerate shading only;
    /// exact polygon planes and authoritative native IDs own picking and overlays.</summary>
    public sealed partial class SphericalGameplayPolitics : IDisposable
    {
        const int FieldWidth = 4096, FieldHeight = 2048, SeedWidth = 2048, SeedHeight = 1024;
        const int UploadRows = 32, StateBatch = 131072, EdgeBatch = 131072;
        const double StartupUploadBudgetMilliseconds = 8;
        const int MaximumStartupTransfers = 32;
        struct CellState { public uint CountryLand, Overlay, Occupation, Degree; }
        Color32[] countryColors;
        NativeGameplayMap map;
        WW2.SphericalTerrainPreview.SphericalTerrainPreview terrain;
        ComputeBuffer nativeEdges, nativeState, nativeBuildSelection;
        uint[] buildSelectionGpu;
        bool buildSelectionDirty;
        CellState[] states;
        readonly SortedSet<int> dirtyState = new SortedSet<int>();
        readonly Vector4[] selectionPlanes = new Vector4[6];
        Texture2D palette;
        Color32[] palettePixels;
        RenderTexture seedTexture, politicalField;
        TextureUpload seedUpload, fieldUpload;
        Task<TopologyRaster> topologyWork;
        Task<FieldResult> fieldWork;
        TopologyRaster topology;
        CancellationTokenSource cancellation;
        int stateCursor, edgeCursor, planeSelection = int.MinValue;
        int overlayCells, occupationCells;
        uint fieldRequestedRevision = uint.MaxValue;
        bool bound;
        long initializationStarted;
        public uint PoliticalRevision { get; private set; } = uint.MaxValue;
        public uint PaletteRevision { get; private set; } = uint.MaxValue;
        public uint NaturalFieldRevision { get; private set; } = uint.MaxValue;
        public int SelectedTile { get; private set; } = -1;
        public uint SelectedCountry { get; private set; }
        public uint HoveredCountry { get; private set; }
        public int OwnershipUploads { get; private set; }
        public int PaletteUploads { get; private set; }
        public int FieldUploads { get; private set; }
        public int IncrementalStateUploads { get; private set; }
        public bool HasOccupationTexture => nativeState != null;
        public bool HasOverlayTexture => nativeState != null;
        public Texture CountryTexture => politicalField;
        public Texture2D PaletteTexture => palette;
        public bool IsInitialized => bound;
        public string LoadingError { get; private set; }
        public bool StartupDataLoadedFromDisk { get; private set; }
        public double StartupPreparationMilliseconds { get; private set; }
        public double InitializationMilliseconds { get; private set; }
        public double StartupUploadMilliseconds { get; private set; }
        public double StartupStateUploadMilliseconds { get; private set; }
        public double StartupEdgeUploadMilliseconds { get; private set; }
        public double StartupTextureUploadMilliseconds { get; private set; }
        public int StartupUploadSteps { get; private set; }
        public int StartupTextureUploadSteps { get; private set; }
        public long EdgeBufferBytes => nativeEdges == null ? 0 : (long)nativeEdges.count * nativeEdges.stride;

        public void Initialize(WW2.SphericalTerrainPreview.SphericalTerrainPreview target, NativeGameplayMap source, Color32[] colors)
        {
            if (!target || source == null || colors == null)
                throw new ArgumentException("Ready native gameplay topology and spherical terrain are required.");
            Dispose(); terrain = target; countryColors = colors; map = source;
            initializationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (map.Count != target.World.Count) throw new InvalidOperationException("Terrain and gameplay must share native sphere IDs.");
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            states = new CellState[map.Count];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = ReadState(i);
                if ((states[i].Overlay >> 24) != 0) overlayCells++;
                if ((states[i].Occupation >> 24) != 0) occupationCells++;
            }
            PoliticalRevision = map.PoliticalRevision;
            nativeState = new ComputeBuffer(map.Count, 16, ComputeBufferType.Structured);
            nativeEdges = new ComputeBuffer(map.Count * 6, 16, ComputeBufferType.Structured);
            buildSelectionGpu = new uint[map.Count];
            for (int i = 0; i < map.Count; i++)
            {
                buildSelectionGpu[i] = map.BuildSelection[i];
            }

            nativeBuildSelection = new ComputeBuffer(map.Count, 4, ComputeBufferType.Structured);
            nativeBuildSelection.SetData(buildSelectionGpu);
            buildSelectionDirty = false;
            map.CountryChanged += CountryChanged; map.OverlayChanged += StateChanged; map.OccupationChanged += StateChanged; map.BuildSelectionChanged += StateChanged;
            var world = target.World;
            ushort[] owners = (ushort[])map.Countries.Clone();
            string bundledPath = System.IO.Path.Combine(Application.streamingAssetsPath, "SphericalMap", StartupDataFileName);
            string localPath = System.IO.Path.Combine(Application.persistentDataPath, "SphericalMap", StartupDataFileName);
            fieldRequestedRevision = map.PoliticalRevision;
            topologyWork = Task.Run(() => LoadOrBuildStartupData(world, owners, bundledPath, localPath, token), token);
            ObserveBackgroundFailure(topologyWork);
            RefreshPalette();
        }

        CellState ReadState(int id)
        {
            byte buildSelection = map.BuildSelection[id];
            if (buildSelectionGpu != null && (uint)id < (uint)buildSelectionGpu.Length)
            {
                buildSelectionGpu[id] = buildSelection;
                buildSelectionDirty = true;
            }

            return new CellState
            {
                CountryLand = map.Countries[id]
                    | (map.Tiles[id].Terrain > 0 ? 0x00ff0000u : 0u)
                    | (CountryBorderMask(id) << 24),
                Overlay = Pack(map.SelectionOverlay[id].a > 0 ? map.SelectionOverlay[id] : map.Overlay[id]),
                Occupation = Pack(map.Occupation[id]),
                Degree = (uint)map.Neighbors[id].Length,
            };
        }

        static uint Pack(Color32 c) => (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
        void StateChanged(int id) { if (map != null && map.Valid(id)) dirtyState.Add(id); }
        uint CountryBorderMask(int id)
        {
            uint mask = 0; ushort country = map.Countries[id];
            if (country == 0 || map.Tiles[id].Terrain <= 0) return 0;
            var neighbors = map.Neighbors[id];
            for (int e = 0; e < neighbors.Length; e++)
            {
                int other = neighbors[e]; ushort owner = map.Countries[other];
                if (owner != 0 && owner != country && map.Tiles[other].Terrain > 0) mask |= 1u << e;
            }
            return mask;
        }
        void CountryChanged(int id)
        {
            StateChanged(id);
            if (map == null || !map.Valid(id)) return;
            foreach (int other in map.Neighbors[id]) StateChanged(other);
        }

        public void UpdatePresentation(float altitude, int selectedTile)
        {
            if (map == null || !terrain || cancellation == null || cancellation.IsCancellationRequested) return;
            
            // Keep each transfer small, but do not impose hundreds of mandatory
            // rendered frames on the loading screen. Stop after 8 ms of CPU work
            // or 32 transfers; live political updates remain one step per frame.
            long uploadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            bool initializing = !bound;
            int transfers = bound ? 1 : MaximumStartupTransfers;
            do
            {
                if (stateCursor < states.Length)
                {
                    int count = Math.Min(StateBatch, states.Length - stateCursor);
                    long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    nativeState.SetData(states, stateCursor, stateCursor, count); stateCursor += count;
                    StartupStateUploadMilliseconds += ElapsedMilliseconds(stepStarted); StartupUploadSteps++;
                }
                else if (!AdvanceBackgroundPresentation()) break;
            }
            while (--transfers > 0 && !bound && ElapsedMilliseconds(uploadStarted) < StartupUploadBudgetMilliseconds);
            if (initializing) StartupUploadMilliseconds += ElapsedMilliseconds(uploadStarted);
            FlushChangedStates();
            if (PoliticalRevision != map.PoliticalRevision)
            { PoliticalRevision = map.PoliticalRevision; OwnershipUploads++; }
            SelectedTile = map.Valid(selectedTile) ? selectedTile : map.SelectedTile;
            SelectedCountry = map.Valid(SelectedTile) ? map.Countries[SelectedTile] : 0u;
            HoveredCountry = 0;
            if (planeSelection != SelectedTile)
            {
                planeSelection = SelectedTile;
                Array.Clear(selectionPlanes, 0, selectionPlanes.Length);
                if (map.Valid(SelectedTile))
                {
                    var corners = map.Corners[SelectedTile];
                    for (int i = 0; i < corners.Length; i++)
                    { Vector3 plane = InwardPlane(corners[i], corners[(i + 1) % corners.Length]);
                        selectionPlanes[i] = new Vector4(plane.x, plane.y, plane.z, 1); }
                }
                terrain.SetNativeSelectionPlanes(selectionPlanes);
            }
            FlushBuildSelectionBufferIfNeeded();
            if (bound)
            {
                terrain.SetGameplayPolitics(nativeEdges, nativeState, seedTexture, politicalField, palette,
                    new Vector4(FieldWidth, FieldHeight, map.Count, map.Radius),
                    new Vector4(SelectedCountry, HoveredCountry, SelectedTile, 0), altitude, overlayCells > 0, occupationCells > 0);
                terrain.SetGameplayBuildSelection(nativeBuildSelection);
            }
        }

        void FlushBuildSelectionBufferIfNeeded()
        {
            if (!buildSelectionDirty || nativeBuildSelection == null || buildSelectionGpu == null)
            {
                return;
            }

            nativeBuildSelection.SetData(buildSelectionGpu);
            buildSelectionDirty = false;
        }

        void FlushChangedStates()
        {
            // Typical movement/selection changes touch tens of cells, never a
            // scan over the 590k-cell globe. Large orders drain in bounded batches.
            int remaining = 4096, transfers = 16;
            while (dirtyState.Count > 0 && remaining > 0 && transfers-- > 0)
            {
                int first = dirtyState.Min, last = first;
                do
                {
                    int id = dirtyState.Min; dirtyState.Remove(id); last = id;
                    CellState updated = ReadState(id), previous = states[id];
                    overlayCells += ((updated.Overlay >> 24) != 0 ? 1 : 0) - ((previous.Overlay >> 24) != 0 ? 1 : 0);
                    occupationCells += ((updated.Occupation >> 24) != 0 ? 1 : 0) - ((previous.Occupation >> 24) != 0 ? 1 : 0);
                    states[id] = updated; remaining--;
                }
                while (dirtyState.Count > 0 && remaining > 0 && dirtyState.Min <= last + 16 && dirtyState.Min - first < 4096);
                int publishedCount = Math.Min(last + 1, stateCursor) - first;
                if (publishedCount > 0) { nativeState.SetData(states, first, first, publishedCount); IncrementalStateUploads++; }
            }
        }

        bool AdvanceBackgroundPresentation()
        {
            if (topology == null)
            {
                if (topologyWork == null || !topologyWork.IsCompleted) return false;
                if (!TryResult(topologyWork, out topology)) return false;
                topologyWork = null;
                StartupDataLoadedFromDisk = topology.LoadedFromDisk;
                StartupPreparationMilliseconds = topology.PreparationMilliseconds;
                if (!string.IsNullOrEmpty(topology.CacheWarning)) Debug.LogWarning(topology.CacheWarning);
                seedUpload = new TextureUpload(SeedWidth, SeedHeight, topology.Seed, "Native sphere lookup seeds", 4);
                fieldUpload = new TextureUpload(FieldWidth, FieldHeight, topology.InitialField.Pixels, "Native natural satellite country field", 4);
                topology.InitialField = null;
            }
            if (edgeCursor < map.Count * 6)
            {
                int count = Math.Min(EdgeBatch, map.Count * 6 - edgeCursor);
                long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                nativeEdges.SetData(topology.Edges, edgeCursor, edgeCursor, count); edgeCursor += count;
                StartupEdgeUploadMilliseconds += ElapsedMilliseconds(stepStarted); StartupUploadSteps++;
                if (edgeCursor == map.Count * 6) topology.Edges = null;
                return true;
            }
            if (seedUpload != null)
            {
                long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                if (seedUpload.Step()) { seedTexture = seedUpload.Take(); seedUpload.Dispose(); seedUpload = null; topology.Seed = null; }
                StartupTextureUploadMilliseconds += ElapsedMilliseconds(stepStarted); StartupTextureUploadSteps++; StartupUploadSteps++;
                return true;
            }
            if (fieldUpload != null)
            {
                long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp(); bool initializing = !bound;
                if (fieldUpload.Step())
                {
                    RenderTexture previous = politicalField; politicalField = fieldUpload.Take();
                    fieldUpload.Dispose(); fieldUpload = null; NaturalFieldRevision = fieldRequestedRevision;
                    if (previous) UnityEngine.Object.Destroy(previous);
                    FieldUploads++;
                    if (!bound) InitializationMilliseconds = ElapsedMilliseconds(initializationStarted);
                    bound = true;
                }
                if (initializing)
                { StartupTextureUploadMilliseconds += ElapsedMilliseconds(stepStarted); StartupTextureUploadSteps++; StartupUploadSteps++; }
                return true;
            }
            if (fieldWork != null)
            {
                if (!fieldWork.IsCompleted) return false;
                if (!TryResult(fieldWork, out FieldResult result)) return false;
                fieldWork = null;
                fieldUpload = new TextureUpload(FieldWidth, FieldHeight, result.Pixels, "Native natural satellite country field");
                return true;
            }
            if (NaturalFieldRevision != map.PoliticalRevision)
            {
                fieldRequestedRevision = map.PoliticalRevision;
                ushort[] owners = (ushort[])map.Countries.Clone();
                var raster = topology; var token = cancellation.Token;
                fieldWork = Task.Run(() => BuildField(raster, owners, token), token);
                ObserveBackgroundFailure(fieldWork);
            }
            return false;
        }

        static double ElapsedMilliseconds(long since) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        static void ObserveBackgroundFailure(Task task)
        {
            // Disposal may release the task before a racing worker fails. The
            // active owner still reports failures in TryResult on the main
            // thread; the continuation only observes abandoned exceptions.
            task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        bool TryResult<T>(Task<T> task, out T result)
        {
            result = default;
            if (task.IsCanceled) return false;
            if (task.IsFaulted)
            {
                LoadingError = task.Exception.GetBaseException().Message;
                cancellation.Cancel(); Debug.LogException(task.Exception.GetBaseException()); return false;
            }
            result = task.Result; return true;
        }

        void RefreshPalette()
        {
            if (!palette)
            {
                palette = new Texture2D(256, 256, TextureFormat.RGBA32, false, true)
                { name = "Native sphere — live ushort country palette", hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                palettePixels = new Color32[65536];
            }
            for (int country = 0; country < palettePixels.Length; country++)
                palettePixels[country] = country < countryColors.Length ? countryColors[country] : default;
            palette.SetPixels32(palettePixels); palette.Apply(false, false);
            PaletteRevision = 0; PaletteUploads++;
        }

        public ushort UploadedCountryAt(int nativeTile) => states != null && (uint)nativeTile < (uint)states.Length
            ? (ushort)(states[nativeTile].CountryLand & 65535) : (ushort)0;

        public byte UploadedBuildSelectionAt(int nativeTile) => buildSelectionGpu != null && (uint)nativeTile < (uint)buildSelectionGpu.Length
            ? (byte)buildSelectionGpu[nativeTile] : (byte)0;

        public void Dispose()
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            if (map != null) { map.CountryChanged -= CountryChanged; map.OverlayChanged -= StateChanged; map.OccupationChanged -= StateChanged; map.BuildSelectionChanged -= StateChanged; }
            if (terrain) terrain.ClearGameplayPolitics();
            nativeEdges?.Dispose(); nativeState?.Dispose(); nativeBuildSelection?.Dispose();
            nativeEdges = nativeState = nativeBuildSelection = null;
            buildSelectionGpu = null; buildSelectionDirty = false;
            seedUpload?.Dispose(); fieldUpload?.Dispose(); seedUpload = fieldUpload = null;
            if (seedTexture) UnityEngine.Object.Destroy(seedTexture);
            if (politicalField) UnityEngine.Object.Destroy(politicalField);
            if (palette) UnityEngine.Object.Destroy(palette);
            seedTexture = politicalField = null; palette = null; palettePixels = null;
            topology = null; topologyWork = null; fieldWork = null; states = null; map = null; countryColors = null; terrain = null;
            bound = false; stateCursor = edgeCursor = overlayCells = occupationCells = 0; dirtyState.Clear(); SelectedTile = -1; planeSelection = int.MinValue;
            PoliticalRevision = PaletteRevision = NaturalFieldRevision = uint.MaxValue; LoadingError = null;
            StartupDataLoadedFromDisk = false; StartupPreparationMilliseconds = InitializationMilliseconds = 0;
            StartupUploadMilliseconds = StartupStateUploadMilliseconds = StartupEdgeUploadMilliseconds = StartupTextureUploadMilliseconds = 0;
            StartupUploadSteps = StartupTextureUploadSteps = 0;
        }
    }
}
