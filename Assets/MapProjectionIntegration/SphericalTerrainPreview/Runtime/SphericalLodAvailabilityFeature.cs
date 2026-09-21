using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>Actual unclipped terrain coverage, per camera MSAA sample.</summary>
    public sealed class SphericalLodAvailabilityFeature : ScriptableRendererFeature
    {
        [NonSerialized] public SphericalTerrainPreview Owner;
        AvailabilityPass pass;
        public override void Create()
        {
            pass?.Dispose();
            pass = new AvailabilityPass { renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses };
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            var navigation = camera ? camera.GetComponent<SphericalPreviewCamera>() : null;
            if (!Owner || !Owner.IsReady || !navigation || navigation.terrain != Owner || Owner.VisibleDetailCells == 0) return;
            renderer.EnqueuePass(pass);
        }
        protected override void Dispose(bool disposing) { pass?.Dispose(); pass = null; }

        sealed class AvailabilityPass : ScriptableRenderPass
        {
            static readonly ShaderTagId Tag = new("SphericalLodAvailability");
            static readonly int Enabled = Shader.PropertyToID("_SphericalLodAvailabilityEnabled");
            static readonly int Samples = Shader.PropertyToID("_SphericalLodAvailabilitySamples");
            static readonly int DepthSamples = Shader.PropertyToID("_SphericalLodDepthSamples");
            static readonly int Size = Shader.PropertyToID("_SphericalLodAvailabilitySize");
            static readonly int Texture = Shader.PropertyToID("_SphericalLodAvailability");
            static readonly int TextureMs = Shader.PropertyToID("_SphericalLodAvailabilityMS");
            static readonly int InverseViewProjection = Shader.PropertyToID("_SphericalLodInverseViewProjection");
            static readonly PropertyInfo DepthPriming = typeof(ScriptableRenderer).GetProperty("useDepthPriming", BindingFlags.Instance | BindingFlags.NonPublic);
            RTHandle availability, singleSampleAvailability;
            int samples, depthSamples, width, height;

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                width = descriptor.width; height = descriptor.height; samples = descriptor.msaaSamples;
                if (samples != 1 && samples != 2 && samples != 4 && samples != 8)
                    throw new InvalidOperationException("Spherical terrain LOD availability requires 1, 2, 4 or 8 camera samples.");
                // No depth: each channel is the union of all actual rasterized
                // front faces of its LOD, before either layer clips. This must
                // preserve the camera's sample positions, including silhouettes.
                descriptor.depthBufferBits = 0; descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.graphicsFormat = GraphicsFormat.R8G8_UNorm;
                descriptor.bindMS = samples > 1;
                descriptor.useMipMap = false; descriptor.autoGenerateMips = false;
                if (!SystemInfo.IsFormatSupported(descriptor.graphicsFormat, FormatUsage.Render)
                    || !SystemInfo.IsFormatSupported(descriptor.graphicsFormat, FormatUsage.Blend)
                    || SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != samples)
                    descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor) != samples)
                    throw new InvalidOperationException("Terrain LOD mask cannot match the camera MSAA sample count; no approximate availability mask was substituted.");
                RenderingUtils.ReAllocateIfNeeded(ref availability, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "Spherical terrain fine/coarse sample availability");
                // URP 14's ordinary depth/normal prepass is single-sampled even
                // when forward color uses MSAA. Its pixel-center geometry must
                // be rasterized independently, not inferred from sample zero.
                if (DepthPriming == null) throw new InvalidOperationException("Cannot inspect URP depth priming for exact LOD sample coverage.");
                bool priming = (bool)DepthPriming.GetValue(renderingData.cameraData.renderer);
                depthSamples = priming ? samples : 1;
                if (samples > 1 && depthSamples == 1)
                {
                    descriptor.msaaSamples = 1; descriptor.bindMS = false;
                    RenderingUtils.ReAllocateIfNeeded(ref singleSampleAvailability, descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                        name: "Spherical terrain pixel-center depth availability");
                }
                ConfigureTarget(availability);
                ConfigureClear(ClearFlag.Color, Color.clear);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get("Spherical terrain LOD availability");
                try
                {
                    // The pass's custom tag exists only on the spherical terrain
                    // shader. It neither changes water nor uses an override
                    // material that could accidentally include other geometry.
                    var drawing = CreateDrawingSettings(Tag, ref renderingData, SortingCriteria.None);
                    drawing.perObjectData = PerObjectData.None;
                    var filtering = new FilteringSettings(RenderQueueRange.opaque);
                    context.DrawRenderers(renderingData.cullResults, ref drawing, ref filtering);
                    if (samples > 1 && depthSamples == 1)
                    {
                        CoreUtils.SetRenderTarget(cmd, singleSampleAvailability, ClearFlag.Color, Color.clear);
                        context.ExecuteCommandBuffer(cmd); cmd.Clear();
                        context.DrawRenderers(renderingData.cullResults, ref drawing, ref filtering);
                        cmd.SetGlobalTexture(Texture, singleSampleAvailability.nameID);
                    }
                    var camera = renderingData.cameraData;
                    var inverse = (camera.GetGPUProjectionMatrix() * camera.GetViewMatrix()).inverse;
                    cmd.SetGlobalMatrix(InverseViewProjection, inverse);
                    cmd.SetGlobalVector(Size, new Vector4(width, height, 1f / width, 1f / height));
                    cmd.SetGlobalInt(Samples, samples);
                    cmd.SetGlobalInt(DepthSamples, depthSamples);
                    cmd.SetGlobalTexture(samples > 1 ? TextureMs : Texture, availability.nameID);
                    cmd.SetGlobalFloat(Enabled, 1);
                    context.ExecuteCommandBuffer(cmd);
                }
                finally { CommandBufferPool.Release(cmd); }
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                // Native Game View and SubmitRenderRequest may render the same
                // camera at different sizes. Never expose an earlier camera's
                // mask to Scene View, another scene, or an interrupted render.
                cmd.SetGlobalFloat(Enabled, 0);
            }
            public void Dispose()
            {
                availability?.Release(); availability = null;
                singleSampleAvailability?.Release(); singleSampleAvailability = null;
            }
        }
    }
}
