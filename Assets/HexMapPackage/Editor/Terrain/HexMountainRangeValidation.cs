using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Exercises fixed mountain topologies on the disposable showcase. No map data,
/// style asset or saved world is mutated. The strength-zero clone is the old
/// independent-stamp baseline, while GPU samples use the actual relief shader.
/// </summary>
public static class HexMountainRangeValidation
{
	[Serializable] public sealed class CaseResult
	{
		public string name;
		public int mountainCells, connectedEdges, samples, directionMask;
		public float minimumCorridorHeight, maximumCorridorLift, maximumPointLift;
		public float maximumCpuGpuError, maximumCpuSeamGap, maximumGpuSeamGap;
		public float maximumIsolatedChange, maximumHeightReduction;
		public int crossSections, mandatoryCrossSections, failedCrossSections;
		public int observedCpuHighEdges, observedGpuHighEdges;
		public float minimumCpuCrestRatio, minimumGpuCrestRatio, minimumCpuBodyWidth, minimumGpuBodyWidth;
	}
	[Serializable] public sealed class CrossSectionResult
	{
		public string fixture;
		public int fromCell, toCell;
		public float along, referencePeakHeight, diagnosticHighCrestRatio = .65f, requiredBodyWidth = .8f;
		public float cpuCrestHeight, gpuCrestHeight, cpuCrestRatio, gpuCrestRatio;
		public float cpuCrestOffset, gpuCrestOffset, cpuBodyWidth, gpuBodyWidth;
		public bool requiresDirectWidth, directCriterionPassed, diagnosticHighCrest, passed;
	}
	[Serializable] public sealed class PeakValleyResult
	{
		public string fixture;
		public int fromCell, toCell;
		public float requiredDropRatio = .25f;
		public float cpuPeakA, cpuPeakB, gpuPeakA, gpuPeakB;
		public float cpuSaddleHeight, gpuSaddleHeight, cpuSaddleAlong, gpuSaddleAlong;
		public float cpuDropRatio, gpuDropRatio;
		public bool passed;
	}
	[Serializable] public sealed class BodyConnectivityResult
	{
		public string fixture;
		public int sampleCount, columns, rows, mountainPeaks, cpuConnectedPeaks, gpuConnectedPeaks;
		public int cpuMissingSeeds, gpuMissingSeeds, cpuComponents, gpuComponents;
		public float step = .1f, heightRatio = .33f, minimumHalfWidth = .2f, referencePeakHeight, threshold;
		public int[] cpuPeakComponents, gpuPeakComponents;
		public bool passed;
	}
	[Serializable] public sealed class GraphResult
	{
		public string fixture;
		public int mountainCells, originalEdges, retainedEdges, originalComponents, retainedComponents;
		public int invalidEdges, asymmetricEdges, retainedTriangles, lostThinEdges, splitEdges, cachedMaskDifferences;
		public bool passed;
	}
	[Serializable] public sealed class FoundationMonotonicityResult
	{
		public string fixture;
		public int sampleCount, invalidSamples;
		public float maximumHeightReduction, maximumHeightLift;
		public float heightTolerance = .001f;
		public bool passed;
	}
	[Serializable] public sealed class Report
	{
		// Schema 4 preserves dunes-enabled render diagnostics but isolates the
		// non-lowering gate from intentional dune attenuation at mountain feet.
		public int schemaVersion = 4;
		public string timestamp, comparison, graphicsDevice;
		public bool passed;
		public int sampleCount, readbackCount, invalidReadbacks;
		public float heightTolerance = .05f, seamTolerance = .05f, isolatedTolerance = .0001f;
		public float maximumCpuGpuError, maximumCpuSeamGap, maximumGpuSeamGap;
		public float oceanFallbackError, riverCarvingError;
		public float mountainRangeStrength, mountainRangeWidth;
		public string[] failures;
		public CaseResult[] cases;
		public CrossSectionResult[] crossSections;
		public PeakValleyResult[] peakValleys;
		public BodyConnectivityResult[] bodies;
		public GraphResult[] graphs;
		public FoundationMonotonicityResult[] foundationMonotonicity;
	}
	sealed class Sample
	{
		public int cell, study, corridor;
		public Vector2 local;
		public Vector3 point;
		public float cpu, baseline, gpu;
	}
	struct Seam { public int a, b; }
	struct Anchor { public Vector3 point; public float height; }
	sealed class CrossSection
	{
		public int study, firstSample;
		public CrossSectionResult result;
	}
	sealed class BodyField
	{
		public int firstSample;
		public Vector3 origin;
		public Anchor[] anchors;
		public BodyConnectivityResult result;
	}
	sealed class PeakValleyProbe
	{
		public int firstPeakA, firstPeakB;
		public PeakValleyResult result;
	}
	const int SectionHalfSamples = 28;
	const float SectionStep = .05f, CrestSearchRadius = .45f, BodyHeightRatio = .30f;

