using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CPU counterpart of HF_EvaluateOriginalRelief.
///
/// The visible terrain is displaced in the relief shader, but physics and
/// ordinary GameObjects cannot read that vertex result. This sampler keeps one
/// compact, single-channel CPU copy of the authored HF height/mixer textures
/// and evaluates the same root + neighbor + corner stamp set as the shader.
/// Logical Catlike elevation remains gameplay data; it is deliberately absent
/// from this visual-surface calculation.
/// </summary>
public sealed class HexSurfaceSampler
{
	const float sqrt3Over2 = 0.86602540378f;
	const float hfSurfaceBias = 0.018f;
	const float hfRiverBed = -1.65f;

	static readonly Vector2[] neighborCenters =
	{
		new(sqrt3Over2, 1.5f),
		new(2f * sqrt3Over2, 0f),
		new(sqrt3Over2, -1.5f),
		new(-sqrt3Over2, -1.5f),
		new(-2f * sqrt3Over2, 0f),
		new(-sqrt3Over2, 1.5f)
	};

	static readonly Vector2[] riverDirections =
	{
		new(0.5f, sqrt3Over2),
		new(1f, 0f),
		new(0.5f, -sqrt3Over2),
		new(-0.5f, -sqrt3Over2),
		new(-1f, 0f),
		new(-0.5f, sqrt3Over2)
	};

	readonly HexGrid grid;
	readonly ScalarTexture[] heights = new ScalarTexture[6];
	readonly ScalarTexture[] mixers = new ScalarTexture[6];

	HexTerrainStyle style;
	int textureSignature;
	bool cacheReady;
	bool cacheAttempted;
	bool reportedCacheFailure;

	struct Accumulator
	{
		public float globalMaximum;
		public float mixerWeight;
		public float fillWeight;
		public float mixerHeight;
		public float fillHeight;
		public float riverDistance;
	}

	sealed class ScalarTexture
	{
		readonly int width, height, stride;
		readonly byte[] pixels;

		public ScalarTexture(Texture2D source, int mipLevel)
		{
			width = Mathf.Max(1, source.width >> mipLevel);
			height = Mathf.Max(1, source.height >> mipLevel);
			bool useR8 = SystemInfo.SupportsRenderTextureFormat(
				RenderTextureFormat.R8) &&
				SystemInfo.SupportsTextureFormat(TextureFormat.R8);
			RenderTextureFormat renderFormat = useR8 ?
				RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
			TextureFormat textureFormat = useR8 ?
				TextureFormat.R8 : TextureFormat.RGBA32;
			stride = useR8 ? 1 : 4;

			RenderTexture target = RenderTexture.GetTemporary(
				width, height, 0, renderFormat, RenderTextureReadWrite.Linear);
			target.filterMode = FilterMode.Bilinear;
			RenderTexture previous = RenderTexture.active;
			Texture2D readable = null;
			try
			{
				// Downsampling selects the same mip used by
				// SAMPLE_TEXTURE2D_LOD in the HF shader. Mixer copies remain at
				// mip zero and therefore keep the full authored boundary.
				Graphics.Blit(source, target);
				RenderTexture.active = target;
				readable = new Texture2D(
					width, height, textureFormat, false, true);
				readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
				readable.Apply(false, false);
				pixels = readable.GetRawTextureData<byte>().ToArray();
			}
			finally
			{
				RenderTexture.active = previous;
				RenderTexture.ReleaseTemporary(target);
				if (readable)
				{
					if (Application.isPlaying)
					{
						UnityEngine.Object.Destroy(readable);
					}
					else
					{
						UnityEngine.Object.DestroyImmediate(readable);
					}
				}
			}
		}

		public float SampleBilinear(Vector2 uv)
		{
			// Hardware bilinear filtering maps UV pixel centers to x + 0.5.
			// Reproducing that convention matters along HF mixer boundaries.
			float x = Mathf.Clamp01(uv.x) * width - 0.5f;
			float y = Mathf.Clamp01(uv.y) * height - 0.5f;
			int x0 = Mathf.FloorToInt(x);
			int y0 = Mathf.FloorToInt(y);
			float tx = x - x0;
			float ty = y - y0;
			int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
			int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
			x0 = Mathf.Clamp(x0, 0, width - 1);
			y0 = Mathf.Clamp(y0, 0, height - 1);

			float a = Mathf.Lerp(Read(x0, y0), Read(x1, y0), tx);
			float b = Mathf.Lerp(Read(x0, y1), Read(x1, y1), tx);
			return Mathf.Lerp(a, b, ty);
		}

