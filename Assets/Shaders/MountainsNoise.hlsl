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

// Hash function for pseudorandom values
float hash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Returns 4D vector: (noise_value, derivative_x, derivative_y, derivative_z)
float4 noised(float3 x)
{
    float3 p = floor(x);
    float3 w = frac(x);

    // Quintic interpolation function and its derivative
    float3 u = w * w * w * (w * (w * 6.0 - 15.0) + 10.0);
    float3 du = 30.0 * w * w * (w * (w - 1.0) + 2.0);

    // Sample random values at 8 corners of unit cube
    float a = hash(p + float3(0, 0, 0));
    float b = hash(p + float3(1, 0, 0));
    float c = hash(p + float3(0, 1, 0));
    float d = hash(p + float3(1, 1, 0));
    float e = hash(p + float3(0, 0, 1));
    float f = hash(p + float3(1, 0, 1));
    float g = hash(p + float3(0, 1, 1));
    float h = hash(p + float3(1, 1, 1));

    // Compute interpolation coefficients
    float k0 = a;
    float k1 = b - a;
    float k2 = c - a;
    float k3 = e - a;
    float k4 = a - b - c + d;
    float k5 = a - c - e + g;
    float k6 = a - b - e + f;
    float k7 = -a + b + c - d + e - f - g + h;

    // Compute noise value
    float noiseValue = k0 + k1 * u.x + k2 * u.y + k3 * u.z +
                       k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x +
                       k7 * u.x * u.y * u.z;

    // Compute analytical derivatives
    float3 derivative = du * float3(
        k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
        k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
        k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y
    );

    return float4(noiseValue, derivative);
}

// Fractal Brownian Motion with analytical derivatives
float4 fbm(float3 x, int numOctaves)
{
    float value = 0.0;
    float3 derivative = float3(0, 0, 0);
    float amplitude = 0.5;
    float frequency = 1.0;

    // Rotation matrix for each octave (reduces directional artifacts)
    float3x3 m = float3x3(
        1.6,  1.2, -0.6,
        -1.2, 1.6,  1.2,
        0.6, -1.2,  1.6
    );

    for (int i = 0; i < numOctaves; i++)
    {
        float4 n = noised(x);
        value += amplitude * n.x;
        derivative += amplitude * n.yzw;

        amplitude *= 0.5;
        x = mul(m, x) * 2.0;
    }

    return float4(value, derivative);
}

// Enhanced terrain function with derivative modification for more interesting shapes
float4 terrain(float3 pos, float frequency, int octaves)
{
    float3 p = pos * frequency;

    float4 noise = fbm(p, octaves);
    float height = noise.x;
    float3 derivative = noise.yzw;

    // // Optional: Modify derivatives for erosion-like effects
    // // This creates more varied terrain with both smooth and rough areas
    // float erosion = 1.0 / (1.0 + dot(derivative, derivative) * 0.1);
    // height *= erosion;
    // derivative *= erosion;

    return float4(height, derivative);
}

#endif
