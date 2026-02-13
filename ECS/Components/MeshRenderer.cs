using System.Numerics;

namespace FluentDesigner.ECS.Components
{
    public class MeshRenderer : IResettable
    {
        public NoteTypeEnum Type { get; set; } = NoteTypeEnum.Click;
        public int MaterialId { get; set; } = 0;
        public Vector4 Color { get; set; } = new Vector4(1f, 1f, 1f, 1f);
        public int TextureIndex { get; set; } = -2;
        public bool Visibility { get; set; } = true;
        public int SortingLayer { get; set; } = 0;
        public int OrderInLayer { get; set; } = 0;

        public void Reset()
        {
            Type = NoteTypeEnum.Click;
            MaterialId = 0;
            Color = new Vector4(1f, 1f, 1f, 1f);
            TextureIndex = -1;
            Visibility = true;
            SortingLayer = 0;
            OrderInLayer = 0;
        }
    }
}