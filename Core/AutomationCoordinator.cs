using FH6OpenAssist.Vision;
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
    private MacroKind? _activeRootKind;
    private CancellationToken _activeRootToken;
    private int _nestedDepth;
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

        _activeRootKind = request.Kind;
        _activeRootToken = cancellationToken;
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

            _activeRootKind = null;
            _activeRootToken = default;
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

    private async Task RunNestedAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (_activeRootKind is not { } activeRootKind ||
            !IsSpinFarmRoot(activeRootKind) ||
            request.Kind is not (MacroKind.FarmarSp or MacroKind.Farmar200kMin))
        {
            throw new AutomationFaultException(
                $"Encadeamento recusado: {_activeRootKind?.ToString() ?? "sem raiz"} -> {request.Kind}.");
        }

        if (cancellationToken != _activeRootToken || !cancellationToken.CanBeCanceled)
        {
            throw new AutomationFaultException(
                "O workflow encadeado não recebeu o token de cancelamento da sessão raiz.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var validSpRequest = request.Kind == MacroKind.FarmarSp &&
                             request.TargetSkillPoints == 999 &&
                             request.TargetCredits is null &&
                             request.Duration is null;
        var validCrRequest = request.Kind == MacroKind.Farmar200kMin &&
                             request.TargetCredits is > 0 and <= 999_999_999 &&
                             request.TargetSkillPoints is null &&
                             request.Duration is null;
        if (!validSpRequest && !validCrRequest)
        {
            throw new AutomationFaultException(
                "Contrato do workflow encadeado inválido: SP exige alvo 999; CR exige um alvo positivo; " +
                "metas cruzadas e duração não são permitidas.");
        }

        if (Interlocked.CompareExchange(ref _nestedDepth, 1, 0) != 0)
        {
            throw new AutomationFaultException(
                "Outro workflow encadeado já está ativo; chamadas filhas devem ser sequenciais.");
        }

        if (!request.Nested)
        {
            request = request with { Nested = true };
        }

        _logger.Info($"Encadeando {DisplayName(request.Kind)} e preservando o retorno ao macro chamador.");
        try
        {
            await ExecuteWorkflowAsync(request, cancellationToken);
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
                cleanupFailure = $"Falha ao liberar entradas no handoff: {exception.Message}";
            }

            try
            {
                await _context.Capture.ReleaseSessionAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanupFailure is null
                    ? $"Falha ao liberar captura no handoff: {exception.Message}"
                    : $"{cleanupFailure} Falha ao liberar captura no handoff: {exception.Message}";
            }
            finally
            {
                Volatile.Write(ref _nestedDepth, 0);
            }

            if (cleanupFailure is not null)
            {
                throw new AutomationFaultException(cleanupFailure);
            }
        }
    }

    private async Task ExecuteWorkflowAsync(MacroRunRequest request, CancellationToken cancellationToken)
    {
        if (!_workflows.TryGetValue(request.Kind, out var workflow))
        {
            throw new AutomationFaultException($"Workflow não registrado: {request.Kind}.");
        }

        await CancelPendingDestructiveDialogAsync(request.Kind, cancellationToken);
        await workflow.RunAsync(_context, request, cancellationToken);
    }

    private async Task CancelPendingDestructiveDialogAsync(
        MacroKind rootKind,
        CancellationToken cancellationToken)
    {
        var game = _context.GameWindow.TryGetGameWindow();
        if (game is null || game.IsMinimized)
        {
            return;
        }

        var promptConfirmations = 0;
        var removalConfirmations = 0;
        var purchaseConfirmations = 0;
        var modalConfirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var probe = await ProbePendingDestructiveDialogAsync(cancellationToken);
            promptConfirmations += probe.HasPrompt ? 1 : 0;
            removalConfirmations += probe.HasRemovalPrompt ? 1 : 0;
            purchaseConfirmations += probe.HasPurchasePrompt ? 1 : 0;
            modalConfirmations += probe.HasClassicalModal ? 1 : 0;
            await Task.Delay(120, cancellationToken);
        }

        if (promptConfirmations < 2)
        {
            if (modalConfirmations >= 2)
            {
                throw new CalibrationRequiredException(
                    "Há um diálogo central aberto, mas o OCR não confirmou se é compra ou remoção. " +
                    "Nenhum BOT será iniciado até a tela ser fechada manualmente.");
            }

            return;
        }

        if (removalConfirmations > 0 && purchaseConfirmations > 0)
        {
            throw new CalibrationRequiredException(
                "O OCR alternou entre confirmação de compra e remoção. Nenhuma entrada será enviada.");
        }

        if (removalConfirmations >= 2)
        {
            if (IsSpinFarmRoot(rootKind))
            {
                _logger.Info(
                    "Modal de remoção pendente entregue ao WheelSpin para cancelamento confirmado pela opção Não.");
                return;
            }

            throw new CalibrationRequiredException(
                "Há uma remoção de carro pendente. Ela não responde com segurança ao botão B; " +
                "inicie o WheelSpin para a recuperação específica ou feche o modal manualmente.");
        }

        if (purchaseConfirmations < 2)
        {
            throw new CalibrationRequiredException(
                "O prompt destrutivo não permaneceu identificado como compra ou remoção em duas capturas. " +
                "Nenhuma entrada será enviada.");
        }

        _logger.Warn(
            "Uma confirmação de compra ficou aberta de uma execução anterior; cancelando com B antes de iniciar o BOT.");
        await _context.Input.TapAsync(GameKey.Escape, cancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var consecutiveMisses = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProbePendingDestructiveDialogAsync(cancellationToken);
            consecutiveMisses = probe.HasPrompt || probe.HasClassicalModal
                ? 0
                : consecutiveMisses + 1;
            if (consecutiveMisses >= 2)
            {
                _logger.Info("Confirmação destrutiva pendente cancelada antes do novo workflow.");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new CalibrationRequiredException(
            "Uma confirmação de compra ou remoção continuou aberta; nenhum BOT será iniciado.");
    }

    private Task<PendingDestructiveDialogProbe> ProbePendingDestructiveDialogAsync(
        CancellationToken cancellationToken) =>
        _context.Vision.AnalyzeScreenAsync(
            (bitmap, document) =>
            {
                var normalizedText = GameVisionService.Normalize(document.Text);
                var hasRemovalPrompt = normalizedText.Contains(
                    GameVisionService.Normalize("QUER MESMO REMOVER"),
                    StringComparison.Ordinal);
                var hasPurchasePrompt = normalizedText.Contains(
                    GameVisionService.Normalize("QUER COMPRAR CARRO"),
                    StringComparison.Ordinal);
                var hasPrompt = hasRemovalPrompt || hasPurchasePrompt;
                var classical = new ClassicalGameStateDetector().Analyze(bitmap);
                var knownTravelPrompt =
                    normalizedText.Contains("VIAJAR PARA CASA", StringComparison.Ordinal) &&
                    normalizedText.Contains("QUER FAZER UMA VIAGEM", StringComparison.Ordinal);
                // Desconexão de controle tem recuperação própria no navegador.
                // Aqui o fallback visual bloqueia somente o layout clássico de
                // confirmação; a confirmação conhecida de viagem é tratada pelo
                // navegador com OCR + CV e deve chegar intacta ao workflow.
                var hasClassicalModal =
                    classical.Kind == ClassicalGameStateKind.ConfirmationDialog &&
                    !knownTravelPrompt;
                return new PendingDestructiveDialogProbe(
                    hasPrompt,
                    hasRemovalPrompt,
                    hasPurchasePrompt,
                    hasClassicalModal);
            },
            cancellationToken);

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

    private static bool IsSpinFarmRoot(MacroKind kind) =>
        kind is MacroKind.FarmarWheelspins or MacroKind.FarmarWheelspinsRevuelto;

    private sealed record PendingDestructiveDialogProbe(
        bool HasPrompt,
        bool HasRemovalPrompt,
        bool HasPurchasePrompt,
        bool HasClassicalModal);

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

