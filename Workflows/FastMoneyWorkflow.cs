using System.Diagnostics;
using System.Runtime.InteropServices;
using ForzaFarm.Core;
using ForzaFarm.Windows;

namespace ForzaFarm.Workflows;

public sealed class FastMoneyWorkflow : IMacroWorkflow
{
    public MacroKind Kind => MacroKind.Farmar200kMin;

    public Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        const string workflow = "200kMin";
        _ = context.GameWindow.GetRequiredGameWindow();
        var mode = context.Settings.InputMode == InputMode.Foreground
            ? "primeiro plano (confiável)"
            : "segundo plano (experimental)";
        context.Logger.State(
            workflow,
            "Iniciar",
            $"Executando a gravação Pulover em {mode}, em uma thread dedicada de alta precisão.");

        return Task.Factory.StartNew(
            () => RunLoop(context, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void RunLoop(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "200kMin";
        var workerThread = Thread.CurrentThread;
        var originalPriority = workerThread.Priority;
        var priorityChanged = false;
        try
        {
            workerThread.Priority = ThreadPriority.AboveNormal;
            priorityChanged = true;
        }
        catch
        {
            // A resolução de 1 ms e o relógio monotônico continuam ativos
            // mesmo se o Windows recusar a alteração de prioridade.
        }

        var highResolutionTimerEnabled = timeBeginPeriod(1) == 0;
        try
        {
            var cycle = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cycle++;
                context.Logger.State(workflow, "Ciclo", $"Iniciando ciclo {cycle}.");
                DelayPrecisely(500, cancellationToken);
                RunCycle(context, cancellationToken);
                DelayPrecisely(2_000, cancellationToken);
                context.Logger.State(workflow, "Ciclo", $"Ciclo {cycle} concluído; reiniciando a sequência.");
            }
        }
        finally
        {
            if (highResolutionTimerEnabled)
            {
                _ = timeEndPeriod(1);
            }

            if (priorityChanged)
            {
                workerThread.Priority = originalPriority;
            }
        }
    }

    private static void RunCycle(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        DelayPrecisely(333, cancellationToken);
        DelayPrecisely(516, cancellationToken);
        Pulse(context, GameKey.Menu, 78, 766, cancellationToken);
        Pulse(context, GameKey.PageDown, 31, 265, cancellationToken);
        Pulse(context, GameKey.PageDown, 31, 265, cancellationToken);
        Pulse(context, GameKey.PageDown, 31, 265, cancellationToken);
        Pulse(context, GameKey.PageDown, 31, 265, cancellationToken);
        Pulse(context, GameKey.Enter, 31, 265, cancellationToken);
        DelayPrecisely(333, cancellationToken);
        Pulse(context, GameKey.Enter, 31, 500, cancellationToken);
        DelayPrecisely(333, cancellationToken);
        Pulse(context, GameKey.Backspace, 31, 400, cancellationToken);
        Pulse(context, GameKey.Up, 31, 265, cancellationToken);
        Pulse(context, GameKey.Enter, 31, 265, cancellationToken);
        Pulse(context, GameKey.NumPad1, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad7, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad9, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad3, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad9, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad3, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad6, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad9, 60, 100, cancellationToken);
        Pulse(context, GameKey.NumPad5, 60, 100, cancellationToken);
        Pulse(context, GameKey.Enter, 60, 200, cancellationToken);
        Pulse(context, GameKey.Down, 60, 100, cancellationToken);
        Pulse(context, GameKey.Enter, 60, 2_500, cancellationToken);
        Pulse(context, GameKey.Enter, 79, 1_878, cancellationToken);
        Pulse(context, GameKey.Enter, 140, 2_832, cancellationToken);
        Pulse(context, GameKey.Enter, 78, 12_518, cancellationToken);
        Pulse(context, GameKey.Enter, 79, 6_000, cancellationToken);
        Pulse(context, GameKey.W, 600, 188, cancellationToken);
        Pulse(context, GameKey.A, 200, 953, cancellationToken);
        Pulse(context, GameKey.A, 180, 1_485, cancellationToken);
        Pulse(context, GameKey.W, 300, 188, cancellationToken);
        DelayPrecisely(1_843, cancellationToken);
        Pulse(context, GameKey.Space, 25_000, 343, cancellationToken);
        Pulse(context, GameKey.Menu, 125, 1_313, cancellationToken);
        Pulse(context, GameKey.X, 125, 594, cancellationToken);
        Pulse(context, GameKey.Enter, 78, 2_422, cancellationToken);
        Pulse(context, GameKey.W, 700, 282, cancellationToken);
        Pulse(context, GameKey.A, 2_700, 735, cancellationToken);
        Pulse(context, GameKey.W, 156, 50, cancellationToken);
        Pulse(context, GameKey.W, 156, 50, cancellationToken);
        Pulse(context, GameKey.W, 312, 8_844, cancellationToken);
        Pulse(context, GameKey.Enter, 62, 800, cancellationToken);
        DelayPrecisely(12_000, cancellationToken);
    }

    private static void Pulse(
        AutomationContext context,
        GameKey key,
        int holdMilliseconds,
        int waitAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        context.Input.KeyDownAsync(key, cancellationToken).GetAwaiter().GetResult();
        try
        {
            DelayPrecisely(holdMilliseconds, cancellationToken);
        }
        finally
        {
            context.Input.KeyUpAsync(key, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (waitAfterMilliseconds > 0)
        {
            DelayPrecisely(waitAfterMilliseconds, cancellationToken);
        }
    }

    private static void DelayPrecisely(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() +
                       (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1_000);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = remainingTicks * 1_000d / Stopwatch.Frequency;
            if (remainingMilliseconds > 4)
            {
                var coarseWait = Math.Max(1, (int)Math.Floor(remainingMilliseconds - 2));
                _ = cancellationToken.WaitHandle.WaitOne(coarseWait);
                continue;
            }

            while (Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(32);
            }

            return;
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
