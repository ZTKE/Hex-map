using UnityEngine;

/// <summary>
/// Toggles the globe fullscreen cosmos + black-gold limb pass.
/// Driven only by globe presentation visibility — never by HexMapCamera
/// enable/disable (UI input lock must not clear the look).
/// Only the registered map camera receives the pass so FairyGUI StageCamera
/// keeps drawing UI on top.
/// </summary>
public static class HexGlobeAtmosphere
{
	public static bool Active { get; private set; }

	public static Camera TargetCamera { get; private set; }
	public static bool BackgroundOnly { get; private set; }

	public static void SetActive(bool active)
	{
		Active = active;
		if (!active)
		{
			TargetCamera = null;
			BackgroundOnly = false;
		}
	}

	public static void SetTargetCamera(Camera camera, bool backgroundOnly = false)
	{
		TargetCamera = camera;
		BackgroundOnly = backgroundOnly;
	}

	public static bool ShouldRender(Camera camera)
	{
		return Active &&
			camera != null &&
			TargetCamera != null &&
			camera == TargetCamera;
	}
}
