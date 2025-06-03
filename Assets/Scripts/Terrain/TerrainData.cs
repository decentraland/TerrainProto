using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    using NoiseFunction = GeoffNoise;

    [CreateAssetMenu(menuName = "Decentraland/Terrain/Terrain Data")]
    public sealed class TerrainData : ScriptableObject
    {
        public int parcelSize = 16;
        public int terrainSize;
        public float maxHeight;
        public int seed = 1;
        public float detailDistance;
        public TreePrototype[] treePrototypes;
        public DetailPrototype[] detailPrototypes;

        private void OnValidate()
        {
            if (seed < 1)
                seed = 1;
        }

        public static float GetHeight(float x, float z)
        {
            return NoiseFunction.GetHeight(x, z);
        }

        public float3 GetNormal(float x, float z)
        {
            return NoiseFunction.GetNormal(x, z);
        }

        public Random GetRandom(Vector2Int parcel)
        {
            return GetRandom(int2(parcel.x, parcel.y), terrainSize, seed);
        }

        public static Random GetRandom(int2 parcel, int terrainSize, int seed)
        {
            static uint lowbias32(uint x)
            {
                x ^= x >> 16;
                x *= 0x7feb352du;
                x ^= x >> 15;
                x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }

            int halfTerrainSize = terrainSize / 2;
            return new Random(lowbias32((uint)(
                (parcel.y + halfTerrainSize) * terrainSize + parcel.x + halfTerrainSize + seed)));
        }

        public static void NextTree(int2 parcel, int parcelSize, int treePrototypeCount,
            ref Random random, out float3 position, out float rotationY, out int prototypeIndex)
        {
            position.x = parcel.x * parcelSize + random.NextFloat(parcelSize);
            position.z = parcel.y * parcelSize + random.NextFloat(parcelSize);
            position.y = GetHeight(position.x, position.z);
            rotationY = random.NextFloat(-180f, 180f);
            prototypeIndex = random.NextInt(treePrototypeCount);
        }

        public Vector2Int WorldPositionToParcel(Vector3 value)
        {
            return new Vector2Int(
                Mathf.RoundToInt(value.x / parcelSize - 0.5f),
                Mathf.RoundToInt(value.z / parcelSize - 0.5f));
        }

        public RectInt WorldPositionToParcelRect(Vector3 center, float radius)
        {
            float invParcelSize = 1f / parcelSize;

            Vector2Int min = new Vector2Int(
                Mathf.RoundToInt((center.x - radius) * invParcelSize - 0.5f),
                Mathf.RoundToInt((center.z - radius) * invParcelSize - 0.5f));

            Vector2Int max = new Vector2Int(
                Mathf.RoundToInt((center.x + radius) * invParcelSize - 0.5f),
                Mathf.RoundToInt((center.z + radius) * invParcelSize - 0.5f));

            return new RectInt(min.x, min.y, max.x - min.x + 1, max.y - min.y + 1);
        }
    }

    [Serializable]
    public struct DetailPrototype
    {
        public GameObject source;
        public DetailScatterMode scatterMode;
        public int density;
        public float alignToGround;
        public float minScaleXZ;
        public float maxScaleXZ;
        public float minScaleY;
        public float maxScaleY;
        public Mesh mesh;
        public Material material;
    }

    public enum DetailScatterMode { JitteredGrid }

    [Serializable]
    public struct TreeLOD
    {
        public float minScreenSize;
        public Mesh mesh;
        public Material[] materials;
    }

    [Serializable]
    public struct TreePrototype
    {
        public GameObject source;
        public GameObject collider;
        public float localSize;
        public TreeLOD[] lods;
    }
}
