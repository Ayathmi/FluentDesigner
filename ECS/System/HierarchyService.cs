using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace FluentDesigner.ECS.System;

public sealed class HierarchyService : Service
{
    private EcsWorldService _ecsWorld;
    private TextureService _textureService;
    private CameraService _cameraService;
    //private PlaybackService _playbackService;
    private bool _isPlaybackMode;
    private int _maxParallelism;

    public Dictionary<int, HierarchyNode> NodeMapping = new();
    public List<int> Roots = new();
    public List<int> SelectedEntitiesId = new();
    public event EventHandler<HierarchyChangedEventArgs> HierarchyChanged;
    public event EventHandler<List<int>> SelectedEntitiesChanged;

    public static int GetOptimalParallelism()
    {
        int processorCount = Environment.ProcessorCount;
        return processorCount switch
        {
            <= 4 => 2,
            <= 8 => 4,
            _ => 8
        };
    }

    public void SetPlaybackMode(bool isPlaying) => _isPlaybackMode = isPlaying;

    public override void Initialize()
    {
        base.Initialize();
        _ecsWorld = GetFramework().GetService<EcsWorldService>();
        _textureService = GetFramework().GetService<TextureService>();
        _cameraService = GetFramework().GetService<CameraService>();
        //_playbackService = GetFramework().GetService<PlaybackService>();
        _maxParallelism = GetOptimalParallelism();
    }

    public static Scale GetDefaultScaleForNoteType(NoteTypeEnum type) => type switch
    {
        NoteTypeEnum.Rail => new Scale { X = 1f, Y = 1f, Z = 1f },
        NoteTypeEnum.RotateL or NoteTypeEnum.RotateR => new Scale { X = 7f, Y = 7f, Z = 1f },
        NoteTypeEnum.Guiding => new Scale { X = 0.2f, Y = 0.2f, Z = 1f },
        NoteTypeEnum.Mine => new Scale { X = 1.2f, Y = 1.2f, Z = 1f },
        NoteTypeEnum.Catch => new Scale { X = 1.2f, Y = 1.2f, Z = 1f },
        _ => new Scale { X = 2.5f, Y = 2.5f, Z = 1f }
    };

