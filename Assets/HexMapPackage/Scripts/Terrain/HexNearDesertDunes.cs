using UnityEngine;

/// <summary>
/// Continuous wind-shaped dune field in hex outer-radius coordinates.
/// This is our reconstruction, guided by the SDK's DuneDesertHills layer;
/// it is not Civilization VI engine source. Keep HexNearDesertDunes.hlsl in sync.
/// </summary>
public static class HexNearDesertDunes
{
	const float Tau = 6.283185307179586f;
	const float Sqrt3 = 1.7320508075688772f;

	public static Vector2 Position(int x, int z, Vector2 local) =>
		new Vector2((x + (z & 1) * .5f) * Sqrt3, z * 1.5f) + local;

	static float Cycle(Vector2 position, Vector2 frequency, float wrapWidth)
	{
		// Every X frequency closes over an integral number of waves. Merely
		// wrapping the position would put a cut through the dune field.
		if (wrapWidth > 0f)
			frequency.x = Mathf.Floor(frequency.x * wrapWidth + .5f) / wrapWidth;
		float value = Vector2.Dot(position, frequency);
		return value - Mathf.Floor(value);
	}

	static float Wave(Vector2 position, float x, float z, float wrapWidth, float phase = 0f) =>
		Mathf.Sin((Cycle(position, new Vector2(x, z), wrapWidth) + phase) * Tau);

	static float Ridge(float cycle, float crest)
	{
		float t = cycle - Mathf.Floor(cycle);
		// A long windward ramp and a short lee face create an actual slipface,
		// rather than a symmetric sinusoidal bump. Both meet a flat valley.
		float slope = t < crest ? t / crest : (1f - t) / (1f - crest);
		return Mathf.Pow(Mathf.Clamp01(slope), 1.35f);
	}

	public static float Evaluate(Vector2 position, float wrapWidth, float wavelength,
		float irregularity, Vector2 wind)
	{
		wavelength = Mathf.Clamp(wavelength, .6f, 3f);
		irregularity = Mathf.Clamp01(irregularity);
		if (wrapWidth > 0f)
			position.x -= Mathf.Floor(position.x / wrapWidth) * wrapWidth;
		float bendA = Wave(position, .12f, .13f, wrapWidth, .17f);
		float bendB = Wave(position, -.06f, .25f, wrapWidth, .43f);
		float bendC = Wave(position, .25f, -.12f, wrapWidth, .71f);
		float meander = irregularity * (.90f * bendA + .38f * bendB + .14f * bendC);
		float crest = .74f + .055f * irregularity * Wave(position, .043f, .066f, wrapWidth, .29f);
		float main = Ridge(Cycle(position, wind / wavelength, wrapWidth) + meander, crest);
		float modulation = .80f + .20f * irregularity * Wave(position, .067f, -.11f, wrapWidth, .13f);
		// A lower, oblique generation crosses the interdune troughs. It gives
		// short subsidiary sand ribs without turning every hex into a new dune.
		Vector2 secondaryWind = new(wind.x * .94f - wind.y * .342f,
			wind.x * .342f + wind.y * .94f);
		float subsidiary = Ridge(Cycle(position, secondaryWind * (1.73f / wavelength), wrapWidth)
			+ meander * .45f + .23f * irregularity * bendB + .37f, .70f);
		return main * modulation + subsidiary * .13f * (1f - main);
	}

	public static float Evaluate(HexGrid grid, HexNearTerrainProfile profile,
		int x, int z, Vector2 local)
	{
		float angle = profile.desertDuneWindAngle * Mathf.Deg2Rad;
		return Evaluate(Position(x, z, local), grid.Wrapping ? grid.CellCountX * Sqrt3 : 0f,
			profile.desertDuneWavelength, profile.desertDuneIrregularity,
			new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
	}
}
