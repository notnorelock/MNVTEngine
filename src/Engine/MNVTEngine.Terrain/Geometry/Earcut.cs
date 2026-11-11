using MNVTEngine.Mathematics;

namespace MNVTEngine.Terrain.Geometry;

/// <summary>
/// Polygon triangulation with holes: a line-for-line port of mapbox/earcut 3.0.2 (ISC), the version three.js bundles,
/// so shapes triangulate exactly as they do in TerrainHeaven. See THIRD-PARTY-NOTICES.md.
/// </summary>
public static class Earcut
{
    private sealed class Node(int i, double x, double y)
    {
        public readonly int I = i;
        public readonly double X = x;
        public readonly double Y = y;
        public Node Prev = null!;
        public Node Next = null!;
        public int Z;
        public Node? PrevZ;
        public Node? NextZ;
        public bool Steiner;
    }

    /// <summary>Vertex indices of the triangles, three per triangle.</summary>
    /// <param name="data">Flat coordinates (x0, y0, x1, y1, ...).</param>
    /// <param name="holeIndices">The vertex index where each hole starts.</param>
    public static List<int> Triangulate(IReadOnlyList<double> data, IReadOnlyList<int>? holeIndices = null, int dim = 2)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hasHoles = holeIndices is { Count: > 0 };
        var outerLength = hasHoles ? holeIndices![0] * dim : data.Count;
        var outerNode = LinkedList(data, 0, outerLength, dim, true);
        var triangles = new List<int>();
        if (outerNode is null || outerNode.Next == outerNode.Prev)
        {
            return triangles;
        }

        double minX = 0, minY = 0, invSize = 0;
        if (hasHoles)
        {
            outerNode = EliminateHoles(data, holeIndices!, outerNode, dim);
        }

        // If the shape is not too simple, use the z-order curve hash later; calculate the polygon bbox.
        if (data.Count > 80 * dim)
        {
            minX = data[0];
            minY = data[1];
            var maxX = minX;
            var maxY = minY;
            for (var i = dim; i < outerLength; i += dim)
            {
                var x = data[i];
                var y = data[i + 1];
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            invSize = Math.Max(maxX - minX, maxY - minY);
            invSize = invSize != 0 ? 32767 / invSize : 0;
        }

        EarcutLinked(outerNode, triangles, dim, minX, minY, invSize, 0);
        return triangles;
    }

    private static Node? LinkedList(IReadOnlyList<double> data, int start, int end, int dim, bool clockwise)
    {
        Node? last = null;
        if (clockwise == (SignedArea(data, start, end, dim) > 0))
        {
            for (var i = start; i < end; i += dim)
            {
                last = InsertNode(i / dim, data[i], data[i + 1], last);
            }
        }
        else
        {
            for (var i = end - dim; i >= start; i -= dim)
            {
                last = InsertNode(i / dim, data[i], data[i + 1], last);
            }
        }

        if (last is not null && EqualPoints(last, last.Next))
        {
            RemoveNode(last);
            last = last.Next;
        }

        return last;
    }

    private static Node? FilterPoints(Node? start, Node? end = null)
    {
        if (start is null)
        {
            return start;
        }

        end ??= start;
        var p = start;
        bool again;
        do
        {
            again = false;
            if (!p.Steiner && (EqualPoints(p, p.Next) || Area(p.Prev, p, p.Next) == 0))
            {
                RemoveNode(p);
                p = end = p.Prev;
                if (p == p.Next)
                {
                    break;
                }

                again = true;
            }
            else
            {
                p = p.Next;
            }
        }
        while (again || p != end);

        return end;
    }

