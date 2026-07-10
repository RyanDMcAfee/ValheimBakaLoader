using System;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Minimal 2D float vector with Unity Vector2 semantics where they matter
    /// for worldgen determinism: approximate equality (sqrMagnitude &lt; 1e-10-ish),
    /// float-precision magnitude/distance and a guarded Normalize.
    /// </summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public float X;
        public float Y;

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public float Magnitude => (float)Math.Sqrt(X * X + Y * Y);

        public float SqrMagnitude => X * X + Y * Y;

        public static float Distance(Vec2 a, Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public Vec2 Normalized
        {
            get
            {
                float mag = Magnitude;
                if (mag > 1e-05f)
                {
                    return new Vec2(X / mag, Y / mag);
                }
                return Zero;
            }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);

        /// <summary>Unity's approximate equality: squared distance below ~1e-10.</summary>
        public static bool operator ==(Vec2 a, Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy < 9.99999944E-11f;
        }

        public static bool operator !=(Vec2 a, Vec2 b) => !(a == b);

        public bool Equals(Vec2 other) => this == other;

        public override bool Equals(object obj) => obj is Vec2 v && this == v;

        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2);

        public override string ToString() => $"({X}, {Y})";
    }

    /// <summary>Integer grid coordinate (river grid key).</summary>
    public readonly struct GridPos : IEquatable<GridPos>
    {
        public readonly int X;
        public readonly int Y;

        public GridPos(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridPos other) => X == other.X && Y == other.Y;

        public override bool Equals(object obj) => obj is GridPos g && Equals(g);

        public override int GetHashCode() => X * 397 ^ Y;
    }
}
