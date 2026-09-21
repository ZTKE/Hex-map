using System.Collections.Generic;
using UnityEngine;

public partial class HexGrid
{
	// Runtime opt-in only: serialized gameplay and authoring scenes retain their
	// existing camera, overview, scheduler, materials and geometry by default.
	Camera externalPresentationCamera;

	/// <summary>Whether a separate presentation currently owns the streamed view.</summary>
	public bool HasExternalPresentationCamera => externalPresentationCamera;

	/// <summary>
	/// Opt into a separately driven near camera, for example one rendering into a
	/// texture composited over a satellite map. Pass null to restore legacy camera
	/// ownership. The caller owns the camera pose, projection, clear alpha, culling,
	/// and any scene-level cloud or input components.
	/// </summary>
	public void SetExternalPresentationCamera(Camera camera)
	{
		if (externalPresentationCamera == camera) return;
		externalPresentationCamera = camera;
		InvalidateStreamingWindow();
		hasFootprintCameraState = false;
		overviewHeldUnderDetail = false;
		streamingCoverActive = false;
		pendingCoverWindowExpand = false;
		if (camera)
		{
			if (overview) overview.SetVisible(false);
		}
		else if (overviewMode && usesChunkStreaming)
		{
			EnsureOverview();
			if (overview) overview.SetVisible(true);
		}
		RefreshSurfaceOverlayShaderGlobals();
	}

	/// <summary>
	/// Request the normal, budgeted detailed terrain through the external camera's
	/// frustum. Call after updating that camera's pose. Start prewarming before its
	/// render texture becomes visible; only published ready chunks render. Passing
	/// false suspends the resident window without discarding its completed meshes.
	/// The existing political overview is suppressed throughout this opt-in mode.
	/// </summary>
	public void UpdateExternalPresentationView(Vector3 focusWorldPosition,
		bool prewarmDetail, bool showFeatures, bool useGlobalOcean = false)
	{
		if (!externalPresentationCamera || chunks == null) return;
		if (overview) overview.SetVisible(false);
		overviewHeldUnderDetail = false;
		streamingCoverActive = false;
		pendingCoverWindowExpand = false;
		if (detailFeaturesVisible != showFeatures) SetDetailFeaturesVisible(showFeatures);
		SetGlobalOceanMode(useGlobalOcean && prewarmDetail);

		if (!usesChunkStreaming) return;
		if (!prewarmDetail)
		{
			if (!overviewMode || !detailChunksSuspended)
			{
				overviewMode = true;
				SuspendDetailChunksForOverview();
				RefreshSurfaceOverlayShaderGlobals();
			}
			return;
		}

		if (overviewMode)
		{
			overviewMode = false;
			currentCenterColumnIndex = -1;
			InvalidateStreamingWindow();
			RefreshSurfaceOverlayShaderGlobals();
		}
		Vector3 localFocus = transform.InverseTransformPoint(focusWorldPosition);
		if (Wrapping) CenterMap(localFocus.x);
		lastViewCameraPosition = focusWorldPosition;
		lastRequestedChunkRadii = new Vector2Int(streamingChunkRadius, streamingChunkRadius);
		hasLastViewCamera = true;
		UpdateVisibleChunks(focusWorldPosition, lastRequestedChunkRadii);
		ResumeDetailChunksAfterOverview();
	}

	/// <summary>
	/// Copy conservative world-space bounds for ready, visible desired chunks.
	/// Includes the current wrapping-column offset and the same terrain-height
	/// envelope used for stream coverage. The caller can reuse its List each frame;
	/// this method creates no temporary arrays or component inventories.
	/// </summary>
	public int CopyReadyChunkWorldBounds(List<Bounds> buffer)
	{
		if (buffer == null) return 0;
		buffer.Clear();
		if (chunks == null || overviewMode) return 0;
		EnsureFootprintHeightEnvelope();
		foreach (int index in activeChunkIndices)
		{
			if (usesChunkStreaming && !desiredChunkIndices.Contains(index)) continue;
			HexGridChunk chunk = chunks[index];
			if (!chunk || !chunk.HasReadyGeometry || !chunk.gameObject.activeInHierarchy ||
				chunk.IsStaticStreamingHidden ||
				!TryGetChunkLocalRect(index, out Vector3 min, out Vector3 max)) continue;
			min.y = footprintMinimumHeight;
			max.y = footprintMaximumHeight;
			int columnIndex = index % chunkCountX;
			Vector3 columnOffset = columns[columnIndex].localPosition;
			min += columnOffset;
			max += columnOffset;
			Bounds bounds = new(transform.TransformPoint(min), Vector3.zero);
			for (int corner = 1; corner < 8; corner++)
			{
				Vector3 point = new((corner & 1) == 0 ? min.x : max.x,
					(corner & 2) == 0 ? min.y : max.y,
					(corner & 4) == 0 ? min.z : max.z);
				bounds.Encapsulate(transform.TransformPoint(point));
			}
			buffer.Add(bounds);
		}
		return buffer.Count;
	}
}
