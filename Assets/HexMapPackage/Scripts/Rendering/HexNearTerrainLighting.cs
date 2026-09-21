using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>Detailed flat-map lighting. Runtime clones and saved light state keep the
/// existing overview/globe and authored pipeline assets independent.</summary>
public sealed class HexNearTerrainLighting : MonoBehaviour
{
	static Light[] sceneLights;
	static bool lightEventsRegistered;
	static float nextLightSearchTime;
	const float missingLightRetrySeconds = 1f;
	UniversalRenderPipelineAsset pipeline;
	RenderPipelineAsset oldGraphics, oldQuality;
	Light sun;
	LightShadows oldShadows;
	Color oldLightColor, oldAmbientSky, oldAmbientEquator, oldAmbientGround;
	Quaternion oldRotation;
	float oldIntensity, oldShadowStrength, oldBias, oldNormalBias;
	AmbientMode oldAmbientMode;
	bool active;
	float appliedShadowDistance;

	// URP 14 exposes these settings with internal setters. Overwrite only the
	// serialized settings on our clone, preserving the actual project's renderer
	// and its features (the integrated game does not use the package URP asset).
	const string ShadowSettings = "{\"m_MainLightShadowsSupported\":true," +
		"\"m_MainLightShadowmapResolution\":4096,\"m_SoftShadowsSupported\":true," +
		"\"m_Cascade4Split\":{\"x\":0.28,\"y\":0.50,\"z\":0.74}}";

	/// <summary>Resolve URP's directional light without enumerating the scene each frame.</summary>
	internal static bool TryGetActiveSun(Camera camera, out Light mainLight)
	{
		mainLight = RenderSettings.sun;
		if (UsableDirectionalLight(mainLight, camera)) return true;
		mainLight = FindBrightestDirectionalLight(GetSceneLights(), camera);
		if (!mainLight && Time.unscaledTime >= nextLightSearchTime)
		{
			// A scene may create its first light after the map. Retry that missing
			// binding at a bounded rate; populated maps reuse their light inventory.
			sceneLights = null;
			mainLight = FindBrightestDirectionalLight(GetSceneLights(), camera);
		}
		return mainLight;
	}

	static Light FindBrightestDirectionalLight(Light[] lights, Camera camera)
	{
		Light mainLight = null;
		foreach (Light candidate in lights)
			if (UsableDirectionalLight(candidate, camera) && candidate.intensity > 0f &&
				(!mainLight || candidate.intensity > mainLight.intensity)) mainLight = candidate;
		return mainLight;
	}

	static bool UsableDirectionalLight(Light light, Camera camera) =>
		light && light.isActiveAndEnabled && light.type == LightType.Directional &&
		(light.cullingMask & camera.cullingMask) != 0;

	static Light[] GetSceneLights()
	{
		if (!lightEventsRegistered)
		{
			SceneManager.sceneLoaded += OnLightSceneLoaded;
			SceneManager.sceneUnloaded += OnLightSceneUnloaded;
			lightEventsRegistered = true;
		}
		if (sceneLights == null)
		{
			// Include disabled objects so enabling an existing scene light is
			// reflected immediately, without another allocating scene-wide search.
			sceneLights = FindObjectsOfType<Light>(true);
			nextLightSearchTime = Time.unscaledTime + missingLightRetrySeconds;
		}
		return sceneLights;
	}

	static void OnLightSceneLoaded(Scene scene, LoadSceneMode mode) => sceneLights = null;
	static void OnLightSceneUnloaded(Scene scene) => sceneLights = null;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	static void ResetLightInventory()
	{
		SceneManager.sceneLoaded -= OnLightSceneLoaded;
		SceneManager.sceneUnloaded -= OnLightSceneUnloaded;
		lightEventsRegistered = false;
		sceneLights = null;
		nextLightSearchTime = 0f;
	}

