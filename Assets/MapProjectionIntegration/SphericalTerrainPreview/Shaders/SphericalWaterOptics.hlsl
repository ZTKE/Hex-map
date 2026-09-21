#ifndef SPHERICAL_WATER_OPTICS_INCLUDED
#define SPHERICAL_WATER_OPTICS_INCLUDED

// HexWaterSurface's continuous shoals, Beer-Lambert transmission and metric
// shore breakers in a planet-space frame. Logical topology comes from the same
// C2 dry-area kernel on the real spherical cells, not a flat six-direction grid.
struct SphericalWaterBed
{
    float depth, coastWeight, sand, ripple;
    float3 normal;
};

float BedHash(float3 p)
{
    uint h = (uint)(int)p.x * 0x8da6b343u ^ (uint)(int)p.y * 0xd8163841u ^ (uint)(int)p.z * 0xcb1ab31fu;
    h ^= h >> 16u; h *= 0x7feb352du; h ^= h >> 15u;
    return (h & 0x00ffffffu) * (1.0 / 16777215.0);
}
float BedNoise(float3 p, float frequency)
{
    p *= frequency;
    float3 i = floor(p), f = frac(p);
    f = f * f * f * (f * (f * 6 - 15) + 10);
    return lerp(lerp(lerp(BedHash(i), BedHash(i + float3(1,0,0)), f.x),
        lerp(BedHash(i + float3(0,1,0)), BedHash(i + float3(1,1,0)), f.x), f.y),
        lerp(lerp(BedHash(i + float3(0,0,1)), BedHash(i + float3(1,0,1)), f.x),
        lerp(BedHash(i + float3(0,1,1)), BedHash(i + float3(1,1,1)), f.x), f.y), f.z);
}
float3 WaterSurfaceGradient(float3 position, float3 normal, float value)
{
    float3 px = ddx(position), py = ddy(position);
    float3 bx = cross(py, normal), by = cross(normal, px);
    float determinant = dot(px, bx);
    float divisor = (determinant < 0 ? -1 : 1) * max(abs(determinant), .000001);
    return (ddx(value) * bx + ddy(value) * by) / divisor;
}
SphericalWaterBed EvaluateSphericalBed(float3 position, float3 radial, float landDensity,
    float actualDepth, float seaInfluence)
{
    float3 p = position - _SphereCenter.xyz;
    float offshore = max(-log2(max(landDensity * 2, .0001)), 0);
    float basin = BedNoise(p, .024);
    float banks = BedNoise(p + float3(17.1,0,-9.3), .061);
    float grain = BedNoise(p, .17);
    float channelPhase = (dot(p, float3(1,.35,.61)) * .038 + basin * .85) * (2 * PI);
    float channel = smoothstep(.50,.93,.5 + .5 * cos(channelPhase)) * (1 - smoothstep(1,3,fwidth(channelPhase)));
    float shoal = smoothstep(.38,.78,banks) * (.55 + basin * .45);
    float slopePower = lerp(1.28,1.04,saturate((_Civ6WaterShelfWidth - .12) / .33));
    float slopeDepth = .22 + 5.4 * pow(offshore,slopePower);
    float relief = ((basin-.5)*1.8 + (grain-.5)*.24 + channel*1.65 - shoal*1.1) *
        max(_Civ6WaterBedRelief,0) * smoothstep(.03,.32,offshore);
    float landscapeDepth = 1.10 + max(slopeDepth + relief - .22,0);
    float bankBlend = (1-smoothstep(.03,.22,actualDepth)) * (1-smoothstep(.62,.90,seaInfluence));
    SphericalWaterBed bed;
    bed.depth = max(lerp(landscapeDepth,actualDepth,bankBlend),.025);
    bed.coastWeight = (1-smoothstep(2.4,10,bed.depth)) * saturate(_Civ6WaterShelfStrength);
    float ripplePhase = (dot(p,float3(1,.25,.42)) * .24 + banks*1.2) * (2*PI);
    bed.ripple = sin(ripplePhase) * (1-smoothstep(.6,2.8,fwidth(ripplePhase)));
    bed.sand = saturate(.55 + (grain-.5)*.45 + (banks-.5)*.25);
    float bedHeight = -relief + bed.ripple*.045*_Civ6WaterBedDetail;
    bed.normal = normalize(radial - WaterSurfaceGradient(position,radial,bedHeight));
    return bed;
}
float3 ShadeSphericalBed(SphericalWaterBed bed, float3 view, float3 normal,
    float waveHeight, Light sun, float3 biome)
{
    float3 deep = lerp(float3(.010,.037,.095), _NearDeepWater.rgb * float3(.86,.88,.98), .90) * _Civ6WaterDeepDarkening;
    float3 shallow = lerp(float3(.025,.18,.25), _NearShallowWater.rgb * float3(.36,.55,.60), .50) * _Civ6WaterShallowDarkening;
    float3 scattering = lerp(shallow,deep,smoothstep(1.5,12,bed.depth));
    float opticalDepth = bed.depth * (.38 + rcp(max(saturate(dot(normal,view)),.35))) / max(_Civ6WaterClarity,.25);
    float3 transmission = exp(-float3(.75,.19,.11)*opticalDepth) * saturate(_Civ6WaterShelfStrength);
    float bedLighting = .62 + .38*saturate(dot(bed.normal,sun.direction));
    float3 sand = lerp(float3(.29,.245,.23),float3(.43,.35,.295),bed.sand);
    // The same dry-cell climate weights reach the submerged shelf. Wet soil
    // continues below grass/plains coasts, while desert keeps warm pale sand.
    sand = lerp(sand,float3(.24,.245,.18)*lerp(.82,1.14,bed.sand),saturate(biome.y)*.82);
    sand = lerp(sand,float3(.46,.37,.245)*lerp(.85,1.12,bed.sand),saturate(biome.x));
    sand = lerp(sand,float3(.34,.36,.37)*lerp(.85,1.1,bed.sand),saturate(biome.z));
    sand *= bedLighting * (1+bed.ripple*.055*_Civ6WaterBedDetail);
    float caustic = smoothstep(.59,.73,waveHeight)*.10*bed.coastWeight;
    float3 water = sand*transmission*(1+caustic) + scattering*(1-transmission);
    return water*(1+(waveHeight-.5)*_Civ6WaterHeightTone);
}
float SphericalShoreFoam(float3 position, float3 radial, float signedDepth,
    float2 coastSlope, float coverage, float height)
{
    // Depth is piecewise linear on the water mesh. Dividing it by ddx/ddy(depth)
    // gave each triangle a different metric and tore crests into bright wedges.
    // CPU-welded bed normals supply a continuous interpolated slope instead.
    // The coarse satellite shell has no bed normals and uses a bounded fallback.
    float slope = coastSlope.y > .5 ? max(coastSlope.x,.08) : .22;
    float depth = max(signedDepth,0);
    float width = max(_Civ6WaterFoamWidth,.1);
    float shoreDistance = depth / slope;
    float distance01 = shoreDistance / width;
    float pixelWorld = max(length(ddx(position)),length(ddy(position)));
    float distanceAA = max(fwidth(distance01),.006);
    float resolved = 1-smoothstep(.28,1.15,pixelWorld/width);
    float band = (1-smoothstep(.60,1.70,distance01))*(1-smoothstep(.85,1.65,depth));
    [branch] if (band < .001 || resolved < .001) return 0;

    // Planet-space noise has neither a longitude seam nor a polar projection.
    // Broad patches control WHERE water breaks: do not keep a nonzero white
    // baseline everywhere along an otherwise perfectly parallel contour.
    float3 p = radial * _SphereRadius;
    float time = _Time.y * _Civ6WaterFoamSpeed;
    float macro = BedNoise(p + float3(17.7,41.3,-8.1),.16);
    float patches = BedNoise(p + float3(time*.31,-time*.19,time*.23),.78);
    float3 bubblePosition = p + float3(-time*.72,time*.38,time*.51);
    // Value-noise contours alone reveal round squares on their regular lattice.
    // Bend only the fine bubble chart by about half a lattice cell; the broad
    // breaking patches, wave timing and opacity distribution remain unchanged.
    float3 bubbleWarp = float3(
        BedNoise(bubblePosition + float3(31.7,-12.4,8.9),.96),
        BedNoise(bubblePosition + float3(-17.3,43.8,21.6),.96),
        BedNoise(bubblePosition + float3(8.1,19.7,-36.2),.96)) - .5;
    float bubbles = BedNoise(bubblePosition + bubbleWarp*.30,3.4);
    float fineResolved = 1-smoothstep(.09,.42,pixelWorld);
    float bubbleEdges = 1-smoothstep(.045,.19,abs(bubbles-.49));
    // Filter the opacity, not the noise input: mean noise .5 would turn every
    // unresolved bubble edge solid white instead of preserving its low coverage.
    bubbleEdges = lerp(.22,bubbleEdges,fineResolved);
    float clumps = smoothstep(.32,.70,patches + (macro-.5)*.24);
    float holes = lerp(.42,smoothstep(.30,.67,bubbles),fineResolved);

    // Each patch has one fading pulse travelling from sea toward the beach.
    // A periodic distance sine creates two rigid white ropes; here the front
    // disappears before its phase wraps, and no permanent outline is added.
    float cycle = frac(time*.72 + macro*.58);
    float life = smoothstep(0,.12,cycle)*(1-smoothstep(.74,1,cycle));
    float frontDistance = (1-cycle)*1.35 + (patches-.5)*.34 + (height-.5)*.10;
    float delta = distance01-frontDistance;
    float spread = .22 + patches*.19 + min(distanceAA,.24);
    float front = exp2(-delta*delta/max(spread*spread,.001)*2.4);
    float trailing = exp2(-max(delta,0)*3.8)*smoothstep(-.05,.26,delta);
    float froth = front*(.13+holes*.32+bubbleEdges*.29);
    float backwash = trailing*bubbleEdges*.18;
    float swash = exp2(-distance01*distance01*15)*clumps*holes*.12;
    return saturate(((froth+backwash)*clumps*life+swash)*band*coverage*resolved*_Civ6WaterFoamStrength);
}
#endif
