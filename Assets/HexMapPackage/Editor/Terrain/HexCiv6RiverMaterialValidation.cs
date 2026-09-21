using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Read-only imported river texture and fixed-binding verification, not a final BRDF test.</summary>
public static class HexCiv6RiverMaterialValidation
{
    const string ShaderName = "Hex Map/Relief";
    const string MaterialPath = "Assets/HexMapPackage/Materials/Hex Relief.mat";
    static readonly string[] Properties = {
        "_Civ6RiverWaveMoments", "_Civ6RiverDensity", "_Civ6RiverScatter", "_Civ6RiverBankAlbedo"
    };

    [Serializable] sealed class Expectations { public ExpectedTexture[] textures; }
    [Serializable] sealed class ExpectedTexture
    {
        public string property, assetPath, guid, expectedPath, expectedSha256, sourceSha256, format, wrap;
        public int width, height;
        public bool sRGB, mipmaps;
    }
    [Serializable] sealed class TextureResult
    {
        public string property, assetPath, expectedPath, expectedGuid, actualGuid;
        public string expectedFormat, actualFormat, graphicsFormat, importerType, wrapU, wrapV, importerWrapU, importerWrapV;
        public string sourceSha256, expectedSha256, exception;
        public int expectedWidth, expectedHeight, width, height, mipCount, comparedPixels, invalidActualChannels, invalidExpectedChannels;
        public int channelsOverTolerance, worstPixel = -1, worstChannel = -1;
        public bool expectedSrgb, actualSrgb, importerSrgb, expectedMips, importerMips, alphaIsTransparency;
        public bool guidMatches, dimensionsMatch, formatMatches, srgbMatches, wrapMatches, mipmapsMatch;
        public bool sourceHashMatches, expectedHashMatches, gpuReadbackCompleted, finite, passed;
        public float tolerance, maximumAbsoluteError;
        public float[] maximumErrorByChannel = new float[4];
        public double meanAbsoluteError;
    }
    [Serializable] sealed class BindingResult
    {
        public string material, property, expectedAsset, actualAsset, actualGuid;
        public bool hasProperty, bound, passed;
    }
    [Serializable] sealed class Report
    {
        public string timestamp, mode, graphicsDevice, activeColorSpace, expectationsPath, expectationsSha256, comparison;
        public bool passed, shaderFound, shaderSupported, assetMaterialFound, assetMaterialShaderMatches;
        public bool defaultMaterialCreated, rendererChecksExecuted;
        public int shaderErrorCount, reliefRendererCount, uniqueRendererMaterials;
        public TextureResult[] textures;
        public BindingResult[] bindings;
        public string[] rendererNames, shaderMessages, failures;
    }

