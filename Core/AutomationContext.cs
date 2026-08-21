using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Core;

public sealed record MacroRunRequest(
    MacroKind Kind,
    int? TargetSkillPoints = null,
    long? TargetCredits = null,
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
    public required GameContextDetector GameContext { get; init; }
    public required CrPositionClassifier CrPosition { get; init; }
    public required CrFarmSampleCollector CrFarmSamples { get; init; }
    public required ResourceTracker Resources { get; init; }
    public required SessionTelemetry Telemetry { get; init; }
    public required Func<MacroRunRequest, CancellationToken, Task> RunNestedAsync { get; set; }
}

public interface IMacroWorkflow
{
    MacroKind Kind { get; }

    Task RunAsync(AutomationContext context, MacroRunRequest request, CancellationToken cancellationToken);
}
