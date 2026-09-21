using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

/// <summary>
/// Imports the small, explicitly exported local SDK reference set. The Steam installation
/// is never touched. Source provenance stays with generated assets; rebuilding preserves GUIDs.
/// </summary>
public static class HexCiv6ReferenceImporter
{
	const string AssetRoot = "Assets/HexMapPackage/Art/Civ6Reference";
	const string StylePath = "Assets/HexMapPackage/Materials/Terrain/Default Hex Terrain Style.asset";
	static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;
	static string ReferenceRoot => Path.Combine(ProjectRoot, "Artifacts", "Civ6Reference");
	static readonly string[] BaseMaps =
	{
		"TER_Desert_B", "TER_Grass_B", "TER_Plains_B", "TER_Tundra_B", "TER_Snow_B",
		"TER_Desert_Hills_B", "TER_Grass_Top_B", "TER_Plains_Top_B", "TER_Tundra_B", "TER_Snow_B",
		"TER_Mountain_B", "TER_Mountain_Top_B", "TER_Mountain_Snow_B",
		"TER_Mountain_Desert_B", "TER_Mountain_Desert_Stripe01_B"
	};
	static readonly string[] HeightMaps =
	{
		"TER_Desert_H", "TER_Grass_H", "TER_Plains_H", "TER_Tundra_H", "TER_Snow_H",
		"TER_Desert_Hills_H", "TER_Grass_H", "TER_Plains_H", "TER_Tundra_H", "TER_Snow_H",
		"TER_Mountain_H", "TER_Mountain_Top_H", "TER_Mountain_Snow_H",
		"TER_Mountain_H", "TER_Mountain_Desert_Stripe01_H"
	};
	static readonly string[] SpeciesNames =
	{
		"Trees_Pine_01", "Trees_Pine_02", "Trees_Pine_03",
		"Trees_Decid_01", "Trees_Decid_02", "Trees_Decid_03",
		"Jungle_PalmA", "Jungle_PalmB", "Jungle_PalmC"
	};

