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

            EditorGUILayout.LabelField("Ground Instance Count", target.GroundInstanceCount.ToString());
            EditorGUILayout.LabelField("Tree Instance Count", target.TreeInstanceCount.ToString());
            EditorGUILayout.LabelField("Clutter Instance Count", target.ClutterInstanceCount.ToString());
            EditorGUILayout.LabelField("Grass Instance Count", target.GrassInstanceCount.ToString());
        }

        private bool HasFrameBounds() =>
            true;

        private Bounds OnGetFrameBounds()
        {
            var target = (TerrainRenderer)this.target;
            return target.Bounds;
        }

        public override bool RequiresConstantRepaint() =>
            EditorApplication.isPlaying;
    }
}
