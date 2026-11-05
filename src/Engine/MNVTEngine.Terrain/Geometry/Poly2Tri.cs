namespace MNVTEngine.Terrain.Geometry;

// A port of poly2tri.js 1.5.0 (BSD-3-Clause, Copyright (c) 2009-2014 Poly2Tri Contributors; see THIRD-PARTY-NOTICES.md):
// the sweep-line constrained Delaunay triangulation TerrainHeaven's terrain mesher uses. Points are compared by
// reference, as in JavaScript (===). Where poly2tri.js would fail on a missing neighbour (a TypeError on null), this
// port throws Poly2TriException, like its own invalid-input errors.

/// <summary>Invalid input for the triangulation: repeated points, collinear constraints, intersecting edges.</summary>
public sealed class Poly2TriException : Exception
{
    public Poly2TriException()
    {
    }

    public Poly2TriException(string message)
        : base(message)
    {
    }

    public Poly2TriException(string message, Exception inner)
        : base(message, inner)
    {
    }

    internal Poly2TriException(string message, params P2TPoint[] points)
        : base(message + string.Concat(points.Select(p => $" ({p.X};{p.Y})")))
    {
    }
}

/// <summary>A triangulation point; compared by reference.</summary>
public sealed class P2TPoint(double x, double y)
{
    public double X { get; } = x;

    public double Y { get; } = y;

    internal List<P2TEdge>? EdgeList { get; set; }

    internal static int Compare(P2TPoint a, P2TPoint b)
    {
        var d = a.Y == b.Y ? a.X - b.X : a.Y - b.Y;
        return d > 0 ? 1 : d < 0 ? -1 : 0;
    }
}

internal sealed class P2TEdge
{
    public P2TEdge(P2TPoint p1, P2TPoint p2)
    {
        P = p1;
        Q = p2;
        if (p1.Y > p2.Y)
        {
            Q = p1;
            P = p2;
        }
        else if (p1.Y == p2.Y)
        {
            if (p1.X > p2.X)
            {
                Q = p1;
                P = p2;
            }
            else if (p1.X == p2.X)
            {
                throw new Poly2TriException("poly2tri Invalid Edge constructor: repeated points!", p1);
            }
        }

        Q.EdgeList ??= [];
        Q.EdgeList.Add(this);
    }

    public P2TPoint P { get; }

    public P2TPoint Q { get; }
}

/// <summary>A triangle of the triangulation: points in counter-clockwise order.</summary>
public sealed class P2TTriangle
{
    private readonly P2TPoint[] _points;
    private readonly P2TTriangle?[] _neighbors = new P2TTriangle?[3];

    internal P2TTriangle(P2TPoint a, P2TPoint b, P2TPoint c) => _points = [a, b, c];

    internal bool Interior { get; set; }

    internal bool[] ConstrainedEdge { get; } = new bool[3];

    internal bool[] DelaunayEdge { get; } = new bool[3];

    public IReadOnlyList<P2TPoint> Points => _points;

    public P2TPoint GetPoint(int index) => _points[index];

    internal P2TTriangle? GetNeighbor(int index) => _neighbors[index];

    internal bool ContainsPoint(P2TPoint point) => point == _points[0] || point == _points[1] || point == _points[2];

    internal bool ContainsPoints(P2TPoint p1, P2TPoint p2) => ContainsPoint(p1) && ContainsPoint(p2);

    internal void MarkNeighborPointers(P2TPoint p1, P2TPoint p2, P2TTriangle t)
    {
        var points = _points;
        if ((p1 == points[2] && p2 == points[1]) || (p1 == points[1] && p2 == points[2]))
        {
            _neighbors[0] = t;
        }
        else if ((p1 == points[0] && p2 == points[2]) || (p1 == points[2] && p2 == points[0]))
        {
            _neighbors[1] = t;
        }
        else if ((p1 == points[0] && p2 == points[1]) || (p1 == points[1] && p2 == points[0]))
        {
            _neighbors[2] = t;
        }
        else
        {
            throw new Poly2TriException("poly2tri Invalid Triangle.markNeighborPointers() call");
        }
    }

    internal void MarkNeighbor(P2TTriangle t)
    {
        var points = _points;
        if (t.ContainsPoints(points[1], points[2]))
        {
            _neighbors[0] = t;
            t.MarkNeighborPointers(points[1], points[2], this);
        }
        else if (t.ContainsPoints(points[0], points[2]))
        {
            _neighbors[1] = t;
            t.MarkNeighborPointers(points[0], points[2], this);
        }
        else if (t.ContainsPoints(points[0], points[1]))
        {
            _neighbors[2] = t;
            t.MarkNeighborPointers(points[0], points[1], this);
        }
    }

