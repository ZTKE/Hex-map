#include "../HexCellData.hlsl"

#include "../Hex Civilization Style.hlsl"

float4 _HexBiomePlantTint[5];
float _HexVegetationStyleBlend;

void GetVertexCellData_float(
	float3 WorldPosition,
	bool EditMode,
	out float2 Visibility)
{
	HexGridData hgd = GetHexGridData(WorldPosition.xz);
	float4 cellData = GetCellData(hgd.cellOffsetCoordinates, EditMode);

	Visibility.x = cellData.x;
	Visibility.x = lerp(0.25, 1, Visibility.x);
	Visibility.y = cellData.y;
}

void GetFragmentData_float(
	UnityTexture2D BaseTexture,
	float2 UV,
	float3 WorldPosition,
	float3 Color,
	float2 Visibility,
	out float3 BaseColor,
	out float Exploration)
{
	float3 c = BaseTexture.Sample(BaseTexture.samplerstate, UV).rgb * Color;
	// The green-dominant feature material is foliage. Tint only that material;
	// farms and urban props keep their authored colors.
	float isFoliage = step(Color.r * 1.8, Color.g) *
		(1.0 - step(0.68, Color.r));
	bool editMode = false;
	#ifdef _HEX_MAP_EDIT_MODE
		editMode = true;
	#endif
	HexGridData hgd = GetHexGridData(WorldPosition.xz);
	float4 cellData = GetCellData(hgd.cellOffsetCoordinates, editMode);
	int terrainIndex = clamp((int)floor(cellData.w * 255.0 + 0.5), 0, 4);
	float3 foliageTint = _HexBiomePlantTint[terrainIndex].rgb;
	c = lerp(c, c * foliageTint * 1.72,
		isFoliage * saturate(_HexVegetationStyleBlend));
	c = HexCivGrade(c, WorldPosition, 0.62, lerp(0.64, 0.9, isFoliage));
	BaseColor = c.rgb * Visibility.x;
	Exploration = Visibility.y;
}
