using System.Collections.Generic;

namespace FluentDesigner.ECS.Components
{
    public class Transform : IResettable
    {
        public Position Position { get; set; }
        public Rotation Rotation { get; set; }
        public Scale Scale { get; set; }
        public RailIndex Index { get; set; }
        public float Radius { get; set; } = 7.5f;
        public int Parents { get; set; } = -1;
        public List<int> Children { get; set; } = new();

        public void Reset()
        {
            Position = default;
            Rotation = new Rotation { X = 0, Y = 0, Z = 0, W = 1 };
            Scale = new Scale { X = 1, Y = 1, Z = 1 };
            Index = default;
            Parents = -1;
            Children = new();
        }
    }

    public struct Position
    {
        public float X, Y, Z;
    }

    public struct Rotation
    {
        public float X, Y, Z, W;
    }

    public struct Scale
    {
        public float X, Y, Z;
    }

    public struct RailIndex
    {
        public int Index;
    }
}