    public int CreateNode(string name, int index, NoteTypeEnum type, int parentId = -1, bool isGroup = false)
    {
        var id = _ecsWorld.CreateEntity();
        var trans = _ecsWorld.AddComponent<Transform>(id);
        trans.Position = new Position { X = 0, Y = 0, Z = 0 };
        trans.Rotation = new Rotation { X = 0, Y = 0, Z = 0, W = 1 };
        trans.Parents = -1;
        trans.Scale = GetDefaultScaleForNoteType(type);
        var mesh = _ecsWorld.AddComponent<MeshRenderer>(id);
        mesh.Type = type;
        mesh.Visibility = true;
        mesh.Color = new Vector4(1, 1, 1, 1);

        if (_textureService != null)
        {
            mesh.TextureIndex = _textureService.GetTextureIndexForNoteType(type);
        }

        var node = new HierarchyNode
        {
            EntityId = id,
            Name = name,
            IsGroup = isGroup,
            IsExpanded = true,
            IsVisible = true,
            NoteType = type
        };

        NodeMapping[id] = node;
        _ecsWorld.AddComponent<Timing>(id);
        if (isGroup)
        {
            _ecsWorld.AddComponent<Description>(id);
        }

        if (parentId != -1)
        {
            SetParent(id, parentId, index);
        }
        else
        {
            Roots.Insert(Math.Clamp(index, 0, Roots.Count), id);
        }

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Added,
            EntityId = id,
            OldParentId = -1,
            NewParnetId = parentId
        });

        return id;
    }

    public bool DeleteNode(int id)
    {
        if (!NodeMapping.TryGetValue(id, out var node))
        {
            return false;
        }

        var trans = _ecsWorld.GetComponent<Transform>(id);
        if (trans == null)
        {
            return false;
        }

        if (node.IsGroup && trans.Children.Count > 0)
        {
            var childrenCopy = new List<int>(trans.Children);
            foreach (var childId in childrenCopy)
            {
                DeleteNode(childId);
            }
        }

        var old = trans.Parents;
        if (old == -1)
        {
            Roots.Remove(id);
        }
        else
        {
            var pTrans = _ecsWorld.GetComponent<Transform>(old);
            pTrans?.Children.Remove(id);
        }

        if (SelectedEntitiesId.Remove(id))
        {
            SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntitiesId]);
        }

        _ecsWorld.DestroyEntity(id);
        NodeMapping.Remove(id);

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Removed,
            EntityId = id,
            OldParentId = old,
            NewParnetId = -1
        });

        return true;
    }

    public int DeleteNodes(IEnumerable<int> ids)
    {
        var idsToDelete = ids.ToList();
        var filteredIds = new List<int>();
        foreach (var id in idsToDelete)
        {
            bool isChildOfAnother = false;
            foreach (var otherId in idsToDelete)
            {
                if (id != otherId && IsDescendantOf(id, otherId))
                {
                    isChildOfAnother = true;
                    break;
                }
            }
            if (!isChildOfAnother)
            {
                filteredIds.Add(id);
            }
        }

        int deletedCount = 0;
        foreach (var id in filteredIds)
        {
            if (DeleteNode(id))
            {
                deletedCount++;
            }
        }

        return deletedCount;
    }

    public int DeleteSelectedNodes()
    {
        var selectedCopy = new List<int>(SelectedEntitiesId);
        return DeleteNodes(selectedCopy);
    }

    public List<int> SaveSelectionState()
    {
        return [.. SelectedEntitiesId];
    }

    public void RestoreSelectionState(List<int> savedSelection, bool validateExists = true)
    {
        if (savedSelection == null)
        {
            ClearSelection();
            return;
        }

        SelectedEntitiesId.Clear();

        foreach (var id in savedSelection)
        {
            if (validateExists && !NodeMapping.ContainsKey(id))
            {
                continue;
            }

            SelectedEntitiesId.Add(id);
        }

        SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntitiesId]);
    }

    public void ClearSelection()
    {
        if (SelectedEntitiesId.Count == 0)
        {
            return;
        }

        SelectedEntitiesId.Clear();
        SelectedEntitiesChanged?.Invoke(this, []);
    }

    public EditorSelectionSnapshot SaveEditorSnapshot()
    {
        var snapshot = new EditorSelectionSnapshot
        {
            SelectedIds = [.. SelectedEntitiesId],
            ExpandedIds = []
        };

        // 保存所有展开的组节点
        foreach (var kvp in NodeMapping)
        {
            if (kvp.Value.IsGroup && kvp.Value.IsExpanded)
            {
                snapshot.ExpandedIds.Add(kvp.Key);
            }
        }

        return snapshot;
    }

    public void RestoreEditorSnapshot(EditorSelectionSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        // 恢复展开状态
        foreach (var kvp in NodeMapping)
        {
            if (kvp.Value.IsGroup)
            {
                kvp.Value.IsExpanded = snapshot.ExpandedIds.Contains(kvp.Key);
            }
        }

        // 恢复选中状态
        RestoreSelectionState(snapshot.SelectedIds, validateExists: true);
    }

    private bool IsDescendantOf(int descendantId, int ancestorId)
    {
        if (!NodeMapping.TryGetValue(ancestorId, out var ancestorNode))
        {
            return false;
        }

        if (!ancestorNode.IsGroup)
        {
            return false;
        }

        var trans = _ecsWorld.GetComponent<Transform>(ancestorId);
        if (trans == null)
        {
            return false;
        }

        foreach (var childId in trans.Children)
        {
            if (childId == descendantId)
            {
                return true;
            }

            if (IsDescendantOf(descendantId, childId))
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateNoteType(int id, NoteTypeEnum newType)
    {
        var mesh = _ecsWorld.GetComponent<MeshRenderer>(id);
        var trans = _ecsWorld.GetComponent<Transform>(id);

        if (mesh == null || trans == null)
        {
            return;
        }

        mesh.Type = newType;
        if (_textureService != null)
        {
            mesh.TextureIndex = _textureService.GetTextureIndexForNoteType(newType);
        }

        if (NodeMapping.TryGetValue(id, out var node))
        {
            node.NoteType = newType;
        }

        trans.Scale = GetDefaultScaleForNoteType(newType);

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.PropertyChanged,
            EntityId = id,
            OldParentId = GetParent(id),
            NewParnetId = GetParent(id)
        });
    }

    public int DuplicateNode(int target, bool isGroup, int targetParent = -1)
    {
        if (!NodeMapping.TryGetValue(target, out var sourceNode))
        {
            return -1;
        }

        var newId = _ecsWorld.CreateEntity();
        var newTransform = _ecsWorld.AddComponent<Transform>(newId);
        var copiedTransform = _ecsWorld.GetComponent<Transform>(target);

        newTransform.Position = new Position
        {
            X = copiedTransform.Position.X,
            Y = copiedTransform.Position.Y,
            Z = copiedTransform.Position.Z
        };
        newTransform.Rotation = new Rotation
        {
            W = copiedTransform.Rotation.W,
            X = copiedTransform.Rotation.X,
            Y = copiedTransform.Rotation.Y,
            Z = copiedTransform.Rotation.Z
        };
        newTransform.Scale = new Scale
        {
            X = copiedTransform.Scale.X,
            Y = copiedTransform.Scale.Y,
            Z = copiedTransform.Scale.Z
        };
        newTransform.Radius = copiedTransform.Radius;
        newTransform.Parents = -1;
        newTransform.Index = new RailIndex
        {
            Index = copiedTransform.Index.Index
        };

        var sourceMesh = _ecsWorld.GetComponent<MeshRenderer>(target);
        if (sourceMesh != null)
        {
            var newMesh = _ecsWorld.AddComponent<MeshRenderer>(newId);
            newMesh.Type = sourceMesh.Type;
            newMesh.Visibility = sourceMesh.Visibility;
            newMesh.Color = sourceMesh.Color;
            newMesh.TextureIndex = sourceMesh.TextureIndex;
            newMesh.MaterialId = sourceMesh.MaterialId;
            newMesh.SortingLayer = sourceMesh.SortingLayer;
            newMesh.OrderInLayer = sourceMesh.OrderInLayer;
        }

        var sourceTiming = _ecsWorld.GetComponent<Timing>(target);
        if (sourceTiming != null)
        {
            var newTiming = _ecsWorld.AddComponent<Timing>(newId);
            newTiming.TargetMin = sourceTiming.TargetMin;
            newTiming.TargetSecond = sourceTiming.TargetSecond;
            newTiming.TargetTick = sourceTiming.TargetTick;
        }

        var sourceDescription = _ecsWorld.GetComponent<Description>(target);
        if (sourceDescription != null)
        {
            var newDescription = _ecsWorld.AddComponent<Description>(newId);
            newDescription.Text = sourceDescription.Text;
        }

        var sourceVelocityCurve = _ecsWorld.GetComponent<VelocityCurve>(target);
        if (sourceVelocityCurve != null)
        {
            var newVelocityCurve = _ecsWorld.AddComponent<VelocityCurve>(newId);
            if (sourceVelocityCurve.Curve.Frames != null && sourceVelocityCurve.Curve.Frames.Length > 0)
            {
                var newFrames = new KeyFrame[sourceVelocityCurve.Curve.Frames.Length];
                for (int i = 0; i < sourceVelocityCurve.Curve.Frames.Length; i++)
                {
                    var srcFrame = sourceVelocityCurve.Curve.Frames[i];
                    newFrames[i] = new KeyFrame
                    {
                        Index = srcFrame.Index,
                        Time = srcFrame.Time,
                        Value = srcFrame.Value,
                        InterpolationType = srcFrame.InterpolationType,
                        ControlPoints = srcFrame.ControlPoints != null
                            ? (ControlPoint[])srcFrame.ControlPoints.Clone()
                            : Array.Empty<ControlPoint>()
                    };
                }
                newVelocityCurve.Curve = new KeyFrames { Frames = newFrames };
            }
        }

        var newNode = new HierarchyNode
        {
            EntityId = newId,
            Name = sourceNode.Name + " (Copy)",
            IsGroup = sourceNode.IsGroup,
            IsExpanded = sourceNode.IsExpanded,
            IsVisible = sourceNode.IsVisible,
            NoteType = sourceNode.NoteType
        };
        NodeMapping[newId] = newNode;

        var parentId = targetParent != -1 ? targetParent : copiedTransform.Parents;
        if (parentId == -1)
        {
            Roots.Add(newId);
        }
        else
        {
            SetParent(newId, parentId, GetChildCount(parentId));
        }

        if (isGroup && sourceNode.IsGroup)
        {
            var childrenCopy = new List<int>(copiedTransform.Children);
            foreach (var child in copiedTransform.Children)
            {
                if (NodeMapping.TryGetValue(child, out var childNode))
                {
                    DuplicateNode(child, childNode.IsGroup, newId);
                }
            }
        }

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Added,
            EntityId = newId,
            OldParentId = -1,
            NewParnetId = parentId
        });

        return newId;
    }

    public async UniTask<List<int>> DuplicateNodesParallelAsync(IEnumerable<int> targetIds, int targetParent = -1, CancellationToken cancellationToken = default)
    {
        var targets = targetIds.ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        var flattenedNodes = new List<FlattenedNode>();
        foreach (var targetId in targets)
        {
            if (NodeMapping.TryGetValue(targetId, out var node))
            {
                FlattenNodeTree(targetId, node, flattenedNodes, 0, -1);
            }
        }

        if (flattenedNodes.Count == 0)
        {
            return [];
        }

        var nodesByDepth = flattenedNodes
            .GroupBy(n => n.Depth)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

        var idMapping = new ConcurrentDictionary<int, int>();
        var createdRootIds = new List<int>();

        foreach (var kvp in nodesByDepth.OrderBy(k => k.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodesAtDepth = kvp.Value;
            var newIds = _ecsWorld.CreateEntities(nodesAtDepth.Count);
            var tasks = new List<UniTask>();
            int batchSize = Math.Max(1, nodesAtDepth.Count / _maxParallelism);

            for (int batchStart = 0; batchStart < nodesAtDepth.Count; batchStart += batchSize)
            {
                int start = batchStart;
                int end = Math.Min(batchStart + batchSize, nodesAtDepth.Count);

                tasks.Add(UniTask.Run(() =>
                {
                    for (int i = start; i < end; i++)
                    {
                        var flatNode = nodesAtDepth[i];
                        var newId = newIds[i];

                        CopyEntityComponents(flatNode.SourceId, newId, flatNode.Node);
                        idMapping[flatNode.SourceId] = newId;
                    }
                }));
            }

            await UniTask.WhenAll(tasks);
            for (int i = 0; i < nodesAtDepth.Count; i++)
            {
                var flatNode = nodesAtDepth[i];
                var newId = newIds[i];

                var newNode = new HierarchyNode
                {
                    EntityId = newId,
                    Name = flatNode.Node.Name + " (Copy)",
                    IsGroup = flatNode.Node.IsGroup,
                    IsExpanded = flatNode.Node.IsExpanded,
                    IsVisible = flatNode.Node.IsVisible,
                    NoteType = flatNode.Node.NoteType
                };
                NodeMapping[newId] = newNode;

                int parentId;
                if (flatNode.Depth == 0)
                {
                    parentId = targetParent;
                    createdRootIds.Add(newId);
                }
                else
                {
                    parentId = idMapping.TryGetValue(flatNode.ParentSourceId, out var mappedParentId) ? mappedParentId : -1;
                }

                var newTransform = _ecsWorld.GetComponent<Transform>(newId);
                if (newTransform != null)
                {
                    newTransform.Parents = parentId;

                    if (parentId == -1)
                    {
                        Roots.Add(newId);
                    }
                    else
                    {
                        var parentTransform = _ecsWorld.GetComponent<Transform>(parentId);
                        parentTransform?.Children.Add(newId);
                    }
                }
            }
        }

        foreach (var newId in createdRootIds)
        {
            HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
            {
                ChangedType = HierarchyChangedType.Added,
                EntityId = newId,
                OldParentId = -1,
                NewParnetId = targetParent
            });
        }

        return createdRootIds;
    }

    private void CopyEntityComponents(int sourceId, int newId, HierarchyNode sourceNode)
    {
        var copiedTransform = _ecsWorld.GetComponent<Transform>(sourceId);

        // Transform 组件
        var newTransform = _ecsWorld.AddComponent<Transform>(newId);
        newTransform.Position = new Position
        {
            X = copiedTransform.Position.X,
            Y = copiedTransform.Position.Y,
            Z = copiedTransform.Position.Z
        };
        newTransform.Rotation = new Rotation
        {
            W = copiedTransform.Rotation.W,
            X = copiedTransform.Rotation.X,
            Y = copiedTransform.Rotation.Y,
            Z = copiedTransform.Rotation.Z
        };
        newTransform.Scale = new Scale
        {
            X = copiedTransform.Scale.X,
            Y = copiedTransform.Scale.Y,
            Z = copiedTransform.Scale.Z
        };
        newTransform.Index = new RailIndex { Index = copiedTransform.Index.Index };
        newTransform.Parents = -1;

        var sourceMesh = _ecsWorld.GetComponent<MeshRenderer>(sourceId);
        if (sourceMesh != null)
        {
            var newMesh = _ecsWorld.AddComponent<MeshRenderer>(newId);
            newMesh.Type = sourceMesh.Type;
            newMesh.Visibility = sourceMesh.Visibility;
            newMesh.Color = sourceMesh.Color;
            newMesh.TextureIndex = sourceMesh.TextureIndex;
            newMesh.MaterialId = sourceMesh.MaterialId;
            newMesh.SortingLayer = sourceMesh.SortingLayer;
            newMesh.OrderInLayer = sourceMesh.OrderInLayer;
        }

        var sourceTiming = _ecsWorld.GetComponent<Timing>(sourceId);
        if (sourceTiming != null)
        {
            var newTiming = _ecsWorld.AddComponent<Timing>(newId);
            newTiming.TargetMin = sourceTiming.TargetMin;
            newTiming.TargetSecond = sourceTiming.TargetSecond;
            newTiming.TargetTick = sourceTiming.TargetTick;
        }

        var sourceDescription = _ecsWorld.GetComponent<Description>(sourceId);
        if (sourceDescription != null)
        {
            var newDescription = _ecsWorld.AddComponent<Description>(newId);
            newDescription.Text = sourceDescription.Text;
        }

        var sourceVelocityCurve = _ecsWorld.GetComponent<VelocityCurve>(sourceId);
        if (sourceVelocityCurve != null)
        {
            var newVelocityCurve = _ecsWorld.AddComponent<VelocityCurve>(newId);
            if (sourceVelocityCurve.Curve.Frames != null && sourceVelocityCurve.Curve.Frames.Length > 0)
            {
                var newFrames = new KeyFrame[sourceVelocityCurve.Curve.Frames.Length];
                for (int i = 0; i < sourceVelocityCurve.Curve.Frames.Length; i++)
                {
                    var srcFrame = sourceVelocityCurve.Curve.Frames[i];
                    newFrames[i] = new KeyFrame
                    {
                        Index = srcFrame.Index,
                        Time = srcFrame.Time,
                        Value = srcFrame.Value,
                        InterpolationType = srcFrame.InterpolationType,
                        ControlPoints = srcFrame.ControlPoints != null
                            ? (ControlPoint[])srcFrame.ControlPoints.Clone()
                            : Array.Empty<ControlPoint>()
                    };
                }
                newVelocityCurve.Curve = new KeyFrames { Frames = newFrames };
            }
        }
    }


    public async UniTask<int> DuplicateNodeParallelAsync(int target, bool isGroup, int targetParent = -1, CancellationToken cancellationToken = default)
    {
        var result = await DuplicateNodesParallelAsync([target], targetParent, cancellationToken);
        return result.Count > 0 ? result[0] : -1;
    }

    private void FlattenNodeTree(
        int sourceId,
        HierarchyNode node,
        List<FlattenedNode> result,
        int depth,
        int parentSourceId)
    {
        result.Add(new FlattenedNode
        {
            SourceId = sourceId,
            Node = node,
            Depth = depth,
            ParentSourceId = parentSourceId
        });

        if (node.IsGroup)
        {
            var transform = _ecsWorld.GetComponent<Transform>(sourceId);
            if (transform?.Children != null)
            {
                foreach (var childId in transform.Children)
                {
                    if (NodeMapping.TryGetValue(childId, out var childNode))
                    {
                        FlattenNodeTree(childId, childNode, result, depth + 1, sourceId);
                    }
                }
            }
        }
    }

    private int GetChildCount(int parentId)
    {
        if (parentId == -1)
        {
            return Roots.Count;
        }

        var pTrans = _ecsWorld.GetComponent<Transform>(parentId);
        return pTrans?.Children.Count ?? 0;
    }

    public bool RenameNode(string name, int id)
    {
        if (!NodeMapping.TryGetValue(id, out var node))
        {
            return false;
        }

        node.Name = name;

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Renamed,
            EntityId = id,
            OldParentId = GetParent(id),
            NewParnetId = GetParent(id)
        });

        return true;
    }

    public bool SetParent(int id, int parent, int index)
    {
        if (id == -1)
        {
            return false;
        }

        var trans = _ecsWorld.GetComponent<Transform>(id);
        if (trans == null)
        {
            return false;
        }

        if (parent != -1)
        {
            if (!NodeMapping.TryGetValue(parent, out var parentNode) || !parentNode.IsGroup)
            {
                return false;
            }

            if (_ecsWorld.GetComponent<Transform>(parent) == null)
            {
                return false;
            }

            if (IsAncestorOf(id, parent))
            {
                return false;
            }
        }

        var oldParent = trans.Parents;
        if (oldParent == -1)
        {
            Roots.Remove(id);
        }
        else
        {
            var oldPTrans = _ecsWorld.GetComponent<Transform>(oldParent);
            oldPTrans?.Children.Remove(id);
        }
        trans.Parents = parent;
        if (parent == -1)
        {
            var insertIndex = Math.Clamp(index, 0, Roots.Count);
            Roots.Insert(insertIndex, id);
        }
        else
        {
            var newPTrans = _ecsWorld.GetComponent<Transform>(parent);
            var insertIndex = Math.Clamp(index, 0, newPTrans.Children.Count);
            newPTrans.Children.Insert(insertIndex, id);
        }

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Moved,
            EntityId = id,
            OldParentId = oldParent,
            NewParnetId = parent
        });

        return true;
    }

    private bool IsAncestorOf(int ancestorId, int descendantId)
    {
        var current = descendantId;
        while (current != -1)
        {
            if (current == ancestorId)
            {
                return true;
            }

            current = GetParent(current);
        }
        return false;
    }

    public void NotifyHierarchyRefresh()
    {
        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.Refreshed,
            EntityId = -1,
            OldParentId = -1,
            NewParnetId = -1
        });
    }

    public int GetParent(int id)
    {
        var trans = _ecsWorld.GetComponent<Transform>(id);
        if (trans == null)
        {
            return -1;
        }

        if (trans.Parents == null || trans.Parents == -1)
        {
            return -1;
        }

        return trans.Parents;
    }

    public int GetDepth(int id)
    {
        var node = _ecsWorld.GetComponent<Transform>(id);
        var depth = 1;
        var parent = node.Parents;
        while (!Roots.Contains(parent))
        {
            parent = _ecsWorld.GetComponent<Transform>(parent).Parents;
            depth++;
        }

        return depth;
    }

    public void CalculateGroupTiming(int groupId)
    {
        if (!NodeMapping.TryGetValue(groupId, out var node) || !node.IsGroup)
        {
            return;
        }

        var groupTiming = _ecsWorld.GetComponent<Timing>(groupId);
        if (groupTiming == null)
        {
            return;
        }

        var trans = _ecsWorld.GetComponent<Transform>(groupId);
        if (trans == null || trans.Children.Count == 0)
        {
            groupTiming.TargetMin = 0;
            groupTiming.TargetSecond = 0;
            groupTiming.TargetTick = 0;
            return;
        }
        float latestTarget = float.MinValue;
        CollectTimingFromChildren(trans.Children, ref latestTarget);

        if (latestTarget != float.MinValue)
        {
            ConvertSecondsToTiming(latestTarget, out groupTiming.TargetMin, out groupTiming.TargetSecond, out groupTiming.TargetTick);
        }
    }


    private void CollectTimingFromChildren(List<int> children, ref float latestTarget)
    {
        foreach (var childId in children)
        {
            var childTiming = _ecsWorld.GetComponent<Timing>(childId);
            if (childTiming != null)
            {
                float targetTotal = ConvertTimingToSeconds(childTiming.TargetMin, childTiming.TargetSecond,
                    childTiming.TargetTick);

                if (targetTotal > latestTarget)
                {
                    latestTarget = targetTotal;
                }
            }

            if (NodeMapping.TryGetValue(childId, out var childNode) && childNode.IsGroup)
            {
                var childTrans = _ecsWorld.GetComponent<Transform>(childId);
                if (childTrans != null && childTrans.Children.Count > 0)
                {
                    CollectTimingFromChildren(childTrans.Children, ref latestTarget);
                }
            }
        }
    }


    private static float ConvertTimingToSeconds(float min, float second, float tick)
    {
        return min * 60f + second + tick / 1000f;
    }

    private static void ConvertSecondsToTiming(float totalSeconds, out float min, out float second, out float tick)
    {
        min = MathF.Floor(totalSeconds / 60f);
        float remaining = totalSeconds - min * 60f;
        second = MathF.Floor(remaining);
        tick = (remaining - second) * 1000f;
    }
    public void AddSelect(int id)
    {
        if (_ecsWorld.GetComponent<Transform>(id) == null)
        {
            return;
        }

        if (!SelectedEntitiesId.Contains(id))
        {
            SelectedEntitiesId.Add(id);
            SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntitiesId]);
        }
    }

    public void RemoveSelect(int id)
    {
        if (_ecsWorld.GetComponent<Transform>(id) == null)
        {
            return;
        }

        if (SelectedEntitiesId.Remove(id))
        {
            SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntitiesId]);
        }
    }

    public void SetVisibility(int id, bool value)
    {
        if (_ecsWorld.GetComponent<Transform>(id) == null)
        {
            return;
        }

        if (!NodeMapping.TryGetValue(id, out var node))
        {
            return;
        }

        if (node.IsVisible == value)
        {
            return;
        }

        node.IsVisible = value;
        var mesh = _ecsWorld.GetComponent<MeshRenderer>(id);
        if (mesh != null)
        {
            mesh.Visibility = value;
        }

        if (node.IsGroup)
        {
            var trans = _ecsWorld.GetComponent<Transform>(id);
            if (trans != null)
            {
                SetChildrenVisibility(trans.Children, value);
            }
        }
        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.VisibilityChanged,
            EntityId = id,
            OldParentId = GetParent(id),
            NewParnetId = GetParent(id)
        });
    }

    private void SetChildrenVisibility(List<int> children, bool value)
    {
        foreach (var childId in children)
        {
            if (!NodeMapping.TryGetValue(childId, out var childNode))
            {
                continue;
            }

            childNode.IsVisible = value;
            var childMesh = _ecsWorld.GetComponent<MeshRenderer>(childId);
            if (childMesh != null)
            {
                childMesh.Visibility = value;
            }

            if (childNode.IsGroup)
            {
                var childTrans = _ecsWorld.GetComponent<Transform>(childId);
                if (childTrans != null && childTrans.Children.Count > 0)
                {
                    SetChildrenVisibility(childTrans.Children, value);
                }
            }
        }
    }

    public bool GetVisibility(int id)
    {
        if (_ecsWorld.GetComponent<Transform>(id) != null && NodeMapping.TryGetValue(id, out var value))
        {
            return value.IsVisible;
        }

        return false;
    }

    public void SetFocus(int id)
    {

        if (_isPlaybackMode)
        {
            return;
        }

        if (!NodeMapping.TryGetValue(id, out var node))
        {
            return;
        }

        if (node.IsGroup)
        {
            return;
        }

        var transform = _ecsWorld.GetComponent<Transform>(id);
        if (transform == null)
        {
            return;
        }

        var targetPosition = new Vector3(
            transform.Position.X,
            transform.Position.Y,
            transform.Position.Z
        );

        _cameraService?.AnimateTo(targetPosition, 15f, 45f, 30f);
    }

    public void SetExpanded(int id, bool value)
    {
        if (_ecsWorld.GetComponent<Transform>(id) == null)
        {
            return;
        }

        if (!NodeMapping.TryGetValue(id, out var node))
        {
            return;
        }

        if (!node.IsGroup || node.IsExpanded == value)
        {
            return;
        }

        node.IsExpanded = value;

        HierarchyChanged?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.ExpandedChanged,
            EntityId = id,
            OldParentId = GetParent(id),
            NewParnetId = GetParent(id)
        });
    }

    public override void Shutdown()
    {
        NodeMapping.Clear();
        Roots.Clear();
        base.Shutdown();
    }

    private sealed class FlattenedNode
    {
        public int SourceId { get; set; }
        public HierarchyNode Node { get; set; }
        public int Depth { get; set; }
        public int ParentSourceId { get; set; }
    }
}

public class HierarchyNode
{
    public int EntityId { get; set; }
    public string Name { get; set; }
    public bool IsGroup { get; init; }
    public bool IsExpanded { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public List<int> Depth { get; set; } = new();
    public NoteTypeEnum NoteType { get; set; }
}

public class HierarchyChangedEventArgs : EventArgs
{
    public HierarchyChangedType ChangedType { get; set; }
    public int EntityId { get; set; }
    public int OldParentId { get; set; }
    public int NewParnetId { get; set; }
}

public enum HierarchyChangedType
{
    Added,
    Removed,
    Moved,
    Renamed,
    VisibilityChanged,
    ExpandedChanged,
    PropertyChanged,
    Refreshed
}

public sealed class EditorSelectionSnapshot
{
    public List<int> SelectedIds { get; set; } = [];
    public List<int> ExpandedIds { get; set; } = [];
}