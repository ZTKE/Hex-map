using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation-only ink and paper palette shared by flat and globe maps.
/// Country ownership, source-map colors, flags and near terrain keep their data.
/// A game can supply Resources/MapPresentation/Antique Atlas Style.
/// </summary>
[CreateAssetMenu(menuName = "Hex Map/Cartography Style")]
public sealed class HexCartographyStyle : ScriptableObject
{
	[Serializable]
	public struct CountryPigment
	{
		public int countryId;
		public string countryName;
		public Color color;
	}

	public Font countryNameFont;
	public Color nameInk = new(0.16f, 0.15f, 0.13f, 0.94f);
	public Color nameHalo = new(0.92f, 0.87f, 0.74f, 0.72f);
	public Color oceanPaper = new(0.82f, 0.745f, 0.60f, 1f);
	public Color paper = new(0.88f, 0.81f, 0.68f, 1f);
	public Color borderInk = new(0.26f, 0.22f, 0.17f, 0.86f);
	public Color globeBackdrop = new(0.24f, 0.29f, 0.28f, 1f);
	public CountryPigment[] countries = Array.Empty<CountryPigment>();

	static HexCartographyStyle current;
	Dictionary<int, Color> palette;

	public static HexCartographyStyle Current
	{
		get
		{
			if (!current)
			{
				current = Resources.Load<HexCartographyStyle>("MapPresentation/Antique Atlas Style");
				if (!current)
				{
					current = CreateInstance<HexCartographyStyle>();
					current.hideFlags = HideFlags.HideAndDontSave;
				}
			}
			return current;
		}
	}

	void OnEnable() => palette = null;
	void OnValidate() => palette = null;

	/// <summary>Returns an sRGB pigment; texture and buffer consumers convert once.</summary>
	public Color GetCountryColor(int countryId, Color source)
	{
		if (palette == null)
		{
			palette = new Dictionary<int, Color>();
			foreach (CountryPigment entry in countries)
				palette[entry.countryId] = entry.color;
		}
		if (palette.TryGetValue(countryId, out Color pigment)) return pigment;
		if (countryId == 0) return paper;
		Color.RGBToHSV(source, out float hue, out float saturation, out float value);
		// Unknown/new countries get the same restrained ink gamut as the authored palette.
		pigment = Color.HSVToRGB(hue, Mathf.Clamp(saturation * 0.45f, 0.12f, 0.30f),
			Mathf.Lerp(0.62f, 0.78f, value));
		pigment.a = 1f;
		return pigment;
	}
}
