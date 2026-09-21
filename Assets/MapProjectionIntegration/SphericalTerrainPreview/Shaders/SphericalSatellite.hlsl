#ifndef WW2_SPHERICAL_SATELLITE_INCLUDED
#define WW2_SPHERICAL_SATELLITE_INCLUDED
TEXTURE2D(_SatelliteColor); SAMPLER(sampler_SatelliteColor);
TEXTURE2D(_SatelliteRelief); SAMPLER(sampler_SatelliteRelief);
TEXTURE2D(_OceanRelief); SAMPLER(sampler_OceanRelief);

half3 SatelliteFarSaturation(half3 color, half saturation)
{
    // The original near path never evaluates satellite shading. The separate
    // far weight also preserves the original satellite color at zero, allowing
    // a continuous transition and an exact baseline during visual review.
    if (_FarEarthGrade.w <= 0) return color;
    half luminance = dot(color, half3(.2126h, .7152h, .0722h));
    half3 vivid = max(0, lerp(luminance.xxx, color, saturation));
    return lerp(color, vivid, _FarEarthGrade.w);
}

float2 SphericalEarthUV(float3 radial)
{
    return float2(atan2(radial.z, radial.x) / (2 * PI) + .5, asin(clamp(radial.y, -1, 1)) / PI + .5);
}

half3 SatelliteIllumination(float3 normal, float3 radialUp, Light sun)
{
    if (_UseNaturalSatellite > .5)
        return lerp(1.0h, .34h + saturate(dot(normal, sun.direction)) * .85h, _NaturalSurfaceParameters.w).xxx;
    return min(SphericalAmbient(normal,radialUp) * .62h + half3(.065h, .078h, .095h)
        + sun.color * (.06h + .94h * saturate(dot(normal, sun.direction))) , 1.2h);
}

half3 SatelliteAtmosphereColor(float3 radial, float3 view, Light sun)
{
    // A narrow, daylight-weighted limb retains the globe shape without washing
    // blue haze across the whole ocean or lifting the dark side equally.
    float rim = pow(1 - saturate(dot(radial, view)), 6);
    float daylight = smoothstep(-.2, .25, dot(radial, sun.direction));
    half3 tint = SatelliteFarSaturation(half3(.018h, .05h, .095h), _FarEarthGrade.z);
    if (_UseNaturalSatellite > .5) tint = _NaturalAtmosphereTint.rgb * .12h;
    // At full satellite distance the dedicated shell owns the continuous
    // inner/outer glow. Avoid a second blue contour on the surface itself.
    float surfaceWeight = _UseNaturalSatellite > .5 ? 1 - _FarEarthGrade.w : 1;
    return tint * rim * lerp(.2, 1, daylight) * surfaceWeight;
}

half3 SatelliteOceanColor(float3 positionWS, float3 radial, float coast, Light sun)
{
    // These are linear-light colors: the earlier .035/.105/.17 values became a
    // pale teal after the final sRGB transfer. The deep blue sits behind the
    // natural continents and the existing warm brass / ivory instruments.
    half3 albedo = lerp(half3(.004h, .014h, .028h), half3(.008h, .027h, .043h), coast * .5);
    if (_UseNaturalSatellite > .5)
    {
        float2 oceanUV = SphericalEarthUV(radial);
        float2 dx = ddx(oceanUV), dy = ddy(oceanUV);
        dx.x -= round(dx.x); dy.x -= round(dy.x);
        half3 relief = SAMPLE_TEXTURE2D_GRAD(_OceanRelief, sampler_OceanRelief, oceanUV, dx, dy).rgb;
        float shallow = 1 - smoothstep(.005, .20, relief.b);
        albedo = lerp(_NaturalOceanTint.rgb, half3(.018h,.16h,.24h), shallow * .68);
        albedo *= lerp(1.25h,.52h,smoothstep(.12,.65,relief.b));
        float3 east = normalize(float3(-radial.z,0,radial.x) + float3(1e-6,0,0));
        float3 north = normalize(cross(east,radial));
        float2 slope = (relief.rg * 2 - 1) * .40;
        float3 floorNormal = normalize(radial - east * slope.x - north * slope.y);
        albedo *= clamp(1 + (dot(floorNormal,sun.direction)-dot(radial,sun.direction)) * .60, .90, 1.10);
    }
    albedo = SatelliteFarSaturation(albedo, _FarEarthGrade.y);
    albedo *= lerp(1, _FarEarthLight.z, _FarEarthLight.x);
    half3 color = albedo * SatelliteIllumination(radial, radial, sun);
    float3 view = SafeNormalize(_WorldSpaceCameraPos.xyz - positionWS);
    float glint = pow(saturate(dot(radial, SafeNormalize(view + sun.direction))), 96)
        * saturate(dot(radial, sun.direction));
    return color + sun.color * glint * .008h + SatelliteAtmosphereColor(radial, view, sun);
}

