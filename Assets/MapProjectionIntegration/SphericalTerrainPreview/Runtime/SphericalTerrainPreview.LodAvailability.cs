using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        readonly List<ScriptableRendererData> lodRendererClones = new();
        readonly List<ScriptableRendererFeature> lodFeatureClones = new();

        void InstallLodAvailabilityRenderer()
        {
            // URP 14 exposes rendererFeatures, but not the pipeline's renderer
            // data array. Only replace the array on our newly made pipeline;
            // neither the saved URP asset nor any source feature is mutated.
            var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);
            var sources = field?.GetValue(previewPipeline) as ScriptableRendererData[];
            if (sources == null || sources.Length == 0)
                throw new InvalidOperationException("Cannot locate URP renderer data for the isolated terrain LOD availability pass.");
            var clones = new ScriptableRendererData[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                if (!sources[i]) continue;
                var clone = Instantiate(sources[i]); clone.name = sources[i].name + " (spherical terrain runtime)";
                clone.hideFlags = HideFlags.DontSave; clones[i] = clone; lodRendererClones.Add(clone);
                clone.rendererFeatures.Clear();
                foreach (var sourceFeature in sources[i].rendererFeatures)
                {
                    if (!sourceFeature) continue;
                    var feature = Instantiate(sourceFeature); feature.hideFlags = HideFlags.DontSave;
                    clone.rendererFeatures.Add(feature); lodFeatureClones.Add(feature);
                }
                var availability = ScriptableObject.CreateInstance<SphericalLodAvailabilityFeature>();
                availability.name = "Spherical terrain actual LOD availability"; availability.hideFlags = HideFlags.DontSave;
                availability.Owner = this; availability.Create();
                clone.rendererFeatures.Add(availability); lodFeatureClones.Add(availability);
            }
            field.SetValue(previewPipeline, clones);
        }

        void DisposeLodAvailabilityRenderer()
        {
            foreach (var feature in lodFeatureClones) if (feature) { feature.Dispose(); Destroy(feature); }
            foreach (var data in lodRendererClones) if (data) Destroy(data);
            lodFeatureClones.Clear(); lodRendererClones.Clear();
        }
    }
}
