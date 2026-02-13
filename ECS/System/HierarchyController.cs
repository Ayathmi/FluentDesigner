using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FluentDesigner.ECS.System;

public class HierarchyController : IDisposable
{
    private readonly IAyaMvcFramework _framework;
    private readonly HierarchyService _hierarchyService;
    private readonly EcsWorldService _ecsWorld;
    private readonly List<HierarchyNode> _clip = new();
    private readonly SceneLayoutService _sceneLayoutService;
    private const int ParallelThreshold = 64;

    public Command<(string, int, NoteTypeEnum, int, bool)> CreateNodeCommand { get; private set; }
    public Command<int> DeleteNodeCommand { get; private set; }
    public Command DeleteSelectedCommand { get; private set; }
    public Command<(int id, string name)> RenameCommand { get; private set; }
    public Command<(int target, bool isGroup)> DuplicateNodeCommand { get; private set; }
    public Command<(int id, int parent, int index)> MoveCommand { get; private set; }
    public Command<int> AddSelectCommand { get; private set; }
    public Command<int> RemoveSelectCommand { get; private set; }
    public Command<int> ToggleVisibilityCommand { get; private set; }
    public Command<int> FocusCommand { get; private set; }
    public Command CopyCommand { get; private set; }
    public Command<int> PasteCommand { get; private set; }

    public HierarchyController(IAyaMvcFramework framework)
    {
        _framework = framework;
        _hierarchyService = framework.GetService<HierarchyService>();
        _hierarchyService.Initialize();
        _ecsWorld = framework.GetService<EcsWorldService>();
        _sceneLayoutService = framework.GetService<SceneLayoutService>();

        Initialize();
        SubscribeEvents();
    }

    public IReadOnlyList<int> GetRoots() => _hierarchyService.Roots;

    public bool TryGetNode(int id, out HierarchyNode node) => _hierarchyService.NodeMapping.TryGetValue(id, out node);

    public void CreateNoteWithTypeAndTime(NoteTypeEnum noteType, int parentId, float timeSeconds)
    {
        var newName = GenerateUniqueName("New Note");
        var entityId = _hierarchyService.CreateNode(newName, 0, noteType, parentId, false);

        if (_ecsWorld.HasComponent<Timing>(entityId))
        {
            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            var totalMilliseconds = (int)(timeSeconds * 1000);
            var minutes = totalMilliseconds / 60000;
            var seconds = (totalMilliseconds % 60000) / 1000;
            var milliseconds = totalMilliseconds % 1000;
            timing.TargetMin = minutes;
            timing.TargetSecond = seconds;
            timing.TargetTick = milliseconds;
        }

        _sceneLayoutService?.OnTimingUpdated(entityId);
    }

    public IReadOnlyList<int> GetChildren(int id)
    {
        var trans = _ecsWorld.GetComponent<Transform>(id);
        return trans?.Children ?? new List<int>();
    }

    public IReadOnlyList<int> GetSelectedIds() => _hierarchyService.SelectedEntitiesId;

    public void ClearSelection()
    {
        var currentSelection = new List<int>(_hierarchyService.SelectedEntitiesId);
        foreach (var id in currentSelection)
        {
            RemoveSelectCommand.Execute(id);
        }
    }

    public void SetSelection(IEnumerable<int> ids)
    {
        ClearSelection();
        foreach (var id in ids)
        {
            AddSelectCommand.Execute(id);
        }
    }

    public void ToggleSelection(int id)
    {
        if (_hierarchyService.SelectedEntitiesId.Contains(id))
        {
            RemoveSelectCommand.Execute(id);
        }
        else
        {
            AddSelectCommand.Execute(id);
        }
    }

    public void AddToSelection(int id)
    {
        AddSelectCommand.Execute(id);
    }

    public void SetExpanded(int id, bool expanded) => _hierarchyService.SetExpanded(id, expanded);

