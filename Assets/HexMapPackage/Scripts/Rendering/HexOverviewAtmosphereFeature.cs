using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Fullscreen parchment grade, breath, and soft bloom while the hex overview is active.
/// </summary>
public sealed class HexOverviewAtmosphereFeature : ScriptableRendererFeature
{
	[System.Serializable]
	public class Settings
	{
		public RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingPostProcessing;
		public Shader shader;
	}

	public Settings settings = new Settings();

	HexOverviewAtmospherePass atmospherePass;
	Material atmosphereMaterial;

	public override void Create()
	{
		atmospherePass = new HexOverviewAtmospherePass(settings.passEvent);
		if (settings.shader != null)
		{
			atmosphereMaterial = CoreUtils.CreateEngineMaterial(settings.shader);
			HexOverviewAtmosphereDefaults.Apply(atmosphereMaterial);
		}
	}

	public override void AddRenderPasses(
		ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		if (atmosphereMaterial == null && settings.shader != null)
		{
			atmosphereMaterial = CoreUtils.CreateEngineMaterial(settings.shader);
		}

		if (atmosphereMaterial != null)
		{
			// Re-apply each frame so style tweaks take effect without recreating URP.
			HexOverviewAtmosphereDefaults.Apply(atmosphereMaterial);
		}

		if (!HexOverviewAtmosphere.Active ||
			atmosphereMaterial == null ||
			renderingData.cameraData.cameraType != CameraType.Game ||
			UniversalRenderer.IsOffscreenDepthTexture(in renderingData.cameraData))
		{
			return;
		}

		atmospherePass.Setup(atmosphereMaterial);
		renderer.EnqueuePass(atmospherePass);
	}

	protected override void Dispose(bool disposing)
	{
		CoreUtils.Destroy(atmosphereMaterial);
		atmospherePass?.Dispose();
	}

	sealed class HexOverviewAtmospherePass : ScriptableRenderPass
	{
		static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
		static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");

		Material material;
		RTHandle copiedColor;
		MaterialPropertyBlock propertyBlock;

		public HexOverviewAtmospherePass(RenderPassEvent passEvent)
		{
			renderPassEvent = passEvent;
			profilingSampler = new ProfilingSampler("Hex Overview Atmosphere");
			propertyBlock = new MaterialPropertyBlock();
			// Auto intermediate-texture mode must provide a sampleable camera color.
			ConfigureInput(ScriptableRenderPassInput.Color);
		}

		public void Setup(Material targetMaterial)
		{
			material = targetMaterial;
		}

		public override void OnCameraSetup(
			CommandBuffer cmd, ref RenderingData renderingData)
		{
			ResetTarget();
			RenderTextureDescriptor descriptor =
				renderingData.cameraData.cameraTargetDescriptor;
			descriptor.msaaSamples = 1;
			descriptor.depthBufferBits = 0;
			RenderingUtils.ReAllocateIfNeeded(
				ref copiedColor, descriptor, name: "_HexOverviewAtmosphereCopy");
		}

		public override void Execute(
			ScriptableRenderContext context, ref RenderingData renderingData)
		{
			if (!material)
			{
				return;
			}

			CommandBuffer cmd = CommandBufferPool.Get();
			RTHandle cameraColor =
				renderingData.cameraData.renderer.cameraColorTargetHandle;

			using (new ProfilingScope(cmd, profilingSampler))
			{
				Blitter.BlitCameraTexture(
					cmd, cameraColor, copiedColor, 0f, false);
				CoreUtils.SetRenderTarget(cmd, cameraColor);

				propertyBlock.Clear();
				propertyBlock.SetTexture(BlitTextureId, copiedColor);
				propertyBlock.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
				cmd.DrawProcedural(
					Matrix4x4.identity, material, 0,
					MeshTopology.Triangles, 3, 1, propertyBlock);
			}

			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}

		public void Dispose()
		{
			copiedColor?.Release();
		}
	}
}
