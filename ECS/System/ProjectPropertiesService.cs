namespace FluentDesigner.ECS.System;

public sealed class ProjectPropertiesService : Service
{
    public ProjectProperties ProjectProperties { get; } = new();
    public ProjectStatistics Statistics { get; } = new();
    public FileIOState FileIO { get; set; } = new();
}
