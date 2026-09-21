using System.Collections.Generic;

/// <summary>How an injected block relates to its anchor line.</summary>
enum TowerInjectionKind
{
    /// <summary>The block replaces the original line (used at toolchange commands).</summary>
    ReplaceLine,

    /// <summary>The block is emitted after the original line (used at layer markers).</summary>
    InsertAfterLine,

    /// <summary>The original line is dropped and nothing is emitted (model extrusion relocated into a purge visit).</summary>
    RemoveLine,
}

/// <summary>
/// A generated G-code block anchored to an input line, produced by the tower planner and
/// replayed by pass 2. <see cref="EffectiveEMm"/> is accounted into the effective extrusion
/// timeline at the anchor line, so splices, junctions, and pings all include generated purge.
/// </summary>
/// <seealso cref="RawMmuScanner"/>
/// <seealso cref="CustomTowerGenerator"/>
sealed record TowerInjection(
    TowerInjectionKind Kind,
    IReadOnlyList<string> Lines,
    double EffectiveEMm);
