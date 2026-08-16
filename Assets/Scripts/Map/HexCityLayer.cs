using System.Collections.Generic;
using HexMap.WorldData;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Lightweight map-wide city presentation. Markers are batched into one mesh;
/// visible names are drawn as screen-space labels with overlap suppression.
/// </summary>
[DisallowMultipleComponent]
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
	int cityNameFontSize = 13;

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
	GUIStyle cityNameStyle;
	GUIStyle cityNameShadowStyle;
	readonly List<CityLabelEntry> labelEntries = new();
	readonly List<Rect> occupiedLabelRects = new();

	void Awake()
	{
		grid = GetComponentInParent<HexGrid>();
		EnsureRenderer();
	}

	void OnEnable()
	{
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

	void OnGUI()
	{
		if (!showCityNames || labelEntries.Count == 0 ||
			Event.current.type != EventType.Repaint)
		{
			return;
		}

		Camera camera = Camera.main;
		if (!camera)
		{
			return;
		}
		EnsureNameStyles();
		occupiedLabelRects.Clear();
		int visibleNameCount = 0;
		GUIContent content = new();
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

			content.text = entry.name;
			Vector2 textSize = cityNameStyle.CalcSize(content);
			Rect labelRect = new(
				screenPosition.x + 6f,
				Screen.height - screenPosition.y - textSize.y * 0.55f,
				Mathf.Min(textSize.x + 8f, 240f), textSize.y + 2f);
			if (OverlapsExistingLabel(labelRect))
			{
				continue;
			}

			occupiedLabelRects.Add(labelRect);
			Rect shadowRect = labelRect;
			shadowRect.position += new Vector2(1.5f, 1.5f);
			GUI.Label(shadowRect, content, cityNameShadowStyle);
			GUI.Label(labelRect, content, cityNameStyle);
			visibleNameCount += 1;
		}
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

	void EnsureNameStyles()
	{
		if (!cityFont)
		{
			cityFont = Font.CreateDynamicFontFromOSFont(
				new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" },
				cityNameFontSize);
		}
		if (cityNameStyle != null && cityNameStyle.fontSize == cityNameFontSize &&
			cityNameStyle.normal.textColor == cityNameColor &&
			cityNameShadowStyle.normal.textColor == cityNameShadowColor)
		{
			return;
		}

		cityNameStyle = new GUIStyle(GUI.skin.label)
		{
			font = cityFont,
			fontSize = cityNameFontSize,
			fontStyle = FontStyle.Bold,
			alignment = TextAnchor.MiddleLeft,
			clipping = TextClipping.Clip,
			wordWrap = false
		};
		cityNameStyle.normal.textColor = cityNameColor;
		cityNameShadowStyle = new GUIStyle(cityNameStyle);
		cityNameShadowStyle.normal.textColor = cityNameShadowColor;
	}

	void OnDestroy()
	{
		DestroyRuntimeObject(cityMaterial);
		DestroyRuntimeObject(cityMesh);
		DestroyRuntimeObject(cityFont);
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
