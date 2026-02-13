using FluentDesigner.ECS.Components;
using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Input;
using static FluentDesigner.ECS.System.HierarchyController;

namespace FluentDesigner.Control
{
    public sealed partial class HierarchyView : UserControl
    {
        private HierarchyController _controller;
        private int _lastSelectedIndex = -1;
        private HierarchyFlatItem _pendingSelectionItem;
        private bool _isProcessingDrag;
        private bool _isQuickCreate;
        private bool _isBatchCreate;
        private HierarchyFlatItem _renamingItem;
        private PlaybackService _playbackService;
        private double _batchInterval;

        public ObservableCollection<HierarchyFlatItem> FlattenedItems { get; } = [];
        private readonly Dictionary<int, HierarchyFlatItem> _allItemsMap = [];
        private readonly Dictionary<int, List<int>> _childrenMap = [];
        private readonly List<int> _rootIds = [];

        public event EventHandler<HierarchySelectionChangedEventArgs> SelectionChanged;
        public event EventHandler<HierarchyDragDropEventArgs> DragDropRequested;
        public event EventHandler<HierarchyContextMenuEventArgs> ContextMenuRequested;

        private bool _isDragging;
        private Point _dragStartPoint;
        private List<HierarchyFlatItem> _dragSource;
        private HierarchyFlatItem _dragTarget;
        private DropPosition _dragPosition;
        private const double DragThreshold = 5;

        private int _contextMenuTargetId = -1;
        private bool _contextMenuTargetIsGroup;

        public bool IsQuickCreate
        {
            get => _isQuickCreate;
            set => _isQuickCreate = value;
        }
        public bool IsBatchCreate
        {
            get => _isBatchCreate;
            set => _isBatchCreate = value;
        }

        public HierarchyController Controller
        {
            get => _controller;
            set
            {
                if (_controller == value)
                {
                    return;
                }

                if (_controller != null)
                {
                    _controller.HierarchyUpdated -= OnHierarchyUpdated;
                    _controller.SelectionUpdated -= OnSelectionUpdated;
                }

                _controller = value;

                if (_controller != null)
                {
                    _controller.HierarchyUpdated += OnHierarchyUpdated;
                    _controller.SelectionUpdated += OnSelectionUpdated;
                }

                RefereshHierarchy();
            }
        }

        public HierarchyView()
        {
            InitializeComponent();
            Initialize();
        }

        private void Initialize()
        {
            var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
            Controller = new HierarchyController(framework);
            PreviewKeyDown += OnKeyDown;
            Loaded += (s, e) => Focus(FocusState.Programmatic);
            PointerPressed += (s, e) => Focus(FocusState.Pointer);
            _playbackService = framework.GetService<PlaybackService>();
            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered += OnPlayModeEntered;
                _playbackService.PlayModeExited += OnPlayModeExited;
            }
        }

