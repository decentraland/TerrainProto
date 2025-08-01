﻿#ifndef MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
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
    fOccupancy = GetOccupancy(heightUV, terrainBounds, ParcelSize);

    const float TERRAIN_MIN = -0.9960938;
    const float TERRAIN_MAX = 0.8615339;
    const float TERRAIN_RANGE = 1.857628; // Pre-calculated

    float heightDerivative2 = SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, heightUV, 0).x;

    // // In the "worst case", if occupancy is 0.25, it can mean that the current vertex is on a corner
    // // between one occupied parcel and three free ones, and height must be zero.
    if (fOccupancy < 0.25f)
    {
        heightDerivative.x = heightDerivative2;
        input.positionWS.y += lerp(heightDerivative.x * _terrainHeight, 0.0, fOccupancy * 4.0);
    }
    else
    {
        input.positionWS.y = 0.0;
    }

    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;

    return input;
}

#endif // MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
