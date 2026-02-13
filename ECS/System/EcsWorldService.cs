using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FluentDesigner.ECS.System
{
    public sealed class EcsWorldService : Service, IDisposable
    {
        private const int InitCapacity = 1024;
        private const int MaxCapacity = 65536;
        private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
        private readonly int _maxParallelism;

        private int[] _sparse;
        private int[] _dense;
        private int _count;
        private int _capacity;
        private int[] _cachedEntityArray;
        private readonly Lock _arrayLock = new();
        private readonly ConcurrentDictionary<Type, IComponentPool> _componentPools = new();
        private readonly ConcurrentQueue<int> _recycleIds = new();
        private int _nextId;

        public int EntityCount => _count;
        public int Capacity => _capacity;

        public EcsWorldService()
        {
            _maxParallelism = Environment.ProcessorCount switch
            {
                >= 16 => 8,
                >= 8 => 4,
                >= 4 => 2,
                _ => 1
            };
            _capacity = InitCapacity;
            _sparse = ArrayPool<int>.Shared.Rent(_capacity);
            _dense = ArrayPool<int>.Shared.Rent(_capacity);
            Array.Fill(_sparse, -1);
        }

        public override void Initialize()
        {
            base.Initialize();
        }

        public int CreateEntity()
        {
            _lock.EnterWriteLock();
            try
            {
                if (_count >= _capacity)
                {
                    EnsureCapacity(_capacity * 2);
                }

                int id = _recycleIds.TryDequeue(out var recycledId) ? recycledId : _nextId++;

                if (id >= _sparse.Length)
                {
                    EnsureCapacity(Math.Min(id * 2, MaxCapacity));
                }

                _sparse[id] = _count;
                _dense[_count] = id;
                _count++;

                return id;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public int[] CreateEntities(int count)
        {
            _lock.EnterWriteLock();
            try
            {
                var required = count + _count;
                if (required > _capacity)
                {
                    EnsureCapacity(Math.Min(required * 2, MaxCapacity));
                }

                var entities = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int id = _recycleIds.TryDequeue(out var recycledId) ? recycledId : _nextId++;

                    if (id >= _sparse.Length)
                    {
                        EnsureCapacity(Math.Min(id * 2, MaxCapacity));
                    }

                    _sparse[id] = _count;
                    _dense[_count] = id;
                    entities[i] = id;
                    _count++;
                }

                return entities;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool DestroyEntity(int id)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!IsValidEntity(id))
                {
                    return false;
                }

                int denseIndex = _sparse[id];
                int last = _dense[_count - 1];

                _dense[denseIndex] = last;
                _sparse[last] = denseIndex;
                _sparse[id] = -1;
                _count--;

                foreach (var pool in _componentPools.Values)
                {
                    pool.Remove(id);
                }

                _recycleIds.Enqueue(id);
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();

            }
        }

        public void DestroyEntities(ReadOnlySpan<int> ids)
        {
            _lock.EnterWriteLock();
            try
            {
                foreach (var id in ids)
                {
                    if (!IsValidEntity(id))
                    {
                        continue;
                    }

                    int denseId = _sparse[id];
                    int last = _dense[_count - 1];

                    _dense[denseId] = last;
                    _sparse[last] = denseId;
                    _sparse[id] = -1;
                    _count--;

                    foreach (var pool in _componentPools.Values)
                    {
                        pool.Remove(id);
                    }

                    _recycleIds.Enqueue(id);
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsValidEntity(int id) => id >= 0 && id < _sparse.Length && _sparse[id] != -1;

        public ComponentPool<T> GetPool<T>() where T : class, new() =>
            (ComponentPool<T>)_componentPools.GetOrAdd(typeof(T), _ => new ComponentPool<T>(InitCapacity, MaxCapacity));

        public T AddComponent<T>(int id) where T : class, new()
        {
            if (!IsValidEntity(id))
            {
                throw new ArgumentException($"Entity {id} is not valid.", nameof(id));
            }

            return GetPool<T>().Add(id);
        }

        public T GetComponent<T>(int id) where T : class, new()
        {
            if (!IsValidEntity(id))
            {
                return null;
            }

            return GetPool<T>().Get(id);
        }

        public bool RemoveComponent<T>(int id) where T : class, new() => IsValidEntity(id) && GetPool<T>().Remove(id);

        public bool HasComponent<T>(int id) where T : class, new() => IsValidEntity(id) && GetPool<T>().Contains(id);

        public void ParallelForEach<T>(Func<int, T> action) where T : class, new()
        {
            var pool = GetPool<T>();
            var entities = pool.GetEntities();

            if (entities.Length == 0)
            {
                return;
            }

            var (cachedArray, length) = GetEntityArrayFromSpan(entities);

            int size = Math.Max(1, entities.Length / _maxParallelism);
            var options = new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism };

            Parallel.ForEach(Partitioner.Create(0, length, size),
                options,
                range =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var id = cachedArray[i];
                        var component = pool.Get(id);
                        if (component != null)
                        {
                            action(id);
                        }
                    }
                });
        }

        public async UniTask ParallelForEachAsync<T>(Func<int, T, UniTask> action) where T : class, new()
        {
            var pool = GetPool<T>();
            var entities = pool.GetEntities();

            if (entities.Length == 0)
            {
                return;
            }

            int length = entities.Length;
            int size = Math.Max(1, entities.Length / _maxParallelism);
            var tasks = new List<UniTask>(_maxParallelism);
            var rentedArray = ArrayPool<int>.Shared.Rent(length);

            try
            {
                entities.CopyTo(rentedArray);
                for (int start = 0; start < length; start += size)
                {
                    int begin = start;
                    int end = Math.Min(start + size, length);
                    int chunkSize = end - begin;
                    var chunkArray = ArrayPool<int>.Shared.Rent(chunkSize);
                    Array.Copy(rentedArray, begin, chunkArray, 0, chunkSize);

                    tasks.Add(UniTask.Run(async () =>
                    {
                        try
                        {
                            for (int i = 0; i < chunkSize; i++)
                            {
                                var id = chunkArray[i];
                                var component = pool.Get(id);
                                if (component != null)
                                {
                                    await action(id, component);
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<int>.Shared.Return(chunkArray);
                        }
                    }));
                }

                await UniTask.WhenAll(tasks);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(rentedArray);
            }

        }

        public ReadOnlySpan<int> Query<T>() where T : class, new() => GetPool<T>().GetEntities();

        public void EnsureCapacity(int newCapacity)
        {
            if (newCapacity <= _capacity)
            {
                return;
            }

            newCapacity = Math.Min(newCapacity, MaxCapacity);

            if (newCapacity <= _capacity)
            {
                throw new InvalidOperationException("Cannot reduce capacity of the entity pool.");
            }

            var newSparse = ArrayPool<int>.Shared.Rent(newCapacity);
            var newDense = ArrayPool<int>.Shared.Rent(newCapacity);

            Array.Fill(newSparse, -1);
            Array.Copy(_sparse, newSparse, _sparse.Length);
            Array.Copy(_dense, newDense, _dense.Length);

            ArrayPool<int>.Shared.Return(_sparse);
            ArrayPool<int>.Shared.Return(_dense);

            _sparse = newSparse;
            _dense = newDense;
            _capacity = newCapacity;
        }

        public override void Shutdown()
        {
            _lock.EnterWriteLock();
            try
            {
                ArrayPool<int>.Shared.Return(_sparse);
                ArrayPool<int>.Shared.Return(_dense);

                lock (_arrayLock)
                {
                    _cachedEntityArray = null;
                }

                foreach (var item in _componentPools.Values)
                {
                    item.Dispose();
                }

                _componentPools.Clear();
                _count = 0;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            Shutdown();
            _lock.Dispose();
        }

        private (int[] array, int length) GetEntityArrayFromSpan(ReadOnlySpan<int> span)
        {
            lock (_arrayLock)
            {
                if (_cachedEntityArray == null || _cachedEntityArray.Length < span.Length)
                {
                    _cachedEntityArray = new int[Math.Max(span.Length, 256)];
                }
                span.CopyTo(_cachedEntityArray);
                return (_cachedEntityArray, span.Length);
            }
        }

        public interface IComponentPool : IDisposable
        {
            bool Remove(int id);
            bool Contains(int id);
        }

        public sealed class ComponentPool<T> : IComponentPool where T : class, new()
        {
            private readonly Lock _lock = new();
            private readonly ConcurrentQueue<T> _objectPool = new();
            private int[] _sparse;
            private int[] _dense;
            private T[] _data;
            private int _count;
            private int _capacity;
            private readonly int _maxCapacity;

            public ComponentPool(int initCapacity, int maxCapacity)
            {
                _capacity = initCapacity;
                _maxCapacity = maxCapacity;
                _sparse = new int[initCapacity];
                _dense = new int[initCapacity];
                _data = new T[initCapacity];
                Array.Fill(_sparse, -1);

                for (int i = 0; i < Math.Min(64, initCapacity); i++)
                {
                    _objectPool.Enqueue(new T());
                }
            }

            public T Add(int id)
            {
                lock (_lock)
                {
                    if (id >= _sparse.Length)
                    {
                        EnsureCapacity(Math.Min(id * 2, _maxCapacity));
                    }

                    if (_sparse[id] != -1)
                    {
                        return _data[_sparse[id]];
                    }

                    if (_count >= _capacity)
                    {
                        EnsureCapacity(Math.Min(_capacity * 2, _maxCapacity));
                    }

                    var component = _objectPool.TryDequeue(out var obj) ? obj : new T();

                    if (component is IResettable reset)
                    {
                        reset.Reset();
                    }

                    _sparse[id] = _count;
                    _dense[_count] = id;
                    _data[_count] = component;
                    _count++;
                    return component;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public T Get(int id)
            {
                if (id < 0 || id >= _sparse.Length)
                {
                    return null;
                }

                var index = _sparse[id];
                return index >= 0 && index < _count ? _data[index] : null;
            }

            public bool Remove(int id)
            {
                lock (_lock)
                {
                    if (id < 0 || id >= _sparse.Length || _sparse[id] == -1)
                    {
                        return false;
                    }

                    int denseId = _sparse[id];
                    int last = _dense[_count - 1];
                    var removed = _data[denseId];

                    _dense[denseId] = last;
                    _data[denseId] = _data[_count - 1];
                    _sparse[last] = denseId;
                    _sparse[id] = -1;
                    _data[_count - 1] = null;
                    _count--;

                    if (removed != null)
                    {
                        _objectPool.Enqueue(removed);
                    }

                    return true;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Contains(int id) => id >= 0 && id < _sparse.Length && _sparse[id] != -1;

            public ReadOnlySpan<int> GetEntities() => new(_dense, 0, _count);

            private void EnsureCapacity(int newCapacity)
            {
                if (newCapacity <= _capacity)
                {
                    return;
                }

                newCapacity = Math.Min(newCapacity, _maxCapacity);

                if (newCapacity <= _capacity)
                {
                    throw new InvalidOperationException("Cannot reduce capacity of the component pool.");
                }

                Array.Resize(ref _sparse, newCapacity);
                Array.Resize(ref _dense, newCapacity);
                Array.Resize(ref _data, newCapacity);

                for (int i = _capacity; i < newCapacity; i++)
                {
                    _sparse[i] = -1;
                }

                _capacity = newCapacity;
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    Array.Clear(_data, 0, _count);
                    _count = 0;
                    while (_objectPool.TryDequeue(out _)) { }
                }
            }
        }
    }
}
