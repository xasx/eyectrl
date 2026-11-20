namespace EyeCtrl.App.Services;

using System;

/// <summary>
/// The guide's one-flow gate (the SafetySetupHost pattern, minus the Settings-window coupling):
/// TryBegin fails while a guide is open; the lease's Dispose releases — on the normal Restore
/// path or on any exception in the raise wiring (r1 F14's fallback).
/// </summary>
internal sealed class FirstRunGuideHost
{
    private bool _open;

    public bool TryBegin(out FirstRunGuideLease lease)
    {
        if (_open) { lease = null!; return false; }
        _open = true;
        lease = new FirstRunGuideLease(this);
        return true;
    }

    internal void End() => _open = false;
}

/// <summary>Disposable marker so an exception in the wiring releases the gate.</summary>
internal sealed class FirstRunGuideLease : IDisposable
{
    private FirstRunGuideHost? _host;
    internal FirstRunGuideLease(FirstRunGuideHost host) => _host = host;
    public void Dispose() { _host?.End(); _host = null; }
}
