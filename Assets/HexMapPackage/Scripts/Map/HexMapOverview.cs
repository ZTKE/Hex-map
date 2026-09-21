using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Strategic mid/far renderer for streamed maps. It deliberately owns a
/// separate fine-paper political surface so the detailed HF terrain remains a
/// near-view concern. Country colors, broad logical relief, and
/// political borders are baked from the resident cell data.
/// </summary>
[DisallowMultipleComponent]
public sealed class HexMapOverview : MonoBehaviour
{
	const string overviewObjectName = "World Overview";
	const string borderObjectName = "World Political Borders";
	const int politicalRasterScale = 2;
	// Cap for very large countries; medium/small countries normalize to their own max depth.
	const int countryFillDistance = 72;
	// A-channel values at/below this are the dark rim + curve AA; blur must not touch them.
	const byte countryRimSoftMaxByte = 24;
	const int fillBlurRadius = 4;
	const int fillBlurPasses = 2;
	const float fillDepthContrast = 1.05f;
	const int oceanFillDistance = 120;
	const int oceanFillBlurRadius = 14;
	const int oceanFillBlurPasses = 2;
	const float oceanFillContrast = 1.62f;
	static readonly Color32 oceanAtlasPixel = new(20, 46, 97, 0);
	static readonly Color unownedLandColor = new(0.76f, 0.73f, 0.67f, 1f);

	Color[] processedCountryColors = new Color[256];
	bool[] processedCountryColorReady = new bool[256];

	GameObject overviewObject;
	GameObject borderObject;
	Mesh overviewMesh;
	Mesh borderMesh;
	Material overviewMaterial;
	Material borderMaterial;
	Texture2D overviewTexture;
	Texture2D overviewReliefTexture;
	ushort[] cachedRasterCountryIds;
	readonly HashSet<ushort> dirtyCountryScratch = new();
	bool hasMapBorders;
	int politicalBorderSegmentCount;

	public bool IsVisible => overviewObject && overviewObject.activeSelf;
	public int PoliticalBorderSegmentCount => politicalBorderSegmentCount;

	public void Rebuild(HexGrid grid)
	{
		if (!grid || grid.CellData == null || grid.CellData.Length == 0)
		{
			SetVisible(false);
			return;
		}

		EnsureOverviewRenderer();
		if (!overviewMaterial)
		{
			return;
		}

		grid.TakeOverviewDirtyState(
			out bool fullRebuild, out bool bordersDirty, dirtyCountryScratch);

		int scale = GetRasterScale(grid);
		int width = grid.CellCountX * scale;
		int height = grid.CellCountZ * scale;
		bool sizeChanged = !overviewTexture ||
			overviewTexture.width != width ||
			overviewTexture.height != height;
		if (sizeChanged)
		{
			fullRebuild = true;
		}

		RebuildOverviewMesh(grid);
		System.Array.Clear(processedCountryColorReady, 0,
			processedCountryColorReady.Length);
		if (fullRebuild || dirtyCountryScratch.Count == 0)
		{
			RebuildTexture(grid);
		}
		else if (!RebuildTexturePartial(grid, dirtyCountryScratch))
		{
			RebuildTexture(grid);
		}

		ApplyOverviewShaderDefaults(overviewMaterial);
		overviewMaterial.SetTexture("_MainTex", overviewTexture);
		overviewMaterial.SetTexture("_ReliefTex", overviewReliefTexture);

		if (bordersDirty || !hasMapBorders || !borderObject)
		{
			RebuildPoliticalBorders(grid);
		}
	}

	public void SetVisible(bool visible)
	{
		if (overviewObject && overviewObject.activeSelf != visible)
		{
			overviewObject.SetActive(visible);
		}
		if (borderObject)
		{
			bool showBorders = visible && hasMapBorders;
			if (borderObject.activeSelf != showBorders)
			{
				borderObject.SetActive(showBorders);
			}
		}
	}

	void EnsureOverviewRenderer()
	{
		if (!overviewObject)
		{
			Transform existing = transform.Find(overviewObjectName);
			overviewObject = existing ? existing.gameObject :
				new GameObject(overviewObjectName);
			overviewObject.transform.SetParent(transform, false);
			overviewObject.layer = gameObject.layer;
		}

		MeshFilter filter = overviewObject.GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = overviewObject.AddComponent<MeshFilter>();
		}
		MeshRenderer renderer = overviewObject.GetComponent<MeshRenderer>();
		if (!renderer)
		{
			renderer = overviewObject.AddComponent<MeshRenderer>();
		}
		ConfigureRenderer(renderer);

		if (!overviewMesh)
		{
			overviewMesh = new Mesh { name = "World Overview Mesh" };
			overviewMesh.MarkDynamic();
		}
		filter.sharedMesh = overviewMesh;

