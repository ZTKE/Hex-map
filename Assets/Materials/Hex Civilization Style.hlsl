#ifndef HEX_CIVILIZATION_STYLE_INCLUDED
#define HEX_CIVILIZATION_STYLE_INCLUDED

float _HexCivStyleStrength;
float _HexCivSaturation;
float _HexCivContrast;
float _HexCivPosterization;
float _HexCivBrushStrength;
float _HexCivBrushScale;
float4 _HexCivWarmLightTint;
float4 _HexCivCoolShadowTint;

float HexCivHash21(float2 p)
{
	p = frac(p * float2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return frac(p.x * p.y);
}

float HexCivValueNoise(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	return lerp(
		lerp(HexCivHash21(i), HexCivHash21(i + float2(1, 0)), f.x),
		lerp(HexCivHash21(i + float2(0, 1)), HexCivHash21(i + 1), f.x),
		f.y);
}

float3 HexCivGrade(
	float3 sourceColor,
	float3 worldPosition,
	float lightAmount,
	float materialResponse)
{
	float3 color = max(sourceColor, 0.0);
	float luminance = dot(color, float3(0.299, 0.587, 0.114));
	color = lerp(float3(luminance, luminance, luminance), color, _HexCivSaturation);
	color = (color - 0.5) * _HexCivContrast + 0.5;

	float lightBand = smoothstep(0.16, 0.86, lightAmount);
	float3 temperature = lerp(
		_HexCivCoolShadowTint.rgb,
		_HexCivWarmLightTint.rgb,
		lightBand);
	color *= lerp(float3(1.0, 1.0, 1.0), temperature,
		0.24 * materialResponse);

	float2 brushUV = worldPosition.xz * _HexCivBrushScale;
	float brushNoise = HexCivValueNoise(brushUV);
	float diagonalStroke = sin(
		dot(worldPosition.xz, float2(0.73, 0.41)) *
		(_HexCivBrushScale * 11.0) + brushNoise * 5.0);
	float brush = (brushNoise - 0.5) * 1.25 + diagonalStroke * 0.18;
	color *= 1.0 + brush * _HexCivBrushStrength * materialResponse;

	// A small amount of broad tonal quantization produces readable painted
	// planes without turning the terrain into a cel-shaded cartoon.
	float3 banded = floor(saturate(color) * 9.0 + 0.5) / 9.0;
	color = lerp(color, banded,
		saturate(_HexCivPosterization * materialResponse));

	float distanceFade = smoothstep(
		180.0, 680.0, distance(_WorldSpaceCameraPos.xz, worldPosition.xz));
	float distantLuminance = dot(color, float3(0.299, 0.587, 0.114));
	float3 distantColor = lerp(
		float3(distantLuminance, distantLuminance, distantLuminance),
		color, 0.86) * _HexCivCoolShadowTint.rgb;
	color = lerp(color, distantColor, distanceFade * 0.16);

	return lerp(sourceColor, saturate(color), saturate(_HexCivStyleStrength));
}

#endif