    private void Initialize()
    {
        CreateNodeCommand = new Command<(string name, int id, NoteTypeEnum type, int parent, bool isGroup)>(param =>
        {
            _hierarchyService.CreateNode(param.name, param.id, param.type, param.parent, param.isGroup);
        }, _framework);

        DeleteNodeCommand = new Command<int>(param =>
        {
            _hierarchyService.DeleteNode(param);
        }, _framework);

        DeleteSelectedCommand = new Command(() =>
        {
            _hierarchyService.DeleteSelectedNodes();
        }, _framework);

        RenameCommand = new Command<(int id, string name)>(param =>
        {
            _hierarchyService.RenameNode(param.name, param.id);
        }, _framework);

        DuplicateNodeCommand = new Command<(int target, bool isGroup)>(param =>
        {
            DuplicateNodeWithThreshold(param.target, param.isGroup, -1).Forget();
        }, _framework);

        MoveCommand = new Command<(int id, int parent, int index)>(param =>
        {
            _hierarchyService.SetParent(param.id, param.parent, param.index);
        }, _framework);

        AddSelectCommand = new Command<int>(param =>
        {
            _hierarchyService.AddSelect(param);
        }, _framework);

        RemoveSelectCommand = new Command<int>(param =>
        {
            _hierarchyService.RemoveSelect(param);
        }, _framework);

        ToggleVisibilityCommand = new Command<int>(param =>
        {
            var visibility = _hierarchyService.GetVisibility(param);
            _hierarchyService.SetVisibility(param, !visibility);
        }, _framework);

        FocusCommand = new Command<int>(param =>
        {
            _hierarchyService.SetFocus(param);
        }, _framework);

        CopyCommand = new Command(() =>
        {
            _clip.Clear();
            var selected = _hierarchyService.SelectedEntitiesId;
            foreach (var id in selected)
            {
                var node = _hierarchyService.NodeMapping[id];
                if (node != null)
                {
                    _clip.Add(node);
                }
            }
        }, _framework);

        PasteCommand = new Command<int>(target =>
        {
            PasteWithThreshold(target).Forget();
        }, _framework);
    }

    private int GetValidPasteTarget(int id)
    {
        if (id == -1)
        {
            return -1;
        }

        if (!_hierarchyService.NodeMapping.TryGetValue(id, out var node))
        {
            return -1;
        }

        if (node.IsGroup)
        {
            return id;
        }

        return _hierarchyService.GetParent(id);
    }

    public ObservableCollection<HierarchyItemBase> GetRootItems()
    {
        var ret = new ObservableCollection<HierarchyItemBase>();

        foreach (var id in _hierarchyService.Roots)
        {
            if (_hierarchyService.NodeMapping.TryGetValue(id, out var node))
            {
                ret.Add(new HierarchyItemImpl
                {
                    EntityId = node.EntityId,
                    Name = node.Name,
                    Depth = 0,
                    Parent = null
                });
            }
        }

        return ret;
    }

    private int CountNodeAndDescendants(int nodeId)
    {
        if (!_hierarchyService.NodeMapping.TryGetValue(nodeId, out var node))
        {
            return 0;
        }

        int count = 1;
        if (node.IsGroup)
        {
            var transform = _ecsWorld.GetComponent<Transform>(nodeId);
            if (transform?.Children != null)
            {
                foreach (var childId in transform.Children)
                {
                    count += CountNodeAndDescendants(childId);
                }
            }
        }

        return count;
    }

    private int CountNodesAndDescendants(IEnumerable<int> nodeIds)
    {
        int total = 0;
        foreach (var id in nodeIds)
        {
            total += CountNodeAndDescendants(id);
        }

        return total;
    }

    private async UniTaskVoid DuplicateNodeWithThreshold(int target, bool isGroup, int targetParent)
    {
        int totalCount = CountNodeAndDescendants(target);
        if (totalCount >= ParallelThreshold)
        {
            await _hierarchyService.DuplicateNodeParallelAsync(target, isGroup, targetParent);
        }
        else
        {
            _hierarchyService.DuplicateNode(target, isGroup, targetParent);
        }
    }

    private async UniTaskVoid PasteWithThreshold(int target)
    {
        if (_clip.Count == 0)
        {
            return;
        }

        var pasteId = GetValidPasteTarget(target);
        var nodeIds = _clip.Select(n => n.EntityId).ToList();
        int totalCount = CountNodesAndDescendants(nodeIds);

        if (totalCount >= ParallelThreshold)
        {
            await _hierarchyService.DuplicateNodesParallelAsync(nodeIds, pasteId);
        }
        else
        {
            foreach (var node in _clip)
            {
                _hierarchyService.DuplicateNode(node.EntityId, node.IsGroup, pasteId);
            }
        }
    }

