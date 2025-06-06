using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using Random = Unity.Mathematics.Random;

namespace Decentraland.Terrain
{
    using Noise = GeoffNoise;

    [CreateAssetMenu(menuName = "Decentraland/Terrain/Terrain Data")]
    public sealed class TerrainData : ScriptableObject
    {
        public int parcelSize = 16;
        public int terrainSize;
        public float maxHeight;
        public int seed = 1;
        [Range(0f, 1f)] public float treeDensity;
        public TreePrototype[] treePrototypes;
        public DetailPrototype[] detailPrototypes;

        private void OnValidate()
        {
            if (seed < 1)
                seed = 1;
        }

        public static void LoadOccupancyMap(out Texture2D texture, out RectInt textureRect)
        {
            var worldManifestAsset = Resources.Load<TextAsset>("WorldManifest");
            var worldManifest = JsonUtility.FromJson<WorldManifest>(worldManifestAsset.text);

            var parcels = new int2[worldManifest.empty.Length];
            int2 parcelMin = int2(int.MaxValue, int.MaxValue);
            int2 parcelMax = int2(int.MinValue, int.MinValue);

            for (int i = 0; i < worldManifest.empty.Length; i++)
            {
                string[] parcelStr = worldManifest.empty[i].Split(',');
                int2 parcel = int2(int.Parse(parcelStr[0]), int.Parse(parcelStr[1]));
                parcels[i] = parcel;
                parcelMin = min(parcelMin, parcel);
                parcelMax = max(parcelMax, parcel);
            }

            textureRect = new(parcelMin.x - 1, parcelMin.y - 1, parcelMax.x - parcelMin.x + 3,
                parcelMax.y - parcelMin.y + 3);

            texture = new Texture2D(textureRect.width, textureRect.height, TextureFormat.R8, false,
                true);

            texture.wrapMode = TextureWrapMode.Clamp;
            NativeArray<byte> data = texture.GetRawTextureData<byte>();

            // Add a black border so that the terrain height blends to zero at its edges.
            {
                int i = 0;

                while (i < textureRect.width)
                    data[i++] = 0;

                int startOfLastRow = textureRect.width * (textureRect.height - 1);
                int terrainWidth = textureRect.width - 2;

                while (i < startOfLastRow)
                {
                    data[i++] = 0;

                    for (int j = 0; j < terrainWidth; j++)
                        data[i++] = 255;

                    data[i++] = 0;
                }

                while (i < data.Length)
                    data[i++] = 0;
            }

            for (int i = 0; i < parcels.Length; i++)
            {
                int2 parcel = parcels[i];
                data[(parcel.y - textureRect.y) * textureRect.width + parcel.x - textureRect.x] = 0;
            }

            texture.Apply(false, false);
        }

        public TerrainDataData GetData()
        {
            return new TerrainDataData(parcelSize, terrainSize, maxHeight, seed, treeDensity,
                treePrototypes.Length);
        }

        private struct WorldManifest
        {
            public string[] empty;
        }
    }

    public readonly struct TerrainDataData
    {
        public readonly int parcelSize;
        public readonly int terrainSize;
        public readonly float maxHeight;
        public readonly int seed;
        public readonly float treeDensity;
        public readonly int treePrototypeCount;

        public TerrainDataData(int parcelSize, int terrainSize, float maxHeight, int seed,
            float treeDensity, int treePrototypeCount)
        {
            this.parcelSize = parcelSize;
            this.terrainSize = terrainSize;
            this.maxHeight = maxHeight;
            this.seed = seed;
            this.treeDensity = treeDensity;
            this.treePrototypeCount = treePrototypeCount;
        }

        public float GetHeight(float x, float z) =>
            Noise.GetHeight(x, z);

        public float3 GetNormal(float x, float z) =>
            Noise.GetNormal(x, z);

        public Random GetRandom(int2 parcel)
        {
            int terrainHalfSize = terrainSize / 2;

            return new Random(Noise.lowbias32((uint)(
                (parcel.y + terrainHalfSize) * terrainSize + parcel.x + terrainHalfSize + seed)));
        }

        public bool NextTree(int2 parcel, ref Random random, out float3 position, out float rotationY,
            out int prototypeIndex)
        {
            if (random.NextFloat() < treeDensity)
            {
                position.x = parcel.x * parcelSize + random.NextFloat(parcelSize);
                position.z = parcel.y * parcelSize + random.NextFloat(parcelSize);
                position.y = Noise.GetHeight(position.x, position.z);
                rotationY = random.NextFloat(-180f, 180f);
                prototypeIndex = random.NextInt(treePrototypeCount);
                return true;
            }
            else
            {
                position = default;
                rotationY = 0f;
                prototypeIndex = -1;
                return false;
            }
        }

        public RectInt PositionToParcelRect(Vector3 center, float radius)
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

    public enum DetailScatterMode
    {
        JitteredGrid
    }

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
