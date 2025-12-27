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

    public PingPlannerState(double initialIntervalMm, double maxIntervalMm, double multiplier, double firstPingBiasMm)
    {
        _intervalMm = initialIntervalMm;
        _maxIntervalMm = maxIntervalMm;
        _multiplier = multiplier;
        _firstPingBiasMm = firstPingBiasMm;
        _lastPingExtrusionMm = 0;
    }

    public bool ShouldInsertPing(double totalEffectiveExtrusionMm)
    {
        return (totalEffectiveExtrusionMm - _lastPingExtrusionMm) > (_intervalMm - _firstPingBiasMm);
    }

    public void OnPingInserted(double atExtrusionMm)
    {
        _intervalMm = Math.Min(_maxIntervalMm, _intervalMm * _multiplier);
        _lastPingExtrusionMm = atExtrusionMm;
    }
}
