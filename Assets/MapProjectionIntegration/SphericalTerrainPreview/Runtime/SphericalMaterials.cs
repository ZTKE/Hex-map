using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Owns preview materials, never shader globals or the source art materials.
    /// The imported atlas layer IDs and signed height responses are the same as
    /// HexNearLandMaterial; only their placement and lighting frame are spherical.
    /// </summary>
    public static class SphericalMaterials
    {
        static Material Create(string shaderName, string name)
        {
            Shader shader = Shader.Find(shaderName);
            if (!shader) throw new InvalidOperationException("Missing spherical preview shader: " + shaderName);
            return new Material(shader) { name = name, hideFlags = HideFlags.DontSave, enableInstancing = true };
        }

        public static Material CreateTerrain(HexNearTerrainProfile profile)
        {
            if (!profile || !profile.IsReady)
                throw new ArgumentException("The spherical terrain preview requires the existing ready near terrain art profile.", nameof(profile));
            Material material = Create("WW2/Spherical Terrain Preview/Terrain", "Spherical terrain — existing near art");
            material.SetTexture("_Albedos", profile.albedoAtlas);
            material.SetTexture("_Shapes", profile.shapeAtlas);
            material.SetFloat("_MaterialTiling", profile.materialTiling);
            material.SetFloat("_MountainHeight", profile.mountainHeight);
            material.SetFloat("_DesertMountainHeight", profile.desertMountainHeight);
            material.SetFloat("_MountainGeometryScale", SphericalSurface.MountainReliefScale);
            material.SetFloat("_PlateauHeight", SphericalPlateauField.PlatformHeight(profile.plateauHeight));
            material.SetFloat("_PlateauWeathering", profile.plateauWeathering);
            foreach (var binding in new[] { ("_CoastAlbedo", "TER_Coast_B"), ("_CoastHeight", "TER_Coast_H"),
                ("_CliffAlbedo", "TER_Cliff_B"), ("_CliffHeight", "TER_Cliff_H") })
            {
                var texture = Resources.Load<Texture2D>("SphericalTerrainPreview/Water/" + binding.Item2);
                if (!texture) throw new InvalidOperationException("Missing source coast material: " + binding.Item2);
                material.SetTexture(binding.Item1, texture);
            }
            HexNearLandMaterialProfile land = profile.landMaterials;
            bool response = land && land.IsReady && profile.albedoAtlas.depth >= 18;
            material.SetFloat("_UseResponse", response ? 1 : 0);
            material.SetFloat("_UseMasks", response && land.HasMasks ? 1 : 0);
            if (response)
            {
                material.SetTexture("_LandResponse", land.responseAtlas);
                if (land.HasMasks) material.SetTexture("_LandMasks", land.materialMasks);
                material.SetVector("_LandParameters", new Vector4(land.normalStrength, land.heightBlend, land.macroStrength, land.macroUVRatio));
                material.SetVector("_LandShading", new Vector4(Mathf.Min(land.roughnessMin, land.roughnessMax),
                    Mathf.Max(land.roughnessMin, land.roughnessMax), land.specularStrength, 0));
            }
            return material;
        }

        public static Material CreateWater(HexNearTerrainProfile profile)
        {
            Material material = Create("WW2/Spherical Terrain Preview/Water", "Spherical water — existing near palette");
            if (profile)
            {
                SetColor(material, "_ShallowWater", profile.shallowWater);
                SetColor(material, "_DeepWater", profile.deepWater);
                SetColor(material, "_NearShallowWater", profile.shallowWater);
                SetColor(material, "_NearDeepWater", profile.deepWater);
            }
            return material;
        }

        public static Material CreateLines(Color color)
        {
            Material material = Create("WW2/Spherical Terrain Preview/Lines", "Spherical cell boundaries");
            material.SetColor("_Color", color);
            return material;
        }

        public static Material CreateVegetation(Material source)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            Material material = Create("WW2/Spherical Terrain Preview/Vegetation", source.name + " — spherical preview");
            material.CopyPropertiesFromMaterial(source);
            material.enableInstancing = true;
            // The isolated shader always exposes the trees, independently of
            // any loaded flat map visibility texture or selection globals.
            material.shaderKeywords = Array.Empty<string>();
            material.renderQueue = (int)RenderQueue.AlphaTest;
            return material;
        }

        public static void SetSphereFrame(Material material, Vector3 center, float radius)
        {
            if (!material) return;
            if (material.HasProperty("_SphereCenter")) material.SetVector("_SphereCenter", center);
            if (material.HasProperty("_SphereRadius")) material.SetFloat("_SphereRadius", radius);
        }

        /// <summary>Shares the existing offline Blue Marble / ETOPO inputs without copying or changing their importers.</summary>
        public static void ConfigureSatellite(Material material, Texture2D color, Texture2D relief)
        {
            if (!material || !material.HasProperty("_UseSatellite")) return;
            material.SetFloat("_UseSatellite", color && relief ? 1 : 0);
            if (color) material.SetTexture("_SatelliteColor", color);
            if (relief) material.SetTexture("_SatelliteRelief", relief);
        }

        public static void SetPresentationAltitude(Material material, float altitude)
        {
            if (!material || !material.HasProperty("_SatelliteBlend")) return;
            material.SetFloat("_SatelliteBlend", Mathf.SmoothStep(0, 1, Mathf.InverseLerp(220, 650, altitude)));
        }

        /// <summary>
        /// Ready chunk coverage in the current region's tangent coordinates.
        /// A null texture restores the original circular patch mask.
        /// </summary>
        public static void SetDetailCoverage(Material material, Texture coverage)
        {
            if (!material || !material.HasProperty("_UseDetailCoverage")) return;
            material.SetFloat("_UseDetailCoverage", coverage ? 1 : 0);
            if (coverage) material.SetTexture("_DetailCoverage", coverage);
        }

        public static void SetDetailCoverage(Material material, Texture coverage, Vector3 focus,
            Vector3 east, Vector3 north, float worldSize, Texture oceanOwners = null)
        {
            SetDetailCoverage(material, coverage);
            if (!material || !material.HasProperty("_CoverageFocus")) return;
            material.SetVector("_CoverageFocus", focus);
            material.SetVector("_CoverageEast", east);
            material.SetVector("_CoverageNorth", north);
            material.SetFloat("_CoverageWorldSize", Mathf.Max(worldSize, 1));
            // Only the ocean shader opts into ownership. Terrain, river and
            // shoreline-strip coverage keep their existing max-union behavior.
            if (material.HasProperty("_UseDetailOwner"))
            {
                material.SetFloat("_UseDetailOwner", coverage && oceanOwners ? 1 : 0);
                if (oceanOwners) material.SetTexture("_DetailOwner", oceanOwners);
            }
        }

        /// <summary>Copy colors without applying the shared style's global setters.</summary>
        public static void ConfigureCoast(Material material, HexTerrainStyle style)
        {
            if (!material || !style) return;
            SetColor(material, "_DrySand", style.drySand);
            SetColor(material, "_WetSand", style.wetSand);
            SetColor(material, "_RiverBank", style.riverBank);
            SetColor(material, "_RiverWater", style.riverWater);
            SetColor(material, "_ShallowWater", style.shallowWater);
            SetColor(material, "_DeepWater", style.deepOcean);
            SetColor(material, "_FoamColor", style.shoreFoam);
            if (material.HasProperty("_RiverCarve"))
                material.SetVector("_RiverCarve", new Vector4(style.hfRiverCarve.x * 10f, style.hfRiverCarve.y * 10f, 0, 0));
            if (material.HasProperty("_RiverMotion"))
                material.SetVector("_RiverMotion", new Vector4(style.riverFlowSpeed, style.riverWaveFrequency, style.riverWaveStrength, style.riverSmoothness));
        }

        /// <summary>
        /// sourceWater is the existing Water.mat; sourceRiver is Hex Relief.mat,
        /// which carries the actual river resources. These are read only.
        /// </summary>
        public static void CopyWaterResources(Material target, Material sourceWater, Material sourceRiver = null)
        {
            if (!target) return;
            string[] waterTextures = { "_Civ6WaterDeep0", "_Civ6WaterDeep1", "_Civ6WaterCoast0", "_Civ6WaterCoast1" };
            bool complete = true;
            foreach (string property in waterTextures) complete &= CopyTexture(target, sourceWater, property);
            target.SetFloat("_UseWaterWaves", complete ? 1 : 0);
            string[] waterParameters = { "_Civ6WaterWorldScale", "_Civ6WaterScrollSpeed", "_Civ6WaterDeepStrength",
                "_Civ6WaterCoastStrength", "_Civ6WaterSpecularExponent", "_Civ6WaterF0", "_Civ6WaterSunStrength",
                "_Civ6WaterSkyStrength", "_Civ6WaterDeepDarkening", "_Civ6WaterShallowDarkening", "_Civ6WaterHeightTone",
                "_Civ6WaterShelfWidth", "_Civ6WaterShelfStrength", "_Civ6WaterFoamStrength", "_Civ6WaterFoamWidth",
                "_Civ6WaterFoamSpeed", "_Civ6WaterClarity", "_Civ6WaterBedRelief", "_Civ6WaterBedDetail" };
            foreach (string property in waterParameters) CopyFloat(target, sourceWater, property);
            bool river = CopyTexture(target, sourceRiver, "_Civ6RiverWaveMoments");
            river &= CopyTexture(target, sourceRiver, "_Civ6RiverDensity");
            river &= CopyTexture(target, sourceRiver, "_Civ6RiverScatter");
            CopyTexture(target, sourceRiver, "_Civ6RiverBankAlbedo");
            target.SetFloat("_UseRiverWaves", river ? 1 : 0);
            string[] riverParameters = { "_Civ6RiverWorldScale", "_Civ6RiverScrollSpeed", "_Civ6RiverBumpStrength",
                "_Civ6RiverOpticalDepth", "_Civ6RiverDensityRange", "_Civ6RiverDensityStrength", "_Civ6RiverScatterTint",
                "_Civ6RiverWaterDarkening", "_Civ6RiverF0", "_Civ6RiverSpecularExponent", "_Civ6RiverSunStrength", "_Civ6RiverSkyStrength",
                "_Civ6RiverBankTextureStrength" };
            foreach (string property in riverParameters) CopyFloat(target, sourceRiver, property);
        }

        /// <summary>The flat near terrain uses the same authored River_B texture
        /// as the river optics; copy its resources into the land pass too.</summary>
        public static void CopyRiverBankResources(Material target, Material sourceRiver)
        {
            CopyTexture(target, sourceRiver, "_Civ6RiverBankAlbedo");
            CopyFloat(target, sourceRiver, "_Civ6RiverBankTextureStrength");
            CopyFloat(target, sourceRiver, "_Civ6RiverWorldScale");
        }

        static bool CopyTexture(Material target, Material source, string property)
        {
            if (!source || !source.HasProperty(property) || !target.HasProperty(property)) return false;
            Texture texture = source.GetTexture(property);
            if (!texture) return false;
            target.SetTexture(property, texture);
            return true;
        }

        static void CopyFloat(Material target, Material source, string property)
        {
            if (source && source.HasProperty(property) && target.HasProperty(property)) target.SetFloat(property, source.GetFloat(property));
        }

        static void SetColor(Material material, string property, Color color)
        {
            // These colors originate from HexTerrainStyle/HexNearTerrainProfile
            // Shader.SetGlobalColor calls. Global colors are raw float values.
            // Their shader Properties must ALSO be Vector, because a Color
            // property performs sRGB conversion when uploaded to the shader,
            // even if GetVector returns the expected stored value. SetVector
            // plus Vector metadata preserves the flat shader's exact contract.
            if (material.HasProperty(property)) material.SetVector(property, new Vector4(color.r, color.g, color.b, color.a));
        }
    }
}