	[MenuItem("Tools/Hex Map/Import Local Civ6 Near Terrain Reference")]
	public static void BuildAndAssignDefault()
	{
		// Complete preflight before creating or changing any asset.
		foreach (string name in BaseMaps.Concat(HeightMaps).Distinct()) RequireFile(TextureSource(name));
		for (int i = 1; i <= 5; i++)
			foreach (string suffix in new[] { "HM", "HBLEND", "ID" })
				RequireFile(TextureSource($"Mountain_Single_{i:00}_{suffix}"));
		for (int i = 1; i <= 4; i++)
			foreach (string suffix in new[] { "HM", "HBLEND", "ID" })
				RequireFile(TextureSource($"MountainDesert_Single_{i:00}_{suffix}"));
		RequireFile(TextureSource("TER_Hills_Standard_Element"));
		foreach (string name in SpeciesNames) RequireFile(ModelSource(name));
		foreach (string name in new[] { "Trees_Pine_B", "Trees_Pine_N", "Trees_Decid_B", "Trees_Decid_N", "Tree_Jungle_B", "Tree_Jungle_N" })
			RequireFile(TextureSource(name));
		RequireFile(Path.Combine(ReferenceRoot, "manifest.json"));
		HexTerrainStyle style = AssetDatabase.LoadAssetAtPath<HexTerrainStyle>(StylePath);
		if (!style) throw new InvalidOperationException("The project's default Hex Terrain Style was not found.");
		Shader lit = Shader.Find("Hex Map/Near Vegetation");
		if (!lit) throw new InvalidOperationException("Hex Map/Near Vegetation shader is required for mesh vegetation and exploration visibility.");

		if (HexCiv6LandMaterialImporter.HasPreparedSources) HexCiv6LandMaterialImporter.ValidatePreparedSources();
		EnsureFolder(AssetRoot);
		EnsureFolder(AssetRoot + "/Meshes");
		EnsureFolder(AssetRoot + "/Textures");
		EnsureFolder(AssetRoot + "/Materials");
		Texture2DArray shapes = BuildShapeAtlas();
		Texture2DArray albedos = HexCiv6LandMaterialImporter.HasPreparedSources ?
			HexCiv6LandMaterialImporter.BuildAlbedoAtlas() : BuildAlbedoAtlas();
		Material pine = BuildMaterial("Pine", "Trees_Pine_B", "Trees_Pine_N", lit);
		Material broadleaf = BuildMaterial("Broadleaf", "Trees_Decid_B", "Trees_Decid_N", lit);
		Material jungle = BuildMaterial("Jungle", "Tree_Jungle_B", "Tree_Jungle_N", lit);
		HexNearVegetationProfile vegetation = LoadOrCreate<HexNearVegetationProfile>(AssetRoot + "/Civ6 Reference Vegetation.asset");
		vegetation.conifer = BuildSpecies(SpeciesNames.Take(3), pine);
		vegetation.broadleaf = BuildSpecies(SpeciesNames.Skip(3).Take(3), broadleaf);
		vegetation.jungle = BuildSpecies(SpeciesNames.Skip(6).Take(3), jungle);
		EditorUtility.SetDirty(vegetation);

		HexNearTerrainProfile profile = LoadOrCreate<HexNearTerrainProfile>(AssetRoot + "/Civ6 Reference Near Terrain.asset");
		profile.shapeAtlas = shapes;
		profile.albedoAtlas = albedos;
		if (HexCiv6LandMaterialImporter.HasPreparedSources)
			profile.landMaterials = HexCiv6LandMaterialImporter.BuildMaterialProfile();
		profile.vegetation = vegetation;
		profile.provenance = "Local Civilization VI Development Assets reference. Original SDK authorship is retained; commercial reuse rights have not been established. " +
			"Source hashes and conversion details: Source Manifest.json. Shapes: R=authored HM.r, G=original HBLEND alpha exported losslessly as grayscale, B=top mask (ID.a byte 128), A=snow mask (ID.a byte 153). " +
			"Layers 0-4=standard mountains, 5=hill, 6-9=desert mesas. Desert has distinct stripe IDs, so top/snow masks are empty; its two surface layers are authored desert base and Stripe01, not an invented separate top material. " +
			"Surface RGB=albedo, A=microheight; source-native static mesh transforms: (x,z,y), reverse triangle winding, UV=(u,1-v).";
		EditorUtility.SetDirty(profile);
		File.Copy(Path.Combine(ReferenceRoot, "manifest.json"), Path.Combine(ProjectRoot, AssetRoot, "Source Manifest.json"), true);
		AssetDatabase.ImportAsset(AssetRoot + "/Source Manifest.json", ImportAssetOptions.ForceSynchronousImport);
		// Assign exactly one field; keep all existing terrain recipes and unrelated appearance settings.
		SerializedObject serialized = new SerializedObject(style);
		serialized.FindProperty("nearTerrainProfile").objectReferenceValue = profile;
		serialized.ApplyModifiedPropertiesWithoutUndo();
		EditorUtility.SetDirty(style);
		AssetDatabase.SaveAssets();
		profile.InvalidateShapeCache();
		profile.ApplyGlobals();
		Debug.Log($"Imported Civ6 near terrain reference: {shapes.depth} shape layers, {albedos.depth} material layers, 9 mesh species. Profile: {AssetRoot}");
	}

	static Texture2DArray BuildShapeAtlas()
	{
		const int size = 256;
		var atlas = new Texture2DArray(size, size, 10, TextureFormat.RGBA32, false, true)
		{
			name = "Civ6 Reference Shapes", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
		};
		for (int layer = 0; layer < 10; layer++)
		{
			string prefix = layer < 5 ? $"Mountain_Single_{layer + 1:00}" : layer > 5 ? $"MountainDesert_Single_{layer - 5:00}" : null;
			Texture2D height = LoadPng(prefix != null ? prefix + "_HM" : "TER_Hills_Standard_Element", true);
			Texture2D blend = prefix != null ? LoadPng(prefix + "_HBLEND", true) : null;
			Texture2D id = prefix != null ? LoadPng(prefix + "_ID", true) : null;
			try
			{
				// Decode categorical IDs before filtering. Interpolating the IDs themselves
				// would invent snow/top regions between unrelated neighboring materials.
				if (id)
				{
					Color32[] masks = id.GetPixels32();
					for (int i = 0; i < masks.Length; i++)
					{
						byte code = masks[i].a;
						masks[i] = new Color32(code == 128 ? (byte)255 : (byte)0, code == 153 ? (byte)255 : (byte)0, 0, 0);
					}
					id.SetPixels32(masks); id.Apply(false, false);
				}
				var pixels = new Color32[size * size];
				for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
				{
					float u = (x + .5f) / size, v = (y + .5f) / size;
					Color masks = id ? id.GetPixelBilinear(u, v) : Color.clear;
					pixels[y * size + x] = new Color(height.GetPixelBilinear(u, v).r,
						blend ? blend.GetPixelBilinear(u, v).r : 0f,
						masks.r, masks.g);
				}
				atlas.SetPixels32(pixels, layer);
			}
			finally { Object.DestroyImmediate(height); if (blend) Object.DestroyImmediate(blend); if (id) Object.DestroyImmediate(id); }
		}
		atlas.Apply(false, false); // Keep readable: collision and object placement sample the same shape bytes.
		return SaveGenerated(atlas, AssetRoot + "/Civ6 Reference Shapes.asset");
	}

