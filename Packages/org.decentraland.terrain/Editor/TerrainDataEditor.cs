using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Decentraland.Terrain
{
    [CustomEditor(typeof(TerrainData), true)]
    public sealed class TerrainDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            TerrainData target = (TerrainData)this.target;

            EditorGUI.BeginDisabledGroup(
#if IMAGE_CONVERSION
                target.OccupancyMap == null
#else
                true
#endif
            );

            if (GUILayout.Button("Save Occupancy Map"))
                SaveOccupancyMap();

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Update Prototypes from Sources"))
                UpdatePrototypes();
        }

        private void SaveOccupancyMap()
        {
#if IMAGE_CONVERSION
            TerrainData target = (TerrainData)this.target;

            string path = EditorUtility.SaveFilePanel("Save Occupancy Map", Application.dataPath,
                "OccupancyMap", "png");

            if (path != null)
                File.WriteAllBytes(path, target.OccupancyMap.EncodeToPNG());
#endif
        }

        private void UpdatePrototypes()
        {
            TerrainData target = (TerrainData)this.target;
            TreePrototype[] treePrototypes = target.TreePrototypes;

            for (int i = 0; i < treePrototypes.Length; i++)
            {
                ref TreePrototype prototype = ref treePrototypes[i];
                GameObject instance = Object.Instantiate(prototype.Source);
                instance.name = prototype.Source.name;
                bool hasColliders = false;

                using (ListPool<Component>.Get(out var components))
                {
                    instance.GetComponentsInChildren(true, components);

                    foreach (var component in components)
                    {
                        if (component is Transform)
                            continue;

                        if (component is Collider)
                            hasColliders = true;
                        else
                            Object.DestroyImmediate(component);
                    }

                    if (hasColliders)
                    {
                        foreach (var component in components)
                        {
                            if (component != null && component is Transform
                                                  && component.GetComponentInChildren<Collider>() == null)
                            {
                                Object.DestroyImmediate(component.gameObject);
                            }
                        }

                        string colliderAssetPath = prototype.Collider != null
                            ? AssetDatabase.GetAssetPath(prototype.Collider)
                            : $"Assets/{instance.name}.prefab";

                        prototype.Collider = PrefabUtility.SaveAsPrefabAsset(instance, colliderAssetPath);
                    }
                    else
                    {
                        prototype.Collider = null;
                    }
                }

                Object.DestroyImmediate(instance);

                LODGroup lodGroup = prototype.Source.GetComponent<LODGroup>();
                prototype.LocalSize = lodGroup.size;
                LOD[] groupLods = lodGroup.GetLODs();
                TreeLOD[] prototypeLods = prototype.Lods;
                Array.Resize(ref prototypeLods, groupLods.Length);
                prototype.Lods = prototypeLods;

                for (int j = 0; j < groupLods.Length; j++)
                {
                    LOD lod = groupLods[j];
                    Renderer renderer = lod.renderers[0];
                    ref TreeLOD treeLod = ref prototypeLods[j];
                    treeLod.Mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    treeLod.MinScreenSize = lod.screenRelativeTransitionHeight;
                    treeLod.Materials = renderer.sharedMaterials;
                }
            }

            DetailPrototype[] detailPrototypes = target.DetailPrototypes;

            for (int i = 0; i < detailPrototypes.Length; i++)
            {
                ref DetailPrototype prototype = ref detailPrototypes[i];
                prototype.Mesh = prototype.Source.GetComponent<MeshFilter>().sharedMesh;
                prototype.Material = prototype.Source.GetComponent<Renderer>().sharedMaterial;
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssetIfDirty(target);
        }
    }
}
