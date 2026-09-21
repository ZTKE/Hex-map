/// <summary>
/// Toggles the strategic-map fullscreen atmosphere pass. Only active in overview LOD.
/// </summary>
public static class HexOverviewAtmosphere
{
	public static bool Active { get; private set; }

	public static void SetActive(bool active)
	{
		Active = active;
	}
}