    internal void ClearNeighbors() => Array.Clear(_neighbors);

    internal void ClearDelaunayEdges() => Array.Clear(DelaunayEdge);

    internal P2TPoint? PointCW(P2TPoint p) =>
        p == _points[0] ? _points[2] : p == _points[1] ? _points[0] : p == _points[2] ? _points[1] : null;

    internal P2TPoint? PointCCW(P2TPoint p) =>
        p == _points[0] ? _points[1] : p == _points[1] ? _points[2] : p == _points[2] ? _points[0] : null;

    internal P2TTriangle? NeighborCW(P2TPoint p) => p == _points[0] ? _neighbors[1] : p == _points[1] ? _neighbors[2] : _neighbors[0];

    internal P2TTriangle? NeighborCCW(P2TPoint p) => p == _points[0] ? _neighbors[2] : p == _points[1] ? _neighbors[0] : _neighbors[1];

    internal bool GetConstrainedEdgeCW(P2TPoint p) => p == _points[0] ? ConstrainedEdge[1] : p == _points[1] ? ConstrainedEdge[2] : ConstrainedEdge[0];

    internal bool GetConstrainedEdgeCCW(P2TPoint p) => p == _points[0] ? ConstrainedEdge[2] : p == _points[1] ? ConstrainedEdge[0] : ConstrainedEdge[1];

    internal bool GetConstrainedEdgeAcross(P2TPoint p) => p == _points[0] ? ConstrainedEdge[0] : p == _points[1] ? ConstrainedEdge[1] : ConstrainedEdge[2];

    internal void SetConstrainedEdgeCW(P2TPoint p, bool ce) => ConstrainedEdge[p == _points[0] ? 1 : p == _points[1] ? 2 : 0] = ce;

    internal void SetConstrainedEdgeCCW(P2TPoint p, bool ce) => ConstrainedEdge[p == _points[0] ? 2 : p == _points[1] ? 0 : 1] = ce;

    internal bool GetDelaunayEdgeCW(P2TPoint p) => p == _points[0] ? DelaunayEdge[1] : p == _points[1] ? DelaunayEdge[2] : DelaunayEdge[0];

    internal bool GetDelaunayEdgeCCW(P2TPoint p) => p == _points[0] ? DelaunayEdge[2] : p == _points[1] ? DelaunayEdge[0] : DelaunayEdge[1];

    internal void SetDelaunayEdgeCW(P2TPoint p, bool e) => DelaunayEdge[p == _points[0] ? 1 : p == _points[1] ? 2 : 0] = e;

    internal void SetDelaunayEdgeCCW(P2TPoint p, bool e) => DelaunayEdge[p == _points[0] ? 2 : p == _points[1] ? 0 : 1] = e;

    internal P2TTriangle? NeighborAcross(P2TPoint p) => p == _points[0] ? _neighbors[0] : p == _points[1] ? _neighbors[1] : _neighbors[2];

    internal P2TPoint OppositePoint(P2TTriangle t, P2TPoint p)
    {
        var cw = t.PointCW(p) ?? throw new Poly2TriException("poly2tri Invalid Triangle.oppositePoint() call");
        return PointCW(cw) ?? throw new Poly2TriException("poly2tri Invalid Triangle.oppositePoint() call");
    }

    internal void Legalize(P2TPoint opoint, P2TPoint npoint)
    {
        var points = _points;
        if (opoint == points[0])
        {
            points[1] = points[0];
            points[0] = points[2];
            points[2] = npoint;
        }
        else if (opoint == points[1])
        {
            points[2] = points[1];
            points[1] = points[0];
            points[0] = npoint;
        }
        else if (opoint == points[2])
        {
            points[0] = points[2];
            points[2] = points[1];
            points[1] = npoint;
        }
        else
        {
            throw new Poly2TriException("poly2tri Invalid Triangle.legalize() call");
        }
    }

    internal int Index(P2TPoint p) =>
        p == _points[0] ? 0 : p == _points[1] ? 1 : p == _points[2] ? 2 : throw new Poly2TriException("poly2tri Invalid Triangle.index() call");

    internal int EdgeIndex(P2TPoint p1, P2TPoint p2)
    {
        var points = _points;
        if (p1 == points[0])
        {
            if (p2 == points[1])
            {
                return 2;
            }

            if (p2 == points[2])
            {
                return 1;
            }
        }
        else if (p1 == points[1])
        {
            if (p2 == points[2])
            {
                return 0;
            }

            if (p2 == points[0])
            {
                return 2;
            }
        }
        else if (p1 == points[2])
        {
            if (p2 == points[0])
            {
                return 1;
            }

            if (p2 == points[1])
            {
                return 0;
            }
        }

        return -1;
    }

