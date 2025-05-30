using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    using NoiseFunction = MountainsNoise;

    [CreateAssetMenu(menuName = "Decentraland/Terrain/Terrain Data")]
    public sealed class TerrainData : ScriptableObject
    {
        public int parcelSize = 16;
        public int terrainSize;
        public float maxHeight;
        public float detailDistance;
        public TreePrototype[] treePrototypes;
        public DetailPrototype[] detailPrototypes;

        public float GetHeight(float x, float z)
        {
            return NoiseFunction.GetHeight(x, z);
        }

        public float3 GetNormal(float x, float z)
        {
            return NoiseFunction.GetNormal(x, z);
        }

        public Random GetRandom(Vector2Int parcel)
        {
            int halfTerrainSize = terrainSize / 2;

            // Make sure the seed is always a positive number.
            return new Random(
                (uint)((parcel.x + halfTerrainSize) * terrainSize + parcel.y + halfTerrainSize + 1));
        }

        public void NextTree(Vector2Int parcel, ref Random random, out Vector3 position,
            out Quaternion rotation, out int prototypeIndex)
        {
            position.x = parcel.x * parcelSize + random.NextFloat(parcelSize);
            position.z = parcel.y * parcelSize + random.NextFloat(parcelSize);
            position.y = GetHeight(position.x, position.z);
            rotation = Quaternion.Euler(0f, random.NextFloat(-180f, 180f), 0f);
            prototypeIndex = random.NextInt(treePrototypes.Length);
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
        public float minWidth;
        public float maxWidth;
        public float minHeight;
        public float maxHeight;
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
