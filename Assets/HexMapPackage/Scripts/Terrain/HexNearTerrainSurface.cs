using UnityEngine;

/// <summary>CPU mirror of HexNearTerrainShape.hlsl for roads, picking and units.</summary>
public static class HexNearTerrainSurface
{
	const float Sqrt3 = 1.7320508075688772f;
	static readonly Vector2[] Centers = {
		new(Sqrt3*.5f, 1.5f), new(Sqrt3, 0), new(Sqrt3*.5f, -1.5f),
		new(-Sqrt3*.5f, -1.5f), new(-Sqrt3, 0), new(-Sqrt3*.5f, 1.5f)
	};
	public static uint Hash(int x, int z)
	{
		unchecked { uint h=(uint)x*73856093u ^ (uint)z*19349663u;
			h ^= h>>13; h*=1274126177u; return h ^ (h>>16); }
	}
	public static float Evaluate(HexGrid grid, HexNearTerrainProfile profile,
		int x, int z, Vector2 p, float sea, float river, float oldY, bool carveRiver)
	{
		if(sea>=.70f)return oldY;
		float mountains=0, hills=0, weight=0, ranges=0, plateau=0, plateauWeight=0;
		Vector2 mountainBase=Vector2.zero,duneCoverage=Vector2.zero;
		Accumulate(grid,profile,x,z,p,ref mountains,ref hills,ref weight,ref ranges,ref plateau,ref plateauWeight,ref mountainBase,ref duneCoverage);
		for(int i=0;i<6;i++)
		{
			Vector2Int n=Neighbor(x,z,i);
			Accumulate(grid,profile,n.x,n.y,p-Centers[i],ref mountains,ref hills,ref weight,ref ranges,ref plateau,ref plateauWeight,ref mountainBase,ref duneCoverage);
			Vector2Int c=Neighbor(n.x,n.y,(i+1)%6);
			Accumulate(grid,profile,c.x,c.y,p-Centers[i]-Centers[(i+1)%6],ref mountains,ref hills,ref weight,ref ranges,ref plateau,ref plateauWeight,ref mountainBase,ref duneCoverage);
		}
		float plateauY=profile.plateauHeight*plateau/Mathf.Max(.0001f,plateauWeight);
		// The same broad base supports both open upland and its mountain ridges.
		// Taking Max(mountain, plateau) clips away the foot of the mountain and
		// makes highland summits look planted on an unrelated flat sheet.
		// A normalized shared lower mass fills the space between authored
		// mountains. Smooth union preserves summits instead of lifting them all.
		float lowerMass=mountainBase.x/Mathf.Max(.0001f,mountainBase.y);
		float duneHeight=0f;
		if(duneCoverage.x>0f)
		{
			float apron=1f-Smooth(.35f,2.2f,Mathf.Max(Mathf.Max(mountains,ranges),lowerMass));
			float dry=1f-Smooth(.02f,.32f,sea);
			float bank=carveRiver?Smooth(.17f,.40f,river):1f;
			duneHeight=HexNearDesertDunes.Evaluate(grid,profile,x,z,p)*
				duneCoverage.x/Mathf.Max(.0001f,duneCoverage.y)*apron*dry*bank;
		}
		float surface=.16f+plateauY+MergeHeights(MergeHeights(
			MergeRange(mountains,ranges),lowerMass,profile.mountainFootBlend),
			hills/Mathf.Max(1f,weight)+duneHeight,profile.mountainFootBlend);
		float land=1f-Smooth(.08f,.70f,sea);
		// Highlands have their own continuous base. Cutting every river to the
		// ocean datum would split a broad plateau with an artificial deep trench.
		if(carveRiver) surface=Mathf.Lerp(surface,plateauY-.32f,(1f-Smooth(.045f,.17f,river))*land);
		float datum=HexMetrics.visualWaterLevel*HexMetrics.elevationStep;
		return Mathf.Lerp(oldY,datum+surface+.018f,land);
	}
	static void Accumulate(HexGrid grid,HexNearTerrainProfile profile,int x,int z,
		Vector2 p,ref float mountains,ref float hills,ref float weight,ref float ranges,
		ref float plateau,ref float plateauWeight,ref Vector2 mountainBase,ref Vector2 duneCoverage)
	{
		float radius=p.magnitude;
		if(radius>=2.4f)return;
		if(!Resolve(grid,ref x,z))return;
		HexCellData cell=grid.CellData[x+z*grid.CellCountX];
		if(cell.IsUnderwater)return;
		float duneW=1f-Smooth(.2f,1.8f,radius);
		duneCoverage.y+=duneW;
		if(cell.TerrainTypeIndex==0&&(cell.landform==HexLandform.Flat||cell.landform==HexLandform.Hill))
			duneCoverage.x+=duneW*(cell.landform==HexLandform.Hill?profile.desertHillDuneHeight:profile.desertDuneHeight);
		uint hash=Hash(x,z);
		float angle01=Mathf.Repeat(cell.TerrainRotation/6f+.5f,1f);
		float angle=Mathf.Clamp(Mathf.RoundToInt(angle01*63f),0,63)/63f*(Mathf.PI*2f)-Mathf.PI;
		float s=Mathf.Sin(angle),c=Mathf.Cos(angle);
		Vector2 rotated=new(p.x*c+p.y*s,-p.x*s+p.y*c);
		// Normalize against every dry neighbor, including flat ground and peaks.
		// A plateau interior is one level, without a mound at every hex centre;
		// only the perimeter slopes down. The support fits the shared neighborhood.
		float plateauW=1f-Smooth(.2f,Mathf.Min(2.4f,Mathf.Max(1.8f,profile.plateauFootprint)),radius);
		plateauWeight+=plateauW;
		if(cell.landform==HexLandform.Plateau)plateau+=plateauW;
		float baseW=1f-Smooth(.2f,Mathf.Clamp(profile.mountainBaseFootprint,1.8f,2.4f),radius);
		mountainBase.y+=baseW;
		if(cell.landform==HexLandform.Mountain)
		{
			int neighborMask=MountainNeighborMask(grid,x,z);
			// Full range flanks have their own support, independent of the
			// narrower summit stamps (particularly the desert pieces).
			if(profile.mountainRangeStrength>0f && neighborMask!=0)
				ranges=Mathf.Max(ranges,EvaluateRange(grid,profile,x,z,cell,neighborMask,p));
			bool desert=cell.TerrainTypeIndex==0;
			Vector2 scale=MountainScale(profile,hash,neighborMask);
			float footprint=(desert?profile.desertMountainFootprint:profile.mountainFootprint)*scale.y;
			float height=(desert?profile.desertMountainHeight:profile.mountainHeight)*scale.x;
			float baseFootprint=Mathf.Min(footprint*(1f+.5f*profile.ridgeWidth),1.85f);
			if(profile.mountainBaseHeight>0f&&baseW>0f)
			{
				// HBLEND is the SDK's alpha-only terrain-element blend texture,
				// imported into G. Its desert masks are particularly compact. A
				// broad shared floor keeps those masks from becoming separate rocks;
				// their authored contours modulate the lower slopes above that floor.
				float broadFootprint=Mathf.Min((desert?profile.desertMountainFootprint:profile.mountainFootprint)*(1f+.5f*profile.ridgeWidth),1.85f);
				Color lower=profile.SampleShape(ShapeLayer(cell,x,z),rotated/(2f*broadFootprint)+Vector2.one*.5f);
				float contour=.55f+.45f*Mathf.Max(lower.g,Mathf.Sqrt(Mathf.Clamp01(lower.r)));
				mountainBase.x+=baseW*(desert?profile.desertMountainHeight:profile.mountainHeight)*
					Mathf.Clamp(profile.mountainBaseHeight,0f,.6f)*contour;
			}
			float reach=Mathf.Max(footprint,baseFootprint)*1.18f;
			if(radius>=reach)return;
			Color sample=profile.SampleShape(desert?6+(int)(hash%4u):(int)(hash%5u),rotated/(2f*footprint)+Vector2.one*.5f);
			float fade=1f-Smooth(footprint*.78f,footprint*1.18f,radius);
			float peak=sample.r*height*fade;
			if(profile.ridgeStrength>0f)
			{
				float baseHeight=profile.SampleShape(desert?6+(int)(hash%4u):(int)(hash%5u),rotated/(2f*baseFootprint)+Vector2.one*.5f).r;
				float baseFade=1f-Smooth(baseFootprint*.70f,baseFootprint*1.18f,radius);
				peak=Mathf.Max(peak,baseHeight*height*profile.ridgeStrength*baseFade);
			}
			mountains=Mathf.Max(mountains,peak);
		}
		else
		{
			float w=1f-Smooth(.35f,1.45f,radius);if(w<=0)return;
			Color sample=profile.SampleShape(5,rotated/(2f*profile.hillFootprint)+Vector2.one*.5f);
			float h=Mathf.Max(0f,sample.r-16f/255f);
			float strength=cell.landform==HexLandform.Plateau?profile.plateauRelief:
				cell.landform==HexLandform.Hill?profile.hillHeight:.18f;
			if(cell.TerrainTypeIndex==0&&cell.landform!=HexLandform.Plateau)strength*=.22f;
			hills+=h*strength*w;weight+=w;
		}
	}
	static float MergeRange(float mountain,float ridge)
	{
		// Fade the smooth-union bandwidth with both contributors. Exactly zero
		// ridge leaves isolated peaks and all non-mountain terrain unchanged.
		return MergeHeights(mountain,ridge,.36f);
	}
	static float MergeHeights(float a,float b,float bandwidth)
	{
		float k=Mathf.Min(bandwidth,2f*Mathf.Min(a,b));
		if(k<=.000001f)return Mathf.Max(a,b);
		float h=Mathf.Max(0f,1f-Mathf.Abs(a-b)/k);
		return Mathf.Max(a,b)+k*h*h*.25f;
	}
	static int MountainNeighborMask(HexGrid grid,int x,int z)
	{
		int mask=0;
		for(int d=0;d<6;d++)
		{
			Vector2Int n=Neighbor(x,z,d);int nx=n.x;
			// Match the encoded shape mask exactly; a submerged endpoint is
			// filtered separately when a connection is evaluated.
			if(Resolve(grid,ref nx,n.y)&&grid.CellData[nx+n.y*grid.CellCountX].landform==HexLandform.Mountain)
				mask|=1<<d;
		}
		return mask;
	}
	static int CountBits(int mask)
	{int count=0;for(int d=0;d<6;d++)count+=(mask>>d)&1;return count;}
	static Vector2 MountainScale(HexNearTerrainProfile profile,uint hash,int neighborMask)
	{
		float dense=Mathf.Clamp01((CountBits(neighborMask)-2)/3f)*profile.mountainMassVariation;
		float peak=(hash&255u)/255f,footprint=((hash>>8)&255u)/255f;
		// Vary the summit, not its shared lower mass. All source influence
		// still fits the same thirteen-cell neighborhood, including world wrap.
		return new Vector2(Mathf.Lerp(1f,.76f+.38f*peak,dense),Mathf.Lerp(1f,.80f+.20f*footprint,dense));
	}
	static int ShapeLayer(HexCellData cell,int x,int z) => cell.TerrainTypeIndex==0 ?
		6+(int)(Hash(x,z)%4u):(int)(Hash(x,z)%5u);
	static float Angle(HexCellData cell)
	{
		float angle01=Mathf.Repeat(cell.TerrainRotation/6f+.5f,1f);
		return Mathf.Clamp(Mathf.RoundToInt(angle01*63f),0,63)/63f*(Mathf.PI*2f)-Mathf.PI;
	}
	static Vector2 PeakPosition(HexNearTerrainProfile profile,HexCellData cell,int x,int z,int neighborMask,out float height)
	{
		Vector4 peak=profile.GetMountainPeak(ShapeLayer(cell,x,z));
		Vector2 scale=MountainScale(profile,Hash(x,z),neighborMask);
		float footprint=(cell.TerrainTypeIndex==0?profile.desertMountainFootprint:profile.mountainFootprint)*scale.y;
		height=peak.z*(cell.TerrainTypeIndex==0?profile.desertMountainHeight:profile.mountainHeight)*scale.x;
		float angle=Angle(cell),s=Mathf.Sin(angle),c=Mathf.Cos(angle);
		// Inverse of the world-to-stamp rotation used by Accumulate.
		return new Vector2(peak.x*c-peak.y*s,peak.x*s+peak.y*c)*footprint;
	}
	static float EvaluateRange(HexGrid grid,HexNearTerrainProfile profile,int x,int z,HexCellData cell,int neighborMask,Vector2 p)
	{
		float result=0f;
		int ridgeMask=grid.ShaderData!=null?grid.ShaderData.GetMountainRidgeMask(x+z*grid.CellCountX):
			HexMountainRidgeGraph.ComputeMask(grid,x,z);
		// Same canonical whole-body sweep and endpoint window as the GPU.
		float sourceFade=1f-Smooth(1.95f,2.4f,p.magnitude);
		for(int d=0;d<6;d++)
		{
			if((ridgeMask&(1<<d))==0)continue;
			Vector2Int n=Neighbor(x,z,d);int nx=n.x;
			if(!Resolve(grid,ref nx,n.y))continue;
			HexCellData other=grid.CellData[nx+n.y*grid.CellCountX];
			if(other.IsUnderwater||other.landform!=HexLandform.Mountain)continue;
			int otherMask=MountainNeighborMask(grid,nx,n.y);
			float chainA=cell.mountainMode==HexMountainMode.Range ||
				(cell.mountainMode==HexMountainMode.Automatic&&CountBits(neighborMask)<=2)?1f:0f;
			float chainB=other.mountainMode==HexMountainMode.Range ||
				(other.mountainMode==HexMountainMode.Automatic&&CountBits(otherMask)<=2)?1f:0f;
			float chain=Mathf.Max(chainA,chainB);
			// Only dense-to-dense edges compete for a principal direction. Do
			// not break an authored bend, a two-cell pair, or the end of a range.
			float directional=1f;
			if(chain<.5f&&CountBits(neighborMask)>2&&CountBits(otherMask)>2)
			{
				Vector2 axisA=new(Mathf.Cos(Angle(cell)),Mathf.Sin(Angle(cell)));
				Vector2 axisB=new(Mathf.Cos(Angle(other)),Mathf.Sin(Angle(other)));
				float alignment=Mathf.Max(Mathf.Abs(Vector2.Dot(axisA,Centers[d]/Sqrt3)),Mathf.Abs(Vector2.Dot(axisB,Centers[d]/Sqrt3)));
				// Directionality lowers secondary crests; it never erases flanks.
				directional=Mathf.Lerp(1f,Mathf.Lerp(.86f,1f,Smooth(.5f,.86f,alignment)),profile.mountainRangeDirectionality);
			}
			Vector2 delta=Centers[d],point=p;
			HexCellData a=cell,b=other;int ax=x,az=z,bx=nx,bz=n.y;
			int maskA=neighborMask,maskB=otherMask;
			if(x+z*grid.CellCountX>nx+n.y*grid.CellCountX)
			{
				a=other;b=cell;ax=nx;az=n.y;bx=x;bz=z;
				maskA=otherMask;maskB=neighborMask;
				point-=delta;delta=-delta;
			}
			Vector2 start=PeakPosition(profile,a,ax,az,maskA,out float heightA);
			Vector2 end=delta+PeakPosition(profile,b,bx,bz,maskB,out float heightB);
			Vector2 segment=end-start;
			float length2=Mathf.Max(segment.sqrMagnitude,.01f);
			float t=Mathf.Clamp01(Vector2.Dot(point-start,segment)/length2);
			float bell=4f*t*(1f-t);
			uint pair;
			unchecked { pair=Hash(ax,az)^(Hash(bx,bz)*1664525u+1013904223u); }
			float variation=(pair&255u)/255f;
			Vector2 side=new(-segment.y,segment.x);side/=Mathf.Sqrt(length2);
			Vector2 curve=start+segment*t+side*((variation-.5f)*.28f*bell);
			float phase=variation*Mathf.PI*2f;
			float width=Mathf.Max(.01f,profile.mountainRangeWidth*Mathf.Lerp(1f,profile.mountainChainWidth,chain)*(.94f+.06f*bell)*(.96f+.08f*variation)*
				(1f+.12f*bell*Mathf.Sin(t*Mathf.PI*4f+phase)));
			// The sweep forms the entire mountain side, not a thin bridge under
			// detached peaks. Authored HM summits are united with this body later.
			Vector2 q=point-curve;
			float cross=Mathf.Pow(Mathf.Clamp01(1f-q.magnitude/width),1.15f);
			if(cross<=0f)continue;
			// Range paints a high continuous spine; Massif leaves lower saddles.
			float saddlePosition=.32f+.36f*((pair>>8)&255u)/255f;
			float saddleRatio=Mathf.Lerp(profile.mountainMassifSaddle,profile.mountainChainSaddle,chain);
			float saddleHeight=Mathf.Min(heightA,heightB)*(saddleRatio+Mathf.Lerp(.10f,.04f,chain)*(((pair>>16)&255u)/255f-.5f));
			float descent=t<saddlePosition?t/saddlePosition:(1f-t)/(1f-saddlePosition);
			float saddle=Mathf.Lerp(Mathf.Lerp(.64f,1f,chain)*Mathf.Lerp(heightA,heightB,t),saddleHeight,
				descent*descent*(3f-2f*descent));
			float ridge=saddle*profile.mountainRangeStrength*directional*cross*sourceFade;
			float amplitude=Mathf.Clamp(profile.mountainSlopeDetail,0f,.35f);
			if(ridge*(1f+amplitude)<=result)continue;
			if(amplitude>0f)
			{
				float detailA=RangeSlopeSample(profile,a,ax,az,(q+segment*(t*.45f))/width);
				float detailB=RangeSlopeSample(profile,b,bx,bz,(q+segment*((t-1f)*.45f))/width);
				ridge*=1f+amplitude*4f*cross*(1f-cross)*Mathf.Lerp(detailA,detailB,t);
			}
			result=Mathf.Max(result,ridge);
		}
		return result;
	}
	static float RangeSlopeSample(HexNearTerrainProfile profile,HexCellData cell,int x,int z,Vector2 detailPoint)
	{
		int layer=ShapeLayer(cell,x,z);
		Vector4 peak=profile.GetMountainPeak(layer);
		float angle=Angle(cell),s=Mathf.Sin(angle),c=Mathf.Cos(angle);
		Vector2 rotated=new(detailPoint.x*c+detailPoint.y*s,-detailPoint.x*s+detailPoint.y*c);
		Vector2 uv=(new Vector2(peak.x,peak.y)+rotated*(cell.TerrainTypeIndex==0?.46f:.70f))*.5f+Vector2.one*.5f;
		Color shape=profile.SampleShape(layer,uv);
		return Mathf.Clamp((shape.r/Mathf.Max(peak.z,.05f)-.55f*Mathf.Sqrt(Mathf.Clamp01(shape.g)))*3f,-1f,1f);
	}
	static bool Resolve(HexGrid g,ref int x,int z)
	{
		if(z<0||z>=g.CellCountZ)return false;
		if(g.Wrapping)x=((x%g.CellCountX)+g.CellCountX)%g.CellCountX;
		return x>=0&&x<g.CellCountX;
	}
	static Vector2Int Neighbor(int x,int z,int d)
	{
		int odd=z&1;return d switch {
			0=>new(x+odd,z+1),1=>new(x+1,z),2=>new(x+odd,z-1),
			3=>new(x+odd-1,z-1),4=>new(x-1,z),_=>new(x+odd-1,z+1)};
	}
	static float Smooth(float a,float b,float x)
	{float t=Mathf.Clamp01((x-a)/(b-a));return t*t*(3f-2f*t);}
}