		float Read(int x, int y) =>
			pixels[(y * width + x) * stride] * (1f / 255f);
	}

	public HexSurfaceSampler(HexGrid grid)
	{
		this.grid = grid;
	}

	public bool UsesHFOriginalSurface =>
		style && style.UsesHFOriginalSurface;

	public void Configure(HexTerrainStyle terrainStyle)
	{
		style = terrainStyle ? terrainStyle : HexTerrainStyle.RuntimeDefault;
		int signature = BuildTextureSignature(style);
		if (signature == textureSignature && (cacheReady || cacheAttempted))
		{
			return;
		}

		textureSignature = signature;
		cacheReady = false;
		cacheAttempted = false;
		reportedCacheFailure = false;
		Array.Clear(heights, 0, heights.Length);
		Array.Clear(mixers, 0, mixers.Length);
	}

	/// <summary>
	/// Sample by map-local XZ. The containing cell is selected as the root.
	/// </summary>
	public float SampleHeight(Vector3 localPosition, bool carveRiver = true)
	{
		HexCoordinates coordinates = HexCoordinates.FromPosition(localPosition);
		if (!grid.TryGetCellIndex(coordinates, out int cellIndex))
		{
			return DatumY;
		}
		return SampleHeight(cellIndex, localPosition, carveRiver);
	}

	/// <summary>
	/// Sample with an explicit root cell. The shader uses this form for each
	/// tessellation patch; the thirteen-stamp neighborhood makes the answer
	/// invariant at shared edges.
	/// </summary>
	public float SampleHeight(
		int rootCellIndex, Vector3 localPosition, bool carveRiver = true)
	{
		if (!UsesHFOriginalSurface)
		{
			return localPosition.y;
		}
		if (!EnsureTextureCache())
		{
			return DatumY;
		}
		if (rootCellIndex < 0 || rootCellIndex >= grid.CellData.Length)
		{
			return DatumY;
		}

		Vector3 center = grid.CellPositions[rootCellIndex];
		float deltaX = localPosition.x - center.x;
		if (grid.Wrapping)
		{
			float mapWidth = HexMetrics.innerDiameter * grid.CellCountX;
			if (deltaX < -mapWidth * 0.5f)
			{
				deltaX += mapWidth;
			}
			else if (deltaX > mapWidth * 0.5f)
			{
				deltaX -= mapWidth;
			}
		}
		Vector2 rootPoint = new(
			deltaX / HexMetrics.outerRadius,
			(localPosition.z - center.z) / HexMetrics.outerRadius);
		int rootX = rootCellIndex % grid.CellCountX;
		int rootZ = rootCellIndex / grid.CellCountX;

		Accumulator accumulator = new()
		{
			riverDistance = 1000f
		};
		Accumulate(ref accumulator, rootX, rootZ, rootPoint, carveRiver);
		for (int direction = 0; direction < 6; direction++)
		{
			Vector2Int first = NeighborOffset(rootX, rootZ, direction);
			Vector2 firstCenter = neighborCenters[direction];
			Accumulate(
				ref accumulator, first.x, first.y,
				rootPoint - firstCenter, carveRiver);

			int nextDirection = (direction + 1) % 6;
			Vector2Int corner = NeighborOffset(
				first.x, first.y, nextDirection);
			Accumulate(
				ref accumulator, corner.x, corner.y,
				rootPoint - firstCenter - neighborCenters[nextDirection],
				carveRiver);
		}

		float missingStrength = 1f - Mathf.Clamp01(accumulator.globalMaximum);
		float totalWeight = accumulator.mixerWeight +
			accumulator.fillWeight * missingStrength;
		if (totalWeight <= 0.0001f)
		{
			return DatumY;
		}
		float heightSample = (accumulator.mixerHeight +
			accumulator.fillHeight * missingStrength) / totalWeight;
		float displacement =
			(heightSample - 0.5f) * style.hfOriginalHeightScale;
		if (displacement < 0f)
		{
			displacement *= 0.6f;
		}

		if (carveRiver)
		{
			float riverFade = SmoothStep(
				style.hfRiverCarve.x,
				style.hfRiverCarve.y,
				accumulator.riverDistance);
			displacement = Mathf.Lerp(hfRiverBed, displacement, riverFade);
		}
		return DatumY + displacement + hfSurfaceBias;
	}