	static Texture2DArray BuildAlbedoAtlas()
	{
		const int size = 512;
		var atlas = new Texture2DArray(size, size, BaseMaps.Length, TextureFormat.RGBA32, true, false)
		{
			name = "Civ6 Reference Surface Materials", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4
		};
		for (int layer = 0; layer < BaseMaps.Length; layer++)
		{
			Texture2D color = LoadPng(BaseMaps[layer], false), height = LoadPng(HeightMaps[layer], true);
			try
			{
				var pixels = new Color32[size * size];
				for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
				{
					float u = (x + .5f) / size, v = (y + .5f) / size;
					Color c = color.GetPixelBilinear(u, v); c.a = height.GetPixelBilinear(u, v).r;
					pixels[y * size + x] = c;
				}
				atlas.SetPixels32(pixels, layer);
			}
			finally { Object.DestroyImmediate(color); Object.DestroyImmediate(height); }
		}
		atlas.Apply(true, false);
		return SaveGenerated(atlas, AssetRoot + "/Civ6 Reference Surface Materials.asset");
	}

	static Material BuildMaterial(string name, string albedoName, string normalName, Shader shader)
	{
		Texture2D albedo = ImportVegetationTexture(albedoName, false), normal = ImportVegetationTexture(normalName, true);
		string path = AssetRoot + "/Materials/" + name + ".mat";
		Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
		if (!material) { material = new Material(shader) { name = name }; AssetDatabase.CreateAsset(material, path); }
		material.shader = shader;
		material.SetTexture("_BaseMap", albedo); material.SetColor("_BaseColor", Color.white);
		material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f);
		material.SetFloat("_Smoothness", .12f); material.SetFloat("_Cutoff", .4f);
		material.SetOverrideTag("RenderType", "TransparentCutout");
		material.shaderKeywords = Array.Empty<string>(); material.renderQueue = (int)RenderQueue.AlphaTest;
		material.enableInstancing = true; material.doubleSidedGI = true;
		EditorUtility.SetDirty(material);
		return material;
	}

	static Texture2D ImportVegetationTexture(string name, bool normal)
	{
		string path = AssetRoot + "/Textures/" + name + ".png";
		File.Copy(TextureSource(name), Path.Combine(ProjectRoot, path), true);
		AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
		var importer = (TextureImporter)AssetImporter.GetAtPath(path);
		importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
		importer.sRGBTexture = !normal; importer.alphaSource = TextureImporterAlphaSource.FromInput;
		importer.alphaIsTransparency = !normal; importer.mipmapEnabled = true;
		importer.wrapMode = TextureWrapMode.Repeat; importer.filterMode = FilterMode.Trilinear;
		importer.anisoLevel = 4; importer.maxTextureSize = 512;
		importer.textureCompression = TextureImporterCompression.Uncompressed;
		importer.SaveAndReimport();
		return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
	}

	static HexNearVegetationProfile.Species[] BuildSpecies(IEnumerable<string> names, Material material) =>
		names.Select(name => new HexNearVegetationProfile.Species
		{
			name = name, mesh = BuildMesh(name), material = material, height = 3f, scaleVariation = .3f, weight = 1f
		}).ToArray();

	static Mesh BuildMesh(string name)
	{
		string json = File.ReadAllText(ModelSource(name));
		float[] p = ReadArray(json, "Position"), n = ReadArray(json, "Normal"), uv = ReadArray(json, "TextureCoordinates0"), tris = ReadArray(json, "triangles");
		// Granny stores TextureCoordinates0 as either float2 or float3. The
		// exported JSON preserves all native components; Unity's UV0 uses U/V.
		if (p.Length == 0 || p.Length % 3 != 0 || n.Length != p.Length ||
			uv.Length % (p.Length / 3) != 0 || uv.Length / (p.Length / 3) < 2 || tris.Length % 4 != 0)
			throw new InvalidDataException($"Invalid mesh arrays: {name} (position={p.Length}, normal={n.Length}, uv={uv.Length}, triangles={tris.Length})");
		int count = p.Length / 3;
		int uvStride = uv.Length / count;
		var vertices = new Vector3[count]; var normals = new Vector3[count]; var texcoords = new Vector2[count];
		for (int i = 0; i < count; i++)
		{
			vertices[i] = new Vector3(p[i * 3], p[i * 3 + 2], p[i * 3 + 1]);
			normals[i] = new Vector3(n[i * 3], n[i * 3 + 2], n[i * 3 + 1]);
			texcoords[i] = new Vector2(uv[i * uvStride], 1f - uv[i * uvStride + 1]);
		}
		int materialCount = 1;
		for (int i = 3; i < tris.Length; i += 4) materialCount = Mathf.Max(materialCount, checked((int)tris[i]) + 1);
		var indices = new List<int>[materialCount];
		for (int i = 0; i < indices.Length; i++) indices[i] = new List<int>();
		for (int i = 0; i < tris.Length; i += 4)
		{
			int a = checked((int)tris[i]), b = checked((int)tris[i + 1]), c = checked((int)tris[i + 2]), m = checked((int)tris[i + 3]);
			if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count) throw new InvalidDataException("Mesh index out of range.");
			indices[m].Add(a); indices[m].Add(c); indices[m].Add(b);
		}
		var mesh = new Mesh { name = name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
		mesh.vertices = vertices; mesh.normals = normals; mesh.uv = texcoords; mesh.subMeshCount = indices.Length;
		for (int i = 0; i < indices.Length; i++) mesh.SetTriangles(indices[i], i, false);
		mesh.RecalculateBounds(); mesh.RecalculateTangents();
		return SaveGenerated(mesh, AssetRoot + "/Meshes/" + name + ".asset");
	}

	// The exporter writes numeric nested arrays. Reading only these arrays avoids adding
	// a JSON library dependency to the shared HexMap editor assembly.
	static float[] ReadArray(string json, string key)
	{
		Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[");
		if (!match.Success) throw new InvalidDataException("Missing mesh array: " + key);
		int start = match.Index + match.Length - 1, depth = 0, end = start;
		for (; end < json.Length; end++) { if (json[end] == '[') depth++; else if (json[end] == ']' && --depth == 0) break; }
		if (end == json.Length) throw new InvalidDataException("Unterminated mesh array: " + key);
		return Regex.Matches(json.Substring(start, end - start + 1), @"[-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?")
			.Cast<Match>().Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
	}

	static Texture2D LoadPng(string name, bool linear)
	{
		var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { wrapMode = TextureWrapMode.Clamp };
		if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(TextureSource(name)), false))
		{ Object.DestroyImmediate(texture); throw new InvalidDataException("PNG could not be decoded: " + name); }
		return texture;
	}
	static T SaveGenerated<T>(T generated, string path) where T : Object
	{
		T existing = AssetDatabase.LoadAssetAtPath<T>(path);
		if (existing) { EditorUtility.CopySerialized(generated, existing); EditorUtility.SetDirty(existing); Object.DestroyImmediate(generated); return existing; }
		AssetDatabase.CreateAsset(generated, path); return generated;
	}
	static T LoadOrCreate<T>(string path) where T : ScriptableObject
	{
		T asset = AssetDatabase.LoadAssetAtPath<T>(path);
		if (!asset) { asset = ScriptableObject.CreateInstance<T>(); AssetDatabase.CreateAsset(asset, path); }
		return asset;
	}
	static string TextureSource(string name) => Path.Combine(ReferenceRoot, "Textures", name + ".png");
	static string ModelSource(string name) => Path.Combine(ReferenceRoot, "Models", name + ".mesh.json");
	static void RequireFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException("Run Tools/Civ6Reference/prepare_reference.py before importing.", path); }
	static void EnsureFolder(string path)
	{
		if (AssetDatabase.IsValidFolder(path)) return;
		int slash = path.LastIndexOf('/'); string parent = path.Substring(0, slash); EnsureFolder(parent);
		AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
	}
}
