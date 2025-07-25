using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    [BurstCompile]
    public struct BlueNoise2D : IJob, IDisposable
    {
        private float2 space;
        private float radius;
        private int k;
        private Random random;
        private float2 start;
        private int2 gridSize;
        private NativeArray<int> grid;
        private NativeList<float2> points;

        public NativeArray<int> Grid => grid;
        public int2 GridSize => gridSize;
        public NativeList<float2> Points => points;

        // https://www.cs.ubc.ca/~rbridson/docs/bridson-siggraph07-poissondisk.pdf
        public BlueNoise2D(float2 space, float radius, float2 start, Random random, int k = 30)
        {
            this.space = space;
            this.radius = radius;
            this.start = start;
            this.random = random;
            this.k = k;

            gridSize = (int2)ceil(space * (SQRT2 / radius));

            grid = new NativeArray<int>(gridSize.x * gridSize.y, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            // The initial capacity is how many circles can you pack inside the space using grid packing:
            // area(space) / area(diameter) == space.x * space.y / (radius * 2) ^ 2
            points = new NativeList<float2>((int)(space.x * space.y / (radius * radius * 4f)),
                Allocator.TempJob);
        }

        public void Dispose()
        {
            grid.Dispose();
            points.Dispose();
        }

        public void Execute()
        {
            for (int i = 0; i < grid.Length; i++)
                grid[i] = -1;

            var active = new NativeList<int>(Allocator.Temp);
            EmitPoint(start, active);

            while (active.Length > 0)
            {
                int index = random.NextInt(active.Length);
                float2 center = points[active[index]];
                bool couldEmitPoint = false;

                for (int i = 0; i < k; i++)
                {
                    float r = sqrt(random.NextFloat()) * radius + radius;
                    float t = random.NextFloat() * PI2;
                    float2 point = wrap(float2(cos(t), sin(t)) * r + center, space);

                    if (CanEmitPoint(point))
                    {
                        if (points.Length >= int.MaxValue)
                            goto end;

                        EmitPoint(point, active);
                        couldEmitPoint = true;
                    }
                }

                if (!couldEmitPoint)
                    active.RemoveAtSwapBack(index);
            }

            end:
            active.Dispose();
        }

        private bool CanEmitPoint(float2 point)
        {
            int2 min = (int2)floor((point - radius) / space * gridSize);
            int2 max = (int2)ceil((point + radius) / space * gridSize);
            float minSqrDistance = radius * radius * 4f;

            for (int y = min.y; y < max.y; y++)
            for (int x = min.x; x < max.x; x++)
            {
                int2 cell = wrap(int2(x, y), gridSize);
                int index = grid[cell.y * gridSize.x + cell.x];

                if (index >= 0 && distancesq(point, points[index]) < minSqrDistance)
                    return false;
            }

            return true;
        }

        private void EmitPoint(float2 point, NativeList<int> active)
        {
            int index = points.Length;
            active.Add(index);
            points.Add(point);
            int2 cell = wrap((int2)floor(point / space * gridSize), gridSize);
            grid[cell.y * gridSize.x + cell.x] = index;
        }

        private static float wrap(float value, float upperBound) =>
            (value % upperBound + upperBound) % upperBound;

        private static float2 wrap(float2 value, float2 upperBound) =>
            float2(wrap(value.x, upperBound.x), wrap(value.y, upperBound.y));

        private static int wrap(int value, int upperBound) =>
            (value % upperBound + upperBound) % upperBound;

        private static int2 wrap(int2 value, int2 upperBound) =>
            int2(wrap(value.x, upperBound.x), wrap(value.y, upperBound.y));
    }
}