    internal void MarkConstrainedEdgeByPoints(P2TPoint p, P2TPoint q)
    {
        var points = _points;
        if ((q == points[0] && p == points[1]) || (q == points[1] && p == points[0]))
        {
            ConstrainedEdge[2] = true;
        }
        else if ((q == points[0] && p == points[2]) || (q == points[2] && p == points[0]))
        {
            ConstrainedEdge[1] = true;
        }
        else if ((q == points[1] && p == points[2]) || (q == points[2] && p == points[1]))
        {
            ConstrainedEdge[0] = true;
        }
    }
}

internal sealed class P2TNode(P2TPoint point, P2TTriangle? triangle = null)
{
    public P2TPoint Point { get; } = point;

    public P2TTriangle? Triangle { get; set; } = triangle;

    public P2TNode? Next { get; set; }

    public P2TNode? Prev { get; set; }

    public double Value { get; } = point.X;
}

internal sealed class P2TAdvancingFront(P2TNode head, P2TNode tail)
{
    private P2TNode _search = head;

    public P2TNode Head { get; } = head;

    public P2TNode Tail { get; } = tail;

    public P2TNode? LocateNode(double x)
    {
        if (x < _search.Value)
        {
            for (var node = _search.Prev; node is not null; node = node.Prev)
            {
                if (x >= node.Value)
                {
                    _search = node;
                    return node;
                }
            }
        }
        else
        {
            for (var node = _search.Next; node is not null; node = node.Next)
            {
                if (x < node.Value)
                {
                    _search = node.Prev!;
                    return node.Prev;
                }
            }
        }

        return null;
    }

    public P2TNode? LocatePoint(P2TPoint point)
    {
        var px = point.X;
        var start = _search;
        var nx = start.Point.X;
        P2TNode? node = start;
        if (px == nx)
        {
            if (point != start.Point)
            {
                if (point == start.Prev?.Point)
                {
                    node = start.Prev;
                }
                else if (point == start.Next?.Point)
                {
                    node = start.Next;
                }
                else
                {
                    throw new Poly2TriException("poly2tri Invalid AdvancingFront.locatePoint() call");
                }
            }
        }
        else if (px < nx)
        {
            for (node = start.Prev; node is not null && point != node.Point; node = node.Prev)
            {
            }
        }
        else
        {
            for (node = start.Next; node is not null && point != node.Point; node = node.Next)
            {
            }
        }

        if (node is not null)
        {
            _search = node;
        }

        return node;
    }
}

/// <summary>
/// The triangulation of one polygon (a contour, holes and interior Steiner points), as poly2tri.js SweepContext.
/// Usage: construct with the contour, AddHole, AddPoints, Triangulate, then read Triangles.
/// </summary>
public sealed class P2TSweepContext
{
    private const double KAlpha = 0.3;
    private readonly List<P2TTriangle> _triangles = [];
    private readonly List<P2TTriangle> _map = [];
    private List<P2TPoint> _points;

    /// <param name="contour">Copied (poly2tri's cloneArrays option); the points themselves are used as given.</param>
    public P2TSweepContext(IEnumerable<P2TPoint> contour)
    {
        ArgumentNullException.ThrowIfNull(contour);
        _points = [.. contour];
        InitEdges(_points);
    }

    internal List<P2TEdge> EdgeList { get; } = [];

    internal P2TAdvancingFront Front { get; private set; } = null!;

    internal P2TPoint Head { get; private set; } = null!;

    internal P2TPoint Tail { get; private set; } = null!;

    internal (P2TNode? Left, P2TNode? Bottom, P2TNode? Right, double Width, bool LeftHighest) Basin;

    internal (P2TEdge? ConstrainedEdge, bool Right) EdgeEvent;

    public IReadOnlyList<P2TTriangle> Triangles => _triangles;

    internal int PointCount => _points.Count;

