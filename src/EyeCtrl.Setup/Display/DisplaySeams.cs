namespace EyeCtrl.Setup.Display;

/// <summary>One step of the display-only progress screen, in the order the workflow reaches it.</summary>
public enum SetupStage
{
    /// <summary>Checking the package's files and signature.</summary>
    Verify,

    /// <summary>Closing the running EyeCtrl.</summary>
    Stop,

    /// <summary>Putting the new version in place.</summary>
    Swap,

    /// <summary>Starting the version.</summary>
    Start,
}

/// <summary>
/// The display-only screens S6e owns: the progress line during an update, rollback or uninstall,
/// and the result that closes itself. The workflow talks to this seam; production binds
/// <see cref="WpfSetupDisplay"/> and the tests bind a recorder.
/// </summary>
public interface ISetupDisplay : IDisposable
{
    /// <summary>Advances the progress text. Never throws into the workflow.</summary>
    void Stage(SetupStage stage);

    /// <summary>
    /// Shows the terminal result and returns when it has closed itself (10 s after a success,
    /// 60 s after a failure). Never throws into the workflow.
    /// </summary>
    void Finish(SetupResult result);
}

/// <summary>Creates the display for one verb. A factory, not one display, because a display is a
/// single screen lifecycle: it is disposed when its verb returns.</summary>
public interface ISetupDisplayFactory
{
    /// <summary>Creates the display for <paramref name="verb"/>; a verb with no screen gets the null display.</summary>
    ISetupDisplay Create(SetupVerb verb);
}

/// <summary>The display that shows nothing. Tests and every verb without a screen use it.</summary>
public sealed class NullSetupDisplay : ISetupDisplay
{
    /// <summary>The one instance (stateless).</summary>
    public static NullSetupDisplay Instance { get; } = new();

    private NullSetupDisplay()
    {
    }

    /// <inheritdoc />
    public void Stage(SetupStage stage)
    {
    }

    /// <inheritdoc />
    public void Finish(SetupResult result)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>The factory that creates only <see cref="NullSetupDisplay"/>.</summary>
public sealed class NullSetupDisplayFactory : ISetupDisplayFactory
{
    /// <summary>The one instance (stateless).</summary>
    public static NullSetupDisplayFactory Instance { get; } = new();

    private NullSetupDisplayFactory()
    {
    }

    /// <inheritdoc />
    public ISetupDisplay Create(SetupVerb verb) => NullSetupDisplay.Instance;
}
