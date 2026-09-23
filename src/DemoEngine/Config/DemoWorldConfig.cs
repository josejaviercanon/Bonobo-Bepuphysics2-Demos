namespace DemoEngine.Config;

/// <summary>
///     World configuration passed to module factories. Mirrors the scalar fields of the
///     engine's <c>Game.Engine.Config.GameWorldConfig</c> that demos actually consume (this
///     repo has no binary config file — the defaults below are the engine defaults).
/// </summary>
public sealed record DemoWorldConfig
{
    public int EntityCount { get; init; } = 12;

    public double GravityX { get; init; }
    public double GravityY { get; init; } = -9.81d;
    public double GravityZ { get; init; }

    public double ArenaHalfWidth { get; init; } = 45d;
    public double ArenaHalfHeight { get; init; } = 20d;
    public double ArenaHalfDepth { get; init; } = 45d;
    public double ArenaFloorY { get; init; } = -4d;

    public double SpawnSpeedBase { get; init; } = 8d;
    public double SpawnSpeedStep { get; init; } = 4d;

    /// <summary>Deterministic defaults matching the engine's <c>GameWorldConfig.Default</c>.</summary>
    public static DemoWorldConfig Default { get; } = new();
}
