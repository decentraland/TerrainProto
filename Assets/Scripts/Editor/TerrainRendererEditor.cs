using UnityEditor;
using UnityEngine;

namespace Decentraland.Terrain
{
    [CustomEditor(typeof(TerrainRenderer))]
    public sealed class TerrainRendererEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            var target = (TerrainRenderer)this.target;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.ObjectField("Parcel Mesh", target.ParcelMesh, typeof(Mesh), target);
            EditorGUI.EndDisabledGroup();
        }
    }
}
