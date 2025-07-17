using System;
using UnityEngine;
using UnityEngine.Rendering;
using System.Linq;
using Unity.Mathematics;

namespace Decentraland.Terrain
{
    [CreateAssetMenu]
    public sealed class GrassIndirectRenderer : ScriptableObject
    {
        [field: SerializeField] private ComputeShader QuadTreeCullingShader { get; set; }
        [field: SerializeField] private ComputeShader ScatterGrassShader { get; set; }
        [field: SerializeField] private Texture2D HeightMapTexture { get; set; }
        [field: SerializeField] private Texture2D TerrainBlendTexture { get; set; }
        [field: SerializeField] private Texture2D GroundDetailTexture { get; set; }
        [field: SerializeField] private Texture2D SandDetailTexture { get; set; }
        public struct QuadTreeNodeData
        {
            public uint Depth8CornerIndexStart24;
        }

        public struct PerInst
        {
            public float4 position;
            public float4 quatRotation;
            public float4 colour;
        }

        //private int renderTextureSize = 512;
        private int maxDepth = 10;
        [NonSerialized] private bool initialized;
        private QuadTreeNodeData[] quadTreeNodes = new QuadTreeNodeData[349525];
        private ComputeBuffer quadTreeNodesComputeBuffer;
        private ComputeBuffer visibleParcelsComputeBuffer;
        private ComputeBuffer visibleparcelCountComputeBuffer;
        private ComputeBuffer grassInstancesComputeBuffer;
        private GraphicsBuffer drawArgs;
        private uint[] args = new uint[5];
        private int[] visibleCount = new int[1];
        private Mesh grassMesh;
        private Material grassMaterial;

        public static uint CreateDepth8CornerIndexStart(byte depth, uint cornerIndexStart)
        {
            return ((uint)depth << 24) | cornerIndexStart;
        }

        public void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            GenerateQuadTree();
            SetupComputeBuffers();
        }

        public void Render(TerrainData terrainData, Camera camera, bool renderToAllCameras)
        {
            Initialize();

            RunFrustumCulling(terrainData, camera);
            GenerateScatteredGrass(terrainData);
            DetailPrototype grass = terrainData.DetailPrototypes[0];
            SetMeshAndMaterial(grass.Mesh, grass.Material);
            RenderGrass(renderToAllCameras ? null : camera);
        }

        public void SetMeshAndMaterial(Mesh mesh, Material material)
        {
            grassMesh = mesh;
            grassMaterial = material;
            grassMaterial.EnableKeyword("_GPU_GRASS_BATCHING");
            args[0] = grassMesh.GetIndexCount(0); // indexCountPerInstance
            args[1] = (uint)(0); // instanceCount
            args[2] = grassMesh.GetIndexStart(0); // startIndexLocation
            args[3] = grassMesh.GetBaseVertex(0); // baseVertexLocation
            args[4] = 0; // startInstanceLocation
        }

        public void GenerateQuadTree()
        {
            quadTreeNodes[0].Depth8CornerIndexStart24 = 0;

            SubdivideNode(0, 0);
        }

