#ifndef MOUNTAINSMOISE_INCLUDED
#define MOUNTAINSMOISE_INCLUDED

#include "Noise/noise2D.cginc"

void MountainsNoise_float(float2 position, float scale, float2 octave0, float2 octave1, float2 octave2,
    float maxHeight, float persistence, float lacunarity, float2 offset, float cutoff, float baseValue,
    float multiplyValue, float divideValue, out float value)
{
    float halfWidth = 0.5;
    float halfHeight = 0.5;

    float amplitude = 1.0;
    float frequency = 1.0;
    float noiseHeight = 0.0;

    // Octave 0
    {
        float sampleX = (position.x - halfWidth + octave0.x + offset.x) / scale * frequency;
        float sampleY = (position.y - halfHeight + octave0.y + offset.y) / scale * frequency;

        float noiseValue = snoise(float2(sampleX, sampleY));

        noiseValue = (noiseValue * 2.0) - 1.0;
        noiseHeight += noiseValue * amplitude;

        amplitude *= persistence;
        frequency *= lacunarity;
    }

    // Octave 1
    {
        float sampleX = (position.x - halfWidth + octave1.x + offset.x) / scale * frequency;
        float sampleY = (position.y - halfHeight + octave1.y + offset.y) / scale * frequency;

        float noiseValue = snoise(float2(sampleX, sampleY));

        noiseValue = (noiseValue * 2.0) - 1.0;
        noiseHeight += noiseValue * amplitude;

        amplitude *= persistence;
        frequency *= lacunarity;
    }

    // Octave 2
    {
        float sampleX = (position.x - halfWidth + octave2.x + offset.x) / scale * frequency;
        float sampleY = (position.y  - halfHeight + octave2.y + offset.y) / scale * frequency;

        float noiseValue = snoise(float2(sampleX, sampleY));

        noiseValue = (noiseValue * 2.0) - 1.0;
        noiseHeight += noiseValue * amplitude;

        amplitude *= persistence;
        frequency *= lacunarity;
    }

    float tempValue = noiseHeight;

    tempValue += baseValue;
    tempValue *= max(multiplyValue, 1.0);
    tempValue /= max(divideValue, 1.0);

    float normalizedHeight = (tempValue + 1.0) / maxHeight;
    tempValue = clamp(normalizedHeight, 0.0, 1.0);

    if (tempValue < cutoff)
        tempValue = 0.0;

    value = tempValue;
}

#endif
