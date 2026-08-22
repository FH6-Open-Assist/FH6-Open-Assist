using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class SpinFarmWorkflow : IMacroWorkflow
{
    private const int MaximumCardSearchMoves = 48;
    private const int FastNavigationPostDelayMs = 110;
    private const int MasteryAnimationDelayMs = 2_100;
    private const int RecoveryCheckpointVersion = 3;
    private const string RecoveryCheckpointFileName = "wheelspin-final-confirmed.json";
    private const int SpRefillIntentVersion = 2;
    private const int LegacyMadMikeSpRefillIntentVersion = 1;
    private const int SpRefillTarget = 999;
    private const int MaximumSpRefillAttempts = 6;
    private const string SpRefillIntentFileName = "wheelspin-sp-refill-intent.json";
    private const double FinalPerkLockedMagentaRatio = 0.04;
    private const double FinalPerkPurchasedMagentaRatio = 0.12;
    private static readonly RectangleF CurrentCarHeaderRegion = new(0.03f, 0.01f, 0.50f, 0.18f);
    private static readonly RectangleF RemovalNoOptionRegion = new(0.3225f, 0.5117f, 0.3540f, 0.0577f);
    private static readonly RectangleF RemovalYesOptionRegion = new(0.3225f, 0.5694f, 0.3540f, 0.0577f);
    private static readonly RectangleF[] VisibleCarCells = CreateVisibleCarCells();
    private static readonly JsonSerializerOptions RecoveryCheckpointJsonOptions = CreateRecoveryCheckpointJsonOptions();
    private readonly SpinFarmProfile _profile;

    internal SpinFarmWorkflow(SpinFarmProfile profile)
    {
        _profile = profile;
    }

    public MacroKind Kind => _profile.Kind;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        var navigator = new GameNavigator(context);
        var settings = _profile.GetSettings(context.Settings);
        if (settings.SkillPointsPerCar is < 1 or > 999 ||
            settings.CreditsPerCar < 1 ||
            settings.PreserveCredits < 0)
        {
            throw new AutomationFaultException(
                $"Configuração de {_profile.VehicleName} inválida: SkillPointsPerCar deve estar entre 1 e 999, " +
                "CreditsPerCar deve ser positivo e PreserveCredits não pode ser negativo.");
        }

        context.Logger.State(
            _profile.Workflow,
            "Recursos",
            "Normalizando a tela atual até a garagem e lendo SP e créditos disponíveis.");
        context.Telemetry.UpdateStage(
            "Lendo recursos",
            "Abrindo a garagem com uma rota visualmente confirmada antes de iniciar compras.");
        // Um controle virtual recém-conectado pode cobrir um modal destrutivo
        // pendente. Remova somente esse overlay conhecido antes de procurar e
        // cancelar a decisão original pela sua rota específica.
        await navigator.ReconnectControllerIfNeededAsync(cancellationToken);
        await CancelPendingDecisionAsync(context, cancellationToken);
        await navigator.EnsureGarageAsync(cancellationToken);
        var resources = await navigator.OpenMasteryAndReadAsync(
            cancellationToken,
            normalizeGarageMenu: true,
            startFromGarageHome: false);
        var credits = await ReadConfirmedCreditsAsync(
            context,
            navigator,
            "CreditosIniciaisConfirmados",
            cancellationToken);

        var normalizedMastery = GameVisionService.Normalize(resources.OcrText);
        var recoveryCheckpoint = LoadRecoveryCheckpoint(context);
        var spRefillIntent = LoadSpRefillIntent(context, resources.SkillPoints);
        if (recoveryCheckpoint is not null && spRefillIntent is not null)
        {
            throw new CalibrationRequiredException(
                "Existem checkpoints simultâneos de ciclo WheelSpin e reabastecimento de SP. " +
                "O BOT não removerá carro, gastará SP nem iniciará uma compra até o estado ser revisado.");
        }

        if (recoveryCheckpoint is not null)
        {
            var recoveryCompleted = false;
            switch (recoveryCheckpoint.Stage)
            {
                case SpinRecoveryStage.PurchaseAuthorized:
                case SpinRecoveryStage.PurchaseConfirmed:
                    var purchaseState = ClassifyPurchaseRecoveryState(
                        recoveryCheckpoint,
                        resources,
                        credits,
                        normalizedMastery,
                        settings.SkillPointsPerCar);
                    if (purchaseState == PurchaseRecoveryState.NotCommitted)
                    {
                        context.Logger.State(
                            _profile.Workflow,
                            "DescartarCompraAutorizada",
                            "A autorização persistida não debitou créditos e o carro atual não é o veículo-alvo; " +
                            "nenhuma compra foi adotada e um novo ciclo poderá começar.");
                        ClearRecoveryCheckpoint(context);
                        break;
                    }

                    if (purchaseState == PurchaseRecoveryState.FinalPerkCandidate)
                    {
                        context.Logger.State(
                            _profile.Workflow,
                            "VerificarCicloInterrompidoAposPerk",
                            "A compra e o débito total de SP batem com o recibo, mas outro carro já está ativo; " +
                            "o BOT verificará diretamente o perk do veículo-alvo antes de removê-lo.");
                        await RecoverFinalizedVehicleFromInventoryAsync(
                            context,
                            navigator,
                            recoveryCheckpoint,
                            cancellationToken);
                        recoveryCompleted = true;
                        break;
                    }

                    if (purchaseState == PurchaseRecoveryState.FinalPerkCurrentCandidate)
                    {
                        context.Logger.State(
                            _profile.Workflow,
                            "VerificarPerkFinalNoMadMikeAtual",
                            "Compra, débito total de SP, créditos e veículo-alvo atual coincidem com o checkpoint; " +
                            "o BOT confirmará que o perk final está Adquirido sem pressionar Enter.");
                        await CompletePartiallyUnlockedVehicleAsync(
                            context,
                            navigator,
                            resources,
                            cancellationToken);
                        recoveryCompleted = true;
                        break;
                    }

                    if (recoveryCheckpoint.Stage == SpinRecoveryStage.PurchaseAuthorized)
                    {
                        recoveryCheckpoint = recoveryCheckpoint with
                        {
                            Stage = SpinRecoveryStage.PurchaseConfirmed,
                            ConfirmedAtUtc = DateTimeOffset.UtcNow
                        };
                        SaveRecoveryCheckpoint(context, recoveryCheckpoint);
                    }

                    context.Logger.State(
                        _profile.Workflow,
                        "RetomarCompraConfirmada",
                        "Autorização, débito exato da compra, saldo de SP inalterado e veículo-alvo atual confirmados; " +
                        "retomando a Maestria desde o início.");
                    context.Telemetry.UpdateStage(
                        "Retomando ciclo WheelSpin",
                        "Compra deste ciclo confirmada pelo checkpoint; iniciando a Maestria sem comprar outro carro.");
                    await ResumePurchasedVehicleAsync(context, navigator, cancellationToken);
                    recoveryCompleted = true;
                    break;

                case SpinRecoveryStage.FinalPerkConfirmed:
                    await ResumeFinalPerkCheckpointAsync(
                        context,
                        navigator,
                        recoveryCheckpoint,
                        resources,
                        credits,
                        normalizedMastery,
                        cancellationToken);
                    recoveryCompleted = true;
                    break;

                case SpinRecoveryStage.RemovalAuthorized:
                    throw new CalibrationRequiredException(
                        "A remoção do veículo-alvo foi autorizada antes de uma interrupção, mas não há prova persistida " +
                        "de que o jogo a concluiu. O BOT não tentará remover outro carro. Revise a garagem, resolva manualmente " +
                        $"o carro deste ciclo e só então exclua o checkpoint: {RecoveryCheckpointPath(context)}");

                case SpinRecoveryStage.RemovalConfirmed:
                    context.Logger.State(
                        _profile.Workflow,
                        "RemocaoConfirmadaRetomada",
                        "O retorno estável à grade foi persistido após uma única confirmação de remoção; " +
                        "nenhuma nova remoção será enviada.");
                    recoveryCompleted = true;
                    break;

                default:
                    throw new CalibrationRequiredException(
                        "O checkpoint WheelSpin possui um estágio desconhecido. Nenhum SP será gasto e nenhum carro será removido.");
            }

            if (recoveryCompleted)
            {
                ClearRecoveryCheckpoint(context);
                context.Telemetry.CycleCompleted(
                    "Ciclo retomado: compra própria, Maestria, troca e remoção confirmadas visualmente.");

                await navigator.EnsureGarageAsync(cancellationToken);
                resources = await navigator.OpenMasteryAndReadAsync(
                    cancellationToken,
                    normalizeGarageMenu: true,
                    startFromGarageHome: false);
                credits = await ReadConfirmedCreditsAsync(
                    context,
                    navigator,
                    "CreditosAposRetomadaConfirmados",
                    cancellationToken);
            }
        }
        else if (IsTargetVehicleText(normalizedMastery))
        {
            throw new CalibrationRequiredException(
                "Há um veículo-alvo selecionado sem checkpoint de compra/perk final criado pelo BOT. " +
            "Por segurança, nenhum SP será gasto e nenhum carro será removido.");
        }

        var spRefillActive = spRefillIntent is not null;
        if (spRefillActive && resources.SkillPoints == SpRefillTarget)
        {
            ClearSpRefillIntent(context);
            spRefillIntent = null;
            spRefillActive = false;
            context.Logger.State(
                _profile.Workflow,
                "ConcluirReabastecimentoSPRetomado",
                $"A intenção persistida foi satisfeita pelo saldo exato de {SpRefillTarget} SP; " +
                "o checkpoint foi removido antes de autorizar compras.");
        }
        else if (spRefillActive)
        {
            context.Logger.State(
                _profile.Workflow,
                "RetomarReabastecimentoSP",
                $"A intenção persistida exige {SpRefillTarget} SP e a releitura confirmou " +
                $"{resources.SkillPoints} SP; o WheelSpin continuará o Farm de SP antes de qualquer compra.");
        }

        var spRefillAttempts = spRefillIntent?.Attempts ?? 0;
        var resumeInterruptedSpRefillAttempt =
            spRefillIntent is not null &&
            spRefillAttempts > 0 &&
            resources.SkillPoints > spRefillIntent.LastObservedSkillPoints &&
            resources.SkillPoints < SpRefillTarget;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spendableCredits = Math.Max(0, credits - settings.PreserveCredits);
            if (spRefillActive || spRefillAttempts > 0 || resources.SkillPoints < settings.SkillPointsPerCar)
            {
                if (context.Settings.InputMode != InputMode.BackgroundExperimental)
                {
                    throw new CalibrationRequiredException(
                        "O reabastecimento integrado de SP exige o modo Segundo plano experimental para usar " +
                        "Start e B de forma distinta na saída do EventLab. Selecione esse modo e tente novamente.");
                }

                if (!spRefillActive)
                {
                    spRefillIntent = CreateSpRefillIntent(context, resources.SkillPoints);
                    spRefillActive = true;
                }

                context.Logger.State(
                    _profile.Workflow,
                    "ReabastecerSP",
                    $"Saldo exato de {resources.SkillPoints} SP ainda não atingiu a meta de reabastecimento; " +
                    $"encadeando Farm de SP até {SpRefillTarget}.");
                var skillPointsBeforeRefill = resources.SkillPoints;
                if (!resumeInterruptedSpRefillAttempt &&
                    spRefillAttempts >= MaximumSpRefillAttempts)
                {
                    throw new CalibrationRequiredException(
                        $"O Farm de SP não atingiu 999 após {MaximumSpRefillAttempts} tentativas persistidas. " +
                        "O WheelSpin não repetirá o reabastecimento indefinidamente.");
                }

                if (resumeInterruptedSpRefillAttempt)
                {
                    context.Logger.State(
                        _profile.Workflow,
                        "RetomarTentativaSPInterrompida",
                        $"A releitura exata avançou de {spRefillIntent!.LastObservedSkillPoints} para " +
                        $"{resources.SkillPoints} SP desde a interrupção; retomando a tentativa persistida " +
                        $"{spRefillAttempts}/{MaximumSpRefillAttempts} sem consumir uma nova tentativa.");
                    resumeInterruptedSpRefillAttempt = false;
                }
                else
                {
                    spRefillAttempts++;
                }

                spRefillIntent = spRefillIntent! with
                {
                    Attempts = spRefillAttempts,
                    LastObservedSkillPoints = resources.SkillPoints
                };
                SaveSpRefillIntent(context, spRefillIntent, overwrite: true);

                context.Telemetry.UpdateStage(
                    "Reabastecendo SP",
                    "Saindo da garagem para farmar até 999 SP antes de retomar o WheelSpin.");
                await navigator.DriveAsync(cancellationToken);
                await context.RunNestedAsync(
                    new MacroRunRequest(
                        MacroKind.FarmarSp,
                        TargetSkillPoints: SpRefillTarget,
                        Nested: true),
                    cancellationToken);
                (resources, credits) = await ReadResourcesAfterHandoffAsync(context, navigator, cancellationToken);
                if (resources.SkillPoints == SpRefillTarget)
                {
                    ClearSpRefillIntent(context);
                    spRefillIntent = null;
                    spRefillActive = false;
                    spRefillAttempts = 0;
                    continue;
                }

                if (resources.SkillPoints > skillPointsBeforeRefill && resources.SkillPoints < SpRefillTarget)
                {
                    spRefillIntent = spRefillIntent! with
                    {
                        LastObservedSkillPoints = resources.SkillPoints
                    };
                    SaveSpRefillIntent(context, spRefillIntent, overwrite: true);
                    context.Logger.Warn(
                        $"O Farm de SP avançou de {skillPointsBeforeRefill} para {resources.SkillPoints}, " +
                        $"mas ainda não chegou a 999; novo handoff limitado " +
                        $"{spRefillAttempts}/{MaximumSpRefillAttempts} será calculado.");
                    continue;
                }

                if (resources.SkillPoints != SpRefillTarget)
                {
                    throw new CalibrationRequiredException(
                        $"O Farm de SP retornou sem progresso válido: {skillPointsBeforeRefill} -> {resources.SkillPoints} SP. " +
                        "O WheelSpin não será retomado.");
                }
            }

            if (spendableCredits < settings.CreditsPerCar)
            {
                var targetCredits = Math.Max(
                    10_000_000L,
                    checked((long)settings.PreserveCredits + settings.CreditsPerCar));
                context.Logger.State(
                    _profile.Workflow,
                    "ReabastecerCR",
                    $"Saldo disponível de {spendableCredits:N0} CR não permite outro ciclo; " +
                    $"encadeando Farm de CR até {targetCredits:N0}.");
                context.Telemetry.UpdateStage(
                    "Reabastecendo CR",
                    "Saindo da garagem para farmar até pelo menos 10.000.000 CR antes de retomar o WheelSpin.");
                await navigator.DriveAsync(cancellationToken);
                await context.RunNestedAsync(
                    new MacroRunRequest(
                        MacroKind.Farmar200kMin,
                        TargetCredits: targetCredits,
                        Nested: true),
                    cancellationToken);
                (resources, credits) = await ReadResourcesAfterHandoffAsync(context, navigator, cancellationToken);
                if (credits < targetCredits)
                {
                    throw new CalibrationRequiredException(
                        $"O Farm de CR retornou, mas a releitura confirmou somente {credits:N0} CR. " +
                        "O WheelSpin não será retomado.");
                }

                if (Math.Max(0, credits - settings.PreserveCredits) < settings.CreditsPerCar)
                {
                    throw new CalibrationRequiredException(
                        "A meta de 10.000.000 CR foi atingida, mas a reserva configurada ainda impede uma compra. " +
                        "Ajuste PreserveCredits antes de retomar; o BOT não repetirá o Farm de CR em loop.");
                }

                continue;
            }

            var purchasesBySp = resources.SkillPoints / settings.SkillPointsPerCar;
            var purchasesByCredits = spendableCredits / settings.CreditsPerCar;
            var purchases = (int)Math.Min(purchasesBySp, purchasesByCredits);
            context.Logger.State(
                _profile.Workflow,
                "PlanejarCompras",
                $"Saldo: {resources.SkillPoints} SP e {credits:N0} CR. " +
                $"É possível concluir {purchases} compra(s) de {settings.SkillPointsPerCar} SP e " +
                $"{settings.CreditsPerCar:N0} CR.");
            context.Telemetry.UpdateStage(
                "Planejando ciclos",
                $"Recursos confirmados para até {purchases} ciclo(s) completo(s).");

            for (var car = 1; car <= purchases; car++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Logger.State(_profile.Workflow, "CicloCompra", $"Carro {car}/{purchases}.");
                context.Telemetry.UpdateStage(
                    "Ciclo WheelSpin",
                    $"Carro {car}/{purchases}: iniciando compra, Maestria, troca e remoção.");
                await ExecuteCarCycleAsync(context, navigator, cancellationToken);
                context.Telemetry.CycleCompleted(
                    $"Carro {car}/{purchases}: compra, Maestria, troca e remoção confirmadas visualmente.");
            }

            context.Logger.State(
                _profile.Workflow,
                "LoteConcluido",
                $"{purchases} compra(s) concluída(s); relendo recursos antes do próximo handoff.");
            (resources, credits) = await ReadResourcesAfterHandoffAsync(context, navigator, cancellationToken);
        }
    }

    private async Task<(MasterySnapshot Resources, long Credits)> ReadResourcesAfterHandoffAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        context.Telemetry.UpdateStage(
            "Revalidando recursos",
            "Retornando à garagem e relendo SP e CR antes de autorizar o próximo ciclo.");
        await navigator.EnsureGarageAsync(cancellationToken);
        var resources = await navigator.OpenMasteryAndReadAsync(
            cancellationToken,
            normalizeGarageMenu: true,
            startFromGarageHome: false);
        var credits = await ReadConfirmedCreditsAsync(
            context,
            navigator,
            "CreditosAposHandoffConfirmados",
            cancellationToken);
        context.Logger.State(
            _profile.Workflow,
            "RecursosRevalidados",
            $"Handoff concluído com releitura exata: {resources.SkillPoints} SP e {credits:N0} CR.");
        return (resources, credits);
    }

    private async Task<long> ReadConfirmedCreditsAsync(
        AutomationContext context,
        GameNavigator navigator,
        string state,
        CancellationToken cancellationToken)
    {
        var observations = new List<long>(3);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            observations.Add(await navigator.ReadCreditsAsync(cancellationToken));
            if (attempt < 3)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        var consensus = observations
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault(group => group.Count() >= 2);
        if (consensus is null)
        {
            throw new CalibrationRequiredException(
                $"O saldo de CR não estabilizou em duas de três leituras: [{string.Join(", ", observations.Select(value => value.ToString("N0")))}]. " +
                "Nenhuma compra ou reabastecimento será autorizado.");
        }

        var credits = consensus.Key;
        context.Logger.State(
            _profile.Workflow,
            state,
            $"Saldo de CR confirmado em {consensus.Count()}/3 leituras: {credits:N0}.");
        return credits;
    }

    private PurchaseRecoveryState ClassifyPurchaseRecoveryState(
        SpinRecoveryCheckpoint checkpoint,
        MasterySnapshot resources,
        long credits,
        string normalizedMastery,
        int skillPointsPerCar)
    {
        var isTargetVehicle = IsTargetVehicleText(normalizedMastery);
        if (checkpoint.Stage == SpinRecoveryStage.PurchaseAuthorized &&
            resources.SkillPoints == checkpoint.SkillPointsBeforeMastery &&
            credits == checkpoint.CreditsBeforePurchase &&
            !isTargetVehicle)
        {
            return PurchaseRecoveryState.NotCommitted;
        }

        if (resources.SkillPoints == checkpoint.SkillPointsBeforeMastery &&
            credits == checkpoint.CreditsAfterPurchase &&
            isTargetVehicle)
        {
            return PurchaseRecoveryState.PurchasedCurrent;
        }

        var expectedAfterFinal = checkpoint.SkillPointsBeforeMastery - skillPointsPerCar;
        if (expectedAfterFinal >= 0 &&
            resources.SkillPoints == expectedAfterFinal &&
            credits == checkpoint.CreditsAfterPurchase)
        {
            return isTargetVehicle
                ? PurchaseRecoveryState.FinalPerkCurrentCandidate
                : PurchaseRecoveryState.FinalPerkCandidate;
        }

        throw new CalibrationRequiredException(
            "A autorização de compra WheelSpin não corresponde a um pós-estado seguro. " +
            $"SP checkpoint/tela: {checkpoint.SkillPointsBeforeMastery}/{resources.SkillPoints}; " +
            $"CR esperado sem compra: {checkpoint.CreditsBeforePurchase:N0}; " +
            $"CR esperado após compra: {checkpoint.CreditsAfterPurchase:N0}; tela: {credits:N0}; " +
            $"veículo-alvo atual: {(isTargetVehicle ? "sim" : "não")}. Nenhum SP será gasto.");
    }

    private async Task RecoverFinalizedVehicleFromInventoryAsync(
        AutomationContext context,
        GameNavigator navigator,
        SpinRecoveryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        context.Telemetry.UpdateStage(
            "Verificando ciclo interrompido",
            "Selecionando o veículo-alvo comprado e comprovando que o perk final já está adquirido.");
        await OpenMyCarsAsync(context, navigator, "RetomarMadMikeFinalizado", cancellationToken);
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await WaitForManufacturerOverlayAsync(
            context,
            "ListaFabricantesRetomarMadMikeFinalizado",
            cancellationToken);
        await SelectManufacturerAsync(
            context,
            "FiltrarFabricanteRetomarVeiculoFinalizado",
            _profile.Manufacturer,
            _profile.VehicleSearchTexts,
            cancellationToken);
        if (!await FocusTargetVehicleCardAsync(
                context,
                "MadMikeFinalizado",
                cancellationToken,
                allowAbsent: true))
        {
            context.Logger.State(
                _profile.Workflow,
                "CicloInterrompidoJaRemovido",
                "A busca limitada atravessou a seção da fabricante-alvo sem encontrar veículo-alvo; " +
                "os débitos exatos indicam que este ciclo já foi concluído e removido.");
            return;
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "EntrarMadMikeFinalizado",
            ["ENTRAR NO CARRO"],
            cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await WaitForTextToDisappearAsync(
            context,
            "TrocaParaMadMikeFinalizado",
            ["ENTRAR NO CARRO"],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "MadMikeFinalizadoAtivo",
            ["APRIMORAR E TUNAR"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        await ConfirmCurrentCarHeaderAsync(
            context,
            expectTargetVehicle: true,
            "MadMikeFinalizadoAtivo",
            cancellationToken);

        var mastery = await navigator.OpenMasteryAndReadAsync(cancellationToken);
        var expectedPoints = checkpoint.SkillPointsBeforeMastery - _profile.GetSettings(context.Settings).SkillPointsPerCar;
        if (mastery.SkillPoints != expectedPoints ||
            !IsTargetVehicleText(GameVisionService.Normalize(mastery.OcrText)))
        {
            throw new CalibrationRequiredException(
                "O veículo-alvo do ciclo interrompido foi selecionado, mas carro e SP não mantiveram o pós-estado esperado. " +
                "Nenhum perk será comprado nem carro removido.");
        }

        var normalized = GameVisionService.Normalize(mastery.OcrText);
        if (!IsFinalPerkText(normalized))
        {
            await TapRepeatedWithDelayAsync(context, GameKey.Left, 6, cancellationToken);
            await TapRepeatedWithDelayAsync(context, GameKey.Up, 6, cancellationToken);
            await TapRepeatedWithDelayAsync(context, GameKey.Right, 6, cancellationToken);
            await Task.Delay(450, cancellationToken);
        }

        var finalState = await ConfirmFinalPerkRecoveryStateAsync(context, cancellationToken);
        if (finalState != FinalPerkRecoveryState.Purchased)
        {
            throw new CalibrationRequiredException(
                "O débito de SP coincidiu com o ciclo, mas o nó final do veículo-alvo não foi confirmado como Adquirido. " +
                "Nenhum SP adicional será gasto e o carro não será removido.");
        }

        PromoteFinalPerkCheckpoint(context, mastery.SkillPoints);
        context.Logger.State(
            _profile.Workflow,
            "PerkFinalInterrompidoConfirmado",
            "Nó final Adquirido confirmado diretamente no veículo-alvo; retomando somente troca e remoção.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await SwitchToAnotherCarAsync(context, navigator, cancellationToken);
        await RemoveTargetVehicleAsync(context, navigator, cancellationToken);
    }

    private async Task ResumePurchasedVehicleAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        await UnlockMasteryAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Trocando de carro",
            "Ativando outro carro antes de remover o veículo-alvo retomado após a compra.");
        await SwitchToAnotherCarAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Removendo veículo-alvo",
            "Localizando e removendo o carro após concluir a Maestria retomada.");
        await RemoveTargetVehicleAsync(context, navigator, cancellationToken);
    }

    private async Task ResumeFinalPerkCheckpointAsync(
        AutomationContext context,
        GameNavigator navigator,
        SpinRecoveryCheckpoint checkpoint,
        MasterySnapshot resources,
        long credits,
        string normalizedMastery,
        CancellationToken cancellationToken)
    {
        if (checkpoint.SkillPointsAfterFinal is not { } skillPointsAfterFinal ||
            resources.SkillPoints != skillPointsAfterFinal)
        {
            throw new CalibrationRequiredException(
                "Existe uma retomada WheelSpin com perk final confirmado, mas o saldo de SP não corresponde ao checkpoint. " +
                $"SP checkpoint/tela: {checkpoint.SkillPointsAfterFinal?.ToString() ?? "ausente"}/{resources.SkillPoints}. " +
                "Nenhum carro será removido.");
        }

        context.Logger.State(
            _profile.Workflow,
            "ValidarCheckpointFinal",
            $"Checkpoint final confirmado com SP exato {skillPointsAfterFinal}. " +
            $"CR do recibo pós-compra={checkpoint.CreditsAfterPurchase:N0}; " +
            $"CR observado={credits:N0}; delta informativo={credits - checkpoint.CreditsAfterPurchase:+#,0;-#,0;0}.");

        if (IsTargetVehicleText(normalizedMastery))
        {
            context.Logger.State(
                _profile.Workflow,
                "RetomarCicloFinalConfirmado",
                "Checkpoint do perk final e veículo-alvo atual confirmados; retomando somente troca e remoção.");
            context.Telemetry.UpdateStage(
                "Retomando ciclo WheelSpin",
                "Confirmando o perk já adquirido antes da troca e remoção.");
            await CompletePartiallyUnlockedVehicleAsync(
                context,
                navigator,
                resources,
                cancellationToken);
            return;
        }

        context.Logger.State(
            _profile.Workflow,
            "RetomarRemocaoFinalConfirmada",
            "Checkpoint do perk final confirmado e outro carro já ativo; retomando somente a remoção.");
        context.Telemetry.UpdateStage(
            "Retomando remoção WheelSpin",
            "Validando o carro atual antes de localizar o veículo-alvo processado.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await RemoveTargetVehicleAsync(
            context,
            navigator,
            cancellationToken,
            allowAlreadyRemoved: true);
    }

    private async Task ExecuteCarCycleAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        context.Logger.State(
            _profile.Workflow,
            "CicloVisual",
            "Compra, Maestria, troca e remoção serão confirmadas visualmente em cada transição.");

        context.Telemetry.UpdateStage(
            "Comprando veículo-alvo",
            "Abrindo a concessionária e confirmando a compra do carro configurado.");
        await OpenDealerAsync(context, navigator, cancellationToken);
        await BuyTargetVehicleAsync(context, cancellationToken);
        context.Telemetry.UpdateStage(
            "Desbloqueando Maestria",
            "Aplicando os pontos e confirmando visualmente o perk final.");
        await UnlockMasteryAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Trocando de carro",
            "Ativando outro carro antes de remover o veículo-alvo utilizado.");
        await SwitchToAnotherCarAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Removendo veículo-alvo",
            "Localizando e removendo o carro após concluir a Maestria.");
        await RemoveTargetVehicleAsync(context, navigator, cancellationToken);
        ClearRecoveryCheckpoint(context);

        context.Logger.State(
            _profile.Workflow,
            "CicloVisualConcluido",
            "veículo-alvo comprado, Maestria confirmada, outro carro ativado e veículo-alvo removido.");
    }

    private async Task OpenDealerAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        if (await context.Vision.ContainsAnyTextAsync(["COMPRAR CARRO"], cancellationToken))
        {
            context.Logger.State(_profile.Workflow, "Concessionaria", "Tela Comprar Carro já aberta.");
            return;
        }

        await navigator.ReturnToGarageMenuAsync(cancellationToken);
        await navigator.OpenBuySellTabAsync(cancellationToken);
        context.Logger.State(
            _profile.Workflow,
            "Concessionaria",
            "Normalizando no topo e abrindo Concessionária com o controle virtual.");
        await TapRepeatedAsync(context, GameKey.Up, 8, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "ComprarCarro",
            ["COMPRAR CARRO"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
    }

    private async Task BuyTargetVehicleAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        context.Logger.State(_profile.Workflow, "Fabricante", "Abrindo Ir para Fabricante com Backspace.");
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await WaitForManufacturerOverlayAsync(context, "ListaFabricantesCompra", cancellationToken);
        await SelectManufacturerAsync(
            context,
            "SelecionarFabricanteCompra",
            _profile.Manufacturer,
            _profile.VehicleSearchTexts,
            cancellationToken);
        await FocusTargetVehicleCardAsync(context, "SelecionarMadMike", cancellationToken);

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "CoresFabricante",
            ["CORES DO FABRICANTE"],
            cancellationToken);
        context.Logger.State(
            _profile.Workflow,
            "CoresFabricante",
            "Cartão veículo-alvo aberto; cada próximo A dependerá do estado visual estável da compra.");

        var purchaseCompleted = false;
        var expectedPriceConfirmed = false;
        try
        {
            for (var step = 1; step <= 6; step++)
            {
                var screen = await WaitForStablePurchaseScreenAsync(context, cancellationToken);
                switch (screen.Kind)
                {
                    case PurchaseScreenKind.Colors:
                        context.Logger.State(
                            _profile.Workflow,
                            "ConfirmarCor",
                            $"Cores do Fabricante confirmada ({step}/6); escolhendo a cor padrão.");
                        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                        await Task.Delay(250, cancellationToken);
                        break;

                    case PurchaseScreenKind.PriceDetails:
                        expectedPriceConfirmed = true;
                        context.Logger.State(
                            _profile.Workflow,
                            "ConfirmarPreco",
                            $"Preço de {_profile.GetSettings(context.Settings).CreditsPerCar:N0} CR confirmado " +
                            $"dentro da faixa amarela ({step}/6); abrindo a confirmação.");
                        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                        await Task.Delay(250, cancellationToken);
                        break;

                    case PurchaseScreenKind.PurchaseModal:
                        if (!expectedPriceConfirmed)
                        {
                            throw await CreateFailureAsync(
                                context,
                                "ConfirmarCompraSemPrecoAnterior",
                                "O modal de compra apareceu sem uma confirmação anterior do preço na faixa amarela; " +
                                "nenhum Enter será enviado.");
                        }

                        await ConfirmPurchaseModalAsync(
                            context,
                            expectedPriceConfirmed,
                            cancellationToken);
                        SavePurchaseAuthorizationCheckpoint(context);
                        context.Logger.State(
                            _profile.Workflow,
                            "ConfirmarCompra",
                            "Preço confirmado na etapa anterior, e prompt com a opção exata Comprar focada " +
                            "foram confirmados em duas de três capturas; " +
                            "a autorização foi persistida antes do A.");
                        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

                        context.Logger.State(
                            _profile.Workflow,
                            "Apresentacao",
                            "Aguardando o fim da apresentação do carro comprado.");
                        await context.Vision.WaitForAnyTextAsync(
                            _profile.Workflow,
                            "FimApresentacao",
                            ["EXPLODIR", "MODO FOTO", "OCULTAR UI", "ALTERNAR ALTURA DA CÂMERA"],
                            cancellationToken,
                            TimeSpan.FromMinutes(4));
                        PromotePurchaseCheckpoint(context);
                        purchaseCompleted = true;
                        context.Resources.AdjustCredits(-_profile.GetSettings(context.Settings).CreditsPerCar);
                        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
                        return;

                    default:
                        throw await CreateFailureAsync(
                            context,
                            "EstadoCompraDesconhecido",
                            "A compra não permaneceu em Cores, preço amarelo ou confirmação conhecida; nenhum Enter adicional será enviado.");
                }
            }

            throw await CreateFailureAsync(
                context,
                "CompraSemProgresso",
                "A compra não chegou ao modal confirmado após seis transições limitadas.");
        }
        finally
        {
            if (!purchaseCompleted)
            {
                await TryCancelPendingPromptAsync(
                    context,
                    ["QUER COMPRAR CARRO"],
                    "CompraPendenteCancelada");
            }
        }
    }

    private async Task UnlockMasteryAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        // O primeiro B depois da apresentação do carro pode ser absorvido
        // enquanto os controles da câmera ainda aparecem. Reclassifique e
        // feche essa tela por uma rota limitada antes de navegar pelas abas;
        // LB nunca deve ser usado enquanto a apresentação ainda estiver ativa.
        await navigator.EnsureGarageAsync(cancellationToken);
        var mastery = await navigator.OpenMasteryAndReadAsync(cancellationToken);
        var normalizedCar = GameVisionService.Normalize(mastery.OcrText);
        if (!IsTargetVehicleText(normalizedCar))
        {
            throw new CalibrationRequiredException(
                $"A tela de Maestria não confirmou que o carro atual é {_profile.VehicleName}. " +
                "Nenhum ponto de habilidade foi gasto.");
        }

        context.Logger.State(
            _profile.Workflow,
            "ConfirmarVeiculoNaMaestria",
            $"{_profile.VehicleName} confirmado pelo OCR antes de gastar pontos de habilidade.");
        var settings = _profile.GetSettings(context.Settings);
        if (mastery.SkillPoints < settings.SkillPointsPerCar)
        {
            throw new AutomationFaultException(
                $"Há somente {mastery.SkillPoints} SP; são necessários {settings.SkillPointsPerCar}.");
        }

        if (_profile.NormalizeMasteryFocusToBottomLeft)
        {
            context.Logger.State(
                _profile.Workflow,
                "NormalizarRaizMaestria",
                "Levando o foco aos limites esquerdo e inferior da árvore antes do primeiro gasto de SP.");
            await TapRepeatedWithDelayAsync(context, GameKey.Left, 6, cancellationToken);
            await TapRepeatedWithDelayAsync(context, GameKey.Down, 6, cancellationToken);
        }

        var directions = _profile.MasteryDirections;
        double? finalPerkBaseline = null;
        for (var index = 0; index < directions.Count; index++)
        {
            await context.Vision.WaitForAnyTextAsync(
                _profile.Workflow,
                $"SelecionarPerk{index + 1}",
                ["SELECIONAR"],
                cancellationToken);
            var isFinalPerk = index == directions.Count - 1;
            var hasDedicatedTextCheck = _profile.MasteryTextChecks.Any(check =>
                check.PurchaseIndex == index);
            if (isFinalPerk)
            {
                finalPerkBaseline = await ConfirmFinalWheelspinPerkAsync(
                    context,
                    purchased: false,
                    baselineMagentaRatio: null,
                    "WheelspinFinalFocado",
                    cancellationToken);
            }
            else if (hasDedicatedTextCheck)
            {
                await ConfirmExpectedMasteryPerkAsync(
                    context,
                    index,
                    purchased: false,
                    cancellationToken);
            }

            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            // "Voltar" e "Desbloquear Tudo" já existem antes da compra e
            // não confirmam que a animação acabou. Durante essa animação o
            // Forza ignora o direcional seguinte. O teste real mostrou que o
            // marcador rosa e o débito de SP estabilizam em cerca de 2 s.
            context.Logger.State(
                _profile.Workflow,
                $"ConfirmarPerk{index + 1}",
                "Aguardando a animação de aquisição terminar antes do próximo direcional.");
            await Task.Delay(MasteryAnimationDelayMs, cancellationToken);
            if (!isFinalPerk && hasDedicatedTextCheck)
            {
                await ConfirmExpectedMasteryPerkAsync(
                    context,
                    index,
                    purchased: true,
                    cancellationToken);
            }

            if (directions[index] is { } direction)
            {
                await context.Input.TapAsync(direction, cancellationToken);
            }
        }

        await ConfirmFinalWheelspinPerkAsync(
            context,
            purchased: true,
            baselineMagentaRatio: finalPerkBaseline,
            "WheelspinFinalComprado",
            cancellationToken);

        var expectedPoints = Math.Max(0, mastery.SkillPoints - settings.SkillPointsPerCar);
        var confirmedPoints = await navigator.ReadMasterySkillPointsAsync(cancellationToken);
        if (confirmedPoints != expectedPoints)
        {
            throw new CalibrationRequiredException(
                $"O perk final apareceu, mas o débito de SP não foi confirmado. " +
                $"Esperado: {expectedPoints}; observado: {confirmedPoints}. O BOT não removerá nenhum carro.");
        }

        context.Logger.State(
            _profile.Workflow,
            "MaestriaConcluida",
            $"Perk final rosa e débito exato de {settings.SkillPointsPerCar} SP confirmados.");
        PromoteFinalPerkCheckpoint(context, confirmedPoints);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
    }

    private async Task CompletePartiallyUnlockedVehicleAsync(
        AutomationContext context,
        GameNavigator navigator,
        MasterySnapshot mastery,
        CancellationToken cancellationToken)
    {
        var normalized = GameVisionService.Normalize(mastery.OcrText);
        if (!IsTargetVehicleText(normalized))
        {
            throw new CalibrationRequiredException(
                "A retomada parcial perdeu a confirmação do veículo-alvo atual; nenhum SP foi gasto.");
        }

        if (!IsFinalPerkText(normalized))
        {
            context.Logger.State(
                _profile.Workflow,
                "LocalizarWheelspinFinalPendente",
                "Normalizando o foco na árvore e indo ao nó superior direito sem pressionar A.");
            // A malha possui limites estáveis. Esquerda leva à primeira coluna,
            // Cima à primeira linha e Direita ao nó final. Nenhum desses passos
            // gasta SP; o OCR localizado e o marcador CV abaixo ainda precisam
            // confirmar perk final antes do Enter.
            await TapRepeatedWithDelayAsync(context, GameKey.Left, 6, cancellationToken);
            await TapRepeatedWithDelayAsync(context, GameKey.Up, 6, cancellationToken);
            await TapRepeatedWithDelayAsync(context, GameKey.Right, 6, cancellationToken);
            await Task.Delay(450, cancellationToken);
        }

        var recoveryState = await ConfirmFinalPerkRecoveryStateAsync(context, cancellationToken);
        if (recoveryState != FinalPerkRecoveryState.Purchased)
        {
            throw new CalibrationRequiredException(
                "O checkpoint afirma que o perk final já foi adquirido, mas o nó perk final apareceu bloqueado ou inconclusivo. " +
                "Nenhum Enter será enviado, nenhum SP será gasto e o carro não será removido.");
        }

        var unchangedPoints = await navigator.ReadMasterySkillPointsAsync(cancellationToken);
        if (unchangedPoints != mastery.SkillPoints)
        {
            throw new CalibrationRequiredException(
                "O nó final está marcado como Adquirido, mas o saldo de SP mudou durante a confirmação. " +
                $"Antes: {mastery.SkillPoints}; agora: {unchangedPoints}. O BOT não removerá o carro.");
        }

        context.Logger.State(
            _profile.Workflow,
            "WheelspinFinalJaAdquirido",
            "perk final confirmado como Adquirido por OCR e preenchimento rosa; nenhum Enter foi enviado.");
        PromoteFinalPerkCheckpoint(context, unchangedPoints);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);

        context.Telemetry.UpdateStage(
            "Trocando de carro",
            "Ativando outro carro antes de remover o veículo-alvo retomado.");
        await SwitchToAnotherCarAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Removendo veículo-alvo",
            "Localizando e removendo o carro após concluir a Maestria retomada.");
        await RemoveTargetVehicleAsync(context, navigator, cancellationToken);
    }

    private async Task SwitchToAnotherCarAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        await ConfirmCurrentCarHeaderAsync(context, expectTargetVehicle: true, "MadMikeAtual", cancellationToken);
        await OpenMyCarsAsync(context, navigator, "TrocarCarro", cancellationToken);
        await MoveFocusAwayFromTargetVehicleAsync(context, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "EntrarOutroCarro",
            ["ENTRAR NO CARRO"],
            cancellationToken);
        // Entrar no Carro é a primeira opção e já vem focada.
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await WaitForTextToDisappearAsync(
            context,
            "TrocaDeCarroIniciada",
            ["ENTRAR NO CARRO"],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        context.Logger.State(
            _profile.Workflow,
            "AguardarTrocaDeCarro",
            "Aguardando a grade fechar e a opção Aprimorar e Tunar reaparecer na aba Carros.");
        _ = await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "OutroCarroConfirmado",
            ["APRIMORAR E TUNAR"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        await ConfirmCurrentCarIsNotTargetVehicleAsync(context, "OutroCarroConfirmado", cancellationToken);
    }

    private async Task RemoveTargetVehicleAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken,
        bool allowAlreadyRemoved = false)
    {
        context.Logger.State(
            _profile.Workflow,
            "RemoverCarro",
            "Normalizando o estado atual e abrindo Meus Carros antes da remoção identificada.");
        await ConfirmCurrentCarIsNotTargetVehicleAsync(context, "AntesDaRemocao", cancellationToken);
        await OpenMyCarsAsync(context, navigator, "RemoverCarro", cancellationToken);
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await WaitForManufacturerOverlayAsync(context, "ListaFabricantesRemocao", cancellationToken);

        await SelectManufacturerAsync(
            context,
            "FiltrarFabricante",
            _profile.Manufacturer,
            _profile.VehicleSearchTexts,
            cancellationToken);

        var targetVehicleFound = await FocusTargetVehicleCardAsync(
            context,
            "MadMikeRemocao",
            cancellationToken,
            allowAbsent: allowAlreadyRemoved);
        if (!targetVehicleFound)
        {
            context.Logger.State(
                _profile.Workflow,
                "RemocaoJaConcluida",
                "A seção da fabricante-alvo foi percorrida e a ausência do veículo-alvo permaneceu estável em duas de três capturas; " +
                "a remoção do checkpoint já havia sido concluída.");
            return;
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        var removeAction = await context.Vision.WaitForAnyTextAsync(
            _profile.Workflow,
            "AcoesDoMadMike",
            ["REMOVER CARRO DA GARAGEM"],
            cancellationToken);

        // Normalize no fim da lista para não depender do foco inicial nem do
        // primeiro direcional, que o jogo pode absorver enquanto o diálogo
        // termina de abrir. A última opção é denunciar/remover pintura; uma
        // posição acima é sempre Remover Carro da Garagem.
        await Task.Delay(800, cancellationToken);
        await TapRepeatedAsync(context, GameKey.Down, 8, cancellationToken);
        await TapRepeatedAsync(context, GameKey.Up, 1, cancellationToken);
        await ConfirmActionFocusedAsync(
            context,
            removeAction.Line,
            "RemoverCarroFocado",
            cancellationToken);
        var removalCompleted = false;
        try
        {
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await ConfirmRemovalDialogAsync(context, cancellationToken);
            // A confirmação do diálogo já exige em 2/3 frames que a opção
            // Não esteja focada. Não repita exatamente as mesmas capturas.
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            await ConfirmRemovalChoiceFocusedAsync(
                context,
                yes: true,
                "ConfirmarRemocaoSimFocado",
                cancellationToken);
            PromoteRemovalAuthorizedCheckpoint(context);
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await ConfirmRemovalProcessedAndGridStableAsync(context, cancellationToken);
            PromoteRemovalConfirmedCheckpoint(context);
            removalCompleted = true;
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        }
        finally
        {
            if (!removalCompleted)
            {
                await TryCancelPendingPromptAsync(
                    context,
                    ["QUER MESMO REMOVER"],
                    "RemocaoPendenteCancelada");
            }
        }
    }

    private async Task TryCancelPendingPromptAsync(
        AutomationContext context,
        IReadOnlyCollection<string> prompts,
        string state)
    {
        try
        {
            var promptConfirmations = 0;
            var modalConfirmations = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var probe = await ProbePendingPromptAsync(context, prompts, CancellationToken.None);
                promptConfirmations += probe.HasPrompt ? 1 : 0;
                modalConfirmations += probe.HasClassicalModal ? 1 : 0;

                if (attempt < 2)
                {
                    await Task.Delay(100, CancellationToken.None);
                }
            }

            if (promptConfirmations < 2)
            {
                if (modalConfirmations >= 2)
                {
                    throw new CalibrationRequiredException(
                        $"{state}: um modal continuou aberto, mas o OCR não confirmou o prompt esperado.");
                }

                return;
            }

            var isRemovalPrompt = prompts
                .Select(CompactText)
                .Any(prompt => prompt.Contains("QUERMESMOREMOVER", StringComparison.Ordinal));
            if (isRemovalPrompt)
            {
                context.Logger.Warn(
                    $"{state}: o modal de remoção permaneceu aberto; normalizando o foco em Não e cancelando sem remover o carro.");
                // A lista possui somente Não (acima) e Sim (abaixo). Up mantém
                // Não selecionado ou move de Sim para Não; o Enter continua
                // proibido até o texto exato e o contorno verde passarem em 2/3.
                await context.Input.TapAsync(GameKey.Up, CancellationToken.None);
                await ConfirmRemovalChoiceFocusedAsync(
                    context,
                    yes: false,
                    state + "NaoFocado",
                    CancellationToken.None);
                await context.Input.TapAsync(GameKey.Enter, CancellationToken.None);

                var removalDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                var removalMisses = 0;
                while (DateTime.UtcNow < removalDeadline)
                {
                    var probe = await ProbePendingPromptAsync(context, prompts, CancellationToken.None);
                    removalMisses = probe.HasPrompt || probe.HasClassicalModal ? 0 : removalMisses + 1;
                    if (removalMisses >= 2)
                    {
                        context.Logger.State(
                            _profile.Workflow,
                            state,
                            "Confirmação destrutiva cancelada pela opção Não em duas capturas consecutivas.");
                        return;
                    }

                    await Task.Delay(180, CancellationToken.None);
                }

                throw new CalibrationRequiredException(
                    $"{state}: o prompt de remoção continuou visível após confirmar a opção Não.");
            }

            context.Logger.Warn(
                $"{state}: uma confirmação destrutiva permaneceu aberta após interrupção; cancelando com B.");
            await context.Input.TapAsync(GameKey.Escape, CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var misses = 0;
            while (DateTime.UtcNow < deadline)
            {
                var probe = await ProbePendingPromptAsync(context, prompts, CancellationToken.None);
                misses = probe.HasPrompt || probe.HasClassicalModal
                    ? 0
                    : misses + 1;
                if (misses >= 2)
                {
                    context.Logger.State(
                        _profile.Workflow,
                        state,
                        "Confirmação destrutiva cancelada em duas capturas consecutivas.");
                    return;
                }

                await Task.Delay(180, CancellationToken.None);
            }

            throw new CalibrationRequiredException(
                $"{state}: o prompt destrutivo continuou visível após o cancelamento com B.");
        }
        catch (CalibrationRequiredException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalibrationRequiredException(
                $"{state}: não foi possível confirmar o cancelamento seguro do prompt: {exception.Message}");
        }
    }

    private Task<PendingPromptProbe> ProbePendingPromptAsync(
        AutomationContext context,
        IReadOnlyCollection<string> prompts,
        CancellationToken cancellationToken) =>
        context.Vision.AnalyzeScreenAsync(
            (bitmap, document) =>
            {
                var normalized = GameVisionService.Normalize(document.Text);
                var hasPrompt = prompts
                    .Select(GameVisionService.Normalize)
                    .Any(prompt => normalized.Contains(prompt, StringComparison.Ordinal));
                var classical = new ClassicalGameStateDetector().Analyze(bitmap);
                return new PendingPromptProbe(
                    hasPrompt,
                    classical.Kind == ClassicalGameStateKind.ConfirmationDialog);
            },
            cancellationToken);

    private async Task WaitForTextToDisappearAsync(
        AutomationContext context,
        string state,
        IReadOnlyCollection<string> texts,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var consecutiveMisses = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await context.Vision.ContainsAnyTextAsync(texts, cancellationToken))
            {
                consecutiveMisses = 0;
            }
            else
            {
                consecutiveMisses++;
                if (consecutiveMisses >= 2)
                {
                    context.Logger.State(
                        _profile.Workflow,
                        state,
                        $"Texto anterior desapareceu de duas capturas consecutivas: [{string.Join(" | ", texts)}].");
                    return;
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new CalibrationRequiredException(
            $"A transição '{state}' não foi confirmada: [{string.Join(" | ", texts)}] permaneceu visível.");
    }

    private async Task CancelPendingDecisionAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        string[] removalPrompts = ["QUER MESMO REMOVER"];
        if (await HasStablePromptAsync(context, removalPrompts, cancellationToken))
        {
            context.Logger.State(
                _profile.Workflow,
                "CancelarDecisaoPendente",
                "Modal de remoção pendente detectado antes da navegação; selecionando Não com confirmação OCR/CV.");
            await TryCancelPendingPromptAsync(
                context,
                removalPrompts,
                "RemocaoPendenteInicialCancelada");
        }

        string[] purchasePrompts = ["QUER COMPRAR CARRO"];
        if (await HasStablePromptAsync(context, purchasePrompts, cancellationToken))
        {
            context.Logger.State(
                _profile.Workflow,
                "CancelarDecisaoPendente",
                "Confirmação de compra pendente detectada antes da navegação; cancelando com B.");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
            await WaitForTextToDisappearAsync(
                context,
                "DecisaoPendenteCancelada",
                purchasePrompts,
                TimeSpan.FromSeconds(8),
                cancellationToken);
        }

        string[] pendingPrompts = [.. removalPrompts, .. purchasePrompts];
        if (await HasStablePromptAsync(context, pendingPrompts, cancellationToken))
        {
            throw await CreateFailureAsync(
                context,
                "CancelarDecisaoPendente",
                "Uma confirmação de compra ou remoção continuou aberta; o BOT não enviará entradas de navegação.");
        }
    }

    private async Task<bool> HasStablePromptAsync(
        AutomationContext context,
        IReadOnlyCollection<string> prompts,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await context.Vision.ContainsAnyTextAsync(prompts, cancellationToken))
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return confirmations >= 2;
    }

    private async Task<PurchaseScreenSnapshot> WaitForStablePurchaseScreenAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var expectedCredits = _profile.GetSettings(context.Settings).CreditsPerCar;
        PurchaseScreenKind lastKind = PurchaseScreenKind.Unknown;
        PurchaseScreenSnapshot? latest = null;
        var consecutive = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) => AnalyzePurchaseScreen(bitmap, document, expectedCredits),
                cancellationToken);

            // Um possível modal sempre ganha precedência. Seus campos e seu
            // realce ainda serão validados em capturas novas antes do Enter.
            if (latest.Kind == PurchaseScreenKind.PurchaseModal)
            {
                return latest;
            }

            if (latest.Kind == PurchaseScreenKind.Unknown)
            {
                lastKind = PurchaseScreenKind.Unknown;
                consecutive = 0;
            }
            else if (latest.Kind == lastKind)
            {
                consecutive++;
                if (consecutive >= 2)
                {
                    return latest;
                }
            }
            else
            {
                lastKind = latest.Kind;
                consecutive = 1;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw await CreateFailureAsync(
            context,
            "EstadoCompraInstavel",
            $"A tela de compra não estabilizou em um estado conhecido. Última leitura: {latest?.Evidence ?? "nenhuma"}.");
    }

    private async Task ConfirmPurchaseModalAsync(
        AutomationContext context,
        bool expectedPricePreviouslyConfirmed,
        CancellationToken cancellationToken)
    {
        var expectedCredits = _profile.GetSettings(context.Settings).CreditsPerCar;
        var confirmations = 0;
        var bestFocusRatio = 0d;
        string lastEvidence = "modal não reconhecido";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var check = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var snapshot = AnalyzePurchaseScreen(bitmap, document, expectedCredits);
                    if (snapshot.Kind != PurchaseScreenKind.PurchaseModal ||
                        (!snapshot.HasExpectedPrice && !expectedPricePreviouslyConfirmed) ||
                        snapshot.PurchaseAction is null)
                    {
                        return new PurchaseModalCheck(false, 0, snapshot.Evidence);
                    }

                    // O modal tem largura variável: a ROI fixa de 60% da tela
                    // diluía uma borda real de ~35% para 58%, abaixo do próprio
                    // limiar. A ação ainda é a linha OCR exata "COMPRAR"; escolha
                    // apenas entre ROIs centrais calibradas e exija borda lime
                    // acima e abaixo antes de autorizar o Enter.
                    var focusRatio = BestActionFocusScore(bitmap, snapshot.PurchaseAction);
                    return new PurchaseModalCheck(
                        focusRatio >= 0.55,
                        focusRatio,
                        $"{snapshot.Evidence}; preço anterior confirmado={(expectedPricePreviouslyConfirmed ? "sim" : "não")}; " +
                        $"foco bilateral={focusRatio:P2}");
                },
                cancellationToken);
            bestFocusRatio = Math.Max(bestFocusRatio, check.FocusRatio);
            lastEvidence = check.Evidence;
            if (check.Valid)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            throw await CreateFailureAsync(
                context,
                "ConfirmarCompra",
                $"Após confirmar {expectedCredits:N0} CR na etapa anterior, o modal não confirmou em duas capturas " +
                $"o prompt e a opção exata Comprar com realce verde. Melhor foco={bestFocusRatio:P2}; " +
                $"última leitura={lastEvidence}.");
        }
    }

    private async Task ConfirmRemovalProcessedAndGridStableAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var consecutiveConfirmations = 0;
        string lastEvidence = "nenhuma captura";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var classical = new ClassicalGameStateDetector().Analyze(bitmap);
                    var normalized = GameVisionService.Normalize(document.Text);
                    var hasRemovalPrompt = CompactText(normalized)
                        .Contains("QUERMESMOREMOVER", StringComparison.Ordinal);
                    var hasBlockingModal = classical.Kind is
                        ClassicalGameStateKind.ConfirmationDialog or
                        ClassicalGameStateKind.ControllerDisconnected;
                    var grid = AnalyzeTargetVehicleGrid(bitmap, document);
                    var valid = !hasRemovalPrompt &&
                                !hasBlockingModal &&
                                grid.IsCarGrid &&
                                grid.FocusedCell >= 0;
                    return new RemovalCompletionProbe(
                        valid,
                        $"prompt={(hasRemovalPrompt ? "sim" : "não")}, " +
                        $"estrutura={classical.Kind}, grade={(grid.IsCarGrid ? "sim" : "não")}, " +
                        $"foco={grid.FocusedCell}");
                },
                cancellationToken);
            lastEvidence = probe.Evidence;
            consecutiveConfirmations = probe.Valid ? consecutiveConfirmations + 1 : 0;
            if (consecutiveConfirmations >= 2)
            {
                context.Logger.State(
                    _profile.Workflow,
                    "RemocaoConcluida",
                    "O modal desapareceu e a grade focada reapareceu sem estrutura clássica conflitante " +
                    "em duas capturas consecutivas.");
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw await CreateFailureAsync(
            context,
            "RemocaoProcessadaInconclusiva",
            "A remoção foi autorizada, mas o retorno estável à grade sem modal não foi confirmado. " +
            $"Nenhuma nova remoção será tentada. Última evidência: {lastEvidence}.");
    }

    private PurchaseScreenSnapshot AnalyzePurchaseScreen(
        Bitmap bitmap,
        OcrDocument document,
        int expectedCredits)
    {
        var compactDocument = CompactText(document.Text);
        var lines = document.Lines
            .Select(line => (Line: line, Compact: CompactText(line.Text)))
            .ToArray();
        var purchasePrompt = lines
            .FirstOrDefault(item => item.Compact.Contains("QUERCOMPRARCARRO", StringComparison.Ordinal))
            .Line;
        var purchaseAction = lines
            .FirstOrDefault(item => item.Compact == "COMPRAR")
            .Line;
        var modalDetected = purchasePrompt is not null ||
                            compactDocument.Contains("COMPRARVALESCARRO", StringComparison.Ordinal);
        if (modalDetected)
        {
            var expectedPrice = purchasePrompt is not null &&
                                ContainsExpectedPrice(purchasePrompt.Text, expectedCredits);
            return new PurchaseScreenSnapshot(
                PurchaseScreenKind.PurchaseModal,
                purchaseAction,
                expectedPrice,
                $"modal=sim, prompt={(purchasePrompt is null ? "não" : "sim")}, " +
                $"preço={(expectedPrice ? expectedCredits.ToString("N0") : "inconclusivo")}, " +
                $"ação={(purchaseAction is null ? "ausente" : "COMPRAR")}");
        }

        var priceLine = lines
            .Where(item => IsExpectedStandalonePrice(item.Compact, expectedCredits))
            .Select(item => new
            {
                item.Line,
                YellowRatio = YellowPriceFillRatio(bitmap, item.Line)
            })
            .OrderByDescending(item => item.YellowRatio)
            .FirstOrDefault();
        if (priceLine is not null && priceLine.YellowRatio >= 0.30)
        {
            return new PurchaseScreenSnapshot(
                PurchaseScreenKind.PriceDetails,
                null,
                true,
                $"preço amarelo {expectedCredits:N0} CR={priceLine.YellowRatio:P1}");
        }

        if (compactDocument.Contains("CORESDOFABRICANTE", StringComparison.Ordinal))
        {
            return new PurchaseScreenSnapshot(
                PurchaseScreenKind.Colors,
                null,
                false,
                "título exato Cores do Fabricante");
        }

        return new PurchaseScreenSnapshot(
            PurchaseScreenKind.Unknown,
            null,
            false,
            $"OCR={Shorten(GameVisionService.Normalize(document.Text))}");
    }

    private bool IsExpectedStandalonePrice(string compact, int expectedCredits)
    {
        var digits = expectedCredits.ToString();
        var normalizedDigits = compact.Replace('O', '0');
        return normalizedDigits == digits ||
               normalizedDigits == $"CR{digits}" ||
               normalizedDigits == $"{digits}CR";
    }

    private bool ContainsExpectedPrice(string text, int expectedCredits)
    {
        var digits = expectedCredits.ToString();
        var normalizedDigits = CompactText(text).Replace('O', '0');
        return Regex.IsMatch(
            normalizedDigits,
            $@"(?<![0-9]){Regex.Escape(digits)}(?![0-9])",
            RegexOptions.CultureInvariant);
    }

    private string CompactText(string text) =>
        Regex.Replace(
            GameVisionService.Normalize(text),
            @"[^A-Z0-9]",
            string.Empty,
            RegexOptions.CultureInvariant);

    private double YellowPriceFillRatio(Bitmap bitmap, OcrLine priceLine)
    {
        var centerX = priceLine.Center.X / (double)bitmap.Width;
        var centerY = priceLine.Center.Y / (double)bitmap.Height;
        var width = Math.Clamp((float)(priceLine.Width / bitmap.Width * 1.65), 0.14f, 0.36f);
        var height = Math.Clamp((float)(priceLine.Height / bitmap.Height * 2.8), 0.055f, 0.14f);
        var region = new RectangleF(
            Math.Clamp((float)centerX - width / 2, 0, 1 - width),
            Math.Clamp((float)centerY - height / 2, 0, 1 - height),
            width,
            height);
        var pixels = ToPixels(bitmap, region);
        var matching = 0;
        var sampled = 0;
        for (var y = pixels.Top; y < pixels.Bottom; y += 2)
        {
            for (var x = pixels.Left; x < pixels.Right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 190 && color.G >= 155 && color.B <= 90 && color.R >= color.G * 0.72)
                {
                    matching++;
                }
            }
        }

        return sampled == 0 ? 0 : matching / (double)sampled;
    }

    private RectangleF ActionSelectionRegion(
        Bitmap bitmap,
        OcrLine action,
        float width = 0.60f,
        float height = 0.12f)
    {
        var centerX = (float)(action.Center.X / (double)bitmap.Width);
        var centerY = (float)(action.Center.Y / (double)bitmap.Height);
        return new RectangleF(
            Math.Clamp(centerX - width / 2, 0, 1 - width),
            Math.Clamp(centerY - height / 2, 0, 1 - height),
            width,
            height);
    }

    private double BestActionFocusScore(Bitmap bitmap, OcrLine action)
    {
        ReadOnlySpan<float> widths = [0.32f, 0.40f, 0.50f, 0.60f];
        ReadOnlySpan<float> heights = [0.09f, 0.11f, 0.13f];
        var best = 0d;
        foreach (var width in widths)
        {
            foreach (var height in heights)
            {
                best = Math.Max(
                    best,
                    LimeHorizontalBorderScore(
                        bitmap,
                        ActionSelectionRegion(bitmap, action, width, height)));
            }
        }

        return best;
    }

    private double LimeHorizontalBorderScore(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var region = ToPixels(bitmap, normalizedRegion);
        var centerY = region.Top + region.Height / 2;
        var bestAbove = 0d;
        var bestBelow = 0d;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var matching = 0;
            var sampled = 0;
            for (var x = region.Left; x < region.Right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 130 && color.G >= 180 && color.B <= 90 && color.G > color.B * 2)
                {
                    matching++;
                }
            }

            var coverage = sampled == 0 ? 0 : matching / (double)sampled;
            if (y < centerY)
            {
                bestAbove = Math.Max(bestAbove, coverage);
            }
            else
            {
                bestBelow = Math.Max(bestBelow, coverage);
            }
        }

        return Math.Min(bestAbove, bestBelow);
    }

    private async Task ConfirmActionFocusedAsync(
        AutomationContext context,
        OcrLine action,
        string state,
        CancellationToken cancellationToken)
    {
        var expectedAction = CompactText(action.Text);
        var confirmations = 0;
        var bestFocusScore = 0d;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var focusScore = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var matchingLine = document.Lines
                        .Where(line => CompactText(line.Text) == expectedAction)
                        .OrderBy(line => Math.Abs(line.Center.X - action.Center.X) + Math.Abs(line.Center.Y - action.Center.Y))
                        .FirstOrDefault();
                    return matchingLine is null ? 0 : BestActionFocusScore(bitmap, matchingLine);
                },
                cancellationToken);
            bestFocusScore = Math.Max(bestFocusScore, focusScore);
            if (focusScore >= 0.55)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            throw await CreateFailureAsync(
                context,
                state,
                $"A opção exata '{action.Text}' não manteve bordas verdes superior e inferior em duas capturas " +
                $"antes do Enter. Melhor foco={bestFocusScore:P1}.");
        }

        context.Logger.State(
            _profile.Workflow,
            state,
            $"Opção exata '{action.Text}' confirmada pelo realce verde bilateral em duas de três capturas.");
    }

    private async Task ConfirmRemovalChoiceFocusedAsync(
        AutomationContext context,
        bool yes,
        string state,
        CancellationToken cancellationToken)
    {
        var region = yes ? RemovalYesOptionRegion : RemovalNoOptionRegion;
        var confirmations = 0;
        var bestRatio = 0d;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ratio = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var compact = CompactText(document.Text);
                    if (!compact.Contains("REMOVERCARRODAGARAGEM", StringComparison.Ordinal) ||
                        !compact.Contains("QUERMESMOREMOVER", StringComparison.Ordinal))
                    {
                        return 0;
                    }

                    return LimeVerticalBorderRatioWithHorizontalTolerance(
                        bitmap,
                        region,
                        horizontalToleranceRatio: 0.020);
                },
                cancellationToken);
            bestRatio = Math.Max(bestRatio, ratio);
            if (ratio >= 0.10)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(140, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            throw await CreateFailureAsync(
                context,
                state,
                $"A opção {(yes ? "Sim" : "Não")} do modal de remoção não manteve o contorno verde " +
                $"em duas capturas. Melhor razão={bestRatio:P1}.");
        }

        context.Logger.State(
            _profile.Workflow,
            state,
            $"Opção {(yes ? "Sim" : "Não")} confirmada pelo contorno verde em duas de três capturas.");
    }

    private async Task ConfirmRemovalDialogAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        string lastEvidence = "prompt não reconhecido";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var check = await context.Vision.AnalyzeScreenAsync(
                AnalyzeRemovalDialog,
                cancellationToken);
            lastEvidence = check.Evidence;
            if (check.Valid)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            throw await CreateFailureAsync(
                context,
                "ConfirmarRemocao",
                "O diálogo final não confirmou título, pergunta e opções Sim/Não em duas capturas. " +
                $"Última leitura: {lastEvidence}.");
        }

        context.Logger.State(
            _profile.Workflow,
            "ConfirmarRemocao",
            "Título, pergunta e opções Sim/Não do modal confirmados na mesma área central em duas de três capturas; " +
            "a identidade veículo-alvo já foi fixada pelo cartão e pela ação exata imediatamente anteriores.");
    }

    private RemovalDialogCheck AnalyzeRemovalDialog(Bitmap bitmap, OcrDocument document)
    {
        var classical = new ClassicalGameStateDetector().Analyze(bitmap);
        if (classical.Kind is not (
                ClassicalGameStateKind.ConfirmationDialog or
                ClassicalGameStateKind.ControllerDisconnected))
        {
            return new RemovalDialogCheck(false, $"estrutura clássica={classical.Kind}");
        }

        var lines = document.Lines
            .Select(line => (Line: line, Compact: CompactText(line.Text)))
            .ToArray();
        var prompts = lines
            .Where(item => item.Compact.Contains("QUERMESMOREMOVER", StringComparison.Ordinal))
            .Select(item => item.Line)
            .ToArray();
        if (prompts.Length == 0)
        {
            return new RemovalDialogCheck(false, "prompt QUER MESMO REMOVER ausente");
        }

        var hasRemovalTitle = lines.Any(item =>
            item.Compact == "REMOVERCARRODAGARAGEM" &&
            IsInsideRemovalModal(bitmap, item.Line));
        if (!hasRemovalTitle)
        {
            return new RemovalDialogCheck(false, "título REMOVER CARRO DA GARAGEM ausente no modal");
        }

        var noFocusRatio = LimeVerticalBorderRatioWithHorizontalTolerance(
            bitmap,
            RemovalNoOptionRegion,
            horizontalToleranceRatio: 0.020);
        if (noFocusRatio < 0.10)
        {
            return new RemovalDialogCheck(
                false,
                $"opção Não padrão sem contorno verde suficiente ({noFocusRatio:P1})");
        }

        return prompts.Any(prompt => IsInsideRemovalModal(bitmap, prompt))
            ? new RemovalDialogCheck(true, "título, pergunta genérica e opção Não focada no modal central")
            : new RemovalDialogCheck(false, "prompt de remoção fora da área central do modal");
    }

    private bool IsInsideRemovalModal(Bitmap bitmap, OcrLine line)
    {
        var centerX = line.Center.X / (double)bitmap.Width;
        var centerY = line.Center.Y / (double)bitmap.Height;
        return centerX is >= 0.28 and <= 0.72 && centerY is >= 0.30 and <= 0.72;
    }

    private async Task<FinalPerkRecoveryState> ConfirmFinalPerkRecoveryStateAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var locked = 0;
        var purchased = 0;
        var ratios = new List<double>(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var normalized = GameVisionService.Normalize(document.Text);
                    if (!IsFinalPerkText(normalized))
                    {
                        return new FinalPerkRecoveryObservation(FinalPerkRecoveryState.Unknown, 0);
                    }

                    var magentaRatio = MagentaFillRatio(bitmap, _profile.FinalPerkIconRegion);
                    if (normalized.Contains("ADQUIRIDO", StringComparison.Ordinal) &&
                        magentaRatio >= FinalPerkPurchasedMagentaRatio)
                    {
                        return new FinalPerkRecoveryObservation(FinalPerkRecoveryState.Purchased, magentaRatio);
                    }

                    if (normalized.Contains("SELECIONAR", StringComparison.Ordinal) &&
                        magentaRatio <= FinalPerkLockedMagentaRatio)
                    {
                        return new FinalPerkRecoveryObservation(FinalPerkRecoveryState.Locked, magentaRatio);
                    }

                    return new FinalPerkRecoveryObservation(FinalPerkRecoveryState.Unknown, magentaRatio);
                },
                cancellationToken);
            ratios.Add(observation.MagentaRatio);
            locked += observation.State == FinalPerkRecoveryState.Locked ? 1 : 0;
            purchased += observation.State == FinalPerkRecoveryState.Purchased ? 1 : 0;
            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (locked >= 2 && purchased == 0)
        {
            return FinalPerkRecoveryState.Locked;
        }

        if (purchased >= 2 && locked == 0)
        {
            return FinalPerkRecoveryState.Purchased;
        }

        throw await CreateFailureAsync(
            context,
            "EstadoWheelspinFinalInconclusivo",
            "O nó perk final não estabilizou como pendente ou Adquirido em duas de três capturas. " +
            $"Preenchimento rosa observado: {string.Join(", ", ratios.Select(value => value.ToString("P2")))}.");
    }

    private async Task ConfirmExpectedMasteryPerkAsync(
        AutomationContext context,
        int purchaseIndex,
        bool purchased,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        var conflicts = 0;
        var observations = new List<string>(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(document.Text);
            observations.Add(Shorten(normalized));
            var hasExpectedState = normalized.Contains(
                purchased ? "ADQUIRIDO" : "SELECIONAR",
                StringComparison.Ordinal);
            if (_profile.MatchesMasteryText(purchaseIndex, normalized) && hasExpectedState)
            {
                confirmations++;
            }
            else if (hasExpectedState)
            {
                conflicts++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (confirmations < 2 || conflicts > 0)
        {
            throw await CreateFailureAsync(
                context,
                $"PerkEspecifico{purchaseIndex + 1}{(purchased ? "Adquirido" : "Focado")}",
                $"O perk específico da etapa {purchaseIndex + 1} não confirmou " +
                $"{(purchased ? "Adquirido" : "Selecionar")} e o texto esperado em duas de três capturas sem conflito. " +
                $"OCR: '{string.Join(" | ", observations)}'.");
        }

        context.Logger.State(
            _profile.Workflow,
            $"PerkEspecifico{purchaseIndex + 1}{(purchased ? "Adquirido" : "Focado")}",
            $"Texto e estado do perk específico {purchaseIndex + 1} confirmados em duas de três capturas.");
    }

    private async Task<double> ConfirmFinalWheelspinPerkAsync(
        AutomationContext context,
        bool purchased,
        double? baselineMagentaRatio,
        string state,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        var observedRatios = new List<double>(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observation = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var normalized = GameVisionService.Normalize(document.Text);
                    if (!IsFinalPerkText(normalized))
                    {
                        return new FinalPerkObservation(false, 0);
                    }

                    var magentaRatio = MagentaFillRatio(bitmap, _profile.FinalPerkRegion);
                    var valid = purchased
                        ? baselineMagentaRatio is { } baseline && magentaRatio >= baseline + 0.004
                        : normalized.Contains("SELECIONAR", StringComparison.Ordinal);
                    return new FinalPerkObservation(valid, magentaRatio);
                },
                cancellationToken);
            observedRatios.Add(observation.MagentaRatio);
            if (observation.Valid)
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        if (confirmations < 2)
        {
            throw await CreateFailureAsync(
                context,
                state,
                purchased
                    ? "O nó Wheelspin não apresentou aumento estável do marcador rosa em duas capturas após a compra."
                    : "O nó final focado não foi identificado como Wheelspin em duas capturas antes de gastar SP.");
        }

        observedRatios.Sort();
        var medianRatio = observedRatios[observedRatios.Count / 2];

        context.Logger.State(
            _profile.Workflow,
            state,
                purchased
                    ? $"Nó Wheelspin/Supersorteio e aumento rosa confirmados em duas de três capturas ({baselineMagentaRatio:P2} -> {medianRatio:P2})."
                    : $"Nó Wheelspin/Supersorteio final confirmado em duas de três capturas; linha de base rosa={medianRatio:P2}.");
        return medianRatio;
    }

    private bool IsTargetVehicleText(string normalized) =>
        _profile.MatchesCurrentVehicleText(normalized);

    private bool IsFinalPerkText(string normalized) =>
        _profile.MatchesFinalPerkText(normalized);

    private async Task OpenMyCarsAsync(
        AutomationContext context,
        GameNavigator navigator,
        string state,
        CancellationToken cancellationToken)
    {
        await navigator.ReturnToGarageMenuAsync(cancellationToken);
        if (!await context.Vision.ContainsAnyTextAsync(["MEUS CARROS"], cancellationToken))
        {
            await navigator.OpenCarsTabAsync(cancellationToken);
        }

        context.Logger.State(_profile.Workflow, $"MeusCarros{state}", "Normalizando no topo e abrindo Meus Carros.");
        await TapRepeatedAsync(context, GameKey.Up, 8, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await WaitForCarGridAsync(context, $"MeusCarros{state}Confirmado", cancellationToken);
    }

    private async Task SelectManufacturerAsync(
        AutomationContext context,
        string state,
        string manufacturer,
        IReadOnlyCollection<string> successorTexts,
        CancellationToken cancellationToken)
    {
        ManufacturerOverlaySnapshot? snapshot = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var candidate = await CaptureManufacturerOverlayAsync(context, manufacturer, cancellationToken);
            if (candidate.IsOverlay && candidate.Target is not null && candidate.Focused is not null)
            {
                snapshot = candidate;
                break;
            }

            await Task.Delay(220, cancellationToken);
        }

        if (snapshot?.Target is null || snapshot.Focused is null)
        {
            throw await CreateFailureAsync(
                context,
                state,
                $"A grade de fabricantes abriu, mas {manufacturer} e o foco verde não foram confirmados no mesmo frame.");
        }

        var verticalMoves = (int)Math.Round(
            (snapshot.Target.Center.Y - snapshot.Focused.Center.Y) / snapshot.RowSpacing);
        var horizontalMoves = (int)Math.Round(
            (snapshot.Target.Center.X - snapshot.Focused.Center.X) / snapshot.ColumnSpacing);
        if (Math.Abs(verticalMoves) > 20 || Math.Abs(horizontalMoves) > 3)
        {
            throw await CreateFailureAsync(
                context,
                state,
                $"A geometria OCR de {manufacturer} ficou fora da grade esperada.");
        }

        context.Logger.State(
            _profile.Workflow,
            state,
            $"{manufacturer} localizado; movendo {Math.Abs(verticalMoves)} linha(s) e " +
            $"{Math.Abs(horizontalMoves)} coluna(s) somente com o controle.");
        await TapRepeatedAsync(
            context,
            verticalMoves < 0 ? GameKey.Up : GameKey.Down,
            Math.Abs(verticalMoves),
            cancellationToken);
        await TapRepeatedAsync(
            context,
            horizontalMoves < 0 ? GameKey.Left : GameKey.Right,
            Math.Abs(horizontalMoves),
            cancellationToken);
        await Task.Delay(250, cancellationToken);

        var targetConfirmed = false;
        const int maximumVerificationRounds = 12;
        const int maximumCorrectiveInputs = 8;
        const int maximumCorrectiveReversals = 2;
        var correctiveInputs = 0;
        var correctiveReversals = 0;
        var consecutiveIgnoredPulses = 0;
        var nextCorrectiveHoldMs = 12;
        var correctiveDeadline = Stopwatch.StartNew();
        (int FocusRow, int FocusColumn, int ExpectedRowDelta, int ExpectedColumnDelta, int HoldMs)?
            pendingPulse = null;
        for (var verificationRound = 0;
             verificationRound < maximumVerificationRounds;
             verificationRound++)
        {
            if (correctiveDeadline.Elapsed > TimeSpan.FromSeconds(15))
            {
                break;
            }

            var confirmations = new List<ManufacturerOverlaySnapshot>(3);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                confirmations.Add(await CaptureManufacturerOverlayAsync(
                    context,
                    manufacturer,
                    cancellationToken));
                if (attempt < 2)
                {
                    await Task.Delay(160, cancellationToken);
                }
            }

            var selectedConsensus = confirmations
                .Where(confirmation =>
                    confirmation.IsOverlay &&
                    confirmation.Target?.Selected == true &&
                    confirmation.Focused is not null &&
                    confirmation.Focused.FocusRatio >= 0.10 &&
                    confirmation.RowSpacing > 0 &&
                    confirmation.ColumnSpacing > 0)
                .Select(confirmation => (
                    FocusRow: (int)Math.Round(confirmation.Focused!.Center.Y / confirmation.RowSpacing),
                    FocusColumn: (int)Math.Round(confirmation.Focused.Center.X / confirmation.ColumnSpacing),
                    TargetRow: (int)Math.Round(confirmation.Target!.Center.Y / confirmation.RowSpacing),
                    TargetColumn: (int)Math.Round(confirmation.Target.Center.X / confirmation.ColumnSpacing)))
                .GroupBy(fingerprint => fingerprint)
                .Any(group => group.Count() >= 2);
            if (selectedConsensus)
            {
                targetConfirmed = true;
                break;
            }

            var stableCorrection = confirmations
                .Where(confirmation =>
                    confirmation.IsOverlay &&
                    confirmation.Target is not null &&
                    confirmation.Focused is not null &&
                    confirmation.Focused.FocusRatio >= 0.10 &&
                    confirmation.RowSpacing > 0 &&
                    confirmation.ColumnSpacing > 0)
                .Select(confirmation => new
                {
                    Moves = (
                        Vertical: (int)Math.Round(
                            (confirmation.Target!.Center.Y - confirmation.Focused!.Center.Y) /
                            confirmation.RowSpacing),
                        Horizontal: (int)Math.Round(
                            (confirmation.Target.Center.X - confirmation.Focused.Center.X) /
                            confirmation.ColumnSpacing)),
                    FocusRow = (int)Math.Round(confirmation.Focused.Center.Y / confirmation.RowSpacing),
                    FocusColumn = (int)Math.Round(confirmation.Focused.Center.X / confirmation.ColumnSpacing),
                    TargetRow = (int)Math.Round(confirmation.Target.Center.Y / confirmation.RowSpacing),
                    TargetColumn = (int)Math.Round(confirmation.Target.Center.X / confirmation.ColumnSpacing),
                    Focus = confirmation.Focused
                })
                .GroupBy(correction => (
                    correction.Moves,
                    correction.FocusRow,
                    correction.FocusColumn,
                    correction.TargetRow,
                    correction.TargetColumn))
                .Select(group => new
                 {
                     Moves = group.Key.Moves,
                     FocusRow = group.Key.FocusRow,
                     FocusColumn = group.Key.FocusColumn,
                     TargetRow = group.Key.TargetRow,
                     TargetColumn = group.Key.TargetColumn,
                     Focus = group.OrderByDescending(item => item.Focus.FocusRatio).First().Focus,
                     Confirmations = group.Count()
                 })
                .OrderByDescending(group => group.Confirmations)
                .FirstOrDefault();
            if (stableCorrection is null ||
                stableCorrection.Confirmations < 2)
            {
                if (verificationRound < maximumVerificationRounds - 1)
                {
                    await Task.Delay(300, cancellationToken);
                }
                continue;
            }

            if (pendingPulse is { } previousPulse)
            {
                var observedRowDelta = stableCorrection.FocusRow - previousPulse.FocusRow;
                var observedColumnDelta = stableCorrection.FocusColumn - previousPulse.FocusColumn;
                var observedDistance = Math.Abs(observedRowDelta) + Math.Abs(observedColumnDelta);
                if (observedDistance == 0)
                {
                    consecutiveIgnoredPulses++;
                    if (consecutiveIgnoredPulses >= 3)
                    {
                        break;
                    }

                    nextCorrectiveHoldMs = previousPulse.HoldMs switch
                    {
                        <= 8 => 12,
                        <= 12 => 16,
                        _ => 20
                    };
                    context.Logger.State(
                        _profile.Workflow,
                        $"{state}Ajuste",
                        $"O pulso preciso de {previousPulse.HoldMs} ms não moveu o foco; " +
                        $"nova tentativa limitada com {nextCorrectiveHoldMs} ms.");
                }
                else
                {
                    consecutiveIgnoredPulses = 0;
                    var movedInExpectedDirection =
                        (Math.Sign(observedRowDelta) == Math.Sign(previousPulse.ExpectedRowDelta) &&
                         observedColumnDelta == 0) ||
                        (Math.Sign(observedColumnDelta) == Math.Sign(previousPulse.ExpectedColumnDelta) &&
                         observedRowDelta == 0);
                    if (!movedInExpectedDirection || observedDistance > 2)
                    {
                        throw await CreateFailureAsync(
                            context,
                            $"{state}AjusteInesperado",
                            $"O pulso de {previousPulse.HoldMs} ms moveu o foco de forma inesperada: " +
                            $"delta observado=({observedRowDelta},{observedColumnDelta}).");
                    }

                    if (observedDistance == 2)
                    {
                        correctiveReversals++;
                        if (correctiveReversals > maximumCorrectiveReversals)
                        {
                            break;
                        }

                        nextCorrectiveHoldMs = 8;
                        context.Logger.State(
                            _profile.Workflow,
                            $"{state}Ajuste",
                            $"O pulso de {previousPulse.HoldMs} ms saltou duas células; " +
                            "a reversão será tentada uma vez com 8 ms precisos.");
                    }
                    else
                    {
                        nextCorrectiveHoldMs = 12;
                    }
                }

                pendingPulse = null;
            }

            if (stableCorrection.Moves == (0, 0))
            {
                if (verificationRound < maximumVerificationRounds - 1)
                {
                    await Task.Delay(300, cancellationToken);
                }

                continue;
            }

            var correctionDistance = Math.Abs(stableCorrection.Moves.Vertical) +
                                     Math.Abs(stableCorrection.Moves.Horizontal);
            var remainingCorrectiveInputs = maximumCorrectiveInputs - correctiveInputs;
            if (correctionDistance < 1 ||
                correctionDistance > Math.Min(4, remainingCorrectiveInputs) ||
                remainingCorrectiveInputs < 1 ||
                verificationRound >= maximumVerificationRounds - 1 ||
                correctiveDeadline.Elapsed > TimeSpan.FromSeconds(13))
            {
                break;
            }

            var correctionKey = stableCorrection.Moves.Vertical switch
            {
                < 0 => GameKey.Up,
                > 0 => GameKey.Down,
                _ when stableCorrection.Moves.Horizontal < 0 => GameKey.Left,
                _ => GameKey.Right
            };
            var stableFocus = stableCorrection.Focus;
            context.Logger.State(
                _profile.Workflow,
                $"{state}Ajuste",
                $"Foco em ({stableFocus.Center.X},{stableFocus.Center.Y}), razão={stableFocus.FocusRatio:P1}; " +
                $"residual=({stableCorrection.Moves.Vertical},{stableCorrection.Moves.Horizontal}). " +
                $"Enviando somente {correctionKey} por {nextCorrectiveHoldMs} ms precisos e revalidando.");
            pendingPulse = (
                stableCorrection.FocusRow,
                stableCorrection.FocusColumn,
                correctionKey switch
                {
                    GameKey.Up => -1,
                    GameKey.Down => 1,
                    _ => 0
                },
                correctionKey switch
                {
                    GameKey.Left => -1,
                    GameKey.Right => 1,
                    _ => 0
                },
                nextCorrectiveHoldMs);
            await context.Input.HoldPreciselyAsync(
                correctionKey,
                nextCorrectiveHoldMs,
                cancellationToken);
            await Task.Delay(320, cancellationToken);
            correctiveInputs++;
        }

        if (!targetConfirmed)
        {
            throw await CreateFailureAsync(
                context,
                $"{state}Foco",
                $"O contorno verde não confirmou {manufacturer} em duas capturas antes do Enter.");
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await WaitForCarGridAsync(context, $"{state}Grade", cancellationToken);
        if (await context.Vision.ContainsAnyTextAsync(successorTexts, cancellationToken))
        {
            context.Logger.State(
                _profile.Workflow,
                $"{state}Confirmado",
                $"Alvo visível após selecionar {manufacturer}; iniciando confirmação do cartão.");
        }
        else
        {
            context.Logger.State(
                _profile.Workflow,
                $"{state}BuscaNoGrid",
                $"{manufacturer} foi selecionado, mas o alvo não está no viewport inicial; iniciando busca limitada.");
        }
    }

    private async Task WaitForManufacturerOverlayAsync(
        AutomationContext context,
        string state,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await CaptureManufacturerOverlayAsync(context, null, cancellationToken);
            if (snapshot.IsOverlay && snapshot.Focused is not null)
            {
                context.Logger.State(
                    _profile.Workflow,
                    state,
                    $"Overlay Fabricante confirmado por título exato, {snapshot.CellCount} células OCR e foco verde.");
                return;
            }

            await Task.Delay(220, cancellationToken);
        }

        throw await CreateFailureAsync(
            context,
            state,
            "Backspace não abriu uma grade de fabricantes confirmada; nenhum direcional será enviado.");
    }

    private async Task<ManufacturerOverlaySnapshot> CaptureManufacturerOverlayAsync(
        AutomationContext context,
        string? manufacturer,
        CancellationToken cancellationToken) =>
        await context.Vision.AnalyzeScreenAsync(
            (bitmap, document) => AnalyzeManufacturerOverlay(bitmap, document, manufacturer),
            cancellationToken);

    private ManufacturerOverlaySnapshot AnalyzeManufacturerOverlay(
        Bitmap bitmap,
        OcrDocument document,
        string? manufacturer)
    {
        var hasExactTitle = document.Lines.Any(line =>
            GameVisionService.Normalize(line.Text) == "FABRICANTE");
        var measured = document.Lines
            .Where(line =>
                line.Center.X >= bitmap.Width * 0.10 &&
                line.Center.X <= bitmap.Width * 0.90 &&
                line.Center.Y >= bitmap.Height * 0.24 &&
                line.Center.Y <= bitmap.Height * 0.89 &&
                GameVisionService.Normalize(line.Text).Length >= 2)
            .Select(line =>
            {
                var region = ManufacturerCellRegion(bitmap, line);
                return new ManufacturerCell(
                    line.Text,
                    line.Center,
                    region,
                    LimeVerticalBorderRatioWithHorizontalTolerance(
                        bitmap,
                        region,
                        horizontalToleranceRatio: 0.040),
                    Selected: false);
            })
            .ToList();
        var rowCenters = ClusterCenters(
            measured.Select(cell => (double)cell.Center.Y),
            bitmap.Height * 0.018);
        var columnCenters = ClusterCenters(
            measured.Select(cell => (double)cell.Center.X),
            bitmap.Width * 0.060);
        var rowSpacing = MedianSpacing(rowCenters);
        var columnSpacing = MedianSpacing(columnCenters);
        // Quando o foco preto cai sobre uma linha inteira, o Windows OCR pode
        // omitir os quatro fabricantes dessa linha. Preserve a malha completa
        // interpolando somente lacunas internas que equivalem, com baixa
        // tolerância, a múltiplos inteiros do espaçamento observado. Isso foi
        // necessário para enxergar Lexus entre as linhas y=366 e y=422.
        if (hasExactTitle &&
            columnCenters.Count == 4 &&
            rowSpacing >= bitmap.Height * 0.040 &&
            rowSpacing <= bitmap.Height * 0.060)
        {
            rowCenters = FillMissingGridCenters(rowCenters, rowSpacing);
            rowSpacing = MedianSpacing(rowCenters);
        }
        var normalizedManufacturer = manufacturer is null
            ? null
            : GameVisionService.Normalize(manufacturer);
        var targetIndex = normalizedManufacturer is null
            ? -1
            : measured.FindIndex(cell =>
                GameVisionService.Normalize(cell.Text) == normalizedManufacturer);

        // O OCR real pode omitir somente Mazda, embora reconheça Maserati. Na
        // lista alfabética Mazda é a próxima célula em ordem de leitura; essa
        // regra também cobre a quebra da última coluna para a linha seguinte.
        // A inferência apenas cria a ROI candidata. O Enter ainda depende de o
        // contorno verde ser confirmado nessa ROI em 2 de 3 frames.
        if (targetIndex < 0 &&
            normalizedManufacturer == "MAZDA" &&
            columnSpacing > 0 &&
            rowSpacing > 0 &&
            rowCenters.Count > 0 &&
            columnCenters.Count > 0)
        {
            var anchor = measured.FirstOrDefault(cell =>
                GameVisionService.Normalize(cell.Text) == "MASERATI");
            var cellOffset = 1;
            if (anchor is null)
            {
                // No frame focado real o OCR omitiu simultaneamente Mazda e
                // Maserati, mas manteve McLaren. Como McLaren é a célula
                // alfabética imediatamente seguinte, ele é uma segunda âncora
                // inequívoca para recuperar Mazda sem adivinhar pelo conteúdo.
                anchor = measured.FirstOrDefault(cell =>
                    GameVisionService.Normalize(cell.Text) == "MCLAREN");
                cellOffset = -1;
            }

            if (anchor is not null)
            {
                var rowIndex = FindClosestCenterIndex(rowCenters, anchor.Center.Y);
                var columnIndex = FindClosestCenterIndex(columnCenters, anchor.Center.X) + cellOffset;
                if (columnIndex >= columnCenters.Count)
                {
                    columnIndex = 0;
                    rowIndex++;
                }
                else if (columnIndex < 0)
                {
                    columnIndex = columnCenters.Count - 1;
                    rowIndex--;
                }

                if (rowIndex >= 0 && rowIndex < rowCenters.Count)
                {
                    var inferredCenter = new Point(
                        (int)Math.Round(columnCenters[columnIndex]),
                        (int)Math.Round(rowCenters[rowIndex]));
                    var inferredRegion = ManufacturerCellRegion(bitmap, inferredCenter);
                    measured.Add(new ManufacturerCell(
                        $"MAZDA (inferido de {GameVisionService.Normalize(anchor.Text)})",
                        inferredCenter,
                        inferredRegion,
                        LimeVerticalBorderRatioWithHorizontalTolerance(
                            bitmap,
                            inferredRegion,
                            horizontalToleranceRatio: 0.040),
                        Selected: false));
                    targetIndex = measured.Count - 1;
                }
            }
        }

        // O realce verde reduz o contraste do texto e o Windows OCR pode omitir
        // exatamente a célula focada (observado com ABARTH). Gere a malha pelas
        // linhas/colunas reconhecidas e meça todas as células, independentemente
        // de haver OcrLine dentro delas.
        var focused = rowCenters
            .SelectMany(row => columnCenters.Select(column =>
            {
                var center = new Point((int)Math.Round(column), (int)Math.Round(row));
                var region = ManufacturerCellRegion(bitmap, center);
                return new ManufacturerCell(
                    "célula geométrica",
                    center,
                    region,
                    LimeVerticalBorderRatioWithHorizontalTolerance(
                        bitmap,
                        region,
                        horizontalToleranceRatio: 0.040),
                    Selected: false);
            }))
            .OrderByDescending(cell => cell.FocusRatio)
            .FirstOrDefault();
        if (focused is not null && focused.FocusRatio >= 0.025)
        {
            focused = focused with { Selected = true };
        }
        else
        {
            focused = null;
        }

        var target = targetIndex >= 0 ? measured[targetIndex] : null;
        if (target is not null &&
            focused is not null &&
            Math.Abs(target.Center.X - focused.Center.X) <= columnSpacing * 0.35 &&
            Math.Abs(target.Center.Y - focused.Center.Y) <= rowSpacing * 0.35)
        {
            target = target with { Selected = true };
        }

        return new ManufacturerOverlaySnapshot(
            hasExactTitle && measured.Count >= 8 && rowSpacing > 0 && columnSpacing > 0,
            target,
            focused,
            measured.Count,
            rowSpacing,
            columnSpacing);
    }

    private async Task WaitForCarGridAsync(
        AutomationContext context,
        string state,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var confirmations = 0;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            confirmations = snapshot.IsCarGrid && snapshot.FocusedCell >= 0
                ? confirmations + 1
                : 0;
            if (confirmations >= 2)
            {
                context.Logger.State(
                    _profile.Workflow,
                    state,
                    "Grade de carros confirmada em duas capturas por estrutura e contorno verde.");
                return;
            }

            await Task.Delay(180, cancellationToken);
        }

        throw await CreateFailureAsync(
            context,
            state,
            "A grade de carros não foi confirmada em duas capturas consecutivas.");
    }

    private async Task<bool> FocusTargetVehicleCardAsync(
        AutomationContext context,
        string state,
        CancellationToken cancellationToken,
        bool allowAbsent = false)
    {
        var stalledMoves = 0;
        // Este modo só é usado imediatamente após a fabricante-alvo ter sido selecionada
        // e confirmada no overlay. A barra de carros é alfabética; alcançar
        // explicitamente uma fabricante posterior sem encontrar o alvo prova
        // que a seção-alvo terminou, sem depender de um D-pad ignorado no fim.
        var startedAtTargetManufacturer = allowAbsent;
        for (var move = 0; move <= MaximumCardSearchMoves; move++)
        {
            var snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            if (!snapshot.IsCarGrid || snapshot.FocusedCell < 0)
            {
                throw await CreateFailureAsync(
                    context,
                    state,
                    "A grade ou o cartão focado ficou inconclusivo durante a busca do veículo-alvo.");
            }

            if (snapshot.CandidateCells.Contains(snapshot.FocusedCell))
            {
                if (await ConfirmFocusedTargetVehicleAsync(context, cancellationToken))
                {
                    context.Logger.State(
                        _profile.Workflow,
                        state,
                        $"veículo-alvo confirmado por OCR e contorno verde na célula {snapshot.FocusedCell + 1}.");
                    return true;
                }

                throw await CreateFailureAsync(
                    context,
                    state,
                    "O candidato veículo-alvo não permaneceu focado em duas de três capturas.");
            }

            if (snapshot.CandidateCells.Count > 0)
            {
                var selectedRow = snapshot.FocusedCell / 4;
                var selectedColumn = snapshot.FocusedCell % 4;
                var targetCell = snapshot.CandidateCells
                    .OrderBy(cell =>
                        Math.Abs(cell / 4 - selectedRow) +
                        Math.Abs(cell % 4 - selectedColumn))
                    .First();
                var targetRow = targetCell / 4;
                var targetColumn = targetCell % 4;
                var direction = selectedRow < targetRow
                    ? GameKey.Down
                    : selectedRow > targetRow
                        ? GameKey.Up
                        : selectedColumn < targetColumn
                            ? GameKey.Right
                            : GameKey.Left;
                await context.Input.TapAsync(
                    direction,
                    cancellationToken,
                    postDelayMs: FastNavigationPostDelayMs);
                await Task.Delay(140, cancellationToken);
                stalledMoves = 0;
                continue;
            }

            var focusedManufacturer = RecognizeFocusedManufacturer(snapshot.FocusedCardText);
            if (focusedManufacturer == _profile.Manufacturer)
            {
                startedAtTargetManufacturer = true;
            }
            else if (allowAbsent &&
                     startedAtTargetManufacturer &&
                     focusedManufacturer is not null)
            {
                await ConfirmTargetVehicleAbsentAfterManufacturerTransitionAsync(
                    context,
                    state,
                    focusedManufacturer,
                    cancellationToken);
                return false;
            }

            var beforeFingerprint = snapshot.ContentFingerprint;
            var beforeFocus = snapshot.FocusedCell;
            await context.Input.TapAsync(
                GameKey.Right,
                cancellationToken,
                postDelayMs: FastNavigationPostDelayMs);
            await Task.Delay(160, cancellationToken);
            var after = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            stalledMoves = after.ContentFingerprint == beforeFingerprint && after.FocusedCell == beforeFocus
                ? stalledMoves + 1
                : 0;
            if (stalledMoves >= 3)
            {
                break;
            }
        }

        throw await CreateFailureAsync(
            context,
            state,
            "A busca limitada percorreu a grade da fabricante-alvo sem confirmar um cartão veículo-alvo por OCR e CV.");
    }

    private async Task ConfirmTargetVehicleAbsentAfterManufacturerTransitionAsync(
        AutomationContext context,
        string state,
        string expectedManufacturer,
        CancellationToken cancellationToken)
    {
        var stableObservations = new List<(int Cell, string Manufacturer)>(3);
        var conflicts = 0;
        var inconclusive = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            var manufacturer = RecognizeFocusedManufacturer(snapshot.FocusedCardText);
            if (!snapshot.IsCarGrid || snapshot.FocusedCell < 0 || manufacturer is null)
            {
                inconclusive++;
            }
            else if (snapshot.CandidateCells.Count > 0 || manufacturer == _profile.Manufacturer)
            {
                conflicts++;
            }
            else
            {
                stableObservations.Add((snapshot.FocusedCell, manufacturer));
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        var stableAbsence = stableObservations
            .GroupBy(observation => observation)
            .Any(group => group.Count() >= 2);
        if (!stableAbsence || conflicts > 0)
        {
            throw await CreateFailureAsync(
                context,
                state + "AusenciaInconclusiva",
                $"A busca alcançou {expectedManufacturer} após a fabricante-alvo, mas a transição sem veículo-alvo não permaneceu " +
                $"estável em duas de três capturas sem conflito (válidas={stableObservations.Count}, " +
                $"conflitos={conflicts}, inconclusivas={inconclusive}).");
        }

        context.Logger.State(
            _profile.Workflow,
            state + "Ausente",
            $"Transição da fabricante-alvo para {expectedManufacturer} e ausência do cartão veículo-alvo " +
            "confirmadas na mesma célula em duas de três capturas.");
    }

    private string? RecognizeFocusedManufacturer(string focusedCardText) =>
        _profile.RecognizeFocusedManufacturer(focusedCardText);

    private async Task<bool> ConfirmFocusedTargetVehicleAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var confirmations = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            if (snapshot.IsCarGrid &&
                snapshot.FocusedCell >= 0 &&
                snapshot.CandidateCells.Contains(snapshot.FocusedCell))
            {
                confirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        return confirmations >= 2;
    }

    private async Task MoveFocusAwayFromTargetVehicleAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
        if (!snapshot.IsCarGrid ||
            snapshot.FocusedCell < 0 ||
            !snapshot.CandidateCells.Contains(snapshot.FocusedCell))
        {
            throw await CreateFailureAsync(
                context,
                "TrocarCarro",
                "O veículo-alvo atual não foi confirmado simultaneamente no cabeçalho e no cartão focado.");
        }

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var before = snapshot;
            await context.Input.TapAsync(
                GameKey.Right,
                cancellationToken,
                postDelayMs: FastNavigationPostDelayMs);
            await Task.Delay(150, cancellationToken);
            snapshot = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            if (!snapshot.IsCarGrid || snapshot.FocusedCell < 0)
            {
                continue;
            }

            var changed = snapshot.FocusedCell != before.FocusedCell ||
                          snapshot.ContentFingerprint != before.ContentFingerprint;
            if (!changed || snapshot.CandidateCells.Contains(snapshot.FocusedCell))
            {
                continue;
            }

            await Task.Delay(180, cancellationToken);
            var confirmation = await CaptureTargetVehicleGridAsync(context, cancellationToken);
            if (confirmation.IsCarGrid &&
                confirmation.FocusedCell == snapshot.FocusedCell &&
                !confirmation.CandidateCells.Contains(confirmation.FocusedCell))
            {
                context.Logger.State(
                    _profile.Workflow,
                    "OutroCarro",
                    $"Foco saiu do veículo-alvo e estabilizou na célula {confirmation.FocusedCell + 1}.");
                return;
            }
        }

        throw await CreateFailureAsync(
            context,
            "OutroCarro",
            "Não foi possível estabilizar o foco em um cartão diferente do veículo-alvo.");
    }

    private async Task ConfirmCurrentCarIsNotTargetVehicleAsync(
        AutomationContext context,
        string state,
        CancellationToken cancellationToken) =>
        await ConfirmCurrentCarHeaderAsync(context, expectTargetVehicle: false, state, cancellationToken);

    private async Task ConfirmCurrentCarHeaderAsync(
        AutomationContext context,
        bool expectTargetVehicle,
        string state,
        CancellationToken cancellationToken)
    {
        var matching = 0;
        var conflicting = 0;
        var observations = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var header = await context.Vision.ReadScaledRegionAsync(
                CurrentCarHeaderRegion,
                3,
                cancellationToken);
            var normalized = GameVisionService.Normalize(header.Text);
            observations.Add(normalized);
            var recognizable = LooksLikeCarHeader(normalized);
            var hasAnyTargetVehicleEvidence = _profile.MatchesAnyTargetVehicleEvidence(normalized);
            var hasFullTargetVehicleIdentity = IsTargetVehicleText(normalized);
            var observationMatches = expectTargetVehicle
                ? hasFullTargetVehicleIdentity
                : !hasAnyTargetVehicleEvidence;
            if (recognizable && observationMatches)
            {
                matching++;
            }
            else if (recognizable)
            {
                conflicting++;
            }

            if (attempt < 2)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        if (matching < 2 || conflicting > 0)
        {
            throw await CreateFailureAsync(
                context,
                state,
                expectTargetVehicle
                    ? $"O cabeçalho não confirmou o veículo-alvo atual em duas capturas. OCR: '{Shorten(string.Join(" | ", observations))}'."
                    : $"O cabeçalho não confirmou um carro diferente do veículo-alvo em duas capturas sem conflito. OCR: '{Shorten(string.Join(" | ", observations))}'.");
        }

        context.Logger.State(
            _profile.Workflow,
            state,
            expectTargetVehicle
                ? "veículo-alvo confirmado no cabeçalho atual em duas de três capturas."
                : "Outro carro confirmado no cabeçalho atual em duas de três capturas; a remoção do veículo-alvo é segura.");
    }

    private async Task<TargetVehicleGridSnapshot> CaptureTargetVehicleGridAsync(
        AutomationContext context,
        CancellationToken cancellationToken) =>
        await context.Vision.AnalyzeScreenAsync(AnalyzeTargetVehicleGrid, cancellationToken);

    private TargetVehicleGridSnapshot AnalyzeTargetVehicleGrid(Bitmap bitmap, OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var compact = CompactText(normalized);
        var selected = VisibleCarCells
            .Select((region, index) => new { Index = index, Ratio = LimeVerticalBorderRatio(bitmap, region) })
            .OrderByDescending(item => item.Ratio)
            .First();
        var selectedCell = selected.Ratio >= 0.025 ? selected.Index : -1;
        var gridLines = document.Lines
            .Where(line =>
                line.Center.X >= bitmap.Width * 0.20 &&
                line.Center.X <= bitmap.Width * 0.96 &&
                line.Center.Y >= bitmap.Height * 0.18 &&
                line.Center.Y <= bitmap.Height * 0.72)
            .ToArray();
        var hasExactManufacturerOverlay = document.Lines.Any(line =>
            GameVisionService.Normalize(line.Text) == "FABRICANTE");
        var hasExactDealerGridTitle = document.Lines.Any(line =>
            GameVisionService.Normalize(line.Text) == "COMPRAR CARRO");
        var hasExactOwnedGridTitle = document.Lines.Any(line =>
            GameVisionService.Normalize(line.Text) == "MEUS CARROS");
        // O Windows OCR separa ocasionalmente o atalho em "I R para
        // Fabricante". Na concessionária, o mesmo comando também aparece como
        // "Alterar Fabricante" e avisos do jogo podem cobrir o rodapé inteiro;
        // nesse caso, aceite somente o título exato Comprar Carro ou Meus
        // Carros. Esses sinais não autorizam a tela sozinhos: estrutura, foco
        // bilateral e ausência do overlay Fabricante continuam obrigatórios no
        // mesmo frame.
        var hasCarGridMarker =
            compact.Contains("IRPARAFABRICANTE", StringComparison.Ordinal) ||
            compact.Contains("ALTERARFABRICANTE", StringComparison.Ordinal) ||
            hasExactDealerGridTitle ||
            hasExactOwnedGridTitle;
        var isCarGrid = hasCarGridMarker &&
                         !hasExactManufacturerOverlay &&
                         selectedCell >= 0 &&
                         gridLines.Length >= 3;
        var candidates = gridLines
            .Where(line => IsTargetVehicleTitle(line.Text))
            .Select(line => FindClosestCarCellIndex(CardRegionFromTitle(bitmap, line)))
            .Where(index => index >= 0)
            .Distinct()
            .ToArray();
        var focusedCardText = selectedCell < 0
            ? string.Empty
            : string.Join(
                " ",
                gridLines
                    .Where(line => IsInsideNormalizedRegion(bitmap, line.Center, VisibleCarCells[selectedCell]))
                    .OrderBy(line => line.Center.Y)
                    .ThenBy(line => line.Center.X)
                    .Select(line => line.Text));
        var fingerprint = string.Join(
            "|",
            gridLines
                .OrderBy(line => line.Center.Y)
                .ThenBy(line => line.Center.X)
                .Select(line =>
                    $"{GameVisionService.Normalize(line.Text)}@{line.Center.X / 20}:{line.Center.Y / 20}"));
        return new TargetVehicleGridSnapshot(isCarGrid, selectedCell, candidates, focusedCardText, fingerprint);
    }

    private bool IsInsideNormalizedRegion(Bitmap bitmap, Point point, RectangleF region)
    {
        var normalizedX = point.X / (double)bitmap.Width;
        var normalizedY = point.Y / (double)bitmap.Height;
        return normalizedX >= region.Left &&
               normalizedX <= region.Right &&
               normalizedY >= region.Top &&
               normalizedY <= region.Bottom;
    }

    private bool IsTargetVehicleTitle(string text) =>
        _profile.MatchesVehicleText(text);

    private bool LooksLikeCarHeader(string normalized)
    {
        if (Regex.IsMatch(normalized, @"\b(?:19|20)\d{2}\b", RegexOptions.CultureInvariant))
        {
            return normalized.Count(char.IsLetter) >= 5;
        }

        // O OCR pode perder o ano amarelo, mas preserva o PI de três dígitos
        // seguido pela marca/modelo no mesmo cabeçalho (ex.: "399 ABARTH FIAT").
        // A ROI é exclusiva do canto do carro atual; ainda exigimos essa ordem
        // para não aceitar números soltos das abas como identidade veicular.
        if (Regex.IsMatch(
                normalized,
                @"\b[1-9]\d{2}\s+[A-Z]{3,}(?:\s+[A-Z0-9]{2,})?\b",
                RegexOptions.CultureInvariant))
        {
            return true;
        }

        var compact = Regex.Replace(normalized, @"[^A-Z0-9]", string.Empty, RegexOptions.CultureInvariant);
        return Regex.IsMatch(
                   compact,
                   @"(?:S1|S2|SI|SLI|I|R|A|B|C|D)\d{3}",
                   RegexOptions.CultureInvariant) &&
               normalized.Count(char.IsLetter) >= 5;
    }

    private RectangleF CardRegionFromTitle(Bitmap bitmap, OcrLine title)
    {
        const float width = 0.174f;
        const float height = 0.225f;
        var x = (float)(title.Center.X / (double)bitmap.Width) - width / 2;
        var y = (float)(title.Center.Y / (double)bitmap.Height) - 0.025f;
        return new RectangleF(
            Math.Clamp(x, 0, 1 - width),
            Math.Clamp(y, 0, 1 - height),
            width,
            height);
    }

    private int FindClosestCarCellIndex(RectangleF candidateRegion)
    {
        var centerX = candidateRegion.X + candidateRegion.Width / 2;
        var centerY = candidateRegion.Y + candidateRegion.Height / 2;
        var closest = VisibleCarCells
            .Select((cell, index) => new
            {
                Index = index,
                Distance = Math.Pow(centerX - (cell.X + cell.Width / 2), 2) +
                           Math.Pow(centerY - (cell.Y + cell.Height / 2), 2)
            })
            .OrderBy(item => item.Distance)
            .First();
        return closest.Distance <= 0.04 * 0.04 ? closest.Index : -1;
    }

    private RectangleF ManufacturerCellRegion(Bitmap bitmap, OcrLine line)
        => ManufacturerCellRegion(bitmap, line.Center);

    private RectangleF ManufacturerCellRegion(Bitmap bitmap, Point center)
    {
        const float width = 0.188f;
        const float height = 0.047f;
        var x = (float)(center.X / (double)bitmap.Width) - width / 2;
        var y = (float)(center.Y / (double)bitmap.Height) - height / 2;
        return new RectangleF(
            Math.Clamp(x, 0, 1 - width),
            Math.Clamp(y, 0, 1 - height),
            width,
            height);
    }

    private IReadOnlyList<double> ClusterCenters(IEnumerable<double> values, double tolerance)
    {
        var groups = new List<List<double>>();
        foreach (var value in values.Order())
        {
            if (groups.Count == 0 || value - groups[^1].Average() > tolerance)
            {
                groups.Add([value]);
            }
            else
            {
                groups[^1].Add(value);
            }
        }

        return groups.Select(group => group.Average()).ToArray();
    }

    private IReadOnlyList<double> FillMissingGridCenters(
        IReadOnlyList<double> centers,
        double spacing)
    {
        if (centers.Count < 2 || spacing <= 0)
        {
            return centers;
        }

        var completed = new List<double>(centers.Count + 4);
        for (var index = 0; index < centers.Count - 1; index++)
        {
            var current = centers[index];
            var next = centers[index + 1];
            completed.Add(current);
            var gapInCells = (next - current) / spacing;
            var roundedGap = (int)Math.Round(gapInCells);
            if (roundedGap is >= 2 and <= 4 &&
                Math.Abs(gapInCells - roundedGap) <= 0.20)
            {
                for (var missing = 1; missing < roundedGap; missing++)
                {
                    completed.Add(current + (next - current) * missing / roundedGap);
                }
            }
        }

        completed.Add(centers[^1]);
        return completed;
    }

    private double MedianSpacing(IReadOnlyList<double> centers)
    {
        var spacings = centers
            .Zip(centers.Skip(1), (left, right) => right - left)
            .Where(spacing => spacing > 1)
            .Order()
            .ToArray();
        return spacings.Length == 0 ? 0 : spacings[spacings.Length / 2];
    }

    private int FindClosestCenterIndex(IReadOnlyList<double> centers, double value) =>
        centers
            .Select((center, index) => new { Index = index, Distance = Math.Abs(center - value) })
            .OrderBy(item => item.Distance)
            .First()
            .Index;

    private double LimeVerticalBorderRatio(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var region = ToPixels(bitmap, normalizedRegion);
        var border = Math.Max(3, (int)Math.Round(Math.Min(region.Width, region.Height) * 0.055));
        var leftMatching = 0;
        var leftSampled = 0;
        var rightMatching = 0;
        var rightSampled = 0;
        for (var y = region.Top + border; y < region.Bottom - border; y++)
        {
            for (var x = region.Left; x < region.Left + border; x++)
            {
                var color = bitmap.GetPixel(x, y);
                leftSampled++;
                if (color.R >= 130 && color.G >= 180 && color.B <= 110 && color.G > color.B * 1.7)
                {
                    leftMatching++;
                }
            }

            for (var x = region.Right - border; x < region.Right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                rightSampled++;
                if (color.R >= 130 && color.G >= 180 && color.B <= 110 && color.G > color.B * 1.7)
                {
                    rightMatching++;
                }
            }
        }

        if (leftSampled == 0 || rightSampled == 0)
        {
            return 0;
        }

        return Math.Min(
            leftMatching / (double)leftSampled,
            rightMatching / (double)rightSampled);
    }

    private double LimeVerticalBorderRatioWithHorizontalTolerance(
        Bitmap bitmap,
        RectangleF normalizedRegion,
        double horizontalToleranceRatio)
    {
        var region = ToPixels(bitmap, normalizedRegion);
        var border = Math.Max(3, (int)Math.Round(Math.Min(region.Width, region.Height) * 0.055));
        var horizontalTolerance = Math.Max(
            3,
            (int)Math.Round(region.Width * horizontalToleranceRatio));
        var top = region.Top + border;
        var bottom = region.Bottom - border;
        if (top >= bottom)
        {
            return 0;
        }

        double BestRatioAround(int expectedStart)
        {
            var best = 0d;
            for (var start = expectedStart - horizontalTolerance;
                 start <= expectedStart + horizontalTolerance;
                 start++)
            {
                if (start < 0 || start + border > bitmap.Width)
                {
                    continue;
                }

                var matching = 0;
                var sampled = 0;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = start; x < start + border; x++)
                    {
                        var color = bitmap.GetPixel(x, y);
                        sampled++;
                        if (color.R >= 130 && color.G >= 180 && color.B <= 110 && color.G > color.B * 1.7)
                        {
                            matching++;
                        }
                    }
                }

                if (sampled > 0)
                {
                    best = Math.Max(best, matching / (double)sampled);
                }
            }

            return best;
        }

        // A ROI normalizada pode ficar alguns pixels mais larga ou mais estreita
        // que o contorno real, principalmente em 1600x900. Procure cada borda
        // somente numa faixa estreita ao redor da posição geométrica esperada;
        // o foco continua exigindo duas bordas verticais lime simultâneas.
        return Math.Min(
            BestRatioAround(region.Left),
            BestRatioAround(region.Right - border));
    }

    private double MagentaFillRatio(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var region = ToPixels(bitmap, normalizedRegion);
        var matching = 0;
        var sampled = 0;
        for (var y = region.Top; y < region.Bottom; y += 3)
        {
            for (var x = region.Left; x < region.Right; x += 3)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R >= 190 && color.G <= 100 && color.B >= 80 && color.R >= color.B)
                {
                    matching++;
                }
            }
        }

        return sampled == 0 ? 0 : matching / (double)sampled;
    }

    private Rectangle ToPixels(Bitmap bitmap, RectangleF normalized)
    {
        var x = Math.Clamp((int)Math.Round(bitmap.Width * normalized.X), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Y), 0, bitmap.Height - 1);
        var width = Math.Clamp((int)Math.Round(bitmap.Width * normalized.Width), 1, bitmap.Width - x);
        var height = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Height), 1, bitmap.Height - y);
        return new Rectangle(x, y, width, height);
    }

    private static RectangleF[] CreateVisibleCarCells() =>
        Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 4).Select(column =>
                new RectangleF(
                    0.208f + column * 0.174f,
                    0.195f + row * 0.232f,
                    0.178f,
                    0.225f)))
            .ToArray();

    private async Task TapRepeatedAsync(
        AutomationContext context,
        GameKey key,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            await context.Input.TapAsync(
                key,
                cancellationToken,
                postDelayMs: FastNavigationPostDelayMs);
        }
    }

    private async Task TapRepeatedWithDelayAsync(
        AutomationContext context,
        GameKey key,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            await context.Input.TapAsync(
                key,
                cancellationToken,
                postDelayMs: FastNavigationPostDelayMs);
            await Task.Delay(40, cancellationToken);
        }
    }

    private SpRefillIntent? LoadSpRefillIntent(
        AutomationContext context,
        int liveSkillPoints)
    {
        var path = SpRefillIntentPath(context);
        if (!File.Exists(path))
        {
            return null;
        }

        SpRefillIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize<SpRefillIntent>(
                         File.ReadAllText(path),
                         RecoveryCheckpointJsonOptions)
                     ?? throw new JsonException("Checkpoint vazio.");
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"A intenção persistida de reabastecimento de SP não pôde ser lida com segurança: {exception.Message} " +
                "Nenhuma compra será autorizada.");
        }

        var age = DateTimeOffset.UtcNow - intent.CreatedAtUtc;
        var isCurrentProfileIntent = intent.Version == SpRefillIntentVersion &&
                                     string.Equals(
                                         intent.VehicleKey,
                                         _profile.RecoveryVehicleKey,
                                         StringComparison.Ordinal);
        var isLegacyMadMikeIntent = intent.Version == LegacyMadMikeSpRefillIntentVersion &&
                                    intent.VehicleKey is null &&
                                    _profile.Kind == MacroKind.FarmarWheelspins;
        if ((!isCurrentProfileIntent && !isLegacyMadMikeIntent) ||
            intent.TargetSkillPoints != SpRefillTarget ||
            intent.Attempts is < 0 or > MaximumSpRefillAttempts ||
            intent.SkillPointsAtStart < 0 ||
            intent.SkillPointsAtStart > intent.LastObservedSkillPoints ||
            intent.LastObservedSkillPoints > liveSkillPoints ||
            liveSkillPoints is < 0 or > SpRefillTarget ||
            intent.CreatedAtUtc == default ||
            age < TimeSpan.FromMinutes(-5) ||
            age > TimeSpan.FromHours(24))
        {
            throw new CalibrationRequiredException(
                "A intenção persistida de reabastecimento de SP é incompatível. " +
                "Nenhuma compra será autorizada até o estado ser revisado.");
        }

        return intent;
    }

    private SpRefillIntent CreateSpRefillIntent(
        AutomationContext context,
        int skillPointsAtStart)
    {
        var intent = new SpRefillIntent(
            SpRefillIntentVersion,
            _profile.RecoveryVehicleKey,
            SpRefillTarget,
            skillPointsAtStart,
            Attempts: 0,
            LastObservedSkillPoints: skillPointsAtStart,
            CreatedAtUtc: DateTimeOffset.UtcNow);
        SaveSpRefillIntent(context, intent, overwrite: false);
        return intent;
    }

    private void SaveSpRefillIntent(
        AutomationContext context,
        SpRefillIntent intent,
        bool overwrite)
    {
        var path = SpRefillIntentPath(context);
        if (File.Exists(path) != overwrite)
        {
            var message = overwrite
                ? "A intenção persistida de reabastecimento de SP desapareceu durante a execução."
                : "Já existe uma intenção persistida de reabastecimento de SP.";
            throw new CalibrationRequiredException(
                $"{message} Nenhuma saída da garagem ou compra será autorizada até o estado ser revisado.");
        }

        var temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(context.Settings.DataDirectory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(intent, RecoveryCheckpointJsonOptions));
            File.Move(temporaryPath, path, overwrite);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"A intenção de reabastecimento de SP não pôde ser salva: {exception.Message} " +
                "O BOT parou antes do próximo handoff ou compra.");
        }
    }

    private void ClearSpRefillIntent(AutomationContext context)
    {
        var path = SpRefillIntentPath(context);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"A meta de {SpRefillTarget} SP foi confirmada, mas a intenção persistida não pôde ser removida: " +
                $"{exception.Message} Nenhuma compra será autorizada.");
        }
    }

    private string SpRefillIntentPath(AutomationContext context) =>
        Path.Combine(context.Settings.DataDirectory, SpRefillIntentFileName);

    private SpinRecoveryCheckpoint? LoadRecoveryCheckpoint(AutomationContext context)
    {
        var path = RecoveryCheckpointPath(context);
        if (!File.Exists(path))
        {
            return null;
        }

        SpinRecoveryCheckpoint checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<SpinRecoveryCheckpoint>(
                             File.ReadAllText(path),
                             RecoveryCheckpointJsonOptions)
                         ?? throw new JsonException("Checkpoint vazio.");
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"O checkpoint WheelSpin não pôde ser lido com segurança: {exception.Message} " +
                "Nenhum carro será removido.");
        }

        var age = DateTimeOffset.UtcNow - checkpoint.ConfirmedAtUtc;
        var isSafeLegacyPurchaseCheckpoint = checkpoint.Version == 2 &&
                                             checkpoint.Stage is
                                                 (SpinRecoveryStage.PurchaseAuthorized or
                                                  SpinRecoveryStage.PurchaseConfirmed) &&
                                             checkpoint.SkillPointsAfterFinal is null;
        if (checkpoint.Version == 2 && checkpoint.Stage == SpinRecoveryStage.FinalPerkConfirmed)
        {
            throw new CalibrationRequiredException(
                "O checkpoint legado chegou ao perk final, mas pode representar uma remoção já enviada pela versão anterior. " +
                "O BOT não tentará remover outro carro. Revise a garagem, resolva manualmente o carro deste ciclo e só então " +
                $"exclua o checkpoint: {path}");
        }

        if ((checkpoint.Version != RecoveryCheckpointVersion && !isSafeLegacyPurchaseCheckpoint) ||
            checkpoint.CycleId == Guid.Empty ||
            !string.Equals(checkpoint.VehicleKey, _profile.RecoveryVehicleKey, StringComparison.Ordinal) ||
            checkpoint.Stage == SpinRecoveryStage.Unknown ||
            !Enum.IsDefined(checkpoint.Stage) ||
            checkpoint.SkillPointsBeforeMastery is < 0 or > 999 ||
            checkpoint.CreditsBeforePurchase < _profile.GetSettings(context.Settings).CreditsPerCar ||
            checkpoint.CreditsAfterPurchase !=
                checkpoint.CreditsBeforePurchase - _profile.GetSettings(context.Settings).CreditsPerCar ||
            age < TimeSpan.FromMinutes(-5) ||
            age > TimeSpan.FromHours(24))
        {
            throw new CalibrationRequiredException(
                "O checkpoint WheelSpin está incompatível ou expirado. Nenhum carro será removido.");
        }

        var finalStageValid = checkpoint.Stage is
                                  (SpinRecoveryStage.FinalPerkConfirmed or
                                   SpinRecoveryStage.RemovalAuthorized or
                                   SpinRecoveryStage.RemovalConfirmed) &&
                              checkpoint.SkillPointsAfterFinal is { } finalPoints &&
                              finalPoints is >= 0 and <= 999 &&
                              checkpoint.SkillPointsBeforeMastery - finalPoints ==
                              _profile.GetSettings(context.Settings).SkillPointsPerCar;
        var purchaseStageValid = checkpoint.Stage is
                                     (SpinRecoveryStage.PurchaseAuthorized or SpinRecoveryStage.PurchaseConfirmed) &&
                                 checkpoint.SkillPointsAfterFinal is null;
        if (!finalStageValid && !purchaseStageValid)
        {
            throw new CalibrationRequiredException(
                "O checkpoint WheelSpin contém saldos incompatíveis com o estágio registrado. " +
                "Nenhum SP será gasto e nenhum carro será removido.");
        }

        if (isSafeLegacyPurchaseCheckpoint)
        {
            checkpoint = checkpoint with { Version = RecoveryCheckpointVersion };
            SaveRecoveryCheckpoint(context, checkpoint);
            context.Logger.State(
                _profile.Workflow,
                "MigrarCheckpointCompra",
                "Checkpoint legado anterior ao gasto de Maestria foi validado e migrado para a versão atual.");
        }

        return checkpoint;
    }

    private void SavePurchaseAuthorizationCheckpoint(AutomationContext context)
    {
        var snapshot = context.Resources.Current;
        if (snapshot.SkillPoints is not { } skillPoints ||
            snapshot.SkillPointsEstimated ||
            snapshot.Credits is not { } credits ||
            snapshot.CreditsEstimated ||
            credits < _profile.GetSettings(context.Settings).CreditsPerCar)
        {
            throw new CalibrationRequiredException(
                "A compra foi validada visualmente, mas os saldos exatos do ciclo não estão disponíveis para criar " +
                "uma autorização persistente. Nenhum A será enviado ao modal de compra.");
        }

        if (File.Exists(SpRefillIntentPath(context)))
        {
            throw new CalibrationRequiredException(
                "Ainda existe uma intenção persistida de reabastecimento de SP. " +
                "Nenhum A será enviado ao modal de compra antes da confirmação exata de 999 SP.");
        }

        if (File.Exists(RecoveryCheckpointPath(context)))
        {
            throw new CalibrationRequiredException(
                "Já existe um checkpoint WheelSpin antes da nova compra. Nenhum A será enviado até o estado ser resolvido.");
        }

        SaveRecoveryCheckpoint(
            context,
            new SpinRecoveryCheckpoint(
                RecoveryCheckpointVersion,
                Guid.NewGuid(),
                _profile.RecoveryVehicleKey,
                SpinRecoveryStage.PurchaseAuthorized,
                skillPoints,
                SkillPointsAfterFinal: null,
                CreditsBeforePurchase: credits,
                CreditsAfterPurchase: credits - _profile.GetSettings(context.Settings).CreditsPerCar,
                ConfirmedAtUtc: DateTimeOffset.UtcNow));
    }

    private void PromotePurchaseCheckpoint(AutomationContext context)
    {
        var checkpoint = LoadRecoveryCheckpoint(context)
                         ?? throw new CalibrationRequiredException(
                             "A compra terminou, mas sua autorização persistente desapareceu. " +
                             "O BOT parou antes de abrir a Maestria.");
        if (checkpoint.Stage != SpinRecoveryStage.PurchaseAuthorized)
        {
            throw new CalibrationRequiredException(
                $"A compra terminou com checkpoint no estágio inesperado {checkpoint.Stage}. " +
                "O BOT parou antes de abrir a Maestria.");
        }

        SaveRecoveryCheckpoint(
            context,
            checkpoint with
            {
                Stage = SpinRecoveryStage.PurchaseConfirmed,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private void PromoteFinalPerkCheckpoint(AutomationContext context, int skillPointsAfterFinal)
    {
        var checkpoint = LoadRecoveryCheckpoint(context)
                         ?? throw new CalibrationRequiredException(
                             "O perk final foi confirmado, mas não existe checkpoint da compra deste ciclo. " +
                             "O BOT parou antes de trocar ou remover o carro.");
        if (checkpoint.Stage is not
            (SpinRecoveryStage.PurchaseAuthorized or
             SpinRecoveryStage.PurchaseConfirmed or
             SpinRecoveryStage.FinalPerkConfirmed))
        {
            throw new CalibrationRequiredException(
                $"O perk final foi confirmado com checkpoint no estágio inesperado {checkpoint.Stage}. " +
                "O BOT parou antes de trocar ou remover o carro.");
        }

        SaveRecoveryCheckpoint(
            context,
            checkpoint with
            {
                Stage = SpinRecoveryStage.FinalPerkConfirmed,
                SkillPointsAfterFinal = skillPointsAfterFinal,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private void PromoteRemovalAuthorizedCheckpoint(AutomationContext context)
    {
        var checkpoint = LoadRecoveryCheckpoint(context)
                         ?? throw new CalibrationRequiredException(
                             "A opção Sim foi confirmada, mas o checkpoint deste ciclo desapareceu. " +
                             "Nenhuma remoção será enviada.");
        if (checkpoint.Stage != SpinRecoveryStage.FinalPerkConfirmed)
        {
            throw new CalibrationRequiredException(
                $"A opção Sim foi confirmada com checkpoint no estágio inesperado {checkpoint.Stage}. " +
                "Nenhuma remoção será enviada.");
        }

        SaveRecoveryCheckpoint(
            context,
            checkpoint with
            {
                Stage = SpinRecoveryStage.RemovalAuthorized,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private void PromoteRemovalConfirmedCheckpoint(AutomationContext context)
    {
        var checkpoint = LoadRecoveryCheckpoint(context)
                         ?? throw new CalibrationRequiredException(
                             "O modal de remoção fechou, mas o checkpoint deste ciclo desapareceu. " +
                             "O BOT não tentará remover outro carro.");
        if (checkpoint.Stage != SpinRecoveryStage.RemovalAuthorized)
        {
            throw new CalibrationRequiredException(
                $"O modal de remoção fechou com checkpoint no estágio inesperado {checkpoint.Stage}. " +
                "O BOT não tentará remover outro carro.");
        }

        SaveRecoveryCheckpoint(
            context,
            checkpoint with
            {
                Stage = SpinRecoveryStage.RemovalConfirmed,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private void SaveRecoveryCheckpoint(
        AutomationContext context,
        SpinRecoveryCheckpoint checkpoint)
    {
        var path = RecoveryCheckpointPath(context);
        var temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(context.Settings.DataDirectory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(checkpoint, RecoveryCheckpointJsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"O checkpoint WheelSpin não pôde ser salvo: {exception.Message} " +
                "O BOT parou antes da próxima ação irreversível.");
        }
    }

    private void ClearRecoveryCheckpoint(AutomationContext context)
    {
        var path = RecoveryCheckpointPath(context);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CalibrationRequiredException(
                $"O ciclo terminou, mas o checkpoint WheelSpin não pôde ser removido: {exception.Message}");
        }
    }

    private string RecoveryCheckpointPath(AutomationContext context) =>
        Path.Combine(context.Settings.DataDirectory, RecoveryCheckpointFileName);

    private static JsonSerializerOptions CreateRecoveryCheckpointJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private async Task<CalibrationRequiredException> CreateFailureAsync(
        AutomationContext context,
        string state,
        string message)
    {
        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, _profile.Workflow, state);
        return new CalibrationRequiredException($"{message} Diagnóstico local: {diagnostic}");
    }

    private string Shorten(string value) =>
        value.Length <= 260 ? value : value[..260] + "…";

    private sealed record ManufacturerOverlaySnapshot(
        bool IsOverlay,
        ManufacturerCell? Target,
        ManufacturerCell? Focused,
        int CellCount,
        double RowSpacing,
        double ColumnSpacing);

    private sealed record ManufacturerCell(
        string Text,
        Point Center,
        RectangleF Region,
        double FocusRatio,
        bool Selected);

    private sealed record TargetVehicleGridSnapshot(
        bool IsCarGrid,
        int FocusedCell,
        IReadOnlyList<int> CandidateCells,
        string FocusedCardText,
        string ContentFingerprint);

    private enum PurchaseScreenKind
    {
        Unknown,
        Colors,
        PriceDetails,
        PurchaseModal
    }

    private sealed record PurchaseScreenSnapshot(
        PurchaseScreenKind Kind,
        OcrLine? PurchaseAction,
        bool HasExpectedPrice,
        string Evidence);

    private sealed record PurchaseModalCheck(
        bool Valid,
        double FocusRatio,
        string Evidence);

    private sealed record RemovalDialogCheck(
        bool Valid,
        string Evidence);

    private sealed record RemovalCompletionProbe(
        bool Valid,
        string Evidence);

    private sealed record FinalPerkObservation(
        bool Valid,
        double MagentaRatio);

    private enum FinalPerkRecoveryState
    {
        Unknown,
        Locked,
        Purchased
    }

    private sealed record FinalPerkRecoveryObservation(
        FinalPerkRecoveryState State,
        double MagentaRatio);

    private enum SpinRecoveryStage
    {
        Unknown,
        PurchaseAuthorized,
        PurchaseConfirmed,
        FinalPerkConfirmed,
        RemovalAuthorized,
        RemovalConfirmed
    }

    private enum PurchaseRecoveryState
    {
        NotCommitted,
        PurchasedCurrent,
        FinalPerkCandidate,
        FinalPerkCurrentCandidate
    }

    private sealed record SpinRecoveryCheckpoint(
        int Version,
        Guid CycleId,
        string VehicleKey,
        SpinRecoveryStage Stage,
        int SkillPointsBeforeMastery,
        int? SkillPointsAfterFinal,
        long CreditsBeforePurchase,
        long CreditsAfterPurchase,
        DateTimeOffset ConfirmedAtUtc);

    private sealed record SpRefillIntent(
        int Version,
        string? VehicleKey,
        int TargetSkillPoints,
        int SkillPointsAtStart,
        int Attempts,
        int LastObservedSkillPoints,
        DateTimeOffset CreatedAtUtc);

    private sealed record PendingPromptProbe(
        bool HasPrompt,
        bool HasClassicalModal);
}
