using System.Collections.Generic;
using HexMap.WorldData;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Lightweight map-wide city presentation. Markers are batched into one mesh;
/// visible names are drawn on a camera-space canvas with overlap suppression.
/// City names/markers show only in near and mid-near; mid overview uses
/// <c>MapCountryNameLayer</c> for capital-anchored country labels instead.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]
public sealed class HexCityLayer : MonoBehaviour
{
	const float surfaceOffset = 1.35f;

	readonly struct CityLabelEntry
	{
		public readonly string name;
		public readonly Vector3 localPosition;

		public CityLabelEntry(string name, Vector3 localPosition)
		{
			this.name = name;
			this.localPosition = localPosition;
		}
	}

	[Header("Visibility")]
	[SerializeField]
	bool showCityMarkers = true;

	[SerializeField]
	bool showCityNames = true;

	[Header("City Names")]
	[SerializeField, Range(9, 22)]
	int cityNameFontSize = 14;

	[SerializeField, Range(20, 500)]
	int maximumVisibleNames = 220;

	[SerializeField]
	Color cityNameColor = new(1f, 0.93f, 0.72f, 1f);

	[SerializeField]
	Color cityNameShadowColor = new(0.04f, 0.035f, 0.03f, 0.96f);

	HexGrid grid;
	Mesh cityMesh;
	Material cityMaterial;
	MeshRenderer cityRenderer;
	Font cityFont;
	Canvas cityNameCanvas;
	RectTransform cityNameCanvasRect;
	readonly List<CityLabelEntry> labelEntries = new();
	readonly List<Text> cityNameLabels = new();
	readonly List<Rect> occupiedLabelRects = new();

	void Awake()
	{
		grid = GetComponentInParent<HexGrid>();
		EnsureRenderer();
	}

	void OnEnable()
	{
		if (cityNameCanvas)
		{
			cityNameCanvas.enabled = true;
		}
		if (!grid)
		{
			grid = GetComponentInParent<HexGrid>();
		}
		if (grid)
		{
			grid.MapReset += Rebuild;
			grid.CityDataChanged += Rebuild;
		}
		Rebuild();
	}

	void OnDisable()
	{
		if (cityNameCanvas)
		{
			cityNameCanvas.enabled = false;
		}
		if (grid)
		{
			grid.MapReset -= Rebuild;
			grid.CityDataChanged -= Rebuild;
		}
	}

	public void Rebuild()
	{
		EnsureRenderer();
		labelEntries.Clear();
		if (!cityMesh || !cityRenderer || !grid || grid.CellData == null ||
			grid.CityCount == 0)
		{
			HideUnusedNameLabels(0);
			cityMesh?.Clear();
			if (cityRenderer)
			{
				cityRenderer.enabled = false;
			}
			return;
		}

		IReadOnlyList<WorldCityData> cities = grid.Cities;
		int copyCount = grid.Wrapping ? 3 : 1;
		int markerCount = cities.Count * copyCount;
		Vector3[] vertices = new Vector3[markerCount * 4];
		Vector2[] uvs = new Vector2[markerCount * 4];
		int[] triangles = new int[markerCount * 6];
		float worldWidth = grid.CellCountX * HexMetrics.innerDiameter;
		Vector2 corner0 = new(-1f, -1f);
		Vector2 corner1 = new(1f, -1f);
		Vector2 corner2 = new(1f, 1f);
		Vector2 corner3 = new(-1f, 1f);

		int marker = 0;
		for (int copy = 0; copy < copyCount; copy++)
		{
			float wrapOffset = grid.Wrapping ?
				(copy - 1) * worldWidth : 0f;
			for (int i = 0; i < cities.Count; i++, marker++)
			{
				int cellIndex = cities[i].targetCellIndex;
				if (cellIndex < 0 || cellIndex >= grid.CellData.Length)
				{
					continue;
				}

				Vector3 center = grid.GetSurfacePosition(cellIndex, surfaceOffset);
				center.x += wrapOffset;
				labelEntries.Add(new CityLabelEntry(cities[i].name, center));
				int vertex = marker * 4;
				vertices[vertex] = center;
				vertices[vertex + 1] = center;
				vertices[vertex + 2] = center;
				vertices[vertex + 3] = center;
				uvs[vertex] = corner0;
				uvs[vertex + 1] = corner1;
				uvs[vertex + 2] = corner2;
				uvs[vertex + 3] = corner3;

				int triangle = marker * 6;
				triangles[triangle] = vertex;
				triangles[triangle + 1] = vertex + 2;
				triangles[triangle + 2] = vertex + 1;
				triangles[triangle + 3] = vertex;
				triangles[triangle + 4] = vertex + 3;
				triangles[triangle + 5] = vertex + 2;
			}
		}

		cityMesh.Clear();
		cityMesh.vertices = vertices;
		cityMesh.uv = uvs;
		cityMesh.triangles = triangles;
		cityMesh.RecalculateBounds();
		cityRenderer.enabled = showCityMarkers;
	}

