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
    /// Members every implementer inherits from <see cref="object"/> or declares for its own bookkeeping,
    /// which a decorator is not obliged to re-declare.
    /// </summary>
    private static readonly HashSet<string> NotForwardingConcerns = new(StringComparer.Ordinal)
    {
        // The decorator's own identity, not the inner provider's.
        "get_Inner",
    };

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
            .Where(name => !NotForwardingConcerns.Contains(name))
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
