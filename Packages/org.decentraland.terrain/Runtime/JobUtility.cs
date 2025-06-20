using UnityEngine;

namespace Decentraland.Terrain
{
    public static class JobUtility
    {
        private static readonly int PROCESSOR_COUNT = SystemInfo.processorCount;
        public static int GetBatchSize(int arrayLength) => arrayLength / PROCESSOR_COUNT + 1;
    }
}
