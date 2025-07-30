using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TerrainProto
{
    [CustomEditor(typeof(TreePlacer))]
    public sealed class TreePlacerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (GUILayout.Button("Place Trees"))
                PlaceTrees();

            if (GUILayout.Button("Delete Trees"))
                DeleteTrees();
        }

        private void PlaceTrees()
        {
            var target = (TreePlacer)this.target;
            string inPath = $"{Application.streamingAssetsPath}/TreeInstancesTest.bin";

            using (var stream = new FileStream(inPath, FileMode.Open))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false)))
            {
                try
                {
                    while (true)
                    {
                        int prototypeIndex = reader.ReadInt32();
                        float x = reader.ReadSingle();
                        float z = reader.ReadSingle();
                        float rotation = reader.ReadSingle();
                        float widthScale = reader.ReadSingle();
                        float heightScale = reader.ReadSingle();

                        GameObject instance = Object.Instantiate(target.treePrefabs[prototypeIndex],
                            new Vector3(x, 0f, z), Quaternion.Euler(0f, rotation, 0f), target.transform);

                        instance.transform.localScale = new Vector3(widthScale, heightScale, widthScale);
                    }
                }
                catch (EndOfStreamException) { }
            }
        }

        private void DeleteTrees()
        {
            var target = (TreePlacer)this.target;
            Transform transform = target.transform;

            for (int i = transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(transform.GetChild(i).gameObject);
        }
    }
}
