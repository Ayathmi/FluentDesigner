using System.Collections.Generic;

namespace FluentDesigner.ECS.Components;

public class Judgement
{
    public JudgeTypeEnum JudgeType { get; private set; }
    public JudgementEnum CurrentState => _stateMachine.CurrentState;

    private readonly JudgementStateMachine _stateMachine = new();
    public bool TryTransit(JudgementEnum state) => _stateMachine.TryTransit(state);
}

public class JudgementStateMachine
{
    public JudgementEnum CurrentState { get; private set; } = JudgementEnum.Unoperable;

    private static readonly Dictionary<JudgementEnum, HashSet<JudgementEnum>> ValidTransitions = new()
    {
        [JudgementEnum.Unoperable] = [
            JudgementEnum.EarlyLost, JudgementEnum.EarlyFaint, JudgementEnum.EarlyPrecise,
            JudgementEnum.Precise,
            JudgementEnum.LatePrecise, JudgementEnum.LateFaint, JudgementEnum.LateLost,
            JudgementEnum.AngleOutOfRange, JudgementEnum.AngleGreaterFaint, JudgementEnum.AngleGreaterPrecise,
            JudgementEnum.AnglePrecise, JudgementEnum.AngleLessPrecise, JudgementEnum.AngleLessFaint,
            JudgementEnum.RotateLessThanThreshold, JudgementEnum.RotateGreaterThanThreshold
        ],

        [JudgementEnum.EarlyLost] = [JudgementEnum.EarlyFaint],
        [JudgementEnum.EarlyFaint] = [JudgementEnum.EarlyPrecise, JudgementEnum.EarlyLost],
        [JudgementEnum.EarlyPrecise] = [JudgementEnum.Precise, JudgementEnum.EarlyFaint],
        [JudgementEnum.Precise] = [JudgementEnum.LatePrecise],
        [JudgementEnum.LatePrecise] = [JudgementEnum.LateFaint],
        [JudgementEnum.LateFaint] = [JudgementEnum.LateLost],
        [JudgementEnum.LateLost] = [],

        [JudgementEnum.AngleOutOfRange] = [JudgementEnum.AngleGreaterFaint, JudgementEnum.AngleLessFaint],
        [JudgementEnum.AngleGreaterFaint] = [JudgementEnum.AngleGreaterPrecise, JudgementEnum.AngleOutOfRange],
        [JudgementEnum.AngleGreaterPrecise] = [JudgementEnum.AnglePrecise, JudgementEnum.AngleGreaterFaint],
        [JudgementEnum.AnglePrecise] = [JudgementEnum.AngleLessPrecise, JudgementEnum.AngleGreaterPrecise],
        [JudgementEnum.AngleLessPrecise] = [JudgementEnum.AngleLessFaint, JudgementEnum.AnglePrecise],
        [JudgementEnum.AngleLessFaint] = [JudgementEnum.AngleOutOfRange, JudgementEnum.AngleLessPrecise],

        [JudgementEnum.RotateLessThanThreshold] = [JudgementEnum.RotateGreaterThanThreshold],
        [JudgementEnum.RotateGreaterThanThreshold] = [JudgementEnum.RotateLessThanThreshold],
    };

    public bool TryTransit(JudgementEnum state)
    {
        if (ValidTransitions.TryGetValue(CurrentState, out var validState))
        {
            CurrentState = state;
            return true;
        }

        return false;
    }

    public bool CanTransit(JudgementEnum state) => ValidTransitions.TryGetValue(CurrentState, out var validState) &&
                                                   validState.Contains(state);

    public void Reset() => CurrentState = JudgementEnum.Unoperable;
}

public enum JudgeTypeEnum
{
    Time = 0,
    Angle = 1,
    Rotation = 2,
}

public enum JudgementEnum
{
    Unoperable = 0,

    EarlyLost = 100,
    EarlyFaint = 101,
    EarlyPrecise = 102,
    Precise = 103,
    LatePrecise = 104,
    LateFaint = 105,
    LateLost = 106,

    AngleOutOfRange = 200,
    AngleGreaterFaint = 201,
    AngleGreaterPrecise = 202,
    AnglePrecise = 203,
    AngleLessPrecise = 204,
    AngleLessFaint = 205,

    RotateLessThanThreshold = 300,
    RotateGreaterThanThreshold = 301,
}