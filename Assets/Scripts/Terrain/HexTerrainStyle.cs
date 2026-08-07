using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Selects the single authority for the rendered and interactive map surface.
/// Legacy Catlike remains available for old scenes and debugging, while new
/// maps use the original HoneyFramework stamp reconstruction end to end.
/// </summary>
public enum HexSurfaceMode
{
	LegacyCatlike = 0,
	HFOriginal = 1
}

/// <summary>
/// Data-driven visual style for the hex terrain.
///
/// The layout mirrors an ArtDef-style terrain pipeline: biome materials are
/// separate from landform geometry, mountains choose from topology modules,
/// and every module may be supplied either as an authored mesh, a readable
/// height mask, or the built-in sampled height field.
/// </summary>
[CreateAssetMenu(menuName = "Hex Map/Terrain Style", fileName = "Hex Terrain Style")]
public sealed class HexTerrainStyle : ScriptableObject
{
	[Serializable]
	public struct MountainPeak
	{
		public Vector2 center;
		public Vector2 size;
		[Range(0f, 1.2f)] public float height;
		[Range(0.35f, 2.5f)] public float sharpness;

		public MountainPeak(
			float x, float y, float width, float depth,
			float height, float sharpness = 1.18f)
		{
			center = new Vector2(x, y);
			size = new Vector2(width, depth);
			this.height = height;
			this.sharpness = sharpness;
		}
	}

	[Serializable]
	public sealed class MountainModule
	{
		public string name;
		[Tooltip("Optional normalized module mesh: X/Z -1..1, Y 0..1. Enable Read/Write on import.")]
		public Mesh authoredMesh;
		[Tooltip("Optional readable grayscale height mask. White is the module summit.")]
		public Texture2D heightMask;
		[Range(0f, 1f)] public float heightMaskBlend = 1f;
		public Vector2 footprintScale = Vector2.one;
		[Range(0.25f, 1.5f)] public float heightScale = 1f;
		public MountainPeak[] peaks;

		public MountainModule(string name, params MountainPeak[] peaks)
		{
			this.name = name;
			this.peaks = peaks;
			footprintScale = Vector2.one;
			heightScale = 1f;
			heightMaskBlend = 1f;
		}
	}

	[Serializable]
	public struct BiomeMaterialStyle
	{
		public string name;
		public Color scree;
		public Color lowRock;
		public Color highRock;
		public Color stripe;
		public Color snow;
		[Range(0f, 1f)] public float hillHighLine;
		[Range(0f, 2f)] public float snowLine;

		public BiomeMaterialStyle(
			string name, Color scree, Color lowRock, Color highRock,
			Color stripe, Color snow, float hillHighLine, float snowLine)
		{
			this.name = name;
			this.scree = scree;
			this.lowRock = lowRock;
			this.highRock = highRock;
			this.stripe = stripe;
			this.snow = snow;
			this.hillHighLine = hillHighLine;
			this.snowLine = snowLine;
		}
	}

	const int bakedMaskSize = 65;
	const int biomeCount = 5;

	[Header("Surface authority")]
	[Tooltip("HF Original drives rendering, collision, roads, rivers, and object placement. Legacy Catlike is retained only as an explicit compatibility path.")]
	public HexSurfaceMode surfaceMode = HexSurfaceMode.LegacyCatlike;
	[Range(2, 8)] public int hfColliderSubdivisions = 4;
	[Range(0, 2)] public int hfOverlaySubdivisionLevels = 1;
	[Tooltip("Road height above the sampled HF surface. A small positive gap prevents the curved terrain from cutting through the road ribbon.")]
	[Range(0f, 0.25f)] public float hfRoadSurfaceOffset = 0.08f;
	[Tooltip("River overlay height above the HF-carved channel surface.")]
	[Range(0f, 0.5f)] public float hfRiverSurfaceOffset = 0.12f;

	[Header("Terrain element scale")]
	[Min(0.1f)] public float hillHeight = 2.9f;
	[Min(0.1f)] public float mountainHeight = 5.45f;
	[Min(0.25f)] public float mountainWidth = 0.93f;
	[Min(0.1f)] public float desertMountainHeight = 4.45f;
	[Min(0.25f)] public float desertMountainWidth = 1.08f;

	[Header("Material sources")]
	[Tooltip("Five equal vertical panels: Desert, Grass, Plains, Tundra, Snow.")]
	public Texture2D terrainSurfaceAtlas;
	[Range(0f, 1f)] public float terrainSurfaceBlend = 0.82f;
	[Min(0.001f)] public float terrainSurfaceTiling = 0.018f;
	[Range(0f, 0.3f)] public float terrainMacroVariation = 0.1f;
	public Texture2D rockAlbedo;
	public Texture2D strataAlbedo;
	public Texture2D rockNormal;
	[Tooltip("Optional color decal exported from an editable terrain source.")]
	public Texture2D mountainColorDecal;

