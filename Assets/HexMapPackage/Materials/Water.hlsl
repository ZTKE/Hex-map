float Foam(float shore, float2 worldXZ, float time, UnityTexture2D noiseTex)
{
	// Thin contour foam only.
	shore = saturate(shore);
	shore = shore * shore;

	float2 noiseUV = worldXZ + _Time.y * 0.18;
	float4 noise = noiseTex.Sample(
		noiseTex.samplerstate, noiseUV * (2.4 * TILING_SCALE));

	float distortion1 = noise.x * (1 - shore) * 0.45;
	float foam1 = sin((shore + distortion1) * 14 - _Time.y * 0.85);
	foam1 *= foam1;

	float distortion2 = noise.y * (1 - shore) * 0.35;
	float foam2 = sin((shore + distortion2) * 16 + _Time.y * 0.7 + 2.1);
	foam2 *= foam2 * 0.55;

	return max(foam1, foam2) * shore;
}

float River(float2 riverUV, float time, UnityTexture2D noiseTex)
{
	float2 uv = riverUV;
	uv.x = uv.x * 0.0625 + _Time.y * 0.005;
	uv.y -= _Time.y * 0.25;
	float4 noise = noiseTex.Sample(noiseTex.samplerstate, uv);

	float2 uv2 = riverUV;
	uv2.x = uv2.x * 0.0625 - _Time.y * 0.0052;
	uv2.y -= _Time.y * 0.23;
	float4 noise2 = noiseTex.Sample(noiseTex.samplerstate, uv2);

	return noise.r * noise2.w;
}

float Waves(float2 worldXZ, float time, UnityTexture2D noiseTex)
{
	float2 uv1 = worldXZ;
	uv1.y += time * 0.55;
	float4 noise1 = noiseTex.Sample(
		noiseTex.samplerstate, uv1 * (3.6 * TILING_SCALE));

	float2 uv2 = worldXZ;
	uv2.x += time * 0.48;
	float4 noise2 = noiseTex.Sample(
		noiseTex.samplerstate, uv2 * (4.4 * TILING_SCALE));

	float blendWave = sin(
		(worldXZ.x + worldXZ.y) * 0.12 +
		(noise1.y + noise2.z) + time * 0.7);
	blendWave *= blendWave;

	float waves =
		lerp(noise1.z, noise1.w, blendWave) +
		lerp(noise2.x, noise2.y, blendWave);
	return smoothstep(0.55, 1.70, waves);
}

float OceanGlitter(float2 worldXZ, float time, UnityTexture2D noiseTex)
{
	float2 uv = worldXZ * (5.4 * TILING_SCALE) + float2(time * 0.10, -time * 0.06);
	float4 noise = noiseTex.Sample(noiseTex.samplerstate, uv);
	float spark = smoothstep(0.62, 0.90, noise.r * 0.5 + noise.b * 0.5);
	spark *= smoothstep(0.48, 0.85, noise.g);
	float2 uv2 = worldXZ * (9.5 * TILING_SCALE) + float2(-time * 0.07, time * 0.05);
	float4 noise2 = noiseTex.Sample(noiseTex.samplerstate, uv2);
	float spark2 = smoothstep(0.76, 0.95, noise2.a);
	return saturate(spark * spark * 0.85 + spark2 * 0.55);
}

// Soft flowing currents for near water — same family as Global Ocean mid-near.
void NearOceanFlow(
	UnityTexture2D noiseTex,
	float2 worldXZ,
	float time,
	out float basinTone,
	out float macroTone,
	out float fineTone,
	out float current)
{
	float2 uvA = worldXZ * (0.011 * TILING_SCALE) + float2(time * 0.0042, -time * 0.0020);
	float2 uvB = worldXZ * (0.019 * TILING_SCALE) + float2(-time * 0.0030, time * 0.0016);
	float2 uvC = worldXZ * (0.0065 * TILING_SCALE) + float2(time * 0.0007, time * 0.0003);
	float2 uvR = worldXZ * (0.045 * TILING_SCALE) + float2(time * 0.009, -time * 0.006);
	float4 noiseA = noiseTex.Sample(noiseTex.samplerstate, uvA);
	float4 noiseB = noiseTex.Sample(noiseTex.samplerstate, uvB);
	float4 noiseC = noiseTex.Sample(noiseTex.samplerstate, uvC);
	float4 noiseR = noiseTex.Sample(noiseTex.samplerstate, uvR);
	basinTone = noiseC.b;
	macroTone = saturate(noiseA.b * 0.56 + noiseB.r * 0.44);
	fineTone = saturate(noiseA.g * 0.40 + noiseB.b * 0.35 + noiseR.g * 0.25);
	float difference = 1.0 - abs(noiseA.r - noiseB.g);
	current = smoothstep(0.74, 0.95, difference) *
		smoothstep(0.32, 0.78, noiseC.r);
}
