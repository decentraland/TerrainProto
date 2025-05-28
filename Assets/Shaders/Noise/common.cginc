#ifndef NOISE_COMMON_INCLUDED
#define NOISE_COMMON_INCLUDED

// Copy-pasted from com.unity.mathematics/Unity.Mathematics/Noise/common.cs

// Modulo 289 without a division (only multiplications)
float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float2 mod289(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }

// Modulo 7 without a division
float3 mod7(float3 x) { return x - floor(x * (1.0 / 7.0)) * 7.0; }

// Permutation polynomial: (34x^2 + x) math.mod 289
float3 permute(float3 x) { return mod289((34.0 * x + 1.0) * x); }

#endif