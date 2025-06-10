// Based on the Kalman Filter implementation from the Accord.NET Extensions Framework
// by Darko Jurić, published under the LGPL.
// Adapted for use in this project.
// Original source: https://www.codeproject.com/Articles/865935/Object-Tracking-Kalman-Filter-with-Ease

using System.Drawing;

namespace Aimmy2.AILogic
{
    /// <summary>
    /// A simple Kalman filter for 2D point tracking.
    /// </summary>
    public class KalmanFilter
    {
        // State vector: [x, y, vx, vy]
        private readonly float[] _x = new float[4];

        // Covariance matrix
        private readonly float[,] _p = new float[4, 4];

        #region Constant Matrices
        // State transition matrix
        private readonly float[,] _f = {
            { 1, 0, 1, 0 },
            { 0, 1, 0, 1 },
            { 0, 0, 1, 0 },
            { 0, 0, 0, 1 }
        };

        // Measurement matrix
        private readonly float[,] _h = {
            { 1, 0, 0, 0 },
            { 0, 1, 0, 0 }
        };

        // Process noise covariance
        private readonly float[,] _q;

        // Measurement noise covariance
        private readonly float[,] _r;

        // Identity matrix
        private readonly float[,] _i = Identity(4);
        #endregion

        #region Pre-allocated Temporary Matrices
        // These are allocated once to avoid GC pressure in the main loop.
        private readonly float[] _predictedState = new float[4];
        private readonly float[,] _transposedF = new float[4, 4];
        private readonly float[,] _transposedH = new float[4, 2];
        private readonly float[,] _tempP = new float[4, 4];
        private readonly float[,] _pht = new float[4, 2];
        private readonly float[,] _s = new float[2, 2];
        private readonly float[,] _sInv = new float[2, 2];
        private readonly float[,] _k = new float[4, 2];
        private readonly float[] _innovation = new float[2];
        private readonly float[,] _kh = new float[4, 4];
        private readonly float[,] _ikh = new float[4, 4];
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="KalmanFilter"/> class.
        /// </summary>
        /// <param name="processNoise">The process noise.</param>
        /// <param name="measurementNoise">The measurement noise.</param>
        /// <param name="initialState">The initial state of the filter.</param>
        /// <param name="initialCovariance">The initial covariance of the filter.</param>
        public KalmanFilter(float processNoise = 1e-2f, float measurementNoise = 1e-1f, PointF? initialState = null, float initialCovariance = 0.1f)
        {
            _q = new float[,] {
                { processNoise, 0, 0, 0 },
                { 0, processNoise, 0, 0 },
                { 0, 0, processNoise, 0 },
                { 0, 0, 0, processNoise }
            };

            _r = new float[,] {
                { measurementNoise, 0 },
                { 0, measurementNoise }
            };

            if (initialState.HasValue)
            {
                _x[0] = initialState.Value.X;
                _x[1] = initialState.Value.Y;
            }

            for (int i = 0; i < 4; i++)
            {
                _p[i, i] = initialCovariance;
            }

            // Pre-calculate the transpose of F as it's constant
            Transpose(_f, _transposedF);
            Transpose(_h, _transposedH);
        }

        /// <summary>
        /// Predicts the next state.
        /// </summary>
        public void Predict()
        {
            // Predict state: x = F * x
            MultiplyVector(_f, _x, _predictedState);
            Array.Copy(_predictedState, _x, _x.Length);

            // Predict covariance: P = F * P * F' + Q
            MultiplyMatrices(_f, _p, _tempP);
            MultiplyMatrices(_tempP, _transposedF, _p);
            AddMatrices(_p, _q, _p);
        }

        /// <summary>
        /// Corrects the state with a new measurement.
        /// </summary>
        /// <param name="measurement">The measurement.</param>
        public void Correct(PointF measurement)
        {
            // Kalman Gain: K = P * H' * (H * P * H' + R)^-1
            MultiplyMatrices(_p, _transposedH, _pht);
            MultiplyMatrices(_h, _pht, _s);
            AddMatrices(_s, _r, _s);
            Invert(_s, _sInv);
            MultiplyMatrices(_pht, _sInv, _k);

            // Update state: x = x + K * (z - H * x)
            float[] z = { measurement.X, measurement.Y };
            MultiplyVector(_h, _x, _innovation);
            SubtractVectors(z, _innovation, _innovation);
            MultiplyVector(_k, _innovation, _predictedState); // Reuse predictedState as a temp buffer
            AddVectors(_x, _predictedState, _x);


            // Update covariance: P = (I - K * H) * P
            MultiplyMatrices(_k, _h, _kh);
            SubtractMatrices(_i, _kh, _ikh);
            MultiplyMatrices(_ikh, _p, _tempP);
            Array.Copy(_tempP, _p, _p.Length);
        }

        public PointF GetPredictedPosition()
        {
            return new PointF(_x[0], _x[1]);
        }
        
        #region Matrix Operations (Allocation-Free)

        private static void Clear(float[,] matrix) => Array.Clear(matrix, 0, matrix.Length);
        private static void Clear(float[] vector) => Array.Clear(vector, 0, vector.Length);

        private static void MultiplyVector(float[,] a, float[] b, float[] result)
        {
            Clear(result);
            for (int i = 0; i < a.GetLength(0); i++)
            {
                for (int j = 0; j < b.Length; j++)
                {
                    result[i] += a[i, j] * b[j];
                }
            }
        }

        private static void MultiplyMatrices(float[,] a, float[,] b, float[,] result)
        {
            Clear(result);
            for (int i = 0; i < a.GetLength(0); i++)
            {
                for (int j = 0; j < b.GetLength(1); j++)
                {
                    for (int k = 0; k < a.GetLength(1); k++)
                    {
                        result[i, j] += a[i, k] * b[k, j];
                    }
                }
            }
        }
        private static void AddMatrices(float[,] a, float[,] b, float[,] result)
        {
            for (int i = 0; i < a.GetLength(0); i++)
            {
                for (int j = 0; j < a.GetLength(1); j++)
                {
                    result[i, j] = a[i, j] + b[i, j];
                }
            }
        }
        
        private static void AddVectors(float[] a, float[] b, float[] result)
        {
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = a[i] + b[i];
            }
        }

        private static void SubtractVectors(float[] a, float[] b, float[] result)
        {
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = a[i] - b[i];
            }
        }

        private static void SubtractMatrices(float[,] a, float[,] b, float[,] result)
        {
            for (int i = 0; i < a.GetLength(0); i++)
            {
                for (int j = 0; j < a.GetLength(1); j++)
                {
                    result[i, j] = a[i, j] - b[i, j];
                }
            }
        }

        private static void Transpose(float[,] a, float[,] result)
        {
            for (int i = 0; i < a.GetLength(0); i++)
            {
                for (int j = 0; j < a.GetLength(1); j++)
                {
                    result[j, i] = a[i, j];
                }
            }
        }
        
        private static float[,] Transpose(float[,] a)
        {
            float[,] result = new float[a.GetLength(1), a.GetLength(0)];
            Transpose(a, result);
            return result;
        }

        private static void Invert(float[,] a, float[,] result)
        {
            float det = 1.0f / (a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0]);
            result[0, 0] = a[1, 1] * det;
            result[0, 1] = -a[0, 1] * det;
            result[1, 0] = -a[1, 0] * det;
            result[1, 1] = a[0, 0] * det;
        }

        private static float[,] Identity(int size)
        {
            float[,] result = new float[size, size];
            for (int i = 0; i < size; i++)
            {
                result[i, i] = 1;
            }
            return result;
        }

        #endregion
    }
} 