#if !MOUNTAINSMOISE_INCLUDED
#define MOUNTAINSMOISE_INCLUDED

#if SHADER_TARGET
#define internal
#define private
#define static
#else
using Unity.Mathematics;
using static Unity.Mathematics.math;
using float2 = Unity.Mathematics.float2;

namespace Decentraland.Terrain
{
    internal static class ConstantNoise
    {
#endif

        /// <summary>
        /// lowbias32 updated with the latest best constants. See
        /// https://nullprogram.com/blog/2018/07/31/ and
        /// https://github.com/skeeto/hash-prospector/issues/19
        /// </summary>
        internal static uint lowbias32(uint x)
        {
            x ^= x >> 16;
            x *= 0x21f0aaad;
            x ^= x >> 15;
            x *= 0xd35a2d97;
            x ^= x >> 15;
            return x;
        }

        internal static float GetHeight(float x, float z)
        {
            return 4.0f;
        }

        internal static float3 GetNormal(float x, float z)
        {
            return float3(0.0f, 1.0f, 0.0f);
        }

#if !SHADER_TARGET
    }
}
#endif

#endif
