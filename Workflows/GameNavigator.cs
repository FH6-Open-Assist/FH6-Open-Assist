using System.Drawing;
using System.Text.RegularExpressions;
using ForzaFarm.Core;
using ForzaFarm.Vision;
using ForzaFarm.Windows;

namespace ForzaFarm.Workflows;

public sealed record MasterySnapshot(int SkillPoints, bool SubaruSelected, string OcrText);

public sealed class GameNavigator(AutomationContext context)
{
    private const string Workflow = "Navegação";
    private int _difficultyRowIndex;

    public async Task EnsureGarageAsync(CancellationToken cancellationToken)
    {
        _ = await context.GameWindow.WaitForGameAsync(cancellationToken);
        if (await context.Vision.ContainsAnyTextAsync(
                ["COMEÇAR JOGO", "COMECAR JOGO"],
                cancellationToken))
        {
            context.Logger.State(
                Workflow,
                "IniciarJogo",
                "Tela inicial detectada; iniciando com A pelo controle virtual, sem alterar o foco.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            _ = await context.Vision.WaitForAnyTextAsync(
                Workflow,
                "AguardarJogo",
                ["DIRIGIR", "MEU HORIZON", "MAPA DO MUNDO", "CENTRAL CRIATIVA", "CONTROLE DESCONECTADO"],
                cancellationToken,
                TimeSpan.FromMinutes(3));
        }

        if (await context.Vision.ContainsAnyTextAsync(
                ["CONTROLE DESCONECTADO", "RECONECTE UM CONTROLE"],
                cancellationToken))
        {
            context.Logger.State(
                Workflow,
                "ReconectarControle",
                "O jogo reconheceu o Xbox virtual; confirmando a reconexão com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await Task.Delay(1_000, cancellationToken);
        }

        if (await context.Vision.ContainsAnyTextAsync(
                ["QUER MESMO SAIR DO MODO FOTO"],
                cancellationToken))
        {
            context.Logger.State(
                Workflow,
                "SairModoFoto",
                "Confirmação do Modo Foto detectada; escolhendo Sim com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await Task.Delay(1_500, cancellationToken);
        }

        if (await context.Vision.ContainsAnyTextAsync(
                ["EXPLODIR", "OCULTAR UI", "ALTERNAR ALTURA DA CÂMERA"],
                cancellationToken))
        {
            context.Logger.State(
                Workflow,
                "FecharApresentacaoCarro",
                "Tela de apresentação do carro detectada; voltando com B antes de localizar a garagem.");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
            await Task.Delay(1_500, cancellationToken);
        }

        // Depois de alguns segundos sem entrada, a garagem oculta toda a UI.
        // Dê tempo para a animação do menu reaparecer antes de concluir que o
        // carro está na rua; a leitura imediata gerava uma falsa categoria.
        for (var wakeAttempt = 1; wakeAttempt <= 2; wakeAttempt++)
        {
            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 40);
            await Task.Delay(1_200, cancellationToken);
            if (await IsGarageAsync(cancellationToken))
            {
                context.Logger.State(Workflow, "GarantirGaragem", "A garagem já está aberta.");
                return;
            }
        }

        await LeaveHouseEntranceIfNeededAsync(cancellationToken);
        var screen = await context.Vision.ReadScreenAsync(cancellationToken);
        var normalized = GameVisionService.Normalize(screen.Text);

        // Uma transição de submenu pode terminar entre a última tentativa de
        // despertar e este frame. Revalide o próprio texto que será usado para
        // decidir a próxima ação, evitando enviar Esc/cliques de rua dentro da
        // garagem.
        if (IsGarageText(normalized))
        {
            context.Logger.State(Workflow, "GarantirGaragem", "Menu da garagem confirmado na releitura.");
            return;
        }

        if (HasAny(
                normalized,
                "MEUS CARROS",
                "IR PARA FABRICANTE",
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "COMPRAR CARRO"))
        {
            context.Logger.State(
                Workflow,
                "RetornarDoSubmenu",
                "Submenu da garagem detectado; retornando ao menu inicial com B.");
            await ReturnToGarageMenuAsync(cancellationToken);
            return;
        }

        // Ao concluir um desafio do EventLab, o jogo pode pedir uma avaliação
        // antes de devolver o controle à rua. Enquanto esse diálogo estiver
        // aberto, Esc apenas o cancela e ainda não abre o menu de pausa.
        if (HasAny(normalized, "AVALIAR DESAFIO", "QUER CURTIR ESTE DESAFIO"))
        {
            context.Logger.State(
                Workflow,
                "FecharAvaliacaoDesafio",
                "Diálogo de avaliação detectado; selecionando Cancelar antes de abrir o menu da rua.");
            // A tela não possui atalho Esc: inicia em Curtir, seguida de Não
            // Gostei e Cancelar.
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
            await Task.Delay(10_000, cancellationToken);
            screen = await context.Vision.ReadScreenAsync(cancellationToken);
            normalized = GameVisionService.Normalize(screen.Text);
        }

        if (HasAny(
                normalized,
                "MEUS DESAFIOS",
                "CRIADOR FAVORITO",
                "OPCOES DO DESAFIO",
                "LABORATORIO DE EVENTOS",
                "RESULTADOS DA BUSCA",
                "COD DE COMPARTILHAMENTO"))
        {
            context.Logger.State(
                Workflow,
                "SairDoEventLab",
                "Submenu do EventLab detectado; voltando com B até o menu de pausa.");
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await context.Input.TapAsync(GameKey.Escape, cancellationToken);
                await Task.Delay(900, cancellationToken);
                screen = await context.Vision.ReadScreenAsync(cancellationToken);
                normalized = GameVisionService.Normalize(screen.Text);

                if (HasAny(normalized, "MAPA DO MUNDO", "CENTRAL CRIATIVA", "MEU HORIZON") &&
                    HasAny(normalized, "CAMPANHA", "ONLINE", "CONFIGURACOES"))
                {
                    context.Logger.State(
                        Workflow,
                        "SairDoEventLab",
                        $"Menu de pausa recuperado após {attempt} comando(s) B.");
                    break;
                }
            }
        }

        var confirmationOpen = HasAny(normalized, "QUER FAZER UMA VIAGEM", "VIAGEM RAPIDA ATE CASA") &&
                               normalized.Contains("SIM", StringComparison.Ordinal);
        var travelBannerOpen = HasAny(normalized, "VIAJAR PARA CASA", "VOLTAR PARA CASA");
        // O OCR ocasionalmente omite somente o rótulo pequeno "Meu Horizon"
        // mesmo com a aba visível. Identifique o menu também pelos cartões e
        // pelas demais abas exclusivas dessa tela.
        var pauseMenuOpen = HasAny(normalized, "MAPA DO MUNDO", "CENTRAL CRIATIVA", "ONLINE") &&
                            HasAny(normalized, "CAMPANHA", "CONFIGURAÇÕES", "CONFIGURACOES", "MEU HORIZON");

        if (!confirmationOpen && !travelBannerOpen)
        {
            if (!pauseMenuOpen)
            {
                context.Logger.State(Workflow, "GarantirGaragem", "Estado de rua detectado; abrindo o menu de pausa.");
                await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                await Task.Delay(1_000, cancellationToken);
                var afterEscape = await context.Vision.ReadScreenAsync(cancellationToken);
                var afterEscapeNormalized = GameVisionService.Normalize(afterEscape.Text);

                // Na saída do EventLab o carro pode nascer exatamente sobre
                // o gatilho da Casa. Nessa situação o primeiro Esc revela
                // "Entrar na Casa" em vez do menu de pausa. Afaste o carro e
                // tente Esc novamente, sempre soltando W no finally do helper.
                if (HasAny(afterEscapeNormalized, "ENTRAR NA CASA", "CASA EM TOQUIO"))
                {
                    await LeaveHouseEntranceIfNeededAsync(cancellationToken);
                    await context.Input.TapAsync(GameKey.Menu, cancellationToken);
                    await Task.Delay(1_000, cancellationToken);
                    afterEscape = await context.Vision.ReadScreenAsync(cancellationToken);
                    afterEscapeNormalized = GameVisionService.Normalize(afterEscape.Text);
                }
                if (IsGarageText(afterEscapeNormalized))
                {
                    context.Logger.State(
                        Workflow,
                        "GarantirGaragem",
                        "O menu da garagem reapareceu após Esc; cancelando a navegação de rua.");
                    return;
                }
                await context.Vision.WaitForAnyTextAsync(
                    Workflow,
                    "AguardarMenuPausa",
                    ["MEU HORIZON", "MAPA DO MUNDO", "CENTRAL CRIATIVA", "ONLINE", "SAIR DO JOGO"],
                    cancellationToken);
            }
            else
            {
                context.Logger.State(Workflow, "GarantirGaragem", "Menu de pausa já está aberto; mantendo-o aberto.");
            }

            // Navegue pelas abas com LB. Diferentemente do clique de mouse, o
            // controle virtual é lido pelo Forza mesmo quando ele está sem foco.
            var meuHorizonOpen = false;
            for (var attempt = 1; attempt <= 7 && !meuHorizonOpen; attempt++)
            {
                context.Logger.State(
                    Workflow,
                    "AbrirMeuHorizon",
                    $"Procurando Meu Horizon com LB ({attempt}/7).");
                await context.Input.TapAsync(GameKey.Shift, cancellationToken, 60);
                await Task.Delay(700, cancellationToken);
                meuHorizonOpen = await context.Vision.ContainsAnyTextAsync(
                    ["VIAGEM RÁPIDA PARA CASA", "VIAJAR PARA CASA", "VOLTAR PARA CASA"],
                    cancellationToken);
            }

            if (!meuHorizonOpen)
            {
                await context.Vision.WaitForAnyTextAsync(
                    Workflow,
                    "AbrirMeuHorizonConfirmado",
                    ["VIAGEM RÁPIDA PARA CASA", "VIAJAR PARA CASA", "VOLTAR PARA CASA"],
                    cancellationToken,
                    TimeSpan.FromSeconds(5));
            }
            context.Logger.State(Workflow, "ViajarParaCasa", "Abrindo o banner selecionado com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }
        else if (travelBannerOpen && !confirmationOpen)
        {
            context.Logger.State(Workflow, "GarantirGaragem", "Meu Horizon já está aberto; selecionando a viagem para casa.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }
        else
        {
            context.Logger.State(Workflow, "GarantirGaragem", "Confirmação de viagem já está aberta.");
        }

        var postTravel = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "ResultadoDaViagem",
            ["SIM", "DIRIGIR", "CONFIGURAÇÕES", "CONFIGURACOES"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        if (GameVisionService.Normalize(postTravel.Line.Text).Contains("SIM", StringComparison.Ordinal))
        {
            context.Logger.State(Workflow, "ConfirmarViagem", "Confirmando Sim com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }
        else
        {
            context.Logger.State(
                Workflow,
                "ConfirmarViagem",
                "A viagem já foi confirmada pela ação anterior; a garagem abriu sem uma confirmação pendente.");
        }
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AguardarGaragem",
            ["DIRIGIR", "DIÁRIO DE COLEÇÃO", "CONFIGURAÇÕES"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
    }

    public async Task OpenDifficultyAsync(CancellationToken cancellationToken)
    {
        await EnsureGarageAsync(cancellationToken);
        await ReturnToGarageMenuAsync(cancellationToken);
        await OpenGarageTabAsync(
            "AbaCampanha",
            // "Dirigir" também existe no rodapé de todas as abas. O Diário
            // é exclusivo da Campanha e evita validar a aba Carros por engano.
            ["DIÁRIO DE COLEÇÃO", "DIARIO DE COLECAO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "AbrirConfiguracoes",
            "Normalizando o menu Campanha no topo e abrindo Configurações.");
        for (var step = 0; step < 5; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        for (var step = 0; step < 3; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AbrirConfiguracoesConfirmado",
            ["ACESSIBILIDADE VISUAL", "GRÁFICOS E DESEMPENHO", "GRAFICOS E DESEMPENHO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "SelecionarDificuldade",
            "Dificuldade é a primeira categoria e já vem selecionada; ativando o painel com Enter.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "SelecionarDificuldadeConfirmado",
            ["VOLTAR AO PADRÃO", "VOLTAR AO PADRAO"],
            cancellationToken);
        _difficultyRowIndex = 0;
    }

    public async Task SetDifficultyOptionAsync(
        string rowLabel,
        string desiredValue,
        GameKey direction,
        int maximumSteps,
        CancellationToken cancellationToken)
    {
        var desiredNormalized = GameVisionService.Normalize(desiredValue);
        var screen = await context.Vision.ReadScreenAsync(cancellationToken);
        if (GameVisionService.Normalize(screen.Text).Contains(desiredNormalized, StringComparison.Ordinal))
        {
            context.Logger.State(Workflow, "ConfigurarDificuldade", $"'{desiredValue}' já está aplicado.");
            return;
        }

        var rowIndex = GameVisionService.Normalize(rowLabel)
            .Contains("PREDEFINICAO DE ASSISTENCIA", StringComparison.Ordinal)
            ? 1
            : 0;
        context.Logger.State(
            Workflow,
            "SelecionarLinhaDificuldade",
            $"Movendo da linha {_difficultyRowIndex + 1} para a linha {rowIndex + 1}: '{rowLabel}'.");
        while (_difficultyRowIndex < rowIndex)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            _difficultyRowIndex++;
        }
        while (_difficultyRowIndex > rowIndex)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            _difficultyRowIndex--;
        }

        for (var step = 1; step <= maximumSteps; step++)
        {
            await context.Input.TapAsync(direction, cancellationToken);
            screen = await context.Vision.ReadScreenAsync(cancellationToken);
            if (GameVisionService.Normalize(screen.Text).Contains(desiredNormalized, StringComparison.Ordinal))
            {
                context.Logger.State(
                    Workflow,
                    "ConfigurarDificuldade",
                    $"'{rowLabel}' definido como '{desiredValue}' em {step} ajuste(s).");
                return;
            }
        }

        throw new CalibrationRequiredException(
            $"Não foi possível definir '{rowLabel}' como '{desiredValue}' após {maximumSteps} passos.");
    }

    public async Task SaveDifficultyAndReturnAsync(CancellationToken cancellationToken)
    {
        context.Logger.State(
            Workflow,
            "SalvarDificuldade",
            "Saindo do painel com B; se houver alterações, confirmaremos 'Salvar e Continuar'.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);

        var outcome = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "ConfirmarSalvarDificuldade",
            [
                "ALTERAÇÕES NÃO SALVAS",
                "ALTERACOES NAO SALVAS",
                "SALVAR E CONTINUAR",
                "DIRIGIR",
                "CONFIGURAÇÕES",
                "DIÁRIO DE COLEÇÃO"
            ],
            cancellationToken,
            TimeSpan.FromSeconds(8));

        var normalizedOutcome = GameVisionService.Normalize(outcome.Line.Text);
        if (normalizedOutcome.Contains("NAO SALVAS", StringComparison.Ordinal) ||
            normalizedOutcome.Contains("SALVAR E CONTINUAR", StringComparison.Ordinal))
        {
            context.Logger.State(
                Workflow,
                "ConfirmarSalvarDificuldade",
                "Confirmando a opção selecionada 'Salvar e Continuar' com A.");
            await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        }

        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "RetornoGaragem",
            ["DIRIGIR", "CONFIGURAÇÕES", "DIÁRIO DE COLEÇÃO"],
            cancellationToken,
            TimeSpan.FromSeconds(10));
    }

    public async Task<MasterySnapshot> OpenMasteryAndReadAsync(
        CancellationToken cancellationToken,
        bool normalizeGarageMenu = true,
        bool startFromGarageHome = false)
    {
        if (startFromGarageHome)
        {
            context.Logger.State(
                Workflow,
                "AbaCarros",
                "Partindo da aba Campanha: avançando duas vezes com RB/PgDn até Carros.");
            await context.Input.TapAsync(GameKey.PageDown, cancellationToken);
            await context.Input.TapAsync(GameKey.PageDown, cancellationToken);
            await context.Vision.WaitForAnyTextAsync(
                Workflow,
                "AbaCarros",
                ["APRIMORAR E TUNAR"],
                cancellationToken);
        }
        else
        {
            if (normalizeGarageMenu)
            {
                await ReturnToGarageMenuAsync(cancellationToken);
            }
            await OpenGarageTabAsync(
                "AbaCarros",
                ["APRIMORAR E TUNAR"],
                cancellationToken);
        }
        context.Logger.State(
            Workflow,
            "AprimorarETunar",
            startFromGarageHome
                ? "Descendo uma posição e abrindo Aprimorar e Tunar."
                : "Normalizando a seleção no topo e abrindo Aprimorar e Tunar.");
        if (!startFromGarageHome)
        {
            for (var step = 0; step < 7; step++)
            {
                await context.Input.TapAsync(GameKey.Up, cancellationToken);
            }
        }
        await context.Input.TapAsync(GameKey.Down, cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AprimorarETunarConfirmado",
            ["MAESTRIA DE CARRO"],
            cancellationToken);

        context.Logger.State(
            Workflow,
            "MaestriaDeCarro",
            startFromGarageHome
                ? "Descendo sete posições e abrindo Maestria de Carro."
                : "Normalizando a seleção no topo e descendo sete posições até Maestria de Carro.");
        if (!startFromGarageHome)
        {
            for (var step = 0; step < 8; step++)
            {
                await context.Input.TapAsync(GameKey.Up, cancellationToken);
            }
        }
        for (var step = 0; step < 7; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "MaestriaDeCarroConfirmada",
            ["PONTOS DISPONÍVEIS", "PONTOS DISPONIVEIS"],
            cancellationToken);

        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var pointsLabel = document.Lines.FirstOrDefault(line =>
            GameVisionService.Normalize(line.Text).Contains("PONTOS DISPONIVEIS", StringComparison.Ordinal));
        var points = pointsLabel is null
            ? (int?)null
            : document.Lines
                .Where(line => Math.Abs(line.Center.Y - pointsLabel.Center.Y) <= Math.Max(12, pointsLabel.Height))
                .SelectMany(line => GameVisionService.ExtractNumbers(line.Text))
                .Where(number => number is >= 0 and <= 999)
                .DefaultIfEmpty(-1)
                .Max();
        if (points is null or < 0)
        {
            var rawPoints = await context.Vision.ReadLargestNumberAsync(
                new RectangleF(0.14f, 0.80f, 0.30f, 0.10f),
                9_999,
                Workflow,
                "LerPontosDisponiveis",
                cancellationToken);
            points = rawPoints > 999 ? rawPoints / 10 : rawPoints;
        }
        context.Resources.SetSkillPoints(points.Value, estimated: false);
        var normalized = GameVisionService.Normalize(document.Text);
        var isSubaru = normalized.Contains("SUBARU", StringComparison.Ordinal) &&
                       (normalized.Contains("22B", StringComparison.Ordinal) ||
                        normalized.Contains("IMPREZA", StringComparison.Ordinal));
        context.Logger.State(
            Workflow,
            "LerPontosECarro",
            $"SP exatos: {points.Value}; Subaru 22B selecionado: {(isSubaru ? "sim" : "não")}.");
        return new MasterySnapshot(points.Value, isSubaru, document.Text);
    }

    public async Task SelectSubaru22BAsync(CancellationToken cancellationToken)
    {
        context.Logger.State(Workflow, "SelecionarSubaru", "O carro atual não é o Subaru 22B; abrindo Meus Carros.");
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await Task.Delay(800, cancellationToken);
        await context.Input.TapAsync(GameKey.Escape, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "RetornarAoMenuCarros",
            ["MEUS CARROS"],
            cancellationToken);

        context.Logger.State(Workflow, "AbrirMeusCarros", "Normalizando no topo e abrindo Meus Carros.");
        for (var step = 0; step < 7; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AbrirMeusCarrosConfirmado",
            ["IR PARA FABRICANTE", "CARRO ATUAL"],
            cancellationToken);

        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "ListaDeFabricantes",
            ["FABRICANTE", "SUBARU"],
            cancellationToken);

        await SelectSubaruManufacturerAsync(cancellationToken);

        await FocusSubaru22BCardAsync(cancellationToken);
        context.Logger.State(Workflow, "Selecionar22B", "22B confirmado pela borda verde; abrindo as ações com Enter.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AcoesDo22B",
            ["ENTRAR NO CARRO"],
            cancellationToken);
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "EntrarNoSubaruConfirmado",
            ["DIRIGIR", "CONFIGURAÇÕES", "CONFIGURACOES"],
            cancellationToken,
            TimeSpan.FromMinutes(2));
        await Task.Delay(2_000, cancellationToken);
    }

    private async Task FocusSubaru22BCardAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var game = context.GameWindow.GetRequiredGameWindow();
        var width = game.ClientBounds.Width;
        var height = game.ClientBounds.Height;
        var target = document.Lines.FirstOrDefault(line =>
        {
            var normalized = GameVisionService.Normalize(line.Text);
            return normalized.Contains("IMPREZA 22B", StringComparison.Ordinal) ||
                   (normalized.Contains("22B", StringComparison.Ordinal) &&
                    normalized.Contains("STI", StringComparison.Ordinal));
        });
        if (target is null)
        {
            throw new CalibrationRequiredException(
                "O filtro Subaru abriu, mas não foi possível localizar o cartão do Impreza 22B-STI pelo texto.");
        }

        // A posição é inferida do texto do próprio cartão. Assim, carros Subaru
        // adquiridos depois podem deslocar o 22B sem quebrar a navegação.
        const double columnSpacing = 0.18;
        const double rowSpacing = 0.232;
        var targetColumn = Math.Clamp(
            (int)Math.Round((target.Center.X / (double)width - 0.25) / columnSpacing),
            0,
            3);
        var targetRow = Math.Clamp(
            (int)Math.Round((target.Center.Y / (double)height - 0.14) / rowSpacing),
            0,
            2);
        var firstColumnCenter = target.Center.X / (double)width - targetColumn * columnSpacing;
        var firstRowTitleCenter = target.Center.Y / (double)height - targetRow * rowSpacing;
        var cells = Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 4).Select(column =>
                new RectangleF(
                    (float)(firstColumnCenter + column * columnSpacing - 0.087),
                    (float)(firstRowTitleCenter + row * rowSpacing - 0.045),
                    0.174f,
                    0.225f)))
            .ToArray();
        var selectedCell = await context.Vision.FindLimeSelectionAsync(cells, cancellationToken);
        if (selectedCell < 0)
        {
            throw new CalibrationRequiredException(
                "Não foi possível localizar o cartão atualmente selecionado antes de escolher o Impreza 22B-STI.");
        }

        var selectedRow = selectedCell / 4;
        var selectedColumn = selectedCell % 4;
        context.Logger.State(
            Workflow,
            "Selecionar22B",
            $"Cartão atual na linha {selectedRow + 1}, coluna {selectedColumn + 1}; " +
            $"22B detectado na linha {targetRow + 1}, coluna {targetColumn + 1}. Movendo somente a diferença exata.");

        while (selectedRow > targetRow)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            selectedRow--;
        }

        while (selectedRow < targetRow)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            selectedRow++;
        }

        while (selectedColumn > targetColumn)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken);
            selectedColumn--;
        }