    public P2TSweepContext AddHole(IReadOnlyList<P2TPoint> polyline)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        InitEdges(polyline);
        _points.AddRange(polyline);
        return this;
    }

    public P2TSweepContext AddPoints(IEnumerable<P2TPoint> points)
    {
        _points = [.. _points, .. points];
        return this;
    }

    public P2TSweepContext Triangulate()
    {
        P2TSweep.Triangulate(this);
        return this;
    }

    internal P2TPoint GetPoint(int index) => _points[index];

    internal void AddToMap(P2TTriangle triangle) => _map.Add(triangle);

    internal P2TNode LocateNode(P2TPoint point) =>
        Front.LocateNode(point.X) ?? throw new Poly2TriException("poly2tri: point outside the advancing front", point);

    internal void InitTriangulation()
    {
        double xmax = _points[0].X, xmin = _points[0].X, ymax = _points[0].Y, ymin = _points[0].Y;
        for (var i = 1; i < _points.Count; i++)
        {
            var p = _points[i];
            xmax = p.X > xmax ? p.X : xmax;
            xmin = p.X < xmin ? p.X : xmin;
            ymax = p.Y > ymax ? p.Y : ymax;
            ymin = p.Y < ymin ? p.Y : ymin;
        }

        var dx = KAlpha * (xmax - xmin);
        var dy = KAlpha * (ymax - ymin);
        Head = new P2TPoint(xmax + dx, ymin - dy);
        Tail = new P2TPoint(xmin - dx, ymin - dy);
        _points = [.. _points.OrderBy(p => p, Comparer<P2TPoint>.Create(P2TPoint.Compare))]; // stable, as Array.prototype.sort
    }

    internal void CreateAdvancingFront()
    {
        var triangle = new P2TTriangle(_points[0], Tail, Head);
        _map.Add(triangle);
        var head = new P2TNode(triangle.GetPoint(1), triangle);
        var middle = new P2TNode(triangle.GetPoint(0), triangle);
        var tail = new P2TNode(triangle.GetPoint(2));
        Front = new P2TAdvancingFront(head, tail);
        head.Next = middle;
        middle.Next = tail;
        middle.Prev = head;
        tail.Prev = middle;
    }

    internal void MapTriangleToNodes(P2TTriangle t)
    {
        for (var i = 0; i < 3; ++i)
        {
            if (t.GetNeighbor(i) is null && t.PointCW(t.GetPoint(i)) is { } cw && Front.LocatePoint(cw) is { } n)
            {
                n.Triangle = t;
            }
        }
    }

    internal void MeshClean(P2TTriangle triangle)
    {
        var triangles = new Stack<P2TTriangle?>();
        triangles.Push(triangle);
        while (triangles.TryPop(out var t) && t is not null)
        {
            if (!t.Interior)
            {
                t.Interior = true;
                _triangles.Add(t);
                for (var i = 0; i < 3; i++)
                {
                    if (!t.ConstrainedEdge[i])
                    {
                        triangles.Push(t.GetNeighbor(i));
                    }
                }
            }
        }
    }

    private void InitEdges(IReadOnlyList<P2TPoint> polyline)
    {
        for (var i = 0; i < polyline.Count; ++i)
        {
            EdgeList.Add(new P2TEdge(polyline[i], polyline[(i + 1) % polyline.Count]));
        }
    }
}

internal static class P2TSweep
{
    private const double Epsilon = 1e-12;

    private enum Orientation
    {
        Cw = 1,
        Ccw = -1,
        Collinear = 0,
    }

    public static void Triangulate(P2TSweepContext tcx)
    {
        tcx.InitTriangulation();
        tcx.CreateAdvancingFront();
        SweepPoints(tcx);
        FinalizationPolygon(tcx);
    }

    private static T Need<T>(T? value, string what)
        where T : class => value ?? throw new Poly2TriException($"poly2tri: missing {what}");

    private static Orientation Orient2d(P2TPoint pa, P2TPoint pb, P2TPoint pc)
    {
        var detleft = (pa.X - pc.X) * (pb.Y - pc.Y);
        var detright = (pa.Y - pc.Y) * (pb.X - pc.X);
        var val = detleft - detright;
        return val > -Epsilon && val < Epsilon ? Orientation.Collinear : val > 0 ? Orientation.Ccw : Orientation.Cw;
    }

    private static bool InScanArea(P2TPoint pa, P2TPoint pb, P2TPoint pc, P2TPoint pd)
    {
        var oadb = ((pa.X - pb.X) * (pd.Y - pb.Y)) - ((pd.X - pb.X) * (pa.Y - pb.Y));
        if (oadb >= -Epsilon)
        {
            return false;
        }

        var oadc = ((pa.X - pc.X) * (pd.Y - pc.Y)) - ((pd.X - pc.X) * (pa.Y - pc.Y));
        return oadc > Epsilon;
    }

    private static bool IsAngleObtuse(P2TPoint pa, P2TPoint pb, P2TPoint pc)
    {
        var ax = pb.X - pa.X;
        var ay = pb.Y - pa.Y;
        var bx = pc.X - pa.X;
        var by = pc.Y - pa.Y;
        return ((ax * bx) + (ay * by)) < 0;
    }

    private static void SweepPoints(P2TSweepContext tcx)
    {
        for (var i = 1; i < tcx.PointCount; ++i)
        {
            var point = tcx.GetPoint(i);
            var node = PointEvent(tcx, point);
            var edges = point.EdgeList;
            for (var j = 0; edges is not null && j < edges.Count; ++j)
            {
                EdgeEventByEdge(tcx, edges[j], node);
            }
        }
    }

