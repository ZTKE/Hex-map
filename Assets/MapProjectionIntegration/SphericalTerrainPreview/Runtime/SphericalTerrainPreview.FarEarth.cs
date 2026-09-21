using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        [Header("Middle / far Earth presentation")]
        [Range(1, 1.6f)] public float farLandSaturation = 1.24f;
        [Range(1, 1.6f)] public float farOceanSaturation = 1.18f;
        [Range(1, 1.6f)] public float farAtmosphereSaturation = 1.20f;
        [Range(0, 1)] public float farCloudOpacity = .88f;
        [Range(0, .2f)] public float farCloudDegreesPerSecond = .075f;
        [Range(0, 1)] public float farAtmosphereStrength = .48f;

        /// <summary>Zero throughout the near art range; independently measurable for integration validation.</summary>
        public float FarEarthColorWeight { get; private set; }
        public float FarEarthAtmosphereWeight { get; private set; }
        public float FarEarthLightWeight { get; private set; }
        public float FarEarthCloudShellHeight { get; private set; }
        public float FarEarthAtmosphereShellHeight { get; private set; }
        public double FarEarthAnimationSeconds { get; private set; }
        public bool FarEarthAtmosphereVisible => farEarthLayers && farEarthLayers.activeSelf;

        GameObject farEarthLayers;
        Material farCloudMaterial, farAtmosphereMaterial;
        Texture2D farCloudMap;
        Mesh farAtmosphereMesh;
        double farEarthClockOrigin;
        Vector4 farAppliedGrade, farAppliedLight;
        bool farGradeCached, farLightCached;
        float farAppliedCloudVisibility = float.NaN;
        float farAppliedAtmosphereVisibility = float.NaN;
        static readonly int FarGradeId = Shader.PropertyToID("_FarEarthGrade");
        static readonly int FarLightId = Shader.PropertyToID("_FarEarthLight");
        static readonly int CloudMotionId = Shader.PropertyToID("_CloudMotion");
        static readonly int CloudVisibilityId = Shader.PropertyToID("_Visibility");

        void InitializeFarEarth()
        {
            Shader shader = Resources.Load<Shader>("SphericalTerrainPreview/SphericalEarthAtmosphere");
            if (!shader) throw new InvalidOperationException("Missing spherical Earth atmosphere shader.");
            farCloudMap = Resources.Load<Texture2D>("SphericalTerrainPreview/EarthClouds8K");
            if (!farCloudMap) throw new InvalidOperationException("Missing saved Earth cloud coverage texture.");
            farAtmosphereMesh = CreateFarAtmosphereMesh();
            farEarthLayers = new GameObject("Earth atmosphere — geographic moving cloud layers");
            farEarthLayers.transform.SetParent(transform, false);
            farEarthLayers.layer = gameObject.layer;
            farCloudMaterial = Own(new Material(shader) { name = "Earth clouds — real-time differential winds", hideFlags = HideFlags.DontSave });
            farAtmosphereMaterial = Own(new Material(shader) { name = "Earth atmosphere — blue daylight limb", hideFlags = HideFlags.DontSave });
            farCloudMaterial.SetTexture("_CloudMap", farCloudMap);
            farCloudMaterial.SetFloat("_LayerKind", 0);
            farAtmosphereMaterial.SetFloat("_LayerKind", 1);
            // Light scattering adds a faint glow over the restored cosmos;
            // alpha blending against bright nebulae otherwise hides the rim.
            farAtmosphereMaterial.SetFloat("_DstBlend", (float)BlendMode.One);
            // Explicit ordering ensures the thin atmosphere lies over clouds;
            // UI is drawn by its own camera and remains unaffected.
            farCloudMaterial.renderQueue = 3010;
            farAtmosphereMaterial.renderQueue = 3020;
            // Game relief is exaggerated relative to the 3300-unit globe.
            // The old +8 shell intersected plateau mountains and its R3 chord
            // facets cut hard cloud-free polygons into the middle-distance art.
            // Use the same conservative relief bound as terrain picking, plus
            // clearance for the atmosphere mesh's inward triangular chords.
            float reliefBound = terrainProfile ? Mathf.Max(terrainProfile.mountainHeight,
                terrainProfile.desertMountainHeight) * 1.4f +
                SphericalPlateauField.PlatformHeight(terrainProfile.plateauHeight) + 2 : 0;
            FarEarthCloudShellHeight = Mathf.Max(48, reliefBound + 12);
            FarEarthAtmosphereShellHeight = FarEarthCloudShellHeight + 27;
            CreateFarEarthLayer("Weather systems and broken low clouds", farCloudMaterial, radius + FarEarthCloudShellHeight);
            CreateFarEarthLayer("Thin atmosphere shell", farAtmosphereMaterial, radius + FarEarthAtmosphereShellHeight);
            farGradeCached = farLightCached = false;
            farAppliedCloudVisibility = farAppliedAtmosphereVisibility = float.NaN;
            farEarthClockOrigin = Time.realtimeSinceStartupAsDouble;
            UpdateFarEarth();
        }

        void CreateFarEarthLayer(string name, Material material, float shellRadius)
        {
            var layer = new GameObject(name) { layer = gameObject.layer };
            layer.transform.SetParent(farEarthLayers.transform, false);
            layer.transform.localScale = Vector3.one * shellRadius;
            layer.AddComponent<MeshFilter>().sharedMesh = farAtmosphereMesh;
            var renderer = layer.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            material.SetFloat("_PlanetRadius", radius);
            material.SetVector("_SphereCenter", transform.position);
        }

        void UpdateFarEarth()
        {
            ApplyNaturalSatelliteBindings();
            FarEarthColorWeight = FarColorWeight(altitude);
            FarEarthAtmosphereWeight = FarAtmosphereWeight(altitude);
            FarEarthLightWeight = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1800, 4500, altitude));
            var grade = new Vector4(farLandSaturation, farOceanSaturation,
                farAtmosphereSaturation, FarEarthColorWeight);
            var light = naturalSatelliteEnabled ? new Vector4(FarEarthLightWeight, 1, 1, 0) :
                new Vector4(FarEarthLightWeight, 1.22f, 1.65f, 0);
            bool gradeChanged = !farGradeCached || !grade.Equals(farAppliedGrade);
            bool lightChanged = !farLightCached || !light.Equals(farAppliedLight);
            // Near presentation and fixed-altitude rotation leave these values
            // unchanged. Avoid repeating native material lookups and writes.
            if (gradeChanged || lightChanged)
            {
                foreach (Material material in presentationMaterials)
                {
                    if (!material) continue;
                    if (gradeChanged && material.HasProperty(FarGradeId)) material.SetVector(FarGradeId, grade);
                    if (lightChanged && material.HasProperty(FarLightId)) material.SetVector(FarLightId, light);
                }
                if (gradeChanged && farAtmosphereMaterial) farAtmosphereMaterial.SetVector(FarGradeId, grade);
                farAppliedGrade = grade; farAppliedLight = light;
                farGradeCached = farLightCached = true;
            }
            if (!farEarthLayers) return;
            bool visible = FarEarthAtmosphereWeight > 0 && satelliteColor && satelliteRelief;
            if (farEarthLayers.activeSelf != visible) farEarthLayers.SetActive(visible);
            // Elapsed real time is independent of simulation speed and pause.
            // Longitude wraps in the shader; drift keeps latitude fixed and
            // texture derivatives are corrected across the dateline.
            FarEarthAnimationSeconds = Time.realtimeSinceStartupAsDouble - farEarthClockOrigin;
            // Hidden clouds retain real elapsed time without touching the GPU.
            // Reappearing clouds therefore resume at their current wind position.
            if (visible)
            {
                float angle = (float)(FarEarthAnimationSeconds * farCloudDegreesPerSecond * Mathf.Deg2Rad);
                float evolution = (float)FarEarthAnimationSeconds * .0016f;
                var motion = new Vector4(angle, evolution,
                    Mathf.SmoothStep(0, 1, Mathf.InverseLerp(900, 2200, altitude)), 0);
                farCloudMaterial.SetVector(CloudMotionId, motion);
            }
            float cloudVisibility = visible ? FarEarthAtmosphereWeight * farCloudOpacity : 0;
            float atmosphereVisibility = visible ? FarEarthAtmosphereWeight * farAtmosphereStrength : 0;
            if (cloudVisibility != farAppliedCloudVisibility)
            {
                farCloudMaterial.SetFloat(CloudVisibilityId, cloudVisibility);
                farAppliedCloudVisibility = cloudVisibility;
            }
            if (atmosphereVisibility != farAppliedAtmosphereVisibility)
            {
                farAtmosphereMaterial.SetFloat(CloudVisibilityId, atmosphereVisibility);
                farAppliedAtmosphereVisibility = atmosphereVisibility;
            }
        }

        public static float FarColorWeight(float height)
            => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(450, 1800, height));
        public static float FarAtmosphereWeight(float height)
            => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(650, 1800, height));

        static Mesh CreateFarAtmosphereMesh()
        {
            // Reuse the actual IcoSphere pack so the horizon is regular at the
            // poles too. This is an independent unit shell, never terrain LOD.
            var pack = global::IcoSphere.Pack.Read(3);
            var vertices = new Vector3[pack.verts.Length];
            var triangles = new int[pack.tris.Length * 3];
            for (int i = 0; i < vertices.Length; i++) vertices[i] = pack.verts[i].normalized;
            int at = 0;
            foreach (var triangle in pack.tris)
            {
                int a = triangle[0], b = triangle[1], c = triangle[2];
                if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), vertices[a]) < 0)
                    (b, c) = (c, b);
                triangles[at++] = a; triangles[at++] = b; triangles[at++] = c;
            }
            var mesh = new Mesh { name = "Earth atmosphere unit IcoSphere", hideFlags = HideFlags.DontSave,
                indexFormat = IndexFormat.UInt32, vertices = vertices, normals = vertices, triangles = triangles };
            mesh.RecalculateBounds(); mesh.UploadMeshData(true);
            return mesh;
        }

        void DisposeFarEarth()
        {
            // All atmospheric renderers share a mesh; destroy it exactly once.
            if (farEarthLayers) Destroy(farEarthLayers);
            if (farAtmosphereMesh) Destroy(farAtmosphereMesh);
            farCloudMap = null; // Resources owns the shared imported texture.
        }
    }
}
