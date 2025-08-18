#include "MountainsNoise.cs"

void Noise_float(float3 PositionIn, float ParcelSize, float4 TerrainBounds,
                 UnityTexture2D OccupancyMap, UnityTexture2D HeightMap, float HeightScale, out float3 PositionOut, out float3 Normal)
{
    PositionOut.x = clamp(PositionIn.x, TerrainBounds.x, TerrainBounds.y);
    PositionOut.z = clamp(PositionIn.z, TerrainBounds.z, TerrainBounds.w);

    float InvParcelSize = ParcelSize;
    float2 uv = (PositionOut.xz * InvParcelSize + OccupancyMap.texelSize.z * 0.5)
        * OccupancyMap.texelSize.x;

    //float occupancy = SAMPLE_TEXTURE2D_LOD(OccupancyMap, OccupancyMap.samplerstate, uv, 0.0).r;

    float2 rangeUV = float2(16.0 / 512.0, 16.0 / 512.0);
    float minValue = SAMPLE_TEXTURE2D_LOD(HeightMap, HeightMap.samplerstate, rangeUV, 0.0).r;
    float height2 = SAMPLE_TEXTURE2D_LOD(HeightMap, HeightMap.samplerstate, uv, 0.0).r;
    //float normalizedInverted = (minValue - height2) / minValue;
    // New stepped height system: 255=occupied, minValue=lowest mountain step, 0=highest peaks
    // Steps go by 10: 0, 10, 20, 30... up to minValue
    float stepSize = 10.0 / 255.0; // Step size in normalized space

    if (height2 >= minValue - stepSize)
    {
        // Flat surface (occupied parcels and above minValue threshold)
        PositionOut.y = 0.0;
        Normal = float3(0.0, 1.0, 0.0);
    }
    else // Mountain area with stepped heights
    {
        // Normalize height to 0..1 range where 1 = highest peaks (height2 = 0 a.k.a black), 0 = lowest mountain step
        float normalizedHeight = 1.0 - height2 / minValue;

        // Noise for surface detail
        float noiseH = GetHeight(PositionOut.x, PositionOut.z);

        // Smooth transition factor near the boundary with flat surface
        float smoothness = 6;
        float transitionFactor = saturate((minValue - height2) / (stepSize * smoothness));

        // Combine base height with attenuated noise
        PositionOut.y = normalizedHeight * HeightScale + noiseH * transitionFactor;
        Normal = GetNormal(PositionOut.x, PositionOut.z);

        // Ensure no negative heights
        if (PositionOut.y < 0.0)
        {
            PositionOut.y = 0.0;
            Normal = float3(0.0, 1.0, 0.0);
        }
    }
}
