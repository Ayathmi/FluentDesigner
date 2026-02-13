using System;

namespace FluentDesigner.ECS.Components
{
    public class VelocityCurve
    {
        public KeyFrames Curve { get; set; } = new KeyFrames();
        public const float MinY = -12f;
        public const float MaxY = 12f;

        public void OffsetAllTimes(float timeDelta, float newMaxTime)
        {
            if (Curve.Frames == null || Curve.Frames.Length == 0)
            {
                return;
            }

            for (int i = 0; i < Curve.Frames.Length; i++)
            {
                ref var frame = ref Curve.Frames[i];
                frame.Time = Math.Clamp(frame.Time + timeDelta, 0f, newMaxTime);
                if (frame.ControlPoints != null)
                {
                    for (int j = 0; j < frame.ControlPoints.Length; j++)
                    {
                        ref var point = ref frame.ControlPoints[j];
                        point.X = Math.Clamp(point.X + timeDelta, 0f, newMaxTime);
                    }
                }
            }
        }

        public float Evaluate(float time)
        {
            if (Curve.Frames.Length == 0)
            {
                return 0f;
            }

            if (Curve.Frames.Length == 1)
            {
                return Curve.Frames[0].Value;
            }

            int i = FindKeyFrameIndex(time);
            if (i >= Curve.Frames.Length - 1)
            {
                return Curve.Frames[^1].Value;
            }

            ref var from = ref Curve.Frames[i];
            ref var to = ref Curve.Frames[i + 1];

            float local = (time - from.Time) / (to.Time - from.Time);

            return from.InterpolationType switch
            {
                CurveInterpolationType.Linear => Lerp(from.Value, to.Value, local),
                CurveInterpolationType.Bezier => Bezier(in from, in to, local),
                CurveInterpolationType.CatmullRom => CatmullRom(i, local),
                _ => from.Value
            };

        }

        private int FindKeyFrameIndex(float time)
        {
            int i = 0;
            for (; i < Curve.Frames.Length - 1; i++)
            {
                if (time < Curve.Frames[i + 1].Time)
                {
                    break;
                }
            }
            return i;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float Bezier(in KeyFrame from, in KeyFrame to, float t)
        {
            var p0 = from.Value;
            var p3 = to.Value;
            var p1 = from.ControlPoints.Length > 0
                ? from.ControlPoints[0].Y
                : p0;
            var p2 = from.ControlPoints.Length > 1
                ? from.ControlPoints[1].Y
                : p3;


            float u = 1 - t;
            float uSquare = u * u;
            float uCube = u * u * u;
            float tSquare = t * t;
            float tCube = t * t * t;

            return uCube * p0 + 3 * uSquare * t * p1 + 3 * u * tSquare * p2 + tCube * p3;
        }

        private static float MapControlPointY(float normalizedY, float fromValue, float toValue)
        {
            float range = MaxY - MinY;
            return fromValue + (normalizedY - MinY) / range * (toValue - fromValue + range) - (toValue - fromValue) / 2;
        }

        private float CatmullRom(int index, float t)
        {
            ref var frame = ref Curve.Frames[index];
            ref var nextFrame = ref Curve.Frames[Math.Min(index + 1, Curve.Frames.Length - 1)];
            if (frame.ControlPoints != null && frame.ControlPoints.Length > 0)
            {
                int totalPoints = 2 + frame.ControlPoints.Length;
                Span<float> pts = stackalloc float[totalPoints];
                pts[0] = frame.Value;
                for (int i = 0; i < frame.ControlPoints.Length; i++)
                {
                    pts[i + 1] = frame.ControlPoints[i].Y;
                }

                pts[totalPoints - 1] = nextFrame.Value;

                int seg = totalPoints - 1;
                float sT = t * seg;
                int segI = Math.Min((int)sT, seg - 1);
                float localT = sT - segI;

                float p0 = segI == 0 ? pts[segI] : pts[segI - 1];
                float p1 = pts[segI];
                float p2 = pts[segI + 1];
                float p3 = segI + 2 < totalPoints ? pts[segI + 2] : pts[segI + 1];

                return CatmullRomInterpolate(p0, p1, p2, p3, localT);
            }

            float pt0 = index == 0 ? frame.Value : Curve.Frames[index - 1].Value;
            float pt1 = frame.Value;
            float pt2 = nextFrame.Value;
            float pt3 = index + 2 < Curve.Frames.Length ? Curve.Frames[index + 2].Value : nextFrame.Value;
            return CatmullRomInterpolate(pt0, pt1, pt2, pt3, t);
        }

        private static float CatmullRomInterpolate(float p0, float p1, float p2, float p3, float t)
        {
            float tSquare = t * t, tCube = tSquare * t;
            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * tSquare +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * tCube);
        }
    }

    public struct KeyFrames
    {
        public KeyFrame[] Frames = Array.Empty<KeyFrame>();

        public KeyFrames()
        {
        }
    }

    public struct KeyFrame
    {
        public uint Index;
        public float Time;
        public float Value;
        public CurveInterpolationType InterpolationType;
        public ControlPoint[] ControlPoints;

        public KeyFrame()
        {
            Index = 0;
            Time = 0f;
            Value = 0f;
            InterpolationType = CurveInterpolationType.Linear;
            ControlPoints = Array.Empty<ControlPoint>();
        }

        public void AddControlPoint(float x, float y)
        {
            y = Math.Clamp(y, VelocityCurve.MinY, VelocityCurve.MaxY);

            var newPoints = new ControlPoint[(ControlPoints?.Length ?? 0) + 1];
            ControlPoints?.CopyTo(newPoints, 0);
            newPoints[^1] = new ControlPoint(x, y);
            ControlPoints = newPoints;
        }

        public void ClearControlPoints()
        {
            ControlPoints = Array.Empty<ControlPoint>();
        }
    }

    public struct ControlPoint
    {
        public float X, Y;

        public ControlPoint(float x, float y)
        {
            X = x;
            Y = Math.Clamp(y, VelocityCurve.MinY, VelocityCurve.MaxY);
        }
    }

    public enum CurveInterpolationType
    {
        Linear = 0,
        Bezier = 1,
        CatmullRom = 2,
        Step = 3
    }
}
