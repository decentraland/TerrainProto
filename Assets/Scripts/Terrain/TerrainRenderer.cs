using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

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
            TreePrototype[] treePrototypes = terrainData.treePrototypes;
            DetailPrototype[] detailPrototypes = terrainData.detailPrototypes;
            Vector3 parcelLocalCenter = new Vector3(parcelSize, maxHeight, parcelSize) * 0.5f;
            float parcelRadius = parcelLocalCenter.magnitude;
            Vector3 cameraPosition = camera.transform.position;
            var worldToProjectionMatrix = camera.projectionMatrix * camera.worldToCameraMatrix;

            float detailSqrDistance = terrainData.detailDistance;
            detailSqrDistance *= detailSqrDistance;

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

            var parcelInstances = ListPool<Matrix4x4>.Get();
            var treeInstances = ListPool<List<Matrix4x4>>.Get();
            var detailInstances = ListPool<List<Matrix4x4>>.Get();

            for (int i = 0; i < treePrototypes.Length; i++)
                treeInstances.Add(ListPool<Matrix4x4>.Get());

            for (int i = 0; i < detailPrototypes.Length; i++)
                detailInstances.Add(ListPool<Matrix4x4>.Get());

            try
            {
                for (int z = 0; z < terrainSize; z++)
                for (int x = 0; x < terrainSize; x++)
                {
                    Vector2Int parcel = new Vector2Int(x - terrainSize / 2, z - terrainSize / 2);

                    Vector4 parcelCenter = new Vector4(parcel.x * parcelSize + parcelLocalCenter.x,
                        parcelLocalCenter.y, parcel.y * parcelSize + parcelLocalCenter.z, 1f);

                    if (Vector4.Dot(clipPlane0, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane1, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane2, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane3, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane4, parcelCenter) < -parcelRadius
                        || Vector4.Dot(clipPlane5, parcelCenter) < -parcelRadius)
                    {
                        continue;
                    }

                    parcelInstances.Add(Matrix4x4.Translate(
                        new Vector3(parcel.x * parcelSize, 0f, parcel.y * parcelSize)));

                    Random random = new Random(
                        (uint)((parcel.x + terrainSize / 2) * terrainSize + parcel.y + terrainSize / 2 + 1));

                    // Tree scattering

                    {
                        TerrainNoiseFunction noise = terrainData.noiseFunction;

                        Vector3 position;
                        position.x = parcel.x * parcelSize + random.NextFloat(parcelSize);
                        position.z = parcel.y * parcelSize + random.NextFloat(parcelSize);
                        position.y = noise.HeightMap(position.x, position.z);

                        float yaw = random.NextFloat(-180f, 180f);

                        int type = random.NextInt(treePrototypes.Length);

                        treeInstances[type].Add(Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw, 0f),
                            Vector3.one));
                    }

                    // Detail scattering

                    if (((Vector3)parcelCenter - cameraPosition).sqrMagnitude < detailSqrDistance)
                    {
                        for (int i = 0; i < detailPrototypes.Length; i++)
                        {
                            ref DetailPrototype prototype = ref detailPrototypes[i];

                            switch (prototype.scatterMode)
                            {
                                case DetailScatterMode.JitteredGrid:
                                    JitteredGrid(parcel, prototype, ref random, detailInstances[i]);
                                    break;
                            }
                        }
                    }
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

                // Parcel rendering

                if (parcelInstances.Count > 0)
                    Graphics.RenderMeshInstanced(in renderParams, parcelMesh, 0, parcelInstances);

                // Tree rendering

                for (int i = 0; i < treePrototypes.Length; i++)
                {
                    if (treeInstances[i].Count == 0)
                        continue;

                    // TODO: Handle LODs
                    Renderer renderer = treePrototypes[i].source.GetComponent<LODGroup>().GetLODs()[0]
                        .renderers[0];

                    Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;

                    using (ListPool<Material>.Get(out var materials))
                    {
                        renderer.GetSharedMaterials(materials);

                        for (int j = 0; j < materials.Count; j++)
                        {
                            renderParams.material = materials[j];
                            Graphics.RenderMeshInstanced(in renderParams, mesh, j, treeInstances[i]);
                        }
                    }
                }

                // Detail rendering

                for (int i = 0; i < detailPrototypes.Length; i++)
                {
                    if (detailInstances[i].Count == 0)
                        continue;

                    Renderer renderer = detailPrototypes[i].source.GetComponent<Renderer>();
                    Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    renderParams.material = renderer.sharedMaterial;
                    renderParams.shadowCastingMode = renderer.shadowCastingMode;
                    Graphics.RenderMeshInstanced(in renderParams, mesh, 0, detailInstances[i]);
                }
            }
            finally
            {
                for (int i = 0; i < treeInstances.Count; i++)
                    ListPool<Matrix4x4>.Release(treeInstances[i]);

                for (int i = 0; i < detailInstances.Count; i++)
                    ListPool<Matrix4x4>.Release(detailInstances[i]);

                ListPool<List<Matrix4x4>>.Release(treeInstances);
                ListPool<List<Matrix4x4>>.Release(detailInstances);
                ListPool<Matrix4x4>.Release(parcelInstances);
            }

            Profiler.EndSample();
        }

        private void JitteredGrid(Vector2Int parcel, DetailPrototype prototype, ref Random random,
            List<Matrix4x4> instances)
        {
            int parcelSize = terrainData.parcelSize;
            float invDensity = (float)parcelSize / prototype.density;
            TerrainNoiseFunction noise = terrainData.noiseFunction;

            for (int z = 0; z < prototype.density; z++)
            for (int x = 0; x < prototype.density; x++)
            {
                Vector3 position;
                position.x = parcel.x * parcelSize + x * invDensity + random.NextFloat(invDensity);
                position.z = parcel.y * parcelSize + z * invDensity + random.NextFloat(invDensity);
                position.y = noise.HeightMap(position.x, position.z);

                float yaw = random.NextFloat(-180f, 180f);

                Vector3 scale;
                scale.x = random.NextFloat(prototype.minWidth, prototype.maxWidth);
                scale.z = scale.x;
                scale.y = random.NextFloat(prototype.minHeight, prototype.maxHeight);

                instances.Add(Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw, 0f), scale));
            }
        }
    }
}
