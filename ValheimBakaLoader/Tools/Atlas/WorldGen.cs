using System;
using System.Collections.Generic;

namespace ValheimBakaLoader.Tools.Atlas
{
    /// <summary>
    /// Biome flags matching the game's save-format values (needed for .db compat later).
    /// </summary>
    [Flags]
    public enum Biome
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 4,
        BlackForest = 8,
        Plains = 16,
        AshLands = 32,
        DeepNorth = 64,
        Ocean = 256,
        Mistlands = 512,
    }

    /// <summary>
    /// Deterministic re-derivation of Valheim's world generation (biomes, base
    /// height, rivers/streams, per-biome terrain height) for rendering a map
    /// from a seed without the game or any mods. Written from an algorithm
    /// spec; float/double mixing deliberately mirrors the game's arithmetic so
    /// results match pixel-for-pixel.
    ///
    /// Known approximation: live Ashlands terrain uses a cellular-noise
    /// crescent (FastNoise) that only affects intra-biome texture/lava; this
    /// port renders Ashlands with the pregeneration formula plus the gap
    /// moats, which is correct for coastline/biome shape at map scale.
    /// </summary>
    public sealed class WorldGen
    {
        public const float WorldRadius = 10000f;
        public const float WorldEdge = 10500f;
        public const float HeightMultiplier = 200f;
        public const float SeaLevelMeters = 30f;

        private const float AshlandsMinDistance = 12000f;
        private const float AshlandsYOffset = -4000f;

        private readonly int _seed;
        private readonly int _version;

        private float _minMountainDistance = 1000f;
        private float _minDarklandNoise = 0.4f;
        private float _maxMarshDistance = 6000f;

        private readonly float _offset0;
        private readonly float _offset1;
        private readonly float _offset2;
        private readonly float _offset3;
        private readonly float _offset4;
        private readonly int _riverSeed;
        private readonly int _streamSeed;

        private readonly UnityRandom _rng;

        private List<Vec2> _lakes = new List<Vec2>();
        private readonly Dictionary<GridPos, RiverPoint[]> _riverPoints = new Dictionary<GridPos, RiverPoint[]>();

        public sealed class River
        {
            public Vec2 P0;
            public Vec2 P1;
            public Vec2 Center;
            public float WidthMax;
            public float WidthMin;
            public float CurveWidth;
            public float CurveWavelength;
        }

        private readonly struct RiverPoint
        {
            public readonly Vec2 P;
            public readonly float W;
            public readonly float W2;

            public RiverPoint(Vec2 p, float w)
            {
                P = p;
                W = w;
                W2 = w * w;
            }
        }

        public int Seed => _seed;

        /// <summary>Number of populated river-grid cells after pregeneration (diagnostics/tests).</summary>
        public int RiverGridCellCount => _riverPoints.Count;

        public WorldGen(int seed, int worldGenVersion = 2)
        {
            _seed = seed;
            _version = worldGenVersion;
            VersionSetup(_version);

            _rng = new UnityRandom(seed);
            _offset0 = _rng.Range(-10000, 10000);
            _offset1 = _rng.Range(-10000, 10000);
            _offset2 = _rng.Range(-10000, 10000);
            _offset3 = _rng.Range(-10000, 10000);
            _riverSeed = _rng.Range(int.MinValue, int.MaxValue);
            _streamSeed = _rng.Range(int.MinValue, int.MaxValue);
            _offset4 = _rng.Range(-10000, 10000);

            Pregenerate();
        }

        private void VersionSetup(int version)
        {
            if (version <= 0)
            {
                _minMountainDistance = 1500f;
            }
            if (version <= 1)
            {
                _minDarklandNoise = 0.5f;
                _maxMarshDistance = 8000f;
            }
        }

        private void Pregenerate()
        {
            FindLakes();
            PlaceRivers();
            PlaceStreams();
        }

        // ---------------------------------------------------------------
        // Lakes
        // ---------------------------------------------------------------

        private void FindLakes()
        {
            var candidates = new List<Vec2>();
            for (float wy = -10000f; wy <= 10000f; wy = (float)((double)wy + 128.0))
            {
                for (float wx = -10000f; wx <= 10000f; wx = (float)((double)wx + 128.0))
                {
                    if (!(new Vec2(wx, wy).Magnitude > 10000f) && GetBaseHeight(wx, wy) < 0.05f)
                    {
                        candidates.Add(new Vec2(wx, wy));
                    }
                }
            }
            _lakes = MergePoints(candidates, 800f);
        }

        private static List<Vec2> MergePoints(List<Vec2> points, float range)
        {
            var merged = new List<Vec2>();
            while (points.Count > 0)
            {
                Vec2 current = points[0];
                points.RemoveAt(0);
                while (points.Count > 0)
                {
                    int idx = FindClosest(points, current, range);
                    if (idx == -1)
                    {
                        break;
                    }
                    current = (current + points[idx]) * 0.5f;
                    points[idx] = points[points.Count - 1];
                    points.RemoveAt(points.Count - 1);
                }
                merged.Add(current);
            }
            return merged;
        }

        private static int FindClosest(List<Vec2> points, Vec2 p, float maxDistance)
        {
            int result = -1;
            float best = 99999f;
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i] == p))
                {
                    float d = Vec2.Distance(p, points[i]);
                    if (d < maxDistance && d < best)
                    {
                        result = i;
                        best = d;
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------
        // Rivers
        // ---------------------------------------------------------------

        private void PlaceRivers()
        {
            _rng.InitState(_riverSeed);
            var rivers = new List<River>();
            var open = new List<Vec2>(_lakes);
            while (open.Count > 1)
            {
                Vec2 lake = open[0];
                int end = FindRandomRiverEnd(rivers, _lakes, lake, 2000f, 0.4f, 128f);
                if (end == -1 && !HaveRiver(rivers, lake))
                {
                    end = FindRandomRiverEnd(rivers, _lakes, lake, 5000f, 0.4f, 128f);
                }
                if (end != -1)
                {
                    var river = new River
                    {
                        P0 = lake,
                        P1 = _lakes[end],
                    };
                    river.Center = (river.P0 + river.P1) * 0.5f;
                    river.WidthMax = _rng.Range(60f, 100f);
                    river.WidthMin = _rng.Range(60f, river.WidthMax);
                    float dist = Vec2.Distance(river.P0, river.P1);
                    river.CurveWidth = (float)((double)dist / 15.0);
                    river.CurveWavelength = (float)((double)dist / 20.0);
                    rivers.Add(river);
                }
                else
                {
                    open.RemoveAt(0);
                }
            }
            RenderRivers(rivers);
        }

        private int FindRandomRiverEnd(List<River> rivers, List<Vec2> points, Vec2 p, float maxDistance, float heightLimit, float checkStep)
        {
            var candidates = new List<int>();
            for (int i = 0; i < points.Count; i++)
            {
                if (!(points[i] == p) && Vec2.Distance(p, points[i]) < maxDistance
                    && !HaveRiver(rivers, p, points[i]) && IsRiverAllowed(p, points[i], checkStep, heightLimit))
                {
                    candidates.Add(i);
                }
            }
            if (candidates.Count == 0)
            {
                return -1;
            }
            return candidates[_rng.Range(0, candidates.Count)];
        }

        private static bool HaveRiver(List<River> rivers, Vec2 p0)
        {
            foreach (River river in rivers)
            {
                if (river.P0 == p0 || river.P1 == p0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HaveRiver(List<River> rivers, Vec2 p0, Vec2 p1)
        {
            foreach (River river in rivers)
            {
                if ((river.P0 == p0 && river.P1 == p1) || (river.P0 == p1 && river.P1 == p0))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsRiverAllowed(Vec2 p0, Vec2 p1, float step, float heightLimit)
        {
            float dist = Vec2.Distance(p0, p1);
            Vec2 dir = (p1 - p0).Normalized;
            bool allInWater = true;
            for (float t = step; t <= (float)((double)dist - (double)step); t = (float)((double)t + (double)step))
            {
                Vec2 sample = p0 + dir * t;
                float bh = GetBaseHeight(sample.X, sample.Y);
                if (bh > heightLimit)
                {
                    return false;
                }
                if (bh > 0.05f)
                {
                    allInWater = false;
                }
            }
            return !allInWater;
        }

        // ---------------------------------------------------------------
        // Streams
        // ---------------------------------------------------------------

        private void PlaceStreams()
        {
            _rng.InitState(_streamSeed);
            var streams = new List<River>();
            for (int i = 0; i < 3000; i++)
            {
                if (FindStreamStartPoint(100, 26f, 31f, out Vec2 start)
                    && FindStreamEndPoint(100, 36f, 44f, start, 80f, 200f, out Vec2 end))
                {
                    Vec2 center = (start + end) * 0.5f;
                    float midHeight = GetPregenerationHeight(center.X, center.Y);
                    if (!(midHeight < 26f) && !(midHeight > 44f))
                    {
                        var stream = new River
                        {
                            P0 = start,
                            P1 = end,
                            Center = center,
                            WidthMax = 20f,
                            WidthMin = 20f,
                        };
                        float dist = Vec2.Distance(stream.P0, stream.P1);
                        stream.CurveWidth = (float)((double)dist / 15.0);
                        stream.CurveWavelength = (float)((double)dist / 20.0);
                        streams.Add(stream);
                    }
                }
            }
            RenderRivers(streams);
        }

        private bool FindStreamStartPoint(int iterations, float minHeight, float maxHeight, out Vec2 p)
        {
            for (int i = 0; i < iterations; i++)
            {
                float wx = _rng.Range(-10000f, 10000f);
                float wy = _rng.Range(-10000f, 10000f);
                float h = GetPregenerationHeight(wx, wy);
                if (h > minHeight && h < maxHeight)
                {
                    p = new Vec2(wx, wy);
                    return true;
                }
            }
            p = Vec2.Zero;
            return false;
        }

        private bool FindStreamEndPoint(int iterations, float minHeight, float maxHeight, Vec2 start, float minLength, float maxLength, out Vec2 end)
        {
            float shrink = (float)(((double)maxLength - (double)minLength) / iterations);
            float radius = maxLength;
            for (int i = 0; i < iterations; i++)
            {
                radius = (float)((double)radius - (double)shrink);
                float angle = _rng.Range(0f, MathF.PI * 2f);
                Vec2 candidate = start + new Vec2((float)Math.Sin(angle), (float)Math.Cos(angle)) * radius;
                float h = GetPregenerationHeight(candidate.X, candidate.Y);
                if (h > minHeight && h < maxHeight)
                {
                    end = candidate;
                    return true;
                }
            }
            end = Vec2.Zero;
            return false;
        }

        // ---------------------------------------------------------------
        // River rasterization into the 64 m grid
        // ---------------------------------------------------------------

        private void RenderRivers(List<River> rivers)
        {
            var accumulated = new Dictionary<GridPos, List<RiverPoint>>();
            foreach (River river in rivers)
            {
                float step = (float)((double)river.WidthMin / 8.0);
                Vec2 dir = (river.P1 - river.P0).Normalized;
                Vec2 perp = new Vec2(0f - dir.Y, dir.X);
                float dist = Vec2.Distance(river.P0, river.P1);
                for (float t = 0f; t <= dist; t = (float)((double)t + (double)step))
                {
                    float f = (float)((double)t / river.CurveWavelength);
                    float wobble = (float)(Math.Sin(f) * Math.Sin((double)f * 0.634119987487793) * Math.Sin((double)f * 0.3341200053691864) * river.CurveWidth);
                    float r = _rng.Range(river.WidthMin, river.WidthMax);
                    Vec2 p = river.P0 + dir * t + perp * wobble;
                    AddRiverPoint(accumulated, p, r);
                }
            }
            foreach (KeyValuePair<GridPos, List<RiverPoint>> cell in accumulated)
            {
                if (_riverPoints.TryGetValue(cell.Key, out RiverPoint[] existing))
                {
                    var combined = new List<RiverPoint>(existing);
                    combined.AddRange(cell.Value);
                    _riverPoints[cell.Key] = combined.ToArray();
                }
                else
                {
                    _riverPoints.Add(cell.Key, cell.Value.ToArray());
                }
            }
        }

        private void AddRiverPoint(Dictionary<GridPos, List<RiverPoint>> points, Vec2 p, float r)
        {
            GridPos center = GetRiverGrid(p.X, p.Y);
            int span = (int)Math.Ceiling((float)((double)r / 64.0));
            for (int gy = center.Y - span; gy <= center.Y + span; gy++)
            {
                for (int gx = center.X - span; gx <= center.X + span; gx++)
                {
                    var grid = new GridPos(gx, gy);
                    if (InsideRiverGrid(grid, p, r))
                    {
                        if (points.TryGetValue(grid, out List<RiverPoint> list))
                        {
                            list.Add(new RiverPoint(p, r));
                        }
                        else
                        {
                            points.Add(grid, new List<RiverPoint> { new RiverPoint(p, r) });
                        }
                    }
                }
            }
        }

        private static bool InsideRiverGrid(GridPos grid, Vec2 p, float r)
        {
            var cellCenter = new Vec2((float)((double)grid.X * 64.0), (float)((double)grid.Y * 64.0));
            Vec2 delta = p - cellCenter;
            if (Math.Abs(delta.X) < (float)((double)r + 32.0))
            {
                return Math.Abs(delta.Y) < (float)((double)r + 32.0);
            }
            return false;
        }

        private static GridPos GetRiverGrid(float wx, float wy)
        {
            int x = (int)Math.Floor((float)(((double)wx + 32.0) / 64.0));
            int y = (int)Math.Floor((float)(((double)wy + 32.0) / 64.0));
            return new GridPos(x, y);
        }

        private void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            // No single-entry cache (unlike the game): direct dictionary reads are
            // already fast and keep this safe for parallel rendering.
            GridPos grid = GetRiverGrid(wx, wy);
            if (_riverPoints.TryGetValue(grid, out RiverPoint[] points))
            {
                GetWeight(points, wx, wy, out weight, out width);
            }
            else
            {
                weight = 0f;
                width = 0f;
            }
        }

        private static void GetWeight(RiverPoint[] points, float wx, float wy, out float weight, out float width)
        {
            var pos = new Vec2(wx, wy);
            weight = 0f;
            width = 0f;
            float weightedWidthSum = 0f;
            float weightSum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                RiverPoint rp = points[i];
                float dSq = (rp.P - pos).SqrMagnitude;
                if (dSq < rp.W2)
                {
                    float d = (float)Math.Sqrt(dSq);
                    float q = (float)(1.0 - (double)d / rp.W);
                    if (q > weight)
                    {
                        weight = q;
                    }
                    weightedWidthSum = (float)((double)weightedWidthSum + (double)rp.W * q);
                    weightSum = (float)((double)weightSum + (double)q);
                }
            }
            if (weightSum > 0f)
            {
                width = (float)((double)weightedWidthSum / weightSum);
            }
        }

        // ---------------------------------------------------------------
        // Biomes
        // ---------------------------------------------------------------

        public static float WorldAngle(float wx, float wy)
        {
            return (float)Math.Sin((float)((double)(float)Math.Atan2(wx, wy) * 20.0));
        }

        public static bool IsAshlands(float x, float y)
        {
            double angle = (double)WorldAngle(x, y) * 100.0;
            return (double)Length(x, (float)((double)y + (double)AshlandsYOffset)) > (double)AshlandsMinDistance + angle;
        }

        /// <summary>Signed gradient into the Ashlands ocean band (used for weather overrides).</summary>
        public static float GetAshlandsOceanGradient(float x, float y)
        {
            double angle = (double)WorldAngle(x, y + AshlandsYOffset) * 100.0;
            return (float)(((double)Length(x, y + AshlandsYOffset) - ((double)AshlandsMinDistance + angle)) / 300.0);
        }

        public static bool IsDeepnorth(float x, float y)
        {
            float angle = (float)((double)WorldAngle(x, y) * 100.0);
            return new Vec2(x, (float)((double)y + 4000.0)).Magnitude > (float)(12000.0 + (double)angle);
        }

        public Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f)
        {
            float dist = Length(wx, wy);
            float baseHeight = GetBaseHeight(wx, wy);
            float angle = (float)((double)WorldAngle(wx, wy) * 100.0);
            if (IsAshlands(wx, wy))
            {
                return Biome.AshLands;
            }
            if (baseHeight <= oceanLevel)
            {
                return Biome.Ocean;
            }
            if (IsDeepnorth(wx, wy))
            {
                if (baseHeight > 0.4f)
                {
                    return Biome.Mountain;
                }
                return Biome.DeepNorth;
            }
            if (baseHeight > 0.4f)
            {
                return Biome.Mountain;
            }
            if (UnityPerlin.Noise((double)(float)((double)_offset0 + (double)wx) * 0.0010000000474974513, (double)(float)((double)_offset0 + (double)wy) * 0.0010000000474974513) > 0.6f
                && dist > 2000f && dist < _maxMarshDistance && baseHeight > 0.05f && baseHeight < 0.25f)
            {
                return Biome.Swamp;
            }
            if (UnityPerlin.Noise((double)(float)((double)_offset4 + (double)wx) * 0.0010000000474974513, (double)(float)((double)_offset4 + (double)wy) * 0.0010000000474974513) > _minDarklandNoise
                && dist > (float)(6000.0 + (double)angle) && dist < 10000f)
            {
                return Biome.Mistlands;
            }
            if (UnityPerlin.Noise((double)(float)((double)_offset1 + (double)wx) * 0.0010000000474974513, (double)(float)((double)_offset1 + (double)wy) * 0.0010000000474974513) > 0.4f
                && dist > (float)(3000.0 + (double)angle) && dist < 8000f)
            {
                return Biome.Plains;
            }
            if (UnityPerlin.Noise((double)(float)((double)_offset2 + (double)wx) * 0.0010000000474974513, (double)(float)((double)_offset2 + (double)wy) * 0.0010000000474974513) > 0.4f
                && dist > (float)(600.0 + (double)angle) && dist < 6000f)
            {
                return Biome.BlackForest;
            }
            if (dist > (float)(5000.0 + (double)angle))
            {
                return Biome.BlackForest;
            }
            return Biome.Meadows;
        }

        // ---------------------------------------------------------------
        // Base height
        // ---------------------------------------------------------------

        public float GetBaseHeight(float wx, float wy)
        {
            float dist = Length(wx, wy);
            double x = wx;
            double y = wy;
            x += 100000.0 + (double)_offset0;
            y += 100000.0 + (double)_offset1;
            float h = 0f;
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.0020000000949949026 * 0.5, y * 0.0020000000949949026 * 0.5) * (double)UnityPerlin.Noise(x * 0.003000000026077032 * 0.5, y * 0.003000000026077032 * 0.5) * 1.0);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.0020000000949949026 * 1.0, y * 0.0020000000949949026 * 1.0) * (double)UnityPerlin.Noise(x * 0.003000000026077032 * 1.0, y * 0.003000000026077032 * 1.0) * (double)h * 0.8999999761581421);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.004999999888241291 * 1.0, y * 0.004999999888241291 * 1.0) * (double)UnityPerlin.Noise(x * 0.009999999776482582 * 1.0, y * 0.009999999776482582 * 1.0) * 0.5 * (double)h);
            h = (float)((double)h - 0.07000000029802322);
            float n1 = UnityPerlin.Noise(x * 0.0020000000949949026 * 0.25 + 0.12300000339746475, y * 0.0020000000949949026 * 0.25 + 0.15123000741004944);
            float n2 = UnityPerlin.Noise(x * 0.0020000000949949026 * 0.25 + 0.32100000977516174, y * 0.0020000000949949026 * 0.25 + 0.23100000619888306);
            float channel = Math.Abs((float)((double)n1 - (double)n2));
            float carve = (float)(1.0 - (double)LerpStep(0.02f, 0.12f, channel));
            carve = (float)((double)carve * (double)SmoothStep(744f, 1000f, dist));
            h = (float)((double)h * (1.0 - (double)carve));
            if (dist > 10000f)
            {
                float t = LerpStep(10000f, 10500f, dist);
                h = Lerp(h, -0.2f, t);
                float edgeStart = 10490f;
                if (dist > edgeStart)
                {
                    float t2 = LerpStepF(edgeStart, 10500f, dist);
                    h = Lerp(h, -2f, t2);
                }
                return h;
            }
            if (dist < _minMountainDistance && h > 0.28f)
            {
                float t3 = (float)Clamp01(((double)h - 0.2800000011920929) / 0.09999999403953552);
                h = Lerp(Lerp(0.28f, 0.38f, t3), h, LerpStep((float)((double)_minMountainDistance - 400.0), _minMountainDistance, dist));
            }
            return h;
        }

        private float AddRivers(float wx, float wy, float h)
        {
            GetRiverWeight(wx, wy, out float weight, out float width);
            if (weight <= 0f)
            {
                return h;
            }
            float t = LerpStep(20f, 60f, width);
            float bed1 = Lerp(0.14f, 0.12f, t);
            float bed2 = Lerp(0.139f, 0.128f, t);
            if (h > bed1)
            {
                h = Lerp(h, bed1, weight);
            }
            if (h > bed2)
            {
                float t2 = LerpStep(0.85f, 1f, weight);
                h = Lerp(h, bed2, t2);
            }
            return h;
        }

        // ---------------------------------------------------------------
        // Heights (meters)
        // ---------------------------------------------------------------

        public float GetHeight(float wx, float wy)
        {
            Biome biome = GetBiome(wx, wy);
            return GetBiomeHeight(biome, wx, wy);
        }

        public float GetPregenerationHeight(float wx, float wy)
        {
            Biome biome = GetBiome(wx, wy);
            return GetBiomeHeight(biome, wx, wy, preGeneration: true);
        }

        public float GetBiomeHeight(Biome biome, float wx, float wy, bool preGeneration = false)
        {
            float mult = preGeneration
                ? HeightMultiplier
                : (float)((double)HeightMultiplier * CreateAshlandsGap(wx, wy) * CreateDeepNorthGap(wx, wy));
            if (Length(wx, wy) > 10500f)
            {
                return -2f * HeightMultiplier;
            }
            switch (biome)
            {
                case Biome.Swamp:
                    return (float)((double)GetMarshHeight(wx, wy) * (double)mult);
                case Biome.DeepNorth:
                    return (float)((double)GetDeepNorthHeight(wx, wy) * (double)mult);
                case Biome.Mountain:
                    return (float)((double)GetSnowMountainHeight(wx, wy) * (double)mult);
                case Biome.BlackForest:
                    return (float)((double)GetForestHeight(wx, wy) * (double)mult);
                case Biome.Ocean:
                    return (float)((double)GetBaseHeight(wx, wy) * (double)mult);
                case Biome.AshLands:
                    // Live Ashlands uses cellular noise for intra-biome texture; the
                    // pregeneration formula is the correct coastline/shape at map scale.
                    return (float)((double)GetAshlandsHeightPregenerate(wx, wy) * (double)mult);
                case Biome.Plains:
                    return (float)((double)GetPlainsHeight(wx, wy) * (double)mult);
                case Biome.Meadows:
                    return (float)((double)GetMeadowsHeight(wx, wy) * (double)mult);
                case Biome.Mistlands:
                    if (preGeneration)
                    {
                        return (float)((double)GetForestHeight(wx, wy) * (double)mult);
                    }
                    return (float)((double)GetMistlandsHeight(wx, wy) * (double)mult);
                default:
                    return 0f;
            }
        }

        private float GetMarshHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = 0.137f;
            wx = (float)((double)wx + 100000.0);
            wy = (float)((double)wy + 100000.0);
            double x = wx;
            double y = wy;
            float bump = (float)((double)UnityPerlin.Noise(x * 0.03999999910593033, y * 0.03999999910593033) * (double)UnityPerlin.Noise(x * 0.07999999821186066, y * 0.07999999821186066));
            h = (float)((double)h + (double)bump * 0.029999999329447746);
            h = AddRivers(origX, origY, h);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
        }

        private float GetMeadowsHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float baseHeight = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            float h = baseHeight;
            h = (float)((double)h + (double)bump * 0.10000000149011612);
            float flattenLevel = 0.15f;
            float above = (float)((double)h - (double)flattenLevel);
            float k = (float)Clamp01((double)baseHeight / 0.4000000059604645);
            if (above > 0f)
            {
                h = (float)((double)h - (double)above * ((1.0 - (double)k) * 0.75));
            }
            h = AddRivers(origX, origY, h);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
        }

        private float GetForestHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            h = (float)((double)h + (double)bump * 0.10000000149011612);
            h = AddRivers(origX, origY, h);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
        }

        private float GetMistlandsHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float ridges = UnityPerlin.Noise(x * 0.019999999552965164 * 0.699999988079071, y * 0.019999999552965164 * 0.699999988079071) * UnityPerlin.Noise(x * 0.03999999910593033 * 0.699999988079071, y * 0.03999999910593033 * 0.699999988079071);
            ridges = (float)((double)ridges + (double)UnityPerlin.Noise(x * 0.029999999329447746 * 0.699999988079071, y * 0.029999999329447746 * 0.699999988079071) * (double)UnityPerlin.Noise(x * 0.05000000074505806 * 0.699999988079071, y * 0.05000000074505806 * 0.699999988079071) * (double)ridges * 0.5);
            ridges = ridges > 0f ? (float)Math.Pow(ridges, 1.5) : ridges;
            h = (float)((double)h + (double)ridges * 0.4000000059604645);
            h = AddRivers(origX, origY, h);
            float ridgeMask = (float)Clamp01((double)ridges * 7.0);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.029999999329447746 * (double)ridgeMask);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.009999999776482582 * (double)ridgeMask);
            float smooth = (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.0020000000949949026);
            float terraced = h;
            terraced = (float)((double)terraced * 400.0);
            terraced = (float)Math.Ceiling(terraced);
            terraced = (float)((double)terraced / 400.0);
            return Lerp(smooth, terraced, ridgeMask);
        }

        private float GetPlainsHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float baseHeight = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            float h = baseHeight;
            h = (float)((double)h + (double)bump * 0.10000000149011612);
            float flattenLevel = 0.15f;
            float above = h - flattenLevel;
            float k = (float)Clamp01((double)baseHeight / 0.4000000059604645);
            if (above > 0f)
            {
                h = (float)((double)h - (double)above * (1.0 - (double)k) * 0.75);
            }
            h = AddRivers(origX, origY, h);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            return (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
        }

        private float GetAshlandsHeightPregenerate(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            h = (float)((double)h + (double)bump * 0.10000000149011612);
            h = (float)((double)h + 0.10000000149011612);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
            return AddRivers(origX, origY, h);
        }

        private float BaseHeightTilt(float wx, float wy)
        {
            float left = GetBaseHeight((float)((double)wx - 1.0), wy);
            float right = GetBaseHeight((float)((double)wx + 1.0), wy);
            float down = GetBaseHeight(wx, (float)((double)wy - 1.0));
            float up = GetBaseHeight(wx, (float)((double)wy + 1.0));
            return (float)((double)Math.Abs((float)((double)right - (double)left)) + (double)Math.Abs((float)((double)down - (double)up)));
        }

        private float GetSnowMountainHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = GetBaseHeight(wx, wy);
            float tilt = BaseHeightTilt(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float above = (float)((double)h - 0.4000000059604645);
            h = (float)((double)h + (double)above);
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            h = (float)((double)h + (double)bump * 0.20000000298023224);
            h = AddRivers(origX, origY, h);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)UnityPerlin.Noise(x * 0.4000000059604645, y * 0.4000000059604645) * 0.003000000026077032);
            return (float)((double)h + (double)UnityPerlin.Noise(x * 0.20000000298023224, y * 0.20000000298023224) * 2.0 * (double)tilt);
        }

        private float GetDeepNorthHeight(float wx, float wy)
        {
            float origX = wx;
            float origY = wy;
            float h = GetBaseHeight(wx, wy);
            wx = (float)((double)wx + 100000.0 + (double)_offset3);
            wy = (float)((double)wy + 100000.0 + (double)_offset3);
            double x = wx;
            double y = wy;
            float above = Math.Max(0f, (float)((double)h - 0.4000000059604645));
            h = (float)((double)h + (double)above);
            float bump = (float)((double)UnityPerlin.Noise(x * 0.009999999776482582, y * 0.009999999776482582) * (double)UnityPerlin.Noise(x * 0.019999999552965164, y * 0.019999999552965164));
            bump = (float)((double)bump + (double)UnityPerlin.Noise(x * 0.05000000074505806, y * 0.05000000074505806) * (double)UnityPerlin.Noise(x * 0.10000000149011612, y * 0.10000000149011612) * (double)bump * 0.5);
            h = (float)((double)h + (double)bump * 0.20000000298023224);
            h = (float)((double)h * 1.2000000476837158);
            h = AddRivers(origX, origY, h);
            // Detail noise here multiplies in FLOAT before widening — a quirk of
            // the game's DeepNorth path that differs from every other biome.
            h = (float)((double)h + (double)UnityPerlin.Noise((double)(wx * 0.1f), (double)(wy * 0.1f)) * 0.009999999776482582);
            return (float)((double)h + (double)UnityPerlin.Noise((double)(wx * 0.4f), (double)(wy * 0.4f)) * 0.003000000026077032);
        }

        private static double CreateAshlandsGap(float wx, float wy)
        {
            double angle = (double)WorldAngle(wx, wy) * 100.0;
            double value = (double)Length(wx, wy + AshlandsYOffset) - ((double)AshlandsMinDistance + angle);
            value = Clamp01(Math.Abs(value) / 400.0);
            return MathfLikeSmoothStep(0.0, 1.0, (float)value);
        }

        private static double CreateDeepNorthGap(float wx, float wy)
        {
            double angle = (double)WorldAngle(wx, wy) * 100.0;
            double value = (double)Length(wx, wy + 4000f) - (12000.0 + angle);
            value = Clamp01(Math.Abs(value) / 400.0);
            return MathfLikeSmoothStep(0.0, 1.0, (float)value);
        }

        // ---------------------------------------------------------------
        // Math helpers with the game's exact float/double semantics
        // ---------------------------------------------------------------

        private static float Length(float x, float y)
        {
            return (float)Math.Sqrt((double)x * x + (double)y * y);
        }

        private static float Lerp(float a, float b, float t)
        {
            if (t <= 0f)
            {
                return a;
            }
            if (t >= 1f)
            {
                return b;
            }
            return (float)((double)a * (1.0 - (double)t) + (double)b * (double)t);
        }

        private static float LerpStep(float l, float h, float v)
        {
            return (float)Clamp01(((double)v - (double)l) / ((double)h - (double)l));
        }

        /// <summary>Pure-float LerpStep (the game's Utils variant, used at the world edge).</summary>
        private static float LerpStepF(float l, float h, float v)
        {
            float t = (v - l) / (h - l);
            if (t < 0f)
            {
                return 0f;
            }
            if (t > 1f)
            {
                return 1f;
            }
            return t;
        }

        private static float SmoothStep(float min, float max, float x)
        {
            float n = (float)Clamp01(((double)x - (double)min) / ((double)max - (double)min));
            return (float)((double)n * (double)n * (3.0 - 2.0 * (double)n));
        }

        private static double MathfLikeSmoothStep(double from, double to, double t)
        {
            t = Clamp01(t);
            t = -2.0 * t * t * t + 3.0 * t * t;
            return (float)(to * t + from * (1.0 - t));
        }

        private static double Clamp01(double v)
        {
            if (v > 1.0)
            {
                return 1.0;
            }
            if (v < 0.0)
            {
                return 0.0;
            }
            return v;
        }
    }
}
