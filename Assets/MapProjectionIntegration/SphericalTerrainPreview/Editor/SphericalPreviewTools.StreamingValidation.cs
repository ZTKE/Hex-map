using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WW2.SphericalTerrainPreview.Editor
{
    public static partial class SphericalPreviewTools
    {
        [Serializable] sealed class StreamingResult
        {
            public string name;
            public bool ready, observedPendingWork;
            public double deadlineSeconds, wallSeconds, firstVisibleSeconds, lastLoadSeconds;
            public double meanFrameMs, p95FrameMs, maximumFrameMs;
            public int frameSamples, readyChunks, pendingChunks, cacheHitsBefore, cacheHitsAfter, buildsBefore, buildsAfter;
            public float longitude, latitude, altitude, maximumCameraFrameDisplacement;
            public string timingNote = "Actual rendered-frame Time.unscaledDeltaTime while this region request completes. Includes editor scheduling and worker/publication work; no render requests or screenshots run during this sample.";
        }

        static IEnumerator ValidateRevisionStreaming()
        {
            SphericalTerrainPreview terrain = validation.terrain;
            SphericalPreviewCamera navigation = validation.navigation;
            validation.report.activeStage = "rapid region requests and warm cache return"; SaveValidation();
            bool observedPending = false;
            float movedWhileLoading = 0, movedTotal = 0;
            foreach (string preset in new[] { "europe", "eastasia", "tibet" })
            {
                navigation.ApplyPreset(preset, true);
                yield return WaitSeconds(.2);
                bool pending = terrain.DetailLoading || terrain.PendingChunks > 0;
                observedPending |= pending;
                Vector3 before = navigation.FocusDirection;
                navigation.ApplyPan(new Vector2(.4f, .8f), 1f / 30f);
                float distance = (navigation.FocusDirection - before).magnitude * terrain.radius;
                movedTotal += distance; if (pending) movedWhileLoading += distance;
                yield return WaitSeconds(.1);
            }
            navigation.ApplyPreset("europe", true);
            yield return ObserveStreamingRequest("rapid-switch-final-europe", 30);
            StreamingResult rapid = validation.report.streaming[validation.report.streaming.Count - 1];
            AddCheck("Latest region request wins after rapid Europe, East Asia and Tibet switches", rapid.ready
                && Math.Abs(rapid.longitude - 10) < .01 && Math.Abs(rapid.latitude - 46) < .01 && Math.Abs(rapid.altitude - 85) < .02,
                rapid.wallSeconds, 30, "Final requested view is Europe. The same camera keeps accepting requests while stale worker results are cancelled or retained as cached regions.");
            AddCheck("Camera movement remains available while regions are requested", movedTotal > 1 && (!observedPending || movedWhileLoading > .1f),
                movedTotal, 1, "Actual ApplyPan moved " + movedTotal + " units; " + movedWhileLoading
                + " units occurred while detail work was pending. Pending work observed=" + observedPending + ". Warm cache hits may avoid a pending state entirely.");

            // Both sides are deliberately warmed at the exact same presets.
            // The return assertion only starts once the outbound view is ready,
            // so unrelated in-flight work cannot be misreported as a cache miss.
            navigation.ApplyPreset("europe", true);
            yield return ObserveStreamingRequest("cache-warm-europe", 30);
            bool europeReady = DetailReady(terrain, navigation);
            navigation.ApplyPreset("eastasia", true);
            yield return ObserveStreamingRequest("cache-warm-eastasia", 30);
            bool eastAsiaReady = DetailReady(terrain, navigation);
            yield return WaitSeconds(.2);
            int buildsBeforeReturn = terrain.BuildCount, cacheHitsBeforeReturn = terrain.CacheHits;
            navigation.ApplyPreset("europe", true);
            yield return ObserveStreamingRequest("cache-return-europe", 30);
            StreamingResult returned = validation.report.streaming[validation.report.streaming.Count - 1];
            int rebuilds = terrain.BuildCount - buildsBeforeReturn, cacheHits = terrain.CacheHits - cacheHitsBeforeReturn;
            AddCheck("Warm Europe to East Asia to Europe return reuses generated chunks", europeReady && eastAsiaReady && returned.ready
                && rebuilds == 0 && cacheHits > 0, rebuilds, 0,
                "Return begins after both fixed presets are ready. Newly scheduled terrain builds=" + rebuilds
                + "; cache hits=" + cacheHits + "; mesh payload/budget=" + terrain.CachedMeshPayloadBytes + "/" + terrain.MeshCacheBudgetBytes
                + " bytes; return wall time=" + returned.wallSeconds + " s. Readiness deadline is a bounded acceptance wait, not a claimed performance target.");
            AddCheck("Rapid switch and cache return complete without a loading exception", string.IsNullOrEmpty(terrain.LoadingError),
                string.IsNullOrEmpty(terrain.LoadingError) ? 0 : 1, 0, "Final ready chunks=" + terrain.ReadyChunks + ", pending chunks=" + terrain.PendingChunks + ".");
            SaveValidation();
        }

        static IEnumerator ObserveStreamingRequest(string name, double deadlineSeconds)
        {
            SphericalTerrainPreview terrain = validation.terrain;
            SphericalPreviewCamera navigation = validation.navigation;
            validation.report.activeStage = name; SaveValidation();
            StreamingResult result = new()
            {
                name = name, deadlineSeconds = deadlineSeconds,
                buildsBefore = terrain.BuildCount, cacheHitsBefore = terrain.CacheHits
            };
            double start = EditorApplication.timeSinceStartup;
            int lastFrame = Time.frameCount, readyFrames = 0;
            List<float> frames = new();
            Vector3 previousCamera = navigation.transform.position;
            // Always allow an actual player-loop tick after SetView. Its terrain
            // SetFocus is published by the camera in LateUpdate, after editor update.
            yield return null;
            while (EditorApplication.timeSinceStartup - start < deadlineSeconds)
            {
                if (!string.IsNullOrEmpty(terrain.LoadingError)) break;
                if (Time.frameCount != lastFrame)
                {
                    lastFrame = Time.frameCount;
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                    result.maximumCameraFrameDisplacement = Mathf.Max(result.maximumCameraFrameDisplacement,
                        Vector3.Distance(previousCamera, navigation.transform.position));
                    previousCamera = navigation.transform.position;
                    result.observedPendingWork |= terrain.DetailLoading || terrain.PendingChunks > 0;
                    readyFrames = DetailReady(terrain, navigation) && navigation.IsSettled ? readyFrames + 1 : 0;
                }
                if (readyFrames >= 4) break;
                yield return null;
            }
            result.wallSeconds = EditorApplication.timeSinceStartup - start;
            result.ready = readyFrames >= 4 && DetailReady(terrain, navigation) && navigation.IsSettled;
            result.longitude = navigation.Longitude; result.latitude = navigation.Latitude; result.altitude = navigation.Altitude;
            result.readyChunks = terrain.ReadyChunks; result.pendingChunks = terrain.PendingChunks;
            result.buildsAfter = terrain.BuildCount; result.cacheHitsAfter = terrain.CacheHits;
            result.firstVisibleSeconds = terrain.FirstVisibleSeconds; result.lastLoadSeconds = terrain.LastLoadSeconds;
            result.frameSamples = frames.Count;
            if (frames.Count > 0)
            {
                double sum = 0; foreach (float value in frames) sum += value;
                frames.Sort(); result.meanFrameMs = sum / frames.Count; result.maximumFrameMs = frames[frames.Count - 1];
                result.p95FrameMs = frames[Mathf.Clamp(Mathf.CeilToInt(frames.Count * .95f) - 1, 0, frames.Count - 1)];
            }
            validation.report.streaming.Add(result); SaveValidation();
        }
    }
}
