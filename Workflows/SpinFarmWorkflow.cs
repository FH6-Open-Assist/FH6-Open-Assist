using System.Drawing;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public sealed class SpinFarmWorkflow : IMacroWorkflow
{
    public MacroKind Kind => MacroKind.FarmarWheelspins;

    public async Task RunAsync(
        AutomationContext context,
        MacroRunRequest request,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        var navigator = new GameNavigator(context);
        var settings = context.Settings.Spins;

        context.Logger.State(
            workflow,
            "Recursos",
            "Partindo da tela inicial da garagem e lendo SP e créditos disponíveis.");
        context.Telemetry.UpdateStage(
            "Lendo recursos",
            "Confirmando SP e créditos disponíveis antes de iniciar compras.");
        var resources = await navigator.OpenMasteryAndReadAsync(
            cancellationToken,
            normalizeGarageMenu: false,
            startFromGarageHome: true);
        var credits = await navigator.ReadCreditsAsync(cancellationToken);
        var spendableCredits = Math.Max(0, credits - settings.PreserveCredits);
        var purchasesBySp = resources.SkillPoints / settings.SkillPointsPerCar;
        var purchasesByCredits = spendableCredits / settings.CreditsPerCar;
        var purchases = (int)Math.Min(purchasesBySp, purchasesByCredits);

        context.Logger.State(
            workflow,
            "PlanejarCompras",
            $"Saldo: {resources.SkillPoints} SP e {credits:N0} CR. " +
            $"É possível concluir {purchases} compra(s) de {settings.SkillPointsPerCar} SP e " +
            $"{settings.CreditsPerCar:N0} CR.");
        context.Telemetry.UpdateStage(
            "Planejando ciclos",
            $"Recursos confirmados para até {purchases} ciclo(s) completo(s).");

        if (purchases <= 0)
        {
            context.Logger.State(
                workflow,
                "SemRecursos",
                "Não há SP ou créditos suficientes para outra compra; encerrando sem iniciar outro macro.");
            return;
        }

        for (var car = 1; car <= purchases; car++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Logger.State(workflow, "CicloCompra", $"Carro {car}/{purchases}.");
            context.Telemetry.UpdateStage(
                "Ciclo WheelSpin",
                $"Carro {car}/{purchases}: iniciando compra, Maestria, troca e remoção.");
            await ExecuteCarCycleAsync(context, navigator, cancellationToken);
            context.Telemetry.CycleCompleted(
                $"Carro {car}/{purchases}: compra, Maestria, troca e remoção confirmadas visualmente.");
        }

        context.Logger.State(
            workflow,
            "ComprasConcluidas",
            $"{purchases} compra(s) concluída(s). O limite de SP ou créditos foi atingido; encerrando.");
    }

    private static async Task ExecuteCarCycleAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        context.Logger.State(
            workflow,
            "CicloVisual",
            "Compra, Maestria, troca e remoção serão confirmadas visualmente em cada transição.");

        context.Telemetry.UpdateStage(
            "Comprando Mad Mike",
            "Abrindo a concessionária e confirmando a compra do carro configurado.");
        await OpenDealerAsync(context, navigator, cancellationToken);
        await BuyMadMikeAsync(context, cancellationToken);
        context.Telemetry.UpdateStage(
            "Desbloqueando Maestria",
            "Aplicando os pontos e confirmando visualmente o perk final.");
        await UnlockMasteryAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Trocando de carro",
            "Ativando outro carro antes de remover o Mad Mike utilizado.");
        await SwitchToAnotherCarAsync(context, navigator, cancellationToken);
        context.Telemetry.UpdateStage(
            "Removendo Mad Mike",
            "Localizando e removendo o carro após concluir a Maestria.");
        await RemoveMadMikeAsync(context, navigator, cancellationToken);

        context.Logger.State(
            workflow,
            "CicloVisualConcluido",
            "Mad Mike comprado, Maestria confirmada, outro carro ativado e Mad Mike removido.");
    }

    private static async Task OpenDealerAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        if (await context.Vision.ContainsAnyTextAsync(["COMPRAR CARRO"], cancellationToken))
        {
            context.Logger.State(workflow, "Concessionaria", "Tela Comprar Carro já aberta.");
            return;
        }

        await navigator.ReturnToGarageMenuAsync(cancellationToken);
        await navigator.OpenBuySellTabAsync(cancellationToken);
        context.Logger.State(
            workflow,
            "Concessionaria",
            "Normalizando no topo e abrindo Concessionária com o controle virtual.");
        for (var step = 0; step < 8; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ComprarCarro",
            ["COMPRAR CARRO"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
    }

    private static async Task BuyMadMikeAsync(
        AutomationContext context,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        context.Logger.State(workflow, "Fabricante", "Abrindo Ir para Fabricante com Backspace.");
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ListaFabricantesCompra",
            ["FABRICANTE"],
            cancellationToken);
        await SelectManufacturerAsync(
            context,
            "SelecionarMazdaCompra",
            "MAZDA",
            ["MAD MIKE 808", "#123 MAD MIKE", "MAD MIKE"],
            cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "SelecionarMadMike",
            ["MAD MIKE 808", "#123 MAD MIKE", "MAD MIKE"],
            cancellationToken);
        // A grade Mazda sempre abre com o primeiro cartão focado. O Forza às
        // vezes aceita o movimento do mouse apenas como hover e mantém o foco
        // do controle no primeiro carro; o Enter seguinte acabava abrindo o
        // MX-5. Navegar pela grade é determinístico: segunda linha, quarta
        // coluna. O preço é validado antes de abrir o carro.
        context.Logger.State(
            workflow,
            "SelecionarMadMike",
            "Texto confirmado; navegando do primeiro cartão para linha 2, coluna 4.");
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        for (var column = 1; column < 4; column++)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
        }

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ValidarMadMike",
            ["100.000", "100 000"],
            cancellationToken,
            TimeSpan.FromSeconds(5));

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "CoresFabricante",
            ["CORES DO FABRICANTE"],
            cancellationToken);
        context.Logger.State(
            workflow,
            "CoresFabricante",
            "Abrindo o cartão já selecionado com A; a confirmação será guiada pelo preço.");

        var priceOpen = false;
        for (var attempt = 1; attempt <= 2 && !priceOpen; attempt++)
        {
            // O primeiro A abre a seleção de cor; o segundo confirma a cor
            // padrão. Antes de qualquer segundo A validamos o preço para não
            // avançar uma tela a mais quando a transição for instantânea.
            context.Logger.State(workflow, "ConfirmarCor", $"A controlado {attempt}/2; aguardando o preço de 100.000 CR.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            var priceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            while (DateTime.UtcNow < priceDeadline && !priceOpen)
            {
                await Task.Delay(400, cancellationToken);
                priceOpen = await context.Vision.ContainsAnyTextAsync(
                    ["100.000", "100 000"],
                    cancellationToken);
            }
        }

        if (!priceOpen)
        {
            throw new CalibrationRequiredException(
                "O preço de 100.000 CR não apareceu após abrir e confirmar a cor padrão.");
        }

        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ConfirmarPreco",
            ["100.000", "100 000"],
            cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ConfirmarCompra",
            ["QUER COMPRAR CARRO", "COMPRAR VALES-CARRO"],
            cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);

        context.Logger.State(workflow, "Apresentacao", "Aguardando o fim da apresentação do carro comprado.");
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "FimApresentacao",
            ["EXPLODIR", "MODO FOTO", "OCULTAR UI", "ALTERNAR ALTURA DA CÂMERA"],
            cancellationToken,
            TimeSpan.FromMinutes(4));
        context.Resources.AdjustCredits(-context.Settings.Spins.CreditsPerCar);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
    }

    private static async Task UnlockMasteryAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        var mastery = await navigator.OpenMasteryAndReadAsync(cancellationToken);
        var normalizedCar = GameVisionService.Normalize(mastery.OcrText);
        if (!normalizedCar.Contains("MAD MIKE", StringComparison.Ordinal))
        {
            throw new CalibrationRequiredException(
                "A tela de Maestria não confirmou que o carro atual é o Mad Mike. " +
                "Nenhum ponto de habilidade foi gasto.");
        }

        context.Logger.State(
            workflow,
            "ConfirmarMadMikeNaMaestria",
            "Mad Mike confirmado pelo OCR antes de gastar pontos de habilidade.");
        if (mastery.SkillPoints < context.Settings.Spins.SkillPointsPerCar)
        {
            throw new AutomationFaultException(
                $"Há somente {mastery.SkillPoints} SP; são necessários {context.Settings.Spins.SkillPointsPerCar}.");
        }

        // Rota de 30 SP: XP inferior esquerdo -> perk inferior central ->
        // sobe toda a coluna central -> wheelspin superior direito.
        GameKey?[] directions = [GameKey.Right, GameKey.Up, GameKey.Up, GameKey.Up, GameKey.Right, null];
        for (var index = 0; index < directions.Length; index++)
        {
            await context.Vision.WaitForAnyTextAsync(
                workflow,
                $"SelecionarPerk{index + 1}",
                ["SELECIONAR"],
                cancellationToken);
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            // "Voltar" e "Desbloquear Tudo" já existem antes da compra e
            // não confirmam que a animação acabou. Durante essa animação o
            // Forza ignora o direcional seguinte. O teste real mostrou que o
            // marcador rosa e o débito de SP estabilizam em cerca de 2 s.
            context.Logger.State(
                workflow,
                $"ConfirmarPerk{index + 1}",
                "Aguardando a animação de aquisição terminar antes do próximo direcional.");
            await Task.Delay(2_500, cancellationToken);
            if (directions[index] is { } direction)
            {
                await context.Input.TapAsync(direction, cancellationToken);
            }
        }

        if (!await context.Vision.HasMagentaMarkerAsync(
                new RectangleF(0.14f, 0.14f, 0.34f, 0.56f),
                cancellationToken))
        {
            throw new CalibrationRequiredException(
                "A sequência terminou, mas o marcador rosa do perk final não foi confirmado.");
        }

        context.Logger.State(workflow, "MaestriaConcluida", "Perk final rosa confirmado.");
        context.Resources.SetSkillPoints(
            Math.Max(0, mastery.SkillPoints - context.Settings.Spins.SkillPointsPerCar),
            estimated: false);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
    }

    private static async Task SwitchToAnotherCarAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        await OpenMyCarsAsync(context, navigator, "TrocarCarro", cancellationToken);
        context.Logger.State(workflow, "OutroCarro", "Movendo um cartão à direita para escolher qualquer carro diferente.");
        await context.Input.TapAsync(GameKey.Right, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
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
            workflow,
            "AguardarTrocaDeCarro",
            "Aguardando a grade fechar e a opção Aprimorar e Tunar reaparecer na aba Carros.");
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "OutroCarroConfirmado",
            ["APRIMORAR E TUNAR"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
    }

    private static async Task RemoveMadMikeAsync(
        AutomationContext context,
        GameNavigator navigator,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        context.Logger.State(
            workflow,
            "RemoverCarro",
            "Normalizando o estado atual e abrindo Meus Carros antes da remoção identificada.");
        await OpenMyCarsAsync(context, navigator, "RemoverCarro", cancellationToken);
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ListaFabricantesRemocao",
            // "CARRO ATUAL" já existe na grade antes do Backspace e gerava
            // um falso positivo: os direcionais seguintes mudavam os carros
            // (chegando em Wuling) sem nunca abrir a lista de fabricantes.
            ["FABRICANTE"],
            cancellationToken);

        await SelectManufacturerAsync(
            context,
            "FiltrarMazda",
            "MAZDA",
            ["MAD MIKE 808", "#123 MAD MIKE", "MAD MIKE"],
            cancellationToken);

        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "MadMikeRemocao",
            ["MAD MIKE 808", "#123 MAD MIKE", "MAD MIKE"],
            cancellationToken);

        // O filtro abre no primeiro Mazda (RX-7); o primeiro Mad Mike é o
        // cartão imediatamente à direita. O painel de detalhes precisa mostrar
        // 100.000 CR antes de abrirmos as ações, evitando remover outro modelo.
        await context.Input.TapAsync(GameKey.Right, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ValidarMadMikeRemocao",
            ["100.000", "100 000"],
            cancellationToken,
            TimeSpan.FromSeconds(5));
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "AcoesDoMadMike",
            ["REMOVER CARRO DA GARAGEM"],
            cancellationToken);

        // Normalize no fim da lista para não depender do foco inicial nem do
        // primeiro direcional, que o jogo pode absorver enquanto o diálogo
        // termina de abrir. A última opção é denunciar/remover pintura; uma
        // posição acima é sempre Remover Carro da Garagem.
        await Task.Delay(1_000, cancellationToken);
        for (var step = 0; step < 8; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Up, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "ConfirmarRemocao",
            ["QUER MESMO REMOVER", "SIM"],
            cancellationToken);
        // A confirmação abre em Não; descer uma posição seleciona Sim.
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await WaitForTextToDisappearAsync(
            context,
            "RemocaoProcessada",
            ["QUER MESMO REMOVER"],
            TimeSpan.FromSeconds(20),
            cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            "RemocaoConcluida",
            ["MEUS CARROS", "IR PARA FABRICANTE"],
            cancellationToken,
            TimeSpan.FromSeconds(20));
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
    }

    private static async Task WaitForTextToDisappearAsync(
        AutomationContext context,
        string state,
        IReadOnlyCollection<string> texts,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
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
                        workflow,
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

    private static async Task OpenMyCarsAsync(
        AutomationContext context,
        GameNavigator navigator,
        string state,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        await navigator.ReturnToGarageMenuAsync(cancellationToken);
        if (!await context.Vision.ContainsAnyTextAsync(["MEUS CARROS"], cancellationToken))
        {
            await navigator.OpenCarsTabAsync(cancellationToken);
        }

        context.Logger.State(workflow, $"MeusCarros{state}", "Normalizando no topo e abrindo Meus Carros.");
        for (var step = 0; step < 8; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            workflow,
            $"MeusCarros{state}Confirmado",
            ["CARRO ATUAL", "IR PARA FABRICANTE"],
            cancellationToken);
    }

    private static async Task SelectManufacturerAsync(
        AutomationContext context,
        string state,
        string manufacturer,
        IReadOnlyCollection<string> successorTexts,
        CancellationToken cancellationToken)
    {
        const string workflow = "FarmarWheelspins";
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var normalizedManufacturer = GameVisionService.Normalize(manufacturer);
        var game = context.GameWindow.GetRequiredGameWindow();
        var width = game.ClientBounds.Width;
        var height = game.ClientBounds.Height;
        var target = document.Lines.FirstOrDefault(line =>
            GameVisionService.Normalize(line.Text).Contains(normalizedManufacturer, StringComparison.Ordinal));
        double targetX = target?.Center.X ?? 0;
        double targetY = target?.Center.Y ?? 0;
        if (target is null)
        {
            // O OCR do Windows omite ocasionalmente toda a terceira coluna da
            // grade de carros possuídos. Mazda vem imediatamente à direita de
            // Maserati nessa lista alfabética, então use o vizinho reconhecido
            // apenas para calcular o direcional; nenhum clique é enviado.
            var neighbor = document.Lines.FirstOrDefault(line =>
                GameVisionService.Normalize(line.Text).Contains("MASERATI", StringComparison.Ordinal));
            if (manufacturer != "MAZDA" || neighbor is null)
            {
                throw new CalibrationRequiredException(
                    $"Não foi possível localizar o fabricante {manufacturer} nem um vizinho seguro na grade.");
            }

            targetX = neighbor.Center.X + width * 0.20;
            targetY = neighbor.Center.Y;
            context.Logger.State(
                workflow,
                state,
                "OCR omitiu Mazda; posição calculada como uma célula à direita de Maserati.");
        }

        var rowCenters = document.Lines
            .Where(line =>
                line.Center.X >= width * 0.08 && line.Center.X <= width * 0.92 &&
                line.Center.Y >= height * 0.20 && line.Center.Y <= height * 0.90)
            .Select(line => line.Center.Y)
            .OrderBy(y => y)
            .Aggregate(
                new List<double>(),
                (rows, y) =>
                {
                    if (rows.Count == 0 || y - rows[^1] > height * 0.018)
                    {
                        rows.Add(y);
                    }
                    else
                    {
                        rows[^1] = (rows[^1] + y) / 2;
                    }
                    return rows;
                });

        var row = rowCenters
            .Select((y, index) => new { Distance = Math.Abs(y - targetY), Index = index })
            .OrderBy(item => item.Distance)
            .First().Index;
        var column = Math.Clamp(
            (int)Math.Round((targetX / width - 0.20) / 0.20),
            0,
            3);

        var firstColumnCenter = targetX - column * width * 0.20;
        var cells = rowCenters
            .SelectMany((y, rowIndex) => Enumerable.Range(0, 4).Select(columnIndex =>
                new RectangleF(
                    (float)((firstColumnCenter + columnIndex * width * 0.20) / width - 0.095),
                    (float)(y / height - 0.027),
                    0.19f,
                    0.054f)))
            .ToArray();
        var selectedCell = await context.Vision.FindLimeSelectionAsync(cells, cancellationToken);
        if (selectedCell < 0)
        {
            throw new CalibrationRequiredException(
                $"Não foi possível localizar o contorno verde atual na grade de fabricantes antes de selecionar {manufacturer}.");
        }

        var selectedRow = selectedCell / 4;
        var selectedColumn = selectedCell % 4;

        context.Logger.State(
            workflow,
            state,
            $"Foco atual na linha {selectedRow + 1}, coluna {selectedColumn + 1}; " +
            $"{manufacturer} na linha {row + 1}, coluna {column + 1}. Movendo somente a diferença exata.");
        while (selectedRow > row)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            selectedRow--;
        }
        while (selectedRow < row)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            selectedRow++;
        }
        while (selectedColumn > column)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken);
            selectedColumn--;
        }
        while (selectedColumn < column)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
            selectedColumn++;
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            workflow,
            $"{state}Confirmado",
            successorTexts,
            cancellationToken);
    }
}