	[Header("HoneyFramework realtime mixer")]
	[Tooltip("Eight-panel compact atlas derived from HF mixer masks. It changes ownership boundaries, not the current surface textures.")]
	public Texture2D hfTerrainMixer;
	[Tooltip("HF's meandering river ownership mask, used only by river and estuary materials.")]
	public Texture2D hfRiverMixer;
	[Min(0.4f)] public float hfStampScale = 1f;
	[Range(0f, 1f)] public float hfTerrainBlend = 0.94f;
	[Range(0f, 1f)] public float hfReliefFootprint = 0.68f;
	[Range(0f, 1f)] public float hfRiverMixerStrength = 0.82f;

	[Header("HoneyFramework original terrain triplets")]
	[Tooltip("Legacy transition control. Explicit HF Original surface mode forces the complete triplet reconstruction (1.0).")]
	[Range(0f, 1f)] public float hfOriginalTerrainBlend = 1f;
	[Min(0.4f)] public float hfOriginalStampScale = 1.6f;
	[Min(0.1f)] public float hfOriginalHeightScale = 16f;
	[Tooltip("Mip level matching HF Oven's downsample plus Gaussian height blur.")]
	[Range(0, 4)] public int hfOriginalHeightLod = 2;
	[Tooltip("Inner and outer distances of the HF river-channel carve, in normalized hex units.")]
	public Vector2 hfRiverCarve = new(0.07f, 0.31f);
	public Texture2D hfDirtDiffuse;
	public Texture2D hfDirtHeight;
	public Texture2D hfDirtMixer;
	public Texture2D hfPlainsDiffuse;
	public Texture2D hfCommonHeight;
	public Texture2D hfPlainsMixer;
	public Texture2D hfMarshDiffuse;
	public Texture2D hfMarshMixer;
	public Texture2D hfHillDiffuse;
	public Texture2D hfHillHeight;
	public Texture2D hfHillMixer;
	public Texture2D hfMountainDiffuse;
	public Texture2D hfMountainHeight;
	public Texture2D hfMountainMixer;
	[Tooltip("HF sea border diffuse (Sand1_d). The current water shader still owns the final ocean colours.")]
	public Texture2D hfSeaDiffuse;
	[Tooltip("HF sea height stamp (Water_h), used to form the continuous shoreline.")]
	public Texture2D hfSeaHeight;
	[Tooltip("HF sea ownership stamp (Water_m), used to blend land into the seabed.")]
	public Texture2D hfSeaMixer;
	public Texture2D hfRiverDiffuse;
	public Texture2D hfRiverHeight;
	public Texture2D hfRiverOriginalMixer;

	[Header("Material transitions")]
	[Range(0f, 1f)] public float snowLowHeight = 0.75f;
	[Range(0f, 1f)] public float snowHighHeight = 0.8125f;
	public Vector4 desertStripeCenters = new(0.417f, 0.75f, 0.833f, 0.958f);
	public Vector4 desertStripeWidths = new(0.052f, 0.032f, 0.024f, 0.017f);

	[Header("Ocean, coast, and river material set")]
	public Color deepOcean = new(0.025f, 0.16f, 0.25f, 1f);
	public Color shallowWater = new(0.08f, 0.47f, 0.55f, 1f);
	public Color shoreFoam = new(0.84f, 0.94f, 0.9f, 1f);
	public Color wetSand = new(0.45f, 0.34f, 0.2f, 1f);
	public Color drySand = new(0.78f, 0.63f, 0.36f, 1f);
	public Color riverWater = new(0.035f, 0.25f, 0.29f, 1f);
	public Color riverBank = new(0.43f, 0.32f, 0.18f, 1f);
	[Range(0f, 1f)] public float waterStyleBlend = 0.84f;

	[Header("Coast cliff material set")]
	public Color coastCliffLow = new(0.32f, 0.24f, 0.16f, 1f);
	public Color coastCliffHigh = new(0.67f, 0.58f, 0.43f, 1f);
	public Color snowCoastCliff = new(0.72f, 0.76f, 0.75f, 1f);
	[Range(0f, 1f)] public float coastCliffStrata = 0.68f;

