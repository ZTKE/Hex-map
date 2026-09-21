#ifndef HEX_NEAR_LAND_MATERIAL_INCLUDED
#define HEX_NEAR_LAND_MATERIAL_INCLUDED

// Unity material adaptation of Civ6 SDK BaseColor / Heightmap / Spec / ID data.
// Geometry remains exclusively in HexNearTerrainShape.hlsl and its CPU mirror.
// Response RG = signed dH/du,dH/dv in Unity UV orientation, B = source gloss,
// A = source Fuzz data. Fuzz has no documented BRDF meaning and is not shaded.
TEXTURE2D_ARRAY(_HexNearLandResponse);
TEXTURE2D_ARRAY(_HexNearLandMasks);
float _HexNearLandResponseEnabled;
float _HexNearLandMasksEnabled;
float4 _HexNearLandParams; // normal amplitude, height-blend feather, macro amount, macro UV ratio
float4 _HexNearLandShading; // roughness min/max, specular amount, reserved (not a fuzz interpretation)

struct HexNearLandMaterial
{
    float3 albedo;
    float3 normalWS;
    float roughness;
    float specular;
    float ao;
    float sourceGloss;
    float sourceFuzz;
};

struct HexLandSample
{
    float3 albedo;
    float3 gradient;
    float height;
    float gloss;
    float fuzz;
};

struct HexLandCoordinates
{
    float3 world;
    float3 dx;
    float3 dy;
    float3 projectionWeights;
};

float4 HexLandParameters()
{
    // Old profiles have only the original fifteen-layer atlas. Keep a useful
    // material when the optional response assets and their driver are absent.
    return _HexNearLandResponseEnabled > .5 ? _HexNearLandParams : float4(.3, .14, .18, .19);
}

float3 HexLandWorldScale(float target)
{
    target = max(target, .0001);
    float mapWidth = max(_HexCellData_TexelSize.z * (2.0 * OUTER_RADIUS * OUTER_TO_INNER), 1.0);
    // Quantize each frequency independently, including the macro frequency.
    // Do not frac spatial UV: translated map copies then keep both phase and LOD.
    return float3(max(round(mapWidth * target), 1.0) / mapWidth, target, target);
}

HexLandSample HexLandZero()
{
    HexLandSample s;
    s.albedo = 0; s.gradient = 0; s.height = 0; s.gloss = 0; s.fuzz = 0;
    return s;
}

void HexLandAccumulate(inout HexLandSample sum, HexLandSample s, float weight)
{
    sum.albedo += s.albedo * weight; sum.gradient += s.gradient * weight;
    sum.height += s.height * weight; sum.gloss += s.gloss * weight; sum.fuzz += s.fuzz * weight;
}

HexLandSample HexLandDivide(HexLandSample s, float total)
{
    float inverse = rcp(max(total, .00001));
    s.albedo *= inverse; s.gradient *= inverse; s.height *= inverse;
    s.gloss *= inverse; s.fuzz *= inverse;
    return s;
}

float HexLandHeightWeight(float height)
{
    // Positive, bounded height assistance preserves the exact zero support of
    // logical weights. It cannot introduce another cell's material at a seam.
    return max(.02, saturate(height) + max(HexLandParameters().y, .02));
}

HexLandSample HexLandBlend(HexLandSample a, HexLandSample b, float amount)
{
    float wa = (1.0 - saturate(amount)) * HexLandHeightWeight(a.height);
    float wb = saturate(amount) * HexLandHeightWeight(b.height);
    HexLandSample s = HexLandZero();
    HexLandAccumulate(s, a, wa); HexLandAccumulate(s, b, wb);
    return HexLandDivide(s, wa + wb);
}

HexLandSample HexLandProjection(float2 uv, float2 dx, float2 dy, int layer, int axis, float3 scale)
{
    float4 base = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos, sampler_HexNearAlbedos, uv, layer, dx, dy);
    float2 slope = 0;
    HexLandSample s = HexLandZero();
    s.albedo = base.rgb; s.height = base.a; s.gloss = .15;
    [branch] if (_HexNearLandResponseEnabled > .5)
    {
        float4 response = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearLandResponse, sampler_HexNearAlbedos, uv, layer, dx, dy);
        slope = response.rg; s.gloss = saturate(response.b); s.fuzz = response.a;
    }
    else
    {
        // The legacy atlas is 512 square. This only samples existing layers;
        // derivatives are per-material, never derivatives of blended albedo alpha.
        const float texel = 1.0 / 512.0;
        float hu = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos, sampler_HexNearAlbedos,
            uv + float2(texel, 0), layer, dx, dy).a;
        float hv = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos, sampler_HexNearAlbedos,
            uv + float2(0, texel), layer, dx, dy).a;
        slope = (float2(hu, hv) - base.a) / texel;
    }
    // Height derivatives become a world gradient before triplanar blending.
    // The final gradient is projected into the actual geometric tangent plane.
    s.gradient = axis == 0 ? float3(0, slope.y * scale.y, slope.x * scale.z) :
        (axis == 1 ? float3(slope.x * scale.x, 0, slope.y * scale.z) :
        float3(slope.x * scale.x, slope.y * scale.y, 0));
    return s;
}

