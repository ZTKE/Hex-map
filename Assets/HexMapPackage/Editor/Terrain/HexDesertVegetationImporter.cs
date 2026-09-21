using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Imports the original Blender cacti into the existing instanced vegetation
/// renderer. The JSON contains Blender-exported mesh corners in Unity Y-up;
/// no runtime Blender installation or FBX material remapping is necessary.
/// </summary>
public static class HexDesertVegetationImporter
{
	const string AssetRoot = "Assets/HexMapPackage/Art/DesertVegetation";
	const string ProfilePath = "Assets/HexMapPackage/Art/Civ6Reference/Civ6 Reference Vegetation.asset";
	const string SourceRoot = "tools/Civ6Reference/Blender/Meshes";
	static readonly string[] Names = { "Cactus Forked", "Cactus Young", "Cactus Cluster" };
	static readonly float[] Heights = { 1.7f, 1.35f, .85f };
	static readonly float[] Weights = { 1f, 1.25f, .8f };

	[Serializable]
	sealed class MeshSource
	{
		public string name;
		public float[] positions, normals, uv;
		public int[] triangles;
	}

	public static bool HasPreparedSources =>
		File.Exists(SourceRoot + "/Cactus Forked.json") &&
		File.Exists(AssetRoot + "/Textures/Cactus Skin.png");

	[MenuItem("Tools/Hex Map/Import Original Desert Cacti")]
	public static void Build()
	{
		if (!HasPreparedSources)
			throw new FileNotFoundException("Run tools/Civ6Reference/Blender/build_desert_cacti.py with Blender first.");
		HexNearVegetationProfile profile = AssetDatabase.LoadAssetAtPath<HexNearVegetationProfile>(ProfilePath);
		if (!profile) throw new InvalidOperationException("Import the near vegetation profile before adding desert cacti.");
		Shader shader = Shader.Find("Hex Map/Near Vegetation");
		if (!shader) throw new InvalidOperationException("The near vegetation shader is required.");
		EnsureFolder(AssetRoot + "/Meshes");
		EnsureFolder(AssetRoot + "/Materials");
		string texturePath = AssetRoot + "/Textures/Cactus Skin.png";
		AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
		if (AssetImporter.GetAtPath(texturePath) is TextureImporter importer)
		{
			importer.textureType = TextureImporterType.Default;
			importer.textureShape = TextureImporterShape.Texture2D;
			importer.sRGBTexture = true;
			importer.alphaSource = TextureImporterAlphaSource.None;
			importer.wrapMode = TextureWrapMode.Repeat;
			importer.filterMode = FilterMode.Bilinear;
			importer.mipmapEnabled = true;
			importer.maxTextureSize = 256;
			importer.textureCompression = TextureImporterCompression.Compressed;
			importer.SaveAndReimport();
		}
		Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
		if (!texture) throw new InvalidDataException("Cactus skin texture did not import.");
		Material material = new(shader) { name = "Cactus Skin", enableInstancing = true };
		material.SetTexture("_BaseMap", texture);
		material.SetColor("_BaseColor", Color.white);
		material.SetFloat("_BumpScale", 0f);
		material.SetFloat("_Cutoff", 0f);
		material.SetFloat("_Smoothness", .12f);
		material.SetFloat("_Translucency", 0f);
		material = Save(material, AssetRoot + "/Materials/Cactus Skin.mat");
		var generated = new HexNearVegetationProfile.Species[Names.Length];
		int triangles = 0, lodTriangles = 0;
		for (int i = 0; i < Names.Length; i++)
		{
			Mesh mesh = BuildMesh(Names[i]), lod = BuildMesh(Names[i] + " LOD");
			triangles += mesh.triangles.Length / 3;
			lodTriangles += lod.triangles.Length / 3;
			generated[i] = new HexNearVegetationProfile.Species {
				name = Names[i], mesh = mesh, lodMesh = lod, material = material,
				height = Heights[i], scaleVariation = .24f, weight = Weights[i]
			};
		}
		// Regeneration refreshes meshes and material in place; it must not reset
		// the user's species sizes, weights, patch density or coverage settings.
		if (profile.desertAccents == null || profile.desertAccents.Length == 0)
		{
			profile.desertAccents = generated;
			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssetIfDirty(profile);
		}
		Debug.Log($"Imported 3 original Blender cactus species, {triangles} triangles ({lodTriangles} at LOD), one material. Desert patch controls: {ProfilePath}");
	}

	static Mesh BuildMesh(string name)
	{
		MeshSource data = JsonUtility.FromJson<MeshSource>(File.ReadAllText(SourceRoot + "/" + name + ".json"));
		if (data == null || data.positions == null || data.positions.Length == 0 || data.positions.Length % 3 != 0 ||
			data.normals == null || data.normals.Length != data.positions.Length || data.uv == null ||
			data.uv.Length != data.positions.Length / 3 * 2 || data.triangles == null || data.triangles.Length % 3 != 0)
			throw new InvalidDataException("Invalid Blender cactus mesh arrays: " + name);
		int count = data.positions.Length / 3;
		var positions = new Vector3[count];
		var normals = new Vector3[count];
		var uv = new Vector2[count];
		for (int i = 0; i < count; i++)
		{
			positions[i] = new(data.positions[i * 3], data.positions[i * 3 + 1], data.positions[i * 3 + 2]);
			normals[i] = new(data.normals[i * 3], data.normals[i * 3 + 1], data.normals[i * 3 + 2]);
			uv[i] = new(data.uv[i * 2], data.uv[i * 2 + 1]);
			if (!Finite(positions[i]) || !Finite(normals[i]) || normals[i].sqrMagnitude < .01f)
				throw new InvalidDataException("Invalid Blender cactus vertex: " + name);
		}
		foreach (int index in data.triangles)
			if (index < 0 || index >= count) throw new InvalidDataException("Cactus index out of range: " + name);
		var mesh = new Mesh { name = name, vertices = positions, normals = normals, uv = uv, triangles = data.triangles };
		mesh.RecalculateBounds();
		mesh.RecalculateTangents();
		if (mesh.bounds.size.y <= .01f || mesh.bounds.size.x > mesh.bounds.size.y * 2f)
			throw new InvalidDataException("Cactus must be a small upright Y-up mesh: " + name);
		return Save(mesh, AssetRoot + "/Meshes/" + name + ".asset");
	}

	static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) &&
		!float.IsNaN(value.z) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);

	static T Save<T>(T source, string path) where T : Object
	{
		T existing = AssetDatabase.LoadAssetAtPath<T>(path);
		if (existing)
		{
			EditorUtility.CopySerialized(source, existing);
			Object.DestroyImmediate(source);
			EditorUtility.SetDirty(existing);
		}
		else { AssetDatabase.CreateAsset(source, path); existing = source; }
		AssetDatabase.SaveAssetIfDirty(existing);
		return existing;
	}

	static void EnsureFolder(string path)
	{
		if (AssetDatabase.IsValidFolder(path)) return;
		int slash = path.LastIndexOf('/');
		EnsureFolder(path.Substring(0, slash));
		AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
	}
}
