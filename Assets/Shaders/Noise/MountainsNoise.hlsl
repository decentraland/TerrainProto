#include "MountainsNoise.cs"

void Noise_float(float3 PositionIn, float ParcelSize, float4 TerrainBounds,
                 UnityTexture2D OccupancyMap, UnityTexture2D HeightMap, float HeightScale, out float3 PositionOut, out float3 Normal)
{
    PositionOut.x = clamp(PositionIn.x, TerrainBounds.x, TerrainBounds.y);
    PositionOut.z = clamp(PositionIn.z, TerrainBounds.z, TerrainBounds.w);

    float2 uv = (PositionOut.xz * ParcelSize + OccupancyMap.texelSize.z * 0.5)
        * OccupancyMap.texelSize.x;

    float height = SAMPLE_TEXTURE2D_LOD(OccupancyMap, OccupancyMap.samplerstate, uv, 0.0).r;
    float minValue = 175.0 / 255.0;
    if (height <= minValue) //0.25
    {
        // Flat surface (occupied parcels and above minValue threshold)
        PositionOut.y = 0.0;
        Normal = float3(0.0, 1.0, 0.0);
    }
    else // Mountain area with stepped heights
    {
        float normalizedHeight = (height - minValue) / (1 - minValue);

        float saturationFactor = 20;
        float noiseH = GetHeight(PositionOut.x, PositionOut.z) * saturate( normalizedHeight * saturationFactor);

        PositionOut.y = normalizedHeight * HeightScale + noiseH;
        Normal = GetNormal(PositionOut.x, PositionOut.z);

        // Ensure no negative heights
        if (PositionOut.y < 0.0)
        {
            PositionOut.y = 0.0;
            Normal = float3(0.0, 1.0, 0.0);
        }
    }
}
