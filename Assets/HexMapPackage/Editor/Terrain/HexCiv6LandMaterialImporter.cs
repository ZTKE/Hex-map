using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Builds only the local SDK land materials; geometry and vegetation stay independently authored.</summary>
public static class HexCiv6LandMaterialImporter
{
    const string AssetRoot = "Assets/HexMapPackage/Art/Civ6Reference";
    const string NearProfilePath = AssetRoot + "/Civ6 Reference Near Terrain.asset";
    const string LandProfilePath = AssetRoot + "/Civ6 Reference Land Materials.asset";
    const int LayerCount = 18;
    const int ColorSize = 1024;
    const int ResponseSize = 512;
    static readonly string[] ExpectedNames =
    {
        "TER_Desert_Base", "TER_Grassland_Base", "TER_Plains_Base", "TER_Tundra_Base", "TER_Snow_Base",
        "TER_Desert_Hills", "TER_Grassland_Top_Hills", "TER_Plains_Top_Hills", "TER_Tundra_Base", "TER_Snow_Base",
        "TER_Mountain_Base", "TER_Mountain_Top", "TER_Mountain_Snow", "TER_Mountain_Desert_Base", "TER_Mountain_Desert_Stripe01",
        "TER_Mountain_Desert_Stripe02", "TER_Mountain_Desert_Stripe03", "TER_Tundra_Blend"
    };
    static string Root => Directory.GetParent(Application.dataPath).FullName;
    static string Sources => Path.Combine(Root, "Artifacts/LandAndShelf");

    [Serializable] public sealed class Layer
    {
        public int index;
        public string name, baseColor, height, gloss, fuzz;
    }
    [Serializable] sealed class LayerManifest { public Layer[] layers; }
    [Serializable] sealed class ValidationLayer
    {
        public string name;
        public float minSlopeU, maxSlopeU, minSlopeV, maxSlopeV, minGloss, maxGloss, minFuzz, maxFuzz;
    }
    [Serializable] sealed class ValidationReport
    {
        public bool passed, responseLinear, albedoSrgb, masksLinear, responseBound, masksBound, signedSlopesValid, materialMasksValid;
        public string timestamp, responseFormat, albedoFormat, masksFormat;
        public int responseLayers, albedoLayers, maskLayers, responseMipCount, albedoMipCount;
        public ValidationLayer[] layers;
        public Vector4[] maskWeights;
    }
    public static bool HasPreparedSources => File.Exists(Path.Combine(Sources, "RenderLayers.json"));
    public static void ValidatePreparedSources() => ReadLayers();

    [MenuItem("Tools/Hex Map/Import Local Civ6 Land Materials")]
    public static void BuildAndAssignDefault()
    {
        Layer[] layers = ReadLayers();
        HexNearTerrainProfile near = AssetDatabase.LoadAssetAtPath<HexNearTerrainProfile>(NearProfilePath);
        if (!near) throw new InvalidOperationException("Build the original near terrain reference before importing land materials.");
        // Preflight all inputs before replacing the first Unity asset.
        RequireInputs(layers);
        Texture2DArray colors = BuildAlbedoAtlas(layers);
        HexNearLandMaterialProfile land = BuildMaterialProfile(layers);
        near.albedoAtlas = colors;
        near.landMaterials = land;
        EditorUtility.SetDirty(near);
        AssetDatabase.SaveAssets();
        near.ApplyGlobals();
        Debug.Log($"Imported {LayerCount} local Civ6 land materials: {ColorSize}px albedo/height, {ResponseSize}px RGBAHalf slope/gloss/fuzz and 10 material masks. Geometry and vegetation were not rebuilt.");
    }

    public static Texture2DArray BuildAlbedoAtlas() => BuildAlbedoAtlas(ReadLayers());
    public static HexNearLandMaterialProfile BuildMaterialProfile() => BuildMaterialProfile(ReadLayers());

