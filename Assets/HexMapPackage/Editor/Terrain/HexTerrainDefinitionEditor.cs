using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Honey-style authoring window for this project's data-driven HF terrain
/// style. The ground and forest tabs deliberately remain separate because the
/// runtime map editor paints those properties independently.
/// </summary>
public sealed class HexTerrainDefinitionEditor : EditorWindow
{
	const string DefaultStylePath =
		"Assets/HexMapPackage/Materials/Terrain/Default Hex Terrain Style.asset";

	readonly struct SurfaceDefinition
	{
		public readonly string name;
		public readonly string role;
		public readonly string diffuse;
		public readonly string height;
		public readonly string mixer;

		public SurfaceDefinition(
			string name, string role, string diffuse, string height, string mixer)
		{
			this.name = name;
			this.role = role;
			this.diffuse = diffuse;
			this.height = height;
			this.mixer = mixer;
		}
	}

	static readonly SurfaceDefinition[] Surfaces =
	{
		new("Dirt", "Desert / dry ground", "hfDirtDiffuse", "hfDirtHeight", "hfDirtMixer"),
		new("Plains", "Grass and plains ground", "hfPlainsDiffuse", "hfCommonHeight", "hfPlainsMixer"),
		new("Marsh", "Wet / tundra ground", "hfMarshDiffuse", "hfCommonHeight", "hfMarshMixer"),
		new("Hill", "Hill relief stamp", "hfHillDiffuse", "hfHillHeight", "hfHillMixer"),
		new("Mountain", "Mountain relief stamp", "hfMountainDiffuse", "hfMountainHeight", "hfMountainMixer"),
		new("Sea Border", "Shoreline and seabed stamp", "hfSeaDiffuse", "hfSeaHeight", "hfSeaMixer"),
		new("River", "River channel stamp", "hfRiverDiffuse", "hfRiverHeight", "hfRiverOriginalMixer")
	};

	static readonly string[] Tabs =
	{
		"Terrain Surfaces", "Forest Profiles", "Advanced"
	};

	static readonly string[] VegetationNames =
	{
		"Natural Mix", "Broadleaf", "Sapling", "Conifer", "Deadwood", "Cold Mix"
	};

	static readonly Dictionary<HexHFForegroundSprite, Texture2D> SpritePreviews =
		new();

	[SerializeField] HexTerrainStyle terrainStyle;
	[SerializeField] int selectedTab;
	[SerializeField] Vector2 scroll;
	[SerializeField] bool autoRebuildInPlayMode = true;

	SerializedObject serializedStyle;

	[MenuItem("Window/Hex Map/Terrain Definitions")]
	public static void Open()
	{
		HexTerrainDefinitionEditor window = GetWindow<HexTerrainDefinitionEditor>();
		window.titleContent = new GUIContent("Terrain Definitions");
		window.minSize = new Vector2(720f, 420f);
		window.Show();
	}

	void OnEnable()
	{
		titleContent = new GUIContent("Terrain Definitions");
		minSize = new Vector2(720f, 420f);
		Undo.undoRedoPerformed += OnUndoRedo;
		if (!terrainStyle)
		{
			terrainStyle = AssetDatabase.LoadAssetAtPath<HexTerrainStyle>(
				DefaultStylePath);
		}
		CreateSerializedStyle();
	}

	void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

	void OnUndoRedo()
	{
		CreateSerializedStyle();
		RebuildOpenMaps(false);
		Repaint();
	}

	void OnGUI()
	{
		DrawHeader();
		if (!terrainStyle || serializedStyle == null)
		{
			EditorGUILayout.HelpBox(
				"Assign a HexTerrainStyle asset to edit its HF terrain definitions.",
				MessageType.Info);
			return;
		}

		serializedStyle.UpdateIfRequiredOrScript();
		selectedTab = GUILayout.Toolbar(selectedTab, Tabs, GUILayout.Height(26f));
		EditorGUILayout.Space(4f);

		EditorGUI.BeginChangeCheck();
		scroll = EditorGUILayout.BeginScrollView(scroll);
		switch (selectedTab)
		{
			case 0:
				DrawTerrainSurfaces();
				break;
			case 1:
				DrawForestProfiles();
				break;
			default:
				DrawAdvancedSettings();
				break;
		}
		EditorGUILayout.EndScrollView();

		bool changed = EditorGUI.EndChangeCheck();
		changed |= serializedStyle.ApplyModifiedProperties();
		if (changed)
		{
			EditorUtility.SetDirty(terrainStyle);
			if (autoRebuildInPlayMode && EditorApplication.isPlaying)
			{
				RebuildOpenMaps(false);
			}
		}
	}

