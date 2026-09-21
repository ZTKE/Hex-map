using UnityEngine;

static class HexGlobeAtmosphereDefaults
{
	public static void Apply(Material material)
	{
		if (!material)
		{
			return;
		}

		material.SetFloat("_Strength", 1f);
		material.SetFloat("_SpaceStrength", 1f);
		material.SetFloat("_NebulaStrength", 0.72f);
		material.SetFloat("_StarStrength", 0.85f);
		material.SetFloat("_LimbStrength", 1f);
		material.SetFloat("_LimbWidth", 0.12f);
		material.SetFloat("_InnerVelvet", 0.62f);
		material.SetFloat("_GoldGlow", 1.05f);
		material.SetFloat("_MistStrength", 0.35f);
		material.SetFloat("_VoidWarmth", 0.18f);
		material.SetFloat("_DriftSpeed", 0.018f);
	}
}
