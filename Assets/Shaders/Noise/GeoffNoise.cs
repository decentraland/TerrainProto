#if !GEOFFNOISE_INCLUDED
#define GEOFFNOISE_INCLUDED

#if SHADER_TARGET
#define internal
#define private
#define static
#else
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace Decentraland.Terrain
{
    internal static class GeoffNoise
    {
#endif

        // Modulo 289 without a division (only multiplications)
        private static float2 mod289(float2 x)
        {
            return x - floor(x * (1.0f / 289.0f)) * 289.0f;
        }

        // Hash function for pseudorandom values
        private static float hash(float3 p)
        {
            p = frac(p * 0.3183099f + 0.1f);
            p *= 17.0f;
            return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        // Returns 4D vector: (noise_value, derivative_x, derivative_y, derivative_z)
        private static float4 noised(float3 x)
        {
            float3 p = floor(x);
            float3 w = frac(x);

            // Quintic interpolation function and its derivative
            float3 u = w * w * w * (w * (w * 6.0f - 15.0f) + 10.0f);
            float3 du = 30.0f * w * w * (w * (w - 1.0f) + 2.0f);

            // Sample random values at 8 corners of unit cube
            float a = hash(p + float3(0.0f, 0.0f, 0.0f));
            float b = hash(p + float3(1.0f, 0.0f, 0.0f));
            float c = hash(p + float3(0.0f, 1.0f, 0.0f));
            float d = hash(p + float3(1.0f, 1.0f, 0.0f));
            float e = hash(p + float3(0.0f, 0.0f, 1.0f));
            float f = hash(p + float3(1.0f, 0.0f, 1.0f));
            float g = hash(p + float3(0.0f, 1.0f, 1.0f));
            float h = hash(p + float3(1.0f, 1.0f, 1.0f));

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
        private static float4 fbm(float3 x, int numOctaves)
        {
            mod289(float2(0.0f, 0.0f));

            float value = 0.0f;
            float3 derivative = float3(0.0f, 0.0f, 0.0f);
            float amplitude = 0.5f;
            float frequency = 1.0f;

            // Rotation matrix for each octave (reduces directional artifacts)
            float3x3 m = float3x3(
                1.6f, 1.2f, -0.6f,
                -1.2f, 1.6f, 1.2f,
                0.6f, -1.2f, 1.6f
            );

            for (int i = 0; i < numOctaves; i++)
            {
                float4 n = noised(x);
                value += amplitude * n.x;
                derivative += amplitude * n.yzw;

                amplitude *= 0.5f;
                x = mul(m, x) * 2.0f;
            }

            return float4(value, derivative);
        }

        // Enhanced terrain function with derivative modification for more interesting shapes
        private static float4 terrain(float3 pos, float frequency, int octaves)
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

        internal static float GetHeight(float x, float z)
        {
            float frequency = 0.005f;
            int octaves = 4;
            float terrainHeight = 10.0f;

            float4 terrainData = terrain(float3(x, 0.0f, z), frequency, octaves);
            return terrainData.x * terrainHeight;
        }

        internal static float3 GetNormal(float x, float z)
        {
            float frequency = 0.005f;
            int octaves = 4;

            return terrain(float3(x, 0.0f, z), frequency, octaves).yzw;
        }

#if !SHADER_TARGET
    }
}
#endif

#endif
