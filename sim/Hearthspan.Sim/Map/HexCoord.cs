namespace Hearthspan.Sim.Map;

/// <summary>Offset (odd-r) hex coordinate over a rectangular Col/Row storage array.</summary>
public readonly struct HexCoord : IEquatable<HexCoord>
{
    public readonly int Col;
    public readonly int Row;

    public HexCoord(int col, int row)
    {
        Col = col;
        Row = row;
    }

    public int Index(int width) => Row * width + Col;

    public bool Equals(HexCoord other) => Col == other.Col && Row == other.Row;
    public override bool Equals(object? obj) => obj is HexCoord h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(Col, Row);
    public override string ToString() => $"({Col},{Row})";

    /// <summary>Odd-r offset -> cube coordinates, for exact hex distance.</summary>
    private (int X, int Y, int Z) ToCube()
    {
        int x = Col - (Row - (Row & 1)) / 2;
        int z = Row;
        int y = -x - z;
        return (x, y, z);
    }

    public static int Distance(HexCoord a, HexCoord b)
    {
        var ca = a.ToCube();
        var cb = b.ToCube();
        return (Math.Abs(ca.X - cb.X) + Math.Abs(ca.Y - cb.Y) + Math.Abs(ca.Z - cb.Z)) / 2;
    }

    private static readonly (int dCol, int dRow)[] EvenRowDeltas =
    {
        (1, 0), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1),
    };

    private static readonly (int dCol, int dRow)[] OddRowDeltas =
    {
        (1, 0), (1, -1), (0, -1), (-1, 0), (0, 1), (1, 1),
    };

    public IEnumerable<HexCoord> Neighbors(int width, int height)
    {
        var deltas = (Row & 1) == 0 ? EvenRowDeltas : OddRowDeltas;
        foreach (var (dCol, dRow) in deltas)
        {
            int nc = Col + dCol;
            int nr = Row + dRow;
            if (nc >= 0 && nc < width && nr >= 0 && nr < height)
            {
                yield return new HexCoord(nc, nr);
            }
        }
    }
}
