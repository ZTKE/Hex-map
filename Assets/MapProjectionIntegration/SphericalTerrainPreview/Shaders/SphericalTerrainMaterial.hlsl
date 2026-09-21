#ifndef SPHERICAL_TERRAIN_MATERIAL_INCLUDED
#define SPHERICAL_TERRAIN_MATERIAL_INCLUDED

// Adaptation of the project's HexNearLandMaterial.hlsl. All maps and layer IDs
// retain the current near art. No HexGrid coordinates or global Y up are used.
TEXTURE2D_ARRAY(_Albedos); SAMPLER(sampler_Albedos);
TEXTURE2D_ARRAY(_Shapes);
TEXTURE2D_ARRAY(_LandResponse);
TEXTURE2D_ARRAY(_LandMasks);
TEXTURE2D(_Civ6RiverBankAlbedo);
TEXTURE2D(_CoastAlbedo);
TEXTURE2D(_CoastHeight);
TEXTURE2D(_CliffAlbedo);
TEXTURE2D(_CliffHeight);

struct LandSample
{
    float3 albedo;
    float3 gradient;
    float height;
    float gloss;
};

struct LandCoordinates
{
    float3 world;
    float3 dx;
    float3 dy;
    float3 weights;
};

LandSample EmptyLand() { return (LandSample)0; }
void AddLand(inout LandSample a, LandSample b, float w)
{
    a.albedo += b.albedo * w; a.gradient += b.gradient * w;
    a.height += b.height * w; a.gloss += b.gloss * w;
}
LandSample DivideLand(LandSample a, float w)
{
    float inv = rcp(max(w, .00001));
    a.albedo *= inv; a.gradient *= inv; a.height *= inv; a.gloss *= inv;
    return a;
}
float HeightWeight(float h) { return max(.02, saturate(h) + max(_LandParameters.y, .02)); }
LandSample BlendLand(LandSample a, LandSample b, float amount)
{
    float wa = (1 - saturate(amount)) * HeightWeight(a.height);
    float wb = saturate(amount) * HeightWeight(b.height);
    LandSample result = EmptyLand(); AddLand(result, a, wa); AddLand(result, b, wb);
    return DivideLand(result, wa + wb);
}

LandSample LandProjection(float2 uv, float2 dx, float2 dy, int layer, int axis, float scale)
{
    float4 base = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, uv, layer, dx, dy);
    LandSample result = EmptyLand(); result.albedo = base.rgb; result.height = base.a; result.gloss = .15;
    float2 slope = 0;
    [branch] if (_UseResponse > .5)
    {
        float4 response = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LandResponse, sampler_Albedos, uv, layer, dx, dy);
        slope = response.rg; result.gloss = saturate(response.b);
    }
    else
    {
        const float texel = 1.0 / 512.0;
        float hu = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, uv + float2(texel, 0), layer, dx, dy).a;
        float hv = SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, uv + float2(0, texel), layer, dx, dy).a;
        slope = (float2(hu, hv) - base.a) / texel;
    }
    result.gradient = (axis == 0 ? float3(0, slope.y, slope.x) :
        (axis == 1 ? float3(slope.x, 0, slope.y) : float3(slope.x, slope.y, 0))) * scale;
    return result;
}