        void SubdivideNode(uint cornerIndexStart, byte currentDepth)
        {
            const uint nFullQuadSize = 512;
            byte newDepth = (byte)(currentDepth + 1);
            uint nCornerSize = (nFullQuadSize >> newDepth);
            nCornerSize *= nCornerSize;

            if (newDepth >= maxDepth)
                return;

            uint arrayPosition = 0;
            for (int layerCount = 0; layerCount < newDepth; ++layerCount)
                arrayPosition += (uint)(1 << (layerCount * 2));

            uint[] cornerIndexStartArray = new uint[4];
            // NW - Top Left
            uint nodeIndex_NW = arrayPosition + (uint)Mathf.FloorToInt((float)cornerIndexStart / nCornerSize);
            quadTreeNodes[nodeIndex_NW].Depth8CornerIndexStart24 =
                CreateDepth8CornerIndexStart(newDepth, cornerIndexStart);
            cornerIndexStartArray[0] = cornerIndexStart;

            // NE - Top Right
            uint nodeIndex_NE = arrayPosition + 1 + (uint)Mathf.FloorToInt((float)cornerIndexStart / nCornerSize);
            quadTreeNodes[nodeIndex_NE].Depth8CornerIndexStart24 =
                CreateDepth8CornerIndexStart(newDepth, cornerIndexStart + (nCornerSize * 1));
            cornerIndexStartArray[1] = cornerIndexStart + (nCornerSize * 1);

            // SW - Bottom Left
            uint nodeIndex_SW = arrayPosition + 2 + (uint)Mathf.FloorToInt((float)cornerIndexStart / nCornerSize);
            quadTreeNodes[nodeIndex_SW].Depth8CornerIndexStart24 =
                CreateDepth8CornerIndexStart(newDepth, cornerIndexStart + (nCornerSize * 2));
            cornerIndexStartArray[2] = cornerIndexStart + (nCornerSize * 2);

            // SE - Bottom Right
            uint nodeIndex_SE = arrayPosition + 3 + (uint)Mathf.FloorToInt((float)cornerIndexStart / nCornerSize);
            quadTreeNodes[nodeIndex_SE].Depth8CornerIndexStart24 =
                CreateDepth8CornerIndexStart(newDepth, cornerIndexStart + (nCornerSize * 3));
            cornerIndexStartArray[3] = cornerIndexStart + (nCornerSize * 3);

            for (byte i = 0; i < 4; ++i)
            {
                SubdivideNode(cornerIndexStartArray[i], newDepth);
            }
        }

        public void SetupComputeBuffers()
        {
            ReleaseBuffers();

            if (!quadTreeNodes.Any())
                return;

            quadTreeNodesComputeBuffer = new ComputeBuffer(quadTreeNodes.Length,
                System.Runtime.InteropServices.Marshal.SizeOf<QuadTreeNodeData>());
            visibleParcelsComputeBuffer = new ComputeBuffer(512 * 512, sizeof(int) * 2);
            visibleparcelCountComputeBuffer = new ComputeBuffer(1, sizeof(int));
            grassInstancesComputeBuffer =
                new ComputeBuffer(256 * 256, System.Runtime.InteropServices.Marshal.SizeOf<PerInst>());
            drawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, count: 1,
                GraphicsBuffer.IndirectDrawIndexedArgs.size);

