using System;

namespace FallRush.Shared
{
    /// <summary>
    /// Fixed-capacity ring buffer of float samples with allocation-free percentile queries
    /// (sorts into a preallocated scratch array). Not thread-safe.
    /// </summary>
    public sealed class RollingSamples
    {
        private readonly float[] _samples;
        private readonly float[] _scratch;
        private int _next;
        private int _count;

        public RollingSamples(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _samples = new float[capacity];
            _scratch = new float[capacity];
        }

        public int Count => _count;
        public int Capacity => _samples.Length;

        public void Add(float value)
        {
            _samples[_next] = value;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }

        public void Clear()
        {
            _next = 0;
            _count = 0;
        }

        /// <summary>Nearest-rank percentile, <paramref name="p"/> in [0, 1]. Returns NaN when empty.</summary>
        public float Percentile(float p)
        {
            if (_count == 0)
                return float.NaN;
            Array.Copy(_samples, _scratch, _count);
            Array.Sort(_scratch, 0, _count);
            return PercentileOfSorted(_scratch, _count, p);
        }

        /// <summary>Computes several percentiles with a single sort.</summary>
        public void Percentiles(float p1, float p2, out float v1, out float v2)
        {
            if (_count == 0)
            {
                v1 = v2 = float.NaN;
                return;
            }

            Array.Copy(_samples, _scratch, _count);
            Array.Sort(_scratch, 0, _count);
            v1 = PercentileOfSorted(_scratch, _count, p1);
            v2 = PercentileOfSorted(_scratch, _count, p2);
        }

        private static float PercentileOfSorted(float[] sorted, int count, float p)
        {
            if (p <= 0f)
                return sorted[0];
            if (p >= 1f)
                return sorted[count - 1];
            var rank = (int)Math.Ceiling(p * count) - 1;
            if (rank < 0)
                rank = 0;
            return sorted[rank];
        }
    }
}
