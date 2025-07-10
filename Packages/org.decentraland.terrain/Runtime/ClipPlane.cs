using Unity.Mathematics;
using Unity.Mathematics.Geometry;

namespace Decentraland.Terrain
{
    public struct ClipPlane
    {
        public Plane plane;
        public int farCornerIndex;

        public ClipPlane(float4 coefficients)
        {
            plane = new Plane() { NormalAndDistance = Plane.Normalize(coefficients) };
            if (plane.NormalAndDistance.x < 0f)
            {
                if (plane.NormalAndDistance.y < 0f)
                    farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b000 : 0b001;
                else
                    farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b010 : 0b011;
            }
            else
            {
                if (plane.NormalAndDistance.y < 0f)
                    farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b100 : 0b101;
                else
                    farCornerIndex = plane.NormalAndDistance.z < 0f ? 0b110 : 0b111;
            }
        }
    }
}
