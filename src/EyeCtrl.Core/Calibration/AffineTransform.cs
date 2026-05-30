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

        // Guard against degenerate inputs (e.g. collinear targets or near-constant
        // gaze) producing non-finite parameters that would poison the gaze pipeline.
        foreach (var v in x)
        {
            if (!double.IsFinite(v))
                throw new InvalidOperationException(
                    "Calibration fit produced non-finite parameters (degenerate calibration points).");
        }

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

            // Singular (or near-singular) matrix — degenerate calibration geometry.
            if (Math.Abs(mat[col, col]) < 1e-9)
                throw new InvalidOperationException(
                    "Calibration points are degenerate (singular matrix); cannot fit transform.");

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