	[Header("Biome feature material set")]
	[Tooltip("Per-biome foliage tint: Desert, Grass, Plains, Tundra, Snow.")]
	[SerializeField] Color[] plantTints = BuildDefaultPlantTints();
	[Range(0f, 1f)] public float vegetationStyleBlend = 0.74f;

	[Header("Civilization-style shader art direction")]
	[Range(0f, 1f)] public float shaderStyleStrength = 0.82f;
	[Range(0.7f, 1.5f)] public float shaderSaturation = 1.08f;
	[Range(0.7f, 1.4f)] public float shaderContrast = 1.02f;
	[Range(0f, 0.5f)] public float shaderPosterization = 0.13f;
	[Range(0f, 0.2f)] public float shaderBrushStrength = 0.055f;
	[Min(0.001f)] public float shaderBrushScale = 0.055f;
	public Color shaderWarmLightTint = new(1.07f, 1.01f, 0.9f, 1f);
	public Color shaderCoolShadowTint = new(0.76f, 0.83f, 0.92f, 1f);

	[Header("Terrain material set (Desert, Grass, Plains, Tundra, Snow)")]
	[SerializeField] BiomeMaterialStyle[] biomeStyles = BuildDefaultBiomes();

	[Header("Terrain element set (End, Bend, Ridge, Junction, Massif)")]
	[SerializeField] MountainModule[] mountainModules = BuildDefaultModules();

	[NonSerialized] readonly Dictionary<int, float[]> bakedMasks = new();
	[NonSerialized] readonly HashSet<Texture2D> unreadableMasks = new();
	[NonSerialized] Texture2DArray mountainMaskArray;
	[NonSerialized] bool reportedIncompleteHFSet;
	[NonSerialized] int runtimeRevision;

	static HexTerrainStyle runtimeDefault;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	static void ReapplyLoadedHFGlobals()
	{
		// Shader globals are cleared by a scripting-domain reload even when Enter
		// Play Mode keeps the scene alive, so chunk Awake methods are not guaranteed
		// to run again. Reapply the loaded style independently of scene lifecycle.
		HexTerrainStyle[] styles = Resources.FindObjectsOfTypeAll<HexTerrainStyle>();
		for (int i = 0; i < styles.Length; i++)
		{
			if (styles[i] && styles[i].UsesHFOriginalSurface &&
				styles[i].HasHFOriginalTerrainSet())
			{
				styles[i].ApplyGlobalMaterialSet();
				return;
			}
		}
	}

	public static HexTerrainStyle RuntimeDefault
	{
		get
		{
			if (!runtimeDefault)
			{
				runtimeDefault = CreateInstance<HexTerrainStyle>();
				runtimeDefault.name = "Runtime Hex Terrain Style";
				runtimeDefault.hideFlags = HideFlags.HideAndDontSave;
			}
			return runtimeDefault;
		}
	}

	public float GetMaximumHeight(HexLandform landform, int terrainTypeIndex) =>
		landform == HexLandform.Hill ? hillHeight :
		(terrainTypeIndex == 0 ? desertMountainHeight : mountainHeight);

	public float GetMountainWidth(int terrainTypeIndex) =>
		terrainTypeIndex == 0 ? desertMountainWidth : mountainWidth;

	/// <summary>
	/// Whether HF owns the complete visible and interactive surface. This does
	/// not silently fall back when an asset is missing: callers keep the HF
	/// branch active and validation reports the incomplete style loudly.
	/// </summary>
	public bool UsesHFOriginalSurface =>
		surfaceMode == HexSurfaceMode.HFOriginal;

	public int RuntimeRevision => runtimeRevision;

	/// <summary>
	/// Compatibility name for the existing coast call sites. In the unified
	/// surface pipeline coast ownership cannot differ from terrain ownership.
	/// </summary>
	public bool UsesHFOriginalCoast => UsesHFOriginalSurface;

	public int SelectMountainModule(int neighborMask, HexHash hash)
	{
		int count = CountBits(neighborMask);
		int module;
		if (count == 0)
		{
			module = Mathf.FloorToInt(hash.e * 5f);
		}
		else if (count == 1)
		{
			module = 0;
		}
		else if (count == 2)
		{
			module = HasOppositeNeighbors(neighborMask) ? 2 : 1;
		}
		else
		{
			module = count == 3 ? 3 : 4;
		}
		return Mathf.Clamp(module, 0, Mathf.Max(0, mountainModules.Length - 1));
	}

	public bool TryGetAuthoredMesh(int moduleIndex, out MountainModule module)
	{
		module = GetModule(moduleIndex);
		return module != null && module.authoredMesh && module.authoredMesh.isReadable;
	}

