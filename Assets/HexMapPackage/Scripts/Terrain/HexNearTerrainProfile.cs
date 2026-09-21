using System;
using UnityEngine;

/// <summary>
/// Replaceable near-view art, separate from logical cells and save data.
/// Linear shape channels: R=height, G=blend, B=top mask, A=snow mask.
/// </summary>
[CreateAssetMenu(menuName = "Hex Map/Near Terrain Profile")]
public sealed class HexNearTerrainProfile : ScriptableObject
{
	public Texture2DArray shapeAtlas;
	public Texture2DArray albedoAtlas;
	public HexNearLandMaterialProfile landMaterials;
	public HexNearVegetationProfile vegetation;
	[Min(.1f)] public float mountainHeight = 7.5f;
	[Min(.1f)] public float hillHeight = 3.5f;
	[Tooltip("Height of a continuous plateau above the shared land datum.")]
	[Min(.1f)] public float plateauHeight = 3.6f;
	[Tooltip("Low rolling relief on the plateau's broad top.")]
	[Range(0f, 2f)] public float plateauRelief = 1.2f;
	[Tooltip("Muted dry alpine grass on grassy plateau tops; snow and desert keep their own materials.")]
	[Range(0f, 1f)] public float plateauWeathering = .65f;
	[Tooltip("Reach of the broad highland base in hex radii. Capped below the thirteen-cell neighborhood's 2.598-radius support boundary.")]
	[Range(1.8f, 2.4f)] public float plateauFootprint = 2.4f;
	[Min(.1f)] public float desertMountainHeight = 7.2f;
	[Range(.7f, 1.8f)] public float desertMountainFootprint = 1.55f;
	[Range(.7f, 1.8f)] public float mountainFootprint = 1.8f;
	[Range(.7f, 2f)] public float hillFootprint = 1.4f;
	[Range(.15f, .8f)] public float ridgeWidth = .6f;
	[Range(0f, 1f)] public float ridgeStrength = .18f;
	[Tooltip("Height of the complete connected mountain body, including its crest and flanks. Use 1 for continuous ranges; zero leaves only the stamps and low foothills.")]
	[Range(0f, 1f)] public float mountainRangeStrength = 1f;
	[Tooltip("Continuous range crest between summits, as a fraction of the lower peak. Range brush and thin automatic chains use this height.")]
	[Range(.55f, 1.05f)] public float mountainChainSaddle = .92f;
	[Tooltip("Lower saddle in a broad mountain area, preserving internal valleys.")]
	[Range(.25f, .85f)] public float mountainMassifSaddle = .55f;
	[Tooltip("Width of a continuous range relative to the broad mountain-area body.")]
	[Range(.65f, 1.3f)] public float mountainChainWidth = 1f;
	[Header("Desert dunes")]
	[Range(0f, 4f)] public float desertDuneHeight = 1.45f;
	[Range(0f, 6f)] public float desertHillDuneHeight = 2.4f;
	[Range(.6f, 3f)] public float desertDuneWavelength = 1.25f;
	[Range(0f, 1f)] public float desertDuneIrregularity = .65f;
	[Range(-180f, 180f)] public float desertDuneWindAngle = 18f;
	[Tooltip("Half-width of the shared mountain body in hex radii, independently of the summit stamp. Only adjacent dry mountain cells connect; support is capped at 2.4 radii.")]
	[Range(.25f, 1.5f)] public float mountainRangeWidth = 1.4f;
	[Tooltip("Lower secondary crests by at most 14% in dense mountain groups. The independent sparse ridge graph selects which edges carry a high crest.")]
	[Range(0f, 1f)] public float mountainRangeDirectionality = .9f;
	[Tooltip("Bounded peak-height and footprint variation inside dense groups. Isolated authored mountains and thin chains retain their original scale.")]
	[Range(0f, 1f)] public float mountainMassVariation = .8f;
	[Tooltip("Blend the base of a mountain into nearby hills without flattening the authored summit.")]
	[Range(0f, 1f)] public float mountainFootBlend = .55f;
	[Tooltip("Bounded geometric ribs and gullies from the imported HM/HBLEND, stretched over connected range flanks. Crest height and compact support are preserved.")]
	[Range(0f, .35f)] public float mountainSlopeDetail = .28f;
	[Tooltip("Low foothill apron as a fraction of authored mountain height. The HBLEND mask shapes this perimeter; the connected range body is controlled separately.")]
	[Range(0f, .6f)] public float mountainBaseHeight = .44f;
	[Tooltip("Reach of the shared foothills in hex radii. Independent of summit scale variation and capped inside the thirteen-cell support.")]
	[Range(1.8f, 2.4f)] public float mountainBaseFootprint = 2.4f;
	[Range(.01f, .2f)] public float materialTiling = .065f;
	[Range(0f, 1f)] public float materialDetail = .3f;
	[Range(8f, 32f)] public float geometryDetail = 20f;
	public Color shallowWater = new(.12f, .40f, .46f);
	public Color deepWater = new(.018f, .09f, .17f);
	[TextArea] public string provenance;
	[NonSerialized] Color32[][] pixels;
	[NonSerialized] int cachedAtlas;
	[NonSerialized] int cachedShapeWidth, cachedShapeHeight;
	[NonSerialized] Vector4[] mountainPeaks;
	public bool IsReady => shapeAtlas && shapeAtlas.isReadable && shapeAtlas.depth >= 10 && albedoAtlas && albedoAtlas.depth >= 15;

