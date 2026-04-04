namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>
/// Tests for DwellDetector thread safety.
/// Bug: Dictionary at line 6 is not thread-safe and can cause collection modified exceptions
/// when event handlers modify the collection during enumeration.
/// </summary>
public class DwellDetectorThreadSafetyTests
{
    [Fact]
    public void ConcurrentAddRemove_ShouldNotThrow()
    {
        var detector = new DwellDetector();
        var exceptions = new List<Exception>();
        var tasks = new List<Task>();

        // Run concurrent add/remove operations
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int j = 0; j < 100; j++)
                    {
                        var target = new DwellTarget($"target-{index}-{j}", new PhysicalPoint(100, 100), 50, 200);
                        detector.AddTarget(target);
                        Thread.Sleep(1); // Small delay to increase chance of race condition
                        detector.RemoveTarget($"target-{index}-{j}");
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Should not have any collection modified exceptions
        Assert.Empty(exceptions);
    }

    [Fact]
    public void ConcurrentUpdateWhileModifyingCollection_ShouldNotThrow()
    {
        var detector = new DwellDetector();
        var exceptions = new List<Exception>();
        var tasks = new List<Task>();

        // Add initial targets
        for (int i = 0; i < 5; i++)
        {
            detector.AddTarget(new DwellTarget($"target-{i}", new PhysicalPoint(100 + i * 50, 100), 50, 200));
        }

        // Thread 1: Continuously update with gaze points
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 500; i++)
                {
                    detector.Update(new PhysicalPoint(100, 100), i * 1000L);
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        // Thread 2: Continuously add/remove targets while Update is enumerating
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    detector.AddTarget(new DwellTarget($"dynamic-{i}", new PhysicalPoint(200, 200), 50, 200));
                    Thread.Sleep(2);
                    detector.RemoveTarget($"dynamic-{i}");
                    Thread.Sleep(2);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        Task.WaitAll(tasks.ToArray());

        // Should not have any collection modified exceptions
        Assert.Empty(exceptions);
    }

    [Fact]
    public void EventHandlerModifyingCollection_ShouldNotThrow()
    {
        var detector = new DwellDetector(cooldownMs: 0);
        var exceptions = new List<Exception>();

        // Add targets
        for (int i = 0; i < 5; i++)
        {
            detector.AddTarget(new DwellTarget($"target-{i}", new PhysicalPoint(100 + i * 20, 100), 30, 100));
        }

        // Event handler that modifies collection when target is activated
        detector.TargetActivated += (id) =>
        {
            try
            {
                // This simulates real-world scenario where activation handler removes targets
                detector.RemoveTarget(id);

                // And potentially adds new ones
                var newTarget = new DwellTarget($"new-{id}", new PhysicalPoint(500, 500), 50, 200);
                detector.AddTarget(newTarget);
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        };

        // Trigger activations by dwelling on each target
        for (int i = 0; i < 5; i++)
        {
            var point = new PhysicalPoint(100 + i * 20, 100);

            // Dwell for enough time to trigger activation
            for (int t = 0; t < 15; t++)
            {
                try
                {
                    detector.Update(point, t * 10_000L);
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        }

        // Should not have any collection modified exceptions
        Assert.Empty(exceptions);
    }

    [Fact]
    public void ConcurrentClearTargets_ShouldNotThrow()
    {
        var detector = new DwellDetector();
        var exceptions = new List<Exception>();
        var tasks = new List<Task>();

        // Thread 1: Add targets
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    detector.AddTarget(new DwellTarget($"target-{i}", new PhysicalPoint(100, 100), 50, 200));
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        // Thread 2: Clear targets
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(5);
                    detector.ClearTargets();
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        // Thread 3: Update with gaze points
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 200; i++)
                {
                    detector.Update(new PhysicalPoint(100, 100), i * 1000L);
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        Task.WaitAll(tasks.ToArray());

        // Should not have any exceptions
        Assert.Empty(exceptions);
    }

    [Fact]
    public void ConcurrentUpdateTargetCenter_ShouldNotThrow()
    {
        var detector = new DwellDetector();
        var exceptions = new List<Exception>();

        // Add targets
        for (int i = 0; i < 10; i++)
        {
            detector.AddTarget(new DwellTarget($"target-{i}", new PhysicalPoint(100, 100), 50, 200));
        }

        var tasks = new List<Task>();

        // Multiple threads updating target centers
        for (int t = 0; t < 5; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        for (int j = 0; j < 10; j++)
                        {
                            detector.UpdateTargetCenter($"target-{j}", new PhysicalPoint(100 + threadId, 100 + i));
                        }
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        // Thread updating with gaze points
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 500; i++)
                {
                    detector.Update(new PhysicalPoint(100, 100), i * 1000L);
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        Task.WaitAll(tasks.ToArray());

        // Should not have any exceptions
        Assert.Empty(exceptions);
    }

    [Fact]
    public void HighFrequencyUpdates_WithConcurrentModifications_ShouldNotThrow()
    {
        // Simulates real-world scenario: 30 Hz gaze updates while UI modifies targets
        var detector = new DwellDetector();
        var exceptions = new List<Exception>();
        var tasks = new List<Task>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Thread 1: High frequency gaze updates (simulate 30 Hz eye tracker)
        tasks.Add(Task.Run(() =>
        {
            try
            {
                long timestamp = 0;
                while (stopwatch.ElapsedMilliseconds < 1000) // Run for 1 second
                {
                    detector.Update(new PhysicalPoint(100 + (timestamp % 100), 100), timestamp);
                    timestamp += 33_333; // ~30 Hz
                    Thread.Sleep(33);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        // Thread 2: Simulate UI adding/removing targets (slower frequency)
        tasks.Add(Task.Run(() =>
        {
            try
            {
                int counter = 0;
                while (stopwatch.ElapsedMilliseconds < 1000)
                {
                    detector.AddTarget(new DwellTarget($"ui-target-{counter}", new PhysicalPoint(150, 150), 50, 200));
                    Thread.Sleep(50);
                    detector.RemoveTarget($"ui-target-{counter}");
                    counter++;
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        }));

        Task.WaitAll(tasks.ToArray());

        // Should complete without exceptions
        Assert.Empty(exceptions);
    }
}
