#ifndef MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
#define MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Noise/GeoffNoise.cs"
//#include "PerlinNoise.hlsl"

float3 TerrainVertexAdjustment(float3 positionWS, out float4 heightDerivative)
{
    // // Get terrain height and derivatives at this position
    // float4 terrainData = terrain(positionWS, _frequency, _octaves);
    // float height = terrainData.x * _terrainHeight;
    // heightDerivative = float4(terrainData.yzw * _terrainHeight, height);
    //
    // // Modify vertex position with terrain height
    // positionWS *= _terrainScale;
    // positionWS.y += height;

    heightDerivative = float4(0,0,0,0);
    return positionWS;
}

VertexPositionInputs GetVertexPositionInputs_Mountain(float3 positionOS, float4 terrainBounds,
    out float4 heightDerivative)
{
    VertexPositionInputs input;
    input.positionWS = TransformObjectToWorld(positionOS);
    input.positionWS.x = clamp(input.positionWS.x, terrainBounds.x, terrainBounds.y);
    input.positionWS.z = clamp(input.positionWS.z, terrainBounds.z, terrainBounds.w);
    heightDerivative = getHeightAndNormal_int(input.positionWS.xz, _frequency, 0);
    input.positionWS.y += heightDerivative.x * _terrainHeight;
    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;

    return input;
}

#endif // MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