    private static void FinalizationPolygon(P2TSweepContext tcx)
    {
        var first = Need(tcx.Front.Head.Next, "front node");
        var t = Need(first.Triangle, "front triangle");
        var p = first.Point;
        while (!t.GetConstrainedEdgeCW(p))
        {
            t = Need(t.NeighborCCW(p), "triangle while finalizing the polygon");
        }

        tcx.MeshClean(t);
    }

    private static P2TNode PointEvent(P2TSweepContext tcx, P2TPoint point)
    {
        var node = tcx.LocateNode(point);
        var newNode = NewFrontTriangle(tcx, point, node);
        if (point.X <= node.Point.X + Epsilon)
        {
            Fill(tcx, node);
        }

        FillAdvancingFront(tcx, newNode);
        return newNode;
    }

    private static void EdgeEventByEdge(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        tcx.EdgeEvent = (edge, edge.P.X > edge.Q.X);
        var triangle = Need(node.Triangle, "node triangle");
        if (IsEdgeSideOfTriangle(triangle, edge.P, edge.Q))
        {
            return;
        }

        FillEdgeEvent(tcx, edge, node);
        EdgeEventByPoints(tcx, edge.P, edge.Q, Need(node.Triangle, "node triangle"), edge.Q);
    }

    private static void EdgeEventByPoints(P2TSweepContext tcx, P2TPoint ep, P2TPoint eq, P2TTriangle triangle, P2TPoint point)
    {
        if (IsEdgeSideOfTriangle(triangle, ep, eq))
        {
            return;
        }

        var p1 = Need(triangle.PointCCW(point), "point");
        var o1 = Orient2d(eq, p1, ep);
        if (o1 == Orientation.Collinear)
        {
            throw new Poly2TriException("poly2tri EdgeEvent: Collinear not supported!", eq, p1, ep);
        }

        var p2 = Need(triangle.PointCW(point), "point");
        var o2 = Orient2d(eq, p2, ep);
        if (o2 == Orientation.Collinear)
        {
            throw new Poly2TriException("poly2tri EdgeEvent: Collinear not supported!", eq, p2, ep);
        }

        if (o1 == o2)
        {
            triangle = Need(o1 == Orientation.Cw ? triangle.NeighborCCW(point) : triangle.NeighborCW(point), "neighbor triangle");
            EdgeEventByPoints(tcx, ep, eq, triangle, point);
        }
        else
        {
            FlipEdgeEvent(tcx, ep, eq, triangle, point);
        }
    }

    private static bool IsEdgeSideOfTriangle(P2TTriangle triangle, P2TPoint ep, P2TPoint eq)
    {
        var index = triangle.EdgeIndex(ep, eq);
        if (index == -1)
        {
            return false;
        }

        triangle.ConstrainedEdge[index] = true;
        triangle.GetNeighbor(index)?.MarkConstrainedEdgeByPoints(ep, eq);
        return true;
    }

    private static P2TNode NewFrontTriangle(P2TSweepContext tcx, P2TPoint point, P2TNode node)
    {
        var next = Need(node.Next, "front node");
        var triangle = new P2TTriangle(point, node.Point, next.Point);
        triangle.MarkNeighbor(Need(node.Triangle, "front triangle"));
        tcx.AddToMap(triangle);
        var newNode = new P2TNode(point) { Next = next, Prev = node };
        next.Prev = newNode;
        node.Next = newNode;
        if (!Legalize(tcx, triangle))
        {
            tcx.MapTriangleToNodes(triangle);
        }

        return newNode;
    }

    private static void Fill(P2TSweepContext tcx, P2TNode node)
    {
        var prev = Need(node.Prev, "front node");
        var next = Need(node.Next, "front node");
        var triangle = new P2TTriangle(prev.Point, node.Point, next.Point);
        triangle.MarkNeighbor(Need(prev.Triangle, "front triangle"));
        triangle.MarkNeighbor(Need(node.Triangle, "front triangle"));
        tcx.AddToMap(triangle);
        prev.Next = next;
        next.Prev = prev;
        if (!Legalize(tcx, triangle))
        {
            tcx.MapTriangleToNodes(triangle);
        }
    }

    private static void FillAdvancingFront(P2TSweepContext tcx, P2TNode n)
    {
        var node = Need(n.Next, "front node");
        while (node.Next is not null)
        {
            if (IsAngleObtuse(node.Point, node.Next.Point, Need(node.Prev, "front node").Point))
            {
                break;
            }

            Fill(tcx, node);
            node = node.Next;
        }

        node = Need(n.Prev, "front node");
        while (node.Prev is not null)
        {
            if (IsAngleObtuse(node.Point, Need(node.Next, "front node").Point, node.Prev.Point))
            {
                break;
            }

            Fill(tcx, node);
            node = node.Prev;
        }

        if (n.Next?.Next is not null && IsBasinAngleRight(n))
        {
            FillBasin(tcx, n);
        }
    }