    private static void EarcutLinked(Node? ear, List<int> triangles, int dim, double minX, double minY, double invSize, int pass)
    {
        if (ear is null)
        {
            return;
        }

        if (pass == 0 && invSize != 0)
        {
            IndexCurve(ear, minX, minY, invSize);
        }

        var stop = ear;
        while (ear.Prev != ear.Next)
        {
            var prev = ear.Prev;
            var next = ear.Next;
            if (invSize != 0 ? IsEarHashed(ear, minX, minY, invSize) : IsEar(ear))
            {
                triangles.Add(prev.I);
                triangles.Add(ear.I);
                triangles.Add(next.I);
                RemoveNode(ear);

                // Skipping the next vertex leads to less sliver triangles.
                ear = next.Next;
                stop = next.Next;
                continue;
            }

            ear = next;
            if (ear == stop)
            {
                if (pass == 0)
                {
                    EarcutLinked(FilterPoints(ear), triangles, dim, minX, minY, invSize, 1);
                }
                else if (pass == 1)
                {
                    ear = CureLocalIntersections(FilterPoints(ear)!, triangles);
                    EarcutLinked(ear, triangles, dim, minX, minY, invSize, 2);
                }
                else if (pass == 2)
                {
                    SplitEarcut(ear, triangles, dim, minX, minY, invSize);
                }

                break;
            }
        }
    }