		if (!overviewMaterial)
		{
			Shader shader = Shader.Find("Hex Map/World Overview");
			if (!shader)
			{
				Debug.LogError(
					"World overview shader is missing; high-altitude LOD is unavailable.",
					this);
				return;
			}
			overviewMaterial = new Material(shader)
			{
				name = "World Overview Material",
				hideFlags = HideFlags.HideAndDontSave
			};
			ApplyOverviewShaderDefaults(overviewMaterial);
		}
		else
		{
			ApplyOverviewShaderDefaults(overviewMaterial);
		}
		renderer.sharedMaterial = overviewMaterial;
	}

	static void ApplyOverviewShaderDefaults(Material material)
	{
		if (!material)
		{
			return;
		}

		material.SetColor("_OceanTint", new Color(0.08f, 0.18f, 0.38f, 1f));
		material.SetFloat("_Brightness", 0.98f);
		material.SetFloat("_CountryBreathStrength", 0.040f);
		material.SetFloat("_CountryBreathTranslucency", 0.040f);
		material.SetFloat("_CountryRimStrength", 0.82f);
		material.SetFloat("_CountryRimStep", 0.52f);
		material.SetFloat("_CountryInnerGlow", 0.72f);
		material.SetFloat("_CountryRimPlateau", 0.12f);
		material.SetFloat("_CountryInteriorLift", 1.0f);
		material.SetFloat("_TerrainReliefStrength", 0.055f);
		material.SetFloat("_PaperGrain", 0.048f);
		material.SetFloat("_RimEmbossStrength", 1.0f);
		material.SetFloat("_OceanRimStrength", 0.62f);
		material.SetFloat("_OceanInteriorDepth", 0.74f);
		material.SetFloat("_BaseBorderHatch", 0.22f);
		material.SetFloat("_HoveredBorderHatch", 0.78f);
		material.SetFloat("_BorderHatchSpacing", 4.5f);
		material.SetFloat("_BorderHatchSlope", 0.72f);
		material.SetColor("_BorderHatchColor", new Color(0.01f, 0.008f, 0.005f, 1f));
		material.SetFloat("_SelectedStripeSpacing", 3.5f);
		material.SetFloat("_SelectedStripeSlope", 0.92f);
		material.SetFloat("_SelectedStripeGray", 0.80f);
		material.SetFloat("_SelectedStripeSoft", 0.022f);
		material.SetFloat("_SelectedStripeDuty", 0.50f);
	}

	static void ApplyBorderShaderDefaults(Material material)
	{
		if (!material)
		{
			return;
		}

		// Ink plate: solid core + soft pigment halo (print-quality national line).
		material.SetColor("_CoreColor", new Color(0.015f, 0.012f, 0.01f, 0.97f));
		material.SetColor("_HaloColor", new Color(0.08f, 0.055f, 0.035f, 0.38f));
		material.SetFloat("_BorderWidthPixels", 5f);
		material.SetFloat("_CoreWidthPixels", 2.1f);
	}

	void EnsureBorderRenderer()
	{
		if (!borderObject)
		{
			Transform existing = transform.Find(borderObjectName);
			borderObject = existing ? existing.gameObject :
				new GameObject(borderObjectName);
			borderObject.transform.SetParent(transform, false);
			borderObject.layer = gameObject.layer;
		}

		MeshFilter filter = borderObject.GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = borderObject.AddComponent<MeshFilter>();
		}
		MeshRenderer renderer = borderObject.GetComponent<MeshRenderer>();
		if (!renderer)
		{
			renderer = borderObject.AddComponent<MeshRenderer>();
		}
		ConfigureRenderer(renderer);

		if (!borderMesh)
		{
			borderMesh = new Mesh
			{
				name = "World Political Border Mesh",
				indexFormat = IndexFormat.UInt32
			};
			borderMesh.MarkDynamic();
		}
		filter.sharedMesh = borderMesh;

		if (!borderMaterial)
		{
			Shader shader = Shader.Find("Hex Map/World Political Border");
			if (!shader)
			{
				Debug.LogError(
					"World political border shader is missing.", this);
				return;
			}
			borderMaterial = new Material(shader)
			{
				name = "World Political Border Material",
				hideFlags = HideFlags.HideAndDontSave
			};
			ApplyBorderShaderDefaults(borderMaterial);
		}
		else
		{
			ApplyBorderShaderDefaults(borderMaterial);
		}
		renderer.sharedMaterial = borderMaterial;
	}

	static void ConfigureRenderer(MeshRenderer renderer)
	{
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = LightProbeUsage.Off;
		renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
	}

	void RebuildTexture(HexGrid grid)
	{
		// Main RGB = graded political base. Relief R = land height / ocean fill,
		// G = outward-hatch source country, B = outward hatch, A = inland amount.
		int scale = GetRasterScale(grid);
		int width = grid.CellCountX * scale;
		int height = grid.CellCountZ * scale;
		if (!overviewTexture || overviewTexture.width != width ||
			overviewTexture.height != height)
		{
			DestroyRuntimeObject(overviewTexture);
			overviewTexture = new Texture2D(
				width, height, TextureFormat.RGBA32, false, false)
			{
				name = "World Political Tint Texture",
				filterMode = FilterMode.Bilinear,
				anisoLevel = 0,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		if (!overviewReliefTexture || overviewReliefTexture.width != width ||
			overviewReliefTexture.height != height)
		{
			DestroyRuntimeObject(overviewReliefTexture);
			overviewReliefTexture = new Texture2D(
				width, height, TextureFormat.RGBA32, false, true)
			{
				name = "World Strategic Relief Texture",
				filterMode = FilterMode.Bilinear,
				anisoLevel = 0,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		overviewTexture.wrapModeU = grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overviewTexture.wrapModeV = TextureWrapMode.Clamp;
		overviewReliefTexture.wrapModeU = grid.Wrapping ?
			TextureWrapMode.Repeat : TextureWrapMode.Clamp;
		overviewReliefTexture.wrapModeV = TextureWrapMode.Clamp;

		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		System.Array.Clear(processedCountryColorReady, 0,
			processedCountryColorReady.Length);
		Color32[] pixels = new Color32[width * height];
		Color32[] reliefPixels = new Color32[width * height];
		ushort[] rasterCountryIds = new ushort[width * height];
		for (int y = 0, pixel = 0; y < height; y++)
		{
			float z = Mathf.Lerp(zMin, zMax, (y + 0.5f) / height);
			for (int x = 0; x < width; x++, pixel++)
			{
				float worldX = Mathf.Lerp(xMin, xMax, (x + 0.5f) / width);
				HexCoordinates coordinates = HexCoordinates.FromPosition(
					new Vector3(worldX, 0f, z));
				if (grid.TryGetCellIndex(coordinates, out int cellIndex))
				{
					pixels[pixel] = GetPoliticalAtlasPixel(grid, cellIndex);
					reliefPixels[pixel] = GetReliefAtlasPixel(grid, cellIndex);
					HexCellData cell = grid.CellData[cellIndex];
					rasterCountryIds[pixel] = cell.IsUnderwater ?
						(ushort)0 : cell.CountryId;
				}
				else
				{
					pixels[pixel] = oceanAtlasPixel;
					reliefPixels[pixel] = Color.clear;
				}
			}
		}
		BakeBorderShadowDistance(
			rasterCountryIds, reliefPixels, width, height, grid.Wrapping, null, scale);
		BlurReliefFillDepth(
			pixels, reliefPixels, rasterCountryIds, width, height, grid.Wrapping, null);
		BakeOceanFillDistance(pixels, reliefPixels, width, height, grid.Wrapping);
		BlurReliefOceanFill(pixels, reliefPixels, width, height, grid.Wrapping);

		overviewTexture.SetPixels32(pixels);
		overviewTexture.Apply(false, false);
		overviewReliefTexture.SetPixels32(reliefPixels);
		overviewReliefTexture.Apply(false, false);
		CacheRasterCountryIds(rasterCountryIds);
	}

	bool RebuildTexturePartial(HexGrid grid, HashSet<ushort> dirtyCountries)
	{
		if (!overviewTexture || !overviewReliefTexture ||
			cachedRasterCountryIds == null)
		{
			return false;
		}

		int scale = GetRasterScale(grid);
		int width = grid.CellCountX * scale;
		int height = grid.CellCountZ * scale;
		if (overviewTexture.width != width || overviewTexture.height != height ||
			cachedRasterCountryIds.Length != width * height)
		{
			return false;
		}

		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		System.Array.Clear(processedCountryColorReady, 0,
			processedCountryColorReady.Length);

		Color32[] pixels = overviewTexture.GetPixels32();
		Color32[] reliefPixels = overviewReliefTexture.GetPixels32();
		ushort[] rasterCountryIds = new ushort[width * height];
		bool touched = false;
		for (int y = 0, pixel = 0; y < height; y++)
		{
			float z = Mathf.Lerp(zMin, zMax, (y + 0.5f) / height);
			for (int x = 0; x < width; x++, pixel++)
			{
				float worldX = Mathf.Lerp(xMin, xMax, (x + 0.5f) / width);
				HexCoordinates coordinates = HexCoordinates.FromPosition(
					new Vector3(worldX, 0f, z));
				ushort previousId = cachedRasterCountryIds[pixel];
				ushort countryId = 0;
				int cellIndex = -1;
				bool hasCell = grid.TryGetCellIndex(coordinates, out cellIndex);
				if (hasCell)
				{
					HexCellData cell = grid.CellData[cellIndex];
					countryId = cell.IsUnderwater ? (ushort)0 : cell.CountryId;
				}
				rasterCountryIds[pixel] = countryId;

				bool pixelDirty =
					dirtyCountries.Contains(countryId) ||
					dirtyCountries.Contains(previousId) ||
					previousId != countryId;
				if (!pixelDirty)
				{
					continue;
				}

				touched = true;
				if (hasCell)
				{
					pixels[pixel] = GetPoliticalAtlasPixel(grid, cellIndex);
					Color32 relief = GetReliefAtlasPixel(grid, cellIndex);
					if (countryId == 0)
					{
						// Unowned / ocean-cleared land: no political grade or hatch.
						relief.b = 0;
						relief.a = grid.CellData[cellIndex].IsUnderwater ?
							(byte)0 : (byte)255;
					}
					reliefPixels[pixel] = relief;
				}
				else
				{
					pixels[pixel] = oceanAtlasPixel;
					reliefPixels[pixel] = Color.clear;
				}
			}
		}

		if (!touched)
		{
			CacheRasterCountryIds(rasterCountryIds);
			return true;
		}

		// Ownership seams always dirty both sides for distance rebake.
		for (int pixel = 0; pixel < rasterCountryIds.Length; pixel++)
		{
			ushort previousId = cachedRasterCountryIds[pixel];
			ushort countryId = rasterCountryIds[pixel];
			if (previousId == countryId)
			{
				continue;
			}
			if (previousId != 0)
			{
				dirtyCountries.Add(previousId);
			}
			if (countryId != 0)
			{
				dirtyCountries.Add(countryId);
			}
		}

		BakeBorderShadowDistance(
			rasterCountryIds, reliefPixels, width, height, grid.Wrapping,
			dirtyCountries, scale);
		BlurReliefFillDepth(
			pixels, reliefPixels, rasterCountryIds, width, height, grid.Wrapping,
			dirtyCountries);

		overviewTexture.SetPixels32(pixels);
		overviewTexture.Apply(false, false);
		overviewReliefTexture.SetPixels32(reliefPixels);
		overviewReliefTexture.Apply(false, false);
		CacheRasterCountryIds(rasterCountryIds);
		return true;
	}

	void CacheRasterCountryIds(ushort[] rasterCountryIds)
	{
		if (cachedRasterCountryIds == null ||
			cachedRasterCountryIds.Length != rasterCountryIds.Length)
		{
			cachedRasterCountryIds = new ushort[rasterCountryIds.Length];
		}
		System.Array.Copy(
			rasterCountryIds, cachedRasterCountryIds, rasterCountryIds.Length);
	}

	static int GetRasterScale(HexGrid grid)
	{
		int maxTextureSize = SystemInfo.maxTextureSize;
		return grid.CellCountX * politicalRasterScale <= maxTextureSize &&
			grid.CellCountZ * politicalRasterScale <= maxTextureSize ?
			politicalRasterScale : 1;
	}

	Color32 GetPoliticalAtlasPixel(HexGrid grid, int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		if (cell.IsUnderwater)
		{
			return oceanAtlasPixel;
		}

		Color political = GetProcessedCountryColor(grid, cell.CountryId);
		return ToOpaqueColor32(political);
	}

	Color GetProcessedCountryColor(HexGrid grid, ushort countryId)
	{
		if (countryId == 0)
		{
			return unownedLandColor;
		}

		if (countryId >= processedCountryColors.Length)
		{
			return unownedLandColor;
		}

		if (!processedCountryColorReady[countryId])
		{
			if (!grid.TryGetCountryColor(countryId, out Color32 raw))
			{
				processedCountryColors[countryId] = unownedLandColor;
			}
			else
			{
				processedCountryColors[countryId] =
					ProcessPoliticalColor(raw, countryId);
			}
			processedCountryColorReady[countryId] = true;
		}

		return processedCountryColors[countryId];
	}

	static Color ProcessPoliticalColor(Color32 raw, ushort countryId) =>
		HexCartographyStyle.Current.GetCountryColor(countryId, raw);

	static float Hash21(float x, float y)
	{
		Vector3 p3 = new(
			Frac(x * 0.1031f),
			Frac(y * 0.1031f),
			Frac(x * 0.1031f));
		float dot = Vector3.Dot(
			p3, new Vector3(p3.y, p3.z, p3.x) + Vector3.one * 33.33f);
		p3 += new Vector3(dot, dot, dot);
		return Frac((p3.x + p3.y) * p3.z);
	}

	static float Frac(float value) => value - Mathf.Floor(value);

	static bool HasCoastNeighbour(
		bool[] isLand, int width, int height,
		int x, int y, bool wrapping)
	{
		bool land = isLand[y * width + x];
		for (int yOffset = -1; yOffset <= 1; yOffset++)
		{
			for (int xOffset = -1; xOffset <= 1; xOffset++)
			{
				if (xOffset == 0 && yOffset == 0)
				{
					continue;
				}

				int nextY = y + yOffset;
				if (nextY < 0 || nextY >= height)
				{
					return true;
				}

				int nextX = x + xOffset;
				if (wrapping)
				{
					nextX = (nextX + width) % width;
				}
				else if (nextX < 0 || nextX >= width)
				{
					return true;
				}

				if (isLand[nextY * width + nextX] != land)
				{
					return true;
				}
			}
		}

		return false;
	}

	static void BlurReliefOceanFill(
		Color32[] pixels, Color32[] reliefPixels,
		int width, int height, bool wrapping)
	{
		int pixelCount = pixels.Length;
		bool[] isLand = new bool[pixelCount];
		float[] buffer = new float[pixelCount];
		float[] scratch = new float[pixelCount];
		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			isLand[pixel] = pixels[pixel].a > 0;
			buffer[pixel] = isLand[pixel] ? 0f : reliefPixels[pixel].r / 255f;
		}

		for (int pass = 0; pass < oceanFillBlurPasses; pass++)
		{
			BoxBlurHorizontal(buffer, scratch, width, height, wrapping, oceanFillBlurRadius);
			BoxBlurVertical(scratch, buffer, width, height, oceanFillBlurRadius);
		}

		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			if (isLand[pixel])
			{
				continue;
			}

			float contrasted = Mathf.Pow(
				Mathf.Clamp01(buffer[pixel]), oceanFillContrast);
			Color32 relief = reliefPixels[pixel];
			relief.r = (byte)Mathf.RoundToInt(contrasted * 255f);
			reliefPixels[pixel] = relief;
		}
	}

	/// <summary>
	/// Long-range ocean inward distance into relief R. Land keeps height in R;
	/// ocean gets a wide coast-to-abyss fill in relief R.
	/// </summary>
	static void BakeOceanFillDistance(
		Color32[] pixels, Color32[] reliefPixels,
		int width, int height, bool wrapping)
	{
		int pixelCount = pixels.Length;
		bool[] isLand = new bool[pixelCount];
		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			isLand[pixel] = pixels[pixel].a > 0;
		}

		ushort[] distances = new ushort[pixelCount];
		for (int i = 0; i < pixelCount; i++)
		{
			distances[i] = ushort.MaxValue;
		}

		int[] queue = new int[pixelCount];
		int queueHead = 0;
		int queueTail = 0;

		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int pixel = y * width + x;
				if (isLand[pixel])
				{
					continue;
				}

				if (HasCoastNeighbour(isLand, width, height, x, y, wrapping))
				{
					distances[pixel] = 0;
					queue[queueTail++] = pixel;
				}
			}
		}

		while (queueHead < queueTail)
		{
			int pixel = queue[queueHead++];
			int nextDistance = distances[pixel] + 1;
			if (nextDistance >= oceanFillDistance)
			{
				continue;
			}

			int x = pixel % width;
			int y = pixel / width;
			for (int yOffset = -1; yOffset <= 1; yOffset++)
			{
				for (int xOffset = -1; xOffset <= 1; xOffset++)
				{
					if (xOffset == 0 && yOffset == 0)
					{
						continue;
					}

					int nextY = y + yOffset;
					if (nextY < 0 || nextY >= height)
					{
						continue;
					}

					int nextX = x + xOffset;
					if (wrapping)
					{
						nextX = (nextX + width) % width;
					}
					else if (nextX < 0 || nextX >= width)
					{
						continue;
					}

					int nextPixel = nextY * width + nextX;
					if (!isLand[nextPixel] && distances[nextPixel] > nextDistance)
					{
						distances[nextPixel] = (ushort)nextDistance;
						queue[queueTail++] = nextPixel;
					}
				}
			}
		}

		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			if (isLand[pixel])
			{
				continue;
			}

			int distance = distances[pixel] == ushort.MaxValue ?
				oceanFillDistance : distances[pixel];
			byte proximity = distance >= oceanFillDistance ? (byte)0 :
				(byte)Mathf.RoundToInt(
					(1f - distance / (float)oceanFillDistance) * 255f);
			Color32 relief = reliefPixels[pixel];
			relief.r = proximity;
			reliefPixels[pixel] = relief;
		}
	}

	static void BlurReliefFillDepth(
		Color32[] politicalPixels, Color32[] reliefPixels, ushort[] countryIds,
		int width, int height, bool wrapping, HashSet<ushort> dirtyCountries)
	{
		int pixelCount = reliefPixels.Length;
		float[] buffer = new float[pixelCount];
		float[] scratch = new float[pixelCount];
		bool[] isLand = new bool[pixelCount];
		byte[] preservedRimA = new byte[pixelCount];
		// Protect the whole rim + 1px curve AA band; blur only deeper wash.
		bool[] protectDarkRim = new bool[pixelCount];
		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			isLand[pixel] = politicalPixels[pixel].a > 0 && countryIds[pixel] != 0;
			buffer[pixel] = reliefPixels[pixel].a / 255f;
			preservedRimA[pixel] = reliefPixels[pixel].a;
			protectDarkRim[pixel] = isLand[pixel] &&
				reliefPixels[pixel].a <= countryRimSoftMaxByte;
		}

		for (int pass = 0; pass < fillBlurPasses; pass++)
		{
			BoxBlurHorizontalCountryMasked(
				buffer, scratch, countryIds, protectDarkRim,
				width, height, wrapping, fillBlurRadius);
			BoxBlurVerticalCountryMasked(
				scratch, buffer, countryIds, protectDarkRim,
				width, height, fillBlurRadius);
		}

		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			if (!isLand[pixel])
			{
				continue;
			}
			if (dirtyCountries != null &&
				!dirtyCountries.Contains(countryIds[pixel]))
			{
				continue;
			}

			Color32 relief = reliefPixels[pixel];
			if (protectDarkRim[pixel])
			{
				relief.a = preservedRimA[pixel];
			}
			else
			{
				float contrasted = Mathf.Pow(
					Mathf.Clamp01(buffer[pixel]), fillDepthContrast);
				relief.a = (byte)Mathf.Max(
					countryRimSoftMaxByte + 1,
					Mathf.RoundToInt(contrasted * 255f));
			}
			reliefPixels[pixel] = relief;
		}
	}

	static void BoxBlurHorizontalCountryMasked(
		float[] source, float[] destination, ushort[] countryIds,
		bool[] protectDarkRim, int width, int height, bool wrapping, int blurRadius)
	{
		for (int y = 0; y < height; y++)
		{
			int row = y * width;
			for (int x = 0; x < width; x++)
			{
				int pixel = row + x;
				ushort countryId = countryIds[pixel];
				if (countryId == 0 || protectDarkRim[pixel])
				{
					destination[pixel] = source[pixel];
					continue;
				}

				float sum = 0f;
				int count = 0;
				for (int offset = -blurRadius; offset <= blurRadius; offset++)
				{
					int sampleX = x + offset;
					if (wrapping)
					{
						sampleX = (sampleX + width) % width;
					}
					else
					{
						sampleX = Mathf.Clamp(sampleX, 0, width - 1);
					}
					int sample = row + sampleX;
					if (countryIds[sample] != countryId || protectDarkRim[sample])
					{
						continue;
					}
					sum += source[sample];
					count++;
				}
				destination[pixel] = count > 0 ? sum / count : source[pixel];
			}
		}
	}

	static void BoxBlurVerticalCountryMasked(
		float[] source, float[] destination, ushort[] countryIds,
		bool[] protectDarkRim, int width, int height, int blurRadius)
	{
		for (int y = 0; y < height; y++)
		{
			int row = y * width;
			for (int x = 0; x < width; x++)
			{
				int pixel = row + x;
				ushort countryId = countryIds[pixel];
				if (countryId == 0 || protectDarkRim[pixel])
				{
					destination[pixel] = source[pixel];
					continue;
				}

				float sum = 0f;
				int count = 0;
				for (int offset = -blurRadius; offset <= blurRadius; offset++)
				{
					int sampleY = Mathf.Clamp(y + offset, 0, height - 1);
					int sample = sampleY * width + x;
					if (countryIds[sample] != countryId || protectDarkRim[sample])
					{
						continue;
					}
					sum += source[sample];
					count++;
				}
				destination[pixel] = count > 0 ? sum / count : source[pixel];
			}
		}
	}

	static void BoxBlurHorizontal(
		float[] source, float[] destination,
		int width, int height, bool wrapping, int blurRadius)
	{
		int diameter = blurRadius * 2 + 1;
		for (int y = 0; y < height; y++)
		{
			int row = y * width;
			for (int x = 0; x < width; x++)
			{
				float sum = 0f;
				for (int offset = -blurRadius; offset <= blurRadius; offset++)
				{
					int sampleX = x + offset;
					if (wrapping)
					{
						sampleX = (sampleX + width) % width;
					}
					else
					{
						sampleX = Mathf.Clamp(sampleX, 0, width - 1);
					}
					sum += source[row + sampleX];
				}
				destination[row + x] = sum / diameter;
			}
		}
	}

	static void BoxBlurVertical(
		float[] source, float[] destination, int width, int height, int blurRadius)
	{
		int diameter = blurRadius * 2 + 1;
		for (int y = 0; y < height; y++)
		{
			int row = y * width;
			for (int x = 0; x < width; x++)
			{
				float sum = 0f;
				for (int offset = -blurRadius; offset <= blurRadius; offset++)
				{
					int sampleY = Mathf.Clamp(y + offset, 0, height - 1);
					sum += source[sampleY * width + x];
				}
				destination[row + x] = sum / diameter;
			}
		}
	}

	static Color32 ToOpaqueColor32(Color color)
	{
		return new Color32(
			(byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
			(byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
			(byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f),
			255);
	}

	static Color32 GetReliefAtlasPixel(HexGrid grid, int cellIndex)
	{
		HexCellData cell = grid.CellData[cellIndex];
		if (cell.IsUnderwater)
		{
			return new Color32(0, 0, 0, 0);
		}

		float landformHeight = cell.landform switch
		{
			HexLandform.Mountain => 0.94f,
			HexLandform.Plateau => 0.69f,
			HexLandform.Hill => 0.56f,
			_ => 0.18f
		};
		landformHeight += Mathf.Clamp(cell.Elevation, 0, 8) * 0.012f;
		byte height = (byte)Mathf.RoundToInt(
			Mathf.Clamp01(landformHeight) * 255f);
		byte vegetation = (byte)Mathf.RoundToInt(
			Mathf.Clamp01(cell.VegetationDensity / 100f) * 255f);
		return new Color32(height, vegetation, 0, 255);
	}

	/// <summary>
	/// Bake inward distance masks into relief A (country fill wash / dark rim).
	/// Outward hover hatch (G/B) is baked separately afterward.
	/// </summary>
	static void BakeBorderShadowDistance(
		ushort[] countryIds, Color32[] reliefPixels,
		int width, int height, bool wrapping, HashSet<ushort> dirtyCountries,
		int rasterScale)
	{
		// Chamfer ≈ Euclidean: iso-distance fronts read as natural curves on
		// irregular borders instead of Chebyshev stair-blocks.
		const float orthoStep = 1f;
		const float diagStep = 1.41421356f;
		const float washStartEncoded = countryRimSoftMaxByte / 255f;
		int pixelCount = countryIds.Length;
		float[] distances = new float[pixelCount];
		for (int i = 0; i < pixelCount; i++)
		{
			distances[i] = float.PositiveInfinity;
		}
		int[] queue = new int[pixelCount];
		int queueHead = 0;
		int queueTail = 0;

		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int pixel = y * width + x;
				ushort countryId = countryIds[pixel];
				if (countryId == 0)
				{
					continue;
				}
				if (dirtyCountries != null && !dirtyCountries.Contains(countryId))
				{
					continue;
				}
				if (HasDifferentCountryNeighbour(
					countryIds, width, height, x, y, countryId, wrapping))
				{
					distances[pixel] = 0f;
					queue[queueTail++] = pixel;
				}
			}
		}

		while (queueHead < queueTail)
		{
			int pixel = queue[queueHead++];
			float current = distances[pixel];
			int x = pixel % width;
			int y = pixel / width;
			ushort countryId = countryIds[pixel];
			for (int yOffset = -1; yOffset <= 1; yOffset++)
			{
				for (int xOffset = -1; xOffset <= 1; xOffset++)
				{
					if (xOffset == 0 && yOffset == 0)
					{
						continue;
					}
					int nextY = y + yOffset;
					if (nextY < 0 || nextY >= height)
					{
						continue;
					}
					int nextX = x + xOffset;
					if (wrapping)
					{
						nextX = (nextX + width) % width;
					}
					else if (nextX < 0 || nextX >= width)
					{
						continue;
					}
					int nextPixel = nextY * width + nextX;
					if (countryIds[nextPixel] != countryId)
					{
						continue;
					}

					float step = (xOffset == 0 || yOffset == 0) ?
						orthoStep : diagStep;
					float nextDistance = current + step;
					if (nextDistance + 0.001f < distances[nextPixel])
					{
						distances[nextPixel] = nextDistance;
						queue[queueTail++] = nextPixel;
					}
				}
			}
		}

		int maxCountryId = 0;
		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			ushort countryId = countryIds[pixel];
			if (countryId > maxCountryId)
			{
				maxCountryId = countryId;
			}
		}
		float[] maxDepthByCountry = new float[maxCountryId + 1];
		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			ushort countryId = countryIds[pixel];
			if (countryId == 0)
			{
				continue;
			}
			if (dirtyCountries != null && !dirtyCountries.Contains(countryId))
			{
				continue;
			}
			float distance = distances[pixel];
			if (float.IsInfinity(distance))
			{
				continue;
			}
			if (distance > maxDepthByCountry[countryId])
			{
				maxDepthByCountry[countryId] = distance;
			}
		}

		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			ushort countryId = countryIds[pixel];
			if (countryId == 0)
			{
				continue;
			}
			if (dirtyCountries != null && !dirtyCountries.Contains(countryId))
			{
				continue;
			}

			float distance = float.IsInfinity(distances[pixel]) ?
				countryFillDistance : distances[pixel];
			int x = pixel % width;
			int y = pixel / width;

			float darkRimPixels = GetDarkRimWidthPixels(x, y, countryId);
			float countryMax = maxDepthByCountry[countryId];
			float reference = countryMax <= 0.001f ?
				1f :
				Mathf.Clamp(countryMax * 0.70f, 10f, countryFillDistance);
			float rampReference = Mathf.Max(
				reference - darkRimPixels, reference * 0.55f);

			// Continuous SDF through the dark rim (no flat inland=0 plateau).
			float inland;
			if (distance < darkRimPixels)
			{
				float t = darkRimPixels <= 0.001f ?
					1f :
					Mathf.Clamp01(distance / darkRimPixels);
				inland = t * washStartEncoded;
			}
			else
			{
				float rampDistance = distance - darkRimPixels;
				float wash = rampReference <= 0.001f ?
					1f : Mathf.Clamp01(rampDistance / rampReference);
				inland = Mathf.Lerp(washStartEncoded, 1f, wash);
			}

			byte fillInland = inland <= 0f ?
				(byte)0 :
				(byte)Mathf.Clamp(
					Mathf.RoundToInt(inland * 255f), 1, 255);

			Color32 relief = reliefPixels[pixel];
			relief.a = fillInland;
			reliefPixels[pixel] = relief;
		}

		// Hover hatch lives outside each country (neighbor land + ocean).
		BakeOutwardCountryHatch(
			countryIds, reliefPixels, width, height, wrapping, rasterScale);
	}

	/// <summary>
	/// Bake exterior hatch proximity into relief B, labeled by source country in G.
	/// Flood stays outside the source country so strokes sit on foreign land / ocean.
	/// </summary>
	static void BakeOutwardCountryHatch(
		ushort[] countryIds, Color32[] reliefPixels,
		int width, int height, bool wrapping, int rasterScale)
	{
		const float orthoStep = 1f;
		const float diagStep = 1.41421356f;
		// Matches GetHatchBandEnd max (2 cells) + slack.
		float maxHatchBandPixels =
			2f * Mathf.Max(1f, rasterScale) + diagStep + 0.01f;
		int pixelCount = countryIds.Length;
		float[] distances = new float[pixelCount];
		ushort[] sourceCountry = new ushort[pixelCount];
		for (int i = 0; i < pixelCount; i++)
		{
			distances[i] = float.PositiveInfinity;
			sourceCountry[i] = 0;
			Color32 relief = reliefPixels[i];
			relief.g = 0;
			relief.b = 0;
			reliefPixels[i] = relief;
		}

		// Re-relaxations can enqueue the same pixel multiple times; List avoids
		// the fixed-array IndexOutOfRange that a pixelCount buffer hits.
		var queue = new List<int>(Mathf.Min(pixelCount, width * 8));
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int pixel = y * width + x;
				ushort selfId = countryIds[pixel];
				for (int yOffset = -1; yOffset <= 1; yOffset++)
				{
					for (int xOffset = -1; xOffset <= 1; xOffset++)
					{
						if (xOffset == 0 && yOffset == 0)
						{
							continue;
						}
						int nextY = y + yOffset;
						if (nextY < 0 || nextY >= height)
						{
							continue;
						}
						int nextX = x + xOffset;
						if (wrapping)
						{
							nextX = (nextX + width) % width;
						}
						else if (nextX < 0 || nextX >= width)
						{
							continue;
						}
						ushort neighborId = countryIds[nextY * width + nextX];
						if (neighborId == 0 || neighborId == selfId)
						{
							continue;
						}

						// Pixel is immediately outside neighborId's territory.
						if (distances[pixel] > 0f)
						{
							distances[pixel] = 0f;
							sourceCountry[pixel] = neighborId;
							queue.Add(pixel);
						}
					}
				}
			}
		}

		for (int queueHead = 0; queueHead < queue.Count; queueHead++)
		{
			int pixel = queue[queueHead];
			float current = distances[pixel];
			ushort label = sourceCountry[pixel];
			if (label == 0)
			{
				continue;
			}
			int x = pixel % width;
			int y = pixel / width;
			for (int yOffset = -1; yOffset <= 1; yOffset++)
			{
				for (int xOffset = -1; xOffset <= 1; xOffset++)
				{
					if (xOffset == 0 && yOffset == 0)
					{
						continue;
					}
					int nextY = y + yOffset;
					if (nextY < 0 || nextY >= height)
					{
						continue;
					}
					int nextX = x + xOffset;
					if (wrapping)
					{
						nextX = (nextX + width) % width;
					}
					else if (nextX < 0 || nextX >= width)
					{
						continue;
					}
					int nextPixel = nextY * width + nextX;
					// Stay in the exterior of the labeled country.
					if (countryIds[nextPixel] == label)
					{
						continue;
					}

					float step = (xOffset == 0 || yOffset == 0) ?
						orthoStep : diagStep;
					float nextDistance = current + step;
					if (nextDistance >= maxHatchBandPixels)
					{
						continue;
					}
					if (nextDistance + 0.001f < distances[nextPixel])
					{
						distances[nextPixel] = nextDistance;
						sourceCountry[nextPixel] = label;
						queue.Add(nextPixel);
					}
				}
			}
		}

		for (int pixel = 0; pixel < pixelCount; pixel++)
		{
			ushort label = sourceCountry[pixel];
			if (label == 0 || float.IsInfinity(distances[pixel]))
			{
				continue;
			}

			int x = pixel % width;
			int y = pixel / width;
			float hatchBandEnd = GetHatchBandEnd(x, y, label, rasterScale);
			float distance = distances[pixel];
			if (distance >= hatchBandEnd)
			{
				continue;
			}

			byte hatchProximity = (byte)Mathf.RoundToInt(
				(1f - distance / hatchBandEnd) * 255f);
			Color32 relief = reliefPixels[pixel];
			relief.g = label > 255 ? (byte)255 : (byte)label;
			relief.b = hatchProximity;
			reliefPixels[pixel] = relief;
		}
	}

	/// <summary>
	/// Exterior hover-hatch width in atlas pixels, smooth random in about [0.5, 2] cells.
	/// </summary>
	static float GetHatchBandEnd(int x, int y, ushort countryId, int rasterScale)
	{
		float jitter = RimBandJitter(x, y, countryId);
		const float minBandCells = 0.5f;
		const float maxBandCells = 2.0f;
		float bandCells = Mathf.Lerp(minBandCells, maxBandCells, jitter);
		float pixelsPerCell = Mathf.Max(0.5f, rasterScale);
		return bandCells * pixelsPerCell;
	}

	/// <summary>
	/// Contour-local dark-rim width in atlas pixels, smooth random in [0.5, 1].
	/// Wash rim only — plate emboss is hex-edge analytical in the overview shader.
	/// </summary>
	static float GetDarkRimWidthPixels(int x, int y, ushort countryId)
	{
		const float cell = 5.5f;
		float fx = x / cell;
		float fy = y / cell;
		int x0 = Mathf.FloorToInt(fx);
		int y0 = Mathf.FloorToInt(fy);
		float tx = fx - x0;
		float ty = fy - y0;
		tx = tx * tx * (3f - 2f * tx);
		ty = ty * ty * (3f - 2f * ty);
		float v00 = RimBandJitter(x0, y0, countryId);
		float v10 = RimBandJitter(x0 + 1, y0, countryId);
		float v01 = RimBandJitter(x0, y0 + 1, countryId);
		float v11 = RimBandJitter(x0 + 1, y0 + 1, countryId);
		float jitter = Mathf.Lerp(
			Mathf.Lerp(v00, v10, tx),
			Mathf.Lerp(v01, v11, tx),
			ty);
		return Mathf.Lerp(0.5f, 1f, jitter);
	}

	static bool HasDifferentCountryNeighbour(
		ushort[] countryIds, int width, int height,
		int x, int y, ushort countryId, bool wrapping)
	{
		for (int yOffset = -1; yOffset <= 1; yOffset++)
		{
			for (int xOffset = -1; xOffset <= 1; xOffset++)
			{
				if (xOffset == 0 && yOffset == 0)
				{
					continue;
				}
				int nextY = y + yOffset;
				if (nextY < 0 || nextY >= height)
				{
					return true;
				}
				int nextX = x + xOffset;
				if (wrapping)
				{
					nextX = (nextX + width) % width;
				}
				else if (nextX < 0 || nextX >= width)
				{
					return true;
				}
				if (countryIds[nextY * width + nextX] != countryId)
				{
					return true;
				}
			}
		}
		return false;
	}

	static float RimBandJitter(int x, int y, ushort countryId)
	{
		uint h = (uint)(x * 73856093 ^ y * 19349663 ^ countryId * 83492791);
		h ^= h >> 16;
		h *= 0x7feb352d;
		h ^= h >> 15;
		h *= 0x846ca68b;
		h ^= h >> 16;
		return (h & 0xFFFFu) / 65535f;
	}

	void RebuildOverviewMesh(HexGrid grid)
	{
		GetMapBounds(grid, out float xMin, out float xMax,
			out float zMin, out float zMax);
		float y = -1f;
		float width = xMax - xMin;
		int copyCount = grid.Wrapping ? 3 : 1;
		Vector3[] vertices = new Vector3[copyCount * 4];
		Vector2[] uvs = new Vector2[copyCount * 4];
		int[] triangles = new int[copyCount * 6];
		for (int copy = 0; copy < copyCount; copy++)
		{
			float offset = grid.Wrapping ? (copy - 1) * width : 0f;
			int vertex = copy * 4;
			vertices[vertex] = new Vector3(xMin + offset, y, zMin);
			vertices[vertex + 1] = new Vector3(xMax + offset, y, zMin);
			vertices[vertex + 2] = new Vector3(xMax + offset, y, zMax);
			vertices[vertex + 3] = new Vector3(xMin + offset, y, zMax);
			uvs[vertex] = Vector2.zero;
			uvs[vertex + 1] = Vector2.right;
			uvs[vertex + 2] = Vector2.one;
			uvs[vertex + 3] = Vector2.up;

			int triangle = copy * 6;
			triangles[triangle] = vertex;
			triangles[triangle + 1] = vertex + 2;
			triangles[triangle + 2] = vertex + 1;
			triangles[triangle + 3] = vertex;
			triangles[triangle + 4] = vertex + 3;
			triangles[triangle + 5] = vertex + 2;
		}

		overviewMesh.Clear();
		overviewMesh.vertices = vertices;
		overviewMesh.uv = uvs;
		overviewMesh.triangles = triangles;
		overviewMesh.RecalculateBounds();
	}

	void RebuildPoliticalBorders(HexGrid grid)
	{
		EnsureBorderRenderer();
		if (!borderMesh || !borderMaterial)
		{
			return;
		}

		List<Vector3> segments = new();
		int politicalSegments = 0;
		for (int cellIndex = 0; cellIndex < grid.CellData.Length; cellIndex++)
		{
			HexCellData cell = grid.CellData[cellIndex];
			if (cell.IsUnderwater)
			{
				continue;
			}
			for (HexDirection direction = HexDirection.NE;
				direction <= HexDirection.NW; direction++)
			{
				bool hasNeighbor = grid.TryGetCellIndex(
					cell.coordinates.Step(direction), out int neighborIndex);
				bool coastline = !hasNeighbor ||
					grid.CellData[neighborIndex].IsUnderwater;
				bool politicalBorder = false;
				if (hasNeighbor && !coastline && neighborIndex > cellIndex)
				{
					ushort neighborCountry =
						grid.CellData[neighborIndex].CountryId;
					politicalBorder = cell.CountryId != 0 &&
						neighborCountry != 0 &&
						neighborCountry != cell.CountryId;
				}
				if (!coastline && !politicalBorder)
				{
					continue;
				}

				Vector3 center = grid.CellPositions[cellIndex];
				segments.Add(center + HexMetrics.GetFirstCorner(direction));
				segments.Add(center + HexMetrics.GetSecondCorner(direction));
				if (politicalBorder)
				{
					politicalSegments += 1;
				}
			}
		}

		hasMapBorders = segments.Count > 0;
		politicalBorderSegmentCount = politicalSegments;
		if (!hasMapBorders)
		{
			borderMesh.Clear();
			borderObject.SetActive(false);
			return;
		}

		float worldWidth = grid.CellCountX * HexMetrics.innerDiameter;
		int copyCount = grid.Wrapping ? 3 : 1;
		int segmentCount = segments.Count / 2;
		Vector3[] vertices = new Vector3[segmentCount * copyCount * 4];
		Vector3[] directions = new Vector3[vertices.Length];
		Vector2[] uvs = new Vector2[vertices.Length];
		int[] triangles = new int[segmentCount * copyCount * 6];
		int vertexCursor = 0;
		int triangleCursor = 0;
		for (int copy = 0; copy < copyCount; copy++)
		{
			float copyOffset = grid.Wrapping ? (copy - 1) * worldWidth : 0f;
			for (int segment = 0; segment < segmentCount; segment++)
			{
				Vector3 a = segments[segment * 2];
				Vector3 b = segments[segment * 2 + 1];
				a.x += copyOffset;
				b.x += copyOffset;
				a.y = b.y = -0.82f;
				Vector3 direction = (b - a).normalized;

				vertices[vertexCursor] = a;
				vertices[vertexCursor + 1] = a;
				vertices[vertexCursor + 2] = b;
				vertices[vertexCursor + 3] = b;
				directions[vertexCursor] = direction;
				directions[vertexCursor + 1] = direction;
				directions[vertexCursor + 2] = direction;
				directions[vertexCursor + 3] = direction;
				uvs[vertexCursor] = new Vector2(0f, 0f);
				uvs[vertexCursor + 1] = new Vector2(1f, 0f);
				uvs[vertexCursor + 2] = new Vector2(1f, 1f);
				uvs[vertexCursor + 3] = new Vector2(0f, 1f);

				triangles[triangleCursor] = vertexCursor;
				triangles[triangleCursor + 1] = vertexCursor + 1;
				triangles[triangleCursor + 2] = vertexCursor + 2;
				triangles[triangleCursor + 3] = vertexCursor;
				triangles[triangleCursor + 4] = vertexCursor + 2;
				triangles[triangleCursor + 5] = vertexCursor + 3;
				vertexCursor += 4;
				triangleCursor += 6;
			}
		}

		borderMesh.Clear();
		borderMesh.vertices = vertices;
		borderMesh.normals = directions;
		borderMesh.uv = uvs;
		borderMesh.triangles = triangles;
		borderMesh.RecalculateBounds();
		borderObject.SetActive(IsVisible);
	}

	static void GetMapBounds(
		HexGrid grid, out float xMin, out float xMax,
		out float zMin, out float zMax)
	{
		float width = grid.CellCountX * HexMetrics.innerDiameter;
		float rowHeight = HexMetrics.outerRadius * 1.5f;
		float height = grid.CellCountZ * rowHeight;
		xMin = -HexMetrics.innerDiameter * 0.5f;
		xMax = xMin + width;
		zMin = -rowHeight * 0.5f;
		zMax = zMin + height;
	}

	void OnDestroy()
	{
		DestroyRuntimeObject(overviewMaterial);
		DestroyRuntimeObject(borderMaterial);
		DestroyRuntimeObject(overviewTexture);
		DestroyRuntimeObject(overviewReliefTexture);
		DestroyRuntimeObject(overviewMesh);
		DestroyRuntimeObject(borderMesh);
	}

	static void DestroyRuntimeObject(Object value)
	{
		if (!value)
		{
			return;
		}
		if (Application.isPlaying)
		{
			Destroy(value);
		}
		else
		{
			DestroyImmediate(value);
		}
	}
}
