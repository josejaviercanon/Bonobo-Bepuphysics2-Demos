using System.Numerics;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.BepuUtilities.Memory;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Mesh construction helper ported from the upstream demo harness
///     (<c>DemoMeshHelper.cs</c>, BepuPhysics2, Apache-2.0, Ross Nordby) — only the deformed
///     plane used by <see cref="CompoundDemo"/>.
/// </summary>
public static class DemoMeshHelper
{
    public static Mesh CreateDeformedPlane(int width, int height, Func<int, int, Vector3> deformer, Vector3 scaling, BufferPool pool)
    {
        pool.Take<Vector3>(width * height, out var vertices);
        for (var i = 0; i < width; ++i)
        {
            for (var j = 0; j < height; ++j)
            {
                vertices[width * j + i] = deformer(i, j);
            }
        }

        var quadWidth = width - 1;
        var quadHeight = height - 1;
        var triangleCount = quadWidth * quadHeight * 2;
        pool.Take<Triangle>(triangleCount, out var triangles);

        for (var i = 0; i < quadWidth; ++i)
        {
            for (var j = 0; j < quadHeight; ++j)
            {
                var triangleIndex = (j * quadWidth + i) * 2;
                ref var triangle0 = ref triangles[triangleIndex];
                ref var v00 = ref vertices[width * j + i];
                ref var v01 = ref vertices[width * j + i + 1];
                ref var v10 = ref vertices[width * (j + 1) + i];
                ref var v11 = ref vertices[width * (j + 1) + i + 1];
                triangle0.A = v00;
                triangle0.B = v01;
                triangle0.C = v10;
                ref var triangle1 = ref triangles[triangleIndex + 1];
                triangle1.A = v01;
                triangle1.B = v11;
                triangle1.C = v10;
            }
        }

        pool.Return(ref vertices);
        return new Mesh(triangles, scaling, pool);
    }
}