            quadTreeNodesComputeBuffer.SetData(quadTreeNodes.ToArray());
        }

        public void RunFrustumCulling(TerrainData terrainData, Camera camera)
        {
            if (QuadTreeCullingShader == null ||
                quadTreeNodesComputeBuffer == null ||
                visibleParcelsComputeBuffer == null ||
                visibleparcelCountComputeBuffer == null)
                return;

            // Reset visible count
            visibleCount[0] = 0;
            visibleparcelCountComputeBuffer.SetData(visibleCount);

            // Set up compute shader
            int kernelIndex = QuadTreeCullingShader.FindKernel("HierarchicalQuadTreeCulling");

            // Set camera data
            Matrix4x4 viewMatrix = camera.worldToCameraMatrix;
            Matrix4x4 projMatrix = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect,
                camera.nearClipPlane, terrainData.DetailDistance);
            Matrix4x4 viewProjMatrix = projMatrix * viewMatrix;

            Vector4 terrainBounds = new Vector4(terrainData.Bounds.xMin, terrainData.Bounds.xMax,
                terrainData.Bounds.yMin, terrainData.Bounds.yMax);

            QuadTreeCullingShader.SetMatrix("viewProjMatrix", viewProjMatrix);
            QuadTreeCullingShader.SetVector("TerrainBounds", terrainBounds);

            QuadTreeCullingShader.SetTexture(kernelIndex, "OccupancyTexture",
                terrainData.OccupancyMap != null ? terrainData.OccupancyMap : Texture2D.blackTexture);

            QuadTreeCullingShader.SetBuffer(kernelIndex, "quadTreeNodes", quadTreeNodesComputeBuffer);
            QuadTreeCullingShader.SetBuffer(kernelIndex, "visibleParcels", visibleParcelsComputeBuffer);
            QuadTreeCullingShader.SetBuffer(kernelIndex, "visibleParcelCount", visibleparcelCountComputeBuffer);

            int threadGroups = Mathf.CeilToInt((quadTreeNodes.Length - 87381) / 256.0f);
            QuadTreeCullingShader.Dispatch(kernelIndex, threadGroups, 1, 1);

            // Read back results
            visibleparcelCountComputeBuffer.GetData(visibleCount);
            visibleCount[0] = Mathf.Min(visibleCount[0] * 256, 256 * 256);
            args[1] = (uint)(visibleCount[0]); // instanceCount
            drawArgs.SetData(args);
        }

        public void GenerateScatteredGrass(TerrainData terrainData)
        {
            if (ScatterGrassShader == null ||
                HeightMapTexture == null ||
                visibleParcelsComputeBuffer == null ||
                visibleparcelCountComputeBuffer == null ||
                grassInstancesComputeBuffer == null ||
                TerrainBlendTexture == null)
                return;

            // Set up compute shader
            int kernelIndex = ScatterGrassShader.FindKernel("ScatterGrass");

            Vector4 terrainBounds = new Vector4(terrainData.Bounds.xMin, terrainData.Bounds.xMax,
                terrainData.Bounds.yMin, terrainData.Bounds.yMax);

            ScatterGrassShader.SetVector("TerrainBounds", terrainBounds);
            ScatterGrassShader.SetFloat("TerrainHeight", 4.0f);
            ScatterGrassShader.SetTexture(kernelIndex, "HeightMapTexture", HeightMapTexture);
            ScatterGrassShader.SetTexture(kernelIndex, "TerrainBlendTexture", TerrainBlendTexture);
            ScatterGrassShader.SetTexture(kernelIndex, "GroundDetailTexture", GroundDetailTexture);
            ScatterGrassShader.SetTexture(kernelIndex, "SandDetailTexture", SandDetailTexture);

            ScatterGrassShader.SetTexture(kernelIndex, "OccupancyTexture",
                terrainData.OccupancyMap != null ? terrainData.OccupancyMap : Texture2D.blackTexture);

            ScatterGrassShader.SetBuffer(kernelIndex, "visibleParcels", visibleParcelsComputeBuffer);
            ScatterGrassShader.SetBuffer(kernelIndex, "visibleParcelCount", visibleparcelCountComputeBuffer);
            ScatterGrassShader.SetBuffer(kernelIndex, "instances", grassInstancesComputeBuffer);

            uint threadGroupSizes_X, threadGroupSizes_Y, threadGroupSizes_Z;
            ScatterGrassShader.GetKernelThreadGroupSizes(kernelIndex, out threadGroupSizes_X, out threadGroupSizes_Y,
                out threadGroupSizes_Z);
            ScatterGrassShader.Dispatch(kernelIndex,
                Mathf.CeilToInt(65536.0f / threadGroupSizes_X),
                Mathf.CeilToInt(1.0f / threadGroupSizes_Y),
                Mathf.CeilToInt(1.0f / threadGroupSizes_Z));
        }

        public void RenderGrass(Camera camera)
        {
            if (drawArgs == null || visibleParcelsComputeBuffer == null || grassInstancesComputeBuffer == null)
                return;

            var renderParams = new RenderParams()
            {
                camera = camera,
                layer = 1, // Default
                receiveShadows = true,
                renderingLayerMask = RenderingLayerMask.defaultRenderingLayerMask,
                worldBounds = new Bounds(Vector3.zero, new Vector3(4096, 16, 4096)),
                shadowCastingMode = ShadowCastingMode.Off,
            };

            renderParams.material = grassMaterial;
            renderParams.matProps = new MaterialPropertyBlock();
            renderParams.matProps.SetBuffer("_PerParcelBuffer", visibleParcelsComputeBuffer);
            renderParams.matProps.SetBuffer("_PerInstanceBuffer", grassInstancesComputeBuffer);
            renderParams.shadowCastingMode = ShadowCastingMode.Off;

            Graphics.RenderMeshIndirect(renderParams, grassMesh, drawArgs);
        }

        void OnDestroy()
        {
            ReleaseBuffers();
        }

        void ReleaseBuffers()
        {
            quadTreeNodesComputeBuffer?.Release();
            visibleParcelsComputeBuffer?.Release();
            visibleparcelCountComputeBuffer?.Release();
            grassInstancesComputeBuffer?.Release();
            drawArgs?.Release();
        }
    }
}
