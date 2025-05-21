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

#endif
