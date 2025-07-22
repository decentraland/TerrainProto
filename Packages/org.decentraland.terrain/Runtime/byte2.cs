using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace Decentraland.Terrain
{
    public struct byte2
    {
        public byte x;
        public byte y;

        public byte2(byte x, byte y)
        {
            this.x = x;
            this.y = y;
        }

        public static float2 operator *(byte2 a, float b) =>
            float2(a.x * b, a.y * b);

        public static explicit operator byte2(float2 value) =>
            new byte2((byte)value.x, (byte)value.y);
    }
}
