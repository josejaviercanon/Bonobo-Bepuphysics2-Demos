using System.Numerics;
using Bonobo.Bepuphysics2.Collidables;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Voxel-tetrahedralization pipeline ported from the upstream <c>NewtDemo</c>
///     (BepuPhysics2, Apache-2.0, Ross Nordby): <c>BoxTriangleCollider</c>,
///     <c>TriangleRasterizer</c> and the deliberately simple <c>DumbTetrahedralizer</c>.
///     Test-bed deviations: the pooled <c>QuickSet</c>/<c>QuickList</c>/<c>Buffer</c> storage is
///     replaced by managed lists and dictionaries (construction-time only, deterministic
///     insertion order preserved); no <c>unsafe</c> code.
/// </summary>
public static class BoxTriangleCollider
{
    private const float IntersectionEpsilon = 1e-4f;

    private static bool OverlapsAlongAxis(ref Vector3 axis, ref Vector3 halfExtents, ref Vector3 a, ref Vector3 b, ref Vector3 c)
    {
        var da = Vector3.Dot(a, axis);
        var db = Vector3.Dot(b, axis);
        var dc = Vector3.Dot(c, axis);

        float min, max;
        if (da < db && da < dc)
        {
            min = da;
            max = db > dc ? db : dc;
        }
        else if (db < dc)
        {
            min = db;
            max = da > dc ? da : dc;
        }
        else
        {
            min = dc;
            max = da > db ? da : db;
        }

        Vector3 boxExtremePoint;
        if (axis.X > 0)
            boxExtremePoint.X = halfExtents.X;
        else
            boxExtremePoint.X = -halfExtents.X;

        if (axis.Y > 0)
            boxExtremePoint.Y = halfExtents.Y;
        else
            boxExtremePoint.Y = -halfExtents.Y;

        if (axis.Z > 0)
            boxExtremePoint.Z = halfExtents.Z;
        else
            boxExtremePoint.Z = -halfExtents.Z;

        var boxMax = Vector3.Dot(boxExtremePoint, axis);
        var boxMin = -boxMax;

        return !(max + IntersectionEpsilon < boxMin || min - IntersectionEpsilon > boxMax);
    }

    /// <summary>Determines if a triangle in a box's local space intersects that box.</summary>
    public static bool Intersecting(ref Vector3 halfExtents, ref Vector3 a, ref Vector3 b, ref Vector3 c)
    {
        // Test each of the box's faces.
        Vector3 expandedHalfExtents;
        expandedHalfExtents.X = halfExtents.X + IntersectionEpsilon;
        expandedHalfExtents.Y = halfExtents.Y + IntersectionEpsilon;
        expandedHalfExtents.Z = halfExtents.Z + IntersectionEpsilon;
        if ((a.X > expandedHalfExtents.X && b.X > expandedHalfExtents.X && c.X > expandedHalfExtents.X) ||
            (a.Y > expandedHalfExtents.Y && b.Y > expandedHalfExtents.Y && c.Y > expandedHalfExtents.Y) ||
            (a.Z > expandedHalfExtents.Z && b.Z > expandedHalfExtents.Z && c.Z > expandedHalfExtents.Z) ||
            (a.X < -expandedHalfExtents.X && b.X < -expandedHalfExtents.X && c.X < -expandedHalfExtents.X) ||
            (a.Y < -expandedHalfExtents.Y && b.Y < -expandedHalfExtents.Y && c.Y < -expandedHalfExtents.Y) ||
            (a.Z < -expandedHalfExtents.Z && b.Z < -expandedHalfExtents.Z && c.Z < -expandedHalfExtents.Z))
        {
            return false;
        }

        // Test the triangle face.
        var ab = b - a;
        var ac = c - a;
        var normal = Vector3.Cross(ab, ac);
        var d = Vector3.Dot(normal, a);
        if (d < 0)
        {
            // Ensure the normal points away from the origin (choice is arbitrary, must be consistent).
            normal = -normal;
            d = -d;
        }

        Vector3 boxExtremePoint;
        if (normal.X > 0)
            boxExtremePoint.X = halfExtents.X;
        else
            boxExtremePoint.X = -halfExtents.X;

        if (normal.Y > 0)
            boxExtremePoint.Y = halfExtents.Y;
        else
            boxExtremePoint.Y = -halfExtents.Y;

        if (normal.Z > 0)
            boxExtremePoint.Z = halfExtents.Z;
        else
            boxExtremePoint.Z = -halfExtents.Z;

        var extremePointDot = Vector3.Dot(boxExtremePoint, normal);
        if (extremePointDot + IntersectionEpsilon < d)
        {
            return false;
        }

        // Test every edge direction. The three box directions all have two zeroes and one one,
        // so the cross product simplifies a lot.
        var bc = c - b;
        Vector3 direction;
        direction = new Vector3(0, -ab.Z, ab.Y);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(0, -ac.Z, ac.Y);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(0, -bc.Z, bc.Y);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;

        direction = new Vector3(ab.Z, 0, -ab.X);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(ac.Z, 0, -ac.X);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(bc.Z, 0, -bc.X);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;

        direction = new Vector3(-ab.Y, ab.X, 0);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(-ac.Y, ac.X, 0);
        if (!OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c)) return false;
        direction = new Vector3(-bc.Y, bc.X, 0);
        return OverlapsAlongAxis(ref direction, ref halfExtents, ref a, ref b, ref c);
    }
}

