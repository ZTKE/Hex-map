#ifndef HEX_NEAR_TERRAIN_SHAPE_INCLUDED
#define HEX_NEAR_TERRAIN_SHAPE_INCLUDED
#include "HexNearDesertDunes.hlsl"
TEXTURE2D_ARRAY(_HexNearShapes);
TEXTURE2D_ARRAY(_HexNearAlbedos);
TEXTURE2D(_HexMountainRidgeData);
SAMPLER(sampler_HexNearAlbedos);
float _HexNearTerrainEnabled;
float4 _HexNearHeights; // mountain height, hill height, mountain footprint, hill footprint
float4 _HexNearPlateau; // continuous plateau height, low rolling relief, dry-grass weathering, base footprint
float4 _HexNearDesert; // desert mountain height and footprint
float4 _HexNearTessellation;
float4 _HexNearDetails; // ridge width, ridge strength, material tiling, normal strength
float4 _HexNearRange; // complete range-body strength, flank width, smooth union, crest directionality
float4 _HexNearMountainForm; // dense-group scale variation, foothill blend, HM slope relief
float4 _HexNearMountainBase; // shared lower mass fraction, compact footprint
float4 _HexNearMountainModes; // chain saddle, massif saddle, chain width scale
float4 _HexNearPeaks[10]; // normalized authored peak XY and sampled height