        while (selectedColumn < targetColumn)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
            selectedColumn++;
        }

        if (!await context.Vision.HasLimeSelectionAsync(
                cells[targetRow * 4 + targetColumn],
                cancellationToken))
        {
            throw new CalibrationRequiredException(
                "O Impreza 22B-STI foi localizado, mas a borda verde não confirmou sua seleção.");
        }
    }

    private async Task SelectSubaruManufacturerAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var game = context.GameWindow.GetRequiredGameWindow();
        var width = game.ClientBounds.Width;
        var height = game.ClientBounds.Height;
        var target = document.Lines.FirstOrDefault(line =>
            GameVisionService.Normalize(line.Text).Contains("SUBARU", StringComparison.Ordinal));
        if (target is null)
        {
            throw new CalibrationRequiredException(
                "Não foi possível localizar Subaru pelo texto na grade de fabricantes.");
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

        var targetRow = rowCenters
            .Select((y, index) => new { Distance = Math.Abs(y - target.Center.Y), Index = index })
            .OrderBy(item => item.Distance)
            .First().Index;
        var targetColumn = Math.Clamp(
            (int)Math.Round((target.Center.X / (double)width - 0.20) / 0.20),
            0,
            3);

        var firstColumnCenter = target.Center.X - targetColumn * width * 0.20;
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
                "Não foi possível localizar o contorno verde atual na grade antes de selecionar Subaru.");
        }

        var selectedRow = selectedCell / 4;
        var selectedColumn = selectedCell % 4;
        context.Logger.State(
            Workflow,
            "FiltrarSubaru",
            $"Foco atual na linha {selectedRow + 1}, coluna {selectedColumn + 1}; " +
            $"Subaru detectado na linha {targetRow + 1}, coluna {targetColumn + 1}. Movendo somente a diferença exata.");

        while (selectedRow > targetRow)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
            selectedRow--;
        }

        while (selectedRow < targetRow)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken);
            selectedRow++;
        }

        while (selectedColumn > targetColumn)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken);
            selectedColumn--;
        }

        while (selectedColumn < targetColumn)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken);
            selectedColumn++;
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        _ = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "FiltrarSubaruConfirmado",
            ["22B-STI", "22B STI", "IMPREZA 22B"],
            cancellationToken);
    }

    public async Task DriveAsync(CancellationToken cancellationToken)
    {
        await ReturnToGarageMenuAsync(cancellationToken);
        // "Dirigir" também aparece no rodapé como atalho "Tab Dirigir".
        // Clicar no primeiro resultado do OCR podia acertar esse rodapé e não
        // sair da garagem. Abra a aba Campanha e normalize a lista no topo;
        // nela, Dirigir é sempre a primeira opção.
        await OpenGarageTabAsync(
            "AbaCampanhaParaDirigir",
            ["DIÁRIO DE COLEÇÃO", "DIARIO DE COLECAO"],
            cancellationToken);
        context.Logger.State(Workflow, "Dirigir", "Selecionando o primeiro item da aba Campanha por teclado.");
        for (var step = 0; step < 8; step++)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken);
        }
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        context.Logger.State(Workflow, "AguardarRua", "Aguardando a saída da garagem.");
        // O HUD da rua aparece antes de o jogo voltar a aceitar Esc. Com oito
        // segundos o comando ainda era descartado em algumas saídas da garagem.
        await Task.Delay(15_000, cancellationToken);

        // Ao sair da Casa em Tóquio, o painel contextual "Entrar na Casa"
        // pode continuar capturando Esc indefinidamente enquanto o carro fica
        // parado na entrada. Afaste o carro por um instante e só então deixe o
        // workflow abrir o menu de pausa. O finally garante W solto inclusive
        // quando F8 cancela durante esse movimento.
        await LeaveHouseEntranceIfNeededAsync(cancellationToken);
    }

    private async Task LeaveHouseEntranceIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!await context.Vision.ContainsAnyTextAsync(
                ["ENTRAR NA CASA", "CASA EM TÓQUIO", "CASA EM TOQUIO"],
                cancellationToken))
        {
            return;
        }

        context.Logger.State(
            Workflow,
            "SairDaEntradaDaCasa",
            "Painel da casa ainda ativo; avançando brevemente antes de abrir o menu da rua.");
        try
        {
            await context.Input.KeyDownAsync(GameKey.W, cancellationToken);
            await Task.Delay(1_800, cancellationToken);
        }
        finally
        {
            await context.Input.KeyUpAsync(GameKey.W, CancellationToken.None);
        }

        await Task.Delay(2_500, cancellationToken);
    }

    public async Task ReturnToGarageMenuAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (await context.Vision.ContainsAnyTextAsync(
                    ["ALTERAÇÕES NÃO SALVAS", "ALTERACOES NAO SALVAS", "SALVAR E CONTINUAR"],
                    cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    "SalvarAlteracoesPendentes",
                    "Diálogo de alterações não salvas detectado; confirmando 'Salvar e Continuar' com A.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                await Task.Delay(1_500, cancellationToken);
                continue;
            }

            if (await context.Vision.ContainsAnyTextAsync(
                    ["QUER MESMO SAIR DO MODO FOTO"],
                    cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    "SairModoFoto",
                    "Diálogo de saída do Modo Foto detectado; confirmando Sim com A.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                await Task.Delay(1_500, cancellationToken);
                continue;
            }

            if (await IsGarageMainMenuAsync(cancellationToken))
            {
                return;
            }

            context.Logger.State(
                Workflow,
                "RetornarMenuGaragem",
                $"Submenu aberto; voltando com B ({attempt + 1}/{maximumAttempts}).");
            await context.Input.TapAsync(GameKey.Escape, cancellationToken);
            await Task.Delay(700, cancellationToken);
            // Depois de uma apresentação longa a garagem pode reabrir com a
            // lista invisível por inatividade. Um direcional a desperta sem
            // abrir nenhuma opção; os chamadores normalizam o foco em seguida.
            await context.Input.TapAsync(GameKey.Up, cancellationToken, 40);
            await Task.Delay(450, cancellationToken);
        }

        var lastScreen = await context.Vision.ReadScreenAsync(CancellationToken.None);
        using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
        var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, "RetornarMenuGaragem");
        var observed = lastScreen.Text.ReplaceLineEndings(" ").Trim();
        if (observed.Length > 260)
        {
            observed = observed[..260] + "…";
        }
        throw new CalibrationRequiredException(
            $"Não foi possível retornar ao menu inicial da garagem. " +
            $"OCR final: '{observed}'. Diagnóstico: {diagnostic}");
    }

    public Task OpenBuySellTabAsync(CancellationToken cancellationToken) =>
        OpenGarageTabAsync(
            "AbaComprarEVender",
            ["CONCESSIONÁRIA", "CONCESSIONARIA", "CASA DE LEILÕES", "CASA DE LEILOES"],
            cancellationToken);

    public Task OpenCarsTabAsync(CancellationToken cancellationToken) =>
        OpenGarageTabAsync(
            "AbaCarros",
            ["MEUS CARROS", "APRIMORAR E TUNAR"],
            cancellationToken);

    public async Task<int> ReadCreditsAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadRegionAsync(
            new RectangleF(0.85f, 0.015f, 0.145f, 0.10f),
            cancellationToken);
        var candidates = ExtractFuzzyCreditNumbers(document.Text)
            .Where(value => value <= 999_999_999)
            .ToArray();
        context.Logger.State(
            Workflow,
            "LerCreditos",
            $"OCR do bloco de créditos: '{document.Text.ReplaceLineEndings(" ").Trim()}'; " +
            $"candidatos corrigidos: [{string.Join(", ", candidates)}].");

        var credits = candidates.DefaultIfEmpty(-1).Max();
        if (credits < 0)
        {
            credits = await context.Vision.ReadLargestNumberAsync(
                new RectangleF(0.80f, 0.00f, 0.20f, 0.14f),
                999_999_999,
                Workflow,
                "LerCreditosFallback",
                cancellationToken);
        }
        context.Logger.State(Workflow, "LerCreditos", $"Créditos detectados: {credits:N0} CR.");
        context.Resources.SetCredits(credits, estimated: false);
        return credits;
    }

    private static IReadOnlyList<int> ExtractFuzzyCreditNumbers(string text)
    {
        return Regex.Matches(text.ToUpperInvariant(), @"[0-9A-Z][0-9A-Z.,]*")
            .Select(match => match.Value)
            .Where(token => token.Count(char.IsDigit) >= 3)
            .Select(token =>
            {
                var digits = new string(token
                    .Select(character => character switch
                    {
                        >= '0' and <= '9' => character,
                        'A' => '4',
                        'B' => '8',
                        'O' or 'Q' or 'D' => '0',
                        'I' or 'L' => '1',
                        'Z' => '2',
                        'S' => '5',
                        'G' => '6',
                        _ => '\0'
                    })
                    .Where(character => character != '\0')
                    .ToArray());
                return int.TryParse(digits, out var value) ? value : -1;
            })
            .Where(value => value >= 0)
            .ToArray();
    }

    private async Task OpenGarageTabAsync(
        string state,
        IReadOnlyCollection<string> uniqueTexts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= 6; attempt++)
        {
            if (await context.Vision.ContainsAnyTextAsync(uniqueTexts, cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    state,
                    $"Aba confirmada por [{string.Join(" | ", uniqueTexts)}].");
                return;
            }

            context.Logger.State(Workflow, state, $"Avançando uma aba com LB ({attempt + 1}/6).");
            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 60);
            await Task.Delay(500, cancellationToken);
        }

        throw new CalibrationRequiredException(
            $"Não foi possível abrir a aba confirmada por [{string.Join(" | ", uniqueTexts)}].");
    }

    private async Task<bool> IsGarageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            var normalized = GameVisionService.Normalize(document.Text);
            return IsGarageText(normalized);
        }
        catch (AutomationFaultException)
        {
            return false;
        }
    }

    private async Task<bool> IsGarageMainMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            return IsGarageMainMenuText(GameVisionService.Normalize(document.Text));
        }
        catch (AutomationFaultException)
        {
            return false;
        }
    }

    private static bool IsGarageText(string normalized)
    {
        // "Dirigir" é exclusivo do menu inicial da garagem. Exigir que o OCR
        // leia simultaneamente todas as abas tornava a detecção frágil.
        if (IsGarageMainMenuText(normalized))
        {
            return true;
        }

        // Os workflows podem chamar EnsureGarage enquanto ainda estão em um
        // submenu interno da casa. Esses textos não existem no menu da rua e
        // são mais seguros do que inferir o estado apenas pela ausência das
        // abas de pausa. Sem esta lista, a Maestria era confundida com a rua e
        // um Esc desviava a automação para uma categoria diferente.
        if (HasAny(
                normalized,
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "TUNAGEM PERSONALIZADA",
                "APRIMORAR E TUNAR",
                "MEUS CARROS",
                "COMPRAR CARRO",
                "IR PARA FABRICANTE",
                "FABRICANTE"))
        {
            return true;
        }

        return false;
    }

    private static bool IsGarageMainMenuText(string normalized)
    {
        // As abas CAMPANHA / COMPRAR E VENDER / CARROS permanecem visíveis
        // dentro de vários submenus. Um único item também não basta sem
        // rejeitar estados internos: Comprar Carro contém "Concessionária"
        // no rodapé. Rejeite primeiro as telas internas conhecidas e só então
        // aceite um item da lista principal; isso tolera uma linha omitida
        // pelo OCR sem voltar ao falso positivo original.
        if (!HasAny(normalized, "CAMPANHA", "COMPRAR E VENDER", "CARROS"))
        {
            return false;
        }

        if (HasAny(
                normalized,
                "COMPRAR CARRO",
                "IR PARA FABRICANTE",
                "PONTOS DISPONIVEIS",
                "MAESTRIA DE CARRO",
                "APRIMORAMENTO PERSONALIZADO",
                "TUNAGEM PERSONALIZADA",
                "FABRICANTE"))
        {
            return false;
        }

        return HasAny(
            normalized,
            "DIARIO DE COLECAO",
            "LISTA DE DETALHES DO FESTIVAL",
            "CONFIGURACOES",
            "CONCESSIONARIA",
            "CASA DE LEILOES",
            "MEUS CARROS",
            "APRIMORAR E TUNAR",
            "DESIGNS E TINTAS");
    }

    private static bool HasAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
