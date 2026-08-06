/// <summary>
/// Local surface relief rendered inside a hex cell.
/// This is intentionally separate from elevation, which remains responsible
/// for simulation rules such as water flow and visibility.
/// </summary>
public enum HexLandform : byte
{
	Flat,
	Hill,
	Mountain
}