uint NearHash(float2 cell)
{
	uint h=(uint)cell.x*73856093u ^ (uint)cell.y*19349663u;
	h ^= h>>13u; h*=1274126177u; return h ^ (h>>16u);
}
float2 NearRotate(float2 p,float angle)
{
	float s,c;
	sincos(angle,s,c);return float2(p.x*c+p.y*s,-p.x*s+p.y*c);
}
float NearMergeHeights(float a,float b,float bandwidth)
{
	float k=min(bandwidth,2.0*min(a,b));
	if(k<=.000001)return max(a,b);
	float h=max(0.0,1.0-abs(a-b)/k);
	return max(a,b)+k*h*h*.25;
}
float NearMergeRange(float mountain,float ridge)
{
	return NearMergeHeights(mountain,ridge,.36);
}
float2 NearMountainScale(HFCellShape cell)
{
	uint hash=NearHash(cell.offset);
	float dense=saturate((HFCountBits(cell.neighborMask)-2)/3.0)*_HexNearMountainForm.x;
	float peak=(float)(hash&255u)/255.0,footprint=(float)((hash>>8u)&255u)/255.0;
	// Vary authored summits; the shared lower mass keeps its full footprint.
	return float2(lerp(1.0,.76+.38*peak,dense),lerp(1.0,.80+.20*footprint,dense));
}
float2 NearPeakPosition(HFCellShape cell,out float height)
{
	uint hash=NearHash(cell.offset);
	bool desert=cell.terrain<.5;
	int layer=desert?6+(int)(hash%4u):(int)(hash%5u);
	float4 peak=_HexNearPeaks[layer];
	float2 scale=NearMountainScale(cell);
	height=peak.z*(desert?_HexNearDesert.x:_HexNearHeights.x)*scale.x;
	return NearRotate(peak.xy,-cell.angle)*(desert?_HexNearDesert.y:_HexNearHeights.z)*scale.y;
}
float NearSummitSnowRetention(HFCellShape cell,float2 p)
{
	if(_HexNearRange.x<=0.0||cell.neighborMask==0u)return 1.0;
	// Snow caps follow authored summits rather than the height of a connector.
	// Retain only a trace on the saddle, with a soft transition to each cap.
	float authoredPeakHeight;
	float2 peak=NearPeakPosition(cell,authoredPeakHeight);
	float peakCap=1.0-smoothstep(.20,.65,length(p-peak));
	return lerp(.06,1.0,peakCap);
}
float NearRangeSlopeSample(HFCellShape cell,float2 detailPoint)
{
	bool desert=cell.terrain<.5;
	int layer=desert?6+(int)(NearHash(cell.offset)%4u):(int)(NearHash(cell.offset)%5u);
	float4 peak=_HexNearPeaks[layer];
	// Expand the useful central portion of the authored mountain across a
	// complete flank. Desert TerrainElements occupy less of their source image.
	float2 uv=(peak.xy+NearRotate(detailPoint,cell.angle)*(desert?.46:.70))*.5+.5;
	float4 shape=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,uv,layer,0);
	// Remove a smooth coverage envelope before using the HM as slope relief.
	// G is the actual HBLEND; this is geometric detail, not a material normal.
	return clamp((shape.r/max(peak.z,.05)-.55*sqrt(saturate(shape.g)))*3.0,-1.0,1.0);
}
float NearEvaluateRange(HFCellShape cell,float2 p)
{
	float result=0.0;
	uint ridgeMask=(uint)round(SAMPLE_TEXTURE2D_LOD(_HexMountainRidgeData,HF_TERRAIN_POINT_SAMPLER,
		(cell.offset+.5)*_HexCellData_TexelSize.xy,0).r*255.0);
	// Every retained ridge edge owns a complete crest AND two broad flanks.
	// Endpoint windows make that body compact without truncating it at a hex
	// edge. Both endpoints emit the same canonical body; their max is seamless.
	float sourceFade=1.0-smoothstep(1.95,2.4,length(p));
	[loop]for(int d=0;d<6;d++)
	{
		[branch]if((ridgeMask&(1u<<d))==0u)continue;
		HFCellShape other=HFLoadCell(HFNeighborOffset(cell.offset,d));
		if(other.valid<.5||other.underwater>.5||other.landform<1.5||other.landform>2.5)continue;
		uint otherPacked=(uint)round(SAMPLE_TEXTURE2D_LOD(_HexMountainRidgeData,HF_TERRAIN_POINT_SAMPLER,
			(other.offset+.5)*_HexCellData_TexelSize.xy,0).r*255.0);
		uint modeA=ridgeMask>>6u,modeB=otherPacked>>6u;
		float chainA=(modeA==1u||(modeA==0u&&HFCountBits(cell.neighborMask)<=2))?1.0:0.0;
		float chainB=(modeB==1u||(modeB==0u&&HFCountBits(other.neighborMask)<=2))?1.0:0.0;
		float chain=max(chainA,chainB);
		float directional=1.0;
		if(chain<.5&&HFCountBits(cell.neighborMask)>2&&HFCountBits(other.neighborMask)>2)
		{
			float2 axisA=float2(cos(cell.angle),sin(cell.angle));
			float2 axisB=float2(cos(other.angle),sin(other.angle));
			float2 edge=HFNeighborCenter(d)/1.7320508075688772;
			float alignment=max(abs(dot(axisA,edge)),abs(dot(axisB,edge)));
			// Dense ranges may have a lower secondary crest, never missing mass.
			directional=lerp(1.0,lerp(.86,1.0,smoothstep(.5,.86,alignment)),_HexNearRange.w);
		}
		float2 delta=HFNeighborCenter(d),samplePoint=p;
		HFCellShape a=cell,b=other;
		// Canonical ordering gives both endpoints exactly the same curved edge,
		// including the east/west wrap. Geometry still uses local short deltas.
		if(cell.offset.x+cell.offset.y*_HexCellData_TexelSize.z>
			other.offset.x+other.offset.y*_HexCellData_TexelSize.z)
		{
			a=other;b=cell;samplePoint-=delta;delta=-delta;
		}
		float heightA,heightB;
		float2 start=NearPeakPosition(a,heightA),end=delta+NearPeakPosition(b,heightB);
		float2 segment=end-start;
		float length2=max(dot(segment,segment),.01);
		float t=saturate(dot(samplePoint-start,segment)/length2);
		float bell=4.0*t*(1.0-t);
		uint pair=NearHash(a.offset)^(NearHash(b.offset)*1664525u+1013904223u);
		float variation=(float)(pair&255u)/255.0;
		float2 side=float2(-segment.y,segment.x)/sqrt(length2);
		float2 curve=start+segment*t+side*((variation-.5)*.28*bell);
		float phase=variation*PI*2.0;
		// Width belongs to the whole sloping mountain body, independently of
		// the much narrower authored summit stamp. Low-frequency buttresses
		// vary the flank silhouette. Range connects near summit height; Massif
		// keeps a lower body so individual peaks stand above internal valleys.
		float width=max(.01,_HexNearRange.y*lerp(1.0,_HexNearMountainModes.z,chain)*(.94+.06*bell)*(.96+.08*variation)*
			(1.0+.12*bell*sin(t*PI*4.0+phase)));
		float2 q=samplePoint-curve;
		float crossSection=pow(saturate(1.0-length(q)/width),1.15);
		[branch]if(crossSection<=0.0)continue;
		float saddlePosition=.32+.36*(float)((pair>>8u)&255u)/255.0;
		float saddleRatio=lerp(_HexNearMountainModes.y,_HexNearMountainModes.x,chain);
		float saddleHeight=min(heightA,heightB)*(saddleRatio+lerp(.10,.04,chain)*((float)((pair>>16u)&255u)/255.0-.5));
		float descent=t<saddlePosition?t/saddlePosition:(1.0-t)/(1.0-saddlePosition);
		float saddle=lerp(lerp(.64,1.0,chain)*lerp(heightA,heightB,t),saddleHeight,
			descent*descent*(3.0-2.0*descent));
		float candidate=saddle*_HexNearRange.x*directional*crossSection*sourceFade;
		// Positive detail is bounded; lower candidates can skip both art reads.
		[branch]if(candidate*(1.0+_HexNearMountainForm.z)<=result)continue;
		[branch]if(_HexNearMountainForm.z>0.0)
		{
			// Carry longitudinal coordinates as well as cross-slope distance:
			// extruding a single HM row would only make parallel grooves.
			float detailA=NearRangeSlopeSample(a,(q+segment*(t*.45))/width);
			float detailB=NearRangeSlopeSample(b,(q+segment*((t-1.0)*.45))/width);
			float detail=lerp(detailA,detailB,t);
			candidate*=1.0+_HexNearMountainForm.z*4.0*crossSection*(1.0-crossSection)*detail;
		}
		result=max(result,candidate);
	}
	return result;
}
void NearAccumulate(float2 offset,float2 p,inout float mountain,inout float hills,inout float weight,inout float ranges,
	inout float plateau,inout float plateauWeight,inout float2 mountainBase,inout float2 duneCoverage)
{
	float radius=length(p);
	// All supported profile footprints are zero beyond this point. Cull
	// corner sources before reading their shape and cell-data textures.
	[branch]if(radius>=2.4)return;
	HFCellShape cell=HFLoadCell(offset);
	if(cell.valid<.5||cell.underwater>.5)return;
	float duneW=1.0-smoothstep(.2,1.8,radius);
	duneCoverage.y+=duneW;
	if(cell.terrain<.5&&cell.landform<1.5)
		duneCoverage.x+=duneW*(cell.landform>.5?_HexNearDunes.y:_HexNearDunes.x);
	uint hash=NearHash(cell.offset);
	float2 rotated=NearRotate(p,cell.angle);
	// All dry cells take part in the denominator, yielding a broad constant
	// interior and a smooth perimeter instead of independent hex-shaped mesas.
	float plateauW=1.0-smoothstep(.2,_HexNearPlateau.w,radius);
	plateauWeight+=plateauW;
	if(cell.landform>2.5)plateau+=plateauW;
	float baseW=1.0-smoothstep(.2,_HexNearMountainBase.y,radius);
	mountainBase.y+=baseW;
	if(cell.landform>1.5&&cell.landform<2.5)
	{
		[branch]if(_HexNearRange.x>0.0&&cell.neighborMask!=0u)
			ranges=max(ranges,NearEvaluateRange(cell,p));
		bool desert=cell.terrain<.5;
		float2 scale=NearMountainScale(cell);
		float footprint=(desert?_HexNearDesert.y:_HexNearHeights.z)*scale.y;
		float height=(desert?_HexNearDesert.x:_HexNearHeights.x)*scale.x;
		// Isolated mountains use the authored stamp. In a connected range this
		// stamp supplies the summit and rock ribs above the shared flank body.
		float baseFootprint=min(footprint*(1.0+.5*_HexNearDetails.x),1.85);
		[branch]if(_HexNearMountainBase.x>0.0&&baseW>0.0)
		{
			// The imported SDK HBLEND is in G. Its compact desert masks shape
			// shoulders over a broad shared floor, never isolated circular bases.
			float broadFootprint=min((desert?_HexNearDesert.y:_HexNearHeights.z)*(1.0+.5*_HexNearDetails.x),1.85);
			float4 lower=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,
				rotated/(2.0*broadFootprint)+.5,desert?6.0+(float)(hash%4u):(float)(hash%5u),0);
			float contour=.55+.45*max(lower.g,sqrt(saturate(lower.r)));
			mountainBase.x+=baseW*(desert?_HexNearDesert.x:_HexNearHeights.x)*_HexNearMountainBase.x*contour;
		}
		float reach=max(footprint,baseFootprint)*1.18;
		[branch]if(radius>=reach)return;
		float4 element=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,
			rotated/(2.0*footprint)+.5,desert?6.0+(float)(hash%4u):(float)(hash%5u),0);
		float peak=element.r*height*(1.0-smoothstep(footprint*.78,footprint*1.18,radius));
		[branch]if(_HexNearDetails.y>0.0)
		{
			float baseHeight=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,
				rotated/(2.0*baseFootprint)+.5,desert?6.0+(float)(hash%4u):(float)(hash%5u),0).r;
			float baseFade=1.0-smoothstep(baseFootprint*.70,baseFootprint*1.18,radius);
			peak=max(peak,baseHeight*height*_HexNearDetails.y*baseFade);
		}
		mountain=max(mountain,peak);
	}
	else
	{
		float w=1.0-smoothstep(.35,1.45,radius);if(w<=0.0)return;
		float h=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,
			rotated/(2.0*_HexNearHeights.w)+.5,5,0).r;
		h=max(0.0,h-16.0/255.0);
		float strength=cell.landform>2.5?_HexNearPlateau.y:(cell.landform>.5?_HexNearHeights.y:.18);
		// Dunes are a shared wind field evaluated once below, not rotated hill
		// stamps. Keep only a little of the authored substrate below desert hills.
		if(cell.terrain<.5&&cell.landform<1.5)strength*=.22;
		hills+=h*strength*w;weight+=w;
	}
}