	/// <summary>
	/// Samples an authored height mask when present. Otherwise the module's
	/// peak data is baked to a small height field and sampled through the same
	/// path, so swapping in an artist-authored mask does not change generation.
	/// </summary>
	public float SampleMountainHeight(int moduleIndex, Vector2 point)
	{
		MountainModule module = GetModule(moduleIndex);
		if (module == null)
		{
			return 0f;
		}

		Vector2 scale = module.footprintScale;
		scale.x = Mathf.Max(0.05f, scale.x);
		scale.y = Mathf.Max(0.05f, scale.y);
		Vector2 samplePoint = new(point.x / scale.x, point.y / scale.y);
		float procedural = SampleBakedMask(moduleIndex, samplePoint);
		float height01 = procedural;

		if (module.heightMask && !unreadableMasks.Contains(module.heightMask))
		{
			try
			{
				float u = samplePoint.x * 0.5f + 0.5f;
				float v = samplePoint.y * 0.5f + 0.5f;
				float authored = module.heightMask.GetPixelBilinear(u, v).grayscale;
				height01 = Mathf.Lerp(
					procedural, authored, module.heightMaskBlend);
			}
			catch (UnityException)
			{
				unreadableMasks.Add(module.heightMask);
				Debug.LogWarning(
					$"Height mask '{module.heightMask.name}' is not readable; " +
					"using the sampled procedural module instead.", this);
			}
		}

		return Mathf.Clamp01(height01) * module.heightScale;
	}

	Texture2DArray GetMountainMaskArray()
	{
		EnsureMountainModules();
		if (mountainMaskArray)
		{
			return mountainMaskArray;
		}

		int depth = Mathf.Max(1, mountainModules.Length);
		mountainMaskArray = new Texture2DArray(
			bakedMaskSize, bakedMaskSize, depth,
			TextureFormat.RHalf, false, true)
		{
			name = $"{name} Mountain Height Masks",
			filterMode = FilterMode.Bilinear,
			wrapMode = TextureWrapMode.Clamp,
			hideFlags = HideFlags.HideAndDontSave
		};

		Color[] pixels = new Color[bakedMaskSize * bakedMaskSize];
		for (int moduleIndex = 0; moduleIndex < depth; moduleIndex++)
		{
			for (int y = 0; y < bakedMaskSize; y++)
			{
				for (int x = 0; x < bakedMaskSize; x++)
				{
					Vector2 point = new(
						x / (float)(bakedMaskSize - 1) * 2f - 1f,
						y / (float)(bakedMaskSize - 1) * 2f - 1f);
					float height = SampleMountainHeight(moduleIndex, point);
					pixels[y * bakedMaskSize + x] = new Color(height, 0f, 0f, 1f);
				}
			}
			mountainMaskArray.SetPixels(pixels, moduleIndex, 0);
		}
		mountainMaskArray.Apply(false, true);
		return mountainMaskArray;
	}

	public void ApplyTo(Material material)
	{
		ApplyGlobalMaterialSet();
		if (!material)
		{
			return;
		}
		if (rockAlbedo)
		{
			material.SetTexture("_Relief_Rock", rockAlbedo);
		}
		if (terrainSurfaceAtlas)
		{
			material.SetTexture("_HexTerrainStyleAtlas", terrainSurfaceAtlas);
		}
		if (strataAlbedo)
		{
			material.SetTexture("_Relief_Strata", strataAlbedo);
		}
		if (rockNormal)
		{
			material.SetTexture("_Relief_Rock_Normal", rockNormal);
		}
		material.SetTexture("_Mountain_Color_Decal", mountainColorDecal);
		material.SetFloat("_Use_Mountain_Color_Decal", mountainColorDecal ? 1f : 0f);
		material.SetVector("_HexSnowBand", new Vector4(
			snowLowHeight, snowHighHeight, 0f, 0f));
		material.SetVector("_HexDesertStripeCenters", desertStripeCenters);
		material.SetVector("_HexDesertStripeWidths", desertStripeWidths);
		material.SetVector("_HexReliefHeights", new Vector4(
			hillHeight, mountainHeight, desertMountainHeight, 0f));
		material.SetVector("_HexReliefWidths", new Vector4(
			mountainWidth, desertMountainWidth, 0f, 0f));
		material.SetVector("_HexReliefRiverCarve", new Vector4(
			hfRiverCarve.x, hfRiverCarve.y, 0f, 0f));
		material.SetTexture("_HexMountainMasks", GetMountainMaskArray());

		EnsureBiomeStyles();
		Vector4[] scree = new Vector4[biomeCount];
		Vector4[] lowRock = new Vector4[biomeCount];
		Vector4[] highRock = new Vector4[biomeCount];
		Vector4[] stripe = new Vector4[biomeCount];
		Vector4[] snow = new Vector4[biomeCount];
		float[] hillLines = new float[biomeCount];
		float[] snowLines = new float[biomeCount];
		for (int i = 0; i < biomeCount; i++)
		{
			scree[i] = biomeStyles[i].scree;
			lowRock[i] = biomeStyles[i].lowRock;
			highRock[i] = biomeStyles[i].highRock;
			stripe[i] = biomeStyles[i].stripe;
			snow[i] = biomeStyles[i].snow;
			hillLines[i] = biomeStyles[i].hillHighLine;
			snowLines[i] = biomeStyles[i].snowLine;
		}
		material.SetVectorArray("_HexBiomeScree", scree);
		material.SetVectorArray("_HexBiomeLowRock", lowRock);
		material.SetVectorArray("_HexBiomeHighRock", highRock);
		material.SetVectorArray("_HexBiomeStripe", stripe);
		material.SetVectorArray("_HexBiomeSnow", snow);
		material.SetFloatArray("_HexBiomeHillLine", hillLines);
		material.SetFloatArray("_HexBiomeSnowLine", snowLines);
	}

