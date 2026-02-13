namespace FluentDesigner.ECS.Components
{
    public class NoteType
    {
        public NoteTypeEnum Type { get; set; }
    }

    public enum NoteTypeEnum
    {
        Click = 0,
        Flick = 1,
        Slide = 2,
        Rail = 3,
        Catch = 4,
        RotateL = 5,
        RotateR = 6,
        Guiding = 7,
        Mine = 8,
        Empty = 999
    }
}