	float DatumY => HexMetrics.visualWaterLevel * HexMetrics.elevationStep;

	void Accumulate(
		ref Accumulator accumulator,
		int requestedX, int requestedZ, Vector2 samplePoint, bool carveRiver)
	{
		if (!ResolveOffset(requestedX, requestedZ, out int x, out int z))
		{
			return;
		}

		int cellIndex = x + z * grid.CellCountX;
		HexCellData cell = grid.CellData[cellIndex];
		float angle = PackedHFAngle(cellIndex);
		float sine = Mathf.Sin(angle);
		float cosine = Mathf.Cos(angle);
		Vector2 oriented = new(
			samplePoint.x * cosine + samplePoint.y * sine,
			-samplePoint.x * sine + samplePoint.y * cosine);
		Vector2 uv = oriented /
			(2f * Mathf.Max(style.hfOriginalStampScale, 0.1f)) +
			new Vector2(0.5f, 0.5f);
		float centralization = Centralization(uv);
		if (centralization <= 0.0001f)
		{
			return;
		}

		int panel = PanelFor(cell);
		float mixer = mixers[panel].SampleBilinear(uv) * centralization;
		float height = heights[panel].SampleBilinear(uv);
		accumulator.globalMaximum = Mathf.Max(
			accumulator.globalMaximum, mixer);
		accumulator.mixerWeight += mixer;
		accumulator.fillWeight += centralization;
		accumulator.mixerHeight += height * mixer;
		accumulator.fillHeight += height * centralization;
		if (carveRiver)
		{
			accumulator.riverDistance = Mathf.Min(
				accumulator.riverDistance,
				RiverDistance(samplePoint, cell));
		}
	}

	bool EnsureTextureCache()
	{
		if (cacheReady)
		{
			return true;
		}
		if (cacheAttempted)
		{
			return false;
		}
		cacheAttempted = true;

		if (!style || !style.HasHFOriginalTerrainSet())
		{
			ReportCacheFailure(
				"HF Original surface mode is selected, but its terrain triplet set is incomplete.");
			return false;
		}

		try
		{
			int heightMip = Mathf.Clamp(style.hfOriginalHeightLod, 0, 12);
			var cache = new Dictionary<(int, int), ScalarTexture>();
			ScalarTexture Read(Texture2D texture, int mip)
			{
				var key = (texture.GetInstanceID(), mip);
				if (!cache.TryGetValue(key, out ScalarTexture scalar))
				{
					scalar = new ScalarTexture(texture, mip);
					cache.Add(key, scalar);
				}
				return scalar;
			}

			heights[0] = Read(style.hfDirtHeight, heightMip);
			heights[1] = heights[2] = Read(style.hfCommonHeight, heightMip);
			heights[3] = Read(style.hfHillHeight, heightMip);
			heights[4] = Read(style.hfMountainHeight, heightMip);
			heights[5] = Read(style.hfSeaHeight, heightMip);
			mixers[0] = Read(style.hfDirtMixer, 0);
			mixers[1] = Read(style.hfPlainsMixer, 0);
			mixers[2] = Read(style.hfMarshMixer, 0);
			mixers[3] = Read(style.hfHillMixer, 0);
			mixers[4] = Read(style.hfMountainMixer, 0);
			mixers[5] = Read(style.hfSeaMixer, 0);
			cacheReady = true;
		}
		catch (Exception exception)
		{
			ReportCacheFailure(
				$"Unable to build the HF CPU surface cache: {exception.Message}");
		}
		return cacheReady;
	}

	void ReportCacheFailure(string message)
	{
		if (reportedCacheFailure)
		{
			return;
		}
		reportedCacheFailure = true;
		Debug.LogError(
			$"{message} Physics and object placement will use the shared HF datum until this is corrected.",
			grid);
	}

	float PackedHFAngle(int cellIndex)
	{
		// Reproduce the shader-data pack/decode exactly. The six authored
		// rotations cannot all be represented exactly by the 6-bit angle, so
		// using an ideal angle here would make CPU placement drift slightly from
		// the terrain rendered by the GPU.
		float angle01 = Mathf.Repeat(
			grid.CellData[cellIndex].TerrainRotation / 6f + 0.5f, 1f);
		int packed = Mathf.Clamp(Mathf.RoundToInt(angle01 * 63f), 0, 63);
		return packed / 63f * (Mathf.PI * 2f) - Mathf.PI;
	}

