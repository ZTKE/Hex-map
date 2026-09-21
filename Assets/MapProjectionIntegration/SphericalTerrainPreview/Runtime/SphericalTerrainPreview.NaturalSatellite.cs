using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        bool naturalSatelliteEnabled, naturalSatelliteApplied;
        Color naturalSurfaceTint, naturalOceanTint, naturalBorderTint, naturalAtmosphereTint;
        Vector4 naturalSurfaceParameters;
        /// <summary>Copies only the natural satellite art values. Geometry and
        /// near material recipes remain the native Spherical Terrain Preview.</summary>
        public void ConfigureNaturalSatellite(Color surfaceTint, Color oceanTint, Color borderTint,
            Color atmosphereTint, float saturation, float contrast, float normalStrength,
            float lightStrength, float politicalOpacity, float atmosphereStrength)
        {
            naturalSatelliteEnabled = true; naturalSatelliteApplied = false;
            naturalSurfaceTint = surfaceTint; naturalOceanTint = oceanTint;
            naturalBorderTint = borderTint; naturalAtmosphereTint = atmosphereTint;
            naturalSurfaceParameters = new Vector4(saturation, contrast, normalStrength, lightStrength);
            naturalPoliticalOpacity = politicalOpacity; farAtmosphereStrength = atmosphereStrength;
            // A_NaturalSatellite is the requested source of truth. Previous
            // far-only extra saturation/brightness must not compound its grade.
            farLandSaturation = farOceanSaturation = farAtmosphereSaturation = 1;
            farGradeCached = farLightCached = false;
            ApplyNaturalSatelliteBindings();
        }
        void ApplyNaturalSatelliteBindings()
        {
            if (!naturalSatelliteEnabled || naturalSatelliteApplied || !coarseMaterial || !farAtmosphereMaterial) return;
            var oceanRelief = Resources.Load<Texture2D>("SphericalTerrainPreview/EarthOceanRelief");
            foreach (Material material in presentationMaterials)
            {
                if (!material || !material.HasProperty("_UseNaturalSatellite")) continue;
                material.SetFloat("_UseNaturalSatellite", 1);
                if (oceanRelief && material.HasProperty("_OceanRelief")) material.SetTexture("_OceanRelief", oceanRelief);
                material.SetColor("_NaturalSurfaceTint", naturalSurfaceTint);
                material.SetColor("_NaturalOceanTint", naturalOceanTint);
                material.SetColor("_NaturalBorderTint", naturalBorderTint);
                material.SetColor("_NaturalAtmosphereTint", naturalAtmosphereTint);
                material.SetVector("_NaturalSurfaceParameters", naturalSurfaceParameters);
            }
            farAtmosphereMaterial.SetColor("_NaturalAtmosphereTint", naturalAtmosphereTint);
            farAtmosphereMaterial.SetFloat("_UseNaturalSatellite", 1);
            naturalSatelliteApplied = true;
        }
    }
}