	void ApplyGlobalMaterialSet()
	{
		bool hasHFOriginalTerrainSet = HasHFOriginalTerrainSet();
		if (UsesHFOriginalSurface && !hasHFOriginalTerrainSet &&
			!reportedIncompleteHFSet)
		{
			reportedIncompleteHFSet = true;
			Debug.LogError(
				$"Terrain style '{name}' selects HF Original surface mode, but " +
				"one or more required diffuse/height/mixer textures are missing. " +
				"The legacy Catlike surface will not be enabled automatically.",
				this);
		}
		EnsurePlantTints();
		if (terrainSurfaceAtlas)
		{
			Shader.SetGlobalTexture("_HexTerrainStyleAtlas", terrainSurfaceAtlas);
		}
		Shader.SetGlobalFloat(
			"_HexTerrainAtlasBlend", terrainSurfaceAtlas ? terrainSurfaceBlend : 0f);
		Shader.SetGlobalFloat("_HexTerrainAtlasTiling", terrainSurfaceTiling);
		Shader.SetGlobalFloat("_HexTerrainMacroVariation", terrainMacroVariation);
		if (hfTerrainMixer)
		{
			Shader.SetGlobalTexture("_HexHFTerrainMixer", hfTerrainMixer);
		}
		if (hfRiverMixer)
		{
			Shader.SetGlobalTexture("_HexHFRiverMixer", hfRiverMixer);
		}
		SetGlobalTexture("_HFDirtDiffuse", hfDirtDiffuse);
		SetGlobalTexture("_HFDirtHeight", hfDirtHeight);
		SetGlobalTexture("_HFDirtMixer", hfDirtMixer);
		SetGlobalTexture("_HFPlainsDiffuse", hfPlainsDiffuse);
		SetGlobalTexture("_HFCommonHeight", hfCommonHeight);
		SetGlobalTexture("_HFPlainsMixer", hfPlainsMixer);
		SetGlobalTexture("_HFMarshDiffuse", hfMarshDiffuse);
		SetGlobalTexture("_HFMarshMixer", hfMarshMixer);
		SetGlobalTexture("_HFHillDiffuse", hfHillDiffuse);
		SetGlobalTexture("_HFHillHeight", hfHillHeight);
		SetGlobalTexture("_HFHillMixer", hfHillMixer);
		SetGlobalTexture("_HFMountainDiffuse", hfMountainDiffuse);
		SetGlobalTexture("_HFMountainHeight", hfMountainHeight);
		SetGlobalTexture("_HFMountainMixer", hfMountainMixer);
		SetGlobalTexture("_HFSeaDiffuse", hfSeaDiffuse);
		SetGlobalTexture("_HFSeaHeight", hfSeaHeight);
		SetGlobalTexture("_HFSeaMixer", hfSeaMixer);
		SetGlobalTexture("_HFRiverDiffuse", hfRiverDiffuse);
		SetGlobalTexture("_HFRiverHeight", hfRiverHeight);
		SetGlobalTexture("_HFRiverMixer", hfRiverOriginalMixer);
		Shader.SetGlobalFloat("_HexHFOriginalBlend",
			UsesHFOriginalSurface && hasHFOriginalTerrainSet ?
				1f : 0f);
		Shader.SetGlobalFloat("_HexHFOriginalStampScale", hfOriginalStampScale);
		Shader.SetGlobalFloat("_HexHFOriginalHeightScale", hfOriginalHeightScale);
		Shader.SetGlobalFloat("_HexHFOriginalHeightLod", hfOriginalHeightLod);
		Shader.SetGlobalVector("_HexReliefRiverCarve", new Vector4(
			hfRiverCarve.x, hfRiverCarve.y, 0f, 0f));
		// Original HF places both the terrain mesh and the water plane on one
		// datum, then lets the centered height texture decide which side of the
		// water line is visible. Catlike's separate shallow/deep base elevations
		// must not enter that reconstruction.
		Shader.SetGlobalFloat(
			"_HexHFOriginalDatumY",
			HexMetrics.visualWaterLevel * HexMetrics.elevationStep);
		Shader.SetGlobalFloat("_HexHFStampScale", hfStampScale);
		Shader.SetGlobalFloat(
			"_HexHFBlendStrength", hfTerrainMixer ? hfTerrainBlend : 0f);
		Shader.SetGlobalFloat(
			"_HexHFReliefFootprint", hfTerrainMixer ? hfReliefFootprint : 0f);
		Shader.SetGlobalFloat(
			"_HexHFRiverMixerStrength",
			hfRiverMixer ? hfRiverMixerStrength : 0f);
		Shader.SetGlobalColor("_HexDeepOceanColor", deepOcean);
		Shader.SetGlobalColor("_HexShallowWaterColor", shallowWater);
		Shader.SetGlobalColor("_HexShoreFoamColor", shoreFoam);
		Shader.SetGlobalColor("_HexWetSandColor", wetSand);
		Shader.SetGlobalColor("_HexDrySandColor", drySand);
		Shader.SetGlobalColor("_HexRiverWaterColor", riverWater);
		Shader.SetGlobalColor("_HexRiverBankColor", riverBank);
		Shader.SetGlobalFloat("_HexWaterStyleBlend", waterStyleBlend);
		Shader.SetGlobalColor("_HexCoastCliffLow", coastCliffLow);
		Shader.SetGlobalColor("_HexCoastCliffHigh", coastCliffHigh);
		Shader.SetGlobalColor("_HexSnowCoastCliff", snowCoastCliff);
		Shader.SetGlobalFloat("_HexCoastCliffStrata", coastCliffStrata);
		Vector4[] foliage = new Vector4[biomeCount];
		for (int i = 0; i < biomeCount; i++)
		{
			foliage[i] = plantTints[i];
		}
		Shader.SetGlobalVectorArray("_HexBiomePlantTint", foliage);
		Shader.SetGlobalFloat("_HexVegetationStyleBlend", vegetationStyleBlend);
		Shader.SetGlobalFloat("_HexCivStyleStrength", shaderStyleStrength);
		Shader.SetGlobalFloat("_HexCivSaturation", shaderSaturation);
		Shader.SetGlobalFloat("_HexCivContrast", shaderContrast);
		Shader.SetGlobalFloat("_HexCivPosterization", shaderPosterization);
		Shader.SetGlobalFloat("_HexCivBrushStrength", shaderBrushStrength);
		Shader.SetGlobalFloat("_HexCivBrushScale", shaderBrushScale);
		Shader.SetGlobalColor("_HexCivWarmLightTint", shaderWarmLightTint);
		Shader.SetGlobalColor("_HexCivCoolShadowTint", shaderCoolShadowTint);
	}

