using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Decentraland.Terrain
{
    [ExecuteAlways]
    public sealed class TerrainRenderer : MonoBehaviour
    {
        [SerializeField] private TerrainData terrainData;
        [SerializeField] private Material material;

        private Mesh parcelMesh;
        private Bounds bounds;

#if UNITY_EDITOR
        public Mesh ParcelMesh => parcelMesh;
#endif

        private void OnValidate()
        {
            if (terrainData == null)
                return;

#if UNITY_EDITOR
            if (parcelMesh != null)
            {
                if (!Application.isPlaying)
                    Object.DestroyImmediate(parcelMesh);
                else
                    Object.Destroy(parcelMesh);
            }
#endif

            parcelMesh = CreateParcelMesh();
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += DrawParcels;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= DrawParcels;
        }

        private void OnDestroy()
        {
            if (parcelMesh != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Object.DestroyImmediate(parcelMesh);
                else
#endif
                Object.Destroy(parcelMesh);
            }
        }

        private Mesh CreateParcelMesh()
        {
            var parcelMesh = new Mesh()
            {
                name = "Parcel Render Mesh",
                hideFlags = HideFlags.DontSave
            };

            int parcelSize = terrainData.parcelSize;
            int sideVertexCount = parcelSize + 1;
            var vertices = new Vector3[sideVertexCount * sideVertexCount];

            for (int z = 0; z <= parcelSize; z++)
                for (int x = 0; x <= parcelSize; x++)
                    vertices[z * sideVertexCount + x] = new Vector3(x, 0f, z);

            var triangles = new int[parcelSize * parcelSize * 6];
            int index = 0;

            for (int z = 0; z < parcelSize; z++)
            {
                for (int x = 0; x < parcelSize; x++)
                {
                    int start = z * sideVertexCount + x;

                    triangles[index++] = start;
                    triangles[index++] = start + sideVertexCount + 1;
                    triangles[index++] = start + 1;

                    triangles[index++] = start;
                    triangles[index++] = start + sideVertexCount;
                    triangles[index++] = start + sideVertexCount + 1;
                }
            }

            var normals = new Vector3[sideVertexCount * sideVertexCount];

            for (int z = 0; z <= parcelSize; z++)
                for (int x = 0; x <= parcelSize; x++)
                    normals[z * sideVertexCount + x] = Vector3.up;

            parcelMesh.vertices = vertices;
            parcelMesh.triangles = triangles;
            parcelMesh.normals = normals;
            parcelMesh.UploadMeshData(true);

            return parcelMesh;
        }

        private void DrawParcels(ScriptableRenderContext context, List<Camera> cameras)
        {
            Camera camera;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                SceneView sceneView = SceneView.lastActiveSceneView;
                camera = sceneView != null ? sceneView.camera : Camera.main;
            }
            else
#endif
            {
                camera = Camera.main;
            }

            if (camera == null)
                return;

            Profiler.BeginSample(nameof(DrawParcels));

            int parcelSize = terrainData.parcelSize;
            int terrainSize = terrainData.terrainSize;
            float maxHeight = terrainData.maxHeight;
            Vector3 parcelLocalCenter = new Vector3(parcelSize, maxHeight, parcelSize) * 0.5f;
            float parcelRadius = parcelLocalCenter.magnitude; // Clever, eh? :D
            var worldToProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;

            Vector4 p1 = worldToProjectionMatrix.GetRow(0);
            Vector4 p2 = worldToProjectionMatrix.GetRow(1);
            Vector4 p3 = worldToProjectionMatrix.GetRow(2);
            Vector4 p4 = worldToProjectionMatrix.GetRow(3);

            Vector4 clipPlane0 = p4 + p3;
            clipPlane0 /= ((Vector3)clipPlane0).magnitude;
            Vector4 clipPlane1 = p4 - p3;
            clipPlane1 /= ((Vector3)clipPlane1).magnitude;
            Vector4 clipPlane2 = p4 + p1;
            clipPlane2 /= ((Vector3)clipPlane2).magnitude;
            Vector4 clipPlane3 = p4 - p1;
            clipPlane3 /= ((Vector3)clipPlane3).magnitude;
            Vector4 clipPlane4 = p4 + p2;
            clipPlane4 /= ((Vector3)clipPlane4).magnitude;
            Vector4 clipPlane5 = p4 - p2;
            clipPlane5 /= ((Vector3)clipPlane5).magnitude;

            var parcelTransforms = ListPool<Matrix4x4>.Get();
            var treeTransforms = ListPool<List<Matrix4x4>>.Get();

            for (int i = 0; i < terrainData.treePrefabs.Length; i++)
                treeTransforms.Add(ListPool<Matrix4x4>.Get());

            try
            {
                for (int z = 0; z < terrainSize; z++)
                for (int x = 0; x < terrainSize; x++)
                {
                    Vector2Int parcel = new Vector2Int(x - terrainSize / 2, z - terrainSize / 2);

                    Vector4 parcelCenter = new Vector4(parcel.x + parcelLocalCenter.x,
                        parcelLocalCenter.y, parcel.y + parcelLocalCenter.z, 1f);

                    if (Vector4.Dot(clipPlane0, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane1, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane2, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane3, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane4, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane5, parcelCenter) < -parcelRadius)
                    {
                        continue;
                    }

                    parcelTransforms.Add(Matrix4x4.Translate(
                        new Vector3(parcel.x * parcelSize, 0f, parcel.y * parcelSize)));

                    // Tree generation starts here.

                    TerrainNoiseFunction noise = terrainData.noiseFunction;
                    Vector3 treePos;
                    treePos.x = noise.RandomRange(parcel.x, parcel.y, parcel.x * parcelSize, (parcel.x + 1) * parcelSize);
                    treePos.z = noise.RandomRange(treePos.x, treePos.x, parcel.y * parcelSize, (parcel.y + 1) * parcelSize);
                    treePos.y = noise.HeightMap(treePos.x, treePos.z);
                    float treeYaw = noise.RandomRange(treePos.x, treePos.z, -180f, 180f);
                    int treeType = (int)noise.RandomRange(treeYaw, treeYaw, 0f, terrainData.treePrefabs.Length);

                    treeTransforms[treeType].Add(Matrix4x4.TRS(treePos, Quaternion.Euler(0f, treeYaw, 0f), Vector3.one));
                }

                var renderParams = new RenderParams()
                {
                    instanceID = GetInstanceID(),
                    layer = gameObject.layer,
                    material = material,
                    receiveShadows = true,
                    renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                    shadowCastingMode = ShadowCastingMode.On
                };

                renderParams.worldBounds = new Bounds(
                    new Vector3(0f, maxHeight * 0.5f, 0f),
                    new Vector3(terrainSize * parcelSize, maxHeight, terrainSize * parcelSize));

                if (parcelTransforms.Count > 0)
                    Graphics.RenderMeshInstanced(in renderParams, parcelMesh, 0, parcelTransforms);

                // Tree rendering starts here.

                for (int i = 0; i < treeTransforms.Count; i++)
                {
                    if (treeTransforms[i].Count == 0)
                        continue;

                    Renderer treeRenderer = terrainData.treePrefabs[i].source.GetComponent<LODGroup>()
                        .GetLODs()[0].renderers[0];

                    Mesh treeMesh = treeRenderer.GetComponent<MeshFilter>().sharedMesh;

                    using (ListPool<Material>.Get(out var treeMaterials))
                    {
                        treeRenderer.GetSharedMaterials(treeMaterials);

                        for (int j = 0; j < treeMaterials.Count; j++)
                        {
                            renderParams.material = treeMaterials[j];
                            Graphics.RenderMeshInstanced(in renderParams, treeMesh, j, treeTransforms[i]);
                        }
                    }
                }
            }
            finally
            {
                for (int i = 0; i < treeTransforms.Count; i++)
                    ListPool<Matrix4x4>.Release(treeTransforms[i]);

                ListPool<List<Matrix4x4>>.Release(treeTransforms);
                ListPool<Matrix4x4>.Release(parcelTransforms);
            }

            Profiler.EndSample();
        }
    }
}
