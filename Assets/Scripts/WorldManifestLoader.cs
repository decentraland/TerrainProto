using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using TerrainData = Decentraland.Terrain.TerrainData;

namespace TerrainProto
{
    public class OccupancyMapLoader : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;

        private void Awake()
        {
            var worldManifestAsset = Resources.Load<TextAsset>("WorldManifest");
            var worldManifest = JsonUtility.FromJson<WorldManifest>(worldManifestAsset.text);

            var parcels = new int2[worldManifest.empty.Length];
            int2 minParcel = int2(int.MaxValue, int.MaxValue);
            int2 maxParcel = int2(int.MinValue, int.MinValue);

            static void GetParcels(string[] parcelStrs, ref int2 minParcel, ref int2 maxParcel,
                int2[] parcels)
            {
                for (int i = 0; i < parcelStrs.Length; i++)
                {
                    string[] parcelStr = parcelStrs[i].Split(',');
                    int2 parcel = int2(int.Parse(parcelStr[0]), int.Parse(parcelStr[1]));
                    minParcel = min(minParcel, parcel);
                    maxParcel = max(maxParcel, parcel);

                    if (parcels != null)
                        parcels[i] = parcel;
                }
            }

            GetParcels(worldManifest.roads, ref minParcel, ref maxParcel, null);
            GetParcels(worldManifest.occupied, ref minParcel, ref maxParcel, null);
            GetParcels(worldManifest.empty, ref minParcel, ref maxParcel, parcels);

            int2 size = maxParcel - minParcel + 1;
            terrainData.bounds = new RectInt(minParcel.x, minParcel.y, size.x, size.y);

            // Give the texture a 1 pixel border. These extra pixels shall be colored red (occupied) so
            // that terrain blends to zero at its edges.
            size += 2;

            terrainData.occupancyMap = new Texture2D(size.x, size.y, TextureFormat.R8, false, true);
            NativeArray<byte> data = terrainData.occupancyMap.GetRawTextureData<byte>();

            for (int i = 0; i < data.Length; i++)
                data[i] = 255;

            for (int i = 0; i < parcels.Length; i++)
            {
                int2 parcel = parcels[i];
                data[(parcel.y - minParcel.y) * size.x + parcel.x - minParcel.x] = 0;
            }

            terrainData.occupancyMap.Apply(false, false);
        }

        private struct WorldManifest
        {
            public string[] roads;
            public string[] occupied;
            public string[] empty;
        }
    }
}
