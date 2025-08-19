#ifndef MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
#define MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Noise/GeoffNoise.cs"
//#include "PerlinNoise.hlsl"

VertexPositionInputs GetVertexPositionInputs_Mountain(float3 positionOS, float4 terrainBounds, out float fOccupancy, out float4 heightDerivative)
{
    heightDerivative = float4(0.0f, 0.0f, 0.0f, 0.0f);
    VertexPositionInputs input;
    input.positionWS = TransformObjectToWorld(positionOS);
    input.positionWS = ClampPosition(input.positionWS, terrainBounds);

    const int ParcelSize = 16;
    float2 heightUV = (input.positionWS.xz + 4096.0f) / 8192.0f;

    // Sample from the new distance field map instead of occupancy map
    float height2 = SAMPLE_TEXTURE2D_LOD(_DistanceFieldMap, sampler_DistanceFieldMap, heightUV, 0).r;
    fOccupancy = height2; // Pass the distance field value for compatibility

    // Get minValue from corner of the texture (as in MountainsNoise.hlsl)
    float2 rangeUV = float2(16.0 / 512.0, 16.0 / 512.0);
    float minValue = SAMPLE_TEXTURE2D_LOD(_DistanceFieldMap, sampler_DistanceFieldMap, rangeUV, 0.0).r;
    float heightDerivative2 = SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, heightUV, 0).x;

    float stepSize = 10.0 / 255.0; // Step size in normalized space
    float threshold = minValue - 3*stepSize;
    // New stepped height system: 255=occupied, minValue=lowest mountain step, 0=highest peaks
    // Steps go by 10: 0, 10, 20, 30... up to minValue
    if (height2 >= threshold)
    {
        // Flat surface (occupied parcels and above minValue threshold)
        input.positionWS.y = 0.0;
    }
    else
    {
        // Normalize to 0..1 range where 0=highest peaks, 1=lowest mountain step
        float normalizedHeight = 1.0 - height2 / threshold;

        // Base height from stepped system
        heightDerivative.x = heightDerivative2;

        // Noise for surface detail (scaled by terrain scale)
        // float noiseH = GetHeight(input.positionWS.x * _terrainScale, input.positionWS.z * _terrainScale);

        // Smooth transition factor near the boundary with flat surface
        float smoothness = 2.0;
        float transitionFactor = saturate((threshold - height2) / (stepSize * smoothness));

        // Combine base height with attenuated noise (add to existing position)
        input.positionWS.y += normalizedHeight * _DistanceFieldScale;// + noiseH * transitionFactor;


        // Ensure no negative heights
        if (input.positionWS.y < 0.0)
        {
            input.positionWS.y = 0.0;
        }
    }

    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;

    return input;
}

#endif // MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
