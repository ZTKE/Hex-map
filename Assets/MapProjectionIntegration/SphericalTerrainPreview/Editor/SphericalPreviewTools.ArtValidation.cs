using System.Collections;
using UnityEditor;
using UnityEngine;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        // These checks catch displaced water and missing art inputs. Appearance
        // is reviewed separately against captures from the actual flat Game_2.
        static IEnumerator ValidateNearArtInputs()
        {
            var terrain = validation.terrain;
            var world = terrain.World;
            validation.report.activeStage = "flat near-art inputs and highland river level";
            SaveValidation();
            var profile = terrain.terrainProfile;
            bool masks = profile.landMaterials && profile.landMaterials.HasMasks;
            AddCheck("Near art uses the current authored material-mask atlas", masks, masks ? 1 : 0, 1,
                "Summit rock/snow and desert stratum IDs come from the flat land-material masks, separate from the height atlas.");
            int riverSamples = 0;
            float largestWaterOffset = 0, deepestBed = 0, highestBed = float.NegativeInfinity;
            for (int id = 0; id < world.Count && riverSamples < 600; id++)
            {
                var cell = world.Cells[id];
                if (cell.Water || cell.RiverEdgeMask == 0) continue;
                Vector2 geo = world.LonLat(world.Centers[id]);
                if (geo.x < 70 || geo.x > 103 || geo.y < 25 || geo.y > 40) continue;
                if (terrain.Surface.BaseHeight(world.Centers[id], id) < 2) continue;
                for (int e = 0; e < world.CornerIds[id].Length && riverSamples < 600; e++)
                {
                    if ((cell.RiverEdgeMask & (1 << e)) == 0 || world.Neighbors[id][e] < id) continue;
                    int a = world.CornerIds[id][e], b = world.CornerIds[id][(e + 1) % world.CornerIds[id].Length];
                    for (int j = 1; j <= 3; j++)
                    {
                        Vector3 p = terrain.Surface.Rivers.Point(a, b, j * .25f);
                        var s = terrain.Surface.Evaluate(p, id);
                        if (s.Land < .9f || s.BaseHeight < 2) continue;
                        largestWaterOffset = Mathf.Max(largestWaterOffset, Mathf.Abs(s.RiverSurface - s.BaseHeight));
                        deepestBed = Mathf.Max(deepestBed, s.RiverSurface - s.Height);
                        highestBed = Mathf.Max(highestBed, s.Height - s.RiverSurface);
                        riverSamples++;
                    }
                }
                if (riverSamples > 0 && riverSamples % 30 < 3) yield return null;
            }
            AddCheck("Highland river water stays at the local platform altitude", riverSamples >= 30 && largestWaterOffset < .2f,
                largestWaterOffset, .2, "Sampled " + riverSamples + " real Tibetan river centerline points. Radial water elevation is compared with the local platform, including step transitions.");
            AddCheck("Highland river beds remain shallow and below their water", riverSamples >= 30 && deepestBed < .55f && highestBed < .02f,
                deepestBed, .55, "Uses real terrain geometry evaluation, not just water vertices. Highest bed relative to water=" + highestBed + ".");
            Light sun = validation.navigation.sun;
            float colorError = sun ? Vector3.Distance(new Vector3(sun.color.r, sun.color.g, sun.color.b), new Vector3(1, .965f, .91f)) : 1;
            AddCheck("Sun color and intensity match the active flat near-terrain art", sun && colorError < .001f && Mathf.Abs(sun.intensity - 1.06f) < .001f,
                colorError, .001, "Compared with HexNearTerrainLighting's actual runtime light values; orbit direction remains spherical.");
        }
    }
}
