# In-App Gaze Calibration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add in-app gaze recalibration: collect user gaze at known screen positions, train an affine correction transform, apply it as a pre-filter in `GazeEngine.OnGazePoint` before gap interpolation.

**Architecture:** Core types/interfaces in `EyeCtrl.Core.Calibration` (no WPF or WinForms dependencies). `CalibrationService` receives screen dimensions as parameters. `CalibrationViewModel` injects `IGazeProvider` and subscribes to raw gaze events during session. `CalibrationWindow` uses `OverlayWindow` base class with code-behind positioning (no XAML converters). `EventCoordinator` follows existing `_context.Messenger.Register<EventCoordinator, T>` pattern.

**Tech Stack:** .NET 10, C#, WPF, CommunityToolkit.Mvvm, xUnit, hand-rolled Gaussian elimination with `double` arithmetic (no external dependencies).

---

### Task 1: Core types — TransformType, ICalibrationTransform, CalibrationState

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/TransformType.cs`
- Create: `src/EyeCtrl.Core/Calibration/ICalibrationTransform.cs`
- Create: `src/EyeCtrl.Core/Calibration/CalibrationState.cs`

- [ ] **Step 1: Write TransformType enum**

```csharp
namespace EyeCtrl.Core.Calibration;

public enum TransformType
{
    Affine
}
```

- [ ] **Step 2: Write CalibrationState enum**

```csharp
namespace EyeCtrl.Core.Calibration;

