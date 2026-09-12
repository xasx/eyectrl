namespace EyeCtrl.Architecture.Tests;

using System.Reflection;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;

/// <summary>
/// A ratchet for a failure mode the compiler structurally cannot report: a decorator that silently
/// stops forwarding an interface member.
/// <para>
/// <see cref="IGazeProvider"/> uses default interface methods with empty bodies so provider fakes
/// compile unchanged — <c>UpdateScreenBounds</c>, <c>TryGetLatestStatus</c> and
/// <c>TrackStatusReceived</c> all have one. That convenience has a sharp edge for a DECORATOR: omit a
/// member and it does not fail to compile, it binds to the no-op and swallows the call. Every test
/// keeps passing, because a fake that also inherits the no-op cannot tell a forwarded call from a
/// dropped one.
/// </para>
/// <para>
/// It has already happened once: <c>RecordingGazeProvider</c> shipped without forwarding
/// <c>UpdateScreenBounds</c>, so with recording enabled a display change never reached the provider
/// that rescales normalised coordinates — confining the user's gaze to the old pixel space for the
/// rest of the session. This test fails the build the next time a member is added to the interface and
/// not to the decorator, which is the only point at which the omission is cheap to fix.
/// </para>
/// </summary>
public class DecoratorForwardingTests
{
    /// <summary>
    /// What this ratchet does NOT catch. Worth stating, because a guard whose limits are unknown gets
    /// trusted past them:
    /// <list type="bullet">
    /// <item>It matches on member NAME, so an added overload of a member the decorator already
    /// declares slips through — the decorator would satisfy the name and still not forward the new
    /// signature.</item>
    /// <item>It does not walk base interfaces. <see cref="IGazeProvider"/> extends only
    /// <see cref="IDisposable"/> today, whose member the decorator declares anyway; a future base
    /// interface would go unchecked.</item>
    /// <item>It scans only <see cref="IGazeProviderDecorator"/>'s own assembly, so a decorator defined
    /// in Platform or App would not be discovered by the coverage test below.</item>
    /// </list>
    /// Each is a widening of the same reflection, deliberately not done now: the failure actually
    /// observed was a whole member silently missing, and that is what this catches.
    /// <para>
    /// There is no exclusion set. An earlier version carried one holding <c>get_Inner</c>, which could
    /// never match — <c>Inner</c> is declared on <see cref="IGazeProviderDecorator"/>, and the filter
    /// runs over <see cref="IGazeProvider"/>'s members. An exclusion set that excludes nothing is worse
    /// than none: it reads as a reviewed list of known-safe omissions, so the next person adds a real
    /// exclusion to it without noticing that nothing was ever being filtered.
    /// </para>
    /// </summary>
    [Fact]
    public void RecordingGazeProvider_DeclaresEveryIGazeProviderMember()
    {
        // Declared members only: an inherited default implementation is exactly the bug being guarded
        // against, so "does the type resolve this member" is the wrong question. The question is
        // whether the decorator wrote a forwarding body of its own.
        var declared = typeof(RecordingGazeProvider)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = typeof(IGazeProvider)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(name => !declared.Contains(name))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.That(missing).IsEmpty();
    }

    [Fact]
    public void EveryIGazeProviderDecorator_IsCoveredByThisRatchet()
    {
        // If a second decorator appears, it inherits the same hazard and this test must grow to cover
        // it. Failing here is the reminder — the alternative is a ratchet that quietly protects only
        // the one type it was written for.
        var decorators = typeof(IGazeProviderDecorator).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IGazeProviderDecorator).IsAssignableFrom(t))
            .ToList();

        Check.That(decorators).ContainsExactly(typeof(RecordingGazeProvider));
    }
}