        public static void OnControllerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HierarchyView view)
            {
                if (e.OldValue is HierarchyController oldController)
                {
                    oldController.HierarchyUpdated -= view.OnHierarchyUpdated;
                    oldController.SelectionUpdated -= view.OnSelectionUpdated;
                }

                if (e.NewValue is HierarchyController newController)
                {
                    newController.HierarchyUpdated += view.OnHierarchyUpdated;
                    newController.SelectionUpdated += view.OnSelectionUpdated;
                }

                view.RefereshHierarchy();
            }
        }

        private void OnHierarchyUpdated(object sender, HierarchyChangedEventArgs e)
        {
            if (_isProcessingDrag)
            {
                return;
            }

            RefereshHierarchy();
        }

        private void OnSelectionUpdated(object sender, List<int> selectedIds)
        {
            foreach (var item in _allItemsMap.Values)
            {
                item.IsSelected = selectedIds.Contains(item.EntityId);
            }
        }

        private void OnCreateNoteButtonPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(sender as UIElement);
            var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);

            if (_isBatchCreate && _isQuickCreate)
            {
                CreateBatchNotes(NoteTypeEnum.Click);
                e.Handled = true;
            }
            else if (_isQuickCreate)
            {
                CreateSingleNote(NoteTypeEnum.Click);
                e.Handled = true;
            }
            else if (_isBatchCreate)
            {
                _pendingBatchCreate = true;
            }
            else
            {
                _pendingBatchCreate = false;
            }
        }

        private bool _pendingBatchCreate;

        private void OnNoteTypeMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && Enum.TryParse<NoteTypeEnum>(item.Tag?.ToString(), out var noteType))
            {
                if (_pendingBatchCreate)
                {
                    CreateBatchNotes(noteType);
                }
                else
                {
                    CreateSingleNote(noteType);
                }
            }
            _pendingBatchCreate = false;
        }

        private void CreateSingleNote(NoteTypeEnum noteType)
        {
            var parentId = Controller?.GetQuickCreateParentId() ?? -1;
            var currentTime = _playbackService?.CurrentTime ?? 0f;
            Controller?.CreateNoteWithTypeAndTime(noteType, parentId, currentTime);
        }

        private void CreateBatchNotes(NoteTypeEnum noteType)
        {
            if (!double.TryParse(BatchIntervalTextBox?.Text, out _batchInterval) || _batchInterval <= 0)
            {
                CreateSingleNote(noteType);
                return;
            }

            var inPoint = _playbackService?.LoopInPoint;
            var outPoint = _playbackService?.LoopOutPoint;

            if (!inPoint.HasValue || !outPoint.HasValue)
            {
                CreateSingleNote(noteType);
                return;
            }

            var parentId = Controller?.GetQuickCreateParentId() ?? -1;
            var startTime = Math.Min(inPoint.Value, outPoint.Value);
            var endTime = Math.Max(inPoint.Value, outPoint.Value);

            var intervalSeconds = (float)(_batchInterval / 1000.0);

            for (var time = startTime; time <= endTime; time += intervalSeconds)
            {
                Controller?.CreateNoteWithTypeAndTime(noteType, parentId, time);
            }
        }

        private void OnPlayModeEntered(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isDragging = false;
                _dragSource = null;
                _dragTarget = null;
                IsHitTestVisible = false;
            });
        }

        private void OnPlayModeExited(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                IsHitTestVisible = true;
            });
        }

        private void OnIsBatchCreateToggle(object s, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                IsBatchCreate = !IsBatchCreate;
            });
        }

        private void OnIsQuickCreateToggle(object s, RoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                IsQuickCreate = !IsQuickCreate;
            });
        }

        public void RefereshHierarchy()
        {
            FlattenedItems.Clear();
            _allItemsMap.Clear();
            _childrenMap.Clear();
            _rootIds.Clear();

            if (Controller == null)
            {
                return;
            }

            foreach (var root in Controller.GetRoots())
            {
                _rootIds.Add(root);
                PopulateItemRecursive(root, -1);
            }

            BuildFlattenedList();
        }
        private void PopulateItemRecursive(int id, int parentId)
        {
            if (!Controller.TryGetNode(id, out var node))
            {
                return;
            }

            var flatItem = new HierarchyFlatItem
            {
                EntityId = node.EntityId,
                ParentId = parentId,
                Name = node.Name,
                IsGroup = node.IsGroup,
                IsExpanded = node.IsExpanded,
                IsVisible = node.IsVisible,
                IsSelected = Controller.GetSelectedIds().Contains(id)
            };

            _allItemsMap[id] = flatItem;

            var children = Controller.GetChildren(id);
            if (children.Count > 0)
            {
                _childrenMap[id] = new List<int>(children);
                foreach (var childId in children)
                {
                    PopulateItemRecursive(childId, id);
                }
            }
        }

        private void BuildFlattenedList()
        {
            foreach (var rootId in _rootIds)
            {
                AddItemToFlatList(rootId, 0);
            }
        }

        private void AddItemToFlatList(int id, int depth)
        {
            if (!_allItemsMap.TryGetValue(id, out var item))
            {
                return;
            }

            item.Depth = depth;
            FlattenedItems.Add(item);

            if (item.IsGroup && item.IsExpanded && _childrenMap.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    AddItemToFlatList(child, depth + 1);
                }
            }
        }

        public void ToggleExpand(int id)
        {
            if (!_allItemsMap.TryGetValue(id, out var item) || !item.IsGroup)
            {
                return;
            }

            var newExpanded = !item.IsExpanded;
            Controller?.SetExpanded(id, newExpanded);
            RebuildFlattenedList();
        }

        private void RebuildFlattenedList()
        {
            FlattenedItems.Clear();
            foreach (var rootId in _rootIds)
            {
                AddItemToFlatList(rootId, 0);
            }
        }

        public Func<HierarchyFlatItem, UIElement> GroupControlFactory { get; set; }
        public Func<HierarchyFlatItem, UIElement> ItemControlFactory { get; set; }

        private void CreateNoteQuickly(object sender, RoutedEventArgs e)
        {
            var parentId = Controller?.GetQuickCreateParentId() ?? -1;
            Controller?.HandleContextMenu(ContextMenuAction.CreateEmpty, parentId);
        }

        private void CreateGroupQuickly(object sender, RoutedEventArgs e)
        {
            var parentId = Controller?.GetQuickCreateParentId() ?? -1;
            Controller?.HandleContextMenu(ContextMenuAction.CreateGroup, parentId);
        }

        private void OnElementPrepared(ItemsRepeater s, ItemsRepeaterElementPreparedEventArgs e)
        {
            if (e.Element is FrameworkElement element && FlattenedItems.Count > e.Index)
            {
                element.DataContext = FlattenedItems[e.Index];
            }
        }

        private void OnElementClearing(ItemsRepeater s, ItemsRepeaterElementClearingEventArgs e)
        {
            if (e.Element is FrameworkElement element)
            {
                element.DataContext = null;
            }
        }

        private void OnItemPointerPressed(object s, PointerRoutedEventArgs e)
        {
            if (s is not FrameworkElement element)
            {
                return;
            }

            if (element.DataContext is not HierarchyFlatItem item)
            {
                return;
            }

            var p = e.GetCurrentPoint(this);
            if (p.Properties.IsLeftButtonPressed)
            {
                _dragStartPoint = p.Position;
                element.CapturePointer(e.Pointer);
                _pendingSelectionItem = null;
                var currentIndex = FlattenedItems.IndexOf(item);
                var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(CoreVirtualKeyStates.Down);
                var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(CoreVirtualKeyStates.Down);

                if (shiftPressed && _lastSelectedIndex >= 0 && _lastSelectedIndex < FlattenedItems.Count)
                {
                    var start = Math.Min(_lastSelectedIndex, currentIndex);
                    var end = Math.Max(_lastSelectedIndex, currentIndex);

                    var rangeIds = new List<int>();
                    for (var i = start; i <= end; i++)
                    {
                        rangeIds.Add(FlattenedItems[i].EntityId);
                    }

                    Controller?.SetSelection(rangeIds);
                    _dragSource = rangeIds.Select(id => _allItemsMap[id]).ToList();
                }
                else if (ctrlPressed)
                {
                    Controller?.ToggleSelection(item.EntityId);

                    _lastSelectedIndex = currentIndex;
                    _dragSource = _allItemsMap.Values.Where(i => i.IsSelected).ToList();
                }
                else
                {
                    var selectedItems = _allItemsMap.Values.Where(i => i.IsSelected).ToList();

                    if (selectedItems.Count > 1 && item.IsSelected)
                    {
                        _pendingSelectionItem = item;
                        _dragSource = selectedItems;
                    }
                    else
                    {
                        Controller?.SetSelection([item.EntityId]);
                        _dragSource = [item];
                    }
                    _lastSelectedIndex = currentIndex;
                }
                HandleItemSelection(_dragSource, e);
            }

            e.Handled = true;
        }

        private void OnItemPointerMoved(object s, PointerRoutedEventArgs e)
        {
            if (_dragSource == null || _dragSource.Count == 0)
            {
                return;
            }

            if (s is not FrameworkElement)
            {
                return;
            }

            var posInRootGrid = e.GetCurrentPoint(RootGrid).Position;

            var posInThis = e.GetCurrentPoint(this).Position;
            var d = new Point(posInThis.X - _dragStartPoint.X, posInThis.Y - _dragStartPoint.Y);
            var distance = Math.Sqrt(d.X * d.X + d.Y * d.Y);

            if (!_isDragging && distance > DragThreshold)
            {
                _isDragging = true;
                BeginDrag(_dragSource);
            }

            if (_isDragging)
            {
                UpdateDragPreview(posInRootGrid);
                UpdateDropTarget(posInRootGrid);
            }

            e.Handled = true;
        }

        private void HandleItemSelection(List<HierarchyFlatItem> items, PointerRoutedEventArgs e)
        {
            var selectedItems = items.Select(item => new HierarchyItemBaseImpl
            {
                EntityId = item.EntityId,
                Name = item.Name
            }).ToList();

            if (selectedItems.Count > 0)
            {
                SelectionChanged?.Invoke(this, new HierarchySelectionChangedEventArgs
                {
                    NewItem = selectedItems[^1]
                });
            }
        }

        private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (_isDragging && _dragSource != null && _dragTarget != null)
            {
                CompleteDrag();
            }
            else if (_pendingSelectionItem != null)
            {
                var idx = FlattenedItems.IndexOf(_pendingSelectionItem);
                Controller.SetSelection([_pendingSelectionItem.EntityId]);
                _lastSelectedIndex = idx;
                HandleItemSelection([_pendingSelectionItem], e);
            }

            _pendingSelectionItem = null;
            EndDrag();
            e.Handled = true;
        }

        private void BeginDrag(List<HierarchyFlatItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            string previewText;
            if (items.Count == 1)
            {
                previewText = items[0].Name;
            }
            else
            {
                previewText = $"{items[0].Name} (+{items.Count - 1} items..)";
            }

            DragPreviewText.Text = previewText;
            DragPreviewCanvas.Visibility = Visibility.Visible;
        }


        private void UpdateDragPreview(Point position)
        {
            Canvas.SetLeft(DragPreviewBorder, position.X + 5);
            Canvas.SetTop(DragPreviewBorder, position.Y + 5);
        }

        private void UpdateDropTarget(Point pos)
        {
            var offset = MainScrollViewer.VerticalOffset;
            var contentY = pos.Y + offset;

            var itemIndex = (int)(contentY / 32);

            if (itemIndex < 0)
            {
                itemIndex = 0;
                _dragPosition = DropPosition.Before;
            }
            else if (itemIndex >= FlattenedItems.Count)
            {
                if (FlattenedItems.Count > 0)
                {
                    itemIndex = FlattenedItems.Count - 1;
                    _dragPosition = DropPosition.After;
                }
                else
                {
                    _dragTarget = null;
                    DropIndicator.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            else
            {
                var itemTopInContent = itemIndex * 32;
                var relativeY = contentY - itemTopInContent;

                if (relativeY < 8)
                {
                    _dragPosition = DropPosition.Before;
                }
                else if (relativeY > 24)
                {
                    _dragPosition = DropPosition.After;
                }
                else
                {
                    var targetItem = FlattenedItems[itemIndex];
                    _dragPosition = targetItem.IsGroup ? DropPosition.Inside : DropPosition.After;
                }
            }

            _dragTarget = FlattenedItems[itemIndex];
            UpdateDropIndicator(itemIndex, _dragPosition);
        }

        private void UpdateDropIndicator(int id, DropPosition pos)
        {
            var offset = MainScrollViewer.VerticalOffset;
            double indicatorY;
            double leftMargin = 0;

            switch (pos)
            {
                case DropPosition.Before:
                    indicatorY = id * 32 - offset;
                    break;
                case DropPosition.After:
                    indicatorY = (id + 1) * 32 - offset;
                    break;
                case DropPosition.Inside:
                    indicatorY = (id + 1) * 32 - offset;
                    leftMargin = 20;
                    break;
                default:
                    return;
            }
            DropIndicator.Margin = new Thickness(leftMargin, indicatorY - 1, 0, 0);
            DropIndicator.Visibility = Visibility.Visible;
        }

        private void CompleteDrag()
        {
            if (_dragSource == null || _dragTarget == null)
            {
                return;
            }

            _isProcessingDrag = true;
            try
            {
                var targetId = _dragTarget.EntityId;
                var dragItems = _dragSource.ToList();
                foreach (var item in dragItems)
                {
                    if (item.EntityId == targetId)
                    {
                        continue;
                    }

                    if (item.IsGroup && IsDescendantOf(targetId, item.EntityId))
                    {
                        continue;
                    }

                    DragDropRequested?.Invoke(this, new HierarchyDragDropEventArgs
                    {
                        Source = new HierarchyItemBaseImpl { EntityId = item.EntityId },
                        Target = new HierarchyItemBaseImpl { EntityId = targetId },
                        Position = _dragPosition
                    });

                    Controller?.HandleDragDrop(item.EntityId, targetId, _dragPosition);
                }
            }
            finally
            {
                _isProcessingDrag = false;
            }
            RefereshHierarchy();
        }

        private bool IsDescendantOf(int childId, int parentId)
        {
            if (childId == parentId)
            {
                return true;
            }

            if (!_childrenMap.TryGetValue(parentId, out var children))
            {
                return false;
            }

            foreach (var child in children)
            {
                if (child == childId)
                {
                    return true;
                }

                if (IsDescendantOf(childId, child))
                {
                    return true;
                }
            }
            return false;
        }

        private void EndDrag()
        {
            _isDragging = false;
            _dragSource = null;
            _dragTarget = null;
            DragPreviewCanvas.Visibility = Visibility.Collapsed;
            DropIndicator.Visibility = Visibility.Collapsed;
            DropIndicator.Margin = new Thickness(0);
        }

        private void OnEmptyAreaPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var position = e.GetCurrentPoint(MainScrollViewer).Position;
            var offset = MainScrollViewer.VerticalOffset;
            var adjustedY = position.Y + offset;
            var itemIndex = (int)(adjustedY / 32);
            if (itemIndex >= FlattenedItems.Count)
            {
                ClearSelection();
                e.Handled = true;
            }
        }

        private void ClearSelection()
        {
            Controller?.ClearSelection();
            foreach (var item in _allItemsMap.Values)
            {
                item.IsSelected = false;
            }

            _lastSelectedIndex = -1;
            _dragSource = null;
            SelectionChanged?.Invoke(this, new HierarchySelectionChangedEventArgs
            {
                OldItem = null,
                NewItem = null
            });
        }

        private void OnRootGridRightTapped(object s, RightTappedRoutedEventArgs e)
        {
            _contextMenuTargetId = -1;
            var menu = Resources["EmptyAreaContextMenu"] as MenuFlyout;
            menu?.ShowAt(RootGrid, e.GetPosition(RootGrid));
            e.Handled = true;
        }

        private void OnItemRightTapped(object s, RightTappedRoutedEventArgs e)
        {
            if (s is not FrameworkElement element)
            {
                return;
            }

            if (element.DataContext is not HierarchyFlatItem item)
            {
                return;
            }

            var selectedIds = Controller?.GetSelectedIds();
            var selectedCount = selectedIds?.Count ?? 0;

            if (selectedCount > 1)
            {
                _contextMenuTargetId = -1;
                var menuInter = Resources["EmptyAreaContextMenu"] as MenuFlyout;
                menuInter?.ShowAt(element, e.GetPosition(element));
                e.Handled = true;
                return;
            }

            _contextMenuTargetId = item.EntityId;
            _contextMenuTargetIsGroup = item.IsGroup;

            ContextMenuRequested?.Invoke(this, new HierarchyContextMenuEventArgs
            {
                Item = new HierarchyItemBaseImpl { EntityId = item.EntityId, Name = item.Name },
                Position = e.GetPosition(this)
            });

            var key = item.IsGroup ? "GroupContextMenu" : "ItemContextMenu";
            var menu = Resources[key] as MenuFlyout;
            menu?.ShowAt(element, e.GetPosition(element));

            e.Handled = true;
        }

        private void OnCreateEmptyClicked(object sender, RoutedEventArgs e)
        => Controller?.HandleContextMenu(ContextMenuAction.CreateEmpty, _contextMenuTargetId);

        private void OnCreateGroupClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.CreateGroup, _contextMenuTargetId);

        private void OnRenameClicked(object sender, RoutedEventArgs e)
            => BeginRename(_contextMenuTargetId);

        private void OnDeleteClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.Delete, _contextMenuTargetId);

        private void OnCopyClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.Copy, _contextMenuTargetId);

        private void OnPasteClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.Paste, _contextMenuTargetId);

        private void OnToggleVisibilityClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.ToggleVisibility, _contextMenuTargetId);

        private void OnFocusClicked(object sender, RoutedEventArgs e)
            => Controller?.HandleContextMenu(ContextMenuAction.FocusOn, _contextMenuTargetId);

        private void OnExpandClicked(object sender, RoutedEventArgs e)
        {
            if (_allItemsMap.TryGetValue(_contextMenuTargetId, out var item))
            {
                Controller?.SetExpanded(_contextMenuTargetId, true);
                item.IsExpanded = true;
                RebuildFlattenedList();
            }
        }

        private void OnCollapseClicked(object sender, RoutedEventArgs e)
        {
            if (_allItemsMap.TryGetValue(_contextMenuTargetId, out var item))
            {
                Controller?.SetExpanded(_contextMenuTargetId, false);
                item.IsExpanded = false;
                RebuildFlattenedList();
            }
        }

        private void OnExpandToggleClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (button.DataContext is not HierarchyFlatItem item)
            {
                return;
            }

            ToggleExpand(item.EntityId);
        }

        private void OnVisibilityButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (button.DataContext is not HierarchyFlatItem item)
            {
                return;
            }

            Controller?.HandleContextMenu(ContextMenuAction.ToggleVisibility, item.EntityId);
        }

        private void BeginRename(int id)
        {
            if (!_allItemsMap.TryGetValue(id, out var item))
            {
                return;
            }

            CancelRename();
            _renamingItem = item;
            item.IsRenaming = true;
        }

        private void CommitRename(TextBox textBox)
        {
            if (_renamingItem == null)
            {
                return;
            }

            var newName = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != _renamingItem.Name)
            {
                Controller?.RenameCommand.Execute((_renamingItem.EntityId, newName));
                _renamingItem.Name = newName;
            }

            _renamingItem.IsRenaming = false;
            _renamingItem = null;
        }

        private void CancelRename()
        {
            if (_renamingItem == null)
            {
                return;
            }

            _renamingItem.IsRenaming = false;
            _renamingItem = null;
        }

        private void OnRenameTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox textBox)
            {
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                CommitRename(textBox);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                CancelRename();
                e.Handled = true;
            }
        }

        private void OnRenameTextBoxLostFocus(object sender, RoutedEventArgs e)
        {
            CancelRename();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_renamingItem != null)
            {
                return;
            }

            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            {
                return;
            }

            var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down);
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);

            var selectedIds = Controller?.GetSelectedIds();

            switch (e.Key)
            {
                case VirtualKey.N when ctrl && !shift:
                    CreateNoteQuickly(null, null);
                    e.Handled = true;
                    break;

                case VirtualKey.N when ctrl && shift:
                    CreateGroupQuickly(null, null);
                    e.Handled = true;
                    break;

                case VirtualKey.E when ctrl && !shift:
                    foreach (var id in selectedIds ?? [])
                    {
                        if (_allItemsMap.TryGetValue(id, out var item) && item.IsGroup)
                        {
                            ToggleExpand(id);
                        }
                    }

                    e.Handled = true;
                    break;

                case VirtualKey.E when ctrl && shift:
                    foreach (var id in selectedIds ?? [])
                    {
                        Controller?.HandleContextMenu(ContextMenuAction.ToggleVisibility, id);
                    }

                    e.Handled = true;
                    break;

                case VirtualKey.D when ctrl:
                    foreach (var id in selectedIds?.ToList() ?? [])
                    {
                        if (_allItemsMap.TryGetValue(id, out var item))
                        {
                            Controller?.DuplicateNodeCommand.Execute((id, item.IsGroup));
                        }
                    }

                    e.Handled = true;
                    break;

                case VirtualKey.A when ctrl:
                    Controller?.SetSelection(_allItemsMap.Keys);
                    e.Handled = true;
                    break;

                case VirtualKey.C when ctrl:
                    Controller?.HandleContextMenu(ContextMenuAction.Copy, -1);
                    e.Handled = true;
                    break;

                case VirtualKey.V when ctrl:
                    Controller?.HandleContextMenu(ContextMenuAction.Paste, selectedIds?.FirstOrDefault() ?? -1);
                    e.Handled = true;
                    break;

                case VirtualKey.F when !ctrl && !shift:
                    if (selectedIds?.Count == 1 && Controller?.CanFocus(selectedIds[0]) == true)
                    {
                        Controller.HandleContextMenu(ContextMenuAction.FocusOn, selectedIds[0]);
                    }
                    e.Handled = true;
                    break;

                case VirtualKey.F2:
                    if (selectedIds?.Count == 1)
                    {
                        BeginRename(selectedIds[0]);
                    }

                    e.Handled = true;
                    break;

                case VirtualKey.Delete when !shift && !ctrl:
                    foreach (var id in selectedIds?.ToList() ?? [])
                    {
                        Controller?.HandleContextMenu(ContextMenuAction.Delete, id);
                    }

                    e.Handled = true;
                    break;

                case VirtualKey.Delete when shift && !ctrl:
                    ShowDeleteConfirmation("Delete Notification", "Delete all notes which wasn't Grouped?", () =>
                    {
                        Controller?.DeleteUngroupedRootNodes();
                    });
                    e.Handled = true;
                    break;

                case VirtualKey.Delete when ctrl && shift:
                    ShowDeleteConfirmation("Empty Scene Notification", "Clear all groups and notes in Scene?", () =>
                    {
                        Controller?.DeleteAllRootNodes();
                    });
                    e.Handled = true;
                    break;

                case VirtualKey.I when ctrl && shift:
                    ClearSelection();
                    e.Handled = true;
                    break;
            }
        }
        private async void ShowDeleteConfirmation(string title, string content, Action onConfirm)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = "Confirm",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                onConfirm();
            }
        }

        private sealed class HierarchyItemBaseImpl : HierarchyItemBase { }
    }

    public sealed class HierarchyFlatItem : INotifyPropertyChanged
    {
        private string _name;
        private int _depth;
        private bool _isGroup, _isExpanded, _isVisible, _isSelected, _isRenaming;

        public int EntityId { get; set; }
        public int ParentId { get; set; }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public int Depth
        {
            get => _depth;
            set { _depth = value; OnPropertyChanged(); }
        }

        public bool IsGroup
        {
            get => _isGroup;
            set
            {
                _isGroup = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGroupVisibility));
                OnPropertyChanged(nameof(IsItemVisibility));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExpandIconUri));
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibilityIconUri));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSelectedVisibility));
            }
        }

        public bool IsRenaming
        {
            get => _isRenaming;
            set
            {
                _isRenaming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NameTextBlockVisibility));
                OnPropertyChanged(nameof(RenameTextBoxVisibility));
            }
        }


        public double IndentWidth => Depth * 16;
        public Visibility IsGroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IsItemVisibility => !IsGroup ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IsSelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NameTextBlockVisibility => IsRenaming ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RenameTextBoxVisibility => IsRenaming ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource ExpandIconUri => new BitmapImage(IsExpanded
            ? new Uri("ms-appx:///Assets/Icons/Light/ArrowUp.png")
            : new Uri("ms-appx:///Assets/Icons/Light/ArrowDown.png"));

        public ImageSource VisibilityIconUri => new BitmapImage(IsVisible
            ? new Uri("ms-appx:///Assets/Icons/Light/View.png")
            : new Uri("ms-appx:///Assets/Icons/Light/Hide.png"));

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }


}