	public static string Run(HexNearTerrainShowcase showcase, string artifactPath,
		out bool passed, out string summary)
	{
		if (!EditorApplication.isPlaying || !showcase || !showcase.Ready ||
			showcase.gameObject.scene.name != HexNearTerrainShowcase.SceneName)
			throw new InvalidOperationException("Mountain range validation requires the ready disposable showcase.");
		HexGrid grid = showcase.grid;
		HexNearTerrainProfile profile = showcase.terrainStyle ? showcase.terrainStyle.nearTerrainProfile : null;
		if (!profile || !profile.IsReady)
			throw new InvalidOperationException("Mountain range validation requires a ready near terrain profile.");
		HexNearTerrainProfile baseline = Object.Instantiate(profile);
		baseline.hideFlags = HideFlags.HideAndDontSave;
		baseline.mountainRangeStrength = 0f;
		HexTerrainStyle baselineStyle = null;
		HexNearTerrainProfile foundationProfile = null, foundationBaselineProfile = null;
		HexTerrainStyle foundationStyle = null, foundationBaselineStyle = null;
		try
		{
			// Use the same full HF sampling path for both surfaces. Broad flank
			// probes can reach coast or river influence even when their mountain
			// endpoints are dry. Forcing sea=0 / no carving only on the baseline
			// would incorrectly report those natural transitions as lost terrain.
			baselineStyle = Object.Instantiate(showcase.terrainStyle);
			baselineStyle.hideFlags = HideFlags.HideAndDontSave;
			baselineStyle.nearTerrainProfile = baseline;
			HexSurfaceSampler baselineSurface = new(grid);
			baselineSurface.Configure(baselineStyle);
			// Raising a range intentionally fades nearby dunes. Compare the
			// underlying range/ground with dunes disabled on BOTH sides for the
			// non-lowering contract, retaining the real dune-enabled comparison
			// and GPU fixtures above. Configure only binds a sampler-local style;
			// no ApplyGlobals, active-grid reconfiguration or asset writes occur.
			foundationProfile = Object.Instantiate(profile);
			foundationProfile.hideFlags = HideFlags.HideAndDontSave;
			foundationProfile.desertDuneHeight = foundationProfile.desertHillDuneHeight = 0f;
			foundationBaselineProfile = Object.Instantiate(foundationProfile);
			foundationBaselineProfile.hideFlags = HideFlags.HideAndDontSave;
			foundationBaselineProfile.mountainRangeStrength = 0f;
			foundationStyle = Object.Instantiate(showcase.terrainStyle);
			foundationStyle.hideFlags = HideFlags.HideAndDontSave;
			foundationStyle.nearTerrainProfile = foundationProfile;
			foundationBaselineStyle = Object.Instantiate(showcase.terrainStyle);
			foundationBaselineStyle.hideFlags = HideFlags.HideAndDontSave;
			foundationBaselineStyle.nearTerrainProfile = foundationBaselineProfile;
			HexSurfaceSampler foundationSurface = new(grid), foundationBaselineSurface = new(grid);
			foundationSurface.Configure(foundationStyle);
			foundationBaselineSurface.Configure(foundationBaselineStyle);
			return RunInternal(showcase, grid, profile, baselineSurface, foundationSurface,
				foundationBaselineSurface, artifactPath, out passed, out summary);
		}
		finally
		{
			if (foundationBaselineStyle) Object.DestroyImmediate(foundationBaselineStyle);
			if (foundationStyle) Object.DestroyImmediate(foundationStyle);
			if (foundationBaselineProfile) Object.DestroyImmediate(foundationBaselineProfile);
			if (foundationProfile) Object.DestroyImmediate(foundationProfile);
			if (baselineStyle) Object.DestroyImmediate(baselineStyle);
			Object.DestroyImmediate(baseline);
		}
	}