    public static string Validate()
    {
        string project = Directory.GetParent(Application.dataPath).FullName;
        string directory = Path.Combine(project, "Artifacts/RiverReference/Validation");
        string mode = EditorApplication.isPlaying ? "play" : "edit";
        string output = Path.Combine(directory, "ImportedRiverMaterials-" + mode + ".json");
        var report = new Report {
            timestamp = DateTime.UtcNow.ToString("O"), mode = mode,
            graphicsDevice = SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType,
            activeColorSpace = QualitySettings.activeColorSpace.ToString(),
            expectationsPath = Path.Combine(directory, "RuntimeExpectations.json"),
            comparison = "All imported mip-0 texels sampled by Graphics.Blit into linear ARGBFloat and read back as linear RGBAFloat. " +
                "Expected floats are already Unity bottom-up, with bank RGB sRGB-decoded. Checks import data, fixed bindings and reported shader errors; not final BRDF, terrain coverage, or visual-quality proof."
        };
        var failures = new List<string>();
        var textureResults = new List<TextureResult>();
        var bindings = new List<BindingResult>();
        var shaderMessages = new List<string>();
        var rendererNames = new List<string>();
        Material defaultMaterial = null;
        Shader shader = null;
        try
        {
            report.expectationsSha256 = HashFile(report.expectationsPath);
            Expectations expectations = JsonUtility.FromJson<Expectations>(File.ReadAllText(report.expectationsPath));
            RequireSchema(expectations);
            shader = Shader.Find(ShaderName);
            report.shaderFound = shader;
            report.shaderSupported = shader && shader.isSupported;
            if (!report.shaderFound || !report.shaderSupported) failures.Add("Relief shader is missing or unsupported.");
            Material assetMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            report.assetMaterialFound = assetMaterial;
            report.assetMaterialShaderMatches = assetMaterial && shader && assetMaterial.shader == shader;
            if (!report.assetMaterialShaderMatches) failures.Add("Hex Relief.mat is missing or does not use the resolved Relief shader.");
            if (shader)
            {
                defaultMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                report.defaultMaterialCreated = true;
            }
            foreach (ExpectedTexture expected in expectations.textures)
            {
                var item = new TextureResult {
                    property = expected.property, assetPath = expected.assetPath, expectedPath = expected.expectedPath,
                    expectedGuid = expected.guid, expectedWidth = expected.width, expectedHeight = expected.height,
                    expectedFormat = expected.format, expectedSrgb = expected.sRGB, expectedMips = expected.mipmaps,
                    tolerance = expected.property == "_Civ6RiverBankAlbedo" ? .002f : .00001f
                };
                textureResults.Add(item);
                try { ValidateTexture(project, expected, item); }
                catch (Exception exception) { item.exception = exception.ToString(); item.passed = false; }
                if (!item.passed) failures.Add(expected.property + ": import or GPU texel comparison failed; see texture result.");
                ValidateBinding(assetMaterial, MaterialPath, expected, bindings, failures);
                ValidateBinding(defaultMaterial, "new Material(Shader.Find(\"" + ShaderName + "\"))", expected, bindings, failures);
            }
            if (EditorApplication.isPlaying && shader)
            {
                report.rendererChecksExecuted = true;
                var checkedMaterials = new HashSet<int>();
                foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (!renderer || EditorUtility.IsPersistent(renderer) ||
                        !renderer.gameObject.scene.IsValid() || !renderer.gameObject.scene.isLoaded) continue;
                    bool usesRelief = false;
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (!material || material.shader != shader) continue;
                        usesRelief = true;
                        if (!checkedMaterials.Add(material.GetInstanceID())) continue;
                        foreach (ExpectedTexture expected in expectations.textures)
                            ValidateBinding(material, "scene shared material: " + material.name + " #" + material.GetInstanceID(),
                                expected, bindings, failures);
                    }
                    if (usesRelief)
                    {
                        report.reliefRendererCount++;
                        rendererNames.Add(renderer.gameObject.scene.path + ":" + TransformPath(renderer.transform));
                    }
                }
                report.uniqueRendererMaterials = checkedMaterials.Count;
                if (report.reliefRendererCount == 0) failures.Add("No Relief renderer was present in the loaded Play scene.");
            }
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        finally
        {
            if (defaultMaterial) Object.DestroyImmediate(defaultMaterial);
            // Collect after the actual texture/material work, even if an earlier item failed.
            if (shader)
            {
                try
                {
                    foreach (var message in ShaderUtil.GetShaderMessages(shader))
                    {
                        shaderMessages.Add(message.severity + ": " + message.file + ":" + message.line + " " + message.message);
                        if (string.Equals(message.severity.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                            report.shaderErrorCount++;
                    }
                }
                catch (Exception exception) { failures.Add("Reading Relief shader messages failed: " + exception); }
            }
        }
        if (report.shaderErrorCount > 0) failures.Add("Relief shader has " + report.shaderErrorCount + " reported errors.");
        report.textures = textureResults.ToArray(); report.bindings = bindings.ToArray();
        report.rendererNames = rendererNames.ToArray(); report.shaderMessages = shaderMessages.ToArray();
        report.failures = failures.ToArray(); report.passed = failures.Count == 0;
        Directory.CreateDirectory(directory);
        File.WriteAllText(output, JsonUtility.ToJson(report, true));
        if (!report.passed) throw new InvalidOperationException("Imported Civ6 river material validation failed; report: " + output);
        return output;
    }

    static void RequireSchema(Expectations expectations)
    {
        if (expectations == null || expectations.textures == null || expectations.textures.Length != Properties.Length)
            throw new InvalidDataException("Expected exactly the four river texture definitions.");
        var seen = new HashSet<string>();
        foreach (ExpectedTexture item in expectations.textures)
        {
            if (item == null || Array.IndexOf(Properties, item.property) < 0 || !seen.Add(item.property) ||
                string.IsNullOrEmpty(item.assetPath) || string.IsNullOrEmpty(item.guid) ||
                string.IsNullOrEmpty(item.expectedPath) || string.IsNullOrEmpty(item.expectedSha256) ||
                string.IsNullOrEmpty(item.sourceSha256) || item.width <= 0 || item.height <= 0)
                throw new InvalidDataException("Missing, duplicate or invalid river texture expectation.");
        }
    }

    static void ValidateTexture(string project, ExpectedTexture expected, TextureResult item)
    {
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(expected.assetPath);
        if (!texture) throw new InvalidDataException("Texture asset is missing: " + expected.assetPath);
        TextureImporter importer = AssetImporter.GetAtPath(expected.assetPath) as TextureImporter;
        if (!importer) throw new InvalidDataException("TextureImporter is missing: " + expected.assetPath);
        item.actualGuid = AssetDatabase.AssetPathToGUID(expected.assetPath);
        item.guidMatches = string.Equals(item.actualGuid, expected.guid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AssetDatabase.GUIDToAssetPath(expected.guid), expected.assetPath, StringComparison.Ordinal);
        item.width = texture.width; item.height = texture.height;
        item.dimensionsMatch = texture.width == expected.width && texture.height == expected.height;
        item.actualFormat = texture.format.ToString(); item.graphicsFormat = texture.graphicsFormat.ToString();
        item.formatMatches = item.actualFormat == expected.format;
        item.importerType = importer.textureType.ToString(); item.alphaIsTransparency = importer.alphaIsTransparency;
        item.actualSrgb = UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        item.importerSrgb = importer.sRGBTexture;
        item.srgbMatches = item.actualSrgb == expected.sRGB && importer.sRGBTexture == expected.sRGB;
        item.wrapU = texture.wrapModeU.ToString(); item.wrapV = texture.wrapModeV.ToString();
        item.importerWrapU = importer.wrapModeU.ToString(); item.importerWrapV = importer.wrapModeV.ToString();
        item.wrapMatches = item.wrapU == expected.wrap && item.wrapV == expected.wrap &&
            item.importerWrapU == expected.wrap && item.importerWrapV == expected.wrap;
        item.mipCount = texture.mipmapCount; item.importerMips = importer.mipmapEnabled;
        item.mipmapsMatch = item.importerMips == expected.mipmaps && (item.mipCount > 1) == expected.mipmaps;
        item.sourceSha256 = HashFile(Path.Combine(project, expected.assetPath));
        item.sourceHashMatches = string.Equals(item.sourceSha256, expected.sourceSha256, StringComparison.OrdinalIgnoreCase);
        item.expectedSha256 = HashFile(expected.expectedPath);
        item.expectedHashMatches = string.Equals(item.expectedSha256, expected.expectedSha256, StringComparison.OrdinalIgnoreCase);
        if (!item.expectedHashMatches) throw new InvalidDataException("Expected GPU float file hash differs: " + expected.expectedPath);
        if (!item.dimensionsMatch) throw new InvalidDataException("Texture dimensions differ; refusing a rescaled GPU comparison.");
        CompareGpuPixels(texture, expected, item);
        item.passed = item.importerType == "Default" && !item.alphaIsTransparency && item.guidMatches && item.dimensionsMatch && item.formatMatches && item.srgbMatches &&
            item.wrapMatches && item.mipmapsMatch && item.sourceHashMatches && item.expectedHashMatches &&
            item.gpuReadbackCompleted && item.finite && item.channelsOverTolerance == 0;
    }

    static void CompareGpuPixels(Texture2D texture, ExpectedTexture expected, TextureResult item)
    {
        if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) ||
            !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
            throw new NotSupportedException("ARGBFloat rendering and RGBAFloat readback are required.");
        byte[] bytes = File.ReadAllBytes(expected.expectedPath);
        int pixelCount = checked(expected.width * expected.height);
        if (bytes.Length != checked(pixelCount * 4 * sizeof(float)))
            throw new InvalidDataException("Expected float file has the wrong byte length.");
        float[] expectedValues = new float[pixelCount * 4];
        // BinaryReader explicitly consumes little-endian float32, independent of host byte order.
        using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
            for (int i = 0; i < expectedValues.Length; i++) expectedValues[i] = reader.ReadSingle();
        RenderTexture previous = RenderTexture.active;
        bool previousSrgbWrite = GL.sRGBWrite;
        RenderTexture target = null;
        Texture2D readback = null;
        try
        {
            target = RenderTexture.GetTemporary(expected.width, expected.height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            GL.sRGBWrite = false;
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            readback = new Texture2D(expected.width, expected.height, TextureFormat.RGBAFloat, false, true) {
                hideFlags = HideFlags.HideAndDontSave
            };
            readback.ReadPixels(new Rect(0, 0, expected.width, expected.height), 0, 0, false);
            readback.Apply(false, false);
            Color[] actual = readback.GetPixels(0);
            if (actual.Length != pixelCount) throw new InvalidDataException("Unexpected GPU readback pixel count.");
            double errorSum = 0;
            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    float value = actual[pixel][channel], wanted = expectedValues[pixel * 4 + channel];
                    bool actualFinite = Finite(value), expectedFinite = Finite(wanted);
                    if (!actualFinite) item.invalidActualChannels++;
                    if (!expectedFinite) item.invalidExpectedChannels++;
                    if (!actualFinite || !expectedFinite) continue;
                    float error = Mathf.Abs(value - wanted);
                    errorSum += error;
                    item.maximumErrorByChannel[channel] = Mathf.Max(item.maximumErrorByChannel[channel], error);
                    if (error > item.maximumAbsoluteError)
                    {
                        item.maximumAbsoluteError = error; item.worstPixel = pixel; item.worstChannel = channel;
                    }
                    if (error > item.tolerance) item.channelsOverTolerance++;
                }
            }
            item.comparedPixels = pixelCount; item.meanAbsoluteError = errorSum / expectedValues.Length;
            item.finite = item.invalidActualChannels == 0 && item.invalidExpectedChannels == 0;
            item.gpuReadbackCompleted = true;
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = previousSrgbWrite;
            if (target) RenderTexture.ReleaseTemporary(target);
            if (readback) Object.DestroyImmediate(readback);
        }
    }

    static void ValidateBinding(Material material, string label, ExpectedTexture expected,
        List<BindingResult> results, List<string> failures)
    {
        var result = new BindingResult { material = label, property = expected.property, expectedAsset = expected.assetPath };
        results.Add(result);
        result.hasProperty = material && material.HasProperty(expected.property);
        Texture actual = result.hasProperty ? material.GetTexture(expected.property) : null;
        result.bound = actual;
        result.actualAsset = actual ? AssetDatabase.GetAssetPath(actual) : "";
        result.actualGuid = string.IsNullOrEmpty(result.actualAsset) ? "" : AssetDatabase.AssetPathToGUID(result.actualAsset);
        result.passed = result.hasProperty && result.bound && result.actualAsset == expected.assetPath &&
            string.Equals(result.actualGuid, expected.guid, StringComparison.OrdinalIgnoreCase) &&
            actual == AssetDatabase.LoadAssetAtPath<Texture2D>(expected.assetPath);
        if (!result.passed) failures.Add(label + ": fixed texture binding failed for " + expected.property);
    }

    static string TransformPath(Transform transform)
    {
        string path = transform.name;
        for (Transform parent = transform.parent; parent; parent = parent.parent) path = parent.name + "/" + path;
        return path;
    }
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    static string HashFile(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
