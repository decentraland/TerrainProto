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

        // Hash function for pseudorandom values - improved version from Quilez
        private static float hash1(float n)
        {
            return frac(n * 17.0f * frac(n * 0.3183099f));
        }

        private static float hash1(float3 p)
        {
            float n = p.x + 317.0f * p.y + 157.0f * p.z;
            return hash1(n);
        }

        // Returns 4D vector: (noise_value, derivative_x, derivative_y, derivative_z)
        // This matches Quilez's exact implementation, converted to HLSL
        private static float4 noised(float3 x)
        {
            float3 p = floor(x);
            float3 w = frac(x);

            // Quintic interpolation function and its derivative
            float3 u = w * w * w * (w * (w * 6.0f - 15.0f) + 10.0f);
            float3 du = 30.0f * w * w * (w * (w - 2.0f) + 1.0f);

            // Sample random values at 8 corners using Quilez's method
            float n = p.x + 317.0f * p.y + 157.0f * p.z;

            float a = hash1(n + 0.0f);
            float b = hash1(n + 1.0f);
            float c = hash1(n + 317.0f);
            float d = hash1(n + 318.0f);
            float e = hash1(n + 157.0f);
            float f = hash1(n + 158.0f);
            float g = hash1(n + 474.0f);
            float h = hash1(n + 475.0f);

            // Compute interpolation coefficients (exactly as in Quilez code)
            float k0 = a;
            float k1 = b - a;
            float k2 = c - a;
            float k3 = e - a;
            float k4 = a - b - c + d;
            float k5 = a - c - e + g;
            float k6 = a - b - e + f;
            float k7 = -a + b + c - d + e - f - g + h;

            // Compute noise value (remapped to [-1,1] range)
            float noiseValue = k0 + k1 * u.x + k2 * u.y + k3 * u.z +
                               k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x +
                               k7 * u.x * u.y * u.z;
            noiseValue = -1.0f + 2.0f * noiseValue;  // Remap [0,1] to [-1,1]

            // Compute analytical derivatives (exactly as in Quilez code)
            float3 derivative = 2.0f * du * float3(
                k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
                k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
                k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y
            );

            return float4(noiseValue, derivative);
        }

        // Fractal Brownian Motion with analytical derivatives - HLSL version of Quilez's fbmd_7
        private static float4 fbm(float3 x, int numOctaves)
        {
            float f = 1.92f;  // Frequency multiplier per octave
            float s = 0.5f;   // Amplitude multiplier per octave
            float a = 0.0f;   // Accumulated value
            float b = 0.5f;   // Current amplitude
            float3 d = float3(0, 0, 0);  // Accumulated derivatives

            // Transform matrix per octave (starts as identity)
            float3x3 m = float3x3(1.0f, 0.0f, 0.0f,
                                  0.0f, 1.0f, 0.0f,
                                  0.0f, 0.0f, 1.0f);

            // Rotation matrices for octaves (from Quilez code)
            float3x3 m3 = float3x3(  0.00f,  0.80f,  0.60f,
                                    -0.80f,  0.36f, -0.48f,
                                    -0.60f, -0.48f,  0.64f);

            float3x3 m3i = float3x3(  0.00f, -0.80f, -0.60f,
                                      0.80f,  0.36f, -0.48f,
                                      0.60f, -0.48f,  0.64f);

            // Clamp octaves to prevent infinite loops
            numOctaves = min(numOctaves, 8);

            for (int i = 0; i < numOctaves; i++)
            {
                float4 n = noised(x);

                a += b * n.x;                    // Accumulate values
                d += b * mul(m, n.yzw);         // Accumulate derivatives with proper transform

                b *= s;                         // Reduce amplitude
                x = mul(m3, x) * f;            // Transform coordinates for next octave
                m = mul(m3i, m) * f;           // Update derivative transform matrix
            }

            return float4(a, d);
        }

        // Enhanced terrain function matching Quilez's approach
        private static float4 terrain(float3 pos, float frequency, int octaves)
        {
            // Apply frequency scaling - this becomes our "p/2000.0" equivalent
            float3 p = pos * frequency;

            // Get FBM noise and derivatives
            float4 noise = fbm(p, octaves);
            float height = noise.x;
            float3 derivative = noise.yzw;

            // Scale derivatives properly: if we sample at p*frequency,
            // derivatives w.r.t. original pos are derivative*frequency
            derivative *= frequency;

            // Optional: Simple erosion-like effect (much simpler than original)
            // This creates varied terrain but keeps it stable
            // float erosion = 1.0 / (1.0 + 0.05 * dot(derivative, derivative));
            // height *= erosion;
            // derivative *= erosion;

            return float4(height, derivative);
        }

        ///////////////////////////
        ///////////////////////////
        ///////////////////////////

        // Optimised hash function: 3 ALU
        private static float hash_optimised(float2 p)
        {
            return frac(sin(dot(p, float2(127.1f, 311.7f))) * 43758.5453f);
        }

        // Optimised 2D noise: ~25 ALU
        private static float noise2d_optimised(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);

            float a = hash_optimised(i);
            float b = hash_optimised(i + float2(1.0f,0.0f));
            float c = hash_optimised(i + float2(0.0f,1.0f));
            float d = hash_optimised(i + float2(1.0f,1.0f));

            float2 u = f * f * (3.0f - 2.0f * f); // smoothstep
            return lerp(lerp(a,b,u.x), lerp(c,d,u.x), u.y);
        }

        // Optimised terrain with 2 octaves: ~60 ALU total
        private static float terrain_optimised(float2 pos, float frequency)
        {
            float2 p = pos * frequency;
            float height = 0.0f;
            float amp = 0.5f;
            float freq = 1.0f;

            // Octave 1
            height += noise2d_optimised(p * freq) * amp;
            amp *= 0.5f; freq *= 2.0f;

            // Octave 2
            height += noise2d_optimised(p * freq) * amp;

            return height;
        }

        // Fast normal calculation using finite differences: ~190 ALU
        private static float3 getNormal_optimised(float2 worldPos, float frequency, int quality)
        {
            float eps = 0.1f / frequency; // Scale epsilon with frequency

            float h_center = terrain_optimised(worldPos, frequency);
            float h_right = terrain_optimised(worldPos + float2(eps, 0.0f), frequency);
            float h_up = terrain_optimised(worldPos + float2(0.0f, eps), frequency);

            float dhdx = (h_right - h_center) / eps;
            float dhdy = (h_up - h_center) / eps;

            return normalize(float3(-dhdx, 1.0f, -dhdy));
        }

        // Optimised version - 60 ALU
        private static float getHeight_optimised(float2 worldPos, float frequency)
        {
            return terrain_optimised(worldPos, frequency);
        }

        // With normals - varies by quality
        private static float4 getHeightAndNormal(float2 worldPos, float frequency, int quality)
        {
            return float4(getHeight_optimised(worldPos, frequency), getNormal_optimised(worldPos, frequency, 0));
        }

        internal static float GetHeight(float x, float z)
        {
            float frequencyCS = 0.15f;
            //int octaves = 5;
            float terrainHeight = 4.0f;

            float terrainData = getHeight_optimised(float2(x, z), frequencyCS);
            //float4 terrainData = terrain(float3(x, 0.0f, z), frequency, octaves);
            return terrainData * terrainHeight;
        }

        internal static float3 GetNormal(float x, float z)
        {
            float frequencyCS = 0.15f;
            //int octaves = 5;

            return getNormal_optimised(float2(x, z), frequencyCS, 0);
            //return terrain(float3(x, 0.0f, z), frequency, octaves).yzw;
        }

#if !SHADER_TARGET
    }
}
#endif

#endif
