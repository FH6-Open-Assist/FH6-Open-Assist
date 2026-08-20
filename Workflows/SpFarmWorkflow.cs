using System.Drawing;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class SpFarmWorkflow : IMacroWorkflow
{
    public MacroKind Kind => MacroKind.FarmarSp;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";

        context.Logger.State(
            workflow,
            "Inicio",
            $"Início rápido na rua: Impreza 22B e Assistência Total são pré-requisitos. " +
            "Execução contínua até F8, sem estimar o saldo total de SP.");

        await OpenEventLabChallengeWithVisionAsync(context, cancellationToken);
        await RunRaceLoopAsync(context, cancellationToken);
    }

    private static async Task OpenEventLabChallengeAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        context.Logger.State(
            workflow,
            "EntrarNoDesafio",
            "Executando o fluxo Pulover calibrado para abrir o desafio 122697651, sem OCR intermediário.");

        await Task.Delay(333, cancellationToken);
        await Task.Delay(1_218, cancellationToken);
        await PulseTimedAsync(context, GameKey.Menu, 141, 750, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 78, 188, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 93, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 78, 422, cancellationToken);
        await PulseTimedAsync(context, GameKey.PageDown, 94, 703, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 109, 672, cancellationToken);
        await PulseTimedAsync(context, GameKey.Down, 172, 422, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 109, 735, cancellationToken);
        await PulseTimedAsync(context, GameKey.Backspace, 109, 562, cancellationToken);
        await PulseTimedAsync(context, GameKey.Up, 109, 453, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 125, 1_235, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad1, 62, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad2, 109, 157, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad2, 78, 828, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad6, 78, 141, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad9, 125, 109, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad7, 62, 329, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad6, 78, 156, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad5, 94, 93, cancellationToken);
        await PulseTimedAsync(context, GameKey.NumPad1, 110, 1_094, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 78, 906, cancellationToken);
        await PulseTimedAsync(context, GameKey.Down, 109, 172, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 94, 1_609, cancellationToken);
        await PulseTimedAsync(context, GameKey.Enter, 172, 0, cancellationToken);
    }

    private static async Task PulseTimedAsync(
        AutomationContext context,
        GameKey key,
        int holdMilliseconds,
        int waitAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        await context.Input.HoldAsync(key, holdMilliseconds, cancellationToken);

        if (waitAfterMilliseconds > 0)
        {
            await Task.Delay(waitAfterMilliseconds, cancellationToken);
        }
    }

    private static async Task OpenEventLabChallengeWithVisionAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        context.Logger.State(workflow, "AbrirMenuRua", "Abrindo o menu da rua.");
        var pauseMenuOpened = false;
        for (var attempt = 1; attempt <= 3 && !pauseMenuOpened; attempt++)
        {
            await context.Input.TapAsync(GameKey.Menu, cancellationToken);
            var attemptDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < attemptDeadline && !pauseMenuOpened)
            {
                await Task.Delay(700, cancellationToken);
                pauseMenuOpened = await context.Vision.ContainsAnyTextAsync(
                    ["CENTRAL CRIATIVA", "MEU HORIZON", "ONLINE", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"],
                    cancellationToken);
            }
            if (!pauseMenuOpened)
            {
                if (await context.Vision.ContainsAnyTextAsync(
                        ["ENTRAR NA CASA", "CASA EM TÓQUIO", "CASA EM TOQUIO"],
                        cancellationToken))
                {
                    context.Logger.State(
                        workflow,
                        "SairDaEntradaDaCasa",
                        "A entrada da casa interceptou Esc; avançando com RT antes da próxima tentativa.");
                    await context.Input.HoldAsync(GameKey.W, 2_000, cancellationToken);
                    await Task.Delay(2_500, cancellationToken);
                }

                context.Logger.State(
                    workflow,
                    "AbrirMenuRua",
                    $"O menu ainda não apareceu após Esc ({attempt}/3); tentando novamente.");
            }
        }

        if (!pauseMenuOpened)
        {
            _ = await context.Vision.WaitForAnyTextAsync(
                workflow,
                "AbrirMenuRuaConfirmado",
                ["CENTRAL CRIATIVA", "MAPA DO MUNDO", "CONFIGURAÇÕES", "CONFIGURACOES"],
                cancellationToken,
                TimeSpan.FromSeconds(8));
        }

        context.Logger.State(
            workflow,
            "CentralCriativa",
            "Procurando a aba Central Criativa com LB, sem mover o foco do Windows.");
        var creativeTabOpen = false;
        for (var attempt = 1; attempt <= 6 && !creativeTabOpen; attempt++)
        {
            creativeTabOpen = await context.Vision.ContainsAnyTextAsync(
                ["CRIAR E NAVEGAR POR EVENTOS", "MINHA CENTRAL CRIATIVA"],
                cancellationToken);
            if (creativeTabOpen)
            {
                break;
            }

            context.Logger.State(workflow, "CentralCriativa", $"Avançando uma aba com LB ({attempt}/6).");
            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 60);
            await Task.Delay(700, cancellationToken);
        }

        if (!creativeTabOpen)
        {
            _ = await context.Vision.WaitForAnyTextAsync(
                workflow,
                "CentralCriativaConfirmada",
                ["CRIAR E NAVEGAR POR EVENTOS", "MINHA CENTRAL CRIATIVA"],
                cancellationToken,
                TimeSpan.FromSeconds(5));
        }

        context.Logger.State(workflow, "EventLab", "O banner EventLab está selecionado; abrindo com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "EventLabConfirmado",
            ["LABORATÓRIO DE EVENTOS", "LABORATORIO DE EVENTOS", "JOGAR DESAFIOS"],
            cancellationToken,
            TimeSpan.FromSeconds(15));
        context.Logger.State(
            workflow,
            "JogarDesafios",
            "A tela inicia em Jogar Evento; descendo uma posição e confirmando Jogar Desafios.");
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "JogarDesafiosConfirmado",
            ["OPÇÕES DO DESAFIO", "OPCOES DO DESAFIO"],
            cancellationToken);

        context.Logger.State(workflow, "Buscar", "Abrindo filtros com Backspace.");
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "CodigoCompartilhamento",
            ["CÓD. DE COMPARTILHAMENTO", "COD. DE COMPARTILHAMENTO", "CÓDIGO DE COMPARTILHAMENTO"],
            cancellationToken,
            TimeSpan.FromSeconds(8));
        context.Logger.State(
            workflow,
            "CodigoCompartilhamento",
            "Localizando visualmente a borda verde do campo Cód. de Compartilhamento.");
        // Na busca de Desafios, o campo fica logo acima de Confirmar. Use
        // apenas a lateral esquerda da linha para não confundir a borda
        // horizontal verde do botão Confirmar quando ele está selecionado.
        var codeRow = new RectangleF(0.275f, 0.605f, 0.03f, 0.045f);
        var codeSelected = await context.Vision.HasLimeSelectionAsync(
            codeRow,
            cancellationToken);
        for (var step = 1; step <= 12 && !codeSelected; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            codeSelected = await context.Vision.HasLimeSelectionAsync(codeRow, cancellationToken);
        }
        for (var step = 1; step <= 12 && !codeSelected; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            codeSelected = await context.Vision.HasLimeSelectionAsync(codeRow, cancellationToken);
        }
        if (!codeSelected)
        {
            throw new CalibrationRequiredException(
                "Não foi possível posicionar a seleção em Cód. de Compartilhamento pelo controle virtual.");
        }
        context.Logger.State(
            workflow,
            "CodigoCompartilhamento",
            "Borda verde confirmada no campo; abrindo com A.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "CampoCodigoPronto",
            ["DIGITE O CÓDIGO DE COMPARTILHAMENTO", "DIGITE O CODIGO DE COMPARTILHAMENTO"],
            cancellationToken,
            TimeSpan.FromSeconds(8));
        await Task.Delay(500, cancellationToken);
        await context.Input.TypeTextAsync(context.Settings.Sp.ShareCode, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        context.Logger.State(
            workflow,
            "ConfirmarBusca",
            "Voltando ao filtro, descendo uma posição até Confirmar e selecionando com A.");
        await Task.Delay(900, cancellationToken);
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ResultadoBusca",
            ["ALWAYS WIN2", "RESULTADOS"],
            cancellationToken,
            TimeSpan.FromSeconds(20));
        context.Logger.State(workflow, "SelecionarEvento", "Selecionando o único evento retornado pela busca.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        context.Logger.State(
            workflow,
            "Cinematica",
            "Evento aberto; o temporizador calibrado da corrida cuidará da apresentação e da contagem regressiva.");
    }

    private static async Task RunRaceLoopAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarSP";
        var earnedPoints = 0;
        var race = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Logger.State(
                workflow,
                "Corrida",
                $"Corrida {race}: sequência temporizada, sem captura contínua da tela.");
            await Task.Delay(333, cancellationToken);
            await Task.Delay(15_859, cancellationToken);
            await context.Input.HoldAsync(GameKey.W, 37_500, cancellationToken);

            await Task.Delay(2_016, cancellationToken);
            earnedPoints += context.Settings.Sp.PointsPerRace;
            context.Logger.State(
                workflow,
                "Resultado",
                $"Corrida {race} contabilizada; {earnedPoints} SP ganhos nesta execução, sem inferir o saldo total.");

            context.Logger.State(
                workflow,
                "TentarNovamente",
                "Resultado já está pronto pelo temporizador; pressionando Esc por 156 ms para tentar novamente.");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken, 156);
            race++;
        }
    }
}
