using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using static Unity.Mathematics.noise;

namespace Decentraland.Terrain
{
    [CreateAssetMenu(menuName = "Decentraland/Terrain/Mountains Noise")]
    public sealed class MountainsNoise : TerrainNoiseFunction
    {
        [SerializeField] private float scale;
        [SerializeField] private Vector2 octave0;
        [SerializeField] private Vector2 octave1;
        [SerializeField] private Vector2 octave2;
        [SerializeField] private float persistence;
        [SerializeField] private float lacunarity;
        [SerializeField] private float multiplyValue;

        public override float HeightMap(float x, float z)
        {
            MountainsNoise_float(float3(x, 0f, z), scale, octave0, octave1, octave2, persistence,
                lacunarity, multiplyValue, out float3 position);

            return position.y;
        }

        public override float RandomRange(float x, float z, float min, float max)
        {
            Unity_RandomRange_float(float2(x, z), min, max, out float value);
            return value;
        }

        private static void MountainsNoise_float(float3 PositionIn, float Scale, float2 Octave0,
            float2 Octave1, float2 Octave2, float Persistence, float Lacunarity, float MultiplyValue,
            out float3 PositionOut)
        {
            float amplitude = 1f;
            float frequency = 1f;
            float noiseHeight = 0f;

            // Octave 0
            {
                float2 sample = (PositionIn.xz + Octave0) * Scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= Persistence;
                frequency *= Lacunarity;
            }

            // Octave 1
            {
                float2 sample = (PositionIn.xz + Octave1) * Scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= Persistence;
                frequency *= Lacunarity;
            }

            // Octave 2
            {
                float2 sample = (PositionIn.xz + Octave2) * Scale * frequency;
                noiseHeight += snoise(sample) * amplitude;
                amplitude *= Persistence;
                frequency *= Lacunarity;
            }

            PositionOut = PositionIn;
            PositionOut.y += noiseHeight * MultiplyValue;
        }

        private static void Unity_RandomRange_float(float2 Seed, float Min, float Max, out float Out)
        {
            float randomno = frac(sin(dot(Seed, float2(12.9898f, 78.233f))) * 43758.5453f);
            Out = lerp(Min, Max, randomno);
        }
    }
}