HexLandSample HexLandReadLayer(HexLandCoordinates c, int layer)
{
    float scaleRatio = layer >= 10 && layer <= 16 && layer != 12 ? .72 : 1.0;
    float targetScale = _HexNearDetails.z * scaleRatio;
    float3 scale = HexLandWorldScale(targetScale);
    float3 p = c.world * scale, dx = c.dx * scale, dy = c.dy * scale;
    HexLandSample s = HexLandZero();
    // No derivatives inside divergent branches: all sample LODs use the
    // world-position gradients captured before the cell/material loops.
    [branch] if (c.projectionWeights.x > 0)
        HexLandAccumulate(s, HexLandProjection(p.zy, dx.zy, dy.zy, layer, 0, scale), c.projectionWeights.x);
    [branch] if (c.projectionWeights.y > 0)
        HexLandAccumulate(s, HexLandProjection(p.xz, dx.xz, dy.xz, layer, 1, scale), c.projectionWeights.y);
    [branch] if (c.projectionWeights.z > 0)
        HexLandAccumulate(s, HexLandProjection(p.xy, dx.xy, dy.xy, layer, 2, scale), c.projectionWeights.z);

    float4 parameters = HexLandParameters();
    [branch] if (parameters.z > 0)
    {
        float3 macroScale = HexLandWorldScale(targetScale * max(parameters.w, .01));
        float2 uv = c.world.xz * macroScale.xz;
        float2 mx = c.dx.xz * macroScale.xz, my = c.dy.xz * macroScale.xz;
        float4 macro = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearAlbedos, sampler_HexNearAlbedos, uv, layer, mx, my);
        float luminance = max(dot(macro.rgb, float3(.2126, .7152, .0722)), .06);
        float3 sourceTint = clamp(macro.rgb / luminance, .72, 1.28);
        float3 variation = sourceTint * lerp(.80, 1.20, macro.a);
        s.albedo *= lerp(1.0, variation, saturate(parameters.z));
        [branch] if (_HexNearLandResponseEnabled > .5)
        {
            float2 gradient = SAMPLE_TEXTURE2D_ARRAY_GRAD(_HexNearLandResponse, sampler_HexNearAlbedos, uv, layer, mx, my).rg;
            s.gradient += float3(gradient.x * macroScale.x, 0, gradient.y * macroScale.z) * parameters.z * .35;
        }
    }
    return s;
}

float HexLandStripeBand(float sdkHeight, float center, float width)
{
    // Nonperiodic extension for procedural connectors outside authored ID masks.
    // Centers/widths come from TerrainStyle.artdef; this smooth profile and the
    // scaling from SDK height 24 to the current mountain height are adaptations.
    return 1.0 - smoothstep(width * .32, width * .5, abs(sdkHeight - center));
}

