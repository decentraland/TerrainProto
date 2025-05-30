#include "MountainsNoise.cs"

void MountainsNoise_float(float3 PositionIn, out float3 PositionOut, out float3 Normal)
{
    PositionOut = PositionIn;
    PositionOut.y += GetHeight(PositionIn.x, PositionIn.z);
    Normal = GetNormal(PositionIn.x, PositionIn.z);
}
