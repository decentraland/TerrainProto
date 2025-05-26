#ifndef MOUNTAINSMOISE_INCLUDED
#define MOUNTAINSMOISE_INCLUDED

#include "Noise/noise2D.cginc"

void MountainsNoise_float(float3 positionIn, float scale, float2 octave0, float2 octave1, float2 octave2,
    float persistence, float lacunarity, float multiplyValue, out float3 positionOut, out float3 normal)
{
    float amplitude = 1.0;
    float frequency = 1.0;
    float noiseHeight = 0.0;

    // Octave 0
    {
        float2 sample = (positionIn.xz + octave0) * scale * frequency;
        noiseHeight += snoise(sample) * amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }

    // Octave 1
    {
        float2 sample = (positionIn.xz + octave1) * scale * frequency;
        noiseHeight += snoise(sample) * amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }

    // Octave 2
    {
        float2 sample = (positionIn.xz + octave2) * scale * frequency;
        noiseHeight += snoise(sample) * amplitude;
        amplitude *= persistence;
        frequency *= lacunarity;
    }

    positionOut = positionIn;
    positionOut.y += noiseHeight * multiplyValue;

    // TODO: Generate noise with derivatives, and use derivatives to generate normals.
    normal = float3(0.0, 1.0, 0.0);
}

void TerrainNormalDerivatives_float(float3 worldPos, float _NormalScale, out float4 oTangentNormal)
{
    float3 dFdxPos = ddx(worldPos.xyz);
    float3 dFdyPos = ddy(worldPos.xyz);

    // Calculate world normal
    float3 worldNormal = normalize(cross(dFdyPos, dFdxPos));

    // Construct tangent space basis from screen-space derivatives
    // dFdx gives us one tangent direction, dFdy gives us another
    float3 tangent = normalize(dFdxPos);
    float3 bitangent = normalize(dFdyPos);

    // Ensure orthogonality using Gram-Schmidt process
    bitangent = normalize(bitangent - dot(bitangent, tangent) * tangent);

    // Create world-to-tangent matrix
    float3x3 worldToTangent = float3x3(
        tangent.x, bitangent.x, worldNormal.x,
        tangent.y, bitangent.y, worldNormal.y,
        tangent.z, bitangent.z, worldNormal.z
    );

    // Transform world normal to tangent space
    float3 tangentNormal = mul(worldToTangent, worldNormal);
    tangentNormal = normalize(tangentNormal) * _NormalScale;

    // Convert to [0,1] range for output
    float3 normalOutput = tangentNormal * 0.5 + 0.5;

    oTangentNormal = float4(normalOutput, 1.0);
}

#endif