	static string RunInternal(HexNearTerrainShowcase showcase, HexGrid grid,
		HexNearTerrainProfile profile, HexSurfaceSampler baseline, HexSurfaceSampler foundation,
		HexSurfaceSampler foundationBaseline, string artifactPath,
		out bool passed, out string summary)
	{
		List<Sample> samples = new();
		List<Seam> seams = new();
		List<CrossSection> sections = new();
		List<BodyField> bodies = new();
		List<PeakValleyProbe> peakValleys = new();
		List<GraphResult> graphs = new();
		List<string> failures = new();
		var studies = HexNearTerrainShowcase.MountainRangeStudyCases;
		CaseResult[] cases = new CaseResult[studies.Length];
		int corridorCount = 0;
		float datum = HexMetrics.visualWaterLevel * HexMetrics.elevationStep;
		for (int study = 0; study < studies.Length; study++)
		{
			var fixture = studies[study];
			CaseResult result = cases[study] = new CaseResult { name = fixture.name,
				mountainCells = fixture.cells.Length, minimumCorridorHeight = float.PositiveInfinity,
				minimumCpuCrestRatio = float.PositiveInfinity, minimumGpuCrestRatio = float.PositiveInfinity,
				minimumCpuBodyWidth = float.PositiveInfinity, minimumGpuBodyWidth = float.PositiveInfinity };
			HashSet<int> members = new();
			foreach (Vector2Int coord in fixture.cells) members.Add(coord.x + coord.y * grid.CellCountX);
			Dictionary<int, Anchor> anchors = new();
			foreach (int index in members) anchors[index] = FindAnchor(grid, baseline, index, datum);
			graphs.Add(CheckGraph(grid, fixture.name, members));
			if (members.Count > 1) AddBodyField(grid, baseline, samples, bodies, study, fixture.name, anchors);
			foreach (int index in members)
			{
				if (grid.CellData[index].landform != HexLandform.Mountain || grid.CellData[index].IsUnderwater)
					throw new InvalidOperationException("Unexpected mountain fixture contents: " + fixture.name);
				// A 9x9 patch covers peak, shoulders and the unchanged isolated footprint.
				for (int z = -4; z <= 4; z++)
					for (int x = -4; x <= 4; x++)
						Add(grid, baseline, samples, index, grid.CellPositions[index] +
							new Vector3(x * .19f, 0f, z * .19f) * HexMetrics.outerRadius, study, -1);
				HexCell cell = new(index, grid);
				for (HexDirection direction = HexDirection.NE; direction <= HexDirection.NW; direction++)
				{
					if (!cell.TryGetNeighbor(direction, out HexCell neighbor) || !members.Contains(neighbor.Index)) continue;
					result.directionMask |= 1 << (int)direction;
					if (neighbor.Index <= index) continue;
					result.connectedEdges++;
					// These dry fixed fixtures contain no plateau or river. Locate their
					// summits independently in the strength-zero surface, then require a
					// one continuous broad flank between them, allowing low saddles.
					AddCrossSections(grid, baseline, samples, sections, study, fixture.name,
						index, neighbor.Index, anchors[index], anchors[neighbor.Index]);
					// The showcase has one isolated pair. Complex bends and clusters
					// are not forced to have a saddle on every logical adjacency.
					if (fixture.name == "adjacent-pair") AddPeakValleyProbe(grid, baseline, samples,
						peakValleys, study, fixture.name, index, neighbor.Index, anchors[index], anchors[neighbor.Index]);
					Vector3 a = grid.CellPositions[index], b = grid.CellPositions[neighbor.Index];
					Vector3 tangent = new Vector3(-(b - a).z, 0f, (b - a).x).normalized * HexMetrics.outerRadius;
					// Curved ridges need not lie on the center line. Measure the maximum
					// across each corridor section, then its minimum along the connection.
					for (int along = 1; along <= 7; along++)
					{
						float t = along / 8f;
						int corridor = corridorCount++;
						for (int across = -5; across <= 5; across++)
							Add(grid, baseline, samples, t <= .5f ? index : neighbor.Index,
								Vector3.Lerp(a, b, t) + tangent * (across * .11f), study, corridor);
					}
					for (int offset = -4; offset <= 4; offset++)
					{
						Vector3 point = (a + b) * .5f + tangent * (offset * .115f);
						int first = samples.Count;
						Add(grid, baseline, samples, index, point, study, -1);
						Add(grid, baseline, samples, neighbor.Index, point, study, -1);
						seams.Add(new Seam { a = first, b = first + 1 });
					}
				}
			}
			// At a three-way meeting the exact same world point must agree from
			// all three roots, including the filled interior of a dense cluster.
			int[] memberArray = new int[members.Count]; members.CopyTo(memberArray);
			for (int a = 0; a < memberArray.Length; a++)
				for (int b = a + 1; b < memberArray.Length; b++)
					for (int c = b + 1; c < memberArray.Length; c++)
					{
						int ia = memberArray[a], ib = memberArray[b], ic = memberArray[c];
						if (!Adjacent(grid, ia, ib) || !Adjacent(grid, ia, ic) || !Adjacent(grid, ib, ic)) continue;
						Vector3 point = (grid.CellPositions[ia] + grid.CellPositions[ib] + grid.CellPositions[ic]) / 3f;
						int first = samples.Count;
						Add(grid, baseline, samples, ia, point, study, -1);
						Add(grid, baseline, samples, ib, point, study, -1);
						Add(grid, baseline, samples, ic, point, study, -1);
						seams.Add(new Seam { a = first, b = first + 1 });
						seams.Add(new Seam { a = first, b = first + 2 });
					}
		}
		Report report = new() { timestamp = DateTime.UtcNow.ToString("O"), cases = cases,
			foundationMonotonicity = new FoundationMonotonicityResult[cases.Length],
			mountainRangeStrength = profile.mountainRangeStrength, mountainRangeWidth = profile.mountainRangeWidth,
			graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
			sampleCount = samples.Count,
			comparison = "CPU and actual relief-shader readback at identical map-local points, shared edges and three-way junctions. " +
				"Baseline uses disposable style/profile clones with mountainRangeStrength=0 and the same full HexSurfaceSampler path, " +
				"including authored HF sea influence, old-surface fallback and river carving; no saved asset changes. " +
				"Schema 4 retains each case's dune-enabled maximumHeightReduction as an observed all-terrain diagnostic. " +
				"New mountain feet intentionally reduce dune height, so non-lowering is now checked separately under foundationMonotonicity: " +
				"two disposable zero-dune profile/style clones, current range strength versus strength zero, sample the identical points " +
				"through the same full HF/sea/fallback/river path. The original 0.001 height tolerance is unchanged; these isolated CPU checks " +
				"do not replace any real dune-enabled CPU/GPU, seam, corridor, width or connectivity check and never change shader globals. " +
				"Corridor height is the lowest transverse maximum across seven sections, permitting curved ridgelines. " +
				"Pairs and thin edges without triangle alternatives require >=30% flanks >=0.8R wide at t=.25/.5/.75. " +
				"Other logical-edge sections are diagnostic: sparse ridges need not raise every triangle side. " +
				"Every group must connect its independently measured summit seeds in the actual >=33% minimum-peak height field after 0.2R erosion. " +
				"The Massif pair retains >=25% peak-to-saddle relief. Explicit Range requires >=70% crest on direct thin edges; wall-like junctions are intentional. " +
				"The 65% crest line and physically high edge counts are diagnostics, not Civilization VI specifications or shape gates. " +
				"The graph gate requires bidirectional edges, connected components and valid masks; Range painting may retain triangles. " +
				"Crest search stays within 0.45 radii of the anchor line; the full cross-section extends +/-1.4 radii. " +
				"Ocean fallback and full river carving are separate CPU contract checks; full showcase surface validation covers rendered coast and river samples." };
		ReadGpu(showcase, samples, report);
		for (int i = 0; i < cases.Length; i++)
			report.foundationMonotonicity[i] = new FoundationMonotonicityResult { fixture = cases[i].name };
		float[] corridorMax = new float[corridorCount], baselineMax = new float[corridorCount];
		int[] corridorStudy = new int[corridorCount];
		for (int i = 0; i < corridorCount; i++) corridorMax[i] = baselineMax[i] = float.NegativeInfinity;
		foreach (Sample sample in samples)
		{
			CaseResult result = cases[sample.study]; result.samples++;
			float error = Mathf.Abs(sample.cpu - sample.gpu);
			result.maximumCpuGpuError = Mathf.Max(result.maximumCpuGpuError, error);
			report.maximumCpuGpuError = Mathf.Max(report.maximumCpuGpuError, error);
			result.maximumPointLift = Mathf.Max(result.maximumPointLift, sample.cpu - sample.baseline);
			result.maximumHeightReduction = Mathf.Max(result.maximumHeightReduction, sample.baseline - sample.cpu);
			FoundationMonotonicityResult foundationResult = report.foundationMonotonicity[sample.study];
			float foundationY = foundation.SampleHeight(sample.cell, sample.point, true);
			float foundationBaselineY = foundationBaseline.SampleHeight(sample.cell, sample.point, true);
			foundationResult.sampleCount++;
			if (!Finite(foundationY) || !Finite(foundationBaselineY)) foundationResult.invalidSamples++;
			else
			{
				foundationResult.maximumHeightReduction = Mathf.Max(foundationResult.maximumHeightReduction, foundationBaselineY - foundationY);
				foundationResult.maximumHeightLift = Mathf.Max(foundationResult.maximumHeightLift, foundationY - foundationBaselineY);
			}
			if (result.mountainCells == 1)
				result.maximumIsolatedChange = Mathf.Max(result.maximumIsolatedChange, Mathf.Abs(sample.cpu - sample.baseline));
			if (sample.corridor >= 0)
			{
				corridorMax[sample.corridor] = Mathf.Max(corridorMax[sample.corridor], sample.cpu);
				baselineMax[sample.corridor] = Mathf.Max(baselineMax[sample.corridor], sample.baseline);
				corridorStudy[sample.corridor] = sample.study;
			}
		}
		for (int i = 0; i < corridorCount; i++)
		{
			CaseResult result = cases[corridorStudy[i]];
			result.minimumCorridorHeight = Mathf.Min(result.minimumCorridorHeight, corridorMax[i] - datum);
			result.maximumCorridorLift = Mathf.Max(result.maximumCorridorLift, corridorMax[i] - baselineMax[i]);
		}
		foreach (Seam seam in seams)
		{
			Sample a = samples[seam.a], b = samples[seam.b];
			float cpuGap = Mathf.Abs(a.cpu - b.cpu), gpuGap = Mathf.Abs(a.gpu - b.gpu);
			CaseResult result = cases[a.study];
			result.maximumCpuSeamGap = Mathf.Max(result.maximumCpuSeamGap, cpuGap);
			result.maximumGpuSeamGap = Mathf.Max(result.maximumGpuSeamGap, gpuGap);
			report.maximumCpuSeamGap = Mathf.Max(report.maximumCpuSeamGap, cpuGap);
			report.maximumGpuSeamGap = Mathf.Max(report.maximumGpuSeamGap, gpuGap);
		}
		report.crossSections = new CrossSectionResult[sections.Count];
		for (int i = 0; i < sections.Count; i++)
		{
			CrossSection section = sections[i];
			CrossSectionResult result = report.crossSections[i] = section.result;
			MeasureCrossSection(samples, section.firstSample, datum, false, result);
			MeasureCrossSection(samples, section.firstSample, datum, true, result);
			result.diagnosticHighCrest = result.cpuCrestRatio >= result.diagnosticHighCrestRatio && result.gpuCrestRatio >= result.diagnosticHighCrestRatio;
			result.directCriterionPassed = result.cpuBodyWidth >= result.requiredBodyWidth && result.gpuBodyWidth >= result.requiredBodyWidth;
			result.passed = !result.requiresDirectWidth || result.directCriterionPassed;
			if (result.requiresDirectWidth &&
				grid.CellData[result.fromCell].mountainMode == HexMountainMode.Range &&
				grid.CellData[result.toCell].mountainMode == HexMountainMode.Range &&
				(result.cpuCrestRatio < .70f || result.gpuCrestRatio < .70f))
			{
				result.passed = false;
				failures.Add(result.fixture + ": continuous Range crest fell below 70% of the reference peak.");
			}
			CaseResult study = cases[section.study]; study.crossSections++;
			if (result.requiresDirectWidth) study.mandatoryCrossSections++;
			study.minimumCpuCrestRatio = Mathf.Min(study.minimumCpuCrestRatio, result.cpuCrestRatio);
			study.minimumGpuCrestRatio = Mathf.Min(study.minimumGpuCrestRatio, result.gpuCrestRatio);
			study.minimumCpuBodyWidth = Mathf.Min(study.minimumCpuBodyWidth, result.cpuBodyWidth);
			study.minimumGpuBodyWidth = Mathf.Min(study.minimumGpuBodyWidth, result.gpuBodyWidth);
			if (!result.passed)
			{
				study.failedCrossSections++;
				failures.Add($"{result.fixture} {result.fromCell}->{result.toCell} t={result.along:F2}: " +
					$"diagnostic crest CPU/GPU={result.cpuCrestRatio:F3}/{result.gpuCrestRatio:F3}, " +
					$"continuous 30% width={result.cpuBodyWidth:F3}/{result.gpuBodyWidth:F3}R (need {result.requiredBodyWidth:F2}R), " +
					$"reference peak={result.referencePeakHeight:F3}, crest offsets={result.cpuCrestOffset:F2}/{result.gpuCrestOffset:F2}R.");
			}
		}
		report.peakValleys = new PeakValleyResult[peakValleys.Count];
		for (int i = 0; i < peakValleys.Count; i++)
		{
			PeakValleyProbe probe = peakValleys[i];
			PeakValleyResult result = report.peakValleys[i] = probe.result;
			MeasurePeakValley(samples, report.crossSections, probe, datum, false);
			MeasurePeakValley(samples, report.crossSections, probe, datum, true);
			result.passed = result.cpuDropRatio >= result.requiredDropRatio && result.gpuDropRatio >= result.requiredDropRatio;
			if (!result.passed) failures.Add($"{result.fixture} {result.fromCell}->{result.toCell}: insufficient peak-to-saddle relief; " +
				$"CPU/GPU drop={result.cpuDropRatio:F3}/{result.gpuDropRatio:F3} (need {result.requiredDropRatio:F2}), " +
				$"peaks CPU={result.cpuPeakA:F3}/{result.cpuPeakB:F3}, GPU={result.gpuPeakA:F3}/{result.gpuPeakB:F3}; " +
				$"lowest crest CPU/GPU={result.cpuSaddleHeight:F3}/{result.gpuSaddleHeight:F3} at t={result.cpuSaddleAlong:F2}/{result.gpuSaddleAlong:F2}.");
		}
		report.bodies = new BodyConnectivityResult[bodies.Count];
		for (int i = 0; i < bodies.Count; i++)
		{
			BodyField body = bodies[i];
			MeasureBodyField(samples, body, datum, false);
			MeasureBodyField(samples, body, datum, true);
			BodyConnectivityResult result = report.bodies[i] = body.result;
			result.passed = result.cpuMissingSeeds == 0 && result.gpuMissingSeeds == 0 &&
				result.cpuConnectedPeaks == result.mountainPeaks && result.gpuConnectedPeaks == result.mountainPeaks;
			if (!result.passed) failures.Add($"{result.fixture}: thick mountain shoulders disconnected at {result.heightRatio:F2} peak height; " +
				$"CPU/GPU connected peaks {result.cpuConnectedPeaks}/{result.gpuConnectedPeaks} of {result.mountainPeaks}, " +
				$"missing seeds {result.cpuMissingSeeds}/{result.gpuMissingSeeds}; threshold={result.threshold:F3}, erosion={result.minimumHalfWidth:F2}R.");
		}
		report.graphs = graphs.ToArray();
		foreach (GraphResult graph in graphs) if (!graph.passed)
			failures.Add($"{graph.fixture}: ridge graph invalid; triangles={graph.retainedTriangles}, asymmetric={graph.asymmetricEdges}, " +
				$"splitEdges={graph.splitEdges}, lostThin={graph.lostThinEdges}, invalid={graph.invalidEdges}, staleCache={graph.cachedMaskDifferences}.");
		const int contractX = 8, contractZ = 18;
		const float oldSeaHeight = 2.125f;
		report.oceanFallbackError = Mathf.Abs(HexNearTerrainSurface.Evaluate(grid, profile, contractX,
			contractZ, Vector2.zero, .70f, 0f, oldSeaHeight, true) - oldSeaHeight);
		report.riverCarvingError = Mathf.Abs(HexNearTerrainSurface.Evaluate(grid, profile, contractX,
			contractZ, Vector2.zero, 0f, 0f, oldSeaHeight, true) - (datum - .302f));
		if (report.invalidReadbacks != 0 || report.readbackCount != report.sampleCount) failures.Add("Incomplete or invalid GPU readback.");
		if (report.maximumCpuGpuError > report.heightTolerance) failures.Add("CPU/GPU height disagreement.");
		if (report.maximumCpuSeamGap > report.seamTolerance || report.maximumGpuSeamGap > report.seamTolerance)
			failures.Add("Height discontinuity across a shared edge or junction.");
		if (report.oceanFallbackError > .0001f || report.riverCarvingError > .0001f)
			failures.Add("Ocean or full river-carving contract changed.");
		foreach (CaseResult result in cases)
		{
			result.observedCpuHighEdges = CountObservedHighEdges(report.crossSections, result.name, false);
			result.observedGpuHighEdges = CountObservedHighEdges(report.crossSections, result.name, true);
			if (result.connectedEdges == 0)
			{
				result.minimumCorridorHeight = 0f;
				result.minimumCpuCrestRatio = result.minimumGpuCrestRatio = result.minimumCpuBodyWidth = result.minimumGpuBodyWidth = 0f;
			}
			if (result.mountainCells == 1 && result.maximumIsolatedChange > report.isolatedTolerance)
				failures.Add(result.name + ": isolated mountain changed.");
		}
		foreach (FoundationMonotonicityResult result in report.foundationMonotonicity)
		{
			result.passed = result.sampleCount > 0 && result.invalidSamples == 0 &&
				result.maximumHeightReduction <= result.heightTolerance;
			if (!result.passed) failures.Add(result.fixture + ": isolated zero-dune range/foundation lowered authored terrain " +
				$"or produced invalid samples; reduction={result.maximumHeightReduction:F6}, tolerance={result.heightTolerance:F6}, invalid={result.invalidSamples}.");
		}
		report.failures = failures.ToArray(); passed = report.passed = failures.Count == 0;
		summary = $"Mountain range validation {(passed ? "passed" : "failed")}: {studies.Length} topologies, " +
			$"{report.readbackCount}/{report.sampleCount} GPU samples, max height error {report.maximumCpuGpuError:F6}, " +
			$"shared-edge/junction gaps {report.maximumCpuSeamGap:F6}/{report.maximumGpuSeamGap:F6}; " +
			$"{sections.Count} diagnostic sections, {bodies.Count} thick-body flood fills, {graphs.Count} graph fixtures and " +
			$"{report.foundationMonotonicity.Length} isolated zero-dune foundation checks completed.";
		Directory.CreateDirectory(Path.GetDirectoryName(artifactPath));
		File.WriteAllText(artifactPath, JsonUtility.ToJson(report, true));
		return artifactPath;
	}