	static void SetGlobalTexture(string propertyName, Texture texture)
	{
		if (texture)
		{
			Shader.SetGlobalTexture(propertyName, texture);
		}
	}

	public bool HasHFOriginalTerrainSet() =>
		hfDirtDiffuse && hfDirtHeight && hfDirtMixer &&
		hfPlainsDiffuse && hfCommonHeight && hfPlainsMixer &&
		hfMarshDiffuse && hfMarshMixer &&
		hfHillDiffuse && hfHillHeight && hfHillMixer &&
		hfMountainDiffuse && hfMountainHeight && hfMountainMixer &&
		hfSeaDiffuse && hfSeaHeight && hfSeaMixer;

	MountainModule GetModule(int index)
	{
		EnsureMountainModules();
		return mountainModules.Length == 0 ? null :
			mountainModules[Mathf.Clamp(index, 0, mountainModules.Length - 1)];
	}

	float SampleBakedMask(int moduleIndex, Vector2 point)
	{
		if (!bakedMasks.TryGetValue(moduleIndex, out float[] samples))
		{
			samples = BakeModuleMask(GetModule(moduleIndex));
			bakedMasks.Add(moduleIndex, samples);
		}
		float x = Mathf.Clamp01(point.x * 0.5f + 0.5f) * (bakedMaskSize - 1);
		float y = Mathf.Clamp01(point.y * 0.5f + 0.5f) * (bakedMaskSize - 1);
		int x0 = Mathf.FloorToInt(x);
		int y0 = Mathf.FloorToInt(y);
		int x1 = Mathf.Min(x0 + 1, bakedMaskSize - 1);
		int y1 = Mathf.Min(y0 + 1, bakedMaskSize - 1);
		float tx = x - x0;
		float ty = y - y0;
		float a = Mathf.Lerp(
			samples[y0 * bakedMaskSize + x0],
			samples[y0 * bakedMaskSize + x1], tx);
		float b = Mathf.Lerp(
			samples[y1 * bakedMaskSize + x0],
			samples[y1 * bakedMaskSize + x1], tx);
		return Mathf.Lerp(a, b, ty);
	}

