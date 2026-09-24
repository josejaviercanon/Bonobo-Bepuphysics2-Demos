using System.Reflection;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Loads the vendored upstream demo assets embedded in this assembly. Native AOT never
///     reads content files from disk; the OBJ text is parsed by <see cref="ObjMeshParser"/>
///     and the client loads its own copy of the same asset from the app bundle.
/// </summary>
public static class DemoContent
{
    public const string NewtObjResourceName = "Game.BepuDemos.Content.newt.obj";

    private static string? _newtObjText;

    /// <summary>Cached OBJ text for the upstream newt model (parsed once per process).</summary>
    public static string NewtObjText => _newtObjText ??= ReadResource(NewtObjResourceName);

    private static string ReadResource(string name)
    {
        using var stream = typeof(DemoContent).GetTypeInfo().Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