/// <summary>Integer voxel coordinate.</summary>
public struct Cell
{
    public int X, Y, Z;
}

/// <summary>Insertion-ordered voxel set (managed replacement for the upstream <c>QuickSet</c>).</summary>
internal sealed class CellSet
{
    private readonly List<Cell> _items;
    private readonly Dictionary<Cell, int> _indices;

    public CellSet(int capacity)
    {
        _items = new List<Cell>(capacity);
        _indices = new Dictionary<Cell, int>(capacity);
    }

    public int Count => _items.Count;

    public Cell this[int index] => _items[index];

    /// <summary>Adds the cell when new; returns true when it was added.</summary>
    public bool Add(Cell cell)
    {
        if (_indices.ContainsKey(cell)) return false;
        _indices.Add(cell, _items.Count);
        _items.Add(cell);
        return true;
    }

    public bool Contains(Cell cell) => _indices.ContainsKey(cell);

    public int IndexOf(Cell cell) => _indices.TryGetValue(cell, out var index) ? index : -1;

    public void Clear()
    {
        _items.Clear();
        _indices.Clear();
    }
}

public struct CellVertexIndices
{
    public int V000, V001, V010, V011, V100, V101, V110, V111;
}

public struct TetrahedronVertices
{
    public readonly int A, B, C, D;

    public TetrahedronVertices(int a, int b, int c, int d)
    {
        A = a;
        B = b;
        C = c;
        D = d;
    }
}

/// <summary>
///     Voxelizes a triangle soup and emits a lattice of welded nodes (upstream
///     <c>DumbTetrahedralizer</c>). Slow but simple; runs once per demo construction.
/// </summary>
public static class DumbTetrahedralizer
{
    private static void AddVertexSpatialIndex(ref Cell vertexSpatialIndex, CellSet vertexIndices, out int index)
    {
        index = vertexIndices.IndexOf(vertexSpatialIndex);
        if (index < 0)
        {
            index = vertexIndices.Count;
            vertexIndices.Add(vertexSpatialIndex);
        }
    }

    private struct VoxelizationBounds
    {
        /// <summary>Exclusive maximum voxel index along the X axis.</summary>
        public int X;

        /// <summary>Exclusive maximum voxel index along the Y axis.</summary>
        public int Y;

        /// <summary>Exclusive maximum voxel index along the Z axis.</summary>
        public int Z;
    }

    private static void RasterizeTriangle(ref Vector3 a, ref Vector3 b, ref Vector3 c, float cellSize, ref Vector3 gridOrigin, CellSet cells)
    {
        var gridA = a - gridOrigin;
        var gridB = b - gridOrigin;
        var gridC = c - gridOrigin;

        // Compute the bounding box of the triangle.
        var max = Vector3.Max(Vector3.Max(gridA, gridB), gridC);
        var min = Vector3.Min(Vector3.Min(gridA, gridB), gridC);
        var epsilon = new Vector3(1e-5f);
        min -= epsilon;
        max += epsilon;

        // Discretize the bounding box. All indices are positive, so we can truncate.
        var inverseCellSize = 1f / cellSize;
        var startX = (int)Math.Floor(min.X * inverseCellSize);
        var endX = (int)Math.Floor(max.X * inverseCellSize);
        var startY = (int)Math.Floor(min.Y * inverseCellSize);
        var endY = (int)Math.Floor(max.Y * inverseCellSize);
        var startZ = (int)Math.Floor(min.Z * inverseCellSize);
        var endZ = (int)Math.Floor(max.Z * inverseCellSize);

        // Test the triangle against each cell.
        var halfExtents = new Vector3(cellSize * 0.5f);
        for (var i = startX; i <= endX; ++i)
        {
            for (var j = startY; j <= endY; ++j)
            {
                for (var k = startZ; k <= endZ; ++k)
                {
                    var cellIndex = new Vector3(i, j, k);
                    var cellOrigin = cellSize * cellIndex + halfExtents;
                    var shiftedA = gridA - cellOrigin;
                    var shiftedB = gridB - cellOrigin;
                    var shiftedC = gridC - cellOrigin;

                    if (BoxTriangleCollider.Intersecting(ref halfExtents, ref shiftedA, ref shiftedB, ref shiftedC))
                    {
                        cells.Add(new Cell { X = i, Y = j, Z = k });
                    }
                }
            }
        }
    }