	public void ApplyGlobals()
	{
		Shader.SetGlobalFloat("_HexNearTerrainEnabled", IsReady ? 1f : 0f);
		Shader.SetGlobalFloat("_HexNearLandResponseEnabled", 0f);
		Shader.SetGlobalFloat("_HexNearLandMasksEnabled", 0f);
		if (!IsReady) return;
		Shader.SetGlobalTexture("_HexNearShapes", shapeAtlas);
		Shader.SetGlobalTexture("_HexNearAlbedos", albedoAtlas);
		if (landMaterials) landMaterials.ApplyGlobals(albedoAtlas.depth);
		Shader.SetGlobalVector("_HexNearHeights", new Vector4(mountainHeight, hillHeight, mountainFootprint, hillFootprint));
		Shader.SetGlobalVector("_HexNearPlateau", new Vector4(plateauHeight, plateauRelief, plateauWeathering, Mathf.Clamp(plateauFootprint, 1.8f, 2.4f)));
		Shader.SetGlobalVector("_HexNearDesert", new Vector4(desertMountainHeight, desertMountainFootprint, 0, 0));
		Shader.SetGlobalVector("_HexNearDetails", new Vector4(ridgeWidth, ridgeStrength, materialTiling, materialDetail));
		Shader.SetGlobalVector("_HexNearRange", new Vector4(mountainRangeStrength, mountainRangeWidth, .36f, Mathf.Clamp01(mountainRangeDirectionality)));
		Shader.SetGlobalVector("_HexNearMountainModes", new Vector4(mountainChainSaddle, mountainMassifSaddle, mountainChainWidth, 0f));
		Shader.SetGlobalVector("_HexNearDunes", new Vector4(desertDuneHeight, desertHillDuneHeight, desertDuneWavelength, desertDuneIrregularity));
		float windAngle = desertDuneWindAngle * Mathf.Deg2Rad;
		Shader.SetGlobalVector("_HexNearDuneWind", new Vector4(Mathf.Cos(windAngle), Mathf.Sin(windAngle), 0f, 0f));
		Shader.SetGlobalVector("_HexNearMountainForm", new Vector4(Mathf.Clamp01(mountainMassVariation), mountainFootBlend, Mathf.Clamp(mountainSlopeDetail, 0f, .35f), 0f));
		Shader.SetGlobalVector("_HexNearMountainBase", new Vector4(Mathf.Clamp(mountainBaseHeight, 0f, .6f), Mathf.Clamp(mountainBaseFootprint, 1.8f, 2.4f), 0f, 0f));
		EnsureShapeCache();
		Shader.SetGlobalVectorArray("_HexNearPeaks", mountainPeaks);
		Shader.SetGlobalVector("_HexNearTessellation", new Vector4(geometryDetail, 120f, 420f, 0f));
		Shader.SetGlobalColor("_HexNearShallowWater", shallowWater);
		Shader.SetGlobalColor("_HexNearDeepWater", deepWater);
	}