float NearEvaluateY(float cellIndex,float2 p,float sea,float river,float oldY)
{
	// land is exactly zero here; preserve the HF sea floor without evaluating
	// a second thirteen-cell height neighborhood.
	[branch]if(sea>=.70)return oldY;
	float2 root=HFCellIndexToOffset(cellIndex);float mountain=0,hills=0,weight=0,ranges=0,plateau=0,plateauWeight=0;
	float2 mountainBase=0,duneCoverage=0;
	NearAccumulate(root,p,mountain,hills,weight,ranges,plateau,plateauWeight,mountainBase,duneCoverage);
	[loop]for(int d=0;d<6;d++)
	{
		float2 n=HFNeighborOffset(root,d),nc=HFNeighborCenter(d);
		NearAccumulate(n,p-nc,mountain,hills,weight,ranges,plateau,plateauWeight,mountainBase,duneCoverage);
		int next=(d+1)%6;
		NearAccumulate(HFNeighborOffset(n,next),p-nc-HFNeighborCenter(next),mountain,hills,weight,ranges,plateau,plateauWeight,mountainBase,duneCoverage);
	}
	float plateauY=_HexNearPlateau.x*plateau/max(.0001,plateauWeight);
	float lowerMass=mountainBase.x/max(.0001,mountainBase.y);
	float duneHeight=0.0;
	[branch]if(duneCoverage.x>0.0)
	{
		float apron=1.0-smoothstep(.35,2.2,max(max(mountain,ranges),lowerMass));
		float dry=1.0-smoothstep(.02,.32,sea);
		float bank=smoothstep(.17,.40,river);
		duneHeight=NearEvaluateDunes(root,p)*duneCoverage.x/max(.0001,duneCoverage.y)*apron*dry*bank;
	}
	float surface=.16+plateauY+NearMergeHeights(NearMergeHeights(
		NearMergeRange(mountain,ranges),lowerMass,_HexNearMountainForm.y),
		hills/max(1.0,weight)+duneHeight,_HexNearMountainForm.y);
	float land=1.0-smoothstep(.08,.70,sea);
	// Match the CPU highland river bed; the shared coastal mask still returns
	// the river to the sea surface as the plateau support fades at the shore.
	surface=lerp(surface,plateauY-.32,(1.0-smoothstep(.045,.17,river))*land);
	return lerp(oldY,_HexHFOriginalDatumY+surface+.018,land);
}

