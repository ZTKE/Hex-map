using UnityEngine;

static class HexOverviewAtmosphereDefaults
{
	public static void Apply(Material material)
	{
		if (!material)
		{
			return;
		}

		material.SetFloat("_Strength", 0.74f);
		material.SetFloat("_BreathExposure", 0.085f);
		material.SetFloat("_Translucency", 0.09f);
		material.SetFloat("_Warmth", 0.30f);
		material.SetFloat("_SoftBloom", 0.10f);
		material.SetFloat("_Vignette", 0.14f);
		material.SetFloat("_Grain", 0.018f);
		material.SetFloat("_PaperFrameStrength", 0.17f);
		material.SetFloat("_PeriodGradeStrength", 0.34f);
	}
}
