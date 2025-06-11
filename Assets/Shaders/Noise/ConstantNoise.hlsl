#include "ConstantNoise.cs"

void Noise_float(float3 PositionIn, float4 TerrainBounds, out float3 PositionOut,
    out float3 Normal)
{
    PositionOut.x = clamp(PositionIn.x, TerrainBounds.x, TerrainBounds.y);
    PositionOut.z = clamp(PositionIn.z, TerrainBounds.z, TerrainBounds.w);
    PositionOut.y = GetHeight(PositionOut.x, PositionOut.z);
    Normal = GetNormal(PositionOut.x, PositionOut.z);
}

void Occupancy_float(float3 Position, out float2 UV)
{
    UV = (frac(Position.xz * 0.0625f) - 0.5f) * 32.0f;
}
