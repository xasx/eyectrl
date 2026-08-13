namespace EyeCtrl.App.Startup;

/// <summary>Outcome of the phase-2 read-back.</summary>
/// <param name="IsSatisfied">True when every expected resource reported a scoped name.</param>
/// <param name="Failures">One human-readable line per problem; empty when satisfied.</param>
public sealed record InstanceScopeResult(bool IsSatisfied, IReadOnlyList<string> Failures);

/// <summary>
/// Phase 2: after wiring, before anything can act on input, confirm every resource actually took
/// its scoped name.
/// <para>
/// Deliberately a <b>read-back</b> rather than a re-derivation. Re-deriving would compute the same
/// expected value twice and agree with itself; reading back off the constructed objects is what
/// catches a receiver that accepts an injectable name and is never passed one.
/// </para>
/// <para>
/// Pure, so the failure modes are testable without launching WPF.
/// </para>
/// </summary>
public static class InstanceScopeVerifier
{
    /// <summary>
    /// Verifies reported resources against the expected set.
    /// </summary>
    /// <param name="reported">What the constructed objects said about themselves.</param>
    /// <param name="expectedNames">Every resource that must appear. A missing entry is a failure.</param>
    /// <param name="defaults">
    /// Optional default value per resource name. When supplied, a resource claiming
    /// <c>Scoped: true</c> while reporting its default value is a failure — the shape a missed
    /// wiring change takes when the reporter derives its flag independently of its name. Entries
    /// for resources that are not reported are ignored, so a stale key cannot manufacture a
    /// failure; the expected list is the single driver of what must exist.
    /// </param>
    public static InstanceScopeResult Verify(
        IReadOnlyList<InstanceResource> reported,
        IReadOnlyList<string> expectedNames,
        IReadOnlyDictionary<string, string>? defaults = null)
    {
        var failures = new List<string>();
        var byName = reported.ToDictionary(r => r.Name, StringComparer.Ordinal);

        foreach (var name in expectedNames)
        {
            if (!byName.TryGetValue(name, out var resource))
            {
                failures.Add(
                    $"{name} was not reported. A resource that exists but is not collected cannot be "
                    + "verified, so this is treated as unscoped.");
                continue;
            }

            // Unscoped is checked before the default-value comparison, and the `continue` matters:
            // an unscoped resource usually also equals its default, and reporting that instead
            // would send the reader hunting for a missing constructor argument when the flag itself
            // is what is false.
            if (!resource.Scoped)
            {
                failures.Add($"{name} is unscoped (effective value '{resource.EffectiveValue}').");
                continue;
            }

            if (defaults != null
                && defaults.TryGetValue(name, out var defaultValue)
                && string.Equals(resource.EffectiveValue, defaultValue, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{name} claims to be scoped but reports its default value '{defaultValue}'. "
                    + "Its owner probably accepts an injectable name and was never passed one.");
            }
        }

        return new InstanceScopeResult(failures.Count == 0, failures);
    }
}
