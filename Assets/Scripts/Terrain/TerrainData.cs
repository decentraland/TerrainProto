using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Decentraland.Terrain
{
    [CreateAssetMenu(menuName = "Decentraland/Terrain/Terrain Data")]
    public sealed class TerrainData : ScriptableObject
    {
		public TerrainNoiseFunction noiseFunction;
        public int parcelSize = 16;
        public int terrainSize;
        public float maxHeight;
        public float detailDistance;
        public TreePrototype[] treePrototypes;
        public DetailPrototype[] detailPrototypes;

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
    }

    public enum DetailScatterMode { JitteredGrid }

    [Serializable]
    public struct TreePrototype
    {
        public GameObject source;
        public GameObject collider;
    }
}
