using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        static void RecordLoading(ViewResult view, SphericalTerrainPreview terrain)
        {
            view.pendingChunks = terrain.PendingChunks; view.readyChunks = terrain.ReadyChunks;
            view.cacheHits = terrain.CacheHits; view.buildCount = terrain.BuildCount; view.workerCount = terrain.WorkerCount;
            view.firstVisibleSeconds = terrain.FirstVisibleSeconds; view.lastLoadSeconds = terrain.LastLoadSeconds;
            view.loadingError = terrain.LoadingError;
        }

        static IEnumerator ValidateRevisionGeography()
        {
            SphericalTerrainPreview terrain = validation.terrain;
            SphericalWorld world = terrain.World;
            validation.report.activeStage = "regional plateau and satellite inputs"; SaveValidation();
            bool existingColor = terrain.satelliteColor && AssetDatabase.GetAssetPath(terrain.satelliteColor)
                == "Assets/MapProjectionIntegration/SatellitePreview/Data/SatelliteEarthColor.png";
            bool existingRelief = terrain.satelliteRelief && AssetDatabase.GetAssetPath(terrain.satelliteRelief)
                == "Assets/MapProjectionIntegration/SatellitePreview/Data/SatelliteEarthRelief.png";
            AddCheck("Reuses the existing offline satellite color and ETOPO relief", existingColor && existingRelief,
                (existingColor ? 1 : 0) + (existingRelief ? 1 : 0), 2, "Verified both scene references point to the previous satellite preview's unchanged image assets.");
            float tibet = terrain.Surface.BaseHeight(SphericalPreviewCamera.Direction(86, 33));
            float lowland = terrain.Surface.BaseHeight(SphericalPreviewCamera.Direction(10, 52));
            AddCheck("Tibetan plateau has a regional ground level above European lowland", tibet > lowland + 1,
                tibet - lowland, 1, "Ground datum excludes authored mountain peaks. Tibet=" + tibet + "; north European lowland=" + lowland + ".");
            int plain = 0, hills = 0, mountains = 0, legacyPlatformTops = 0, sourcePlain = 0, samples = 0, checkedEdges = 0;
            float maximumSharedBaseError = 0, maximumRelief = 0;
            for (int id = 0; id < world.Count; id++)
            {
                Vector3 direction = world.Centers[id];
                if (world.Cells[id].Water || direction.y < .17f || direction.y > .77f) continue;
                Vector2 geo = world.LonLat(direction);
                if (geo.x < 65 || geo.x > 110 || geo.y < 10 || geo.y > 50) continue;
                float baseHeight = terrain.Surface.BaseHeight(direction, id);
                if (baseHeight < 2) continue;
                int displayForm = world.Cells[id].Landform;
                if (displayForm == 0) sourcePlain++;
                if (displayForm == 3) legacyPlatformTops++;
                int localForm = SphericalSurface.LocalLandform(displayForm);
                if (localForm == 0) plain++; else if (localForm == 1) hills++; else if (localForm == 2) mountains++;
                if ((samples++ & 63) == 0)
                {
                    SphericalSurface.Sample sample = terrain.Surface.Evaluate(direction, id);
                    maximumRelief = Mathf.Max(maximumRelief, sample.Height - sample.BaseHeight);
                    yield return null;
                }
                if (checkedEdges < 128)
                    for (int edge = 0; edge < world.Neighbors[id].Length && checkedEdges < 128; edge++)
                    {
                        int other = world.Neighbors[id][edge];
                        if (world.Cells[other].Water) continue;
                        Vector3 midpoint = (world.Corners[id][edge] + world.Corners[id][(edge + 1) % world.Corners[id].Length]).normalized;
                        float error = Mathf.Abs(terrain.Surface.BaseHeight(midpoint, id) - terrain.Surface.BaseHeight(midpoint, other));
                        maximumSharedBaseError = Mathf.Max(maximumSharedBaseError, error); checkedEdges++;
                    }
            }
            AddCheck("High plateau supports independent local plain and mountain art", plain > 0 && mountains > 0,
                plain + mountains, 2, "Regional base remains above 2 units. Rendered local plains=" + plain + ", hills=" + hills
                + ", mountains=" + mountains + ". Sphere Flat=" + sourcePlain + "; legacy Plateau tags=" + legacyPlatformTops
                + ". Local relief classification is independent of the regional platform; source map integrity is checked separately. Maximum sampled local relief=" + maximumRelief + ".");
            var interior = terrain.Surface.Evaluate(SphericalPreviewCamera.Direction(86, 33), -1, false);
            float interiorRelief = interior.Height - interior.BaseHeight;
            AddCheck("Plateau interior actually renders as open plain on its raised base", interior.Landform == 0
                && interior.BaseHeight > 4 && interiorRelief >= -.001f && interiorRelief < .2f,
                interiorRelief, .2, "Sampled actual surface at central Tibet: local material landform=" + interior.Landform
                + ", base=" + interior.BaseHeight + ", local relief=" + interiorRelief + ". Checks the rendered sphere after its independent classification layer.");
            AddCheck("Regional platform remains shared across highland cell boundaries", checkedEdges > 0 && maximumSharedBaseError < .001f,
                maximumSharedBaseError, .001, "Compared " + checkedEdges + " exact shared-edge midpoints with both incident cell hints; ground-level equality is independent of five/six-sided shape.");
            float highestPlatform = 0;
            for (int id = 0; id < world.Count; id += 7)
            {
                if (!world.Cells[id].Water) highestPlatform = Mathf.Max(highestPlatform, terrain.Surface.BaseHeight(world.Centers[id], id) - .178f);
                if (id % 7000 == 0) yield return null;
            }
            AddCheck("All regional highlands remain within one raised level", highestPlatform <= terrain.Surface.PlateauStepHeight + .001f
                && highestPlatform > terrain.Surface.PlateauStepHeight - .01f, highestPlatform, terrain.Surface.PlateauStepHeight,
                "Global independent base samples; local mountain and dune relief is excluded.");
            var riverCorners = new HashSet<int>();
            int invalidRoutes = 0, routeEdges = 0;
            foreach (var route in world.RiverRoutes)
            {
                if (route.Corners.Length < 25) invalidRoutes++;
                for (int i = 0; i < route.Corners.Length; i++)
                {
                    int corner = route.Corners[i];
                    if (!riverCorners.Add(corner)) invalidRoutes++;
                    if (terrain.Surface.Rivers.Degree(corner) != (i == 0 || i == route.Corners.Length - 1 ? 1 : 2)) invalidRoutes++;
                    if (i > 0)
                    {
                        var adjacent = world.Pack.adjTris[route.Corners[i - 1]];
                        if (adjacent[0] != corner && adjacent[1] != corner && adjacent[2] != corner) invalidRoutes++;
                        routeEdges++;
                    }
                }
                int Wet(int corner)
                {
                    var t = world.Pack.tris[corner];
                    return (world.Cells[t[0]].Water ? 1 : 0) + (world.Cells[t[1]].Water ? 1 : 0) + (world.Cells[t[2]].Water ? 1 : 0);
                }
                if (Wet(route.Source) != 0 || Wet(route.Mouth) != 2) invalidRoutes++;
                yield return null;
            }
            AddCheck("Redrawn sphere rivers are complete source-to-coast strokes", world.RiverRoutes.Length == 26
                && world.OmittedRiverRoutes.Length == 0 && invalidRoutes == 0 && routeEdges == world.RiverEdges.Length,
                invalidRoutes, 0, "26 authored sphere routes; every edge adjacent, every source dry, every mouth on the coast, no shared branch nodes, loops or unpublished fragments.");
            Vector3 previewFocus = SphericalPreviewCamera.Direction(83.90f, 35.97f);
            int first = world.FindCell(previewFocus);
            var nearby = new List<int> { first }; var seen = new HashSet<int> { first };
            var treads = new HashSet<int>(); int previewPlain = 0, previewMountains = 0;
            for (int cursor = 0; cursor < nearby.Count; cursor++)
                foreach (int next in world.Neighbors[nearby[cursor]])
                    if (seen.Add(next) && (world.Centers[next] - previewFocus).magnitude * world.Radius < 65) nearby.Add(next);
            foreach (int id in nearby)
            {
                if (world.Cells[id].Water) continue;
                float height = terrain.Surface.BaseHeight(world.Centers[id], id) - .178f;
                int tread = Mathf.RoundToInt(height / terrain.Surface.PlateauStepHeight);
                if (Mathf.Abs(height - tread * terrain.Surface.PlateauStepHeight) < .08f) treads.Add(tread);
                int form = SphericalSurface.LocalLandform(world.Cells[id].Landform);
                if (height > 1 && form == 0) previewPlain++;
                if (height > 1 && form == 2) previewMountains++;
            }
            AddCheck("Highland preview uses only one raised platform with independent local art", treads.Count > 0
                && !treads.Contains(2) && !treads.Contains(3) && !treads.Contains(4)
                && previewPlain > 0 && previewMountains > 0, treads.Count, 2,
                "Actual sphere cells within 65 map units of 83.90E, 35.97N: treads=" + treads.Count
                + ", elevated local plains=" + previewPlain + ", elevated mountains=" + previewMountains
                + ". The preset moves to an existing geographic edge; no demonstration cells were fabricated.");
        }

        static void ValidatePresentationForView(string name, SphericalTerrainPreview terrain, SphericalPreviewCamera navigation)
        {
            int bound = 0, mismatched = 0;
            float expectedBlend = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(220, 650, navigation.Altitude));
            foreach (MeshRenderer renderer in terrain.GetComponentsInChildren<MeshRenderer>())
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material || !material.HasProperty("_UseSatellite") || material.GetFloat("_UseSatellite") < .5f) continue;
                    bound++;
                    if (material.GetTexture("_SatelliteColor") != terrain.satelliteColor
                        || material.GetTexture("_SatelliteRelief") != terrain.satelliteRelief
                        || Mathf.Abs(material.GetFloat("_SatelliteBlend") - expectedBlend) > .005f) mismatched++;
                }
            AddCheck(name + " satellite and near art use the current continuous distance", bound > 0 && mismatched == 0,
                mismatched, 0, "Inspected " + bound + " visible materials; expected satellite blend=" + expectedBlend + ". Images verify the resulting art separately.");
            AddCheck(name + " terrain worker completed without a loading error", string.IsNullOrEmpty(terrain.LoadingError),
                string.IsNullOrEmpty(terrain.LoadingError) ? 0 : 1, 0, "Ready chunks=" + terrain.ReadyChunks + ", pending=" + terrain.PendingChunks
                    + ", cache hits=" + terrain.CacheHits + ", builds=" + terrain.BuildCount + ", last load seconds=" + terrain.LastLoadSeconds + ".");
            if (name == "tibet" || name == "tibet-wide")
            {
                float baseHeight = terrain.Surface.BaseHeight(navigation.FocusDirection);
                float cameraRadius = navigation.transform.position.magnitude;
                AddCheck(name + " camera remains above the raised regional ground", cameraRadius > terrain.radius + baseHeight + 10,
                    cameraRadius - terrain.radius - baseHeight, 10, "Camera offset is measured over the regional platform, with mountain peak height excluded from the orbit reference.");
            }
        }

        static IEnumerator ValidateRevisionCamera()
        {
            SphericalTerrainPreview terrain = validation.terrain;
            SphericalPreviewCamera navigation = validation.navigation;
            validation.report.activeStage = "flat-style spherical movement"; SaveValidation();
            navigation.SetView(10, 46, 85, true);
            Vector3 beforeFocus = navigation.FocusDirection;
            float beforeAltitude = navigation.Altitude;
            for (int frame = 0; frame < 60; frame++) { navigation.ApplyPan(new Vector2(1, 0), 1f / 60f); yield return null; }
            float moved = (navigation.FocusDirection - beforeFocus).magnitude * terrain.radius;
            AddCheck("WASD path moves on the sphere without changing zoom", moved > 40 && Finite(navigation.FocusDirection)
                && Mathf.Abs(navigation.FocusDirection.sqrMagnitude - 1) < .0001f && Mathf.Abs(navigation.Altitude - beforeAltitude) < .01f,
                moved, 40, "Ran the actual ApplyPan path for 60 frames at 1/60 s. Movement is measured in spherical chord world units.");
            Vector3 fixedFocus = navigation.FocusDirection;
            Quaternion beforeRotation = navigation.transform.rotation;
            Vector3 beforeSun = navigation.sun ? navigation.sun.transform.forward : Vector3.forward;
            navigation.ApplyRotation(30);
            float rotated = Quaternion.Angle(beforeRotation, navigation.transform.rotation);
            AddCheck("Q/E rotates the view while keeping its geographic focus and sun", rotated > 29 && rotated < 31
                && (navigation.FocusDirection - fixedFocus).sqrMagnitude < 1e-9f
                && (!navigation.sun || (beforeSun - navigation.sun.transform.forward).sqrMagnitude < 1e-9f),
                rotated, 30, "Called the actual 30-degree rotation path; the sun and selected geographic focus remain independent of camera bearing.");
            navigation.SetView(10, 46, 85, true); beforeFocus = navigation.FocusDirection;
            navigation.ApplyPan(Vector2.right, 1f / 30f);
            float nearMove = (navigation.FocusDirection - beforeFocus).magnitude * terrain.radius;
            navigation.SetView(10, 46, 700, true); beforeFocus = navigation.FocusDirection;
            navigation.ApplyPan(Vector2.right, 1f / 30f);
            float farMove = (navigation.FocusDirection - beforeFocus).magnitude * terrain.radius;
            float ratio = farMove / Mathf.Max(nearMove, .00001f), expected = 700f / 85f;
            AddCheck("Pan speed scales with camera distance as in the flat map", Mathf.Abs(ratio - expected) < expected * .08f,
                ratio, expected, "Compared one 1/30 s pan at altitude 85 and 700. Small streaming damping differences are permitted.");
            navigation.SetView(0, 89.98f, 85, true);
            Vector3 lastUp = navigation.transform.up; float largestTurn = 0;
            int invalid = 0;
            for (int frame = 0; frame < 12; frame++)
            {
                navigation.ApplyPan(Vector2.up, 1f / 60f);
                largestTurn = Mathf.Max(largestTurn, Vector3.Angle(lastUp, navigation.transform.up));
                if (!Finite(navigation.transform.position) || !Finite(navigation.transform.up)) invalid++;
                lastUp = navigation.transform.up; yield return null;
            }
            AddCheck("Keyboard pan reaches the polar limit without a camera flip", invalid == 0 && largestTurn < 1,
                largestTurn, 1, "Keeps geographic north fixed and stops at the polar latitude limit; largest up-vector change per frame is in degrees.");
        }
    }
}
