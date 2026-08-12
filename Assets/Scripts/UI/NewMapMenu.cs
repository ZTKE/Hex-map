using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Component that applies actions from the new map menu UI to the hex map.
/// Public methods are hooked up to the in-game UI.
/// </summary>
public class NewMapMenu : MonoBehaviour
{
	const int minimumCustomDimension = 15;

	[SerializeField]
	HexGrid hexGrid;

	[SerializeField]
	HexMapGenerator mapGenerator;

	bool generateMaps = true;

	bool wrapping = true;

	InputField widthInput;
	InputField heightInput;
	Text customSizeFeedback;
	Button customCreateButton;
	Text customCreateButtonLabel;

	void Awake() => BuildCustomSizeControls();

	void OnEnable()
	{
		if (widthInput && string.IsNullOrWhiteSpace(widthInput.text))
		{
			widthInput.text = "100";
		}
		if (heightInput && string.IsNullOrWhiteSpace(heightInput.text))
		{
			heightInput.text = "75";
		}
		ValidateCustomSize();
	}

	void Update()
	{
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			Close();
		}
	}

	public void ToggleMapGeneration(bool toggle) => generateMaps = toggle;

	public void ToggleWrapping(bool toggle) => wrapping = toggle;

	public void Open()
	{
		gameObject.SetActive(true);
		HexMapCamera.Locked = true;
	}

	public void Close()
	{
		gameObject.SetActive(false);
		HexMapCamera.Locked = false;
	}

	public void CreateSmallMap() => CreateMap(20, 15);

	public void CreateMediumMap() => CreateMap(40, 30);

	public void CreateLargeMap() => CreateMap(80, 60);

	/// <summary>
	/// Create a map from the exact dimensions entered in the custom size fields.
	/// Partial chunks at the north and east edges are supported.
	/// </summary>
	public void CreateCustomMap()
	{
		if (!TryGetCustomSize(out int x, out int z, out string message))
		{
			SetCustomSizeFeedback(message, false);
			return;
		}
		CreateMap(x, z);
	}

	void CreateMap(int x, int z)
	{
		if (!HexGrid.TryValidateMapSize(x, z, out string validationMessage))
		{
			SetCustomSizeFeedback(validationMessage, false);
			return;
		}

		if (generateMaps)
		{
			if (!mapGenerator.GenerateMap(x, z, wrapping))
			{
				SetCustomSizeFeedback("The generated map could not be created.", false);
				return;
			}
		}
		else if (!hexGrid.CreateMap(x, z, wrapping))
		{
			SetCustomSizeFeedback("The map could not be created.", false);
			return;
		}
		HexMapCamera.FocusCreatedMap();
		Close();
	}

	void BuildCustomSizeControls()
	{
		RectTransform menu = transform.Find("Menu") as RectTransform;
		if (!menu)
		{
			Debug.LogWarning("New map menu root was not found; custom size UI is unavailable.");
			return;
		}

		menu.sizeDelta = new Vector2(300f, 382f);
		RectTransform cancelButton = menu.Find("Cancel Button") as RectTransform;
		if (cancelButton)
		{
			cancelButton.anchoredPosition = new Vector2(0f, -353f);
			cancelButton.sizeDelta = new Vector2(260f, 30f);
		}

		RectTransform customRoot = CreateUIObject(
			"Custom Size", menu, new Vector2(0f, -190f),
			new Vector2(280f, 142f));
		CreateText(
			"Title", customRoot, "Custom size (exact cells)",
			new Vector2(0f, 0f), new Vector2(260f, 22f),
			14, TextAnchor.MiddleCenter, new Color(0.2f, 0.2f, 0.2f));

		CreateText(
			"Width Label", customRoot, "Width",
			new Vector2(-112f, -31f), new Vector2(48f, 28f),
			13, TextAnchor.MiddleLeft, new Color(0.2f, 0.2f, 0.2f));
		widthInput = CreateIntegerInput(
			"Width Input", customRoot, new Vector2(-53f, -31f));
		CreateText(
			"By", customRoot, "x",
			new Vector2(0f, -31f), new Vector2(20f, 28f),
			13, TextAnchor.MiddleCenter, new Color(0.2f, 0.2f, 0.2f));
		CreateText(
			"Height Label", customRoot, "Height",
			new Vector2(31f, -31f), new Vector2(48f, 28f),
			13, TextAnchor.MiddleLeft, new Color(0.2f, 0.2f, 0.2f));
		heightInput = CreateIntegerInput(
			"Height Input", customRoot, new Vector2(91f, -31f));

		customSizeFeedback = CreateText(
			"Feedback", customRoot, string.Empty,
			new Vector2(0f, -64f), new Vector2(260f, 34f),
			12, TextAnchor.MiddleCenter, new Color(0.2f, 0.2f, 0.2f));
		customSizeFeedback.horizontalOverflow = HorizontalWrapMode.Wrap;
		customSizeFeedback.verticalOverflow = VerticalWrapMode.Truncate;

		customCreateButton = CreateButton(
			"Create Custom Button", customRoot,
			new Vector2(0f, -105f), new Vector2(260f, 30f),
			out customCreateButtonLabel);
		customCreateButton.onClick.AddListener(CreateCustomMap);
		widthInput.onValueChanged.AddListener(_ => ValidateCustomSize());
		heightInput.onValueChanged.AddListener(_ => ValidateCustomSize());
	}

	void ValidateCustomSize()
	{
		if (!customCreateButton)
		{
			return;
		}
		bool valid = TryGetCustomSize(out int x, out int z, out string message);
		customCreateButton.interactable = valid;
		if (valid)
		{
			long cellCount = (long)x * z;
			message = cellCount > HexGrid.ChunkStreamingCellThreshold ?
				$"{cellCount:N0} cells - streamed rendering" :
				$"{cellCount:N0} cells";
		}
		SetCustomSizeFeedback(message, valid);
	}

	bool TryGetCustomSize(out int x, out int z, out string message)
	{
		x = z = 0;
		if (!widthInput || !heightInput ||
			!int.TryParse(widthInput.text, out x) ||
			!int.TryParse(heightInput.text, out z))
		{
			message = "Enter whole numbers for width and height.";
			return false;
		}
		if (x < minimumCustomDimension || z < minimumCustomDimension)
		{
			message = $"Each dimension must be at least {minimumCustomDimension}.";
			return false;
		}
		return HexGrid.TryValidateMapSize(x, z, out message);
	}

	void SetCustomSizeFeedback(string message, bool valid)
	{
		if (!customSizeFeedback)
		{
			return;
		}
		customSizeFeedback.text = message ?? string.Empty;
		customSizeFeedback.color = valid ?
			new Color(0.18f, 0.35f, 0.2f) : new Color(0.65f, 0.16f, 0.12f);
		if (customCreateButtonLabel)
		{
			customCreateButtonLabel.color = customCreateButton.interactable ?
				new Color(0.15f, 0.15f, 0.15f) : Color.gray;
		}
	}

	static RectTransform CreateUIObject(
		string name, Transform parent, Vector2 anchoredPosition, Vector2 size)
	{
		GameObject gameObject = new(name, typeof(RectTransform));
		RectTransform rect = gameObject.GetComponent<RectTransform>();
		rect.SetParent(parent, false);
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 1f);
		rect.anchoredPosition = anchoredPosition;
		rect.sizeDelta = size;
		return rect;
	}

	static Text CreateText(
		string name, Transform parent, string value, Vector2 anchoredPosition,
		Vector2 size, int fontSize, TextAnchor alignment, Color color)
	{
		GameObject gameObject = new(
			name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
		RectTransform rect = gameObject.GetComponent<RectTransform>();
		rect.SetParent(parent, false);
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 1f);
		rect.anchoredPosition = anchoredPosition;
		rect.sizeDelta = size;
		Text text = gameObject.GetComponent<Text>();
		text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		text.fontSize = fontSize;
		text.alignment = alignment;
		text.color = color;
		text.text = value;
		text.raycastTarget = false;
		return text;
	}

	static InputField CreateIntegerInput(
		string name, Transform parent, Vector2 anchoredPosition)
	{
		GameObject gameObject = new(
			name, typeof(RectTransform), typeof(CanvasRenderer),
			typeof(Image), typeof(InputField));
		RectTransform rect = gameObject.GetComponent<RectTransform>();
		rect.SetParent(parent, false);
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 1f);
		rect.anchoredPosition = anchoredPosition;
		rect.sizeDelta = new Vector2(70f, 28f);

		Image image = gameObject.GetComponent<Image>();
		image.color = new Color(1f, 1f, 1f, 0.92f);
		InputField input = gameObject.GetComponent<InputField>();
		input.contentType = InputField.ContentType.IntegerNumber;
		input.characterLimit = 6;
		input.lineType = InputField.LineType.SingleLine;

		Text value = CreateText(
			"Text", rect, string.Empty, Vector2.zero,
			new Vector2(62f, 24f), 13, TextAnchor.MiddleCenter,
			new Color(0.12f, 0.12f, 0.12f));
		value.supportRichText = false;
		Text placeholder = CreateText(
			"Placeholder", rect, "0", Vector2.zero,
			new Vector2(62f, 24f), 13, TextAnchor.MiddleCenter,
			new Color(0.45f, 0.45f, 0.45f, 0.65f));
		input.textComponent = value;
		input.placeholder = placeholder;
		return input;
	}

	static Button CreateButton(
		string name, Transform parent, Vector2 anchoredPosition, Vector2 size,
		out Text label)
	{
		GameObject gameObject = new(
			name, typeof(RectTransform), typeof(CanvasRenderer),
			typeof(Image), typeof(Button));
		RectTransform rect = gameObject.GetComponent<RectTransform>();
		rect.SetParent(parent, false);
		rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
		rect.pivot = new Vector2(0.5f, 1f);
		rect.anchoredPosition = anchoredPosition;
		rect.sizeDelta = size;
		Image image = gameObject.GetComponent<Image>();
		image.color = new Color(0.95f, 0.95f, 0.95f, 1f);
		Button button = gameObject.GetComponent<Button>();
		button.targetGraphic = image;
		label = CreateText(
			"Text", rect, "Create Custom Map", Vector2.zero, size,
			14, TextAnchor.MiddleCenter, new Color(0.15f, 0.15f, 0.15f));
		return button;
	}
}
