using System;

namespace VISOR.Telemetry
{
    /// <summary>The shift-point learner's linear algebra, kept apart from its physics.</summary>
    internal static class LinearSolver
    {
        /// <summary>
        /// Solves a x = b for a symmetric positive-definite n×n matrix (row-major), by Cholesky
        /// factorization. Overwrites <paramref name="a"/>. Returns null when the matrix isn't
        /// positive definite or the result isn't finite.
        /// </summary>
        public static double[]? CholeskySolve(double[] a, double[] b, int n)
        {
            // In-place lower-triangular factorization: a = L L^T.
            for (int j = 0; j < n; j++)
            {
                double sum = a[j * n + j];
                for (int k = 0; k < j; k++) sum -= a[j * n + k] * a[j * n + k];
                if (sum <= 0 || !double.IsFinite(sum)) return null;
                double ljj = Math.Sqrt(sum);
                a[j * n + j] = ljj;
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i * n + j];
                    for (int k = 0; k < j; k++) s -= a[i * n + k] * a[j * n + k];
                    a[i * n + j] = s / ljj;
                }
            }
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = b[i];
                for (int k = 0; k < i; k++) s -= a[i * n + k] * y[k];
                y[i] = s / a[i * n + i];
            }
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double s = y[i];
                for (int k = i + 1; k < n; k++) s -= a[k * n + i] * x[k];
                x[i] = s / a[i * n + i];
            }
            foreach (var v in x) if (!double.IsFinite(v)) return null;
            return x;
        }
    }
}