	public Color SampleShape(int layer, Vector2 uv)
	{
		if (!shapeAtlas) return Color.clear;
		EnsureShapeCache();
		// The CPU pixels and dimensions have the same lifetime. Height queries
		// can sample this atlas thousands of times while preparing one chunk;
		// avoid native texture property calls for every bilinear lookup.
		int w = cachedShapeWidth, h = cachedShapeHeight;
		float x = Mathf.Clamp01(uv.x) * w - .5f, y = Mathf.Clamp01(uv.y) * h - .5f;
		int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
		float tx = x - x0, ty = y - y0;
		int x1 = Mathf.Clamp(x0 + 1, 0, w - 1), y1 = Mathf.Clamp(y0 + 1, 0, h - 1);
		x0 = Mathf.Clamp(x0, 0, w - 1); y0 = Mathf.Clamp(y0, 0, h - 1);
		Color32[] p = pixels[Mathf.Clamp(layer, 0, pixels.Length - 1)];
		return Color.LerpUnclamped(Color.LerpUnclamped(p[y0*w+x0], p[y0*w+x1], tx),
			Color.LerpUnclamped(p[y1*w+x0], p[y1*w+x1], tx), ty);
	}
	/// <summary>Authored peak in normalized stamp coordinates and its height.</summary>
	public Vector4 GetMountainPeak(int layer)
	{
		if (!shapeAtlas || !shapeAtlas.isReadable) return Vector4.zero;
		EnsureShapeCache();
		return mountainPeaks[Mathf.Clamp(layer, 0, 9)];
	}
	void EnsureShapeCache()
	{
		if (pixels != null && mountainPeaks != null && cachedAtlas == shapeAtlas.GetInstanceID()) return;
		cachedAtlas = shapeAtlas.GetInstanceID();
		cachedShapeWidth = shapeAtlas.width;
		cachedShapeHeight = shapeAtlas.height;
		pixels = new Color32[shapeAtlas.depth][];
		for (int i = 0; i < pixels.Length; i++) pixels[i] = shapeAtlas.GetPixels32(i, 0);
		mountainPeaks = new Vector4[10];
		int w = cachedShapeWidth, h = cachedShapeHeight;
		for (int layer = 0; layer < mountainPeaks.Length; layer++)
		{
			int highest = -1; float closest = float.MaxValue;
			Color32[] data = pixels[Mathf.Min(layer, pixels.Length - 1)];
			for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
			{
				float px = (x + .5f) / w * 2f - 1f, py = (y + .5f) / h * 2f - 1f;
				float distance = px * px + py * py;
				int value = data[y * w + x].r;
				// Keep anchors inside the peak-bearing centre. A bright corner of
				// replacement art must not pull a ridge across an unrelated cell.
				if (distance > .16f || value < highest || (value == highest && distance >= closest)) continue;
				highest = value; closest = distance;
				mountainPeaks[layer] = new Vector4(px, py, value / 255f, 0f);
			}
		}
	}
	public uint ShapeRevision { get; private set; }
	public void InvalidateShapeCache()
	{ unchecked { ShapeRevision++; }
		pixels = null; mountainPeaks = null; cachedAtlas = 0;
		cachedShapeWidth = cachedShapeHeight = 0;
	}
	void OnValidate()
	{
		InvalidateShapeCache();
		// Editing an unassigned profile must not change the active renderer.
		foreach (HexTerrainStyle style in Resources.FindObjectsOfTypeAll<HexTerrainStyle>())
			if (style.nearTerrainProfile == this) { ApplyGlobals(); break; }
	}
}
