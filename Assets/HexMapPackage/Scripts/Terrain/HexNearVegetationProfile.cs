using System;
using UnityEngine;

/// <summary>Mesh vegetation recipes, independent from logical biome and terrain height.</summary>
[CreateAssetMenu(menuName = "Hex Map/Near Vegetation Profile", fileName = "Near Vegetation Profile")]
public sealed class HexNearVegetationProfile : ScriptableObject
{
	[Serializable]
	public sealed class Species
	{
		public string name;
		public Mesh mesh;
		public Material material;
		[Tooltip("Optional per-submesh materials, for example trunk and canopy.")]
		public Material[] materials;
		public Mesh lodMesh;
		[Tooltip("Height in map units; imported mesh bounds are normalized automatically.")]
		[Min(0.05f)] public float height = 3f;
		[Range(0f, 0.6f)] public float scaleVariation = 0.3f;
		[Min(0f)] public float weight = 1f;
		[Tooltip("Imported assets must already use Unity's Y-up orientation.")]
		public Vector3 rotationOffset;
		public bool IsReady => mesh && mesh.vertexCount > 0 &&
			(mesh.bounds.size.y > 0.0001f) &&
			(material || (materials != null && materials.Length > 0 && materials[0]));
		public Material GetMaterial(int submesh) =>
			materials != null && submesh < materials.Length && materials[submesh] ?
				materials[submesh] : material;
	}

	public Species[] broadleaf = Array.Empty<Species>();
	public Species[] conifer = Array.Empty<Species>();
	public Species[] jungle = Array.Empty<Species>();
	public Species[] deadwood = Array.Empty<Species>();
	[Tooltip("Optional sparse desert details. Independent of saved forest species and density.")]
	public Species[] desertAccents = Array.Empty<Species>();
	[Tooltip("Maximum small plants in a desert patch. Zero disables automatic desert details.")]
	[Range(0, 12)] public int desertAccentDensity = 3;
	[Tooltip("Fraction of unwooded desert cells with a small patch; seeded by map coordinates.")]
	[Range(0f, 1f)] public float desertAccentCoverage = 0.38f;
	[Tooltip("Maximum desert planting slope. Dune slip faces and rocky walls remain bare.")]
	[Range(0f, 45f)] public float desertAccentMaxSlope = 26f;
	[Min(0.5f)] public float desertAccentClusterRadius = 2.8f;
	[Range(1, 100)] public int densityPerCell = 34;
	[Range(32, 8192)] public int maxInstancesPerChunk = 1024;
	[Range(0f, 70f)] public float maxSlopeDegrees = 38f;
	[Min(0f)] public float roadClearance = 1.15f;
	[Min(0f)] public float riverClearance = 1.45f;
	[Min(0f)] public float coastClearance = 1.1f;
	[Min(0f)] public float minimumSpacing = 0.8f;
	[Tooltip("Share of trees grouped into two or three stable groves per cell.")]
	[Range(0f, 1f)] public float clusteredFraction = 0.72f;
	[Min(0.5f)] public float clusterRadius = 3f;
	[Tooltip("Crown-height structure: taller grove interiors and smaller trees on glades, forest edges and steep slopes.")]
	[Range(0f, 1f)] public float canopyStructure = 0.65f;
	[Tooltip("Subtle coherent grove colour, layered over the author's forest tint.")]
	[Range(0f, 0.2f)] public float groveTintVariation = 0.08f;
	[Tooltip("Width in map units over which crowns taper beside unwooded cells. Forest-to-forest edges stay full height.")]
	[Min(0f)] public float forestEdgeWidth = 2.4f;
	[Tooltip("Share of the authored tree density allowed on low mountain foothills. Zero keeps mountain cells bare.")]
	[Range(0f, 0.5f)] public float mountainDensityScale = 0.25f;
	[Tooltip("Maximum planting elevation above the map datum, as a fraction of the terrain profile's mountain height.")]
	[Range(0.1f, 0.7f)] public float mountainTreeLine = 0.42f;
	[Min(0f)] public float settlementClearance = 3.3f;
	[Min(0f)] public float groundInset = 0.08f;
	[Min(1f)] public float lodDistance = 180f;
	[Min(1f)] public float shadowDistance = 280f;
	[Min(1f)] public float drawDistance = 850f;
	[Tooltip("Use tropical meshes for Broadleaf when the author explicitly selects this profile.")]
	public bool tropicalBroadleaf;

	public bool IsReady => HasReady(broadleaf) || HasReady(conifer) ||
		HasReady(jungle) || HasReady(deadwood) || HasReady(desertAccents);

	public bool HasDesertAccents => desertAccentDensity > 0 &&
		desertAccentCoverage > 0f && HasReady(desertAccents);

	public Species PickDesertAccent(float choice) => PickFrom(desertAccents, choice);

	public Species Pick(HexVegetation kind, float groupChoice, float speciesChoice)
	{
		Species[] group = kind switch
		{
			HexVegetation.Jungle => HasReady(jungle) ? jungle : broadleaf,
			HexVegetation.Conifer => conifer,
			HexVegetation.Deadwood => deadwood,
			HexVegetation.ColdMixed => groupChoice < 0.8f ? conifer : broadleaf,
			HexVegetation.Mixed => groupChoice < 0.3f ? conifer : broadleaf,
			_ => tropicalBroadleaf && HasReady(jungle) ? jungle : broadleaf
		};
		// A missing matching recipe lets the caller retain the original HF trees.
		return PickFrom(group, speciesChoice);
	}

	static Species PickFrom(Species[] group, float speciesChoice)
	{
		float total = 0f;
		if (group == null) return null;
		foreach (Species species in group)
			if (species != null && species.IsReady) total += Mathf.Max(0f, species.weight);
		if (total <= 0f) return null;
		float target = Mathf.Clamp01(speciesChoice) * total;
		Species last = null;
		foreach (Species species in group)
		{
			if (species == null || !species.IsReady || species.weight <= 0f) continue;
			last = species;
			target -= species.weight;
			if (target <= 0f) return species;
		}
		return last;
	}

	public bool HasRecipe(HexVegetation kind) =>
		Pick(kind, 0f, 0f) != null || Pick(kind, 1f, 0f) != null;

	static bool HasReady(Species[] group)
	{
		if (group == null) return false;
		foreach (Species species in group)
			if (species != null && species.IsReady && species.weight > 0f) return true;
		return false;
	}
}
