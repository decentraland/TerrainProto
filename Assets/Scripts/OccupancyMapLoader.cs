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

            // Initialize all as occupied (255)
            for (int i = 0; i < data.Length; i++)
                data[i] = 255;

            // Mark empty parcels as unoccupied (0)
            for (int i = 0; i < parcels.Length; i++)
            {
                int2 parcel = parcels[i];
                data[(parcel.y - minParcel.y + 1) * size.x + (parcel.x - minParcel.x + 1)] = 0;
            }

            // Mark the 1-pixel border as unoccupied (0) so it participates in distance field
            // But leave everything outside as occupied (255) to affect distance calculation
            // Top and bottom borders
            for (int x = 0; x < size.x; x++)
            {
                data[0 * size.x + x] = 0; // Top border
                data[(size.y - 1) * size.x + x] = 0; // Bottom border
            }
            // Left and right borders
            for (int y = 0; y < size.y; y++)
            {
                data[y * size.x + 0] = 0; // Left border
                data[y * size.x + (size.x - 1)] = 0; // Right border
            }

            terrainData.OccupancyMap.Apply(false, false);
            SaveTextureAsR8AssetPNG(terrainData.OccupancyMap, "Assets/Test.png");
        }
#if UNITY_EDITOR
        static void SaveTextureAsR8AssetPNG(Texture2D tex, string assetPath)
        {
            // Extend texture to 512x512 power-of-2 where pixel (264,261) corresponds to parcel (0,0)
            const int targetSize = 512;
            const int centerPixelX = 263;
            const int centerPixelY = 260;

            Texture2D extendedTexture = new Texture2D(targetSize, targetSize, TextureFormat.R8, false, true);
            NativeArray<byte> extendedData = extendedTexture.GetRawTextureData<byte>();
            NativeArray<byte> originalData = tex.GetRawTextureData<byte>();
            
            // Initialize all pixels as white (occupied = 255)
            for (int i = 0; i < extendedData.Length; i++)
                extendedData[i] = 255;
            
            // Calculate offset to center the original texture so that parcel (0,0) maps to pixel (264,261)
            // The original texture has a 1-pixel border, so we need to account for that
            int2 offset = int2(centerPixelX - tex.width / 2, centerPixelY - tex.height / 2);
            
            // Copy original texture data to the extended texture
            for (int y = 0; y < tex.height; y++)
            {
                for (int x = 0; x < tex.width; x++)
                {
                    int targetX = offset.x + x;
                    int targetY = offset.y + y;
                    
                    if (targetX >= 0 && targetX < targetSize && targetY >= 0 && targetY < targetSize)
                    {
                        extendedData[targetY * targetSize + targetX] = originalData[y * tex.width + x];
                    }
                }
            }
            
            extendedTexture.Apply(false, false);
            
            // Now apply distance field calculation to the extended texture
            int rangeValue = WriteInteriorChamferOnBlack(
                extendedTexture,
                farIsHigh: false
            );
            
            // Get the updated data after distance field calculation
            extendedData = extendedTexture.GetRawTextureData<byte>();
            
            // Place range value in top-left 64x64 square of the final extended texture
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    extendedData[y * targetSize + x] = (byte)rangeValue;
                }
            }
            
            Debug.Log($"Range value stored in occupancy map: {rangeValue}");
            
            extendedTexture.Apply(false, false);
            
            // Encode to PNG and write
            var bytes = extendedTexture.EncodeToPNG();
            System.IO.File.WriteAllBytes(assetPath, bytes);
            
            DestroyImmediate(extendedTexture);
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

     public static int WriteInteriorChamferOnBlack(Texture2D r8, bool farIsHigh = false)
    {
        int w = r8.width, h = r8.height, n = w * h;
        var src = r8.GetRawTextureData<byte>();
        if (!src.IsCreated || src.Length != n) return 0;

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
            return 0;
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

        // Normalize using only BLACK pixels' distances with 10-value steps
        int maxD = 0;
        for (int i = 0; i < n; i++)
            if (src[i] == 0 && dist[i] < INF && dist[i] > maxD) maxD = dist[i];
        if (maxD == 0) { r8.Apply(false, false); return 0; }

        // Calculate how many steps we can fit, each step is 10 values
        const int stepSize = 10;
        int maxSteps = Mathf.Min(maxD, 25); // Cap at 25 steps to keep values under 255
        int rangeValue = maxSteps * stepSize; // This will be stored in top-left pixel

        // Write back: keep white at 255, paint gradient inside black with 10-value steps
        for (int i = 0; i < n; i++)
        {
            if (src[i] != 0) { src[i] = 255; continue; } // untouched white (occupied)
            
            // Convert distance to step number
            int stepNumber = Mathf.Min(dist[i], maxSteps);
            int value = stepNumber * stepSize;
            
            src[i] = (byte)(farIsHigh ? value : rangeValue - value);
        }

        // Don't place range square here - it will be placed in the final extended texture

        r8.Apply(false, false);
        return rangeValue;
    }

        private struct WorldManifest
        {
            public string[] roads;
            public string[] occupied;
            public string[] empty;
        }
    }
}
