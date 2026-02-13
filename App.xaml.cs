using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;

namespace FluentDesigner
{

    public partial class App : Application
    {
        private Window? _window;
        public static Window MainWindow { get; private set; }
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindow = _window;
            _window.Activate();
        }
    }

    public class AyaMvcFrameworkImpl : AyaMvcFramework<AyaMvcFrameworkImpl>
    {
        protected override void Initialize()
        {
            EntryPoint.RegisterService(new RenderService());
            EntryPoint.RegisterService(new ConstantBufferService());
            EntryPoint.RegisterService(new InstanceBufferService());
            EntryPoint.RegisterService(new TextureService());
            EntryPoint.RegisterService(new EcsWorldService());
            EntryPoint.RegisterService(new GridService());
            EntryPoint.RegisterService(new CameraService());
            EntryPoint.RegisterService(new RenderLoopService());
            EntryPoint.RegisterService(new HierarchyService());
            EntryPoint.RegisterService(new OutlineService());
            EntryPoint.RegisterService(new InspectorService());
            EntryPoint.RegisterService(new ProjectPropertiesService());
            EntryPoint.RegisterService(new ScenePickerService());
            EntryPoint.RegisterService(new SceneLayoutService());
            EntryPoint.RegisterService(new PlaybackService());
            EntryPoint.RegisterService(new WaveformService());
            EntryPoint.RegisterService(new CsvMarkerService());
        }
    }
}
