using FluentDesigner.Control;
using FluentDesigner.ECS.Components;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentDesigner.ECS.System;

public sealed class MultiSelectController
{
    private readonly IAyaMvcFramework _framework;
    private readonly HierarchyService _hierarchyService;
    private readonly EcsWorldService _ecsWorld;
    private readonly SceneLayoutService _sceneLayoutService;
    private readonly InspectorService _inspectorService;

    public MultiSelectController(IAyaMvcFramework framework)
    {
        _framework = framework;
        _hierarchyService = framework.GetService<HierarchyService>();
        _ecsWorld = framework.GetService<EcsWorldService>();
        _sceneLayoutService = framework.GetService<SceneLayoutService>();
        _inspectorService = framework.GetService<InspectorService>();
    }

    private static long TimingToTotalMs(Timing timing)
    {
        return (long)timing.TargetMin * 60000L
               + (long)timing.TargetSecond * 1000L
               + (long)timing.TargetTick;
    }

    public List<NoteRailData> GetSelectedNotesWithRailIndex()
    {
        var result = new List<NoteRailData>();
        var selectedIds = _hierarchyService.SelectedEntitiesId.ToList();

        foreach (var id in selectedIds)
        {
            if (!_hierarchyService.NodeMapping.TryGetValue(id, out var node))
            {
                continue;
            }

            if (node.IsGroup)
            {
                continue;
            }

            var timing = _ecsWorld.GetComponent<Timing>(id);
            var transform = _ecsWorld.GetComponent<Transform>(id);
            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(id);

            if (timing == null || transform == null)
            {
                continue;
            }

            bool isRotate = meshRenderer?.Type is NoteTypeEnum.RotateL or NoteTypeEnum.RotateR;

            result.Add(new NoteRailData
            {
                EntityId = id,
                Name = node.Name,
                TimeMs = TimingToTotalMs(timing),
                CurrentRailIndex = transform.Index.Index,
                IsRotateType = isRotate
            });
        }

        return result;
    }

    public void ApplyRailIndexChanges(List<NoteRailData> noteData)
    {
        foreach (var note in noteData)
        {
            var transform = _ecsWorld.GetComponent<Transform>(note.EntityId);
            if (transform == null)
            {
                continue;
            }

            int newIndex = (int)Math.Clamp(note.CurrentRailIndex, 0, 359);
            transform.Index = new RailIndex { Index = newIndex };

            _sceneLayoutService?.OnRailIndexUpdated(note.EntityId);
        }

        NotifyTimingChanged();
    }

    private void SetTimingFromTotalMs(Timing timing, long totalMs)
    {
        totalMs = Math.Max(0, totalMs);

        timing.TargetMin = (int)(totalMs / 60000L);
        timing.TargetSecond = (int)((totalMs % 60000L) / 1000L);
        timing.TargetTick = (int)(totalMs % 1000L);
    }

    public void NotifyTimingChanged()
    {
        _inspectorService?.NotifyPropertyChanged("Timing", "TargetTime", null, null);
    }

    public MultiSelectInfo GetSelectionInfo()
    {
        var selectedIds = _hierarchyService.SelectedEntitiesId;
        var info = new MultiSelectInfo();

        float minTime = float.MaxValue;
        float maxTime = float.MinValue;

        foreach (var id in selectedIds)
        {
            if (!_hierarchyService.NodeMapping.TryGetValue(id, out var node))
            {
                continue;
            }

            if (node.IsGroup)
            {
                info.GroupCount++;
            }
            else
            {
                info.NoteCount++;
            }

            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing != null)
            {
                float totalSeconds = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;

                if (totalSeconds < minTime)
                {
                    minTime = totalSeconds;
                    info.StartMin = timing.TargetMin;
                    info.StartSecond = timing.TargetSecond;
                    info.StartTick = timing.TargetTick;
                    info.FirstEntityId = id;
                }

                if (totalSeconds > maxTime)
                {
                    maxTime = totalSeconds;
                    info.EndMin = timing.TargetMin;
                    info.EndSecond = timing.TargetSecond;
                    info.EndTick = timing.TargetTick;
                    info.LastEntityId = id;
                }
            }
        }

        if (minTime == float.MaxValue)
        {
            info.StartMin = info.StartSecond = info.StartTick = 0;
            info.EndMin = info.EndSecond = info.EndTick = 0;
        }