    private static bool TryFloodFill(Cell cell, ref VoxelizationBounds bounds, CellSet occupiedCells, CellSet newlyFilledCells, List<Cell> cellsToVisit)
    {
        if (cell.X > bounds.X || cell.Y > bounds.Y || cell.Z > bounds.Z || cell.X < -1 || cell.Y < -1 || cell.Z < -1)
        {
            // We've escaped the world; the start location was not inside a closed section.
            return false;
        }

        if (newlyFilledCells.Contains(cell) || occupiedCells.Contains(cell))
        {
            // Already traversed this cell before or during the current flood fill.
            return true;
        }

        newlyFilledCells.Add(cell);

        cellsToVisit.Add(new Cell { X = cell.X, Y = cell.Y, Z = cell.Z - 1 });
        cellsToVisit.Add(new Cell { X = cell.X, Y = cell.Y, Z = cell.Z + 1 });
        cellsToVisit.Add(new Cell { X = cell.X, Y = cell.Y - 1, Z = cell.Z });
        cellsToVisit.Add(new Cell { X = cell.X, Y = cell.Y + 1, Z = cell.Z });
        cellsToVisit.Add(new Cell { X = cell.X - 1, Y = cell.Y, Z = cell.Z });
        cellsToVisit.Add(new Cell { X = cell.X + 1, Y = cell.Y, Z = cell.Z });

        return true;
    }

    private static void InitiateFloodFill(Cell start, ref VoxelizationBounds bounds, CellSet occupiedCells, CellSet newlyFilledCells, List<Cell> cellsToVisit)
    {
        if (occupiedCells.Contains(start)) return;
        cellsToVisit.Add(start);
        while (cellsToVisit.Count > 0)
        {
            var cell = cellsToVisit[^1];
            cellsToVisit.RemoveAt(cellsToVisit.Count - 1);
            if (!TryFloodFill(cell, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit))
            {
                // The flood fill escaped the voxel bounds. Must be an open area; don't fill.
                cellsToVisit.Clear();
                newlyFilledCells.Clear();
                return;
            }
        }

        // Flood fill completed without reaching the voxel bounds. Dump newly filled cells.
        for (var i = 0; i < newlyFilledCells.Count; ++i)
        {
            occupiedCells.Add(newlyFilledCells[i]);
        }

        newlyFilledCells.Clear();
    }

