using System.Collections.Generic;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>
    /// Packed (a,b) graph edges need an avalanche hash. UInt64.GetHashCode XORs
    /// its halves, so spatially related sphere IDs collide in very long chains.
    /// Equality and saved IDs remain unchanged; only hash-bucket placement differs.
    /// </summary>
    internal sealed class SphericalEdgeKeyComparer : IEqualityComparer<ulong>
    {
        public static readonly SphericalEdgeKeyComparer Instance = new SphericalEdgeKeyComparer();
        public bool Equals(ulong a, ulong b) => a == b;
        public int GetHashCode(ulong value)
        {
            unchecked
            {
                value ^= value >> 30;
                value *= 0xbf58476d1ce4e5b9UL;
                value ^= value >> 27;
                value *= 0x94d049bb133111ebUL;
                value ^= value >> 31;
                return (int)(value ^ (value >> 32));
            }
        }
    }
}