        return info;
    }

    public void OffsetAllTargetTime(int minOffset, int secOffset, int tickOffset)
    {
        var selectedIds = _hierarchyService.SelectedEntitiesId.ToList();
        long offsetMs = minOffset * 60000L + secOffset * 1000L + tickOffset;

        foreach (var id in selectedIds)
        {
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing == null)
            {
                continue;
            }

            long currentMs = TimingToTotalMs(timing);
            long newMs = currentMs + offsetMs;

            SetTimingFromTotalMs(timing, newMs);
            _sceneLayoutService?.OnTimingUpdated(id);
        }
        NotifyTimingChanged();
    }

    public void SetStartTime(float min, float sec, float tick)
    {
        var info = GetSelectionInfo();
        if (info.FirstEntityId == -1)
        {
            return;
        }

        float currentStartTotal = info.StartMin * 60f + info.StartSecond + info.StartTick / 1000f;
        float newStartTotal = min * 60f + sec + tick / 1000f;
        float offset = newStartTotal - currentStartTotal;

        var selectedIds = _hierarchyService.SelectedEntitiesId.ToList();

        foreach (var id in selectedIds)
        {
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing == null)
            {
                continue;
            }

            float currentTotal = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;
            float newTotal = Math.Max(0, currentTotal + offset);

            SetTimingFromTotalSeconds(timing, newTotal);
            _sceneLayoutService?.OnTimingUpdated(id);
        }
        NotifyTimingChanged();
    }

    public void SetEndTime(float min, float sec, float tick)
    {
        var info = GetSelectionInfo();
        if (info.LastEntityId == -1)
        {
            return;
        }

        float currentEndTotal = info.EndMin * 60f + info.EndSecond + info.EndTick / 1000f;
        float newEndTotal = min * 60f + sec + tick / 1000f;
        float offset = newEndTotal - currentEndTotal;

        var selectedIds = _hierarchyService.SelectedEntitiesId.ToList();

        foreach (var id in selectedIds)
        {
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing == null)
            {
                continue;
            }

            float currentTotal = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;
            float newTotal = Math.Max(0, currentTotal + offset);

            SetTimingFromTotalSeconds(timing, newTotal);
            _sceneLayoutService?.OnTimingUpdated(id);
        }
        NotifyTimingChanged();
    }

    public void DistributeEvenly(float startMin, float startSec, float startTick,
                                  float endMin, float endSec, float endTick)
    {
        var selectedIds = _hierarchyService.SelectedEntitiesId.ToList();

        var sortedNotes = new List<(int id, float time)>();
        foreach (var id in selectedIds)
        {
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing != null)
            {
                float total = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;
                sortedNotes.Add((id, total));
            }
        }

        sortedNotes = sortedNotes.OrderBy(x => x.time).ToList();

        if (sortedNotes.Count < 2)
        {
            return;
        }

        long startMs = (long)startMin * 60000L + (long)startSec * 1000L + (long)startTick;
        long endMs = (long)endMin * 60000L + (long)endSec * 1000L + (long)endTick;

        long totalRange = endMs - startMs;
        int count = sortedNotes.Count - 1;

        for (int i = 0; i < sortedNotes.Count; i++)
        {
            var (id, _) = sortedNotes[i];
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing == null)
            {
                continue;
            }

            long newMs = startMs + (totalRange * i) / count;
            SetTimingFromTotalMs(timing, newMs);
            _sceneLayoutService?.OnTimingUpdated(id);
        }
        NotifyTimingChanged();
    }

    private static void SetTimingFromTotalSeconds(Timing timing, float totalSeconds)
    {
        int totalMs = (int)(totalSeconds * 1000);
        int minutes = totalMs / 60000;
        int seconds = (totalMs % 60000) / 1000;
        int ticks = totalMs % 1000;

        timing.TargetMin = minutes;
        timing.TargetSecond = seconds;
        timing.TargetTick = ticks;
    }
}

public class MultiSelectInfo
{
    public int NoteCount { get; set; }
    public int GroupCount { get; set; }
    public float StartMin { get; set; }
    public float StartSecond { get; set; }
    public float StartTick { get; set; }
    public float EndMin { get; set; }
    public float EndSecond { get; set; }
    public float EndTick { get; set; }
    public int FirstEntityId { get; set; } = -1;
    public int LastEntityId { get; set; } = -1;
}

public class NoteRailData
{
    public int EntityId { get; set; }
    public string Name { get; set; }
    public long TimeMs { get; set; }
    public float CurrentRailIndex { get; set; }
    public bool IsRotateType { get; set; }
}