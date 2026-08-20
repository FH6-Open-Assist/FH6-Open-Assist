using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Core;

public sealed class AutomationCoordinator : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<MacroKind, IMacroWorkflow> _workflows;
    private readonly AutomationContext _context;
    private readonly AutomationLogger _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _disposeGate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _activeTask;
    private Task? _disposeTask;
    private volatile bool _endInProgress;
    private int _disposeState;

    public MacroKind? SelectedMacro { get; private set; }
    public MacroRunState State { get; private set; } = MacroRunState.Parado;

    public event Action<MacroRunState, MacroKind?, string>? StateChanged;

    public AutomationCoordinator(
        IEnumerable<IMacroWorkflow> workflows,
        AutomationContext context,
        AutomationLogger logger)
    {
        _workflows = workflows.ToDictionary(workflow => workflow.Kind);
        _context = context;
        _logger = logger;
        _context.RunNestedAsync = RunNestedAsync;
    }

    public bool Select(MacroKind kind)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return false;
        }

        if (_endInProgress || State is MacroRunState.Executando or MacroRunState.Parando)
        {
            _logger.Warn("Não é possível trocar o BOT durante uma execução ou encerramento.");
            return false;
        }

        SelectedMacro = kind;
        ChangeState(MacroRunState.Parado, $"{DisplayName(kind)} selecionado. Clique em Ativar BOT para armar.");
        return true;
    }

    public void ArmSelected()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        if (SelectedMacro is null)
        {
            _logger.Warn("Selecione um bot antes de ativar.");
            return;
        }

        if (_endInProgress || State is MacroRunState.Executando or MacroRunState.Parando)
        {
            _logger.Warn("Não é possível trocar a ativação durante uma execução ou encerramento.");
            return;
        }

        ChangeState(MacroRunState.Armado, $"{DisplayName(SelectedMacro.Value)} ativo. F8 inicia; F9 encerra.");
    }

    public async Task StartSelectedAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_endInProgress)
            {
                _logger.Warn("O BOT está sendo encerrado; ative-o novamente após a conclusão do F9.");
                return;
            }

            if (_activeTask is { IsCompleted: false } || State is MacroRunState.Executando or MacroRunState.Parando)
            {
                _logger.Warn("Já existe um macro em execução.");
                return;
            }

            if (SelectedMacro is null || State != MacroRunState.Armado)
            {
                _logger.Warn("Selecione um bot e clique em Ativar BOT antes de pressionar F8.");
                return;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var request = new MacroRunRequest(SelectedMacro.Value);
            _context.Telemetry.Begin(
                request.Kind,
                "Iniciando",
                $"Preparando {DisplayName(request.Kind)}.");
            ChangeState(MacroRunState.Executando, $"Iniciando {DisplayName(request.Kind)}.");
            _activeTask = ExecuteRootAsync(request, _cancellation.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        await StopCoreAsync(allowDuringDisposal: false);
    }

    private async Task StopCoreAsync(bool allowDuringDisposal)
    {
        Task? task;
        await _lifecycleLock.WaitAsync();
        try
        {
            if (!allowDuringDisposal && Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_activeTask is null || _activeTask.IsCompleted)
            {
                await _context.Input.ReleaseAllAsync();
                await _context.Capture.ReleaseSessionAsync();
                ChangeState(SelectedMacro is null ? MacroRunState.Parado : MacroRunState.Armado, "Nenhum macro está executando.");
                return;
            }

            ChangeState(MacroRunState.Parando, "F8 recebido: interrompendo e soltando todas as entradas.");
            _context.Telemetry.UpdateStatus(
                MacroRunState.Parando,
                "Interrompendo",
                "Cancelando o fluxo atual e liberando todas as entradas.");
            _cancellation?.Cancel();
            task = _activeTask;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        await _context.Input.ReleaseAllAsync();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Cancelamento solicitado pelo usuário.
        }
    }

    public async Task EndAsync()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_endInProgress)
            {
                _logger.Warn("O encerramento do BOT já está em andamento.");
                return;
            }

            _endInProgress = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        var stoppedSafely = false;
        try
        {
            await StopCoreAsync(allowDuringDisposal: false);
            stoppedSafely = true;
        }
        finally
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                if (stoppedSafely && Volatile.Read(ref _disposeState) == 0)
                {
                    ChangeState(MacroRunState.Parado, "BOT encerrado com F9. Clique em Ativar BOT para usar novamente.");
                    _context.Telemetry.Stop(
                        MacroRunState.Parado,
                        "Sessão encerrada",
                        "BOT desarmado com F9; todas as entradas foram liberadas.");
                }

                _endInProgress = false;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
    }

    private async Task ExecuteRootAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        var finalState = MacroRunState.Armado;
        var finalStage = "Sessão concluída";
        var finalDescription = $"{DisplayName(request.Kind)} finalizado.";
        var finalMessage = finalDescription;

        try
        {
            await ExecuteWorkflowAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info("Macro cancelado pelo usuário.");
            finalStage = "Sessão interrompida";
            finalDescription = "Fluxo cancelado com segurança; um novo F8 inicia outra sessão.";
            finalMessage = "Macro interrompido com segurança.";
        }
        catch (CalibrationRequiredException exception)
        {
            _logger.Error($"CALIBRAÇÃO NECESSÁRIA: {exception.Message}");
            finalState = MacroRunState.CalibracaoNecessaria;
            finalStage = "Calibração necessária";
            finalDescription = exception.Message;
            finalMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.Error($"Falha segura: {exception.Message}");
            finalState = MacroRunState.Falhou;
            finalStage = "Falha segura";
            finalDescription = exception.Message;
            finalMessage = exception.Message;
        }
        finally
        {
            string? cleanupFailure = null;
            try
            {
                await _context.Input.ReleaseAllAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure = $"Falha ao liberar entradas: {exception.Message}";
                _logger.Error(cleanupFailure);
            }

            try
            {
                await _context.Capture.ReleaseSessionAsync();
            }
            catch (Exception exception)
            {
                var captureFailure = $"Falha ao liberar a captura: {exception.Message}";
                cleanupFailure = cleanupFailure is null
                    ? captureFailure
                    : $"{cleanupFailure} {captureFailure}";
                _logger.Error(captureFailure);
            }

            if (cleanupFailure is not null)
            {
                finalState = MacroRunState.Falhou;
                finalStage = "Falha na liberação segura";
                finalDescription = cleanupFailure;
                finalMessage = cleanupFailure;
            }
        }

        if (finalState is MacroRunState.Falhou or MacroRunState.CalibracaoNecessaria)
        {
            _context.Telemetry.Failure(finalDescription, finalState, finalStage);
        }
        else
        {
            _context.Telemetry.Stop(finalState, finalStage, finalDescription);
        }

        ChangeState(finalState, finalMessage);
    }

    private Task RunNestedAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (!request.Nested)
        {
            request = request with { Nested = true };
        }

        _logger.Info($"Encadeando {DisplayName(request.Kind)} e preservando o retorno ao macro chamador.");
        return ExecuteWorkflowAsync(request, cancellationToken);
    }

    private Task ExecuteWorkflowAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (!_workflows.TryGetValue(request.Kind, out var workflow))
        {
            throw new AutomationFaultException($"Workflow não registrado: {request.Kind}.");
        }

        return workflow.RunAsync(_context, request, cancellationToken);
    }

    private void ChangeState(MacroRunState state, string message)
    {
        State = state;
        _logger.Info(message);
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<MacroRunState, MacroKind?, string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state, SelectedMacro, message);
            }
            catch (Exception exception)
            {
                _logger.Warn($"Um observador de estado falhou: {exception.Message}");
            }
        }
    }

    private static string DisplayName(MacroKind kind) => BotCatalog.Get(kind).Name;

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposeState, 1);
        try
        {
            await StopCoreAsync(allowDuringDisposal: true);
        }
        finally
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                _cancellation?.Dispose();
                _cancellation = null;
                _activeTask = null;
                _endInProgress = false;
                Volatile.Write(ref _disposeState, 2);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
    }
}

