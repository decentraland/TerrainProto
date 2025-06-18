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

VertexPositionInputs GetVertexPositionInputs_Mountain(float3 positionOS, float4 terrainBounds, out float4 heightDerivative)
{
    VertexPositionInputs input;
    input.positionWS = TransformObjectToWorld(positionOS);

    if (_UseHeightMap > 0)
    {
        const float TERRAIN_MIN = -0.9960938;
        const float TERRAIN_MAX = 0.8615339;
        const float TERRAIN_RANGE = 1.857628; // Pre-calculated

        float2 heightUV = (input.positionWS.xz + 4096.0f) / 8192.0f;
        float heightDerivative2 = SAMPLE_TEXTURE2D_LOD(_HeightMap, sampler_HeightMap, heightUV, 0).x;

        if (false) // For Accuracy Testing
        {
            heightDerivative2 = (heightDerivative2 - TERRAIN_MIN) / TERRAIN_RANGE;
            heightDerivative2 = saturate(heightDerivative2);
        }

        heightDerivative.x = heightDerivative2;
        input.positionWS.y += heightDerivative.x * _terrainHeight;

        if (false) // For Accuracy Testing
        {
            float heightDerivative1 = terrain(float3(input.positionWS.x, 0.0f, input.positionWS.z), _frequency, _octaves).x;
            //heightDerivative1 = (((input.positionWS.x / 4096.0f) * 0.5f) + 0.5f) * (((input.positionWS.z / 4096.0f) * 0.5f) + 0.5f);

            float normalizedHeight = (heightDerivative1 - TERRAIN_MIN) / TERRAIN_RANGE;
            normalizedHeight = saturate(normalizedHeight);

            heightDerivative.x = abs(normalizedHeight - heightDerivative.x);
            // if(heightDerivative < 0.01f)
            //     heightDerivative = 0.0f;
        }
    }
    else
    {
        input.positionWS.x = clamp(input.positionWS.x, terrainBounds.x, terrainBounds.y);
        input.positionWS.z = clamp(input.positionWS.z, terrainBounds.z, terrainBounds.w);
        heightDerivative = getHeightAndNormal_int(input.positionWS.xz, _frequency, 0);
        input.positionWS.y += heightDerivative.x * _terrainHeight;
    }

    input.positionVS = TransformWorldToView(input.positionWS);
    input.positionCS = TransformWorldToHClip(input.positionWS);

    float4 ndc = input.positionCS * 0.5f;
    input.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
    input.positionNDC.zw = input.positionCS.zw;

    return input;
}

#endif // MOUNTAIN_LIT_VERTEX_FUNCTIONS_INCLUDED
