using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Decentraland.Terrain
{
    /// <remarks>
    /// Why not <see cref="UnityEngine.Pool.ObjectPool{T}"/>? Because it is not serializable and is not
    /// preserved when reloading scripts during play mode.
    /// </remarks>
    [Serializable]
    public struct PrefabInstancePool : IDisposable
    {
        private List<GameObject> instances;
        private GameObject prefab;
#if UNITY_EDITOR
        private Transform parent;
#endif

        public PrefabInstancePool(GameObject prefab
#if UNITY_EDITOR
            , Transform parent
#endif
        )
        {
            instances = new List<GameObject>();
            this.prefab = prefab;
#if UNITY_EDITOR
            this.parent = parent;
#endif
        }

        public readonly void Dispose()
        {
            for (int i = 0; i < instances.Count; i++)
            {
                GameObject instance = instances[i];

                if (instance != null)
                    Object.Destroy(instances[i]);
            }
        }

        public readonly GameObject Get()
        {
            GameObject item;

            if (instances.Count > 0)
            {
                int last = instances.Count - 1;
                item = instances[last];
                instances.RemoveAt(last);
            }
            else
            {
                item = Object.Instantiate(prefab
#if UNITY_EDITOR
                    , parent
#endif
                );
            }

            return item;
        }

        public readonly void Release(GameObject item)
        {
            instances.Add(item);
        }
    }
}