    private static bool IsBasinAngleRight(P2TNode node)
    {
        var far = node.Next!.Next!.Point;
        var ax = node.Point.X - far.X;
        var ay = node.Point.Y - far.Y;
        if (!(ay >= 0))
        {
            throw new Poly2TriException("unordered y");
        }

        return ax >= 0 || Math.Abs(ax) < ay;
    }

    private static bool Legalize(P2TSweepContext tcx, P2TTriangle t)
    {
        for (var i = 0; i < 3; ++i)
        {
            if (t.DelaunayEdge[i])
            {
                continue;
            }

            var ot = t.GetNeighbor(i);
            if (ot is null)
            {
                continue;
            }

            var p = t.GetPoint(i);
            var op = ot.OppositePoint(t, p);
            var oi = ot.Index(op);
            if (ot.ConstrainedEdge[oi] || ot.DelaunayEdge[oi])
            {
                t.ConstrainedEdge[i] = ot.ConstrainedEdge[oi];
                continue;
            }

            if (InCircle(p, Need(t.PointCCW(p), "point"), Need(t.PointCW(p), "point"), op))
            {
                t.DelaunayEdge[i] = true;
                ot.DelaunayEdge[oi] = true;
                RotateTrianglePair(t, p, ot, op);
                if (!Legalize(tcx, t))
                {
                    tcx.MapTriangleToNodes(t);
                }

                if (!Legalize(tcx, ot))
                {
                    tcx.MapTriangleToNodes(ot);
                }

                t.DelaunayEdge[i] = false;
                ot.DelaunayEdge[oi] = false;
                return true;
            }
        }

        return false;
    }

    private static bool InCircle(P2TPoint pa, P2TPoint pb, P2TPoint pc, P2TPoint pd)
    {
        var adx = pa.X - pd.X;
        var ady = pa.Y - pd.Y;
        var bdx = pb.X - pd.X;
        var bdy = pb.Y - pd.Y;
        var oabd = (adx * bdy) - (bdx * ady);
        if (oabd <= 0)
        {
            return false;
        }

        var cdx = pc.X - pd.X;
        var cdy = pc.Y - pd.Y;
        var ocad = (cdx * ady) - (adx * cdy);
        if (ocad <= 0)
        {
            return false;
        }

        var alift = (adx * adx) + (ady * ady);
        var blift = (bdx * bdx) + (bdy * bdy);
        var clift = (cdx * cdx) + (cdy * cdy);
        var det = (alift * ((bdx * cdy) - (cdx * bdy))) + (blift * ocad) + (clift * oabd);
        return det > 0;
    }

    private static void RotateTrianglePair(P2TTriangle t, P2TPoint p, P2TTriangle ot, P2TPoint op)
    {
        var n1 = t.NeighborCCW(p);
        var n2 = t.NeighborCW(p);
        var n3 = ot.NeighborCCW(op);
        var n4 = ot.NeighborCW(op);
        var ce1 = t.GetConstrainedEdgeCCW(p);
        var ce2 = t.GetConstrainedEdgeCW(p);
        var ce3 = ot.GetConstrainedEdgeCCW(op);
        var ce4 = ot.GetConstrainedEdgeCW(op);
        var de1 = t.GetDelaunayEdgeCCW(p);
        var de2 = t.GetDelaunayEdgeCW(p);
        var de3 = ot.GetDelaunayEdgeCCW(op);
        var de4 = ot.GetDelaunayEdgeCW(op);
        t.Legalize(p, op);
        ot.Legalize(op, p);
        ot.SetDelaunayEdgeCCW(p, de1);
        t.SetDelaunayEdgeCW(p, de2);
        t.SetDelaunayEdgeCCW(op, de3);
        ot.SetDelaunayEdgeCW(op, de4);
        ot.SetConstrainedEdgeCCW(p, ce1);
        t.SetConstrainedEdgeCW(p, ce2);
        t.SetConstrainedEdgeCCW(op, ce3);
        ot.SetConstrainedEdgeCW(op, ce4);
        t.ClearNeighbors();
        ot.ClearNeighbors();
        if (n1 is not null)
        {
            ot.MarkNeighbor(n1);
        }

        if (n2 is not null)
        {
            t.MarkNeighbor(n2);
        }

        if (n3 is not null)
        {
            t.MarkNeighbor(n3);
        }

        if (n4 is not null)
        {
            ot.MarkNeighbor(n4);
        }

        t.MarkNeighbor(ot);
    }

