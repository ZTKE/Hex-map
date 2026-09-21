#ifndef HEX_NEAR_DESERT_DUNES_INCLUDED
#define HEX_NEAR_DESERT_DUNES_INCLUDED
// CPU mirror: HexNearDesertDunes.cs. The SDK exposes DuneDesertHills as a
// separate procedural layer; these reconstruction equations are our own.
float4 _HexNearDunes; // ordinary height, hill height, wavelength in radii, irregularity
float4 _HexNearDuneWind; // global unit wind direction in XZ

float NearDuneCycle(float2 position,float2 frequency,float wrapWidth)
{
	if(wrapWidth>0.0)frequency.x=floor(frequency.x*wrapWidth+.5)/wrapWidth;
	return frac(dot(position,frequency));
}
float NearDuneWave(float2 position,float2 frequency,float wrapWidth,float phase)
{
	return sin((NearDuneCycle(position,frequency,wrapWidth)+phase)*6.283185307179586);
}
float NearDuneRidge(float cycle,float crest)
{
	float t=frac(cycle);
	float slope=t<crest?t/crest:(1.0-t)/(1.0-crest);
	return pow(saturate(slope),1.35);
}
float NearDuneField(float2 position,float wrapWidth,float wavelength,float irregularity,float2 wind)
{
	wavelength=clamp(wavelength,.6,3.0);
	irregularity=saturate(irregularity);
	if(wrapWidth>0.0)position.x-=floor(position.x/wrapWidth)*wrapWidth;
	float bendA=NearDuneWave(position,float2(.12,.13),wrapWidth,.17);
	float bendB=NearDuneWave(position,float2(-.06,.25),wrapWidth,.43);
	float bendC=NearDuneWave(position,float2(.25,-.12),wrapWidth,.71);
	float meander=irregularity*(.90*bendA+.38*bendB+.14*bendC);
	float crest=.74+.055*irregularity*NearDuneWave(position,float2(.043,.066),wrapWidth,.29);
	float main=NearDuneRidge(NearDuneCycle(position,wind/wavelength,wrapWidth)+meander,crest);
	float modulation=.80+.20*irregularity*NearDuneWave(position,float2(.067,-.11),wrapWidth,.13);
	float2 secondaryWind=float2(wind.x*.94-wind.y*.342,wind.x*.342+wind.y*.94);
	float subsidiary=NearDuneRidge(NearDuneCycle(position,secondaryWind*(1.73/wavelength),wrapWidth)
		+meander*.45+.23*irregularity*bendB+.37,.70);
	return main*modulation+subsidiary*.13*(1.0-main);
}
float NearEvaluateDunes(float2 root,float2 local)
{
	float2 position=float2((root.x+fmod(root.y,2.0)*.5)*1.7320508075688772,root.y*1.5)+local;
	float wrapWidth=_HexTerrainShapeWrap>.5?_HexCellData_TexelSize.z*1.7320508075688772:0.0;
	return NearDuneField(position,wrapWidth,_HexNearDunes.z,_HexNearDunes.w,_HexNearDuneWind.xy);
}
#endif
