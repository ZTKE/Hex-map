using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        ComputeBuffer gameEdges, gameState, gameBuildSelection;
        Texture gameSeeds, gameField, gamePalette;
        Vector4 gameGrid, gameSelection, gameStrength, gamePoliticalStyle;
        bool gameBound;
        float naturalPoliticalOpacity = .27f;
        // X: inward color-band opacity, Y: width in saved field texels,
        // Z: outline width in screen pixels, W: strategic palette emphasis.
        public static Vector4 PoliticalStyleAtAltitude(float height)
        {
            float middle = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(70, 650, height));
            float globeFade = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(850, 4200, height));
            return new Vector4(Mathf.Lerp(.56f, .76f, middle) * globeFade,
                Mathf.Lerp(.8f, 10f, middle), Mathf.Lerp(.85f, 1.2f, middle), middle * globeFade);
        }
        public void SetGameplayPolitics(ComputeBuffer edges, ComputeBuffer state, Texture seeds,
            Texture field, Texture palette, Vector4 grid, Vector4 selection, float height, bool overlay, bool occupation)
        {
            if (!coarseMaterial || !detailMaterial || !waterMaterial || !fineWaterMaterial || !riverMaterial) return;
            Vector4 style = PoliticalStyleAtAltitude(height);
            selection.w = naturalPoliticalOpacity * 1.85f * style.w;
            float globeFade = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(850, 4200, height));
            var strength = new Vector4(Mathf.Lerp(.92f, .80f, style.w) * globeFade, overlay ? 1 : 0, occupation ? 1 : 0,
                // A 4K distance field magnifies its zero-texel band in middle
                // views. Keep exact native edge planes until cells are small;
                // only the far view hands off to the filtered natural field.
                Mathf.SmoothStep(0, 1, Mathf.InverseLerp(1800, 3000, height)));
            bool resources = !gameBound || gameEdges != edges || gameState != state ||
                gameSeeds != seeds || gameField != field || gamePalette != palette;
            bool parameters = !gameBound || !gameGrid.Equals(grid) || !gameSelection.Equals(selection) || !gameStrength.Equals(strength) || !gamePoliticalStyle.Equals(style);
            if (!resources && !parameters) return;
            ApplyNativePolitics(coarseMaterial, edges, state, seeds, field, palette, grid, selection, strength, resources, parameters);
            ApplyNativePolitics(detailMaterial, edges, state, seeds, field, palette, grid, selection, strength, resources, parameters);
            ApplyNativePolitics(waterMaterial, edges, state, seeds, field, palette, grid, selection, strength, resources, parameters);
            ApplyNativePolitics(fineWaterMaterial, edges, state, seeds, field, palette, grid, selection, strength, resources, parameters);
            ApplyNativePolitics(riverMaterial, edges, state, seeds, field, palette, grid, selection, strength, resources, parameters);
            gameEdges = edges; gameState = state; gameSeeds = seeds; gameField = field; gamePalette = palette;
            if (parameters)
            {
                coarseMaterial.SetVector("_GameplayPoliticalStyle", style);
                detailMaterial.SetVector("_GameplayPoliticalStyle", style);
                waterMaterial.SetVector("_GameplayPoliticalStyle", style);
                fineWaterMaterial.SetVector("_GameplayPoliticalStyle", style);
                riverMaterial.SetVector("_GameplayPoliticalStyle", style);
            }
            gameGrid = grid; gameSelection = selection; gameStrength = strength; gamePoliticalStyle = style; gameBound = true;
        }

        static void ApplyNativePolitics(Material material, ComputeBuffer edges, ComputeBuffer state, Texture seeds,
            Texture field, Texture palette, Vector4 grid, Vector4 selection, Vector4 strength, bool resources, bool parameters)
        {
            if (!material) return;
            if (resources)
            {
                material.SetBuffer("_GameplayNativeEdges", edges); material.SetBuffer("_GameplayNativeState", state);
                material.SetTexture("_GameplayNativeLookup", seeds); material.SetTexture("_GameplayCountries", field);
                material.SetTexture("_GameplayPalette", palette); material.SetFloat("_UseGameplayPolitics", 1);
            }
            if (parameters)
            {
                material.SetVector("_GameplayGrid", grid); material.SetVector("_GameplaySelection", selection);
                material.SetVector("_GameplayPoliticalStrength", strength);
            }
        }
        public void SetNativeSelectionPlanes(Vector4[] planes)
        {
            SetSelectionPlanes(coarseMaterial, planes); SetSelectionPlanes(detailMaterial, planes);
            SetSelectionPlanes(waterMaterial, planes); SetSelectionPlanes(fineWaterMaterial, planes);
            SetSelectionPlanes(riverMaterial, planes);
        }
        static void SetSelectionPlanes(Material material, Vector4[] planes)
        { if (material) material.SetVectorArray("_NativeSelectionPlanes", planes); }

        public void SetGameplayBuildSelection(ComputeBuffer buildSelection)
        {
            if (!coarseMaterial || !detailMaterial || !waterMaterial || !fineWaterMaterial || !riverMaterial)
            {
                return;
            }

            if (gameBuildSelection == buildSelection)
            {
                return;
            }

            coarseMaterial.SetBuffer("_GameplayBuildSelection", buildSelection);
            detailMaterial.SetBuffer("_GameplayBuildSelection", buildSelection);
            waterMaterial.SetBuffer("_GameplayBuildSelection", buildSelection);
            fineWaterMaterial.SetBuffer("_GameplayBuildSelection", buildSelection);
            riverMaterial.SetBuffer("_GameplayBuildSelection", buildSelection);
            gameBuildSelection = buildSelection;
        }

        public void ClearGameplayPolitics()
        {
            if (coarseMaterial) coarseMaterial.SetFloat("_UseGameplayPolitics", 0);
            if (detailMaterial) detailMaterial.SetFloat("_UseGameplayPolitics", 0);
            if (waterMaterial) waterMaterial.SetFloat("_UseGameplayPolitics", 0);
            if (fineWaterMaterial) fineWaterMaterial.SetFloat("_UseGameplayPolitics", 0);
            if (riverMaterial) riverMaterial.SetFloat("_UseGameplayPolitics", 0);
            gameBound = false; gameEdges = gameState = gameBuildSelection = null; gameSeeds = gameField = gamePalette = null;
        }
    }
}