    private static bool IsEar(Node ear)
    {
        var (a, b, c) = (ear.Prev, ear, ear.Next);
        if (Area(a, b, c) >= 0)
        {
            return false;
        }

        double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
        double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
        double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));
        var p = c.Next;
        while (p != a)
        {
            if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1
                && PointInTriangleExceptFirst(ax, ay, bx, by, cx, cy, p.X, p.Y)
                && Area(p.Prev, p, p.Next) >= 0)
            {
                return false;
            }

            p = p.Next;
        }

        return true;
    }

    private static bool IsEarHashed(Node ear, double minX, double minY, double invSize)
    {
        var (a, b, c) = (ear.Prev, ear, ear.Next);
        if (Area(a, b, c) >= 0)
        {
            return false;
        }

        double ax = a.X, bx = b.X, cx = c.X, ay = a.Y, by = b.Y, cy = c.Y;
        double x0 = Math.Min(ax, Math.Min(bx, cx)), y0 = Math.Min(ay, Math.Min(by, cy));
        double x1 = Math.Max(ax, Math.Max(bx, cx)), y1 = Math.Max(ay, Math.Max(by, cy));
        var minZ = ZOrder(x0, y0, minX, minY, invSize);
        var maxZ = ZOrder(x1, y1, minX, minY, invSize);
        var p = ear.PrevZ;
        var n = ear.NextZ;

        bool Blocks(Node q) => q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1 && q != a && q != c
            && PointInTriangleExceptFirst(ax, ay, bx, by, cx, cy, q.X, q.Y) && Area(q.Prev, q, q.Next) >= 0;

        while (p is not null && p.Z >= minZ && n is not null && n.Z <= maxZ)
        {
            if (Blocks(p))
            {
                return false;
            }

            p = p.PrevZ;
            if (Blocks(n))
            {
                return false;
            }

            n = n.NextZ;
        }

        while (p is not null && p.Z >= minZ)
        {
            if (Blocks(p))
            {
                return false;
            }

            p = p.PrevZ;
        }

        while (n is not null && n.Z <= maxZ)
        {
            if (Blocks(n))
            {
                return false;
            }

            n = n.NextZ;
        }

        return true;
    }

    private static Node? CureLocalIntersections(Node start, List<int> triangles)
    {
        var p = start;
        do
        {
            var a = p.Prev;
            var b = p.Next.Next;
            if (!EqualPoints(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
            {
                triangles.Add(a.I);
                triangles.Add(p.I);
                triangles.Add(b.I);
                RemoveNode(p);
                RemoveNode(p.Next);
                p = start = b;
            }

            p = p.Next;
        }
        while (p != start);

        return FilterPoints(p);
    }

    private static void SplitEarcut(Node start, List<int> triangles, int dim, double minX, double minY, double invSize)
    {
        var a = start;
        do
        {
            var b = a.Next.Next;
            while (b != a.Prev)
            {
                if (a.I != b.I && IsValidDiagonal(a, b))
                {
                    var c = SplitPolygon(a, b);
                    a = FilterPoints(a, a.Next)!;
                    c = FilterPoints(c, c.Next)!;
                    EarcutLinked(a, triangles, dim, minX, minY, invSize, 0);
                    EarcutLinked(c, triangles, dim, minX, minY, invSize, 0);
                    return;
                }

                b = b.Next;
            }

            a = a.Next;
        }
        while (a != start);
    }

    private static Node EliminateHoles(IReadOnlyList<double> data, IReadOnlyList<int> holeIndices, Node outerNode, int dim)
    {
        var queue = new List<Node>();
        for (int i = 0, length = holeIndices.Count; i < length; i++)
        {
            var start = holeIndices[i] * dim;
            var end = i < length - 1 ? holeIndices[i + 1] * dim : data.Count;
            var list = LinkedList(data, start, end, dim, false);
            if (list is null)
            {
                continue;
            }

            if (list == list.Next)
            {
                list.Steiner = true;
            }

            queue.Add(GetLeftmost(list));
        }

        // Array.prototype.sort is stable; so is OrderBy.
        foreach (var hole in queue.OrderBy(n => n, Comparer<Node>.Create(CompareXYSlope)).ToList())
        {
            outerNode = EliminateHole(hole, outerNode);
        }

        return outerNode;
    }

    private static int CompareXYSlope(Node a, Node b)
    {
        var result = a.X - b.X;
        if (result == 0)
        {
            result = a.Y - b.Y;
            if (result == 0)
            {
                var aSlope = (a.Next.Y - a.Y) / (a.Next.X - a.X);
                var bSlope = (b.Next.Y - b.Y) / (b.Next.X - b.X);
                result = aSlope - bSlope;
            }
        }

        return result > 0 ? 1 : result < 0 ? -1 : 0; // NaN compares as equal, as the JS sort treats it
    }

    private static Node EliminateHole(Node hole, Node outerNode)
    {
        var bridge = FindHoleBridge(hole, outerNode);
        if (bridge is null)
        {
            return outerNode;
        }

        var bridgeReverse = SplitPolygon(bridge, hole);
        FilterPoints(bridgeReverse, bridgeReverse.Next);
        return FilterPoints(bridge, bridge.Next)!;
    }

    /// <summary>David Eberly's algorithm for finding a bridge between a hole and the outer polygon.</summary>
    private static Node? FindHoleBridge(Node hole, Node outerNode)
    {
        var p = outerNode;
        var hx = hole.X;
        var hy = hole.Y;
        var qx = double.NegativeInfinity;
        Node? m = null;
        if (EqualPoints(hole, p))
        {
            return p;
        }

        do
        {
            if (EqualPoints(hole, p.Next))
            {
                return p.Next;
            }

            if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
            {
                var x = p.X + ((hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y));
                if (x <= hx && x > qx)
                {
                    qx = x;
                    m = p.X < p.Next.X ? p : p.Next;
                    if (x == hx)
                    {
                        return m;
                    }
                }
            }

            p = p.Next;
        }
        while (p != outerNode);

        if (m is null)
        {
            return null;
        }

        var stop = m;
        var mx = m.X;
        var my = m.Y;
        var tanMin = double.PositiveInfinity;
        p = m;
        do
        {
            if (hx >= p.X && p.X >= mx && hx != p.X
                && PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
            {
                var tan = Math.Abs(hy - p.Y) / (hx - p.X);
                if (LocallyInside(p, hole)
                    && (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
                {
                    m = p;
                    tanMin = tan;
                }
            }

            p = p.Next;
        }
        while (p != stop);

        return m;
    }

    private static bool SectorContainsSector(Node m, Node p) => Area(m.Prev, m, p.Prev) < 0 && Area(p.Next, m, m.Next) < 0;

    private static void IndexCurve(Node start, double minX, double minY, double invSize)
    {
        var p = start;
        do
        {
            if (p.Z == 0)
            {
                p.Z = ZOrder(p.X, p.Y, minX, minY, invSize);
            }

            p.PrevZ = p.Prev;
            p.NextZ = p.Next;
            p = p.Next;
        }
        while (p != start);

        p.PrevZ!.NextZ = null;
        p.PrevZ = null;
        SortLinked(p);
    }

    /// <summary>Simon Tatham's linked list merge sort.</summary>
    private static Node? SortLinked(Node? list)
    {
        int numMerges;
        var inSize = 1;
        do
        {
            var p = list;
            list = null;
            Node? tail = null;
            numMerges = 0;
            while (p is not null)
            {
                numMerges++;
                var q = p;
                var pSize = 0;
                for (var i = 0; i < inSize; i++)
                {
                    pSize++;
                    q = q.NextZ;
                    if (q is null)
                    {
                        break;
                    }
                }

                var qSize = inSize;
                while (pSize > 0 || (qSize > 0 && q is not null))
                {
                    Node e;
                    if (pSize != 0 && (qSize == 0 || q is null || p!.Z <= q.Z))
                    {
                        e = p!;
                        p = p!.NextZ;
                        pSize--;
                    }
                    else
                    {
                        e = q!;
                        q = q!.NextZ;
                        qSize--;
                    }

                    if (tail is not null)
                    {
                        tail.NextZ = e;
                    }
                    else
                    {
                        list = e;
                    }

                    e.PrevZ = tail;
                    tail = e;
                }

                p = q;
            }

            tail!.NextZ = null;
            inSize *= 2;
        }
        while (numMerges > 1);

        return list;
    }

    private static int ZOrder(double px, double py, double minX, double minY, double invSize)
    {
        var x = (int)((px - minX) * invSize);
        var y = (int)((py - minY) * invSize);
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        y = (y | (y << 8)) & 0x00FF00FF;
        y = (y | (y << 4)) & 0x0F0F0F0F;
        y = (y | (y << 2)) & 0x33333333;
        y = (y | (y << 1)) & 0x55555555;
        return x | (y << 1);
    }

    private static Node GetLeftmost(Node start)
    {
        var p = start;
        var leftmost = start;
        do
        {
            if (p.X < leftmost.X || (p.X == leftmost.X && p.Y < leftmost.Y))
            {
                leftmost = p;
            }

            p = p.Next;
        }
        while (p != start);

        return leftmost;
    }

    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        ((cx - px) * (ay - py)) >= ((ax - px) * (cy - py))
        && ((ax - px) * (by - py)) >= ((bx - px) * (ay - py))
        && ((bx - px) * (cy - py)) >= ((cx - px) * (by - py));

    private static bool PointInTriangleExceptFirst(double ax, double ay, double bx, double by, double cx, double cy, double px, double py) =>
        !(ax == px && ay == py) && PointInTriangle(ax, ay, bx, by, cx, cy, px, py);

    private static bool IsValidDiagonal(Node a, Node b) =>
        a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b)
        && ((LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b) && (Area(a.Prev, a, b.Prev) != 0 || Area(a, b.Prev, b) != 0))
            || (EqualPoints(a, b) && Area(a.Prev, a, a.Next) > 0 && Area(b.Prev, b, b.Next) > 0));

    private static double Area(Node p, Node q, Node r) => ((q.Y - p.Y) * (r.X - q.X)) - ((q.X - p.X) * (r.Y - q.Y));

    private static bool EqualPoints(Node p1, Node p2) => p1.X == p2.X && p1.Y == p2.Y;

    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        var o1 = Math.Sign(Area(p1, q1, p2));
        var o2 = Math.Sign(Area(p1, q1, q2));
        var o3 = Math.Sign(Area(p2, q2, p1));
        var o4 = Math.Sign(Area(p2, q2, q1));
        if (o1 != o2 && o3 != o4)
        {
            return true;
        }

        return (o1 == 0 && OnSegment(p1, p2, q1)) || (o2 == 0 && OnSegment(p1, q2, q1))
            || (o3 == 0 && OnSegment(p2, p1, q2)) || (o4 == 0 && OnSegment(p2, q1, q2));
    }

    private static bool OnSegment(Node p, Node q, Node r) =>
        q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

    private static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
            {
                return true;
            }

            p = p.Next;
        }
        while (p != a);

        return false;
    }

    private static bool LocallyInside(Node a, Node b) =>
        Area(a.Prev, a, a.Next) < 0
            ? Area(a, b, a.Next) >= 0 && Area(a, a.Prev, b) >= 0
            : Area(a, b, a.Prev) < 0 || Area(a, a.Next, b) < 0;

    private static bool MiddleInside(Node a, Node b)
    {
        var p = a;
        var inside = false;
        var px = (a.X + b.X) / 2;
        var py = (a.Y + b.Y) / 2;
        do
        {
            if (((p.Y > py) != (p.Next.Y > py)) && p.Next.Y != p.Y && (px < ((p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y)) + p.X))
            {
                inside = !inside;
            }

            p = p.Next;
        }
        while (p != a);

        return inside;
    }

    private static Node SplitPolygon(Node a, Node b)
    {
        var a2 = new Node(a.I, a.X, a.Y);
        var b2 = new Node(b.I, b.X, b.Y);
        var an = a.Next;
        var bp = b.Prev;
        a.Next = b;
        b.Prev = a;
        a2.Next = an;
        an.Prev = a2;
        b2.Next = a2;
        a2.Prev = b2;
        bp.Next = b2;
        b2.Prev = bp;
        return b2;
    }

    private static Node InsertNode(int i, double x, double y, Node? last)
    {
        var p = new Node(i, x, y);
        if (last is null)
        {
            p.Prev = p;
            p.Next = p;
        }
        else
        {
            p.Next = last.Next;
            p.Prev = last;
            last.Next.Prev = p;
            last.Next = p;
        }

        return p;
    }

    private static void RemoveNode(Node p)
    {
        p.Next.Prev = p.Prev;
        p.Prev.Next = p.Next;
        if (p.PrevZ is not null)
        {
            p.PrevZ.NextZ = p.NextZ;
        }

        if (p.NextZ is not null)
        {
            p.NextZ.PrevZ = p.PrevZ;
        }
    }

    private static double SignedArea(IReadOnlyList<double> data, int start, int end, int dim)
    {
        var sum = 0.0;
        for (int i = start, j = end - dim; i < end; i += dim)
        {
            sum += (data[j] - data[i]) * (data[i + 1] + data[j + 1]);
            j = i;
        }

        return sum;
    }
}

