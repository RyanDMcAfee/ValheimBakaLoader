namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Deterministic reimplementation of Unity's Random PRNG (xorshift128),
    /// re-derived from the publicly documented algorithm so map/weather math
    /// matches the game's world generation exactly. Instance-based (no global
    /// state) — each consumer owns its own stream.
    /// </summary>
    public sealed class UnityRandom
    {
        private uint _x, _y, _z, _w;

        public UnityRandom(int seed)
        {
            InitState(seed);
        }

        public void InitState(int seed)
        {
            unchecked
            {
                _x = (uint)seed;
                _y = _x * 1812433253u + 1u;
                _z = _y * 1812433253u + 1u;
                _w = _z * 1812433253u + 1u;
            }
        }

        public uint NextUInt()
        {
            unchecked
            {
                uint t = _x ^ (_x << 11);
                t ^= t >> 8;
                _x = _y; _y = _z; _z = _w;
                _w = _w ^ (_w >> 19) ^ t;
                return _w;
            }
        }

        /// <summary>23-bit mantissa fraction in [0,1] (inclusive of 1).</summary>
        private float NextFloat01()
        {
            return (NextUInt() & 0x7FFFFF) / 8388607f;
        }

        /// <summary>Unity Random.value — fraction, no inversion.</summary>
        public float Value => NextFloat01();

        /// <summary>Unity Random.Range(float, float) — REVERSED lerp, both ends inclusive.</summary>
        public float Range(float min, float max)
        {
            return (min - max) * NextFloat01() + max;
        }

        /// <summary>Unity Random.Range(int, int) — min inclusive, max exclusive.</summary>
        public int Range(int min, int maxExclusive)
        {
            unchecked
            {
                if (min > maxExclusive)
                {
                    (min, maxExclusive) = (maxExclusive, min);
                }
                uint diff = (uint)(maxExclusive - min);
                if (diff == 0u)
                {
                    return min;
                }
                return min + (int)(NextUInt() % diff);
            }
        }
    }
}