	static float[] BakeModuleMask(MountainModule module)
	{
		float[] samples = new float[bakedMaskSize * bakedMaskSize];
		if (module == null || module.peaks == null)
		{
			return samples;
		}
		for (int y = 0; y < bakedMaskSize; y++)
		{
			for (int x = 0; x < bakedMaskSize; x++)
			{
				Vector2 point = new(
					x / (float)(bakedMaskSize - 1) * 2f - 1f,
					y / (float)(bakedMaskSize - 1) * 2f - 1f);
				float height = 0f;
				foreach (MountainPeak peak in module.peaks)
				{
					Vector2 d = point - peak.center;
					float width = Mathf.Max(0.02f, peak.size.x);
					float depth = Mathf.Max(0.02f, peak.size.y);
					float q = Mathf.Sqrt(
						d.x * d.x / (width * width) +
						d.y * d.y / (depth * depth));
					float angle = Mathf.Atan2(d.y, d.x);
					q *= 1f + 0.075f * Mathf.Sin(
						angle * 3f + peak.center.x * 9.1f) +
						0.035f * Mathf.Sin(angle * 7f - peak.center.y * 11.7f);
					float profile = Mathf.Pow(
						Mathf.Clamp01(1f - q), peak.sharpness);
					float shoulder = Mathf.SmoothStep(0f, 1f, profile);
					height = Mathf.Max(height,
						peak.height * Mathf.Lerp(profile, shoulder, 0.14f));
				}
				samples[y * bakedMaskSize + x] = height;
			}
		}
		return samples;
	}

	void OnValidate()
	{
		runtimeRevision++;
		reportedIncompleteHFSet = false;
		bakedMasks.Clear();
		unreadableMasks.Clear();
		if (mountainMaskArray)
		{
			if (Application.isPlaying)
			{
				Destroy(mountainMaskArray);
			}
			else
			{
				DestroyImmediate(mountainMaskArray);
			}
			mountainMaskArray = null;
		}
		EnsureBiomeStyles();
		EnsureMountainModules();
		EnsurePlantTints();
		ApplyGlobalMaterialSet();
	}

	void EnsureBiomeStyles()
	{
		if (biomeStyles == null || biomeStyles.Length != biomeCount)
		{
			biomeStyles = BuildDefaultBiomes();
		}
	}

	void EnsureMountainModules()
	{
		if (mountainModules == null || mountainModules.Length < 5)
		{
			mountainModules = BuildDefaultModules();
			bakedMasks.Clear();
		}
	}

	void EnsurePlantTints()
	{
		if (plantTints == null || plantTints.Length != biomeCount)
		{
			plantTints = BuildDefaultPlantTints();
		}
	}

	static bool HasOppositeNeighbors(int mask)
	{
		for (int i = 0; i < 3; i++)
		{
			if ((mask & (1 << i)) != 0 && (mask & (1 << (i + 3))) != 0)
			{
				return true;
			}
		}
		return false;
	}

	static int CountBits(int value)
	{
		int count = 0;
		while (value != 0)
		{
			count += value & 1;
			value >>= 1;
		}
		return count;
	}