    public static string ValidateImported()
    {
        HexNearTerrainProfile near = AssetDatabase.LoadAssetAtPath<HexNearTerrainProfile>(NearProfilePath);
        if (!near || !near.landMaterials || !near.landMaterials.IsReady || !near.landMaterials.HasMasks)
            throw new InvalidOperationException("The imported land profile is incomplete.");
        HexNearLandMaterialProfile land = near.landMaterials;
        Texture2DArray response = land.responseAtlas, masks = land.materialMasks, colors = near.albedoAtlas;
        var report = new ValidationReport
        {
            timestamp = DateTime.UtcNow.ToString("O"), responseFormat = response.format.ToString(), albedoFormat = colors.format.ToString(), masksFormat = masks.format.ToString(),
            responseLayers = response.depth, albedoLayers = colors.depth, maskLayers = masks.depth,
            responseMipCount = response.mipmapCount, albedoMipCount = colors.mipmapCount,
            responseLinear = !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(response.graphicsFormat),
            albedoSrgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(colors.graphicsFormat),
            masksLinear = !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(masks.graphicsFormat),
            responseBound = Shader.GetGlobalTexture("_HexNearLandResponse") == response && Shader.GetGlobalFloat("_HexNearLandResponseEnabled") > .5f,
            masksBound = Shader.GetGlobalTexture("_HexNearLandMasks") == masks && Shader.GetGlobalFloat("_HexNearLandMasksEnabled") > .5f,
            layers = new ValidationLayer[LayerCount]
        };
        for (int layer = 0; layer < LayerCount; layer++)
        {
            Color[] pixels = response.GetPixels(layer, 0);
            var item = new ValidationLayer { name = land.layerNames[layer], minSlopeU = float.PositiveInfinity, minSlopeV = float.PositiveInfinity,
                maxSlopeU = float.NegativeInfinity, maxSlopeV = float.NegativeInfinity, minGloss = 1f, minFuzz = 1f };
            foreach (Color c in pixels)
            {
                if (float.IsNaN(c.r) || float.IsInfinity(c.r) || float.IsNaN(c.g) || float.IsInfinity(c.g))
                    throw new InvalidDataException("A land response contains an invalid slope.");
                item.minSlopeU = Mathf.Min(item.minSlopeU, c.r); item.maxSlopeU = Mathf.Max(item.maxSlopeU, c.r);
                item.minSlopeV = Mathf.Min(item.minSlopeV, c.g); item.maxSlopeV = Mathf.Max(item.maxSlopeV, c.g);
                item.minGloss = Mathf.Min(item.minGloss, c.b); item.maxGloss = Mathf.Max(item.maxGloss, c.b);
                item.minFuzz = Mathf.Min(item.minFuzz, c.a); item.maxFuzz = Mathf.Max(item.maxFuzz, c.a);
            }
            report.layers[layer] = item;
        }
        report.signedSlopesValid = report.layers.All(x => x.minSlopeU < 0f && x.maxSlopeU > 0f && x.minSlopeV < 0f && x.maxSlopeV > 0f &&
            x.minGloss >= 0f && x.maxGloss <= 1f && x.minFuzz >= 0f && x.maxFuzz <= 1f);
        report.materialMasksValid = true;
        report.maskWeights = new Vector4[10];
        for (int layer = 0; layer < 10; layer++)
        {
            Vector4 sum = Vector4.zero;
            foreach (Color c in masks.GetPixels(layer, 0)) sum += new Vector4(c.r, c.g, c.b, c.a);
            report.maskWeights[layer] = sum;
            report.materialMasksValid &= layer < 5 ? sum.x > 0 && sum.y > 0 && sum.z == 0 && sum.w == 0 :
                layer == 5 ? sum == Vector4.zero : sum.x > 0 && sum.y > 0 && sum.z > 0 && sum.w > 0;
        }
        report.passed = response.format == TextureFormat.RGBAHalf && response.depth == LayerCount && colors.depth == LayerCount && masks.depth == 10 &&
            report.responseLinear && report.albedoSrgb && report.masksLinear && report.responseBound && report.masksBound &&
            report.signedSlopesValid && report.materialMasksValid && response.width == ResponseSize && colors.width == ColorSize &&
            masks.mipmapCount == 1 && response.mipmapCount > 1 && colors.mipmapCount > 1;
        string output = Path.Combine(Sources, "Validation/ImportedLandMaterials.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonUtility.ToJson(report, true));
        if (!report.passed) throw new InvalidDataException("Land material import validation failed; see " + output);
        return output;
    }

    static Layer[] ReadLayers()
    {
        string path = Path.Combine(Sources, "RenderLayers.json");
        LayerManifest manifest = JsonUtility.FromJson<LayerManifest>(File.ReadAllText(path));
        if (manifest?.layers == null || manifest.layers.Length != LayerCount ||
            manifest.layers.Where((x, i) => x == null || x.index != i || x.name != ExpectedNames[i]).Any())
            throw new InvalidDataException("Land material manifest must contain the fixed 18 ordered layers.");
        RequireInputs(manifest.layers);
        return manifest.layers;
    }

    static void RequireInputs(Layer[] layers)
    {
        foreach (Layer layer in layers)
            foreach (string name in new[] { layer.baseColor, layer.height, layer.gloss, layer.fuzz })
                if (!File.Exists(TexturePath(name))) throw new FileNotFoundException("Missing prepared land texture", TexturePath(name));
        for (int i = 0; i < 10; i++)
        {
            if (i == 5) continue;
            string path = TexturePath(ShapeIdName(i));
            if (!File.Exists(path)) throw new FileNotFoundException("Missing original material ID image", path);
        }
        if (!File.Exists(Path.Combine(Sources, "SourceAudit.json")))
            throw new FileNotFoundException("The verified local source audit is required.");
    }

    static string TexturePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.Contains(".."))
            throw new InvalidDataException("Texture names must be basenames from the prepared manifest.");
        return Path.Combine(Sources, "Textures", name + ".png");
    }
    static string ShapeIdName(int layer) => layer < 5 ? $"Mountain_Single_{layer + 1:00}_ID" : $"MountainDesert_Single_{layer - 5:00}_ID";

    static Texture2D Load(string name, bool linear)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear) { wrapMode = TextureWrapMode.Repeat };
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(TexturePath(name)), false))
        {
            Object.DestroyImmediate(texture);
            throw new InvalidDataException("Could not decode " + name);
        }
        return texture;
    }

    static Texture2DArray BuildAlbedoAtlas(Layer[] layers)
    {
        var atlas = new Texture2DArray(ColorSize, ColorSize, LayerCount, TextureFormat.RGBA32, true, false)
        { name = "Civ6 Reference Surface Materials", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        try
        {
            for (int layer = 0; layer < LayerCount; layer++)
            {
                Texture2D color = Load(layers[layer].baseColor, false), height = Load(layers[layer].height, true);
                try
                {
                    var pixels = new Color32[ColorSize * ColorSize];
                    for (int y = 0; y < ColorSize; y++) for (int x = 0; x < ColorSize; x++)
                    {
                        float u = (x + .5f) / ColorSize, v = (y + .5f) / ColorSize;
                        Color c = color.GetPixelBilinear(u, v); c.a = height.GetPixelBilinear(u, v).r;
                        pixels[y * ColorSize + x] = c;
                    }
                    atlas.SetPixels32(pixels, layer);
                }
                finally { Object.DestroyImmediate(color); Object.DestroyImmediate(height); }
            }
            atlas.Apply(true, false);
            return Save(atlas, AssetRoot + "/Civ6 Reference Surface Materials.asset");
        }
        catch { if (atlas && !AssetDatabase.Contains(atlas)) Object.DestroyImmediate(atlas); throw; }
    }

    static HexNearLandMaterialProfile BuildMaterialProfile(Layer[] layers)
    {
        Texture2DArray response = BuildResponse(layers);
        Texture2DArray masks = BuildMasks();
        HexNearLandMaterialProfile profile = AssetDatabase.LoadAssetAtPath<HexNearLandMaterialProfile>(LandProfilePath);
        if (!profile) { profile = ScriptableObject.CreateInstance<HexNearLandMaterialProfile>(); AssetDatabase.CreateAsset(profile, LandProfilePath); }
        profile.responseAtlas = response;
        profile.materialMasks = masks;
        profile.layerNames = layers.Select(x => x.name).ToArray();
        using (SHA256 hash = SHA256.Create())
            profile.provenance = "Local Civilization VI SDK materials, original authorship retained. SourceAudit.json SHA256 " +
                BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(Path.Combine(Sources, "SourceAudit.json")))).Replace("-", "").ToLowerInvariant() +
                ". RG=signed dH/du,dH/dv in Unity UV orientation; B=source gloss (white glossy), A=source Fuzz. Fuzz BRDF and native CLEAN filtering were not recovered. Material mask IDs were decoded before filtering; geometry heights were not changed.";
        EditorUtility.SetDirty(profile);
        return profile;
    }

    static Texture2DArray BuildResponse(Layer[] layers)
    {
        var atlas = new Texture2DArray(ResponseSize, ResponseSize, LayerCount, TextureFormat.RGBAHalf, true, true)
        { name = "Civ6 Reference Land Response", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        try
        {
            for (int layer = 0; layer < LayerCount; layer++)
            {
                Texture2D height = Load(layers[layer].height, true), gloss = Load(layers[layer].gloss, true), fuzz = Load(layers[layer].fuzz, true);
                try
                {
                    var pixels = new Color[ResponseSize * ResponseSize];
                    // First sample one periodic height field, then differentiate that same
                    // field. Signed HALF avoids UNORM clamping and keeps small slopes at mips.
                    var h = new float[pixels.Length];
                    for (int y = 0; y < ResponseSize; y++) for (int x = 0; x < ResponseSize; x++)
                        h[y * ResponseSize + x] = height.GetPixelBilinear((x + .5f) / ResponseSize, (y + .5f) / ResponseSize).r;
                    for (int y = 0; y < ResponseSize; y++) for (int x = 0; x < ResponseSize; x++)
                    {
                        float u = (x + .5f) / ResponseSize, v = (y + .5f) / ResponseSize;
                        float du = (h[y * ResponseSize + (x + 1) % ResponseSize] - h[y * ResponseSize + (x + ResponseSize - 1) % ResponseSize]) * (.5f * ResponseSize);
                        float dv = (h[((y + 1) % ResponseSize) * ResponseSize + x] - h[((y + ResponseSize - 1) % ResponseSize) * ResponseSize + x]) * (.5f * ResponseSize);
                        pixels[y * ResponseSize + x] = new Color(du, dv, gloss.GetPixelBilinear(u, v).r, fuzz.GetPixelBilinear(u, v).r);
                    }
                    atlas.SetPixels(pixels, layer);
                }
                finally { Object.DestroyImmediate(height); Object.DestroyImmediate(gloss); Object.DestroyImmediate(fuzz); }
            }
            atlas.Apply(true, false);
            return Save(atlas, AssetRoot + "/Civ6 Reference Land Response.asset");
        }
        catch { if (atlas && !AssetDatabase.Contains(atlas)) Object.DestroyImmediate(atlas); throw; }
    }

    static Texture2DArray BuildMasks()
    {
        const int size = 256;
        var atlas = new Texture2DArray(size, size, 10, TextureFormat.RGBA32, false, true)
        { name = "Civ6 Reference Land Material Masks", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        try
        {
            for (int layer = 0; layer < 10; layer++)
            {
                var pixels = new Color32[size * size];
                if (layer != 5)
                {
                    Texture2D id = Load(ShapeIdName(layer), true); id.wrapMode = TextureWrapMode.Clamp;
                    try
                    {
                        Color32[] decoded = id.GetPixels32();
                        for (int i = 0; i < decoded.Length; i++)
                        {
                            byte code = decoded[i].a;
                            decoded[i] = layer < 5 ? new Color32(code == 128 ? (byte)255 : (byte)0, code == 153 ? (byte)255 : (byte)0, 0, 0) :
                                new Color32(code == 110 ? (byte)255 : (byte)0, code == 112 ? (byte)255 : (byte)0, code == 117 ? (byte)255 : (byte)0, code == 89 ? (byte)255 : (byte)0);
                        }
                        id.SetPixels32(decoded); id.Apply(false, false);
                        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                            pixels[y * size + x] = id.GetPixelBilinear((x + .5f) / size, (y + .5f) / size);
                    }
                    finally { Object.DestroyImmediate(id); }
                }
                atlas.SetPixels32(pixels, layer);
            }
            atlas.Apply(false, false);
            return Save(atlas, AssetRoot + "/Civ6 Reference Land Material Masks.asset");
        }
        catch { if (atlas && !AssetDatabase.Contains(atlas)) Object.DestroyImmediate(atlas); throw; }
    }

    static T Save<T>(T created, string path) where T : Object
    {
        T existing = AssetDatabase.LoadAssetAtPath<T>(path);
        if (!existing) { AssetDatabase.CreateAsset(created, path); return created; }
        EditorUtility.CopySerialized(created, existing);
        EditorUtility.SetDirty(existing);
        Object.DestroyImmediate(created);
        return existing;
    }
}