half3 SatelliteSurfaceColor(float3 positionWS, float3 radial, Light sun,
    out float geographicLand, out float geographicCoast)
{
    float2 uv = SphericalEarthUV(radial);
    float2 dx = ddx(uv), dy = ddy(uv);
    // atan2 wraps on the dateline. Correct only the U derivative so Repeat
    // sampling never chooses a blurred whole-world mip at the longitude seam.
    dx.x -= round(dx.x); dy.x -= round(dy.x);
    half4 relief = SAMPLE_TEXTURE2D_GRAD(_SatelliteRelief, sampler_SatelliteRelief, uv, dx, dy);
    half3 albedo = SAMPLE_TEXTURE2D_GRAD(_SatelliteColor, sampler_SatelliteColor, uv, dx, dy).rgb;
    half luminance = dot(albedo, half3(.2126h, .7152h, .0722h));
    half saturation = _UseNaturalSatellite > .5 ? _NaturalSurfaceParameters.x : .7h;
    half contrast = _UseNaturalSatellite > .5 ? _NaturalSurfaceParameters.y : 1.06h;
    half3 surfaceTint = _UseNaturalSatellite > .5 ? _NaturalSurfaceTint.rgb : half3(.96h, .96h, .89h);
    albedo = lerp(luminance.xxx, albedo, saturation) * surfaceTint;
    albedo = max(0, (albedo - .18h) * contrast + .18h);
    if (_UseNaturalSatellite > .5)
    {
        half naturalLuminance = dot(albedo, half3(.2126h,.7152h,.0722h));
        albedo = max(0, lerp(naturalLuminance.xxx, albedo, 1.16h)) * 1.28h;
    }
    albedo = SatelliteFarSaturation(albedo, _FarEarthGrade.x);
    albedo *= lerp(1, _FarEarthLight.y, _FarEarthLight.x);
    // Far coastlines come from the geographic texture, not the sparse shell's
    // vertex heights. Both the terrain and overlapping ocean converge to this
    // same complete Earth color before ocean geometry is removed. This keeps
    // small islands and inlets from becoming large R4 triangle cut-outs.
    float land = smoothstep(.38, .62, relief.a);
    geographicLand = land;
    // Reuse the filtered geographic alpha: an approximately one-pixel coastal
    // outline has no dependence on native cell raster resolution or latitude.
    float coastDistance = abs(relief.a - .5) / max(fwidth(relief.a), .00001);
    geographicCoast = (1 - smoothstep(.35, 1.25, coastDistance)) * land * saturate(_UseNaturalSatellite);
    float3 east = float3(-radial.z, 0, radial.x);
    east = dot(east, east) > .000001 ? normalize(east) : float3(0, 0, 1);
    float3 north = normalize(cross(east, radial));
    float2 encodedNormal = relief.rg * 2 - 1;
    float normalUp = sqrt(saturate(1 - dot(encodedNormal, encodedNormal)));
    float normalStrength = _UseNaturalSatellite > .5 ? _NaturalSurfaceParameters.z * 4 : 2.6;
    float2 slope = encodedNormal / max(normalUp, .05) * normalStrength;
    // Geographic elevation is texture relief only; the actual globe and near
    // mesh keep one radial geometry, with no projection or silhouette morph.
    float3 normal = normalize(radial + (east * slope.x + north * slope.y) * saturate(land));
    half3 color = albedo * SatelliteIllumination(normal, radial, sun);
    float3 view = SafeNormalize(_WorldSpaceCameraPos.xyz - positionWS);
    color += SatelliteAtmosphereColor(radial, view, sun);
    half3 ocean = SatelliteOceanColor(positionWS, radial, saturate(relief.a * 2), sun);
    return lerp(ocean, color, land);
}

half3 SatelliteSurfaceColor(float3 positionWS, float3 radial, Light sun)
{
    float geographicLand, geographicCoast;
    return SatelliteSurfaceColor(positionWS, radial, sun, geographicLand, geographicCoast);
}

void ClipSatelliteOcean(float waterKind)
{
    // The terrain mesh contains the entire globe, including its submerged
    // floor. It is already fully satellite-colored at this threshold, so an
    // ocean depth pass cannot reintroduce the old coarse polygon coastline.
    if (waterKind < .5) clip(.9999 - saturate(_UseSatellite * _SatelliteBlend));
}
#endif
