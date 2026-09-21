/// <summary>
/// Local surface relief rendered inside a hex cell.
/// This is intentionally separate from elevation, which remains responsible
/// for simulation rules such as water flow and visibility.
/// </summary>
public enum HexLandform : byte
{
	// Persisted byte values and the shader's two-bit field; never renumber.
	Flat = 0,
	Hill = 1,
	Mountain = 2,
	Plateau = 3
}

/// <summary>Visual mountain composition, independent of the logical landform.</summary>
public enum HexMountainMode : byte
{
	Automatic = 0,
	Range = 1,
	Massif = 2
}
