#ifndef MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
#define MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Noise/GeoffNoise.cs"

float3 TerrainVertexAdjustment(float3 positionWS, out float3 heightDerivative)
{
    // Get terrain height and derivatives at this position
    float4 terrainData = terrain(positionWS, _frequency, _octaves);
    float height = terrainData.x * _terrainHeight;
    heightDerivative = terrainData.yzw * _terrainHeight;

    // Modify vertex position with terrain height
    positionWS *= _terrainScale;
    positionWS.y += height;

    return positionWS;
}

VertexPositionInputs GetVertexPositionInputs_Mountain(float3 positionOS, out float3 heightDerivative)
{
    VertexPositionInputs input;
    input.positionWS = TransformObjectToWorld(positionOS);
    input.positionWS = TerrainVertexAdjustment(input.positionWS, heightDerivative);
    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;

    return input;
}

#endif // MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
