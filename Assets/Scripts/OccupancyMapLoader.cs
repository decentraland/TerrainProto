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

        [ContextMenu("Load")]
        private void Load()
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
            terrainData.Bounds = new RectInt(minParcel.x, minParcel.y, size.x, size.y);

            // Give the texture a 1 pixel border. These extra pixels shall be colored red (occupied) so
            // that terrain blends to zero at its edges.
            size += 2;

            terrainData.OccupancyMap = new Texture2D(size.x, size.y, TextureFormat.R8, false, true);
            NativeArray<byte> data = terrainData.OccupancyMap.GetRawTextureData<byte>();

            for (int i = 0; i < data.Length; i++)
                data[i] = 255;

            for (int i = 0; i < parcels.Length; i++)
            {
                int2 parcel = parcels[i];
                data[(parcel.y - minParcel.y) * size.x + parcel.x - minParcel.x] = 0;
            }

            WriteInteriorChamferOnBlack(
                terrainData.OccupancyMap,
                farIsHigh: false       // set false if your shader expects inverted values
            );

            terrainData.OccupancyMap.Apply(false, false);

            SaveTextureAsR8AssetPNG(terrainData.OccupancyMap, "Assets/Test.png");
        }
#if UNITY_EDITOR
        static void SaveTextureAsR8AssetPNG(Texture2D tex, string assetPath)
        {
            // Encode to PNG and write
            var bytes = tex.EncodeToPNG();
            System.IO.File.WriteAllBytes(assetPath, bytes);
            UnityEditor.AssetDatabase.ImportAsset(assetPath, UnityEditor.ImportAssetOptions.ForceUpdate);

            // Force importer to R8, Linear, Uncompressed
            var importer = (UnityEditor.TextureImporter)UnityEditor.AssetImporter.GetAtPath(assetPath);
            importer.sRGBTexture = false; // linear
            importer.textureCompression = UnityEditor.TextureImporterCompression.Uncompressed;
#if UNITY_2021_2_OR_NEWER
            importer.alphaSource = UnityEditor.TextureImporterAlphaSource.None;
#endif
            var plat = importer.GetDefaultPlatformTextureSettings();
            plat.overridden = true;
            plat.format = UnityEditor.TextureImporterFormat.R8;
            importer.SetPlatformTextureSettings(plat);

            importer.isReadable = true; // optional, handy if you’ll read it again
            UnityEditor.EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
#endif

     public static void WriteInteriorChamferOnBlack(Texture2D r8, bool farIsHigh = false)
    {
        int w = r8.width, h = r8.height, n = w * h;
        var src = r8.GetRawTextureData<byte>();
        if (!src.IsCreated || src.Length != n) return;

        const int INF = 1 << 28;
        const int ORTH = 3; // 3-4 chamfer (good Euclidean approx)
        const int DIAG = 4;

        // Seed distances at WHITE pixels (leave them 0), propagate into BLACK
        var dist = new int[n];
        bool anyBlack = false, anyWhite = false;
        for (int i = 0; i < n; i++)
        {
            if (src[i] == 0) { dist[i] = INF; anyBlack = true; }
            else              { dist[i] = 0;   anyWhite = true; }
        }
        if (!anyBlack || !anyWhite)
        {
            // Nothing to do if no black or no white regions exist.
            return;
        }

        // Forward pass
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int d = dist[i];
                if (d != 0) // skip white seeds
                {
                    if (x > 0)                 d = Mathf.Min(d, dist[i - 1]      + ORTH);
                    if (y > 0)                 d = Mathf.Min(d, dist[i - w]      + ORTH);
                    if (x > 0 && y > 0)        d = Mathf.Min(d, dist[i - w - 1]  + DIAG);
                    if (x + 1 < w && y > 0)    d = Mathf.Min(d, dist[i - w + 1]  + DIAG);
                    dist[i] = d;
                }
            }
        }
        // Backward pass
        for (int y = h - 1; y >= 0; y--)
        {
            int row = y * w;
            for (int x = w - 1; x >= 0; x--)
            {
                int i = row + x;
                int d = dist[i];
                if (d != 0) // skip white seeds
                {
                    if (x + 1 < w)             d = Mathf.Min(d, dist[i + 1]      + ORTH);
                    if (y + 1 < h)             d = Mathf.Min(d, dist[i + w]      + ORTH);
                    if (x + 1 < w && y + 1 < h)d = Mathf.Min(d, dist[i + w + 1]  + DIAG);
                    if (x > 0 && y + 1 < h)    d = Mathf.Min(d, dist[i + w - 1]  + DIAG);
                    dist[i] = d;
                }
            }
        }

        // Normalize using only BLACK pixels’ distances
        int maxD = 0;
        for (int i = 0; i < n; i++)
            if (src[i] == 0 && dist[i] < INF && dist[i] > maxD) maxD = dist[i];
        if (maxD == 0) { r8.Apply(false, false); return; }

        float scale = 255f / maxD;

        // Write back: keep white at 255, paint gradient inside black
        for (int i = 0; i < n; i++)
        {
            if (src[i] != 0) { src[i] = 255; continue; } // untouched white
            int q = Mathf.RoundToInt(dist[i] * scale);
            q = Mathf.Clamp(q, 0, 255);
            src[i] = (byte)(farIsHigh ? q : 255 - q);
        }

        r8.Apply(false, false);
    }

        private void GenerateDistanceField(NativeArray<byte> data, int width, int height)
        {
            const int maxDistance = 255;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (data[index] == 0) continue;

                    int minDistance = maxDistance;

                    for (int dy = -maxDistance; dy <= maxDistance && minDistance > abs(dy); dy++)
                    {
                        int checkY = y + dy;
                        if (checkY < 0 || checkY >= height) continue;

                        int maxDx = (int)sqrt(minDistance * minDistance - dy * dy);
                        for (int dx = -maxDx; dx <= maxDx; dx++)
                        {
                            int checkX = x + dx;
                            if (checkX < 0 || checkX >= width) continue;

                            int checkIndex = checkY * width + checkX;
                            if (data[checkIndex] == 0)
                            {
                                int distance = (int)sqrt(dx * dx + dy * dy);
                                if (distance < minDistance)
                                    minDistance = distance;
                            }
                        }
                    }

                    data[index] = (byte)min(minDistance, 255);
                }
            }
        }

        private struct WorldManifest
        {
            public string[] roads;
            public string[] occupied;
            public string[] empty;
        }
    }
}
