using UnityEngine;

namespace Decentraland.Terrain
{
    public static class JobUtility
    {
        private static readonly int processorCount = SystemInfo.processorCount;
        public static int GetBatchSize(int arrayLength) => arrayLength / processorCount + 1;
    }
}
