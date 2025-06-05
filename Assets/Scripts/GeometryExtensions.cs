using System;
using Unity.Mathematics;
using Unity.Mathematics.Geometry;
using UnityEngine;
using static Unity.Mathematics.math;

namespace Decentraland.Terrain
{
    public static class GeometryExtensions
    {
        public static void Clip(ref this MinMaxAABB bounds, Bounds other)
        {
            bounds.Min = max(bounds.Min, other.min);
            bounds.Max = min(bounds.Max, other.max);
        }

        public static void Clip(ref this MinMaxAABB bounds, MinMaxAABB other)
        {
            bounds.Min = max(bounds.Min, other.Min);
            bounds.Max = min(bounds.Max, other.Max);
        }

        public static float3 GetCorner(this MinMaxAABB bounds, int index)
        {
            switch (index)
            {
                case 0b000: return bounds.Min;
                case 0b001: return float3(bounds.Min.xy, bounds.Max.z);
                case 0b010: return float3(bounds.Min.x, bounds.Max.y, bounds.Min.z);
                case 0b011: return float3(bounds.Min.x, bounds.Max.yz);
                case 0b100: return float3(bounds.Max.x, bounds.Min.yz);
                case 0b101: return float3(bounds.Max.x, bounds.Min.y, bounds.Max.z);
                case 0b110: return float3(bounds.Max.xy, bounds.Min.z);
                case 0b111: return bounds.Max;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public static Bounds ToBounds(this MinMaxAABB bounds)
        {
            return new Bounds(bounds.Center, bounds.Extents);
        }

        public static MinMaxAABB ToMinMaxAABB(this Bounds bounds)
        {
            return new MinMaxAABB(bounds.min, bounds.max);
        }
    }
}