	static MountainModule[] BuildDefaultModules() => new[]
	{
		new MountainModule("End / Single",
			new MountainPeak(0.02f, -0.08f, 0.7f, 0.62f, 0.94f),
			new MountainPeak(-0.35f, 0.15f, 0.52f, 0.46f, 0.79f),
			new MountainPeak(0.37f, 0.18f, 0.48f, 0.43f, 0.71f),
			new MountainPeak(0.17f, -0.38f, 0.42f, 0.38f, 0.59f),
			new MountainPeak(0f, 0f, 0.92f, 0.36f, 0.48f, 0.92f)),
		new MountainModule("Bend",
			new MountainPeak(-0.33f, -0.1f, 0.54f, 0.49f, 0.81f),
			new MountainPeak(0.03f, 0.05f, 0.62f, 0.57f, 0.97f),
			new MountainPeak(0.35f, 0.28f, 0.5f, 0.45f, 0.77f),
			new MountainPeak(0.28f, -0.28f, 0.4f, 0.36f, 0.57f),
			new MountainPeak(0f, 0f, 0.92f, 0.36f, 0.48f, 0.92f)),
		new MountainModule("Through Ridge",
			new MountainPeak(-0.48f, 0.03f, 0.5f, 0.48f, 0.73f),
			new MountainPeak(-0.13f, -0.04f, 0.55f, 0.55f, 0.94f),
			new MountainPeak(0.25f, 0.035f, 0.52f, 0.49f, 0.86f),
			new MountainPeak(0.53f, -0.02f, 0.43f, 0.41f, 0.66f),
			new MountainPeak(0f, 0f, 0.98f, 0.33f, 0.5f, 0.95f)),
		new MountainModule("Junction",
			new MountainPeak(-0.2f, -0.2f, 0.58f, 0.51f, 0.93f),
			new MountainPeak(0.29f, -0.07f, 0.52f, 0.48f, 0.86f),
			new MountainPeak(0f, 0.32f, 0.5f, 0.46f, 0.78f),
			new MountainPeak(-0.42f, 0.19f, 0.38f, 0.35f, 0.59f),
			new MountainPeak(0f, 0f, 0.92f, 0.39f, 0.48f, 0.92f)),
		new MountainModule("Massif",
			new MountainPeak(-0.23f, 0.02f, 0.62f, 0.56f, 0.98f),
			new MountainPeak(0.28f, -0.14f, 0.55f, 0.5f, 0.9f),
			new MountainPeak(0.14f, 0.33f, 0.49f, 0.45f, 0.77f),
			new MountainPeak(-0.4f, -0.31f, 0.43f, 0.39f, 0.66f),
			new MountainPeak(0f, 0f, 0.96f, 0.42f, 0.52f, 0.92f))
	};

	static BiomeMaterialStyle[] BuildDefaultBiomes() => new[]
	{
		new BiomeMaterialStyle("Desert",
			new Color(0.55f, 0.38f, 0.19f), new Color(0.43f, 0.27f, 0.14f),
			new Color(0.64f, 0.46f, 0.27f), new Color(0.64f, 0.44f, 0.23f),
			new Color(0.9f, 0.92f, 0.91f), 0.66f, 2f),
		new BiomeMaterialStyle("Grass",
			new Color(0.36f, 0.35f, 0.24f), new Color(0.27f, 0.28f, 0.23f),
			new Color(0.49f, 0.48f, 0.39f), new Color(0.56f, 0.53f, 0.4f),
			new Color(0.84f, 0.87f, 0.87f), 0.72f, 0.88f),
		new BiomeMaterialStyle("Plains",
			new Color(0.42f, 0.41f, 0.36f), new Color(0.31f, 0.32f, 0.31f),
			new Color(0.57f, 0.57f, 0.54f), new Color(0.63f, 0.62f, 0.56f),
			new Color(0.86f, 0.88f, 0.87f), 0.64f, 0.86f),
		new BiomeMaterialStyle("Tundra",
			new Color(0.44f, 0.31f, 0.21f), new Color(0.33f, 0.25f, 0.2f),
			new Color(0.58f, 0.43f, 0.31f), new Color(0.65f, 0.49f, 0.34f),
			new Color(0.87f, 0.89f, 0.89f), 0.69f, 0.83f),
		new BiomeMaterialStyle("Snow",
			new Color(0.51f, 0.54f, 0.54f), new Color(0.37f, 0.41f, 0.42f),
			new Color(0.68f, 0.7f, 0.69f), new Color(0.72f, 0.73f, 0.7f),
			new Color(0.91f, 0.93f, 0.94f), 0.38f, 0.68f)
	};

	static Color[] BuildDefaultPlantTints() => new[]
	{
		new Color(0.62f, 0.55f, 0.28f, 1f),
		new Color(0.24f, 0.7f, 0.19f, 1f),
		new Color(0.58f, 0.64f, 0.2f, 1f),
		new Color(0.42f, 0.46f, 0.29f, 1f),
		new Color(0.31f, 0.46f, 0.38f, 1f)
	};
}
