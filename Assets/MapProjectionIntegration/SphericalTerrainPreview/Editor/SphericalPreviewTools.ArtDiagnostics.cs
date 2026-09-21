using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        [Serializable] sealed class ArtSnapshot
        {
            public string profile, albedo, response, masks;
            public bool landResponseReady;
            public int albedoLayers;
            public long cachedMeshBytes, savedWaterChannelBytes;
            public List<ArtMaterial> materials = new();
            public List<ArtWater> water = new();
            public List<ArtPoint> points = new();
        }
        [Serializable] sealed class ArtWater
        {
            public string name, shader, palettePropertyType;
            public int vertices, stride;
            public Vector4 riverColor;
        }
        [Serializable] sealed class ArtMaterial
        {
            public string name, shader, albedo, response;
            public int vertices;
            public int[] uvDimensions;
            public float useResponse;
            public Vector4 normalParameters;
        }
        [Serializable] sealed class ArtPoint
        {
            public Vector2 geographic;
            public int cell, sourceLandform, sourceBiome;
            public SphericalSurface.Sample sample;
        }
        static string CaptureArtDiagnostics()
        {
            var terrain = RequirePreview();
            var profile = terrain.terrainProfile;
            var report = new ArtSnapshot { profile = AssetDatabase.GetAssetPath(profile),
                albedo = AssetDatabase.GetAssetPath(profile.albedoAtlas), albedoLayers = profile.albedoAtlas.depth,
                response = AssetDatabase.GetAssetPath(profile.landMaterials.responseAtlas),
                masks = AssetDatabase.GetAssetPath(profile.landMaterials.materialMasks), landResponseReady = profile.landMaterials.IsReady,
                cachedMeshBytes = terrain.CachedMeshPayloadBytes, savedWaterChannelBytes = terrain.CachedWaterChannelSavingsBytes };
            var seen = new HashSet<Material>();
            foreach (var renderer in terrain.GetComponentsInChildren<MeshRenderer>())
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || !seen.Add(material)) continue;
                    var mesh = filter.sharedMesh;
                    if (material.shader.name == "WW2/Spherical Terrain Preview/Water")
                        report.water.Add(new ArtWater { name = material.name, shader = material.shader.name, vertices = mesh.vertexCount,
                            stride = mesh.GetVertexBufferStride(0), riverColor = material.GetVector("_RiverWater"),
                            palettePropertyType = material.shader.GetPropertyType(material.shader.FindPropertyIndex("_RiverWater")).ToString() });
                    if (!material.HasProperty("_Albedos")) continue;
                    var dimensions = new int[6];
                    for (int i = 0; i < 6; i++)
                        dimensions[i] = mesh.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + i));
                    report.materials.Add(new ArtMaterial { name = material.name, shader = material.shader.name, vertices = mesh.vertexCount,
                        uvDimensions = dimensions, useResponse = material.GetFloat("_UseResponse"), normalParameters = material.GetVector("_LandParameters"),
                        albedo = AssetDatabase.GetAssetPath(material.GetTexture("_Albedos")), response = AssetDatabase.GetAssetPath(material.GetTexture("_LandResponse")) });
                }
            }
            var focus = RequireNavigation().FocusDirection;
            SphericalSurface.Frame(focus, out var east, out var north);
            for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++)
            {
                var direction = (focus + (east * x + north * y) * (15f / terrain.radius)).normalized;
                var s = terrain.Surface.Evaluate(direction);
                var cell = terrain.World.Cells[s.Cell];
                report.points.Add(new ArtPoint { geographic = terrain.World.LonLat(direction), cell = s.Cell,
                    sourceBiome = cell.Biome, sourceLandform = cell.Landform, sample = s });
            }
            Directory.CreateDirectory(Artifacts);
            string path = Path.Combine(Artifacts, "ArtDiagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            return path;
        }
    }
}
