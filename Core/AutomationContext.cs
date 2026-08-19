using ForzaFarm.Vision;
using ForzaFarm.Windows;

namespace ForzaFarm.Core;

public sealed record MacroRunRequest(
    MacroKind Kind,
    int? TargetSkillPoints = null,
    TimeSpan? Duration = null,
    bool Nested = false);

public sealed class AutomationContext
{
    public required AutomationSettings Settings { get; init; }
    public required AutomationLogger Logger { get; init; }
    public required GameWindowService GameWindow { get; init; }
    public required GameInputService Input { get; init; }
    public required GameCaptureService Capture { get; init; }
    public required GameVisionService Vision { get; init; }
    public required ResourceTracker Resources { get; init; }
    public required Func<MacroRunRequest, CancellationToken, Task> RunNestedAsync { get; set; }
}

public interface IMacroWorkflow
{
    MacroKind Kind { get; }

    Task RunAsync(AutomationContext context, MacroRunRequest request, CancellationToken cancellationToken);
}
