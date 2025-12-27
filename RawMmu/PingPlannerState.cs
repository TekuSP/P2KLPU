using System;

/// <summary>
/// Tracks connected-mode ping spacing state during a RAW_MMU scan.
/// </summary>
/// <remarks>
/// In connected mode, pings are inserted based on effective extrusion distance.
/// This state machine implements an interval that grows by a multiplier up to a maximum.
///
/// This type is currently an implementation detail of <see cref="RawMmuScanner"/>.
/// </remarks>
/// <seealso cref="RawMmuScanner"/>
internal sealed class PingPlannerState
{
    private double _intervalMm;
    private readonly double _maxIntervalMm;
    private readonly double _multiplier;
    private readonly double _firstPingBiasMm;
    private double _lastPingExtrusionMm;

    /// <summary>
    /// Creates a ping planner state machine.
    /// </summary>
    /// <param name="initialIntervalMm">Initial interval in mm of effective extrusion.</param>
    /// <param name="maxIntervalMm">Maximum interval in mm after growth.</param>
    /// <param name="multiplier">Growth multiplier applied after each ping.</param>
    /// <param name="firstPingBiasMm">Bias used to bring the first ping slightly earlier.</param>
    public PingPlannerState(double initialIntervalMm, double maxIntervalMm, double multiplier, double firstPingBiasMm)
    {
        _intervalMm = initialIntervalMm;
        _maxIntervalMm = maxIntervalMm;
        _multiplier = multiplier;
        _firstPingBiasMm = firstPingBiasMm;
        _lastPingExtrusionMm = 0;
    }

    /// <summary>
    /// Determines whether a ping should be inserted at the current effective extrusion distance.
    /// </summary>
    /// <param name="totalEffectiveExtrusionMm">Total effective extrusion in mm.</param>
    /// <returns><see langword="true"/> when the ping threshold has been reached.</returns>
    public bool ShouldInsertPing(double totalEffectiveExtrusionMm)
    {
        return (totalEffectiveExtrusionMm - _lastPingExtrusionMm) > (_intervalMm - _firstPingBiasMm);
    }

    /// <summary>
    /// Updates internal interval state after a ping has been inserted.
    /// </summary>
    /// <param name="atExtrusionMm">Effective extrusion position where the ping was inserted.</param>
    public void OnPingInserted(double atExtrusionMm)
    {
        _intervalMm = Math.Min(_maxIntervalMm, _intervalMm * _multiplier);
        _lastPingExtrusionMm = atExtrusionMm;
    }
}