    private static void FillBasin(P2TSweepContext tcx, P2TNode node)
    {
        var next = node.Next!;
        var left = Orient2d(node.Point, next.Point, next.Next!.Point) == Orientation.Ccw ? next.Next : next;
        var bottom = left;
        while (bottom.Next is not null && bottom.Point.Y >= bottom.Next.Point.Y)
        {
            bottom = bottom.Next;
        }

        tcx.Basin = (left, bottom, null, 0, false);
        if (bottom == left)
        {
            return;
        }

        var right = bottom;
        while (right.Next is not null && right.Point.Y < right.Next.Point.Y)
        {
            right = right.Next;
        }

        tcx.Basin = (left, bottom, right, 0, false);
        if (right == bottom)
        {
            return;
        }

        tcx.Basin = (left, bottom, right, right.Point.X - left.Point.X, left.Point.Y > right.Point.Y);
        FillBasinReq(tcx, bottom);
    }

    private static void FillBasinReq(P2TSweepContext tcx, P2TNode node)
    {
        while (true)
        {
            if (IsShallow(tcx, node))
            {
                return;
            }

            Fill(tcx, node);
            var prev = Need(node.Prev, "front node");
            var next = Need(node.Next, "front node");
            if (prev == tcx.Basin.Left && next == tcx.Basin.Right)
            {
                return;
            }

            if (prev == tcx.Basin.Left)
            {
                if (Orient2d(node.Point, next.Point, Need(next.Next, "front node").Point) == Orientation.Cw)
                {
                    return;
                }

                node = next;
            }
            else if (next == tcx.Basin.Right)
            {
                if (Orient2d(node.Point, prev.Point, Need(prev.Prev, "front node").Point) == Orientation.Ccw)
                {
                    return;
                }

                node = prev;
            }
            else
            {
                node = prev.Point.Y < next.Point.Y ? prev : next;
            }
        }
    }

    private static bool IsShallow(P2TSweepContext tcx, P2TNode node)
    {
        var height = tcx.Basin.LeftHighest
            ? tcx.Basin.Left!.Point.Y - node.Point.Y
            : tcx.Basin.Right!.Point.Y - node.Point.Y;
        return tcx.Basin.Width > height;
    }