	public static void Apply(Camera camera, bool enabled, float focusDistance = 0f)
	{
		if (!camera) return;
		HexNearTerrainLighting controller = camera.GetComponent<HexNearTerrainLighting>();
		if (!controller && enabled) controller = camera.gameObject.AddComponent<HexNearTerrainLighting>();
		if (!controller) return;
		controller.SetActive(enabled);
		if (enabled && controller.active)
		{
			// Showcase cameras have no HexMapCamera. Their original 850-unit
			// coverage is the fallback; map cameras supply their actual distance.
			float shadowDistance = focusDistance > 0f ?
				Mathf.Clamp(focusDistance * 1.5f + 160f, 350f, 1950f) : 850f;
			if (Mathf.Abs(shadowDistance - controller.appliedShadowDistance) > 1f)
			{
				controller.pipeline.shadowDistance = shadowDistance;
				controller.appliedShadowDistance = shadowDistance;
			}
		}
	}
	void SetActive(bool value)
	{
		if (active == value) return;
		if (!value) { Restore(); return; }
		oldGraphics = GraphicsSettings.defaultRenderPipeline;
		oldQuality = QualitySettings.renderPipeline;
		UniversalRenderPipelineAsset source = (oldQuality ? oldQuality : oldGraphics) as UniversalRenderPipelineAsset;
		if (!source) return;
		pipeline = Instantiate(source);
		pipeline.name = "Near Terrain Lighting (Runtime)";
		pipeline.hideFlags = HideFlags.DontSave;
		JsonUtility.FromJsonOverwrite(ShadowSettings, pipeline);
		pipeline.shadowDistance = 850f;
		appliedShadowDistance = 850f;
		pipeline.shadowCascadeCount = 4;
		pipeline.cascadeBorder = .16f;
		pipeline.shadowDepthBias = .6f;
		pipeline.shadowNormalBias = .35f;
		GraphicsSettings.defaultRenderPipeline = pipeline;
		QualitySettings.renderPipeline = pipeline;
		sun = RenderSettings.sun;
		if (!sun)
		{
			// Zoom can return here after runtime objects changed in overview.
			sceneLights = null;
			foreach (Light light in GetSceneLights())
				if (light && light.type == LightType.Directional && light.isActiveAndEnabled)
				{ sun = light; break; }
		}
		if (sun)
		{
			oldShadows = sun.shadows; oldLightColor = sun.color;
			oldIntensity = sun.intensity; oldRotation = sun.transform.rotation;
			oldShadowStrength = sun.shadowStrength; oldBias = sun.shadowBias; oldNormalBias = sun.shadowNormalBias;
			sun.shadows = LightShadows.Soft;
			sun.color = new Color(1f, .965f, .91f);
			sun.intensity = 1.06f;
			sun.shadowStrength = .78f; sun.shadowBias = .06f; sun.shadowNormalBias = .32f;
			sun.transform.rotation = Quaternion.Euler(48f, -125f, 0f);
		}
		oldAmbientMode = RenderSettings.ambientMode;
		oldAmbientSky = RenderSettings.ambientSkyColor;
		oldAmbientEquator = RenderSettings.ambientEquatorColor;
		oldAmbientGround = RenderSettings.ambientGroundColor;
		RenderSettings.ambientMode = AmbientMode.Trilight;
		RenderSettings.ambientSkyColor = new Color(.48f,.57f,.68f);
		RenderSettings.ambientEquatorColor = new Color(.30f,.34f,.36f);
		RenderSettings.ambientGroundColor = new Color(.18f,.17f,.15f);
		active = true;
	}
	void Restore()
	{
		if (!active) return;
		if (GraphicsSettings.defaultRenderPipeline == pipeline) GraphicsSettings.defaultRenderPipeline = oldGraphics;
		if (QualitySettings.renderPipeline == pipeline) QualitySettings.renderPipeline = oldQuality;
		if (sun)
		{
			sun.shadows=oldShadows; sun.color=oldLightColor; sun.intensity=oldIntensity;
			sun.transform.rotation=oldRotation; sun.shadowStrength=oldShadowStrength;
			sun.shadowBias=oldBias; sun.shadowNormalBias=oldNormalBias;
		}
		RenderSettings.ambientMode=oldAmbientMode;
		RenderSettings.ambientSkyColor=oldAmbientSky;
		RenderSettings.ambientEquatorColor=oldAmbientEquator;
		RenderSettings.ambientGroundColor=oldAmbientGround;
		if (pipeline) Destroy(pipeline);
		pipeline=null; active=false;
	}
	void OnDisable() => Restore();
	void OnDestroy() => Restore();
}
