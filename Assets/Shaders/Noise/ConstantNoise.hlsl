#include "ConstantNoise.cs"

void Noise_float(float3 PositionIn, float InvParcelSize, float4 TerrainBounds,
                 UnityTexture2D OccupancyMap, out float3 PositionOut, out float3 Normal)
{
    PositionOut.x = clamp(PositionIn.x, TerrainBounds.x, TerrainBounds.y);
    PositionOut.z = clamp(PositionIn.z, TerrainBounds.z, TerrainBounds.w);

    float2 uv = (PositionOut.xz * InvParcelSize + OccupancyMap.texelSize.z * 0.5)
        * OccupancyMap.texelSize.x;

    float occupancy = SAMPLE_TEXTURE2D_LOD(OccupancyMap, OccupancyMap.samplerstate, uv, 0.0).r;

    // In the "worst case", if occupancy is 0.25, it can mean that the current vertex is on a corner
    // between one occupied parcel and three free ones, and height must be zero.
    if (occupancy < 0.25)
    {
        float height = GetHeight(PositionOut.x, PositionOut.z);
        PositionOut.y = lerp(height, 0.0, occupancy * 4.0);
        Normal = GetNormal(PositionOut.x, PositionOut.z);
    }
    else
    {
        PositionOut.y = 0.0;
        Normal = float3(0.0, 1.0, 0.0);
    }
}