    public void HandleDragDrop(int source, int target, DropPosition pos)
    {
        if (source == target)
        {
            return;
        }

        var sourceNode = _hierarchyService.NodeMapping.GetValueOrDefault(source);
        var targetNode = _hierarchyService.NodeMapping.GetValueOrDefault(target);

        if (sourceNode == null || targetNode == null)
        {
            return;
        }

        int newParentId;
        int insertIndex;

        switch (pos)
        {
            case DropPosition.Before:
                newParentId = _hierarchyService.GetParent(target);
                insertIndex = GetSiblingIndex(target);
                break;

            case DropPosition.After:
                newParentId = _hierarchyService.GetParent(target);
                insertIndex = GetSiblingIndex(target) + 1;
                break;

            case DropPosition.Inside:
                if (!targetNode.IsGroup)
                {
                    return;
                }

                newParentId = target;
                insertIndex = 0;
                break;

            default:
                return;
        }

        MoveCommand.Execute((source, newParentId, insertIndex));
    }

    public void HandleContextMenu(ContextMenuAction action, int idx)
    {
        var targetId = idx;

        var parentId = GetCreateTargetParentId(targetId);

        switch (action)
        {
            case ContextMenuAction.CreateEmpty:
                var newEmptyName = GenerateUniqueName("New Note");
                CreateNodeCommand.Execute((newEmptyName, 0, NoteTypeEnum.Click, parentId, false));
                break;

            case ContextMenuAction.CreateGroup:
                var newGroupName = GenerateUniqueName("New Group");
                CreateNodeCommand.Execute((newGroupName, 0, NoteTypeEnum.Empty, parentId, true));
                break;

            case ContextMenuAction.Rename:
                break;

            case ContextMenuAction.Delete:
                var selectedIds = _hierarchyService.SelectedEntitiesId;
                if (selectedIds.Count > 1)
                {
                    DeleteSelectedCommand.Execute(null);
                }
                else if (targetId != -1)
                {
                    DeleteNodeCommand.Execute(targetId);
                }

                break;

            case ContextMenuAction.Duplicate:
                if (targetId != -1 && _hierarchyService.NodeMapping.TryGetValue(targetId, out var node))
                {
                    DuplicateNodeCommand.Execute((targetId, node.IsGroup));
                }

                break;

            case ContextMenuAction.Copy:
                CopyCommand.Execute(null);
                break;

            case ContextMenuAction.Paste:
                PasteCommand.Execute(targetId);
                break;

            case ContextMenuAction.ToggleVisibility:
                if (targetId != -1)
                {
                    ToggleVisibilityCommand.Execute(targetId);
                }

                break;

            case ContextMenuAction.FocusOn:
                if (targetId != -1)
                {
                    FocusCommand.Execute(targetId);
                }

                break;
        }
    }

    public void DeleteUngroupedRootNodes()
    {
        var ungroupedIds = _hierarchyService.Roots
            .Where(id => _hierarchyService.NodeMapping.TryGetValue(id, out var node) && !node.IsGroup)
            .ToList();

        foreach (var id in ungroupedIds)
        {
            _hierarchyService.DeleteNode(id);
        }
    }

    public void DeleteAllRootNodes()
    {
        var rootIds = _hierarchyService.Roots.ToList();

        foreach (var id in rootIds)
        {
            _hierarchyService.DeleteNode(id);
        }
    }

    public List<Type> GetAvailableComponents(int entityId)
    {
        var available = new List<Type>();
        if (entityId == -1)
        {
            return available;
        }

        if (_hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup)
        {
            return available;
        }

        var allComponents = new Type[]
        {
            typeof(Timing),
            typeof(Description),
            typeof(VelocityCurve)
        };

        foreach (var componentType in allComponents)
        {
            if (!HasComponent(entityId, componentType))
            {
                available.Add(componentType);
            }
        }

        return available;
    }

    public bool HasAllComponents(int entityId)
    {
        return GetAvailableComponents(entityId).Count == 0;
    }

    public bool IsGroup(int entityId)
    {
        return _hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup;
    }

    public bool CanFocus(int entityId)
    {
        if (entityId == -1)
        {
            return false;
        }

        if (!_hierarchyService.NodeMapping.TryGetValue(entityId, out var node))
        {
            return false;
        }

        return !node.IsGroup;
    }

    private bool HasComponent(int entityId, Type componentType)
    {
        if (componentType == typeof(Transform))
        {
            return _ecsWorld.GetComponent<Transform>(entityId) != null;
        }

        if (componentType == typeof(MeshRenderer))
        {
            return _ecsWorld.GetComponent<MeshRenderer>(entityId) != null;
        }

        if (componentType == typeof(Timing))
        {
            return _ecsWorld.GetComponent<Timing>(entityId) != null;
        }

        if (componentType == typeof(Description))
        {
            return _ecsWorld.GetComponent<Description>(entityId) != null;
        }

        if (componentType == typeof(VelocityCurve))
        {
            return _ecsWorld.GetComponent<VelocityCurve>(entityId) != null;
        }

        return false;
    }



