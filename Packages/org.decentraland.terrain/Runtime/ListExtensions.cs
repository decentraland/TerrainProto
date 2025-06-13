using System.Collections.Generic;

namespace Decentraland.Terrain
{
    public static class ListExtensions
    {
        public static void EnsureCapacity<T>(this List<T> list, int capacity)
        {
            if (list.Capacity < capacity)
                list.Capacity = capacity;
        }
    }
}
// D:\Decentraland\unity-explorer\Explorer\Library\PackageCache\com.unity.render-pipelines.universal@fd2e729c5297\Runtime\UniversalRenderPipeline.cs
