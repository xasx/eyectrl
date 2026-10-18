namespace EyeCtrl.LM;

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

/// <summary>One DirectML attempt: how to append its provider, and the adapter name for the log.</summary>
internal sealed record GpuAttempt(Action<SessionOptions> Append, string? AdapterName, bool IsDefault);

/// <summary>
/// Creates the inference session through the GPU attempts in order, then the CPU. A failed
/// attempt (the append itself or the session creation) is logged with its exception and the next
/// one is tried; the successful GPU attempt is logged with the adapter it used.
/// </summary>
internal static class DmlSessionSetup
{
    internal static T CreateSessionWithFallback<T>(
        string modelPath,
        IReadOnlyList<GpuAttempt> attempts,
        Func<string, SessionOptions, T> createSession,
        ILogger? logger,
        string? modelLabel)
        where T : class
    {
        foreach (var attempt in attempts)
        {
            SessionOptions? options = null;
            try
            {
                options = new SessionOptions();
                attempt.Append(options);
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

                var session = createSession(modelPath, options);
                LogUsingGpu(logger, modelLabel, attempt);
                return session;
            }
            catch (Exception ex)
            {
                if (attempt.IsDefault)
                {
                    logger?.LogWarning(ex, "DirectML not available, falling back to CPU");
                }
                else
                {
                    logger?.LogWarning(ex, "DirectML on {Adapter} failed, falling back to the default adapter",
                        attempt.AdapterName);
                }
            }
            finally
            {
                options?.Dispose();
            }
        }

        using var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return createSession(modelPath, cpuOptions);
    }

    private static void LogUsingGpu(ILogger? logger, string? modelLabel, GpuAttempt attempt)
    {
        if (logger == null) return;

        if (attempt.IsDefault)
        {
            logger.LogInformation("Using DirectML (GPU) for {Model} on the default adapter", modelLabel);
        }
        else if (!string.IsNullOrWhiteSpace(attempt.AdapterName))
        {
            logger.LogInformation("Using DirectML (GPU) for {Model} on {Adapter}", modelLabel, attempt.AdapterName);
        }
        else
        {
            logger.LogInformation("Using DirectML (GPU) for {Model}", modelLabel);
        }
    }
}