LandSample ReadLand(LandCoordinates c, int layer)
{
    float scale = max(_MaterialTiling * (layer >= 10 && layer <= 16 && layer != 12 ? .72 : 1), .0001);
    float3 p = c.world * scale, dx = c.dx * scale, dy = c.dy * scale;
    LandSample result = EmptyLand();
    [branch] if (c.weights.x > .001) AddLand(result, LandProjection(p.zy, dx.zy, dy.zy, layer, 0, scale), c.weights.x);
    [branch] if (c.weights.y > .001) AddLand(result, LandProjection(p.xz, dx.xz, dy.xz, layer, 1, scale), c.weights.y);
    [branch] if (c.weights.z > .001) AddLand(result, LandProjection(p.xy, dx.xy, dy.xy, layer, 2, scale), c.weights.z);
    // A source-albedo macro sample on each projection preserves its grain and
    // hue without adding procedural replacement textures or polar UV seams.
    [branch] if (_LandParameters.z > .001)
    {
        float macroScale = scale * max(_LandParameters.w, .01);
        float3 mp = c.world * macroScale, mx = c.dx * macroScale, my = c.dy * macroScale;
        float4 macro = 0;
        [branch] if (c.weights.x > .001) macro += SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, mp.zy, layer, mx.zy, my.zy) * c.weights.x;
        [branch] if (c.weights.y > .001) macro += SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, mp.xz, layer, mx.xz, my.xz) * c.weights.y;
        [branch] if (c.weights.z > .001) macro += SAMPLE_TEXTURE2D_ARRAY_GRAD(_Albedos, sampler_Albedos, mp.xy, layer, mx.xy, my.xy) * c.weights.z;
        float luminance = max(dot(macro.rgb, float3(.2126, .7152, .0722)), .06);
        float3 sourceTint = clamp(macro.rgb / luminance, .72, 1.28);
        result.albedo *= lerp(1, sourceTint * lerp(.8, 1.2, macro.a), saturate(_LandParameters.z));
        // The flat material also includes source-height response at its macro
        // frequency. Omitting it left large rock faces smooth despite using the
        // correct albedo. Transform every projection's signed derivative into
        // the same world gradient before mixing, just like the micro response.
        [branch] if (_UseResponse > .5)
        {
            float3 macroGradient = 0;
            [branch] if (c.weights.x > .001)
            {
                float2 g = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LandResponse, sampler_Albedos, mp.zy, layer, mx.zy, my.zy).rg;
                macroGradient += float3(0, g.y, g.x) * c.weights.x;
            }
            [branch] if (c.weights.y > .001)
            {
                float2 g = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LandResponse, sampler_Albedos, mp.xz, layer, mx.xz, my.xz).rg;
                macroGradient += float3(g.x, 0, g.y) * c.weights.y;
            }
            [branch] if (c.weights.z > .001)
            {
                float2 g = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LandResponse, sampler_Albedos, mp.xy, layer, mx.xy, my.xy).rg;
                macroGradient += float3(g.x, g.y, 0) * c.weights.z;
            }
            result.gradient += macroGradient * macroScale * _LandParameters.z * .35;
        }
    }
    return result;
}

LandSample CoastProjection(float2 uv, float2 dx, float2 dy, int axis, float scale, bool cliff)
{
    LandSample sample = EmptyLand();
    const float texel = 1.0 / 512.0;
    float h, hu, hv;
    if (cliff)
    {
        sample.albedo = SAMPLE_TEXTURE2D_GRAD(_CliffAlbedo, sampler_Albedos, uv, dx, dy).rgb;
        h = SAMPLE_TEXTURE2D_GRAD(_CliffHeight, sampler_Albedos, uv, dx, dy).r;
        hu = SAMPLE_TEXTURE2D_GRAD(_CliffHeight, sampler_Albedos, uv + float2(texel,0), dx, dy).r;
        hv = SAMPLE_TEXTURE2D_GRAD(_CliffHeight, sampler_Albedos, uv + float2(0,texel), dx, dy).r;
    }
    else
    {
        sample.albedo = SAMPLE_TEXTURE2D_GRAD(_CoastAlbedo, sampler_Albedos, uv, dx, dy).rgb;
        h = SAMPLE_TEXTURE2D_GRAD(_CoastHeight, sampler_Albedos, uv, dx, dy).r;
        hu = SAMPLE_TEXTURE2D_GRAD(_CoastHeight, sampler_Albedos, uv + float2(texel,0), dx, dy).r;
        hv = SAMPLE_TEXTURE2D_GRAD(_CoastHeight, sampler_Albedos, uv + float2(0,texel), dx, dy).r;
    }
    float2 g = (float2(hu,hv)-h) / texel * scale;
    sample.gradient = axis == 0 ? float3(0,g.y,g.x) : axis == 1 ? float3(g.x,0,g.y) : float3(g.x,g.y,0);
    sample.height = h; sample.gloss = .18;
    return sample;
}
LandSample ReadCoast(LandCoordinates c, bool cliff)
{
    float scale = max(_CoastMaterialScale, .001) * (cliff ? .55 : 1);
    float3 p = c.world * scale, dx = c.dx * scale, dy = c.dy * scale;
    LandSample sample = EmptyLand();
    if (c.weights.x > .001) AddLand(sample, CoastProjection(p.zy,dx.zy,dy.zy,0,scale,cliff), c.weights.x);
    if (c.weights.y > .001) AddLand(sample, CoastProjection(p.xz,dx.xz,dy.xz,1,scale,cliff), c.weights.y);
    if (c.weights.z > .001) AddLand(sample, CoastProjection(p.xy,dx.xy,dy.xy,2,scale,cliff), c.weights.z);
    return sample;
}

