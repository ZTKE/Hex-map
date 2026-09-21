#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
static class HexGlobeAtmosphereInstaller
{
	const string FeatureLabel = "HexGlobeAtmosphere";

	static HexGlobeAtmosphereInstaller()
	{
		EditorApplication.delayCall += TryInstall;
	}

	static void TryInstall()
	{
		Shader shader = Shader.Find("Hex Map/Globe Atmosphere");
		if (!shader)
		{
			return;
		}

		string[] guids = AssetDatabase.FindAssets("t:UniversalRendererData");
		bool changed = false;
		foreach (string guid in guids)
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			UniversalRendererData data =
				AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
			if (!data || data.rendererFeatures.Any(
				feature => feature && feature.name == FeatureLabel))
			{
				continue;
			}

			HexGlobeAtmosphereFeature feature =
				ScriptableObject.CreateInstance<HexGlobeAtmosphereFeature>();
			feature.name = FeatureLabel;
			feature.settings.shader = shader;
			AssetDatabase.AddObjectToAsset(feature, data);
			data.rendererFeatures.Add(feature);
			EditorUtility.SetDirty(data);
			changed = true;
		}

		if (changed)
		{
			AssetDatabase.SaveAssets();
		}
	}
}
#endif
