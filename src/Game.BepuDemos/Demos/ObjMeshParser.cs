using System.Globalization;
using System.Numerics;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.BepuUtilities.Memory;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Minimal span-based Wavefront OBJ parser (positions + triangles only) replacing the
///     upstream <c>ObjLoader</c> NuGet dependency (no new packages allowed in this repo).
///     Handles <c>v</c> and <c>f</c> records, <c>a/b/c</c> index forms, negative
///     (relative) indices, multi-space/tab separators and fan-triangulates polygons.
///     Every other record type (<c>vn</c>, <c>vt</c>, <c>g</c>, <c>o</c>, <c>s</c>,
///     <c>usemtl</c>, <c>mtllib</c>, comments) is ignored. Parsing happens once at scene
///     construction, never inside a step.
/// </summary>
public static class ObjMeshParser
{
    public readonly struct MeshData
    {
        public readonly Vector3[] Vertices;
        public readonly int[] Indices;

        public MeshData(Vector3[] vertices, int[] indices)
        {
            Vertices = vertices;
            Indices = indices;
        }
    }

    /// <summary>Parses OBJ text into a vertex array plus a triangle index list.</summary>
    public static MeshData Parse(ReadOnlySpan<char> text)
    {
        var vertices = new List<Vector3>(1024);
        var indices = new List<int>(4096);

        var position = 0;
        while (position < text.Length)
        {
            var newline = text[position..].IndexOf('\n');
            var line = newline < 0 ? text[position..] : text.Slice(position, newline);
            position = newline < 0 ? text.Length : position + newline + 1;

            line = line.Trim();
            if (line.IsEmpty || line[0] == '#') continue;

            var separator = line.IndexOfAny(' ', '\t');
            var keyword = separator < 0 ? line : line[..separator];
            var rest = separator < 0 ? ReadOnlySpan<char>.Empty : line[(separator + 1)..];

            if (keyword.Length == 1 && keyword[0] == 'v')
            {
                if (TryReadVector3(rest, out var vertex)) vertices.Add(vertex);
            }
            else if (keyword.Length == 1 && keyword[0] == 'f')
            {
                ParseFace(rest, vertices.Count, indices);
            }
        }

        return new MeshData(vertices.ToArray(), indices.ToArray());
    }

    /// <summary>Builds a Bepu triangle mesh from parsed data (upstream <c>LoadModel</c> shape).</summary>
    public static Mesh CreateMesh(MeshData data, Vector3 scaling, BufferPool pool)
    {
        var triangleCount = data.Indices.Length / 3;
        pool.Take<Triangle>(triangleCount, out var triangles);
        for (var i = 0; i < triangleCount; ++i)
        {
            var baseIndex = i * 3;
            triangles[i] = new Triangle(
                data.Vertices[data.Indices[baseIndex]],
                data.Vertices[data.Indices[baseIndex + 1]],
                data.Vertices[data.Indices[baseIndex + 2]]);
        }

        return new Mesh(triangles, scaling, pool);
    }

    private static void ParseFace(ReadOnlySpan<char> rest, int vertexCount, List<int> indices)
    {
        var first = -1;
        var previous = -1;
        var index = 0;

        while (TryReadToken(rest, ref index, out var token))
        {
            var slash = token.IndexOf('/');
            var indexSpan = slash < 0 ? token : token[..slash];
            if (!int.TryParse(indexSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)) continue;

            var vertexIndex = raw < 0 ? vertexCount + raw : raw - 1;
            if ((uint)vertexIndex >= (uint)vertexCount) continue;

            if (first < 0)
            {
                first = vertexIndex;
            }
            else
            {
                // Fan triangulation: (first, previous, current).
                indices.Add(first);
                indices.Add(previous);
                indices.Add(vertexIndex);
            }

            previous = vertexIndex;
        }
    }

    private static bool TryReadVector3(ReadOnlySpan<char> line, out Vector3 vector)
    {
        vector = default;
        var index = 0;
        if (!TryReadToken(line, ref index, out var x) || !TryReadFloat(x, out var fx)) return false;
        if (!TryReadToken(line, ref index, out var y) || !TryReadFloat(y, out var fy)) return false;
        if (!TryReadToken(line, ref index, out var z) || !TryReadFloat(z, out var fz)) return false;

        vector = new Vector3(fx, fy, fz);
        return true;
    }

    private static bool TryReadToken(ReadOnlySpan<char> line, ref int index, out ReadOnlySpan<char> token)
    {
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) ++index;
        if (index >= line.Length)
        {
            token = ReadOnlySpan<char>.Empty;
            return false;
        }

        var start = index;
        while (index < line.Length && line[index] != ' ' && line[index] != '\t') ++index;
        token = line[start..index];
        return true;
    }

    private static bool TryReadFloat(ReadOnlySpan<char> token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