// Macro biome blending uses neighboring logical cells. Fine surface texture
// stays anchored in world space, so boundaries and chunk reloads never swim.
float4 NearSampleBiome(float2 worldUV,float biome,float hill,float2 uvDx,float2 uvDy)
{
	float4 flat=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,worldUV,biome,uvDx,uvDy);
	float4 upper=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,worldUV,biome+5.0,uvDx,uvDy);
	return lerp(flat,upper,hill);
}
float3 NearEvaluateAlbedo(float cellIndex,float2 local,float3 world,float3 normal,out float materialHeight)
{
	float2 root=HFCellIndexToOffset(cellIndex),uv=world.xz*_HexNearDetails.z;
	// Compute derivatives outside the varying loop/branches. Explicit gradients
	// keep mip selection defined when neighboring pixels take different paths.
	float2 uvDx=ddx(uv),uvDy=ddy(uv);
	float4 color=0;float total=0;
	float mountainPresence=0,topMask=0,snowStamp=0,rockWeight=0,desertWeight=0,snowLine=0,peakHeight=0;
	float summitSnowPresence=0;
	float plateau=0,plateauWeight=0;
	// Use the same thirteen source cells as displacement. A corner-owned
	// shoulder must not lose its rock and highland material at a hex boundary.
	[loop]for(int i=-1;i<12;i++)
	{
		float2 off=root,p=local;
		if(i>=0)
		{
			int direction=i/2;
			off=HFNeighborOffset(root,direction);p-=HFNeighborCenter(direction);
			if((i&1)!=0){int next=(direction+1)%6;off=HFNeighborOffset(off,next);p-=HFNeighborCenter(next);}
		}
		float radius=length(p);
		[branch]if(radius>=2.4)continue;
		HFCellShape c=HFLoadCell(off);if(c.valid<.5||c.underwater>.5)continue;
		float plateauW=1.0-smoothstep(.2,_HexNearPlateau.w,radius);
		plateauWeight+=plateauW;
		if(c.landform>2.5)plateau+=plateauW;
		float w=pow(saturate(1.0-radius/1.55),2.0);
		float hill=smoothstep(.3,1.8,world.y-_HexHFOriginalDatumY)*(1.0-saturate(c.landform-1.0));
		[branch]if(w>0.0)
		{
			float4 ground=NearSampleBiome(uv,c.terrain,hill,uvDx,uvDy);
			if(c.landform>2.5&&c.terrain>.5&&c.terrain<2.5)
			{
				// Preserve authored ground texture while removing the saturated
				// lowland yellow-green from dry, exposed highland grass.
				float luminance=dot(ground.rgb,float3(.299,.587,.114));
				ground.rgb=lerp(ground.rgb,luminance*float3(1.07,.98,.85),_HexNearPlateau.z);
			}
			color+=ground*w;total+=w;
		}
		if(c.landform>1.5&&c.landform<2.5)
		{
			uint h=NearHash(c.offset);
			bool desert=c.terrain<.5;
			float2 scale=NearMountainScale(c);
			float footprint=(desert?_HexNearDesert.y:_HexNearHeights.z)*scale.y;
			float height=(desert?_HexNearDesert.x:_HexNearHeights.x)*scale.x;
			float baseFootprint=min(footprint*(1.0+.5*_HexNearDetails.x),1.85);
			float materialReach=max(footprint,baseFootprint)*1.18;
			if(_HexNearMountainBase.x>0.0)materialReach=max(materialReach,_HexNearMountainBase.y);
			if(_HexNearRange.x>0.0&&c.neighborMask!=0u)materialReach=max(materialReach,2.4);
			[branch]if(radius>=materialReach)continue;
			float4 shape=SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes,HF_TERRAIN_LINEAR_SAMPLER,
				NearRotate(p,c.angle)/(2.0*footprint)+.5,desert?6.0+(float)(h%4u):(float)(h%5u),0);
			float fade=1.0-smoothstep(footprint*.78,footprint*1.18,radius);
			float presence=max(shape.g*fade,smoothstep(.035,.35,shape.r*height*fade));
			// Match the broad lower geometry beyond the compact authored ID map.
			// Height and slope below still expose grassy, sandy or rocky shoulders.
			if(_HexNearMountainBase.x>0.0)
				presence=max(presence,(1.0-smoothstep(.2,_HexNearMountainBase.y,radius))*.7*saturate(_HexNearMountainBase.x/.34));
			// Continuous flanks extend beyond both authored colour masks. Use
			// actual height/slope to expose rock across the entire shared body.
			if(_HexNearRange.x>0.0&&c.neighborMask!=0u)
				presence=max(presence,(1.0-smoothstep(1.2,2.4,radius))*.85*saturate(_HexNearRange.x));
			mountainPresence=max(mountainPresence,presence);
			// A high connecting saddle is not an authored summit. Keep snow
			// around the actual peak anchors, exposing rock between them even
			// when the continuous range reaches the same altitude as a peak.
			summitSnowPresence=max(summitSnowPresence,presence*NearSummitSnowRetention(c,p));
			float rockW=presence*presence;
			rockWeight+=rockW;topMask+=shape.b*rockW;snowStamp+=shape.a*rockW;
			desertWeight+=(desert?1.0:0.0)*rockW;
			snowLine+=(c.terrain>3.5?.2:(c.terrain>2.5?.49:.71))*rockW;
			peakHeight+=height*rockW;
		}
	}
	[branch]if(total>.0001)color/=total;
	else color=NearSampleBiome(uv,1,0,uvDx,uvDy);
	float plateauY=_HexNearPlateau.x*plateau/max(.0001,plateauWeight);
	// Rock and snow measure relief above the regional base, not above sea
	// level. A raised grassy table must not become an exposed snowy mountain.
	float elevation=max(world.y-_HexHFOriginalDatumY-plateauY,0.0);
	float slope=1.0-saturate(normal.y);
	float face=max(smoothstep(.08,.42,slope),smoothstep(2.2,4.2,elevation));
	float exposed=smoothstep(.36,1.6,elevation)*smoothstep(.02,.5,mountainPresence)*lerp(.3,1.0,face);
	[branch]if(exposed<=0.0){materialHeight=color.a;return color.rgb;}
	float inverseWeight=1.0/max(rockWeight,.0001);
	topMask*=inverseWeight;snowStamp*=inverseWeight;desertWeight*=inverseWeight;
	snowLine*=inverseWeight;peakHeight*=inverseWeight;
	float4 baseRock=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,uv*.72,10,uvDx*.72,uvDy*.72);
	float4 topRock=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,uv*.72,11,uvDx*.72,uvDy*.72);
	float4 snow=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,uv,12,uvDx,uvDy);
	float4 rock=lerp(baseRock,topRock,topMask);
	[branch]if(desertWeight>.001)
	{
		float4 desertBase=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,uv*.72,13,uvDx*.72,uvDy*.72);
		float4 desertStripe=SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos,sampler_HexNearAlbedos,uv*.72,14,uvDx*.72,uvDy*.72);
		rock=lerp(rock,lerp(desertBase,desertStripe,smoothstep(.35,.7,frac(elevation*1.15))),desertWeight);
	}
	float relativeHeight=elevation/max(peakHeight,.1);
	float snowMask=smoothstep(snowLine,snowLine+.11,relativeHeight);
	snowMask=max(snowMask,snowStamp*mountainPresence*smoothstep(.34,.68,relativeHeight));
	snowMask*=1.0-desertWeight;
	snowMask*=smoothstep(.18,.7,normal.y);
	snowMask*=saturate(summitSnowPresence/max(mountainPresence,.0001));
	rock=lerp(rock,snow,snowMask);
	float4 result=lerp(color,rock,exposed);
	materialHeight=result.a;
	return result.rgb;
}

float3 NearDetailNormal(float3 position,float3 normal,float height)
{
	float3 dx=ddx(position),dy=ddy(position);
	float3 r1=cross(dy,normal),r2=cross(normal,dx);
	float det=dot(dx,r1);
	float3 gradient=sign(det)*(ddx(height)*r1+ddy(height)*r2);
	return normalize(max(abs(det),1e-8)*normal-gradient*_HexNearDetails.w);
}
#endif
