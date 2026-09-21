#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
static class HexOverviewAtmosphereInstaller
{
	const string FeatureLabel = "HexOverviewAtmosphere";

	static HexOverviewAtmosphereInstaller()
	{
		EditorApplication.delayCall += TryInstall;
	}

	static void TryInstall()
	{
		Shader shader = Shader.Find("Hex Map/Overview Atmosphere");
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

			HexOverviewAtmosphereFeature feature =
				ScriptableObject.CreateInstance<HexOverviewAtmosphereFeature>();
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