	static int CountObservedHighEdges(CrossSectionResult[] sections, string fixture, bool gpu)
	{
		Dictionary<long, bool> high = new();
		foreach (CrossSectionResult section in sections)
		{
			if (section.fixture != fixture) continue;
			long edge = ((long)section.fromCell << 32) | (uint)section.toCell;
			bool passed = gpu ? section.gpuCrestRatio >= section.diagnosticHighCrestRatio && section.gpuBodyWidth >= section.requiredBodyWidth :
				section.cpuCrestRatio >= section.diagnosticHighCrestRatio && section.cpuBodyWidth >= section.requiredBodyWidth;
			high[edge] = (!high.TryGetValue(edge, out bool earlier) || earlier) && passed;
		}
		int count = 0; foreach (bool value in high.Values) if (value) count++;
		return count;
	}
	static bool HasTriangleAlternative(HexGrid grid, int a, int b)
	{
		HexCell cell = new(a, grid);
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			if (cell.TryGetNeighbor(d, out HexCell other) && other.Index != b &&
				grid.CellData[other.Index].landform == HexLandform.Mountain && !grid.CellData[other.Index].IsUnderwater &&
				Adjacent(grid, b, other.Index)) return true;
		return false;
	}
	static Dictionary<int, int> LabelGraph(Dictionary<int, List<int>> edges, out int components)
	{
		Dictionary<int, int> labels = new(); Queue<int> pending = new(); components = 0;
		foreach (int root in edges.Keys)
		{
			if (labels.ContainsKey(root)) continue;
			labels.Add(root, components); pending.Enqueue(root);
			while (pending.Count > 0)
			{
				int current = pending.Dequeue();
				foreach (int next in edges[current]) if (!labels.ContainsKey(next))
				{ labels.Add(next, components); pending.Enqueue(next); }
			}
			components++;
		}
		return labels;
	}
	static GraphResult CheckGraph(HexGrid grid, string fixture, HashSet<int> members)
	{
		GraphResult result = new() { fixture = fixture, mountainCells = members.Count };
		bool hasExplicitRange = false;
		Dictionary<int, int> masks = new();
		Dictionary<int, List<int>> original = new(), retained = new();
		foreach (int index in members)
		{
			hasExplicitRange |= grid.CellData[index].mountainMode == HexMountainMode.Range;
			int mask = HexMountainRidgeGraph.ComputeMask(grid, index % grid.CellCountX, index / grid.CellCountX);
			masks.Add(index, mask); original.Add(index, new List<int>()); retained.Add(index, new List<int>());
			if (grid.ShaderData != null && grid.ShaderData.GetMountainRidgeMask(index) != mask) result.cachedMaskDifferences++;
		}
		foreach (int index in members)
		{
			HexCell cell = new(index, grid);
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				bool kept = (masks[index] & (1 << (int)d)) != 0;
				if (!cell.TryGetNeighbor(d, out HexCell neighbor) || !members.Contains(neighbor.Index))
				{ if (kept) result.invalidEdges++; continue; }
				int other = neighbor.Index; original[index].Add(other);
				if (kept) retained[index].Add(other);
				if (kept != ((masks[other] & (1 << (((int)d + 3) % 6))) != 0)) result.asymmetricEdges++;
				if (other <= index) continue;
				result.originalEdges++; if (kept) result.retainedEdges++;
				if (!HasTriangleAlternative(grid, index, other) && !kept) result.lostThinEdges++;
			}
		}
		Dictionary<int, int> before = LabelGraph(original, out result.originalComponents);
		Dictionary<int, int> after = LabelGraph(retained, out result.retainedComponents);
		foreach (int a in members)
		{
			foreach (int b in original[a]) if (before[a] == before[b] && after[a] != after[b]) result.splitEdges++;
			foreach (int b in retained[a]) if (b > a)
				foreach (int c in retained[a]) if (c > b && retained[b].Contains(c)) result.retainedTriangles++;
		}
		result.passed = result.originalComponents == result.retainedComponents && result.invalidEdges == 0 &&
			result.asymmetricEdges == 0 && (hasExplicitRange || result.retainedTriangles == 0) && result.lostThinEdges == 0 &&
			result.splitEdges == 0 && result.cachedMaskDifferences == 0;
		return result;
	}
	static int Owner(HexGrid grid, Vector3 point)
	{
		if (!grid.TryGetCellIndex(HexCoordinates.FromPosition(point), out int cell))
			throw new InvalidOperationException("Mountain cross-section left the fixed showcase map.");
		return cell;
	}
	static float SampleBaseline(HexGrid grid, HexSurfaceSampler baseline, Vector3 point)
	{
		int cell = Owner(grid, point);
		return baseline.SampleHeight(cell, point, true);
	}
	static Anchor FindAnchor(HexGrid grid, HexSurfaceSampler baseline, int cell, float datum)
	{
		// Search the independently rendered stamp. Do not duplicate the range's
		// private anchors, random bend, cross-section or smooth-union equation.
		Vector3 center = grid.CellPositions[cell], best = center;
		float highest = float.NegativeInfinity, radius = HexMetrics.outerRadius;
		for (int z = -10; z <= 10; z++) for (int x = -10; x <= 10; x++)
		{
			if (x * x + z * z > 100) continue;
			Vector3 point = center + new Vector3(x * .08f, 0f, z * .08f) * radius;
			float value = SampleBaseline(grid, baseline, point);
			if (value > highest) { highest = value; best = point; }
		}
		Vector3 coarse = best;
		for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++)
		{
			Vector3 point = coarse + new Vector3(x * .02f, 0f, z * .02f) * radius;
			if ((point - center).sqrMagnitude > .64f * radius * radius) continue;
			float value = SampleBaseline(grid, baseline, point);
			if (value > highest) { highest = value; best = point; }
		}
		return new Anchor { point = best, height = Mathf.Max(.001f, highest - datum - .178f) };
	}
	static void AddCrossSections(HexGrid grid, HexSurfaceSampler baseline, List<Sample> samples,
		List<CrossSection> sections, int study, string fixture, int from, int to, Anchor a, Anchor b)
	{
		Vector3 axis = b.point - a.point;
		Vector3 across = new Vector3(-axis.z, 0f, axis.x).normalized * HexMetrics.outerRadius;
		for (int along = 1; along <= 3; along++)
		{
			float t = along * .25f;
			sections.Add(new CrossSection { study = study, firstSample = samples.Count,
				result = new CrossSectionResult { fixture = fixture, fromCell = from, toCell = to,
					along = t, referencePeakHeight = Mathf.Min(a.height, b.height),
					requiresDirectWidth = !HasTriangleAlternative(grid, from, to) } });
			for (int i = -SectionHalfSamples; i <= SectionHalfSamples; i++)
			{
				Vector3 point = Vector3.Lerp(a.point, b.point, t) + across * (i * SectionStep);
				Add(grid, baseline, samples, Owner(grid, point), point, study, -1);
			}
		}
	}
	static void AddPeakValleyProbe(HexGrid grid, HexSurfaceSampler baseline, List<Sample> samples,
		List<PeakValleyProbe> probes, int study, string fixture, int from, int to, Anchor a, Anchor b)
	{
		PeakValleyProbe probe = new() { firstPeakA = samples.Count,
			result = new PeakValleyResult { fixture = fixture, fromCell = from, toCell = to } };
		AddPeak(a.point);
		probe.firstPeakB = samples.Count;
		AddPeak(b.point);
		probes.Add(probe);
		void AddPeak(Vector3 anchor)
		{
			// Actual CPU/GPU maxima near independently found authored summits.
			// No production ridge anchor or saddle position is reused here.
			for (int z = -3; z <= 3; z++) for (int x = -3; x <= 3; x++)
			{
				Vector3 point = anchor + new Vector3(x * .04f, 0f, z * .04f) * HexMetrics.outerRadius;
				Add(grid, baseline, samples, Owner(grid, point), point, study, -1);
			}
		}
	}
	static void MeasurePeakValley(List<Sample> samples, CrossSectionResult[] sections,
		PeakValleyProbe probe, float datum, bool gpu)
	{
		PeakValleyResult result = probe.result;
		float a = Peak(probe.firstPeakA), b = Peak(probe.firstPeakB);
		float saddle = float.PositiveInfinity, along = 0f;
		foreach (CrossSectionResult section in sections)
		{
			if (section.fixture != result.fixture || section.fromCell != result.fromCell || section.toCell != result.toCell) continue;
			float height = gpu ? section.gpuCrestHeight : section.cpuCrestHeight;
			if (height < saddle) { saddle = height; along = section.along; }
		}
		float drop = (Mathf.Min(a, b) - saddle) / Mathf.Max(.001f, Mathf.Min(a, b));
		if (gpu) { result.gpuPeakA = a; result.gpuPeakB = b; result.gpuSaddleHeight = saddle; result.gpuSaddleAlong = along; result.gpuDropRatio = drop; }
		else { result.cpuPeakA = a; result.cpuPeakB = b; result.cpuSaddleHeight = saddle; result.cpuSaddleAlong = along; result.cpuDropRatio = drop; }
		float Peak(int first)
		{
			float peak = float.NegativeInfinity;
			for (int i = 0; i < 49; i++) peak = Mathf.Max(peak, (gpu ? samples[first + i].gpu : samples[first + i].cpu) - datum - .178f);
			return peak;
		}
	}
	static void AddBodyField(HexGrid grid, HexSurfaceSampler baseline, List<Sample> samples,
		List<BodyField> bodies, int study, string fixture, Dictionary<int, Anchor> anchors)
	{
		float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
		float reference = float.PositiveInfinity, radius = HexMetrics.outerRadius;
		Anchor[] peaks = new Anchor[anchors.Count]; int at = 0;
		foreach (Anchor anchor in anchors.Values)
		{
			peaks[at++] = anchor; reference = Mathf.Min(reference, anchor.height);
			minX = Mathf.Min(minX, anchor.point.x); maxX = Mathf.Max(maxX, anchor.point.x);
			minZ = Mathf.Min(minZ, anchor.point.z); maxZ = Mathf.Max(maxZ, anchor.point.z);
		}
		minX = Mathf.Max(radius, minX - 1.5f * radius);
		maxX = Mathf.Min((grid.CellCountX - 1) * 1.7320508075688772f * radius, maxX + 1.5f * radius);
		minZ = Mathf.Max(0f, minZ - 1.5f * radius);
		maxZ = Mathf.Min((grid.CellCountZ - 1) * 1.5f * radius, maxZ + 1.5f * radius);
		BodyConnectivityResult result = new() { fixture = fixture, referencePeakHeight = reference, mountainPeaks = peaks.Length };
		result.threshold = reference * result.heightRatio;
		float step = result.step * radius;
		result.columns = Mathf.CeilToInt((maxX - minX) / step) + 1;
		result.rows = Mathf.CeilToInt((maxZ - minZ) / step) + 1;
		result.sampleCount = result.columns * result.rows;
		BodyField field = new() { firstSample = samples.Count, origin = new Vector3(minX, 0f, minZ), anchors = peaks, result = result };
		bodies.Add(field);
		for (int z = 0; z < result.rows; z++) for (int x = 0; x < result.columns; x++)
		{
			Vector3 point = field.origin + new Vector3(x * step, 0f, z * step);
			Add(grid, baseline, samples, Owner(grid, point), point, study, -1);
		}
	}
	static void MeasureBodyField(List<Sample> samples, BodyField field, float datum, bool gpu)
	{
		BodyConnectivityResult result = field.result; int width = result.columns, height = result.rows;
		bool[] body = new bool[result.sampleCount], interior = new bool[result.sampleCount];
		for (int i = 0; i < body.Length; i++)
		{
			Sample sample = samples[field.firstSample + i];
			body[i] = (gpu ? sample.gpu : sample.cpu) - datum - .178f >= result.threshold;
		}
		// 0.2R erosion tests body thickness before four-neighbor connectivity.
		// A single narrow crest pixel cannot join otherwise separate mountains.
		for (int z = 2; z < height - 2; z++) for (int x = 2; x < width - 2; x++)
		{
			bool thick = true;
			for (int dz = -2; dz <= 2 && thick; dz++) for (int dx = -2; dx <= 2; dx++)
				if (dx * dx + dz * dz <= 4 && !body[x + dx + (z + dz) * width]) { thick = false; break; }
			interior[x + z * width] = thick;
		}
		int[] labels = new int[body.Length]; for (int i = 0; i < labels.Length; i++) labels[i] = -1;
		int components = 0; Queue<int> pending = new();
		for (int root = 0; root < labels.Length; root++)
		{
			if (!interior[root] || labels[root] >= 0) continue;
			labels[root] = components; pending.Enqueue(root);
			while (pending.Count > 0)
			{
				int current = pending.Dequeue(), x = current % width, z = current / width;
				Visit(x > 0 ? current - 1 : -1); Visit(x + 1 < width ? current + 1 : -1);
				Visit(z > 0 ? current - width : -1); Visit(z + 1 < height ? current + width : -1);
				void Visit(int next)
				{
					if (next < 0 || !interior[next] || labels[next] >= 0) return;
					labels[next] = components; pending.Enqueue(next);
				}
			}
			components++;
		}
		int[] peakComponents = new int[field.anchors.Length]; int shared = -1, connected = 0, missing = 0;
		float step = result.step * HexMetrics.outerRadius;
		for (int i = 0; i < field.anchors.Length; i++)
		{
			Vector3 relative = (field.anchors[i].point - field.origin) / step;
			int px = Mathf.RoundToInt(relative.x), pz = Mathf.RoundToInt(relative.z), selected = -1;
			float distance = float.PositiveInfinity;
			for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
			{
				int x = px + dx, z = pz + dz;
				if (x < 0 || x >= width || z < 0 || z >= height || dx * dx + dz * dz > 9) continue;
				int index = x + z * width; float d = dx * dx + dz * dz;
				if (interior[index] && d < distance) { distance = d; selected = index; }
			}
			int component = selected < 0 ? -1 : labels[selected]; peakComponents[i] = component;
			if (component < 0) missing++; else if (shared < 0) shared = component;
			if (component >= 0 && component == shared) connected++;
		}
		if (gpu) { result.gpuComponents = components; result.gpuConnectedPeaks = connected; result.gpuMissingSeeds = missing; result.gpuPeakComponents = peakComponents; }
		else { result.cpuComponents = components; result.cpuConnectedPeaks = connected; result.cpuMissingSeeds = missing; result.cpuPeakComponents = peakComponents; }
	}
	static void MeasureCrossSection(List<Sample> samples, int first, float datum, bool gpu, CrossSectionResult result)
	{
		int count = SectionHalfSamples * 2 + 1, crest = SectionHalfSamples;
		float[] heights = new float[count];
		float maximum = float.NegativeInfinity;
		for (int i = 0; i < count; i++)
		{
			heights[i] = (gpu ? samples[first + i].gpu : samples[first + i].cpu) - datum - .178f;
			if (Mathf.Abs(i - SectionHalfSamples) * SectionStep <= CrestSearchRadius + .0001f && heights[i] > maximum)
			{ maximum = heights[i]; crest = i; }
		}
		float threshold = result.referencePeakHeight * BodyHeightRatio, width = 0f;
		if (maximum >= threshold)
		{
			int left = crest, right = crest;
			while (left > 0 && heights[left - 1] >= threshold) left--;
			while (right + 1 < count && heights[right + 1] >= threshold) right++;
			float lo = left, hi = right;
			if (left > 0) lo -= (heights[left] - threshold) / Mathf.Max(.000001f, heights[left] - heights[left - 1]);
			if (right + 1 < count) hi += (heights[right] - threshold) / Mathf.Max(.000001f, heights[right] - heights[right + 1]);
			width = (hi - lo) * SectionStep;
		}
		float ratio = maximum / result.referencePeakHeight, offset = (crest - SectionHalfSamples) * SectionStep;
		if (gpu) { result.gpuCrestHeight = maximum; result.gpuCrestRatio = ratio; result.gpuCrestOffset = offset; result.gpuBodyWidth = width; }
		else { result.cpuCrestHeight = maximum; result.cpuCrestRatio = ratio; result.cpuCrestOffset = offset; result.cpuBodyWidth = width; }
	}

	static bool Adjacent(HexGrid grid, int a, int b)
	{
		HexCell cell = new(a, grid);
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			if (cell.TryGetNeighbor(d, out HexCell neighbor) && neighbor.Index == b) return true;
		return false;
	}
	static void Add(HexGrid grid, HexSurfaceSampler baseline, List<Sample> samples,
		int cell, Vector3 point, int study, int corridor)
	{
		Vector3 relative = point - grid.CellPositions[cell];
		Vector2 local = new Vector2(relative.x, relative.z) / HexMetrics.outerRadius;
		samples.Add(new Sample { cell = cell, local = local, point = point, study = study, corridor = corridor,
			cpu = grid.SampleSurfaceHeight(cell, point, true),
			baseline = baseline.SampleHeight(cell, point, true) });
	}

	static void ReadGpu(HexNearTerrainShowcase showcase, List<Sample> samples, Report report)
	{
		if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
			!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
			throw new InvalidOperationException("Floating point height readback is unavailable on this GPU.");
		Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/HexMapPackage/Editor/Terrain/HexNearSurfaceValidation.shader");
		if (!shader || !shader.isSupported) throw new InvalidOperationException("Near surface validation shader is unavailable.");
		const int width = 128;
		int height = Mathf.CeilToInt(samples.Count / (float)width);
		Color[] input = new Color[width * height];
		for (int i = 0; i < input.Length; i++) input[i] = new Color(0, 0, 0, -1);
		for (int i = 0; i < samples.Count; i++) input[i] = new Color(samples[i].cell, samples[i].local.x, samples[i].local.y, i);
		Texture2D points = new(width, height, TextureFormat.RGBAFloat, false, true) {
			filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
		Material material = new(shader) { hideFlags = HideFlags.DontSave };
		RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
		RenderTexture previous = RenderTexture.active;
		bool previousSRGB = GL.sRGBWrite;
		Texture2D readback = null;
		try
		{
			HexReliefMesh relief = showcase.grid.GetComponentInChildren<HexReliefMesh>();
			if (relief) material.CopyPropertiesFromMaterial(relief.GetComponent<MeshRenderer>().sharedMaterial);
			showcase.terrainStyle.ApplyTo(null);
			points.SetPixels(input); points.Apply(false, false);
			material.SetTexture("_SamplePoints", points);
			GL.sRGBWrite = false;
			Graphics.Blit(points, target, material, 0);
			RenderTexture.active = target;
			readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false); readback.Apply(false, false);
			bool[] seen = new bool[samples.Count];
			foreach (Color value in readback.GetPixels())
			{
				if (!Finite(value.r) || !Finite(value.g)) { report.invalidReadbacks++; continue; }
				int id = Mathf.RoundToInt(value.g); if (id < 0) continue;
				if (id >= samples.Count || seen[id] || Mathf.Abs(value.g - id) > .001f)
				{ report.invalidReadbacks++; continue; }
				seen[id] = true; report.readbackCount++; samples[id].gpu = value.r;
				if (!Finite(samples[id].cpu) || !Finite(samples[id].baseline)) report.invalidReadbacks++;
			}
		}
		finally
		{
			GL.sRGBWrite = previousSRGB; RenderTexture.active = previous;
			RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(points); Object.DestroyImmediate(material);
			if (readback) Object.DestroyImmediate(readback);
		}
	}
	static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