    private static void FloodFillAdjacentCells(Cell cell, ref VoxelizationBounds bounds, CellSet occupiedCells, CellSet newlyFilledCells, List<Cell> cellsToVisit)
    {
        InitiateFloodFill(new Cell { X = cell.X + 1, Y = cell.Y, Z = cell.Z }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
        InitiateFloodFill(new Cell { X = cell.X - 1, Y = cell.Y, Z = cell.Z }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
        InitiateFloodFill(new Cell { X = cell.X, Y = cell.Y + 1, Z = cell.Z }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
        InitiateFloodFill(new Cell { X = cell.X, Y = cell.Y - 1, Z = cell.Z }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
        InitiateFloodFill(new Cell { X = cell.X, Y = cell.Y, Z = cell.Z + 1 }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
        InitiateFloodFill(new Cell { X = cell.X, Y = cell.Y, Z = cell.Z - 1 }, ref bounds, occupiedCells, newlyFilledCells, cellsToVisit);
    }

    public static void Tetrahedralize(
        Triangle[] triangles, float cellSize,
        out Vector3[] vertices, out Cell[] vertexSpatialIndices,
        out CellVertexIndices[] cellVertexIndices, out TetrahedronVertices[] tetrahedraVertexIndices)
    {
        // Compute the size of the 3d grid by scanning all vertices.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < triangles.Length; ++i)
        {
            ref var triangle = ref triangles[i];
            min = Vector3.Min(min, triangle.A);
            min = Vector3.Min(min, triangle.B);
            min = Vector3.Min(min, triangle.C);
            max = Vector3.Max(max, triangle.A);
            max = Vector3.Max(max, triangle.B);
            max = Vector3.Max(max, triangle.C);
        }

        // Add a little buffer.
        min -= new Vector3(cellSize);

        var cells = new CellSet(triangles.Length);
        for (var i = 0; i < triangles.Length; ++i)
        {
            ref var triangle = ref triangles[i];
            RasterizeTriangle(ref triangle.A, ref triangle.B, ref triangle.C, cellSize, ref min, cells);
        }

        if (cells.Count == 0)
            throw new ArgumentException("Mesh seems to have no volume; triangle rasterization occupied no cells.");

        VoxelizationBounds bounds;
        var size = max - min;
        var inverseCellSize = 1f / cellSize;
        bounds.X = (int)Math.Ceiling(inverseCellSize * size.X);
        bounds.Y = (int)Math.Ceiling(inverseCellSize * size.Y);
        bounds.Z = (int)Math.Ceiling(inverseCellSize * size.Z);

        // Perform a flood fill on every surface vertex.
        var floodFilledCells = new CellSet(32);
        var cellsToVisit = new List<Cell>(32);
        for (var i = cells.Count - 1; i >= 0; --i)
        {
            FloodFillAdjacentCells(cells[i], ref bounds, cells, floodFilledCells, cellsToVisit);
        }

        // Build the vertex list and per-cell vertex index lists.
        var vertexSpatialIndexSet = new CellSet(cells.Count * 4);
        var cellIndicesArray = new CellVertexIndices[cells.Count];
        for (var i = 0; i < cells.Count; ++i)
        {
            var cell = cells[i];
            CellVertexIndices cellIndices;
            var vertexSpatialIndex = cell;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V000);
            vertexSpatialIndex.X = cell.X;
            vertexSpatialIndex.Y = cell.Y;
            vertexSpatialIndex.Z = cell.Z + 1;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V001);
            vertexSpatialIndex.X = cell.X;
            vertexSpatialIndex.Y = cell.Y + 1;
            vertexSpatialIndex.Z = cell.Z;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V010);
            vertexSpatialIndex.X = cell.X;
            vertexSpatialIndex.Y = cell.Y + 1;
            vertexSpatialIndex.Z = cell.Z + 1;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V011);
            vertexSpatialIndex.X = cell.X + 1;
            vertexSpatialIndex.Y = cell.Y;
            vertexSpatialIndex.Z = cell.Z;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V100);
            vertexSpatialIndex.X = cell.X + 1;
            vertexSpatialIndex.Y = cell.Y;
            vertexSpatialIndex.Z = cell.Z + 1;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V101);
            vertexSpatialIndex.X = cell.X + 1;
            vertexSpatialIndex.Y = cell.Y + 1;
            vertexSpatialIndex.Z = cell.Z;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V110);
            vertexSpatialIndex.X = cell.X + 1;
            vertexSpatialIndex.Y = cell.Y + 1;
            vertexSpatialIndex.Z = cell.Z + 1;
            AddVertexSpatialIndex(ref vertexSpatialIndex, vertexSpatialIndexSet, out cellIndices.V111);

            cellIndicesArray[i] = cellIndices;
        }

        // Create the tetrahedra.
        var tetrahedra = new TetrahedronVertices[cellIndicesArray.Length * 5];
        var tetrahedronIndex = 0;
        for (var i = 0; i < cellIndicesArray.Length; ++i)
        {
            var cellIndices = cellIndicesArray[i];
            tetrahedra[tetrahedronIndex++] = new TetrahedronVertices(cellIndices.V010, cellIndices.V111, cellIndices.V001, cellIndices.V100); // Central tetrahedron
            tetrahedra[tetrahedronIndex++] = new TetrahedronVertices(cellIndices.V000, cellIndices.V001, cellIndices.V010, cellIndices.V100); // Origin tetrahedron
            tetrahedra[tetrahedronIndex++] = new TetrahedronVertices(cellIndices.V010, cellIndices.V100, cellIndices.V111, cellIndices.V110);
            tetrahedra[tetrahedronIndex++] = new TetrahedronVertices(cellIndices.V010, cellIndices.V001, cellIndices.V111, cellIndices.V011);
            tetrahedra[tetrahedronIndex++] = new TetrahedronVertices(cellIndices.V101, cellIndices.V001, cellIndices.V100, cellIndices.V111);
        }

        // Create the vertices.
        vertices = new Vector3[vertexSpatialIndexSet.Count];
        vertexSpatialIndices = new Cell[vertexSpatialIndexSet.Count];
        for (var i = 0; i < vertices.Length; ++i)
        {
            var index = vertexSpatialIndexSet[i];
            vertexSpatialIndices[i] = index;
            vertices[i] = new Vector3(index.X, index.Y, index.Z) * cellSize + min;
        }

        cellVertexIndices = cellIndicesArray;
        tetrahedraVertexIndices = tetrahedra;
    }
}