float StripeBand(float sdkHeight, float center, float width)
{ return 1 - smoothstep(width * .32, width * .5, abs(sdkHeight - center)); }

LandSample EvaluateLand(float3 positionWS, float3 normalWS, float3 radialUp,
    float4 mountainMaterial, float4 terrain, float4 mountainClimate,
    float4 biomeWeights, float4 upperBiomeWeights, float4 desertMaterial, float landWeight)
{
    LandCoordinates c;
    c.world = positionWS - _SphereCenter.xyz;
    c.dx = ddx(positionWS); c.dy = ddy(positionWS);
    c.weights = pow(abs(normalWS), 4); c.weights /= max(dot(c.weights, 1), .00001);
    // The flat material only uses its top projection on gentle terrain. A
    // sphere has no global Y-up: use compact radial chart weights for that
    // top surface, retaining broad triplanar projection on exposed faces.
    // Charts are fixed in planet space (no camera-following UV or polar seam).
    float3 topWeights = pow(abs(radialUp), 16);
    topWeights /= max(dot(topWeights, 1), .00001);
    c.weights = lerp(c.weights, topWeights, smoothstep(.60, .90, dot(normalWS, radialUp)));
    float elevation = max(terrain.z + .178, 0), slope = 1 - saturate(dot(normalWS, radialUp));
    // UV1.y retains authored material height as the spherical silhouette changes.
    // UV1.z stays actual local relief, independent of the regional platform.
    float mountainElevation = max(terrain.y + .178, 0);
    float authoredPeakHeight = mountainClimate.x / max(_MountainGeometryScale,.01);
    // The CPU integrates dry-cell biome coverage over the same compact support
    // as the spherical surface. Interpolate coverage, never a discrete layer
    // ID: rounding that ID painted bands of unrelated biomes inside triangles.
    // UV3 = biome 0..3; the fifth (snow) is the normalized remainder. Clamp only
    // rounding drift so interior cells retain one active texture layer.
    float4 coverage = max(biomeWeights, 0);
    coverage /= max(dot(coverage, 1), 1);
    float weights[5];
    weights[0] = coverage.x; weights[1] = coverage.y;
    weights[2] = coverage.z; weights[3] = coverage.w;
    weights[4] = saturate(1 - dot(coverage, 1));
    float upper[5];
    upper[0] = upperBiomeWeights.x; upper[1] = upperBiomeWeights.y;
    upper[2] = upperBiomeWeights.z; upper[3] = upperBiomeWeights.w;
    upper[4] = terrain.x;
    LandSample ground = EmptyLand();
    float totalWeight = 0;
    float hill = smoothstep(.3, 1.8, elevation);
    [loop] for (int layer = 0; layer < 5; layer++)
    {
        // Explicit texture gradients were captured in c before this loop.
        // Zero support stays zero, so source microheight can refine a blend
        // without introducing another material outside its geographic region.
        [branch] if (weights[layer] <= 0) continue;
        LandSample source = ReadLand(c, layer);
        float upperAmount = hill * saturate(upper[layer] / max(weights[layer], .00001));
        [branch] if (upperAmount > .001) source = BlendLand(source, ReadLand(c, layer + 5), upperAmount);
        float weight = weights[layer] * HeightWeight(source.height);
        AddLand(ground, source, weight); totalWeight += weight;
    }
    ground = DivideLand(ground, totalWeight);
    // Recover the regional base from radial altitude minus local relief. A
    // continuous shoulder mask replaces per-triangle material classification.
    float platform = saturate((length(c.world) - _SphereRadius - terrain.z - .178) / max(_PlateauHeight,.01));
    float plateauRiser = smoothstep(.025,.25,platform) * (1-smoothstep(.75,.975,platform)) *
        smoothstep(.025,.22,slope) * (1-smoothstep(.5,2.2,terrain.z));
    float warmPlateau = saturate(mountainClimate.w);
    [branch] if (_UseResponse > .5 && warmPlateau > .001)
    {
        LandSample highland = BlendLand(ReadLand(c, 3), ReadLand(c, 17), .15);
        highland.albedo *= dot(ground.albedo, float3(.2126, .7152, .0722)) /
            max(dot(highland.albedo, float3(.2126, .7152, .0722)), .0001);
        // Climate adaptation uses the source's dry highland texture, retaining
        // base luminance as the flat preview does.
        LandSample weathered = EmptyLand();
        float weather = warmPlateau * _PlateauWeathering;
        AddLand(weathered, ground, 1 - weather); AddLand(weathered, highland, weather); ground = weathered;
    }
    float face = max(smoothstep(.08, .42, slope), smoothstep(2.2, 4.2, mountainElevation));
    float mountainPresence = saturate(mountainMaterial.x);
    float exposed = smoothstep(.36, 1.6, mountainElevation) * smoothstep(.02, .5, mountainPresence) * lerp(.3, 1, face);
    [branch] if (exposed > .001)
    {
        // Same presence-squared contribution reduction as HexNearLandMaterial.
        // The CPU reads the actual authored masks before interpolation; no
        // winning biome/stamp ID can repaint a whole triangle or saddle.
        LandSample rock = ReadLand(c, 10);
        [branch] if (mountainMaterial.y > .001) rock = BlendLand(rock, ReadLand(c, 11), mountainMaterial.y);
        float desertWeight = saturate(mountainClimate.y);
        [branch] if (desertWeight > .001)
        {
            LandSample desert = ReadLand(c, 13);
            float sdkHeight = 24 * mountainElevation / max(authoredPeakHeight, .1);
            float3 bands = float3(max(StripeBand(sdkHeight, 10, 10), StripeBand(sdkHeight, 20, 1)),
                StripeBand(sdkHeight, 18, 4), StripeBand(sdkHeight, 23, 2));
            float3 stripes = max(desertMaterial.rgb, 0);
            float sand = max(desertMaterial.a, 0);
            stripes = saturate(stripes + bands * (1 - saturate(dot(stripes, 1) + sand)) * .65);
            if (_UseResponse < .5) stripes.yz = 0;
            float total = dot(stripes, 1) + sand, inv = rcp(max(total, 1));
            stripes *= inv; sand *= inv;
            LandSample striped = EmptyLand();
            float w = (1 - saturate(total)) * HeightWeight(desert.height), weight = w;
            AddLand(striped, desert, w);
            [branch] if (sand > .001)
            { LandSample s = ReadLand(c, 0); w = sand * HeightWeight(s.height); AddLand(striped, s, w); weight += w; }
            [unroll] for (int index = 0; index < 3; index++)
            {
                [branch] if (stripes[index] > .001)
                { LandSample s = ReadLand(c, 14 + index); w = stripes[index] * HeightWeight(s.height); AddLand(striped, s, w); weight += w; }
            }
            desert = DivideLand(striped, weight);
            rock = BlendLand(rock, desert, desertWeight);
        }
        float relativeHeight = mountainElevation / max(authoredPeakHeight, .1);
        float snowLine = mountainClimate.z;
        float snow = max(smoothstep(snowLine, snowLine + .11, relativeHeight),
            mountainMaterial.z * mountainPresence * smoothstep(.34, .68, relativeHeight));
        snow *= (1 - desertWeight) * smoothstep(.18, .7, dot(normalWS, radialUp));
        snow *= saturate(mountainMaterial.w);
        [branch] if (snow > .001) rock = BlendLand(rock, ReadLand(c, 12), snow);
        ground = BlendLand(ground, rock, exposed);
    }
    [branch] if (plateauRiser > .001)
    {
        // Exposed shoulders blend into the same local soil and keep world-space
        // triplanar texture density on the slope, toe and raised top.
        LandSample wall = ReadLand(c, 10);
        [branch] if (coverage.x > .001) wall = BlendLand(wall, ReadLand(c, 13), coverage.x);
        ground = BlendLand(ground, wall, plateauRiser * lerp(.42,.20,coverage.x));
        ground.gradient *= 1 - plateauRiser * .25;
    }
    // The shoreline keeps its contributing climate. Grass/plains expose damp
    // soil and coarse mineral grains; desert retains sand; steep coasts expose
    // the source cliff material. One yellow tint no longer repaints every shore.
    float seaHeight = length(c.world) - _SphereRadius;
    float beach = smoothstep(.025, .17, 1-landWeight) * (1-smoothstep(.20,.65,seaHeight));
    if (beach > .001)
    {
        LandSample sand = ReadCoast(c, false), mineral = ReadCoast(c, true);
        LandSample shore = BlendLand(mineral, sand, .22);
        float wet = 1-smoothstep(-.12,.24,seaHeight);
        shore.albedo *= float3(.88,.91,.81);
        shore = BlendLand(shore, sand, coverage.x);
        float rock = smoothstep(.035,.20,slope) * (1-coverage.x*.65);
        shore = BlendLand(shore, mineral, rock);
        float snow = saturate(1-dot(coverage,1));
        if (snow > .001) shore = BlendLand(shore, ReadLand(c,4), snow*(1-wet*.55));
        // Apply wetness after climate/rock blending so a pure desert shore
        // also darkens, while retaining its source sand grain.
        float3 sandGrade = lerp(_DrySand.rgb,_WetSand.rgb,wet);
        float gradeLum = max(dot(sandGrade,float3(.2126,.7152,.0722)),.04);
        shore.albedo *= lerp(1,clamp(sandGrade/gradeLum,.65,1.35),coverage.x*.28) * lerp(1,.67,wet);
        shore.gloss = lerp(.13,.46,wet);
        float grainEdge = saturate(beach + (shore.height-.5)*.28*beach*(1-beach));
        ground = BlendLand(ground, shore, grainEdge*.85);
    }
    // Same continuous HF floor grading as Game_2, using radial water depth.
    // Climate-specific shore grain remains visible through the thin wet edge.
    float waterDepth = max(.004-seaHeight,0);
    if (waterDepth > .001)
    {
        float3 floorSand = lerp(_DrySand.rgb,_WetSand.rgb,smoothstep(0,.55,waterDepth));
        floorSand = lerp(floorSand,ground.albedo,.32);
        float3 deepFloor = lerp(_DeepWater.rgb*1.05,_DeepWater.rgb*.82,smoothstep(.55,3.2,waterDepth));
        deepFloor = lerp(_ShallowWater.rgb*float3(.55,.75,.82),deepFloor,smoothstep(.15,1.10,waterDepth));
        float3 floorColor = lerp(floorSand,deepFloor,smoothstep(.18,.95,waterDepth));
        floorColor *= lerp(.88,1.12,dot(ground.albedo,float3(.299,.587,.114)));
        ground.albedo = lerp(ground.albedo,floorColor,smoothstep(.04,.72,1-landWeight));
    }
    float bank = (1 - smoothstep(_RiverCarve.x + .15, _RiverCarve.x + 1.2, terrain.w * 2.4)) * landWeight;
    float3 bankColor = _RiverBank.rgb;
    [branch] if (bank > .001 && _Civ6RiverBankTextureStrength > 0)
    {
        float scale = max(_Civ6RiverWorldScale, .0001) * .45;
        float3 p = c.world * scale, dx = c.dx * scale, dy = c.dy * scale;
        float3 source = 0;
        [branch] if (c.weights.x > .001) source += SAMPLE_TEXTURE2D_GRAD(_Civ6RiverBankAlbedo, sampler_Albedos, p.zy, dx.zy, dy.zy).rgb * c.weights.x;
        [branch] if (c.weights.y > .001) source += SAMPLE_TEXTURE2D_GRAD(_Civ6RiverBankAlbedo, sampler_Albedos, p.xz, dx.xz, dy.xz).rgb * c.weights.y;
        [branch] if (c.weights.z > .001) source += SAMPLE_TEXTURE2D_GRAD(_Civ6RiverBankAlbedo, sampler_Albedos, p.xy, dx.xy, dy.xy).rgb * c.weights.z;
        float luminance = max(dot(source, float3(.2126, .7152, .0722)), .04);
        float3 variation = clamp(source / luminance, .75, 1.25) * lerp(.82, 1.18, saturate(luminance));
        bankColor *= lerp(1, variation, saturate(_Civ6RiverBankTextureStrength));
    }
    ground.albedo = lerp(ground.albedo, bankColor, bank * .56);
    return ground;
}

float3 LandSpecular(LandSample material, float3 normal, float3 view, Light light)
{
    float3 h = SafeNormalize(view + light.direction);
    float noL = saturate(dot(normal, light.direction)), noV = max(saturate(dot(normal, view)), .001);
    float noH = saturate(dot(normal, h)), voH = saturate(dot(view, h));
    float roughness = clamp(lerp(_LandShading.y, _LandShading.x, material.gloss), .12, 1);
    float alpha = roughness * roughness, a2 = alpha * alpha;
    float denominator = noH * noH * (a2 - 1) + 1;
    float distribution = a2 / max(PI * denominator * denominator, .00001);
    float gv = noL * sqrt(noV * noV * (1 - a2) + a2);
    float gl = noV * sqrt(noL * noL * (1 - a2) + a2);
    float fresnel = .04 + .96 * pow(1 - voH, 5);
    return light.color * (distribution * .5 / max(gv + gl, .00001) * fresnel * noL * _LandShading.z * light.shadowAttenuation);
}
#endif
