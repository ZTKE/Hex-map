using UnityEngine;

/// <summary>
/// Material-only data and Unity shading controls for the source-authored Civ6 layers.
/// Does not own cell topology, displacement, collision or vegetation placement.
/// </summary>
[CreateAssetMenu(menuName = "Hex Map/Near Land Materials")]
public sealed class HexNearLandMaterialProfile : ScriptableObject
{
    [Tooltip("18 linear RGBAHalf layers: signed dH/du,dH/dv, original gloss, original Fuzz.")]
    public Texture2DArray responseAtlas;
    [Tooltip("10 linear material-mask layers. Standard mountains: top/snow in RG. Desert mountains: three source stripe IDs in RGB, sandy foothills in A.")]
    public Texture2DArray materialMasks;
    public string[] layerNames;
    [Range(0f, 1f)] public float normalStrength = .30f;
    [Range(.01f, .5f)] public float heightBlend = .14f;
    [Range(0f, .5f)] public float macroStrength = .18f;
    [Range(.05f, .5f)] public float macroUVRatio = .19f;
    [Range(.2f, 1f)] public float roughnessMin = .38f;
    [Range(.2f, 1f)] public float roughnessMax = .96f;
    [Range(0f, .5f)] public float specularStrength = .12f;
    [TextArea] public string provenance;

    public bool IsReady => responseAtlas && responseAtlas.depth >= 18;
    public bool HasMasks => materialMasks && materialMasks.depth >= 10;

    public void ApplyGlobals(int albedoLayers)
    {
        bool enabled = IsReady && albedoLayers >= 18;
        Shader.SetGlobalFloat("_HexNearLandResponseEnabled", enabled ? 1f : 0f);
        Shader.SetGlobalFloat("_HexNearLandMasksEnabled", enabled && HasMasks ? 1f : 0f);
        if (!enabled) return;
        Shader.SetGlobalTexture("_HexNearLandResponse", responseAtlas);
        if (HasMasks) Shader.SetGlobalTexture("_HexNearLandMasks", materialMasks);
        Shader.SetGlobalVector("_HexNearLandParams", new Vector4(normalStrength, heightBlend, macroStrength, macroUVRatio));
        Shader.SetGlobalVector("_HexNearLandShading", new Vector4(Mathf.Min(roughnessMin, roughnessMax), Mathf.Max(roughnessMin, roughnessMax), specularStrength, 0f));
    }

    void OnValidate()
    {
        foreach (HexTerrainStyle style in Resources.FindObjectsOfTypeAll<HexTerrainStyle>())
            if (style.nearTerrainProfile && style.nearTerrainProfile.landMaterials == this)
            { style.nearTerrainProfile.ApplyGlobals(); break; }
    }
}
