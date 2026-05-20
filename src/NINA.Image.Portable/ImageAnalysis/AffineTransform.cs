namespace NINA.Image.ImageAnalysis;

public class AffineTransform {
    public double M00 { get; set; } = 1;
    public double M01 { get; set; }
    public double M10 { get; set; }
    public double M11 { get; set; } = 1;
    public double Tx { get; set; }
    public double Ty { get; set; }

    public static AffineTransform Identity => new();

    public (double x, double y) Apply(double x, double y) {
        return (M00 * x + M01 * y + Tx, M10 * x + M11 * y + Ty);
    }

    public static AffineTransform? FromPointPairs(
        (double x, double y)[] src, (double x, double y)[] dst) {
        int n = Math.Min(src.Length, dst.Length);
        if (n < 3) return null;

        // Least-squares solve for two independent systems:
        // dst_x = a*src_x + b*src_y + tx
        // dst_y = c*src_x + d*src_y + ty
        // Design matrix A = [src_x, src_y, 1] for each point

        double sumX = 0, sumY = 0;
        double sumXX = 0, sumYY = 0, sumXY = 0;
        double sumDxX = 0, sumDxY = 0, sumDx = 0;
        double sumDyX = 0, sumDyY = 0, sumDy = 0;

        for (int i = 0; i < n; i++) {
            double sx = src[i].x, sy = src[i].y;
            double dx = dst[i].x, dy = dst[i].y;

            sumX += sx; sumY += sy;
            sumXX += sx * sx; sumYY += sy * sy; sumXY += sx * sy;
            sumDxX += dx * sx; sumDxY += dx * sy; sumDx += dx;
            sumDyX += dy * sx; sumDyY += dy * sy; sumDy += dy;
        }

        // Normal equations: A^T A * params = A^T b
        // A^T A = [[sumXX, sumXY, sumX], [sumXY, sumYY, sumY], [sumX, sumY, n]]
        var ata = new double[3, 3] {
            { sumXX, sumXY, sumX },
            { sumXY, sumYY, sumY },
            { sumX,  sumY,  n    }
        };

        double[] rhsX = [sumDxX, sumDxY, sumDx];
        double[] rhsY = [sumDyX, sumDyY, sumDy];

        var paramsX = Solve3x3(ata, rhsX);
        var paramsY = Solve3x3(ata, rhsY);

        if (paramsX == null || paramsY == null) return null;

        return new AffineTransform {
            M00 = paramsX[0], M01 = paramsX[1], Tx = paramsX[2],
            M10 = paramsY[0], M11 = paramsY[1], Ty = paramsY[2]
        };
    }

    private static double[]? Solve3x3(double[,] A, double[] b) {
        // Gaussian elimination with partial pivoting
        var a = new double[3, 4];
        for (int i = 0; i < 3; i++) {
            for (int j = 0; j < 3; j++) a[i, j] = A[i, j];
            a[i, 3] = b[i];
        }

        for (int col = 0; col < 3; col++) {
            int maxRow = col;
            double maxVal = Math.Abs(a[col, col]);
            for (int row = col + 1; row < 3; row++) {
                if (Math.Abs(a[row, col]) > maxVal) {
                    maxVal = Math.Abs(a[row, col]);
                    maxRow = row;
                }
            }

            if (maxVal < 1e-12) return null;

            if (maxRow != col) {
                for (int j = 0; j < 4; j++) {
                    (a[col, j], a[maxRow, j]) = (a[maxRow, j], a[col, j]);
                }
            }

            for (int row = col + 1; row < 3; row++) {
                double factor = a[row, col] / a[col, col];
                for (int j = col; j < 4; j++) {
                    a[row, j] -= factor * a[col, j];
                }
            }
        }

        var x = new double[3];
        for (int i = 2; i >= 0; i--) {
            x[i] = a[i, 3];
            for (int j = i + 1; j < 3; j++) {
                x[i] -= a[i, j] * x[j];
            }
            x[i] /= a[i, i];
        }

        return x;
    }
}
