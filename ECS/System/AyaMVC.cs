using Cysharp.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FluentDesigner.ECS.System
{
    public abstract class AyaMvcFramework<T> : IAyaMvcFramework, IDisposable where T : AyaMvcFramework<T>, new()
    {
        private static T Instance { get; set; }
        private readonly IoCContainer _container = new();
        public static IAyaMvcFramework EntryPoint => Instance ??= new T();

        protected AyaMvcFramework()
        {
            Instance = (T)this;
            Initialize();
        }

        protected abstract void Initialize();

        public void RegisterService<TS>(TS service) where TS : IService
        {
            service.SetFramework(this);
            _container.RegisterIoCInstance(service);
        }

        public TS GetService<TS>() where TS : IService => _container.GetInstance<TS>();

        public void SendCommand<TC>(TC command, object param) where TC : ICommand
        {
            command.Execute(param);
        }

        public async UniTask<TC> SendCommandAsync<TC>(TC command, object param) where TC : ICommandAsync<TC>
        {
            command.SetFramework(this);
            return await command.ExecuteAsync(param);
        }

        public void Shutdown()
        {
            _container.Dispose();
        }

        public void Dispose() => Shutdown();
    }

    public class Service : IService
    {
        private IAyaMvcFramework Framework { get; set; }
        public bool IsInitialized { get; private set; }
        public IAyaMvcFramework GetFramework() => Framework;
        public void SetFramework(IAyaMvcFramework framework) => Framework = framework;

        public virtual void Initialize() { IsInitialized = true; }
        public virtual void Shutdown() { IsInitialized = false; }
    }

    public class Command : ICommand
    {
        public Action Action { get; private set; }
        private IAyaMvcFramework Framework { get; set; }

        public Command(Action action, IAyaMvcFramework framework)
        {
            Action = action;
            Framework = framework;
        }
        public void Execute(object param) => Action.Invoke();
        public IAyaMvcFramework GetFramework() => Framework;
        public void SetFramework(IAyaMvcFramework framework) => Framework = framework;
    }

    public class Command<T> : ICommand<T>
    {
        public Action<T> Action { get; private set; }
        private IAyaMvcFramework Framework { get; set; }
        public Command(Action<T> action, IAyaMvcFramework framework)
        {
            Action = action;
            Framework = framework;
        }
        public void Execute(T param)
        {
            Action.Invoke(param);
        }
        public IAyaMvcFramework GetFramework() => Framework;
        public void SetFramework(IAyaMvcFramework framework) => Framework = framework;
    }

    public class CommandAsync : ICommandAsync
    {
        public UniTask Task { get; private set; }
        private IAyaMvcFramework Framework { get; set; }
        public CommandAsync(UniTask executeAsync, IAyaMvcFramework framework)
        {
            Task = executeAsync;
            Framework = framework;
        }
        public async UniTask ExecuteAsync(object param) => await Task;
        public IAyaMvcFramework GetFramework() => Framework;
        public void SetFramework(IAyaMvcFramework framework) => Framework = framework;
    }

    public class CommandAsync<T> : ICommandAsync<T>
    {
        public UniTask<T> Task { get; private set; }
        private IAyaMvcFramework Framework { get; set; }

        public CommandAsync(UniTask<T> executeAsync, IAyaMvcFramework framework)
        {
            Task = executeAsync;
            Framework = framework;
        }
        public async UniTask<T> ExecuteAsync(object param) => await Task;
        public IAyaMvcFramework GetFramework() => Framework;
        public void SetFramework(IAyaMvcFramework framework) => Framework = framework;

    }

    public class IoCContainer : IDisposable
    {
        private readonly ConcurrentDictionary<Type, object> _instances = new();

        private bool _isDisposed;

        public void RegisterIoCInstance<T>(T instance)
        {
            if (_isDisposed)
            {
                return;
            }

            var key = typeof(T);
            _instances[key] = instance;
        }

        public T GetInstance<T>()
        {
            if (_isDisposed)
            {
                return default;
            }

            var key = typeof(T);
            if (_instances.TryGetValue(key, out var instance))
            {
                return (T)instance;
            }

            return _instances.Values.OfType<T>().FirstOrDefault();
        }

        public IEnumerable<T> GetInstanceByType<T>() where T : class => _isDisposed ? null : _instances.Values.Where(ins => ins is T).Cast<T>();

        public void Clear()
        {
            foreach (var ins in _instances.Values)
            {
                switch (ins)
                {
                    case IInitializable init:
                        init.Shutdown();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }

            _instances.Clear();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Clear();
                _isDisposed = true;
            }
        }
    }

    public interface IAyaMvcFramework
    {
        T GetService<T>() where T : IService;
        void SendCommand<T>(T command, object param) where T : ICommand;
        UniTask<T> SendCommandAsync<T>(T command, object param) where T : ICommandAsync<T>;
        void RegisterService<T>(T service) where T : IService;
        void Shutdown();
    }

    public interface IGetFramework
    {
        public IAyaMvcFramework GetFramework();
    }

    public interface ISetFramework
    {
        public void SetFramework(IAyaMvcFramework framework);
    }

    public interface IGetService : IGetFramework, ISetFramework { }
    public interface ISetCommand : IGetFramework, ISetFramework { }

    public interface IInitializable
    {
        bool IsInitialized { get; }
        void Initialize();
        void Shutdown();
    }

    public interface IService : IGetService, ISetCommand, IInitializable { }
    public interface ICommand : IGetService, ISetCommand
    {
        Action Action { get; }
        void Execute(object param);
    }
    public interface ICommand<T> : IGetService, ISetCommand
    {
        Action<T> Action { get; }
        void Execute(T param);
    }
    public interface ICommandAsync : IGetService, ISetCommand
    {
        UniTask ExecuteAsync(object param);
    }
    public interface ICommandAsync<T> : IGetService, ISetCommand
    {
        UniTask<T> ExecuteAsync(object param);
    }

    public interface IEventBus
    {
        void Publish<TEvent>(TEvent evt);
        void Subscribe<TEvent>(Action<TEvent> handler);
        void Unsubscribe<TEvent>(Action<TEvent> handler);
    }

    public sealed class EventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = [];

        public void Publish<TEvent>(TEvent evt)
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                foreach (var handler in list)
                {
                    ((Action<TEvent>)handler)(evt);
                }
            }
        }

        public void Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }
            list.Add(handler);
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler)
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list.Remove(handler);
            }
        }
    }
}
