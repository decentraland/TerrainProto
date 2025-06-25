using System;
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

            if (GUILayout.Button("Update Prototypes from Sources"))
                UpdatePrototypes();
        }

        private void UpdatePrototypes()
        {
            TerrainData target = (TerrainData)this.target;
            TreePrototype[] treePrototypes = target.treePrototypes;

            for (int i = 0; i < treePrototypes.Length; i++)
            {
                ref TreePrototype prototype = ref treePrototypes[i];
                GameObject instance = Object.Instantiate(prototype.source);
                instance.name = prototype.source.name;
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

                        string colliderAssetPath = prototype.collider != null
                            ? AssetDatabase.GetAssetPath(prototype.collider)
                            : $"Assets/{instance.name}.prefab";

                        prototype.collider = PrefabUtility.SaveAsPrefabAsset(instance, colliderAssetPath);
                    }
                    else
                    {
                        prototype.collider = null;
                    }
                }

                Object.DestroyImmediate(instance);

                LODGroup lodGroup = prototype.source.GetComponent<LODGroup>();
                prototype.localSize = lodGroup.size;
                LOD[] lods = lodGroup.GetLODs();
                Array.Resize(ref prototype.lods, lods.Length);

                for (int j = 0; j < lods.Length; j++)
                {
                    LOD lod = lods[j];
                    Renderer renderer = lod.renderers[0];
                    ref TreeLOD treeLod = ref prototype.lods[j];
                    treeLod.mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    treeLod.minScreenSize = lod.screenRelativeTransitionHeight;
                    treeLod.materials = renderer.sharedMaterials;
                }
            }

            DetailPrototype[] detailPrototypes = target.detailPrototypes;

            for (int i = 0; i < detailPrototypes.Length; i++)
            {
                ref DetailPrototype prototype = ref detailPrototypes[i];
                prototype.mesh = prototype.source.GetComponent<MeshFilter>().sharedMesh;
                prototype.material = prototype.source.GetComponent<Renderer>().sharedMaterial;
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssetIfDirty(target);
        }
    }
}