	static int PanelFor(HexCellData cell)
	{
		if (cell.IsUnderwater)
		{
			return 5;
		}
		if (cell.landform == HexLandform.Mountain)
		{
			return 4;
		}
		if (cell.landform == HexLandform.Hill)
		{
			return 3;
		}
		// Vegetation is an independent foreground layer. It must never replace
		// the authored ground panel (the old coupling made every forest Plains).
		// HF authored only three flat height/mixer panels. Terrain IDs 3 and 4
		// reuse the nearest structural panels for CPU placement, while the shader
		// overlays their independent Tundra and Snow logical surface materials.
		return cell.TerrainTypeIndex switch
		{
			0 => 0,
			1 => 1,
			2 => 2,
			3 => 0,
			_ => 1
		};
	}

	static float Centralization(Vector2 uv)
	{
		Vector2 edge = new(
			Mathf.Abs(uv.x - 0.5f), Mathf.Abs(uv.y - 0.5f));
		float maximum = Mathf.Max(edge.x, edge.y);
		if (maximum > 0.5f)
		{
			return 0f;
		}
		return Mathf.Clamp01(3f * (1f - maximum * 2f));
	}

	static float RiverDistance(Vector2 point, HexCellData cell)
	{
		float distance = 1000f;
		for (int direction = 0; direction < 6; direction++)
		{
			if (!cell.HasRiverThroughEdge((HexDirection)direction))
			{
				continue;
			}
			Vector2 endpoint = riverDirections[direction] * 1.08f;
			float denominator = Mathf.Max(Vector2.Dot(endpoint, endpoint), 0.0001f);
			float t = Mathf.Clamp01(Vector2.Dot(point, endpoint) / denominator);
			distance = Mathf.Min(distance, (point - endpoint * t).magnitude);
		}
		return distance;
	}

	static float SmoothStep(float minimum, float maximum, float value)
	{
		float t = Mathf.Clamp01((value - minimum) /
			Mathf.Max(maximum - minimum, 0.0001f));
		return t * t * (3f - 2f * t);
	}

	Vector2Int NeighborOffset(int x, int z, int direction)
	{
		int odd = z & 1;
		return direction switch
		{
			0 => new Vector2Int(x + odd, z + 1),
			1 => new Vector2Int(x + 1, z),
			2 => new Vector2Int(x + odd, z - 1),
			3 => new Vector2Int(x + odd - 1, z - 1),
			4 => new Vector2Int(x - 1, z),
			_ => new Vector2Int(x + odd - 1, z + 1)
		};
	}

	bool ResolveOffset(
		int requestedX, int requestedZ, out int resolvedX, out int resolvedZ)
	{
		resolvedX = requestedX;
		resolvedZ = requestedZ;
		if (requestedZ < 0 || requestedZ >= grid.CellCountZ)
		{
			return false;
		}
		if (grid.Wrapping)
		{
			resolvedX %= grid.CellCountX;
			if (resolvedX < 0)
			{
				resolvedX += grid.CellCountX;
			}
			return true;
		}
		return requestedX >= 0 && requestedX < grid.CellCountX;
	}

	static int BuildTextureSignature(HexTerrainStyle terrainStyle)
	{
		if (!terrainStyle)
		{
			return 0;
		}
		unchecked
		{
			int hash = terrainStyle.GetInstanceID();
			hash = hash * 31 + terrainStyle.hfOriginalHeightLod;
			hash = hash * 31 + terrainStyle.RuntimeRevision;
			Texture2D[] textures =
			{
				terrainStyle.hfDirtHeight, terrainStyle.hfDirtMixer,
				terrainStyle.hfCommonHeight, terrainStyle.hfPlainsMixer,
				terrainStyle.hfMarshMixer,
				terrainStyle.hfHillHeight, terrainStyle.hfHillMixer,
				terrainStyle.hfMountainHeight, terrainStyle.hfMountainMixer,
				terrainStyle.hfSeaHeight, terrainStyle.hfSeaMixer
			};
			for (int i = 0; i < textures.Length; i++)
			{
				hash = hash * 31 + (textures[i] ? textures[i].GetInstanceID() : 0);
			}
			return hash;
		}
	}
}