	void DrawHeader()
	{
		EditorGUILayout.BeginVertical(EditorStyles.helpBox);
		EditorGUILayout.BeginHorizontal();
		HexTerrainStyle selected = (HexTerrainStyle)EditorGUILayout.ObjectField(
			new GUIContent("Terrain Style"), terrainStyle,
			typeof(HexTerrainStyle), false);
		if (selected != terrainStyle)
		{
			terrainStyle = selected;
			CreateSerializedStyle();
			GUIUtility.ExitGUI();
		}

		if (GUILayout.Button("Use Default", GUILayout.Width(88f)))
		{
			terrainStyle = AssetDatabase.LoadAssetAtPath<HexTerrainStyle>(
				DefaultStylePath);
			CreateSerializedStyle();
			GUIUtility.ExitGUI();
		}

		GUI.enabled = terrainStyle;
		if (GUILayout.Button("Ping", GUILayout.Width(52f)))
		{
			Selection.activeObject = terrainStyle;
			EditorGUIUtility.PingObject(terrainStyle);
		}
		if (GUILayout.Button("Save", GUILayout.Width(52f)))
		{
			AssetDatabase.SaveAssets();
		}
		if (GUILayout.Button("Rebuild Open Map", GUILayout.Width(124f)))
		{
			RebuildOpenMaps(true);
		}
		GUI.enabled = true;
		EditorGUILayout.EndHorizontal();

		EditorGUILayout.BeginHorizontal();
		autoRebuildInPlayMode = EditorGUILayout.ToggleLeft(
			"Auto rebuild while Play Mode is running",
			autoRebuildInPlayMode, GUILayout.Width(240f));
		GUILayout.FlexibleSpace();
		if (terrainStyle)
		{
			GUIStyle statusStyle = new(EditorStyles.miniLabel)
			{
				normal =
				{
					textColor = terrainStyle.HasHFOriginalTerrainSet() ?
						new Color(0.35f, 0.75f, 0.35f) :
						new Color(1f, 0.55f, 0.3f)
				}
			};
			GUILayout.Label(
				terrainStyle.HasHFOriginalTerrainSet() ?
					"HF texture set complete" : "HF texture set incomplete",
				statusStyle);
		}
		EditorGUILayout.EndHorizontal();
		EditorGUILayout.EndVertical();
	}

