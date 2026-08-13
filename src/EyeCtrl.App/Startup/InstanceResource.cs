namespace EyeCtrl.App.Startup;

/// <summary>
/// One process-external resource as reported by the object that owns it.
/// <para>
/// <c>EffectiveValue</c> is the name the resource is <b>configured</b> with, not proof it is bound:
/// the settings-writer mutex is created per <c>Save</c> call and may not bind until exit. Genuinely
/// bound at manifest time are the app singleton mutex, the dwell pipe and the Run value.
/// </para>
/// </summary>
/// <param name="Name">Stable identifier, e.g. <c>DwellPipe</c>.</param>
/// <param name="Scoped">Whether this resource took a scoped name.</param>
/// <param name="EffectiveValue">The name or path actually configured.</param>
public sealed record InstanceResource(string Name, bool Scoped, string EffectiveValue);