    public void AddComponentToEntity(int entityId, Type componentType)
    {
        if (entityId == -1)
        {
            return;
        }

        if (componentType == typeof(Timing))
        {
            _ecsWorld.AddComponent<Timing>(entityId);
        }
        else if (componentType == typeof(Description))
        {
            _ecsWorld.AddComponent<Description>(entityId);
        }
        else if (componentType == typeof(VelocityCurve))
        {
            _ecsWorld.AddComponent<VelocityCurve>(entityId);
        }

        HierarchyUpdated?.Invoke(this, new HierarchyChangedEventArgs
        {
            ChangedType = HierarchyChangedType.PropertyChanged,
            EntityId = entityId
        });
    }

    private int GetCreateTargetParentId(int targetId)
    {
        if (targetId == -1)
        {
            return -1;
        }

        if (!_hierarchyService.NodeMapping.TryGetValue(targetId, out var node))
        {
            return -1;
        }

        if (node.IsGroup)
        {
            return targetId;
        }

        return _hierarchyService.GetParent(targetId);
    }

    public int GetQuickCreateParentId()
    {
        var selectedIds = _hierarchyService.SelectedEntitiesId;

        if (selectedIds.Count != 1)
        {
            return -1;
        }

        var selectedId = selectedIds[0];
        if (!_hierarchyService.NodeMapping.TryGetValue(selectedId, out var node))
        {
            return -1;
        }

        return node.IsGroup ? selectedId : -1;
    }

    private void OnHierarchyChanged(object s, HierarchyChangedEventArgs e)
    {
        HierarchyUpdated?.Invoke(this, e);
    }

    private void OnSelectionChanged(object s, List<int> ids)
    {
        SelectionUpdated?.Invoke(this, ids);
    }

    public event EventHandler<HierarchyChangedEventArgs> HierarchyUpdated;
    public event EventHandler<List<int>> SelectionUpdated;

    private int GetSiblingIndex(int id)
    {
        var parent = _hierarchyService.GetParent(id);
        if (parent == -1)
        {
            return _hierarchyService.Roots.IndexOf(id);
        }

        var pTrans = _ecsWorld.GetComponent<Transform>(parent);
        return pTrans?.Children.IndexOf(id) ?? 0;
    }

    private List<int> GetDepthPath(int id)
    {
        var path = new List<int>();
        if (id == -1)
        {
            return path;
        }

        var current = id;
        while (current != -1)
        {
            var idx = GetSiblingIndex(current);
            path.Insert(0, idx);
            current = _hierarchyService.GetParent(current);
        }

        return path;
    }

    private string GenerateUniqueName(string name)
    {
        var ext = new HashSet<string>();
        foreach (var node in _hierarchyService.NodeMapping.Values)
        {
            ext.Add(node.Name);
        }

        if (!ext.Contains(name))
        {
            return name;
        }

        int count = 1;
        while (ext.Contains($"{name} ({count})"))
        {
            count++;
        }

        return $"{name} ({count})";
    }

    private void SubscribeEvents()
    {
        _hierarchyService.HierarchyChanged += OnHierarchyChanged;
        _hierarchyService.SelectedEntitiesChanged += OnSelectionChanged;
    }

    public void Dispose()
    {
        _hierarchyService.HierarchyChanged -= OnHierarchyChanged;
        _hierarchyService.SelectedEntitiesChanged -= OnSelectionChanged;
    }

    private sealed class HierarchyItemImpl : HierarchyItemBase { }

    public abstract class HierarchyItemBase
    {
        public int EntityId { get; set; }
        public string Name { get; set; }
        public int Depth { get; set; }
        public HierarchyItemBase Parent { get; set; }
    }

    public enum DropPosition { Before, After, Inside }

    public enum ContextMenuAction
    {
        CreateEmpty, CreateGroup, Rename, Delete, Duplicate,
        Copy, Paste, ToggleVisibility, FocusOn
    }

    public class HierarchySelectionChangedEventArgs : EventArgs
    {
        public HierarchyItemBase OldItem { get; set; }
        public HierarchyItemBase NewItem { get; set; }
    }

    public class HierarchyDragDropEventArgs : EventArgs
    {
        public HierarchyItemBase Source { get; set; }
        public HierarchyItemBase Target { get; set; }
        public DropPosition Position { get; set; }
    }

    public class HierarchyContextMenuEventArgs : EventArgs
    {
        public HierarchyItemBase Item { get; set; }
        public Windows.Foundation.Point Position { get; set; }
    }
}