HexNearLandMaterial EvaluateHexNearLandMaterial(float cellIndex, float2 local, float3 world, float3 geometryNormal)
{
    HexLandCoordinates coordinates;
    coordinates.world = world; coordinates.dx = ddx(world); coordinates.dy = ddy(world);
    float3 projection = pow(abs(geometryNormal), 4.0);
    projection /= max(dot(projection, 1.0), .00001);
    // Flat fields use only the top projection; steep faces gain side projections.
    coordinates.projectionWeights = lerp(projection, float3(0, 1, 0), smoothstep(.60, .90, geometryNormal.y));
    float biomeWeights[5], upperWeights[5];
    [unroll] for (int b = 0; b < 5; b++) { biomeWeights[b] = 0; upperWeights[b] = 0; }
    float2 root = HFCellIndexToOffset(cellIndex);
    float plateau = 0, plateauWeight = 0, weatherWeight = 0, groundWeight = 0;
    float mountainPresence = 0, topMask = 0, snowStamp = 0, rockWeight = 0;
    float summitSnowPresence = 0;
    float desertWeight = 0, desertSandMask = 0, snowLine = 0, peakHeight = 0;
    float3 stripeMasks = 0;

    // Same thirteen source positions and compact support as the unchanged
    // geometry contract. Aggregate logical biomes before doing material samples.
    [loop] for (int i = -1; i < 12; i++)
    {
        float2 off = root, p = local;
        if (i >= 0)
        {
            int direction = i >> 1;
            off = HFNeighborOffset(root, direction); p -= HFNeighborCenter(direction);
            if ((i & 1) != 0) { int next = direction == 5 ? 0 : direction + 1; off = HFNeighborOffset(off, next); p -= HFNeighborCenter(next); }
        }
        float radius = length(p);
        [branch] if (radius >= 2.4) continue;
        HFCellShape c = HFLoadCell(off);
        if (c.valid < .5 || c.underwater > .5) continue;
        float plateauW = 1.0 - smoothstep(.2, _HexNearPlateau.w, radius);
        plateauWeight += plateauW;
        if (c.landform > 2.5) plateau += plateauW;
        float w = pow(saturate(1.0 - radius / 1.55), 2.0);
        int biome = clamp((int)round(c.terrain), 0, 4);
        biomeWeights[biome] += w; groundWeight += w;
        upperWeights[biome] += w * (1.0 - saturate(c.landform - 1.0));
        if (c.landform > 2.5 && biome > 0 && biome < 3) weatherWeight += w;
        [branch] if (c.landform > 1.5 && c.landform < 2.5)
        {
            uint hash = NearHash(c.offset); bool desert = c.terrain < .5;
            float2 size = NearMountainScale(c);
            float footprint = (desert ? _HexNearDesert.y : _HexNearHeights.z) * size.y;
            float height = (desert ? _HexNearDesert.x : _HexNearHeights.x) * size.x;
            float baseFootprint = min(footprint * (1.0 + .5 * _HexNearDetails.x), 1.85);
            float reach = max(footprint, baseFootprint) * 1.18;
            if (_HexNearMountainBase.x > 0.0) reach = max(reach, _HexNearMountainBase.y);
            if (_HexNearRange.x > 0.0 && c.neighborMask != 0u) reach = max(reach, 2.4);
            [branch] if (radius >= reach) continue;
            float2 shapeUV = NearRotate(p, c.angle) / (2.0 * footprint) + .5;
            int shapeLayer = desert ? 6 + (int)(hash % 4u) : (int)(hash % 5u);
            float4 shape = SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearShapes, HF_TERRAIN_LINEAR_SAMPLER, shapeUV, shapeLayer, 0);
            float fade = 1.0 - smoothstep(footprint * .78, footprint * 1.18, radius);
            float presence = max(shape.g * fade, smoothstep(.035, .35, shape.r * height * fade));
            // The shared low mountain mass extends beyond the summit's stamp.
            // Height and slope below still retain soil on its gentle perimeter.
            if (_HexNearMountainBase.x > 0.0)
                presence = max(presence, (1.0 - smoothstep(.2, _HexNearMountainBase.y, radius)) *
                    .7 * saturate(_HexNearMountainBase.x / .34));
            if (_HexNearRange.x > 0.0 && c.neighborMask != 0u)
                presence = max(presence, (1.0 - smoothstep(1.2, 2.4, radius)) * .85 * saturate(_HexNearRange.x));
            mountainPresence = max(mountainPresence, presence);
            summitSnowPresence = max(summitSnowPresence, presence * NearSummitSnowRetention(c, p));
            float rockW = presence * presence;
            float4 materialMask = float4(shape.ba, 0, 0);
            [branch] if (_HexNearLandMasksEnabled > .5)
                materialMask = SAMPLE_TEXTURE2D_ARRAY_LOD(_HexNearLandMasks, HF_TERRAIN_LINEAR_SAMPLER, shapeUV, shapeLayer, 0);
            // A TerrainElement ID only owns the area covered by its HBLEND.
            // In particular the desert texture's exterior sand ID must not
            // repaint the new procedural mountainside as a flat sand apron.
            // Uncovered range flanks use the layer's base/height materials below.
            materialMask *= saturate(shape.g * fade);
            if (!desert) { topMask += materialMask.r * rockW; snowStamp += materialMask.g * rockW; }
            else if (_HexNearLandMasksEnabled > .5)
            {
                stripeMasks += materialMask.rgb * rockW;
                desertSandMask += materialMask.a * rockW; // authored ID 89 = Desert_Base
            }
            rockWeight += rockW; desertWeight += (desert ? 1.0 : 0.0) * rockW;
            snowLine += (c.terrain > 3.5 ? .2 : (c.terrain > 2.5 ? .49 : .71)) * rockW;
            peakHeight += height * rockW;
        }
    }
    float plateauY = _HexNearPlateau.x * plateau / max(.0001, plateauWeight);
    float elevation = max(world.y - _HexHFOriginalDatumY - plateauY, 0.0);
    float hill = smoothstep(.3, 1.8, elevation);
    HexLandSample ground = HexLandZero(); float total = 0;
    [loop] for (int biomeIndex = 0; biomeIndex < 5; biomeIndex++)
    {
        [branch] if (biomeWeights[biomeIndex] <= 0) continue;
        HexLandSample biomeSample = HexLandReadLayer(coordinates, biomeIndex);
        float upper = hill * upperWeights[biomeIndex] / max(biomeWeights[biomeIndex], .00001);
        [branch] if (upper > 0)
            biomeSample = HexLandBlend(biomeSample, HexLandReadLayer(coordinates, biomeIndex + 5), upper);
        float w = biomeWeights[biomeIndex] * HexLandHeightWeight(biomeSample.height);
        HexLandAccumulate(ground, biomeSample, w); total += w;
    }
    if (total > .00001) ground = HexLandDivide(ground, total);
    else ground = HexLandReadLayer(coordinates, 1);
    [branch] if (_HexNearLandResponseEnabled > .5 && weatherWeight > 0)
    {
        // The source TundraBlend is still yellow/olive. Use the colder Tundra
        // base as the dominant highland palette, keeping a little transition
        // material. Match local ground luminance so baked white frost patches
        // do not turn a dry plateau into an unrelated bright snow biome.
        HexLandSample highland = HexLandBlend(HexLandReadLayer(coordinates, 3),
            HexLandReadLayer(coordinates, 17), .15);
        const float3 luminanceWeights = float3(.2126, .7152, .0722);
        float groundLuminance = dot(ground.albedo, luminanceWeights);
        float highlandLuminance = dot(highland.albedo, luminanceWeights);
        highland.albedo *= groundLuminance / max(highlandLuminance, .0001);
        // Regional climate coverage must not be weakened a second time by
        // unrelated microheight values. Only Plateau grass/plains contributed
        // weatherWeight above; ordinary lowland and desert retain zero weight.
        float weather = saturate(weatherWeight / max(groundWeight, .00001)) * saturate(_HexNearPlateau.z);
        HexLandSample regional = HexLandZero();
        HexLandAccumulate(regional, ground, 1.0 - weather);
        HexLandAccumulate(regional, highland, weather);
        ground = regional;
    }

    float slope = 1.0 - saturate(geometryNormal.y);
    float face = max(smoothstep(.08, .42, slope), smoothstep(2.2, 4.2, elevation));
    float exposed = smoothstep(.36, 1.6, elevation) * smoothstep(.02, .5, mountainPresence) * lerp(.3, 1.0, face);
    [branch] if (exposed > 0)
    {
        float inverseWeight = rcp(max(rockWeight, .0001));
        topMask *= inverseWeight; snowStamp *= inverseWeight;
        snowLine *= inverseWeight; peakHeight *= inverseWeight;
        stripeMasks /= max(desertWeight, .0001);
        desertSandMask /= max(desertWeight, .0001); desertWeight *= inverseWeight;
        HexLandSample rock = HexLandReadLayer(coordinates, 10);
        [branch] if (topMask > 0) rock = HexLandBlend(rock, HexLandReadLayer(coordinates, 11), topMask);
        [branch] if (desertWeight > 0)
        {
            HexLandSample desert = HexLandReadLayer(coordinates, 13);
            float sdkHeight = 24.0 * elevation / max(peakHeight, .1);
            float3 bands = float3(max(HexLandStripeBand(sdkHeight, 10, 10), HexLandStripeBand(sdkHeight, 20, 1)),
                HexLandStripeBand(sdkHeight, 18, 4), HexLandStripeBand(sdkHeight, 23, 2));
            // Preserve classified ID coverage; extend only its uncovered area.
            float uncovered = 1.0 - saturate(dot(stripeMasks, 1.0) + desertSandMask);
            stripeMasks = saturate(stripeMasks + bands * uncovered * .65);
            if (_HexNearLandResponseEnabled <= .5) stripeMasks.yz = 0; // legacy 15-layer atlas
            // Sand and stripe IDs are mutually exclusive authored categories.
            // Accumulate them as peers: premixing sand into base and then
            // multiplying base by the stripe remainder would count sand twice.
            float maskTotal = dot(stripeMasks, 1.0) + desertSandMask;
            float inverseMaskTotal = rcp(max(maskTotal, 1.0));
            stripeMasks *= inverseMaskTotal; desertSandMask *= inverseMaskTotal;
            HexLandSample striped = HexLandZero(); float stripeWeight = 0;
            float baseW = max(0.0, 1.0 - min(maskTotal, 1.0)) * HexLandHeightWeight(desert.height);
            HexLandAccumulate(striped, desert, baseW); stripeWeight += baseW;
            [branch] if (desertSandMask > 0)
            {
                HexLandSample sand = HexLandReadLayer(coordinates, 0);
                float w = desertSandMask * HexLandHeightWeight(sand.height);
                HexLandAccumulate(striped, sand, w); stripeWeight += w;
            }
            [loop] for (int stripe = 0; stripe < 3; stripe++)
            {
                [branch] if (stripeMasks[stripe] <= 0) continue;
                HexLandSample s = HexLandReadLayer(coordinates, 14 + stripe);
                float w = stripeMasks[stripe] * HexLandHeightWeight(s.height);
                HexLandAccumulate(striped, s, w); stripeWeight += w;
            }
            desert = HexLandDivide(striped, stripeWeight);
            rock = HexLandBlend(rock, desert, desertWeight);
        }
        float relativeHeight = elevation / max(peakHeight, .1);
        float snow = max(smoothstep(snowLine, snowLine + .11, relativeHeight),
            snowStamp * mountainPresence * smoothstep(.34, .68, relativeHeight));
        snow *= (1.0 - desertWeight) * smoothstep(.18, .7, geometryNormal.y);
        snow *= saturate(summitSnowPresence / max(mountainPresence, .0001));
        [branch] if (snow > 0) rock = HexLandBlend(rock, HexLandReadLayer(coordinates, 12), snow);
        ground = HexLandBlend(ground, rock, exposed);
    }

    HexNearLandMaterial result;
    result.albedo = max(ground.albedo, 0);
    // Blending height derivatives as surface gradients avoids false bump walls
    // where two different materials or triplanar projections exchange weight.
    float3 gradient = ground.gradient - geometryNormal * dot(geometryNormal, ground.gradient);
    gradient *= max(HexLandParameters().x, 0.0);
    gradient *= rsqrt(max(dot(gradient, gradient) / 4.0, 1.0));
    result.normalWS = normalize(geometryNormal - gradient);
    result.sourceGloss = saturate(ground.gloss); result.sourceFuzz = ground.fuzz;
    float4 shading = _HexNearLandResponseEnabled > .5 ? _HexNearLandShading : float4(.38, .96, 0, 0);
    // Source G is gloss, not linear roughness. This monotone mapping is an
    // explicit Unity approximation; it does not claim Civ6's CLEAN/Beckmann BRDF.
    result.roughness = clamp(lerp(shading.y, shading.x, result.sourceGloss), .12, 1.0);
    result.specular = max(shading.z, 0); result.ao = 1.0;
    return result;
}

float3 HexNearLandDirectSpecular(HexNearLandMaterial material, float3 normal, float3 view, float3 light, float3 lightColor)
{
    float3 halfVector = view + light;
    halfVector *= rsqrt(max(dot(halfVector, halfVector), .00001));
    float noL = saturate(dot(normal, light)), noV = max(saturate(dot(normal, view)), .001);
    float noH = saturate(dot(normal, halfVector)), voH = saturate(dot(view, halfVector));
    float alpha = material.roughness * material.roughness, alpha2 = alpha * alpha;
    float denominator = noH * noH * (alpha2 - 1.0) + 1.0;
    float distribution = alpha2 / max(PI * denominator * denominator, .00001);
    float gv = noL * sqrt(noV * noV * (1.0 - alpha2) + alpha2);
    float gl = noV * sqrt(noL * noL * (1.0 - alpha2) + alpha2);
    float visibility = .5 / max(gv + gl, .00001);
    float fresnel = .04 + .96 * pow(1.0 - voH, 5.0);
    return lightColor * (distribution * visibility * fresnel * noL * material.specular);
}
#endif