	void DrawTerrainSurfaces()
	{
		EditorGUILayout.HelpBox(
			"These cards edit the same Diffuse / Height / Mixer triplets used by " +
			"the HF surface sampler and shaders. Plains and Marsh intentionally " +
			"share Common Height, matching the original Honey definitions.",
			MessageType.None);

		float availableWidth = Mathf.Max(360f, position.width - 36f);
		int columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / 390f));
		float cardWidth = availableWidth / columns - 8f;
		for (int first = 0; first < Surfaces.Length; first += columns)
		{
			EditorGUILayout.BeginHorizontal();
			for (int column = 0; column < columns; column++)
			{
				int index = first + column;
				if (index >= Surfaces.Length)
				{
					break;
				}
				DrawSurfaceCard(Surfaces[index], cardWidth);
			}
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();
		}

		EditorGUILayout.Space(6f);
		EditorGUILayout.LabelField("Shared HF Bake Controls", EditorStyles.boldLabel);
		EditorGUILayout.BeginVertical(EditorStyles.helpBox);
		DrawProperty("surfaceMode", "Surface Mode");
		DrawProperty("hfOriginalStampScale", "Stamp Scale");
		DrawProperty("hfOriginalHeightScale", "Height Scale");
		DrawProperty("hfOriginalHeightLod", "Height LOD / Blur");
		DrawProperty("hfRiverCarve", "River Carve Range");
		EditorGUILayout.EndVertical();
	}

	void DrawSurfaceCard(SurfaceDefinition surface, float width)
	{
		EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(width));
		EditorGUILayout.LabelField(surface.name, EditorStyles.boldLabel);
		EditorGUILayout.LabelField(surface.role, EditorStyles.miniLabel);
		EditorGUILayout.Space(2f);
		DrawTextureProperty(surface.diffuse, "Diffuse");
		DrawTextureProperty(surface.height, "Height");
		DrawTextureProperty(surface.mixer, "Mixer");
		EditorGUILayout.EndVertical();
	}

	void DrawTextureProperty(string propertyName, string label)
	{
		SerializedProperty property = serializedStyle.FindProperty(propertyName);
		if (property == null)
		{
			EditorGUILayout.LabelField(label, "Missing serialized property");
			return;
		}

		EditorGUILayout.BeginHorizontal();
		EditorGUILayout.PropertyField(property, new GUIContent(label));
		Rect previewRect = GUILayoutUtility.GetRect(
			54f, 54f, GUILayout.Width(54f), GUILayout.Height(54f));
		Texture texture = property.objectReferenceValue as Texture;
		if (texture)
		{
			EditorGUI.DrawPreviewTexture(previewRect, texture, null, ScaleMode.ScaleToFit);
		}
		else
		{
			GUI.Box(previewRect, "None", EditorStyles.helpBox);
		}
		EditorGUILayout.EndHorizontal();
	}

	void DrawForestProfiles()
	{
		EditorGUILayout.BeginHorizontal();
		EditorGUILayout.HelpBox(
			"Each profile controls the atlas sprites emitted at 100% Forest density. " +
			"The map's density slider scales these counts, and its Theme setting " +
			"multiplies the base tints.", MessageType.None);
		if (GUILayout.Button("Reset Honey Defaults", GUILayout.Width(150f), GUILayout.Height(38f)))
		{
			ResetForestProfiles();
		}
		EditorGUILayout.EndHorizontal();

		SerializedProperty profiles = serializedStyle.FindProperty("foregroundProfiles");
		if (profiles == null)
		{
			EditorGUILayout.HelpBox(
				"The selected style does not expose foreground profiles.",
				MessageType.Error);
			return;
		}

		float availableWidth = Mathf.Max(420f, position.width - 36f);
		int columns = Mathf.Max(1, Mathf.FloorToInt(availableWidth / 465f));
		float cardWidth = availableWidth / columns - 8f;
		for (int first = 0; first < profiles.arraySize; first += columns)
		{
			EditorGUILayout.BeginHorizontal();
			for (int column = 0; column < columns; column++)
			{
				int index = first + column;
				if (index >= profiles.arraySize)
				{
					break;
				}
				DrawForestCard(profiles.GetArrayElementAtIndex(index), index, cardWidth);
			}
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();
		}
	}

	void DrawForestCard(SerializedProperty profile, int index, float width)
	{
		EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(width));
		string title = index < VegetationNames.Length ?
			VegetationNames[index] : $"Profile {index + 1}";
		EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
		EditorGUILayout.PropertyField(
			profile.FindPropertyRelative("displayName"), new GUIContent("Display Name"));

		SerializedProperty entries = profile.FindPropertyRelative("entries");
		int deleteIndex = -1;
		for (int i = 0; i < entries.arraySize; i++)
		{
			SerializedProperty entry = entries.GetArrayElementAtIndex(i);
			SerializedProperty sprite = entry.FindPropertyRelative("sprite");
			SerializedProperty count = entry.FindPropertyRelative("denseCount");
			SerializedProperty tint = entry.FindPropertyRelative("baseTint");

			EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
			Rect previewRect = GUILayoutUtility.GetRect(
				42f, 42f, GUILayout.Width(42f), GUILayout.Height(42f));
			DrawSpritePreview(
				previewRect, (HexHFForegroundSprite)sprite.enumValueIndex);
			EditorGUILayout.BeginVertical();
			EditorGUILayout.PropertyField(sprite, GUIContent.none);
			count.intValue = Mathf.Max(0, EditorGUILayout.IntField(
				new GUIContent("Dense Count", "Sprites emitted at 100% density."),
				count.intValue));
			EditorGUILayout.PropertyField(tint, new GUIContent("Base Tint"));
			EditorGUILayout.EndVertical();
			if (GUILayout.Button("x", GUILayout.Width(22f)))
			{
				deleteIndex = i;
			}
			EditorGUILayout.EndHorizontal();
		}

		if (deleteIndex >= 0)
		{
			entries.DeleteArrayElementAtIndex(deleteIndex);
		}
		if (GUILayout.Button("+ Add Tree Entry"))
		{
			int newIndex = entries.arraySize;
			entries.arraySize++;
			SerializedProperty entry = entries.GetArrayElementAtIndex(newIndex);
			entry.FindPropertyRelative("sprite").enumValueIndex = 0;
			entry.FindPropertyRelative("denseCount").intValue = 10;
			entry.FindPropertyRelative("baseTint").colorValue = Color.white;
		}
		EditorGUILayout.EndVertical();
	}

	void DrawAdvancedSettings()
	{
		DrawNearTerrainControls();
		EditorGUILayout.HelpBox(
			"Advanced style controls are shared by Hex, the terrain-editing scene, " +
			"and Game_2, the gameplay scene. Change these only when adjusting the " +
			"rendering pipeline rather than an individual map cell.",
			MessageType.Info);

		DrawSection("Surface and interaction",
			"surfaceMode", "hfColliderSubdivisions", "hfOverlaySubdivisionLevels",
			"hfRoadSurfaceOffset", "hfRiverSurfaceOffset");
		DrawSection("Realtime mixer",
			"hfTerrainMixer", "hfRiverMixer", "hfStampScale",
			"hfTerrainBlend", "hfReliefFootprint", "hfRiverMixerStrength");
		DrawSection("Material sources",
			"terrainSurfaceAtlas", "terrainSurfaceBlend", "terrainSurfaceTiling",
			"terrainMacroVariation", "rockAlbedo", "strataAlbedo", "rockNormal",
			"mountainColorDecal");
		DrawSection("Relief scale",
			"hillHeight", "mountainHeight", "mountainWidth",
			"desertMountainHeight", "desertMountainWidth");
		DrawSection("Water, coast, and river",
			"deepOcean", "shallowWater", "shoreFoam", "wetSand", "drySand",
			"riverWater", "riverBank", "riverFlowSpeed", "riverWaveFrequency",
			"riverWaveStrength", "riverSmoothness", "riverMouthLength",
			"riverMouthWidth", "riverMouthTint", "riverMouthFoam",
			"waterStyleBlend", "coastCliffLow", "coastCliffHigh",
			"snowCoastCliff", "coastCliffStrata");
		DrawSection("Biome and mountain definitions",
			"plantTints", "vegetationStyleBlend", "biomeStyles", "mountainModules");
	}

	void DrawSection(string title, params string[] propertyNames)
	{
		EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
		EditorGUILayout.BeginVertical(EditorStyles.helpBox);
		foreach (string propertyName in propertyNames)
		{
			DrawProperty(propertyName, ObjectNames.NicifyVariableName(propertyName));
		}
		EditorGUILayout.EndVertical();
		EditorGUILayout.Space(4f);
	}

	void DrawNearTerrainControls()
	{
		DrawSection("Near terrain art", "nearTerrainProfile");
		HexNearTerrainProfile profile = terrainStyle.nearTerrainProfile;
		if (!profile) return;
		var art = new SerializedObject(profile);
		art.Update();
		EditorGUILayout.LabelField("Mountain ranges and desert dunes", EditorStyles.boldLabel);
		EditorGUILayout.HelpBox("Paint Automatic / Continuous Range / Mountain Massif under Relief > Mountain in the map editor. " +
			"These controls set their shared appearance. Dunes also appear on flat desert.", MessageType.Info);
		foreach (string name in new[] { "mountainChainSaddle", "mountainMassifSaddle", "mountainChainWidth",
			"mountainRangeStrength", "mountainRangeWidth", "desertDuneHeight", "desertHillDuneHeight",
			"desertDuneWavelength", "desertDuneIrregularity", "desertDuneWindAngle" })
			EditorGUILayout.PropertyField(art.FindProperty(name));
		if (art.ApplyModifiedProperties()) { EditorUtility.SetDirty(profile); profile.ApplyGlobals(); }
		if (!profile.vegetation) return;
		var vegetation = new SerializedObject(profile.vegetation);
		vegetation.Update();
		EditorGUILayout.LabelField("Desert cactus clusters", EditorStyles.boldLabel);
		foreach (string name in new[] { "desertAccents", "desertAccentDensity", "desertAccentCoverage",
			"desertAccentMaxSlope", "desertAccentClusterRadius" })
		{
			SerializedProperty property = vegetation.FindProperty(name);
			if (property != null) EditorGUILayout.PropertyField(property, true);
		}
		if (vegetation.ApplyModifiedProperties()) EditorUtility.SetDirty(profile.vegetation);
	}

	void DrawProperty(string propertyName, string label)
	{
		SerializedProperty property = serializedStyle.FindProperty(propertyName);
		if (property != null)
		{
			EditorGUILayout.PropertyField(property, new GUIContent(label), true);
		}
	}

	void ResetForestProfiles()
	{
		if (!EditorUtility.DisplayDialog(
			"Reset forest profiles?",
			"This replaces all editable tree recipes with the original Honey-derived defaults.",
			"Reset", "Cancel"))
		{
			return;
		}

		Undo.RecordObject(terrainStyle, "Reset HF Forest Profiles");
		terrainStyle.ResetForegroundProfilesToDefaults();
		EditorUtility.SetDirty(terrainStyle);
		CreateSerializedStyle();
		RebuildOpenMaps(false);
		GUIUtility.ExitGUI();
	}

	void RebuildOpenMaps(bool logResult)
	{
		if (!terrainStyle)
		{
			return;
		}

		HexGrid[] grids = Object.FindObjectsOfType<HexGrid>(true);
		int rebuilt = 0;
		foreach (HexGrid grid in grids)
		{
			if (!grid || !grid.gameObject.scene.IsValid())
			{
				continue;
			}
			grid.ConfigureSurface(terrainStyle);
			if (grid.CellData != null)
			{
				grid.RefreshAllChunks();
				rebuilt++;
			}
		}
		SceneView.RepaintAll();
		if (logResult)
		{
			Debug.Log(rebuilt > 0 ?
				$"Rebuilt {rebuilt} loaded HexGrid map(s) with '{terrainStyle.name}'." :
				"Terrain style saved. Enter Play Mode in Hex to build and preview the map.",
				terrainStyle);
		}
	}

	void CreateSerializedStyle()
	{
		if (terrainStyle)
		{
			// Existing style assets predate the serialized foreground array. Asking
			// for one profile runs its non-destructive migration before the window
			// binds SerializedProperty instances.
			terrainStyle.GetForegroundProfile(HexVegetation.Mixed);
			serializedStyle = new SerializedObject(terrainStyle);
		}
		else
		{
			serializedStyle = null;
		}
	}

	static void DrawSpritePreview(Rect rect, HexHFForegroundSprite sprite)
	{
		Texture2D texture = GetSpritePreview(sprite);
		if (texture)
		{
			EditorGUI.DrawPreviewTexture(rect, texture, null, ScaleMode.ScaleToFit);
		}
		else
		{
			GUI.Box(rect, sprite.ToString(), EditorStyles.helpBox);
		}
	}

	static Texture2D GetSpritePreview(HexHFForegroundSprite sprite)
	{
		if (SpritePreviews.TryGetValue(sprite, out Texture2D texture))
		{
			return texture;
		}

		string fileName = sprite switch
		{
			HexHFForegroundSprite.Tree14 => "tree14.psd",
			HexHFForegroundSprite.DeadTree07 => "deadtree07.psd",
			HexHFForegroundSprite.Tree04 => "tree04.psd",
			HexHFForegroundSprite.DeadTree02 => "deadtree02.psd",
			_ => "tree07.psd"
		};
		texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
			$"Assets/HexMapPackage/ThirdParty/HoneyFrameworkOriginal/Foreground/{fileName}");
		SpritePreviews[sprite] = texture;
		return texture;
	}
}