/// <summary>three.js ShapeUtils: shape area, winding and the triangulateShape wrapper over earcut.</summary>
public static class ShapeUtils
{
    public static double Area(IReadOnlyList<Vector2> contour)
    {
        ArgumentNullException.ThrowIfNull(contour);
        var n = contour.Count;
        var a = 0.0;
        for (int p = n - 1, q = 0; q < n; p = q++)
        {
            a += (contour[p].X * contour[q].Y) - (contour[q].X * contour[p].Y);
        }

        return a * 0.5;
    }

    public static bool IsClockWise(IReadOnlyList<Vector2> points) => Area(points) < 0;

    /// <summary>
    /// Faces (vertex index triples) over the contour followed by every hole. Like three.js, a duplicate closing point is
    /// removed from the given lists in place.
    /// </summary>
    public static List<int[]> TriangulateShape(List<Vector2> contour, List<List<Vector2>> holes)
    {
        ArgumentNullException.ThrowIfNull(contour);
        ArgumentNullException.ThrowIfNull(holes);
        var vertices = new List<double>();
        var holeIndices = new List<int>();
        RemoveDupEndPoints(contour);
        AddContour(vertices, contour);
        var holeIndex = contour.Count;
        holes.ForEach(RemoveDupEndPoints);
        foreach (var hole in holes)
        {
            holeIndices.Add(holeIndex);
            holeIndex += hole.Count;
            AddContour(vertices, hole);
        }

        var triangles = Earcut.Triangulate(vertices, holeIndices);
        var faces = new List<int[]>(triangles.Count / 3);
        for (var i = 0; i + 2 < triangles.Count; i += 3)
        {
            faces.Add([triangles[i], triangles[i + 1], triangles[i + 2]]);
        }

        return faces;
    }

    private static void RemoveDupEndPoints(List<Vector2> points)
    {
        var l = points.Count;
        if (l > 2 && points[l - 1] == points[0])
        {
            points.RemoveAt(l - 1);
        }
    }

    private static void AddContour(List<double> vertices, List<Vector2> contour)
    {
        foreach (var point in contour)
        {
            vertices.Add(point.X);
            vertices.Add(point.Y);
        }
    }
}
