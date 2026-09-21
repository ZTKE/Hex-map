using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class HexGridChunk
{
	// The shipped chunks contain static meshes and world-space labels. Preserve
	// their native hierarchy when caching a published chunk: SetActive changes
	// notify editor scene-wide hierarchy listeners even when no mesh is rebuilt.
	readonly List<Component> streamingComponents = new();
	readonly List<Renderer> streamingRenderers = new();
	readonly List<bool> streamingRendererOff = new();
	readonly List<Collider> streamingColliders = new();
	readonly List<bool> streamingColliderEnabled = new();
	readonly List<Behaviour> streamingCanvasBehaviours = new();
	readonly List<bool> streamingCanvasEnabled = new();
	readonly List<HexNearVegetationMesh> streamingVegetation = new();
	readonly List<bool> streamingVegetationVisible = new();
	bool staticStreamingHidden;

	/// <summary>True when published static geometry is hidden without deactivation.</summary>
	public bool IsStaticStreamingHidden => staticStreamingHidden;

	/// <summary>
	/// Cache a generated static chunk without changing its GameObject hierarchy.
	/// Cold/incomplete chunks and custom prefabs retain normal Unity activation.
	/// </summary>
	public void SetStreamingVisible(bool visible)
	{
		if (visible)
		{
			RestoreStreamingVisibility();
			if (!gameObject.activeSelf) gameObject.SetActive(true);
			return;
		}
		if (staticStreamingHidden)
		{
			if (HasReadyGeometry) return;
			ResetStreamingVisibilityForBuild();
			return;
		}
		if (Grid && Grid.UsesChunkStreaming && HasReadyGeometry &&
			gameObject.activeSelf && CaptureStaticStreamingComponents())
		{
			staticStreamingHidden = true;
			for (int i = 0; i < streamingRenderers.Count; i++)
				streamingRenderers[i].forceRenderingOff = true;
			CaptureAndSuppressStreamingPhysics();
			for (int i = 0; i < streamingCanvasBehaviours.Count; i++)
				streamingCanvasBehaviours[i].enabled = false;
			for (int i = 0; i < streamingVegetation.Count; i++)
				streamingVegetation[i].SetStreamingVisible(false);
			return;
		}
		gameObject.SetActive(false);
	}

	// Scan at the cache boundary, never per frame. List capacity is retained;
	// rebuilding the inventory catches newly published detail renderers and
	// custom components added since the last cache visit. No partial hide occurs
	// until the entire inventory has passed the conservative static whitelist.
	bool CaptureStaticStreamingComponents()
	{
		ClearStreamingVisibilityInventory();
		GetComponentsInChildren(true, streamingComponents);
		foreach (Component component in streamingComponents)
		{
			if (!component || !IsSupportedStaticStreamingComponent(component.GetType()))
			{
				ClearStreamingVisibilityInventory();
				return false;
			}
		}
		foreach (Component component in streamingComponents)
		{
			if (component is Renderer renderer)
			{
				streamingRenderers.Add(renderer);
				streamingRendererOff.Add(renderer.forceRenderingOff);
			}
			else if (component is Collider collider)
			{
				streamingColliders.Add(collider);
				streamingColliderEnabled.Add(collider.enabled);
			}
			else if (component is Canvas || component is CanvasScaler)
			{
				Behaviour behaviour = (Behaviour)component;
				streamingCanvasBehaviours.Add(behaviour);
				streamingCanvasEnabled.Add(behaviour.enabled);
			}
			else if (component is HexNearVegetationMesh vegetation)
			{
				streamingVegetation.Add(vegetation);
				streamingVegetationVisible.Add(vegetation.StreamingVisible);
			}
		}
		return true;
	}

	static bool IsSupportedStaticStreamingComponent(Type type) =>
		type == typeof(Transform) || type == typeof(RectTransform) ||
		type == typeof(MeshFilter) || type == typeof(MeshRenderer) ||
		type == typeof(MeshCollider) || type == typeof(Canvas) ||
		type == typeof(CanvasRenderer) || type == typeof(CanvasScaler) ||
		type == typeof(Text) || type == typeof(Image) ||
		type == typeof(HexGridChunk) || type == typeof(HexFeatureManager) ||
		type == typeof(HexMesh) || type == typeof(HexReliefMesh) ||
		type == typeof(HexCoastMesh) || type == typeof(HexSurfaceCollider) ||
		type == typeof(HexHFForegroundMesh) || type == typeof(HexNearVegetationMesh);

	// This is also used around a legitimate interaction change while suspended.
	// Re-capture the new requested state afterwards, keeping hidden physics off.
	void RestoreStreamingPhysics()
	{
		if (!staticStreamingHidden) return;
		for (int i = 0; i < streamingColliders.Count; i++)
			if (streamingColliders[i])
				streamingColliders[i].enabled = streamingColliderEnabled[i];
	}

	void CaptureAndSuppressStreamingPhysics()
	{
		if (!staticStreamingHidden) return;
		for (int i = 0; i < streamingColliders.Count; i++)
		{
			Collider collider = streamingColliders[i];
			if (!collider) continue;
			streamingColliderEnabled[i] = collider.enabled;
			collider.enabled = false;
		}
	}

	void RestoreStreamingVisibility()
	{
		if (!staticStreamingHidden) return;
		for (int i = 0; i < streamingRenderers.Count; i++)
			if (streamingRenderers[i])
				streamingRenderers[i].forceRenderingOff = streamingRendererOff[i];
		RestoreStreamingPhysics();
		for (int i = 0; i < streamingCanvasBehaviours.Count; i++)
			if (streamingCanvasBehaviours[i])
				streamingCanvasBehaviours[i].enabled = streamingCanvasEnabled[i];
		for (int i = 0; i < streamingVegetation.Count; i++)
			if (streamingVegetation[i])
				streamingVegetation[i].SetStreamingVisible(streamingVegetationVisible[i]);
		staticStreamingHidden = false;
		ClearStreamingVisibilityInventory();
	}

	/// <summary>Return to the existing inactive cold-build/pool path.</summary>
	internal void ResetStreamingVisibilityForBuild()
	{
		if (!staticStreamingHidden) return;
		// Restore flags only after deactivation, so retained old meshes and
		// colliders cannot appear while the next binding is constructed.
		gameObject.SetActive(false);
		RestoreStreamingVisibility();
	}

	void ClearStreamingVisibilityInventory()
	{
		streamingComponents.Clear();
		streamingRenderers.Clear(); streamingRendererOff.Clear();
		streamingColliders.Clear(); streamingColliderEnabled.Clear();
		streamingCanvasBehaviours.Clear(); streamingCanvasEnabled.Clear();
		streamingVegetation.Clear(); streamingVegetationVisible.Clear();
	}
}
