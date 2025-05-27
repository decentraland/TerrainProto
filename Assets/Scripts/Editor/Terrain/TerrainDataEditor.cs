using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

namespace Decentraland.Terrain
{
    [CustomEditor(typeof(TerrainData))]
    public sealed class TerrainDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (GUILayout.Button("Extract Colliders"))
                ExtractColliders();
        }

        private void ExtractColliders()
        {
            var target = this.target as TerrainData;
            var treePrefabs = target.treePrefabs;

            for (int i = 0; i < treePrefabs.Length; i++)
            {
                var treePrefab = treePrefabs[i];
                GameObject instance = Object.Instantiate(treePrefab.source);
                instance.name = treePrefab.source.name;
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

                        string colliderAssetPath = treePrefab.collider != null
                            ? AssetDatabase.GetAssetPath(treePrefab.collider)
                            : $"Assets/{instance.name}.prefab";

                        treePrefab.collider = PrefabUtility.SaveAsPrefabAsset(instance, colliderAssetPath);
                    }
                    else
                    {
                        treePrefab.collider = null;
                    }
                }

                treePrefabs[i] = treePrefab;
                Object.DestroyImmediate(instance);
            }

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssetIfDirty(target);
        }
    }
}