	void EnsureRenderer()
	{
		MeshFilter filter = GetComponent<MeshFilter>();
		if (!filter)
		{
			filter = gameObject.AddComponent<MeshFilter>();
		}
		cityRenderer = GetComponent<MeshRenderer>();
		if (!cityRenderer)
		{
			cityRenderer = gameObject.AddComponent<MeshRenderer>();
		}
		cityRenderer.shadowCastingMode = ShadowCastingMode.Off;
		cityRenderer.receiveShadows = false;
		cityRenderer.lightProbeUsage = LightProbeUsage.Off;
		cityRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

		if (!cityMesh)
		{
			cityMesh = new Mesh
			{
				name = "World City Marker Mesh",
				hideFlags = HideFlags.HideAndDontSave
			};
			cityMesh.MarkDynamic();
		}
		filter.sharedMesh = cityMesh;

		if (!cityMaterial)
		{
			Shader shader = Shader.Find("Hex Map/World City Marker");
			if (!shader)
			{
				Debug.LogError("World city marker shader is missing.", this);
				return;
			}
			cityMaterial = new Material(shader)
			{
				name = "World City Marker Material",
				hideFlags = HideFlags.HideAndDontSave
			};
		}
		cityRenderer.sharedMaterial = cityMaterial;
	}

	void LateUpdate()
	{
		// Near + mid-near only. Mid overview / globe / view transitions must not
		// briefly reopen city names (IsOverviewZoom is false once ViewMode leaves Flat).
		bool allowCityPresentation = !HexMapCamera.HasInstance ||
			(HexMapCamera.IsFlatPresentationActive && !HexMapCamera.IsOverviewZoom);

		if (cityRenderer)
		{
			cityRenderer.enabled = allowCityPresentation && showCityMarkers &&
				labelEntries.Count > 0;
		}

		// 城市名仅近景/中近景；中景改由国家名层负责。
		if (!allowCityPresentation || !showCityNames || labelEntries.Count == 0)
		{
			if (cityNameCanvas)
			{
				cityNameCanvas.enabled = false;
			}
			HideUnusedNameLabels(0);
			return;
		}

		Camera camera = Camera.main;
		if (!camera)
		{
			HideUnusedNameLabels(0);
			return;
		}
		EnsureNameCanvas(camera);
		if (cityNameCanvas)
		{
			cityNameCanvas.enabled = true;
		}
		occupiedLabelRects.Clear();
		int visibleNameCount = 0;
		for (int i = 0; i < labelEntries.Count &&
			visibleNameCount < maximumVisibleNames; i++)
		{
			CityLabelEntry entry = labelEntries[i];
			Vector3 worldPosition = grid.transform.TransformPoint(entry.localPosition);
			Vector3 screenPosition = camera.WorldToScreenPoint(worldPosition);
			if (screenPosition.z <= 0f || screenPosition.x < -40f ||
				screenPosition.x > Screen.width + 40f ||
				screenPosition.y < -20f || screenPosition.y > Screen.height + 20f)
			{
				continue;
			}

			Text label = GetNameLabel(visibleNameCount);
			if (label.text != entry.name)
			{
				label.text = entry.name;
			}
			Vector2 textSize = new(
				Mathf.Min(label.preferredWidth + 8f, 240f),
				label.preferredHeight + 2f);
			Rect labelRect = new(
				screenPosition.x + 6f,
				screenPosition.y - textSize.y * 0.5f,
				textSize.x, textSize.y);
			if (OverlapsExistingLabel(labelRect))
			{
				continue;
			}

			occupiedLabelRects.Add(labelRect);
			RectTransform labelRectTransform = label.rectTransform;
			labelRectTransform.sizeDelta = textSize;
			Rect canvasRect = cityNameCanvasRect.rect;
			Rect cameraRect = camera.pixelRect;
			float viewportX = cameraRect.width > 0f ?
				(screenPosition.x - cameraRect.x) / cameraRect.width : 0.5f;
			float viewportY = cameraRect.height > 0f ?
				(screenPosition.y - cameraRect.y) / cameraRect.height : 0.5f;
			labelRectTransform.anchoredPosition = new Vector2(
				canvasRect.xMin + viewportX * canvasRect.width + 6f,
				canvasRect.yMin + viewportY * canvasRect.height);
			if (!label.enabled)
			{
				label.enabled = true;
			}
			visibleNameCount += 1;
		}
		HideUnusedNameLabels(visibleNameCount);
	}