    private static void FillEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        if (tcx.EdgeEvent.Right)
        {
            FillRightAboveEdgeEvent(tcx, edge, node);
        }
        else
        {
            FillLeftAboveEdgeEvent(tcx, edge, node);
        }
    }

    private static void FillRightAboveEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (Need(node.Next, "front node").Point.X < edge.P.X)
        {
            if (Orient2d(edge.Q, node.Next!.Point, edge.P) == Orientation.Ccw)
            {
                FillRightBelowEdgeEvent(tcx, edge, node);
            }
            else
            {
                node = node.Next;
            }
        }
    }

    private static void FillRightBelowEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (node.Point.X < edge.P.X)
        {
            var next = Need(node.Next, "front node");
            if (Orient2d(node.Point, next.Point, Need(next.Next, "front node").Point) == Orientation.Ccw)
            {
                FillRightConcaveEdgeEvent(tcx, edge, node);
                return;
            }

            FillRightConvexEdgeEvent(tcx, edge, node);
        }
    }

    private static void FillRightConcaveEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (true)
        {
            Fill(tcx, Need(node.Next, "front node"));
            var next = Need(node.Next, "front node");
            if (next.Point == edge.P || Orient2d(edge.Q, next.Point, edge.P) != Orientation.Ccw
                || Orient2d(node.Point, next.Point, Need(next.Next, "front node").Point) != Orientation.Ccw)
            {
                return;
            }
        }
    }

    private static void FillRightConvexEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (true)
        {
            var next = Need(node.Next, "front node");
            var next2 = Need(next.Next, "front node");
            if (Orient2d(next.Point, next2.Point, Need(next2.Next, "front node").Point) == Orientation.Ccw)
            {
                FillRightConcaveEdgeEvent(tcx, edge, next);
                return;
            }

            if (Orient2d(edge.Q, next2.Point, edge.P) != Orientation.Ccw)
            {
                return;
            }

            node = next;
        }
    }

    private static void FillLeftAboveEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (Need(node.Prev, "front node").Point.X > edge.P.X)
        {
            if (Orient2d(edge.Q, node.Prev!.Point, edge.P) == Orientation.Cw)
            {
                FillLeftBelowEdgeEvent(tcx, edge, node);
            }
            else
            {
                node = node.Prev;
            }
        }
    }

    private static void FillLeftBelowEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (node.Point.X > edge.P.X)
        {
            var prev = Need(node.Prev, "front node");
            if (Orient2d(node.Point, prev.Point, Need(prev.Prev, "front node").Point) == Orientation.Cw)
            {
                FillLeftConcaveEdgeEvent(tcx, edge, node);
                return;
            }

            FillLeftConvexEdgeEvent(tcx, edge, node);
        }
    }

    private static void FillLeftConvexEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (true)
        {
            var prev = Need(node.Prev, "front node");
            var prev2 = Need(prev.Prev, "front node");
            if (Orient2d(prev.Point, prev2.Point, Need(prev2.Prev, "front node").Point) == Orientation.Cw)
            {
                FillLeftConcaveEdgeEvent(tcx, edge, prev);
                return;
            }

            if (Orient2d(edge.Q, prev2.Point, edge.P) != Orientation.Cw)
            {
                return;
            }

            node = prev;
        }
    }

    private static void FillLeftConcaveEdgeEvent(P2TSweepContext tcx, P2TEdge edge, P2TNode node)
    {
        while (true)
        {
            Fill(tcx, Need(node.Prev, "front node"));
            var prev = Need(node.Prev, "front node");
            if (prev.Point == edge.P || Orient2d(edge.Q, prev.Point, edge.P) != Orientation.Cw
                || Orient2d(node.Point, prev.Point, Need(prev.Prev, "front node").Point) != Orientation.Cw)
            {
                return;
            }
        }
    }

    private static void FlipEdgeEvent(P2TSweepContext tcx, P2TPoint ep, P2TPoint eq, P2TTriangle t, P2TPoint p)
    {
        while (true)
        {
            var ot = t.NeighborAcross(p) ?? throw new Poly2TriException("FLIP failed due to missing triangle!");
            var op = ot.OppositePoint(t, p);
            if (t.GetConstrainedEdgeAcross(p))
            {
                var index = t.Index(p);
                throw new Poly2TriException("poly2tri Intersecting Constraints", p, op, t.GetPoint((index + 1) % 3), t.GetPoint((index + 2) % 3));
            }

            if (InScanArea(p, Need(t.PointCCW(p), "point"), Need(t.PointCW(p), "point"), op))
            {
                RotateTrianglePair(t, p, ot, op);
                tcx.MapTriangleToNodes(t);
                tcx.MapTriangleToNodes(ot);
                if (p == eq && op == ep)
                {
                    if (eq == tcx.EdgeEvent.ConstrainedEdge!.Q && ep == tcx.EdgeEvent.ConstrainedEdge.P)
                    {
                        t.MarkConstrainedEdgeByPoints(ep, eq);
                        ot.MarkConstrainedEdgeByPoints(ep, eq);
                        Legalize(tcx, t);
                        Legalize(tcx, ot);
                    }

                    return;
                }

                var o = Orient2d(eq, op, ep);
                t = NextFlipTriangle(tcx, o, t, ot, p, op);
                continue; // flipEdgeEvent(tcx, ep, eq, t, p)
            }

            var newP = NextFlipPoint(ep, eq, ot, op);
            FlipScanEdgeEvent(tcx, ep, eq, t, ot, newP);
            EdgeEventByPoints(tcx, ep, eq, t, p);
            return;
        }
    }

    private static P2TTriangle NextFlipTriangle(P2TSweepContext tcx, Orientation o, P2TTriangle t, P2TTriangle ot, P2TPoint p, P2TPoint op)
    {
        if (o == Orientation.Ccw)
        {
            ot.DelaunayEdge[ot.EdgeIndex(p, op)] = true;
            Legalize(tcx, ot);
            ot.ClearDelaunayEdges();
            return t;
        }

        t.DelaunayEdge[t.EdgeIndex(p, op)] = true;
        Legalize(tcx, t);
        t.ClearDelaunayEdges();
        return ot;
    }

    private static P2TPoint NextFlipPoint(P2TPoint ep, P2TPoint eq, P2TTriangle ot, P2TPoint op) => Orient2d(eq, op, ep) switch
    {
        Orientation.Cw => Need(ot.PointCCW(op), "point"),
        Orientation.Ccw => Need(ot.PointCW(op), "point"),
        _ => throw new Poly2TriException("poly2tri [Unsupported] nextFlipPoint: opposing point on constrained edge!", eq, op, ep),
    };

    private static void FlipScanEdgeEvent(P2TSweepContext tcx, P2TPoint ep, P2TPoint eq, P2TTriangle flipTriangle, P2TTriangle t, P2TPoint p)
    {
        while (true)
        {
            var ot = t.NeighborAcross(p) ?? throw new Poly2TriException("FLIP failed due to missing triangle");
            var op = ot.OppositePoint(t, p);
            if (InScanArea(eq, Need(flipTriangle.PointCCW(eq), "point"), Need(flipTriangle.PointCW(eq), "point"), op))
            {
                FlipEdgeEvent(tcx, eq, op, ot, op);
                return;
            }

            p = NextFlipPoint(ep, eq, ot, op);
            t = ot;
        }
    }
}