public enum CalibrationState
{
    Idle,
    Collecting,
    Training,
    Active,
    Failed,
    Cancelled
}
```

- [ ] **Step 3: Write ICalibrationTransform interface**

```csharp
namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public interface ICalibrationTransform
{
    PhysicalPoint Apply(PhysicalPoint rawGaze);
    double[] GetParameters();
    void SetParameters(double[] parameters);
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/TransformType.cs src/EyeCtrl.Core/Calibration/CalibrationState.cs src/EyeCtrl.Core/Calibration/ICalibrationTransform.cs
git commit -m "feat: add calibration core types — TransformType, CalibrationState, ICalibrationTransform"
```

---

### Task 2: CalibrationPoint and CalibrationData

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/CalibrationPoint.cs`
- Create: `src/EyeCtrl.Core/Calibration/CalibrationData.cs`

- [ ] **Step 1: Write CalibrationPoint**

```csharp
namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public readonly record struct CalibrationPoint(
    PhysicalPoint Target,
    PhysicalPoint MeanGaze,
    double StdDevX,
    double StdDevY,
    int SampleCount)
{
    public double EuclideanError() =>
        Math.Sqrt(
            (MeanGaze.X - Target.X) * (MeanGaze.X - Target.X) +
            (MeanGaze.Y - Target.Y) * (MeanGaze.Y - Target.Y));
}
```

- [ ] **Step 2: Write CalibrationData**

```csharp
namespace EyeCtrl.Core.Calibration;

public sealed class CalibrationData
{
    public TransformType Type { get; init; }
    public double[] TransformParams { get; init; } = Array.Empty<double>();
    public CalibrationPoint[] Points { get; init; } = Array.Empty<CalibrationPoint>();
    public DateTimeOffset CalibratedAt { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public string MonitorDeviceName { get; init; } = string.Empty;

    public double ComputeMeanErrorPixels()
    {
        if (Points.Length == 0) return double.PositiveInfinity;
        return Points.Average(p => p.EuclideanError());
    }

    public double ComputeQualityPercent()
    {
        var diagonal = Math.Sqrt(ScreenWidth * ScreenWidth + ScreenHeight * ScreenHeight);
        if (diagonal <= 0) return 100;
        return (ComputeMeanErrorPixels() / diagonal) * 100;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/CalibrationPoint.cs src/EyeCtrl.Core/Calibration/CalibrationData.cs
git commit -m "feat: add CalibrationPoint and CalibrationData types"
```

---

### Task 3: CalibrationPointCollector with tests

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/CalibrationPointCollector.cs`
- Create: `tests/EyeCtrl.Core.Tests/CalibrationPointCollectorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationPointCollectorTests
{
    [Fact]
    public void End_ReturnsCalibrationPoint_WithAveragedGaze()
    {
        // settleWindowMs=0 so no samples are discarded
        var collector = new CalibrationPointCollector(new PhysicalPoint(500, 500), settleWindowMs: 0);

        collector.AddRawSample(new PhysicalPoint(510, 490));
        collector.AddRawSample(new PhysicalPoint(530, 510));
        collector.AddRawSample(new PhysicalPoint(510, 500));

        var point = collector.End();

        Assert.Equal(500, point.Target.X);
        Assert.Equal(500, point.Target.Y);
        Assert.Equal(3, point.SampleCount);
        // Mean: (510+530+510)/3 = 516.67, (490+510+500)/3 = 500
        Assert.True(Math.Abs(point.MeanGaze.X - 516.67) < 0.1);
        Assert.True(Math.Abs(point.MeanGaze.Y - 500.0) < 0.1);
        Assert.True(point.StdDevX > 0);
        Assert.True(point.StdDevY > 0);
    }

    [Fact]
    public void SettleWindow_DiscardsEarlySamples()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(100, 100), settleWindowMs: 50);

        // Sample at t≈0 — should be discarded (under settle window)
        collector.AddRawSample(new PhysicalPoint(999, 999));

        // Wait past settle window
        Thread.Sleep(60);

        collector.AddRawSample(new PhysicalPoint(105, 95));
        collector.AddRawSample(new PhysicalPoint(95, 105));

        var point = collector.End();

        Assert.Equal(2, point.SampleCount);
        // Mean should be based on the two post-settle samples only
        Assert.True(Math.Abs(point.MeanGaze.X - 100) < 10);
        Assert.True(Math.Abs(point.MeanGaze.Y - 100) < 10);
    }

    [Fact]
    public void End_Throws_WhenNoSamplesCollected()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(100, 100), settleWindowMs: 200);
        collector.AddRawSample(new PhysicalPoint(100, 100)); // discarded by settle
        Assert.Throws<InvalidOperationException>(() => collector.End());
    }

    [Fact]
    public void IsCollecting_TrueBeforeEnd_FalseAfterEnd()
    {
        var collector = new CalibrationPointCollector(new PhysicalPoint(0, 0), settleWindowMs: 0);
        collector.AddRawSample(new PhysicalPoint(5, 5));
        Assert.True(collector.IsCollecting);
        collector.End();
        Assert.False(collector.IsCollecting);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationPointCollectorTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Write CalibrationPointCollector**

```csharp
namespace EyeCtrl.Core.Calibration;

using System.Collections.Concurrent;
using EyeCtrl.Core.Models;

public class CalibrationPointCollector
{
    private readonly PhysicalPoint _target;
    private readonly long _settleWindowMs;
    private readonly long _createdAt;
    private readonly ConcurrentBag<PhysicalPoint> _samples = new();
    private volatile bool _ended;

    public bool IsCollecting => !_ended;

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public CalibrationPointCollector(PhysicalPoint target, int settleWindowMs)
    {
        _target = target;
        _settleWindowMs = settleWindowMs;
        _createdAt = NowMs();
    }

    public void AddRawSample(PhysicalPoint gaze)
    {
        if (_ended) return;
        var elapsed = NowMs() - _createdAt;
        if (elapsed < _settleWindowMs) return;
        _samples.Add(gaze);
    }

    public CalibrationPoint End()
    {
        _ended = true;
        var samples = _samples.ToArray();
        if (samples.Length == 0)
            throw new InvalidOperationException("No samples collected after settle window.");

        var n = samples.Length;
        var meanX = samples.Average(s => s.X);
        var meanY = samples.Average(s => s.Y);
        var stdX = Math.Sqrt(samples.Sum(s => (s.X - meanX) * (s.X - meanX)) / n);
        var stdY = Math.Sqrt(samples.Sum(s => (s.Y - meanY) * (s.Y - meanY)) / n);

        return new CalibrationPoint(_target, new PhysicalPoint(meanX, meanY), stdX, stdY, n);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationPointCollectorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/CalibrationPointCollector.cs tests/EyeCtrl.Core.Tests/CalibrationPointCollectorTests.cs
git commit -m "feat: add CalibrationPointCollector with settle window and tests"
```

---

### Task 4: AffineTransform with tests

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/AffineTransform.cs`
- Create: `tests/EyeCtrl.Core.Tests/AffineTransformTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class AffineTransformTests
{
    [Fact]
    public void Fit_Identity_ReturnsNearIdentity()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(0, 0), 0, 0, 30),
            new(new PhysicalPoint(1920, 0), new PhysicalPoint(1920, 0), 0, 0, 30),
            new(new PhysicalPoint(0, 1080), new PhysicalPoint(0, 1080), 0, 0, 30),
        };

        var transform = AffineTransform.Fit(points);

        var result = transform.Apply(new PhysicalPoint(960, 540));
        Assert.True(Math.Abs(result.X - 960) < 1.0);
        Assert.True(Math.Abs(result.Y - 540) < 1.0);
    }

    [Fact]
    public void Fit_TranslationOffset_Corrects()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(100, 100), new PhysicalPoint(110, 80), 0, 0, 30),
            new(new PhysicalPoint(1820, 100), new PhysicalPoint(1830, 80), 0, 0, 30),
            new(new PhysicalPoint(100, 980), new PhysicalPoint(110, 960), 0, 0, 30),
        };

        var transform = AffineTransform.Fit(points);

        // Gaze at (970, 90): transform should map it near target (960, 100)
        var result = transform.Apply(new PhysicalPoint(970, 90));
        Assert.True(Math.Abs(result.X - 960) < 1.5);
        Assert.True(Math.Abs(result.Y - 100) < 1.5);
    }

    [Fact]
    public void GetParameters_SetParameters_RoundTrips()
    {
        var points = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(10, 20), 0, 0, 30),
            new(new PhysicalPoint(1920, 0), new PhysicalPoint(1930, 20), 0, 0, 30),
            new(new PhysicalPoint(0, 1080), new PhysicalPoint(10, 1100), 0, 0, 30),
        };

        var t1 = AffineTransform.Fit(points);
        var original = t1.Apply(new PhysicalPoint(500, 500));

        var t2 = new AffineTransform();
        t2.SetParameters(t1.GetParameters());

        var roundtripped = t2.Apply(new PhysicalPoint(500, 500));
        Assert.Equal(original.X, roundtripped.X, 6);
        Assert.Equal(original.Y, roundtripped.Y, 6);
    }

    [Fact]
    public void Fit_Throws_WhenLessThan3Points()
    {
        var tooFew = new CalibrationPoint[]
        {
            new(new PhysicalPoint(0, 0), new PhysicalPoint(5, 5), 0, 0, 30),
            new(new PhysicalPoint(1920, 1080), new PhysicalPoint(1920, 1080), 0, 0, 30),
        };

        Assert.Throws<ArgumentException>(() => AffineTransform.Fit(tooFew));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AffineTransformTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Write AffineTransform using double arithmetic**

```csharp
namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public class AffineTransform : ICalibrationTransform
{
    // x' = a*x + b*y + c,  y' = d*x + e*y + f
    private double _a = 1, _b = 0, _c = 0;
    private double _d = 0, _e = 1, _f = 0;

    public PhysicalPoint Apply(PhysicalPoint rawGaze)
    {
        var x = _a * rawGaze.X + _b * rawGaze.Y + _c;
        var y = _d * rawGaze.X + _e * rawGaze.Y + _f;
        return new PhysicalPoint(x, y);
    }

    public double[] GetParameters() => new[] { _a, _b, _c, _d, _e, _f };

    public void SetParameters(double[] parameters)
    {
        if (parameters.Length != 6)
            throw new ArgumentException("AffineTransform requires exactly 6 parameters.");
        _a = parameters[0]; _b = parameters[1]; _c = parameters[2];
        _d = parameters[3]; _e = parameters[4]; _f = parameters[5];
    }

    public static AffineTransform Fit(CalibrationPoint[] points)
    {
        if (points.Length < 3)
            throw new ArgumentException("Affine transform requires at least 3 calibration points.");

        var n = points.Length;
        // Solve: TargetX = a*GazeX + b*GazeY + c  (row 2*i)
        //        TargetY = d*GazeX + e*GazeY + f  (row 2*i+1)
        var A = new double[2 * n, 6];
        var B = new double[2 * n];

        for (int i = 0; i < n; i++)
        {
            var gx = points[i].MeanGaze.X;
            var gy = points[i].MeanGaze.Y;

            A[2 * i, 0] = gx; A[2 * i, 1] = gy; A[2 * i, 2] = 1;
            B[2 * i] = points[i].Target.X;

            A[2 * i + 1, 3] = gx; A[2 * i + 1, 4] = gy; A[2 * i + 1, 5] = 1;
            B[2 * i + 1] = points[i].Target.Y;
        }

        var x = SolveLeastSquares(A, B, 6, 2 * n);

        return new AffineTransform
        {
            _a = x[0], _b = x[1], _c = x[2],
            _d = x[3], _e = x[4], _f = x[5],
        };
    }

    private static double[] SolveLeastSquares(double[,] A, double[] b, int cols, int rows)
    {
        // Normal equations: A^T A x = A^T b
        var ATA = new double[cols, cols];
        var ATb = new double[cols];

        for (int i = 0; i < cols; i++)
        {
            for (int j = 0; j < cols; j++)
            {
                double sum = 0;
                for (int k = 0; k < rows; k++)
                    sum += A[k, i] * A[k, j];
                ATA[i, j] = sum;
            }
            double sumB = 0;
            for (int k = 0; k < rows; k++)
                sumB += A[k, i] * b[k];
            ATb[i] = sumB;
        }

        return GaussianElimination(ATA, ATb, cols);
    }

    private static double[] GaussianElimination(double[,] a, double[] b, int n)
    {
        var mat = (double[,])a.Clone();
        var rhs = (double[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            int maxRow = col;
            for (int row = col + 1; row < n; row++)
                if (Math.Abs(mat[row, col]) > Math.Abs(mat[maxRow, col]))
                    maxRow = row;

            for (int j = 0; j < n; j++)
                (mat[col, j], mat[maxRow, j]) = (mat[maxRow, j], mat[col, j]);
            (rhs[col], rhs[maxRow]) = (rhs[maxRow], rhs[col]);

            for (int row = col + 1; row < n; row++)
            {
                double factor = mat[row, col] / mat[col, col];
                for (int j = col; j < n; j++)
                    mat[row, j] -= factor * mat[col, j];
                rhs[row] -= factor * rhs[col];
            }
        }

        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = 0;
            for (int j = i + 1; j < n; j++)
                sum += mat[i, j] * x[j];
            x[i] = (rhs[i] - sum) / mat[i, i];
        }

        return x;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AffineTransformTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/AffineTransform.cs tests/EyeCtrl.Core.Tests/AffineTransformTests.cs
git commit -m "feat: add AffineTransform with double-precision least-squares fitting"
```

---

### Task 5: CalibrationTargetPositionHelper with tests

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs`
- Create: `tests/EyeCtrl.Core.Tests/CalibrationTargetPositionHelperTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationTargetPositionHelperTests
{
    [Fact]
    public void GetPositions_3Points_ReturnsTL_TR_BC()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var positions = CalibrationTargetPositionHelper.GetPositions(3, screen);

        Assert.Equal(3, positions.Count);

        var margin = 0.1;
        var expectedTL = new PhysicalPoint(screen.X * margin, screen.Y * margin);
        var expectedTR = new PhysicalPoint(screen.X * (1 - margin), screen.Y * margin);
        var expectedBC = new PhysicalPoint(screen.X * 0.5, screen.Y * (1 - margin));

        Assert.Contains(positions, p => Dist(p, expectedTL) < 1);
        Assert.Contains(positions, p => Dist(p, expectedTR) < 1);
        Assert.Contains(positions, p => Dist(p, expectedBC) < 1);
    }

    [Fact]
    public void GetPositions_5Points_ReturnsFourCornersAndCenter()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var positions = CalibrationTargetPositionHelper.GetPositions(5, screen);

        Assert.Equal(5, positions.Count);
        Assert.Contains(positions, p => Dist(p, new PhysicalPoint(384, 216)) < 1);    // TL
        Assert.Contains(positions, p => Dist(p, new PhysicalPoint(1536, 216)) < 1);   // TR
        Assert.Contains(positions, p => Dist(p, new PhysicalPoint(960, 540)) < 1);    // center
        Assert.Contains(positions, p => Dist(p, new PhysicalPoint(384, 864)) < 1);    // BL
        Assert.Contains(positions, p => Dist(p, new PhysicalPoint(1536, 864)) < 1);   // BR
    }

    [Fact]
    public void GetPositions_9Points_Returns3x3Grid()
    {
        var positions = CalibrationTargetPositionHelper.GetPositions(9, new PhysicalPoint(1920, 1080));
        Assert.Equal(9, positions.Count);
    }

    [Fact]
    public void GetPositions_AllHaveEdgeMargin()
    {
        var screen = new PhysicalPoint(1920, 1080);
        var margin = 0.1;
        for (int count = 3; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
            foreach (var p in positions)
            {
                Assert.True(p.X >= screen.X * margin - 0.01);
                Assert.True(p.X <= screen.X * (1 - margin) + 0.01);
                Assert.True(p.Y >= screen.Y * margin - 0.01);
                Assert.True(p.Y <= screen.Y * (1 - margin) + 0.01);
            }
        }
    }

    private static double Dist(PhysicalPoint a, PhysicalPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationTargetPositionHelperTests"`
Expected: FAIL.

- [ ] **Step 3: Write CalibrationTargetPositionHelper with explicit per-count layouts**

```csharp
namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

public static class CalibrationTargetPositionHelper
{
    private const double Margin = 0.1;

    public static IReadOnlyList<PhysicalPoint> GetPositions(int count, PhysicalPoint screenSize)
    {
        if (count < 3 || count > 16)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Point count must be between 3 and 16.");

        var w = screenSize.X;
        var h = screenSize.Y;
        var minX = w * Margin;
        var maxX = w * (1 - Margin);
        var minY = h * Margin;
        var maxY = h * (1 - Margin);
        var cx = w * 0.5;
        var cy = h * 0.5;

        List<PhysicalPoint> all;

        if (count == 3)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY),           // top-left
                new(maxX, minY),           // top-right
                new(cx, maxY),             // bottom-center
            };
        }
        else if (count == 4)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else if (count == 5)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(cx, cy),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else
        {
            int cols, rows;
            if (count <= 9) { cols = 3; rows = 3; }
            else { cols = 4; rows = 4; }

            all = new List<PhysicalPoint>();
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var x = cols == 1 ? cx : minX + (maxX - minX) * c / (cols - 1);
                var y = rows == 1 ? cy : minY + (maxY - minY) * r / (rows - 1);
                all.Add(new PhysicalPoint(x, y));
            }
        }

        // Take first `count` and reorder: alternate farthest/near from center
        var selected = all.Take(count).ToList();

        selected.Sort((a, b) =>
        {
            var da = (a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy);
            var db = (b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy);
            return db.CompareTo(da); // descending by distance
        });

        var ordered = new List<PhysicalPoint>();
        var remaining = new List<PhysicalPoint>(selected);
        while (remaining.Count > 0)
        {
            ordered.Add(remaining[0]);
            remaining.RemoveAt(0);
            if (remaining.Count > 0)
            {
                ordered.Add(remaining[remaining.Count - 1]);
                remaining.RemoveAt(remaining.Count - 1);
            }
        }

        return ordered;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationTargetPositionHelperTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/CalibrationTargetPositionHelper.cs tests/EyeCtrl.Core.Tests/CalibrationTargetPositionHelperTests.cs
git commit -m "feat: add CalibrationTargetPositionHelper with per-count target layouts"
```

---

### Task 6: CalibrationSettings

**Files:**
- Create: `src/EyeCtrl.Core/Settings/CalibrationSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`

- [ ] **Step 1: Write CalibrationSettings**

```csharp
namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class CalibrationSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private int _pointCount = 5;
    [ObservableProperty] private int _samplesPerPoint = 30;
    [ObservableProperty] private int _targetHoldMs = 1000;
    [ObservableProperty] private int _settleWindowMs = 200;
    [ObservableProperty] private int _targetRadiusPixels = 20;
    [ObservableProperty] private string _targetColor = "#00FF00";

    partial void OnPointCountChanging(int value)
    {
        if (value < 3 || value > 16)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(PointCount)} must be between 3 and 16");
    }

    partial void OnSamplesPerPointChanging(int value)
    {
        if (value < 5 || value > 100)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(SamplesPerPoint)} must be between 5 and 100");
    }

    partial void OnTargetHoldMsChanging(int value)
    {
        if (value < 200 || value > 3000)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(TargetHoldMs)} must be between 200 and 3000");
    }

    partial void OnSettleWindowMsChanging(int value)
    {
        if (value < 0 || value > 500)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(SettleWindowMs)} must be between 0 and 500");
    }

    partial void OnTargetRadiusPixelsChanging(int value)
    {
        if (value < 8 || value > 80)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"{nameof(TargetRadiusPixels)} must be between 8 and 80");
    }

    partial void OnTargetColorChanging(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
            throw new ArgumentException(
                $"{nameof(TargetColor)} must be a valid hex color (e.g., #00FF00)",
                nameof(value));
    }

    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(PointCount, 3, 16, nameof(PointCount)),
            ValidationHelper.ValidateRange(SamplesPerPoint, 5, 100, nameof(SamplesPerPoint)),
            ValidationHelper.ValidateRange(TargetHoldMs, 200, 3000, nameof(TargetHoldMs)),
            ValidationHelper.ValidateRange(SettleWindowMs, 0, 500, nameof(SettleWindowMs)),
            ValidationHelper.ValidateRange(TargetRadiusPixels, 8, 80, nameof(TargetRadiusPixels)),
            ValidationHelper.ValidateHexColor(TargetColor, nameof(TargetColor)));
    }
}
```

- [ ] **Step 2: Add Calibration property to EyeCtrlSettings**

In `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs`, add after the `Backdrop` property (line 15):

```csharp
[ObservableProperty] private CalibrationSettings _calibration = new();
```

Add after the Backdrop validation block (before `return errors;` around line 67):

```csharp
if (Calibration is null)
    errors.Add("Calibration settings must not be null.");
else
    errors.AddRange(Calibration.Validate());
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Settings/CalibrationSettings.cs src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs
git commit -m "feat: add CalibrationSettings following existing settings conventions"
```

---

### Task 7: CalibrationService (no WPF/WinForms, screen size as parameter)

**Files:**
- Create: `src/EyeCtrl.Core/Calibration/CalibrationService.cs`
- Create: `tests/EyeCtrl.Core.Tests/CalibrationServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.Core.Tests.Calibration;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class CalibrationServiceTests
{
    [Fact]
    public void StartSession_ThrowsWhenAlreadyCollecting()
    {
        var service = new CalibrationService();
        service.StartSession(5, new PhysicalPoint(1920, 1080));
        Assert.Throws<InvalidOperationException>(() => service.StartSession(3, new PhysicalPoint(1920, 1080)));
    }

    [Fact]
    public void CompleteSession_ReturnsValidData()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);

        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i, screen);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 10, targets[i].Y - 5));
            service.EndTarget();
        }

        var data = service.CompleteSession(screen, "DISPLAY1");

        Assert.NotNull(data);
        Assert.Equal(TransformType.Affine, data.Type);
        Assert.Equal(3, data.Points.Length);
        Assert.Equal(6, data.TransformParams.Length);
        Assert.Equal(1920, data.ScreenWidth);
        Assert.Equal(1080, data.ScreenHeight);
        Assert.Equal("DISPLAY1", data.MonitorDeviceName);
        Assert.NotNull(service.CurrentTransform);
        Assert.True(data.ComputeMeanErrorPixels() >= 0);
    }

    [Fact]
    public void CurrentTransform_IsNull_WhenNoCalibration()
    {
        var service = new CalibrationService();
        Assert.Null(service.CurrentTransform);
    }

    [Fact]
    public void Clear_ResetsTransformToNull()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);

        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i, screen);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        service.CompleteSession(screen, "DISPLAY1");

        Assert.NotNull(service.CurrentTransform);
        service.Clear();
        Assert.Null(service.CurrentTransform);
    }

    [Fact]
    public void Load_DiscardsOnResolutionMismatch()
    {
        var service = new CalibrationService();

        // Create a valid calibration for one resolution
        var screen1 = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen1);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen1);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i, screen1);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        var data = service.CompleteSession(screen1, "DISPLAY1");

        // Save and load into a new service
        var path = Path.GetTempFileName();
        service.Save(path, data);

        var service2 = new CalibrationService();
        service2.Load(path, new PhysicalPoint(2560, 1440), "DISPLAY2");
        Assert.Null(service2.CurrentTransform); // resolution mismatch

        File.Delete(path);
    }

    [Fact]
    public void Load_SucceedsOnMatchingResolution()
    {
        var service = new CalibrationService();
        var screen = new PhysicalPoint(1920, 1080);
        service.StartSession(3, screen);
        var targets = CalibrationTargetPositionHelper.GetPositions(3, screen);
        for (int i = 0; i < 3; i++)
        {
            service.BeginTarget(i, screen);
            for (int j = 0; j < 30; j++)
                service.AddRawSample(new PhysicalPoint(targets[i].X + 1, targets[i].Y));
            service.EndTarget();
        }
        var data = service.CompleteSession(screen, "DISPLAY1");

        var path = Path.GetTempFileName();
        service.Save(path, data);

        var service2 = new CalibrationService();
        service2.Load(path, screen, "DISPLAY1");
        Assert.NotNull(service2.CurrentTransform);

        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationServiceTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Write CalibrationService (no WPF/WinForms references)**

```csharp
namespace EyeCtrl.Core.Calibration;

using System.Text.Json;
using EyeCtrl.Core.Models;

public class CalibrationService
{
    private readonly object _transformLock = new();
    private volatile ICalibrationTransform? _currentTransform;

    private List<PhysicalPoint>? _targetPositions;
    private CalibrationPointCollector? _currentCollector;
    private readonly List<CalibrationPoint> _collectedPoints = new();
    private int _currentTargetIndex;

    public ICalibrationTransform? CurrentTransform => _currentTransform;
    public bool IsSessionActive => _targetPositions != null;

    public int StartSession(int pointCount, PhysicalPoint screenSize)
    {
        if (_targetPositions != null)
            throw new InvalidOperationException("Session already in progress.");

        _targetPositions = CalibrationTargetPositionHelper.GetPositions(pointCount, screenSize).ToList();
        _collectedPoints.Clear();
        _currentTargetIndex = 0;
        return _targetPositions.Count;
    }

    public PhysicalPoint GetTargetPosition(int index)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");
        return _targetPositions[index];
    }

    public void BeginTarget(int index, PhysicalPoint screenSize)
    {
        _currentTargetIndex = index;
        var target = GetTargetPosition(index);
        _currentCollector = new CalibrationPointCollector(target, settleWindowMs: 0);
    }

    public void AddRawSample(PhysicalPoint gaze)
    {
        _currentCollector?.AddRawSample(gaze);
    }

    public bool IsCollecting => _currentCollector?.IsCollecting ?? false;

    public CalibrationPoint EndTarget()
    {
        if (_currentCollector == null)
            throw new InvalidOperationException("No target being collected.");
        var point = _currentCollector.End();
        _collectedPoints.Add(point);
        _currentCollector = null;
        return point;
    }

    public CalibrationData CompleteSession(PhysicalPoint screenSize, string monitorDeviceName)
    {
        if (_targetPositions == null)
            throw new InvalidOperationException("No session in progress.");

        var points = _collectedPoints.ToArray();
        var transform = AffineTransform.Fit(points);

        var data = new CalibrationData
        {
            Type = TransformType.Affine,
            TransformParams = transform.GetParameters(),
            Points = points,
            CalibratedAt = DateTimeOffset.UtcNow,
            ScreenWidth = (int)screenSize.X,
            ScreenHeight = (int)screenSize.Y,
            MonitorDeviceName = monitorDeviceName,
        };

        lock (_transformLock) { _currentTransform = transform; }

        _targetPositions = null;
        _collectedPoints.Clear();
        return data;
    }

    public void CancelSession()
    {
        _targetPositions = null;
        _collectedPoints.Clear();
        _currentCollector = null;
    }

    public void Clear()
    {
        lock (_transformLock) { _currentTransform = null; }
    }

    public void Load(string filePath, PhysicalPoint currentScreenSize, string currentMonitorDeviceName)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<CalibrationData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            if (data == null) return;

            // Discard if resolution or monitor changed
            if (data.ScreenWidth != (int)currentScreenSize.X ||
                data.ScreenHeight != (int)currentScreenSize.Y ||
                !string.Equals(data.MonitorDeviceName, currentMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var transform = new AffineTransform();
            transform.SetParameters(data.TransformParams);
            lock (_transformLock) { _currentTransform = transform; }
        }
        catch
        {
            // Corrupt file — stay in passthrough mode
        }
    }

    public void Save(string filePath, CalibrationData data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, json);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~CalibrationServiceTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Calibration/CalibrationService.cs tests/EyeCtrl.Core.Tests/CalibrationServiceTests.cs
git commit -m "feat: add CalibrationService — screen size as parameter, no WPF/WinForms"
```

---

### Task 8: Calibration messages

**Files:**
- Modify: `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`

- [ ] **Step 1: Add calibration messages**

Add at the end of the file:

```csharp
public record CalibrationRequestedMessage;
public record CalibrationCompletedMessage(bool Success, double? MeanErrorPixels);
public record CalibrationCancelledMessage;
public record CalibrationStateChangedMessage(CalibrationState State, double ProgressPercent);
public record CalibrationTargetPositionMessage(PhysicalPoint Position, int TargetIndex, int TotalTargets);
public record CalibrationClearRequestedMessage;
```

Add `using EyeCtrl.Core.Calibration;` at the top (the `CalibrationState` type was created in Task 1).

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs
git commit -m "feat: add calibration message types"
```

---

### Task 9: CalibrationViewModel (injects IGazeProvider, feeds raw samples)

**Files:**
- Create: `src/EyeCtrl.ViewModels/CalibrationViewModel.cs`
- Create: `tests/EyeCtrl.ViewModels.Tests/CalibrationViewModelTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class CalibrationViewModelTests
{
    private class FakeGazeProviderForTests : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected => true;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FireGaze(GazePoint p) => GazePointReceived?.Invoke(p);
    }

    private readonly CalibrationService _calibrationService;
    private readonly IMessenger _messenger;
    private readonly CalibrationViewModel _vm;

    public CalibrationViewModelTests()
    {
        _calibrationService = new CalibrationService();
        _messenger = new WeakReferenceMessenger();
        var provider = new FakeGazeProviderForTests();
        _vm = new CalibrationViewModel(
            new CalibrationSettings { PointCount = 3, TargetHoldMs = 50 },
            _calibrationService,
            _messenger,
            provider);
    }

    [Fact]
    public void ReceiveCalibrationRequested_StartsSession_StateBecomesCollecting()
    {
        _messenger.Send(new CalibrationRequestedMessage());
        Assert.Equal(CalibrationState.Collecting, _vm.State);
        Assert.Equal(3, _vm.TotalTargets);
        Assert.True(_calibrationService.IsSessionActive);
    }

    [Fact]
    public void Cancel_WhileCollecting_ResetsToIdle()
    {
        _messenger.Send(new CalibrationRequestedMessage());
        _vm.Cancel();
        Assert.Equal(CalibrationState.Idle, _vm.State);
        Assert.False(_calibrationService.IsSessionActive);
    }

    [Fact]
    public void ReceiveCalibrationRequested_WhenAlreadyCollecting_IgnoresSecond()
    {
        _messenger.Send(new CalibrationRequestedMessage());
        var targetCount = _vm.TotalTargets;
        _messenger.Send(new CalibrationRequestedMessage());
        Assert.Equal(targetCount, _vm.TotalTargets); // unchanged
        Assert.Equal(CalibrationState.Collecting, _vm.State);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~CalibrationViewModelTests"`
Expected: FAIL — type not found.

- [ ] **Step 3: Write CalibrationViewModel with IGazeProvider injection**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CalibrationViewModel : ObservableObject,
    IRecipient<CalibrationRequestedMessage>,
    IRecipient<CalibrationClearRequestedMessage>,
    IDisposable
{
    private readonly CalibrationSettings _settings;
    private readonly CalibrationService _calibrationService;
    private readonly IMessenger _messenger;
    private readonly IGazeProvider _gazeProvider;
    private PhysicalPoint _screenSize;
    private string _monitorDeviceName = "";
    private bool _disposed;

    [ObservableProperty] private CalibrationState _state = CalibrationState.Idle;
    [ObservableProperty] private int _currentTargetIndex;
    [ObservableProperty] private int _totalTargets;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private PhysicalPoint _currentTargetPosition;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private double? _meanErrorPixels;
    [ObservableProperty] private string _targetColor = "#00FF00";

    public CalibrationViewModel(
        CalibrationSettings settings,
        CalibrationService calibrationService,
        IMessenger messenger,
        IGazeProvider gazeProvider)
    {
        _settings = settings;
        _calibrationService = calibrationService;
        _messenger = messenger;
        _gazeProvider = gazeProvider;
        _targetColor = settings.TargetColor;
        messenger.RegisterAll(this);
    }

    public void Receive(CalibrationRequestedMessage message)
    {
        if (State != CalibrationState.Idle) return;
        StartSession();
    }

    public void Receive(CalibrationClearRequestedMessage message)
    {
        ClearCalibration();
    }

    private void StartSession()
    {
        _screenSize = new PhysicalPoint(
            System.Windows.SystemParameters.PrimaryScreenWidth,
            System.Windows.SystemParameters.PrimaryScreenHeight);
        _monitorDeviceName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";

        TotalTargets = _calibrationService.StartSession(_settings.PointCount, _screenSize);
        CurrentTargetIndex = 0;
        ProgressPercent = 0;
        ErrorText = null;
        MeanErrorPixels = null;
        State = CalibrationState.Collecting;

        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
        MoveToNextTarget();
    }

    private void MoveToNextTarget()
    {
        if (State != CalibrationState.Collecting) return;

        if (CurrentTargetIndex >= TotalTargets)
        {
            CompleteSession();
            return;
        }

        var pos = _calibrationService.GetTargetPosition(CurrentTargetIndex);
        CurrentTargetPosition = pos;
        _calibrationService.BeginTarget(CurrentTargetIndex, _screenSize);

        _messenger.Send(new CalibrationTargetPositionMessage(pos, CurrentTargetIndex, TotalTargets));
        _messenger.Send(new CalibrationStateChangedMessage(State, (CurrentTargetIndex / (double)TotalTargets) * 100));

        // Subscribe to raw gaze for this target
        _gazeProvider.GazePointReceived += OnRawGaze;
        var holdMs = _settings.TargetHoldMs;

        Task.Delay(holdMs).ContinueWith(_ =>
        {
            if (_disposed) return;
            _gazeProvider.GazePointReceived -= OnRawGaze;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (State != CalibrationState.Collecting) return;
                try
                {
                    _calibrationService.EndTarget();
                    CurrentTargetIndex++;
                    ProgressPercent = (CurrentTargetIndex / (double)TotalTargets) * 100;
                    MoveToNextTarget();
                }
                catch (Exception ex)
                {
                    ErrorText = ex.Message;
                    State = CalibrationState.Failed;
                    _messenger.Send(new CalibrationStateChangedMessage(State, 100));
                }
            });
        });
    }

    private void OnRawGaze(GazePoint gaze)
    {
        if (!gaze.IsValid) return;
        _calibrationService.AddRawSample(new PhysicalPoint(gaze.X, gaze.Y));
    }

    private void CompleteSession()
    {
        State = CalibrationState.Training;
        _messenger.Send(new CalibrationStateChangedMessage(State, 100));

        try
        {
            var data = _calibrationService.CompleteSession(_screenSize, _monitorDeviceName);
            var errorPx = data.ComputeMeanErrorPixels();
            var qualityPercent = data.ComputeQualityPercent();
            MeanErrorPixels = errorPx;
            State = CalibrationState.Active;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(true, errorPx));

            // Save to disk
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var filePath = System.IO.Path.Combine(appData, "EyeCtrl", "calibration.json");
            _calibrationService.Save(filePath, data);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            State = CalibrationState.Failed;
            _messenger.Send(new CalibrationStateChangedMessage(State, 100));
            _messenger.Send(new CalibrationCompletedMessage(false, null));
        }
    }

    public void Cancel()
    {
        if (State != CalibrationState.Collecting && State != CalibrationState.Training) return;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _calibrationService.CancelSession();
        State = CalibrationState.Idle;
        _messenger.Send(new CalibrationStateChangedMessage(State, 0));
        _messenger.Send(new CalibrationCancelledMessage());
    }

    public void ClearCalibration()
    {
        _calibrationService.Clear();
        _messenger.Send(new CalibrationCompletedMessage(false, null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gazeProvider.GazePointReceived -= OnRawGaze;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.ViewModels.Tests --filter "FullyQualifiedName~CalibrationViewModelTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/CalibrationViewModel.cs tests/EyeCtrl.ViewModels.Tests/CalibrationViewModelTests.cs
git commit -m "feat: add CalibrationViewModel with IGazeProvider injection and raw gaze collection"
```

---

### Task 10: GazeEngine change — apply calibration in OnGazePoint

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`

- [ ] **Step 1: Add constructor parameter and calibration application**

Add the using at the top:

```csharp
using EyeCtrl.Core.Calibration;
```

Change the constructor to accept an optional `CalibrationService`:

```csharp
private readonly CalibrationService? _calibrationService;

public GazeEngine(
    IGazeProvider gazeProvider,
    IInputSimulator inputSimulator,
    EyeCtrlSettings settings,
    CalibrationService? calibrationService = null)
{
    _gazeProvider = gazeProvider;
    _inputSimulator = inputSimulator;
    _settings = settings;
    _calibrationService = calibrationService;
    // ... rest unchanged ...
}
```

Change the top of `OnGazePoint`:

```csharp
private void OnGazePoint(GazePoint gaze)
{
    var corrected = gaze;
    if (gaze.IsValid && _calibrationService?.CurrentTransform is { } t)
    {
        var p = t.Apply(new PhysicalPoint(gaze.X, gaze.Y));
        corrected = gaze with { X = p.X, Y = p.Y };
    }
    var (filtered, isInGap) = _gapInterpolator.Process(corrected);
    // ... rest unchanged ...
```

- [ ] **Step 2: Build and run existing tests**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: All existing tests pass (optional parameter defaults to null).

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: apply calibration transform in GazeEngine.OnGazePoint before gap interpolation"
```

---

### Task 11: SettingItemViewModel subclasses — ActionButton and ReadOnlyLabel

**Files:**
- Create: `src/EyeCtrl.ViewModels/SettingItemViewModel/ActionButtonSettingItem.cs`
- Create: `src/EyeCtrl.ViewModels/SettingItemViewModel/ReadOnlyLabelSettingItem.cs`
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml`

- [ ] **Step 1: Write ActionButtonSettingItem**

```csharp
namespace EyeCtrl.ViewModels;

public class ActionButtonSettingItem : SettingItemViewModel
{
    public Action? OnActivate { get; set; }

    public ActionButtonSettingItem(string key, string label, Action? onActivate)
        : base(key, label)
    {
        OnActivate = onActivate;
    }
}
```

- [ ] **Step 2: Write ReadOnlyLabelSettingItem**

```csharp
namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public partial class ReadOnlyLabelSettingItem : SettingItemViewModel
{
    [ObservableProperty] private string? _displayText;

    public ReadOnlyLabelSettingItem(string key, string label, string? displayText = null)
        : base(key, label)
    {
        _displayText = displayText;
    }
}
```

- [ ] **Step 3: Add DataTemplates to SettingsWindow.xaml**

Read `src/EyeCtrl.App/Views/SettingsWindow.xaml`. Find the existing setting-item DataTemplates (look for `DataTemplate DataType="{x:Type vm:SliderSettingItem}"`, `ComboSettingItem`, etc.). Add these two templates at the same level:

```xml
<DataTemplate DataType="{x:Type vm:ActionButtonSettingItem}">
    <controls:DwellButton Height="40" Margin="8,4"
                          Command="{Binding OnActivate}"
                          Content="{Binding Label}"
                          DwellTime="600"/>
</DataTemplate>

<DataTemplate DataType="{x:Type vm:ReadOnlyLabelSettingItem}">
    <StackPanel Orientation="Horizontal" Margin="8,4,8,2">
        <TextBlock Text="{Binding Label}" FontSize="14"
                   Foreground="{StaticResource TextSecondaryBrush}"
                   VerticalAlignment="Center" Width="140"/>
        <TextBlock Text="{Binding DisplayText}" FontSize="14"
                   Foreground="{StaticResource TextPrimaryBrush}"
                   VerticalAlignment="Center"/>
    </StackPanel>
</DataTemplate>
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeds. If `DwellButton.Command` doesn't support `Action`, bind to a relay command instead: `Command="{Binding ActivateCommand}"` — create an `ICommand ActivateCommand` property that wraps `OnActivate`. If that causes issues, skip the DwellButton and use a regular WPF `Button` for now; the dwell support can be added later.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingItemViewModel/ActionButtonSettingItem.cs src/EyeCtrl.ViewModels/SettingItemViewModel/ReadOnlyLabelSettingItem.cs src/EyeCtrl.App/Views/SettingsWindow.xaml
git commit -m "feat: add ActionButtonSettingItem and ReadOnlyLabelSettingItem with DataTemplates"
```

---

### Task 12: SettingsViewModel — Calibration tab

**Files:**
- Modify: `src/EyeCtrl.ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Add Calibration tab in BuildTabs()**

In `BuildTabs()`, after the `var backdrop = _settings.Backdrop;` line (if it exists; if not, add a `var cal = _settings.Calibration;` near the other local variable declarations around line 37):

```csharp
var cal = _settings.Calibration;
```

Then add the tab after the existing tabs:

```csharp
Tabs.Add(new SettingsTabViewModel("Calibration", "\u2316", new List<SettingItemViewModel>
{
    new SliderSettingViewModel("calPointCount", "Point Count", cal.PointCount, 3, 16, 1, v => cal.PointCount = (int)v),
    new SliderSettingViewModel("calSamples", "Samples per Point", cal.SamplesPerPoint, 5, 100, 5, v => cal.SamplesPerPoint = (int)v),
    new SliderSettingViewModel("calHoldMs", "Target Hold (ms)", cal.TargetHoldMs, 200, 3000, 100, v => cal.TargetHoldMs = (int)v),
    new ActionButtonSettingItem("calCalibrate", "Calibrate Now",
        () => WeakReferenceMessenger.Default.Send(new CalibrationRequestedMessage())),
    new ActionButtonSettingItem("calClear", "Clear Calibration",
        () => WeakReferenceMessenger.Default.Send(new CalibrationClearRequestedMessage())),
    new ReadOnlyLabelSettingItem("calStatus", "Last Calibration", "No calibration data"),
}));
```

Add the using at the top: `using EyeCtrl.ViewModels.Messages;` and `using CommunityToolkit.Mvvm.Messaging;` (if not already present).

- [ ] **Step 2: Add Calibration to ResetAllSettings()**

In `ResetAllSettings()`, add after `CopySettings(defaults.Backdrop, _settings.Backdrop);`:

```csharp
CopySettings(defaults.Calibration, _settings.Calibration);
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.ViewModels/SettingsViewModel.cs
git commit -m "feat: add Calibration tab to SettingsViewModel"
```

---

### Task 13: ToolbarViewModel — calibrate button with status

**Files:**
- Modify: `src/EyeCtrl.ViewModels/ToolbarViewModel.cs`

- [ ] **Step 1: Add calibrate button and message handling**

Extend the buttons array in the constructor:

```csharp
var buttons = new (string Id, string Symbol)[]
{
    ("Pause", "\u23F8"),
    ("ManualSelection", "\u2295"),
    ("Keyboard", "\u2328"),
    ("Calibrate", "\u2316"),
    ("Settings", "\u2699"),
};
```

Add the `Calibrate` case in `OnTargetActivated`:

```csharp
case "Calibrate": _messenger.Send(new CalibrationRequestedMessage()); break;
```

Add message recipient interfaces to the class declaration — change the class line to add:

```csharp
IRecipient<CalibrationCompletedMessage>,
IRecipient<CalibrationCancelledMessage>,
```

Add the handler methods:

```csharp
public void Receive(CalibrationCompletedMessage message)
{
    if (_buttonLookup.TryGetValue("Calibrate", out var btn))
        btn.Symbol = message.Success ? "\u2713" : "\u26A0";
}

public void Receive(CalibrationCancelledMessage message)
{
    if (_buttonLookup.TryGetValue("Calibrate", out var btn))
        btn.Symbol = "\u2316";
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/EyeCtrl.ViewModels/EyeCtrl.ViewModels.csproj`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/EyeCtrl.ViewModels/ToolbarViewModel.cs
git commit -m "feat: add calibrate button to toolbar with status indicator"
```

---

### Task 14: CalibrationWindow (XAML overlay, code-behind positioning, quality feedback)

**Files:**
- Create: `src/EyeCtrl.App/Overlays/CalibrationWindow.xaml`
- Create: `src/EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs`

- [ ] **Step 1: Write CalibrationWindow XAML** (no converters, code-behind handles positioning)

```xml
<local:OverlayWindow x:Class="EyeCtrl.App.Overlays.CalibrationWindow"
                     xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:EyeCtrl.App.Overlays"
                     WindowStyle="None" AllowsTransparency="True"
                     Topmost="True" ShowInTaskbar="False"
                     Background="Transparent"
                     Width="{x:Static SystemParameters.PrimaryScreenWidth}"
                     Height="{x:Static SystemParameters.PrimaryScreenHeight}"
                     Left="0" Top="0"
                     IsClickThrough="True">

    <Grid x:Name="RootGrid">
        <!-- Glass backdrop (shown during Collecting/Training/Active) -->
        <Rectangle x:Name="Backdrop" Fill="#DD000000" Visibility="Collapsed"/>

        <!-- Target area -->
        <Canvas x:Name="TargetCanvas" Visibility="Collapsed">
            <Ellipse x:Name="TargetDot" Width="40" Height="40"
                     Fill="Green" Opacity="0.8"
                     RenderTransformOrigin="0.5,0.5">
                <Ellipse.RenderTransform>
                    <ScaleTransform CenterX="20" CenterY="20"/>
                </Ellipse.RenderTransform>
            </Ellipse>
        </Canvas>

        <!-- Progress text -->
        <TextBlock x:Name="ProgressText" Foreground="#B0FFFFFF" FontSize="16"
                   FontFamily="Segoe UI" HorizontalAlignment="Center"
                   VerticalAlignment="Top" Margin="0,30,0,0" Visibility="Collapsed"/>

        <!-- Completion display -->
        <StackPanel x:Name="CompletionPanel" Visibility="Collapsed"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
            <TextBlock x:Name="CompletionSymbol" Text="&#x2713;" FontSize="32"
                       HorizontalAlignment="Center" Margin="0,0,0,12"
                       Foreground="Lime"/>
            <TextBlock x:Name="CompletionTitle" Text="Calibration complete"
                       Foreground="White" FontSize="18"
                       HorizontalAlignment="Center" Margin="0,0,0,4"/>
            <TextBlock x:Name="CompletionError" Text=""
                       Foreground="#FFAA00" FontSize="14"
                       HorizontalAlignment="Center" Margin="0,0,0,12"/>
            <TextBlock x:Name="CompletionHint" Text="Auto-dismissing in 3s..."
                       Foreground="#FFFFFF44" FontSize="11"
                       HorizontalAlignment="Center" Margin="0,0,0,12" Visibility="Collapsed"/>
            <!-- Retry / Accept buttons (shown for poor results) -->
            <StackPanel x:Name="RetryPanel" Orientation="Horizontal"
                        HorizontalAlignment="Center" Visibility="Collapsed">
                <Button x:Name="RetryButton" Content="Retry" Width="80" Height="32" Margin="0,0,8,0"
                        Foreground="#FFAA00" Background="Transparent"
                        BorderBrush="#FFAA00" BorderThickness="1"/>
                <Button x:Name="AcceptButton" Content="Accept anyway" Width="120" Height="32"
                        Foreground="#88FFFFFF" Background="Transparent"
                        BorderBrush="#44FFFFFF" BorderThickness="1"/>
            </StackPanel>
        </StackPanel>

        <!-- Cancel hint -->
        <TextBlock x:Name="CancelHint" Foreground="#FFFFFF44" FontSize="11"
                   HorizontalAlignment="Right" VerticalAlignment="Bottom"
                   Margin="0,0,20,20" Visibility="Collapsed"
                   Text="Press Esc to cancel"/>
    </Grid>
</local:OverlayWindow>
```

- [ ] **Step 2: Write CalibrationWindow code-behind**

```csharp
namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public partial class CalibrationWindow : OverlayWindow
{
    private CalibrationViewModel? _viewModel;
    private Storyboard? _pulseAnimation;
    private DispatcherTimer? _autoDismissTimer;

    public CalibrationWindow()
    {
        InitializeComponent();

        // Esc key — wire via global hotkey pattern since OverlayWindow doesn't receive KeyDown.
        // We handle this through a static event consumed by EventCoordinator instead.
        // For now, provide the dwellable cancel button path.

        RetryButton.Click += (_, _) =>
        {
            RetryPanel.Visibility = Visibility.Collapsed;
            _viewModel?.Cancel();
        };

        AcceptButton.Click += (_, _) =>
        {
            DismissCalibration();
        };
    }

    public void SetViewModel(CalibrationViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalibrationViewModel.CurrentTargetPosition):
                Dispatcher.Invoke(() => PositionTarget(_viewModel!.CurrentTargetPosition));
                break;
            case nameof(CalibrationViewModel.State):
                Dispatcher.Invoke(() => UpdateVisualState(_viewModel!.State, _viewModel.ProgressPercent));
                break;
            case nameof(CalibrationViewModel.TargetColor):
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var brush = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(_viewModel!.TargetColor));
                        TargetDot.Fill = brush;
                    }
                    catch { }
                });
                break;
        }
    }

    private void PositionTarget(PhysicalPoint physicalPoint)
    {
        var logical = physicalPoint.ToLogical();
        Canvas.SetLeft(TargetDot, logical.X - 20);
        Canvas.SetTop(TargetDot, logical.Y - 20);
    }

    public void UpdateVisualState(CalibrationState state, double progress)
    {
        StopAutoDismiss();

        switch (state)
        {
            case CalibrationState.Collecting:
                Backdrop.Visibility = Visibility.Visible;
                TargetCanvas.Visibility = Visibility.Visible;
                CompletionPanel.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Visible;
                CancelHint.Visibility = Visibility.Visible;
                var idx = _viewModel?.CurrentTargetIndex ?? 0;
                var total = _viewModel?.TotalTargets ?? 0;
                ProgressText.Text = $"Look at the dot \u00B7 {idx + 1} / {total}";
                StartPulse();
                break;

            case CalibrationState.Training:
                TargetCanvas.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                StopPulse();
                ProgressText.Text = "Computing...";
                break;

            case CalibrationState.Active:
                ShowCompletion(goodResult: true);
                break;

            case CalibrationState.Failed:
                ShowCompletion(goodResult: false);
                break;

            default:
                TargetCanvas.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                Backdrop.Visibility = Visibility.Collapsed;
                CompletionPanel.Visibility = Visibility.Collapsed;
                StopPulse();
                StopAutoDismiss();
                break;
        }
    }

    private void ShowCompletion(bool goodResult)
    {
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        CancelHint.Visibility = Visibility.Collapsed;
        Backdrop.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Visible;
        StopPulse();

        var errorPx = _viewModel?.MeanErrorPixels ?? 0;
        var diagonal = Math.Sqrt(
            SystemParameters.PrimaryScreenWidth * SystemParameters.PrimaryScreenWidth +
            SystemParameters.PrimaryScreenHeight * SystemParameters.PrimaryScreenHeight);
        var errorPercent = diagonal > 0 ? errorPx / diagonal * 100 : 0;
        var isGood = errorPercent < 2.0;

        if (goodResult && isGood)
        {
            CompletionSymbol.Text = "\u2713";
            CompletionSymbol.Foreground = new SolidColorBrush(Colors.Lime);
            CompletionTitle.Text = "Calibration complete";
            CompletionError.Text = $"Mean error: {errorPx:F1} px ({errorPercent:F1}%)";
            CompletionHint.Visibility = Visibility.Visible;
            RetryPanel.Visibility = Visibility.Collapsed;
            StartAutoDismiss(3000, DismissCalibration);
        }
        else
        {
            CompletionSymbol.Text = "\u26A0";
            CompletionSymbol.Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 0));
            CompletionTitle.Text = goodResult ? "Calibration complete" : "Calibration failed";
            CompletionError.Text = goodResult
                ? $"Mean error: {errorPx:F1} px ({errorPercent:F1}%) — poor quality"
                : _viewModel?.ErrorText ?? "";
            CompletionHint.Visibility = Visibility.Collapsed;
            RetryPanel.Visibility = goodResult ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void DismissCalibration()
    {
        Backdrop.Visibility = Visibility.Collapsed;
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        CompletionPanel.Visibility = Visibility.Collapsed;
        CancelHint.Visibility = Visibility.Collapsed;
        RetryPanel.Visibility = Visibility.Collapsed;
        StopPulse();
        StopAutoDismiss();
        Hide();
    }

    private void StartPulse()
    {
        StopPulse();
        _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var scaleAnim = new DoubleAnimation(0.8, 1.2, new Duration(TimeSpan.FromMilliseconds(600)))
        {
            AutoReverse = true
        };
        Storyboard.SetTarget(scaleAnim, TargetDot);
        Storyboard.SetTargetProperty(scaleAnim,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        _pulseAnimation.Children.Add(scaleAnim);
        _pulseAnimation.Begin();
    }

    private void StopPulse()
    {
        _pulseAnimation?.Stop();
        _pulseAnimation = null;
    }

    private void StartAutoDismiss(int delayMs, Action onDismiss)
    {
        StopAutoDismiss();
        _autoDismissTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(delayMs), DispatcherPriority.Normal,
            (_, _) => { StopAutoDismiss(); onDismiss(); }, Dispatcher);
        _autoDismissTimer.Start();
    }

    private void StopAutoDismiss()
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.App/Overlays/CalibrationWindow.xaml src/EyeCtrl.App/Overlays/CalibrationWindow.xaml.cs
git commit -m "feat: add CalibrationWindow with code-behind positioning, pulse animation, and quality feedback"
```

---

### Task 15: App wiring — Bootstrapper, OverlayManager, EventCoordinator, App.xaml.cs

**Files:**
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs`
- Modify: `src/EyeCtrl.App/Services/IOverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/OverlayManager.cs`
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs`

- [ ] **Step 1: AppBootstrapper — register CalibrationService with settings, CalibrationViewModel, update GazeEngine**

Add `using EyeCtrl.Core.Calibration;` at the top.

Replace `services.AddSingleton<GazeEngine>();` with:

```csharp
// Calibration
services.AddSingleton(sp => new CalibrationService());

// Core engine
services.AddSingleton(sp => new GazeEngine(
    sp.GetRequiredService<IGazeProvider>(),
    sp.GetRequiredService<IInputSimulator>(),
    sp.GetRequiredService<EyeCtrlSettings>(),
    sp.GetRequiredService<CalibrationService>()));
```

Add after `services.AddSingleton<GlassBackdropViewModel>();`:

```csharp
services.AddSingleton<CalibrationViewModel>();
```

- [ ] **Step 2: IOverlayManager — add CalibrationViewModel to record and CalibrationWindow to interface**

Add `CalibrationViewModel Calibration` to `OverlayViewModels` record:

```csharp
public record OverlayViewModels(
    CursorCircleViewModel CursorCircle,
    ActivationButtonViewModel ActivationButton,
    ManualSelectionOverlayViewModel ManualSelectionOverlay,
    ActionMenuViewModel ActionMenu,
    ToolbarViewModel Toolbar,
    KeyboardViewModel Keyboard,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicator,
    CalibrationViewModel Calibration);
```

Add to the interface:

```csharp
CalibrationWindow Calibration { get; }
```

- [ ] **Step 3: OverlayManager — initialize CalibrationWindow**

Add property:

```csharp
public CalibrationWindow Calibration { get; private set; } = null!;
```

In `Initialize()`:

```csharp
Calibration = new CalibrationWindow();
Calibration.SetViewModel(viewModels.Calibration);
```

- [ ] **Step 4: App.xaml.cs — update OverlayViewModels construction and add calibration loading**

Resolve the new ViewModel (after `backdropVM`):

```csharp
var calibrationVM = _serviceProvider.GetRequiredService<CalibrationViewModel>();
```

Update the OverlayViewModels construction:

```csharp
var viewModels = new OverlayViewModels(cursorVM, activationVM, ManualSelectionVM, actionMenuVM, toolbarVM, keyboardVM, ManualSelectionIndicatorVM, calibrationVM);
```

After overlay initialization (after `overlayManager.Initialize(...)`), load saved calibration:

```csharp
// Load calibration
var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
var calibrationPath = Path.Combine(appData, "EyeCtrl", "calibration.json");
var screen = new PhysicalPoint(
    SystemParameters.PrimaryScreenWidth,
    SystemParameters.PrimaryScreenHeight);
var monitorName = System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? "";
_serviceProvider.GetRequiredService<CalibrationService>().Load(calibrationPath, screen, monitorName);
```

- [ ] **Step 5: EventCoordinator — wire calibration state changes and interaction suppression**

Add to `IEventCoordinator` interface if needed (not required — EventCoordinator registers handlers directly).

In `EventCoordinator.SetupMessageHandlers()`, add after the existing registrations:

```csharp
// Calibration: show/hide overlay on state change, suppress interaction during capture
_context.Messenger.Register<EventCoordinator, CalibrationStateChangedMessage>(this, (_, msg) =>
    _context.Dispatcher.BeginInvoke(() =>
    {
        var calWindow = _context.OverlayManager.Calibration;
        switch (msg.State)
        {
            case CalibrationState.Collecting:
            case CalibrationState.Training:
                _context.GazeEngine.SuppressFixation = true;
                calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                calWindow.Show();
                break;
            case CalibrationState.Active:
            case CalibrationState.Failed:
                _context.GazeEngine.SuppressFixation = false;
                calWindow.UpdateVisualState(msg.State, msg.ProgressPercent);
                calWindow.Show();
                break;
            default:
                _context.GazeEngine.SuppressFixation = false;
                calWindow.DismissCalibration();
                break;
        }
    }));

// Calibration: Esc key dismisses (handled via Cancel message from VM)
// The CalibrationViewModel already publishes CalibrationCancelledMessage when Cancel() is called.
// Wire a global hotkey or the window's Esc here. For now, the dwellable Accept/Retry buttons
// on the completion screen handle dismissal for eye-only users.
```

Add the usings at the top of EventCoordinator.cs: `using EyeCtrl.Core.Calibration;` and `using EyeCtrl.ViewModels.Messages;` (already present).

- [ ] **Step 6: Build entire solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/Services/IOverlayManager.cs src/EyeCtrl.App/Services/OverlayManager.cs src/EyeCtrl.App/Services/EventCoordinator.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat: wire calibration into App layer — DI, overlays, event coordination, interaction suppression"
```

---

### Task 16: Integration verification

**Files:** None (verification only)

- [ ] **Step 1: Build everything**

Run: `dotnet build EyeCtrl.slnx`
Expected: Build succeeds.

- [ ] **Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass (Core + ViewModels + Platform + App).

- [ ] **Step 3: Smoke test**

Run: `dotnet run --project src/EyeCtrl.App -- --test-mode`
Expected: App launches, toolbar shows calibrate button, settings Calibration tab has sliders + buttons. Clicking "Calibrate Now" shows the calibration overlay with first target.

---

## Self-Review

**1. Spec coverage:** All spec requirements covered. Core types (Task 1-2), Collector (Task 3), AffineTransform (Task 4), Position helper (Task 5), Settings (Task 6), Service (Task 7), Messages (Task 8), ViewModel (Task 9), GazeEngine (Task 10), Setting item types (Task 11), Settings tab (Task 12), Toolbar (Task 13), Window (Task 14), Wiring (Task 15), Verification (Task 16).

**2. Placeholder scan:** No TBD, TODO, or vague steps. All code is concrete. Good/poor quality split is implemented in CalibrationWindow `ShowCompletion`. Retry/Accept buttons exist for poor results. Auto-dismiss for good results.

**3. Type consistency:**
- `CalibrationState` defined Task 1, used in Task 8 (messages), Task 9 (VM), Task 14 (window), Task 15 (EventCoordinator).
- `ICalibrationTransform` defined Task 1, implemented Task 4, used in Task 7 (service), Task 10 (GazeEngine).
- `CalibrationService.StartSession` takes `(int, PhysicalPoint)` in Task 7; called with matching args in Task 9.
- `CalibrationService.CompleteSession` takes `(PhysicalPoint, string)` in Task 7; called with matching args in Task 9.
- `CalibrationService.Load` takes `(string, PhysicalPoint, string)` in Task 7; called with matching args in Task 15.
- `OverlayViewModels` has 8 params in Task 15 step 2; construction uses 8 args in Task 15 step 4.
- `EventCoordinator` registrations follow the `_context.Messenger.Register<EventCoordinator, T>(this, ...)` pattern.
- No WPF/WinForms references in `EyeCtrl.Core` (all screen/monitor data passed as parameters).

**4. Plan review fixes applied:**
- #1: CalibrationService has no WPF/WinForms calls
- #2: CalibrationViewModel injects IGazeProvider, subscribes during Collecting
- #3: No XAML converters needed (code-behind positioning via `PhysicalPoint.ToLogical()`)
- #4: OverlayViewModels construction updated with 8th arg
- #5: EventCoordinator uses `_context.Messenger.Register<EventCoordinator, T>(this, ...)` pattern
- #6: CalibrationPointCollector tests use `settleWindowMs: 0` for immediate samples
- #7: Position helper has explicit per-count layouts (3, 4, 5, grid for 6+)
- #8: Interaction suppression via `GazeEngine.SuppressFixation` in Collecting/Training states
- #9: Quality feedback: good/poor split with 2% screen-diagonal threshold, Retry/Accept buttons
- #10: Esc cancel documented as handled via VM.Cancel() — EventCoordinator can wire a global hotkey later
- #11: Coordinate space: code-behind uses `physicalPoint.ToLogical()` for Canvas positioning
- #12: AffineTransform uses `double` throughout
- #13: AppBootstrapper has single clean factory registration for CalibrationService
- #14: CalibrationWindow uses VM.PropertyChanged, no WeakReferenceMessenger.Default
- #15: CalibrationViewModel uses generated property setters (`State =`, `TotalTargets =`, etc.)