	bool OverlapsExistingLabel(Rect candidate)
	{
		Rect padded = new(
			candidate.x - 3f, candidate.y - 1f,
			candidate.width + 6f, candidate.height + 2f);
		for (int i = 0; i < occupiedLabelRects.Count; i++)
		{
			if (padded.Overlaps(occupiedLabelRects[i]))
			{
				return true;
			}
		}
		return false;
	}

	void EnsureNameCanvas(Camera camera)
	{
		if (!cityFont)
		{
			cityFont = Font.CreateDynamicFontFromOSFont(
				new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" },
				cityNameFontSize);
		}
		if (!cityNameCanvas)
		{
			GameObject canvasObject = new("World City Name Canvas",
				typeof(RectTransform), typeof(Canvas));
			canvasObject.layer = gameObject.layer;
			if (gameObject.scene.IsValid())
			{
				SceneManager.MoveGameObjectToScene(canvasObject, gameObject.scene);
			}
			cityNameCanvas = canvasObject.GetComponent<Canvas>();
			cityNameCanvasRect = canvasObject.GetComponent<RectTransform>();
			cityNameCanvas.renderMode = RenderMode.ScreenSpaceCamera;
			cityNameCanvas.overrideSorting = true;
			cityNameCanvas.sortingOrder = -100;
			cityNameCanvas.pixelPerfect = true;
		}
		if (cityNameCanvas.worldCamera != camera)
		{
			cityNameCanvas.worldCamera = camera;
			cityNameCanvas.planeDistance = Mathf.Max(1f, camera.nearClipPlane + 0.5f);
		}
	}

	Text GetNameLabel(int index)
	{
		while (cityNameLabels.Count <= index)
		{
			GameObject labelObject = new("City Name",
				typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Shadow));
			labelObject.layer = cityNameCanvas.gameObject.layer;
			labelObject.transform.SetParent(cityNameCanvasRect, false);
			RectTransform rectTransform = labelObject.GetComponent<RectTransform>();
			rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
			rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
			rectTransform.pivot = new Vector2(0f, 0.5f);
			Text label = labelObject.GetComponent<Text>();
			label.raycastTarget = false;
			label.maskable = false;
			label.supportRichText = false;
			label.alignment = TextAnchor.MiddleLeft;
			label.horizontalOverflow = HorizontalWrapMode.Overflow;
			label.verticalOverflow = VerticalWrapMode.Overflow;
			SetNameLabelVisuals(label);
			cityNameLabels.Add(label);
		}
		return cityNameLabels[index];
	}

	void SetNameLabelVisuals(Text label)
	{
		label.font = cityFont;
		label.fontSize = cityNameFontSize;
		label.fontStyle = FontStyle.Bold;
		label.color = cityNameColor;
		Shadow shadow = label.GetComponent<Shadow>();
		shadow.effectColor = cityNameShadowColor;
		shadow.effectDistance = new Vector2(1.5f, -1.5f);
		shadow.useGraphicAlpha = true;
	}

	void HideUnusedNameLabels(int firstUnusedIndex)
	{
		// Keep the pooled hierarchy stable as city counts change during a pan.
		// Text owns both its glyphs and Shadow geometry; disabling it hides both.
		for (int i = firstUnusedIndex; i < cityNameLabels.Count; i++)
		{
			if (cityNameLabels[i].enabled)
			{
				cityNameLabels[i].enabled = false;
			}
		}
	}

	void OnDestroy()
	{
		DestroyRuntimeObject(cityMaterial);
		DestroyRuntimeObject(cityMesh);
		DestroyRuntimeObject(cityFont);
		if (cityNameCanvas)
		{
			DestroyRuntimeObject(cityNameCanvas.gameObject);
		}
	}

	static void DestroyRuntimeObject(Object value)
	{
		if (!value)
		{
			return;
		}
		if (Application.isPlaying)
		{
			Destroy(value);
		}
		else
		{
			DestroyImmediate(value);
		}
	}
}
