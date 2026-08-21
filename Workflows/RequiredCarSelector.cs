using System.Drawing;
using System.Text.RegularExpressions;
using FH6OpenAssist.Core;
using FH6OpenAssist.Vision;
using FH6OpenAssist.Windows;

namespace FH6OpenAssist.Workflows;

public enum PerformanceIndexEvidence
{
    Required,
    Wrong,
    Missing,
    Conflicting
}

public sealed record RequiredCarDefinition(
    string DisplayName,
    string Manufacturer,
    bool RequiresS1Pi800)
{
    public static RequiredCarDefinition SkillPoints { get; } = new(
        "Subaru Impreza 22B-STI Version",
        "SUBARU",
        RequiresS1Pi800: false);

    public static RequiredCarDefinition CrFarm { get; } = new(
        "Nissan S-Cargo S1 800",
        "NISSAN",
        RequiresS1Pi800: true);

    public bool MatchesHeaderModel(string text)
    {
        var normalized = GameVisionService.Normalize(text);
        var compact = Compact(normalized);
        if (Manufacturer == "SUBARU")
        {
            var hasManufacturer = normalized.Contains(Manufacturer, StringComparison.Ordinal);
            var hasTruncatedManufacturer = compact.Contains("BARUIMPREZA22B", StringComparison.Ordinal);
            var hasImpreza = normalized.Contains("IMPREZA", StringComparison.Ordinal);
            var has22B = normalized.Contains("22B", StringComparison.Ordinal);
            var hasVariant = normalized.Contains("STI", StringComparison.Ordinal) ||
                             normalized.Contains("VERSION", StringComparison.Ordinal) ||
                             Regex.IsMatch(normalized, @"\bST[IL1]\b", RegexOptions.CultureInvariant);

            return has22B &&
                   ((hasManufacturer && (hasImpreza || hasVariant)) ||
                    (hasTruncatedManufacturer && hasVariant));
        }

        if (!normalized.Contains(Manufacturer, StringComparison.Ordinal))
        {
            return false;
        }

        return compact.Contains("SCARGO", StringComparison.Ordinal) ||
               compact.Contains("CARGOFORZAEDITION", StringComparison.Ordinal) ||
               compact.Contains("SCAFORZAEDITION", StringComparison.Ordinal);
    }

    public bool MatchesCardTitle(string text)
    {
        var normalized = GameVisionService.Normalize(text);
        if (Manufacturer == "SUBARU")
        {
            var exactTitle = normalized.Contains("22B", StringComparison.Ordinal) &&
                             (normalized.Contains("IMPREZA", StringComparison.Ordinal) ||
                              normalized.Contains("STI", StringComparison.Ordinal) ||
                              normalized.Contains("VERSION", StringComparison.Ordinal) ||
                              Regex.IsMatch(normalized, @"\bST[IL1]\b", RegexOptions.CultureInvariant));
            var subaruCompact = Compact(normalized);
            var marqueeTitle = normalized.Contains("SUBARU", StringComparison.Ordinal) &&
                               normalized.Contains("1998", StringComparison.Ordinal) &&
                               subaruCompact.Contains("IMPR", StringComparison.Ordinal) &&
                               Regex.IsMatch(subaruCompact, @"ST[IL1]VERSION", RegexOptions.CultureInvariant);
            return exactTitle || marqueeTitle;
        }

        var compact = Compact(normalized);
        return compact.Contains("SCARGO", StringComparison.Ordinal) ||
               (compact.Contains("CARGO", StringComparison.Ordinal) &&
                (compact.Contains("FE", StringComparison.Ordinal) ||
                 compact.Contains("FORZA", StringComparison.Ordinal) ||
                 compact.Contains("EDITION", StringComparison.Ordinal)));
    }

    public bool MatchesRequiredClass(string text)
    {
        if (!RequiresS1Pi800)
        {
            return true;
        }

        return ClassifyPerformanceIndex(text) == PerformanceIndexEvidence.Required;
    }

    public PerformanceIndexEvidence ClassifyPerformanceIndex(string text)
    {
        if (!RequiresS1Pi800)
        {
            return PerformanceIndexEvidence.Required;
        }

        var compact = Compact(GameVisionService.Normalize(text));
        var matches = Regex.Matches(
                compact,
                @"S(?:LI|1|2|I|L)?\d{3}|[RIABCD]\d{3}",
                RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasRequired = matches.Any(value =>
            Regex.IsMatch(value, @"^S(?:LI|1|I|L)?800$", RegexOptions.CultureInvariant));
        var hasWrong = matches.Any(value =>
            !Regex.IsMatch(value, @"^S(?:LI|1|I|L)?800$", RegexOptions.CultureInvariant));
        return (hasRequired, hasWrong) switch
        {
            (true, true) => PerformanceIndexEvidence.Conflicting,
            (true, false) => PerformanceIndexEvidence.Required,
            (false, true) => PerformanceIndexEvidence.Wrong,
            _ => PerformanceIndexEvidence.Missing
        };
    }

    private static string Compact(string value) =>
        Regex.Replace(value, @"[^A-Z0-9]", string.Empty, RegexOptions.CultureInvariant);
}

public sealed class RequiredCarSelector(AutomationContext context)
{
    private const string Workflow = "Seleção de carro";
    private const int MaximumManufacturerSteps = 48;
    private const int MaximumCarColumns = 64;
    // O cabeçalho do carro fica no lado esquerdo. Limitar a ROI evita que o
    // saldo de CR no canto direito (por exemplo, "CR 31.560.033") produza um
    // falso PI como "R315" e conflite com o S1 800 real da Nissan.
    private static readonly RectangleF HeaderRegion = new(0.03f, 0.01f, 0.50f, 0.18f);
    private static readonly RectangleF CampaignTabRegion = new(0.24f, 0.185f, 0.09f, 0.04f);
    private static readonly RectangleF CarsTabRegion = new(0.33f, 0.185f, 0.075f, 0.04f);
    private static readonly RectangleF CampaignPanelRegion = new(0.24f, 0.22f, 0.50f, 0.58f);
    private static readonly RectangleF DetailsClassRegion = new(0.05f, 0.74f, 0.12f, 0.10f);
    private static readonly RectangleF ChangeCarTileRegion = new(0.265f, 0.255f, 0.47f, 0.30f);
    private static readonly RectangleF CarDeliveryStatusRegion = new(0.78f, 0.83f, 0.21f, 0.14f);
    private static readonly RectangleF[] VisibleManufacturerHeaders = CreateVisibleManufacturerHeaders();
    private static readonly RectangleF[] VisibleCarCells = CreateVisibleCarCells();

    public async Task EnsureSelectedAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredCar);

        await EnsureStreetMenuOpenAsync(cancellationToken);
        await NormalizeCampaignTabAsync(cancellationToken);
        var currentCar = await ReadCurrentCarAsync(requiredCar, cancellationToken);
        if (currentCar.Kind == CurrentCarKind.Correct)
        {
            context.Logger.State(
                Workflow,
                "CarroConfirmado",
                $"{requiredCar.DisplayName} confirmado no cabeçalho do menu da rua; nenhuma troca necessária.");
            return;
        }

        if (currentCar.Kind == CurrentCarKind.Inconclusive)
        {
            throw await CreateFailureAsync(
                "LerCarroAtual",
                $"Não foi possível identificar com segurança o carro atual no menu da rua. " +
                $"O BOT não assumirá que o veículo está incorreto. OCR: '{Shorten(currentCar.ObservedText)}'.");
        }

        context.Logger.State(
            Workflow,
            "TrocarCarro",
            $"O cabeçalho confirmou outro veículo; procurando {requiredCar.DisplayName} em Carros > Trocar de Carro.");
        context.Telemetry.UpdateStage(
            "Selecionando carro requisito",
            $"Localizando {requiredCar.DisplayName} na coleção do jogador.");

        await OpenChangeCarGridAsync(cancellationToken);
        await SelectManufacturerAsync(requiredCar, cancellationToken);
        var focusedCarCellIndex = await FocusRequiredCarAsync(requiredCar, cancellationToken);
        await EnterFocusedCarAsync(requiredCar, focusedCarCellIndex, cancellationToken);
        await WaitForStreetMenuAfterChangeAsync(cancellationToken);
        await NormalizeCampaignTabAsync(cancellationToken);

        var confirmation = await ReadCurrentCarAsync(requiredCar, cancellationToken);
        if (confirmation.Kind != CurrentCarKind.Correct)
        {
            throw await CreateFailureAsync(
                "RevalidarCarro",
                $"A troca terminou, mas o cabeçalho não confirmou {requiredCar.DisplayName}. " +
                $"Nenhum farm será iniciado. OCR: '{Shorten(confirmation.ObservedText)}'.");
        }

        context.Logger.State(
            Workflow,
            "CarroTrocado",
            $"{requiredCar.DisplayName} selecionado e revalidado no menu da rua.");
    }

    private async Task EnsureStreetMenuOpenAsync(CancellationToken cancellationToken)
    {
        var unknownProbeUsed = false;
        var houseMovementRecoveryUsed = false;
        var persistentHouseProbeUsed = false;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var state = await context.GameContext.DetectAsync(cancellationToken);
            if (state.Kind == GameContextKind.StreetMenu)
            {
                return;
            }

            if (state.Kind == GameContextKind.WorldMap)
            {
                context.Logger.State(Workflow, "FecharMapa", "Mapa-múndi aberto; fechando antes de validar o carro.");
                await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
                await Task.Delay(900, cancellationToken);
                continue;
            }

            if (state.Kind is GameContextKind.Event or
                GameContextKind.EventMenu or
                GameContextKind.EventPreRaceMenu or
                GameContextKind.Garage or
                GameContextKind.ControllerDisconnected or
                GameContextKind.CarPositionResetConfirmation or
                GameContextKind.EventExitConfirmation)
            {
                throw await CreateFailureAsync(
                    "PrepararMenuRua",
                    $"A seleção automática de carro exige passeio livre; o contexto detectado foi {state.Kind}. " +
                    "Nenhuma entrada de navegação foi enviada nesse contexto.");
            }

            if (await IsStreetMenuTextAsync(cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    "MenuRuaPorTexto",
                    "Estrutura completa do menu da rua confirmada antes de enviar outra sonda.");
                return;
            }

            var houseEntranceOpen = await IsHouseEntranceOpenAsync(cancellationToken);
            if (houseEntranceOpen && !houseMovementRecoveryUsed)
            {
                context.Logger.State(
                    Workflow,
                    "SairDaEntradaDaCasa",
                    "A entrada da casa interceptou Esc; avançando brevemente antes de abrir o menu.");
                await context.Input.HoldAsync(GameKey.W, 1_800, cancellationToken);
                await Task.Delay(1_500, cancellationToken);
                if (await IsHouseEntranceOpenAsync(cancellationToken))
                {
                    context.Logger.State(
                        Workflow,
                        "RecuarDaEntradaDaCasa",
                        "O painel da casa permaneceu após avançar; recuando de forma limitada para sair do gatilho.");
                    await context.Input.HoldAsync(GameKey.S, 3_000, cancellationToken);
                    await Task.Delay(2_500, cancellationToken);
                }

                // O afastamento altera de forma observável o estado da rua.
                // Autorize uma nova e única sonda no novo checkpoint, sem herdar
                // a sonda que apenas revelou o painel da casa.
                unknownProbeUsed = false;
                houseMovementRecoveryUsed = true;
                houseEntranceOpen = await IsHouseEntranceOpenAsync(cancellationToken);
            }

            if (houseEntranceOpen)
            {
                if (persistentHouseProbeUsed)
                {
                    throw await CreateFailureAsync(
                        "PrepararMenuRua",
                        "O painel da casa permaneceu após o afastamento e uma nova sonda Start; " +
                        "nenhuma entrada adicional será enviada nesse ponto.");
                }

                persistentHouseProbeUsed = true;
                context.Logger.State(
                    Workflow,
                    "ReabrirMenuAposCasa",
                    "O painel da casa permaneceu, mas o passeio livre foi confirmado; " +
                    "usando uma única sonda Start adicional e validando o menu antes de continuar.");
            }

            if (state.Kind is not (GameContextKind.Street or GameContextKind.Unknown))
            {
                throw await CreateFailureAsync(
                    "PrepararMenuRua",
                    $"O contexto {state.Kind} não autoriza uma sonda por Esc/Start. " +
                    "Abra o passeio livre e feche avisos antes de iniciar o BOT.");
            }

            if (state.Kind == GameContextKind.Unknown)
            {
                if (unknownProbeUsed)
                {
                    throw await CreateFailureAsync(
                        "PrepararMenuRua",
                        "A sonda única em um contexto inconclusivo não revelou o menu da rua; nenhuma nova entrada será enviada.");
                }

                unknownProbeUsed = true;
                context.Logger.State(
                    Workflow,
                    "SondaRuaInconclusiva",
                    "A rua sem marcadores ficou inconclusiva; usando uma única sonda reversível por Esc/Start e validando o resultado.");
            }

            context.Logger.State(
                Workflow,
                "AbrirMenuRua",
                $"Sonda segura por Esc/Start para confirmar o menu da rua ({attempt}/4).");
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
            await Task.Delay(1_000, cancellationToken);

            var postProbeState = await context.GameContext.DetectAsync(cancellationToken);
            if (postProbeState.Kind == GameContextKind.StreetMenu ||
                await IsStreetMenuTextAsync(cancellationToken))
            {
                context.Logger.State(
                    Workflow,
                    "MenuRuaAposSonda",
                    $"Menu da rua confirmado após a sonda {attempt}/4.");
                return;
            }
        }

        throw await CreateFailureAsync(
            "PrepararMenuRua",
            "Não foi possível confirmar o menu da rua após quatro sondas limitadas.");
    }

    private async Task<CurrentCarObservation> ReadCurrentCarAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        var observed = new List<string>();
        var confirmations = 0;
        var mismatches = 0;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var header = await context.Vision.ReadScaledRegionAsync(
                HeaderRegion,
                requestedScale: 3,
                cancellationToken);
            var text = header.Text;
            observed.Add(text);

            if (requiredCar.MatchesHeaderModel(text))
            {
                var performanceIndex = requiredCar.ClassifyPerformanceIndex(text);
                if (performanceIndex == PerformanceIndexEvidence.Required)
                {
                    confirmations++;
                }
                else if (performanceIndex == PerformanceIndexEvidence.Wrong)
                {
                    mismatches++;
                }
            }
            else if (LooksLikeCarHeader(text))
            {
                mismatches++;
            }

            if (attempt < 3)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        var kind = confirmations >= 2 && mismatches == 0
            ? CurrentCarKind.Correct
            : mismatches >= 2 && confirmations == 0
                ? CurrentCarKind.Different
                : CurrentCarKind.Inconclusive;
        return new CurrentCarObservation(kind, string.Join(" | ", observed));
    }

    private async Task OpenChangeCarGridAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var carsTabReady = await context.Vision.AnalyzeScreenAsync(
                (bitmap, _) =>
                    DarkFillRatio(bitmap, CarsTabRegion) >= 0.45 &&
                    LimeVerticalBorderRatio(bitmap, ChangeCarTileRegion) >= 0.018,
                cancellationToken);
            if (carsTabReady)
            {
                context.Logger.State(
                    Workflow,
                    "AbrirTrocarDeCarro",
                    "Aba Carros e bloco Trocar de Carro confirmados pela guia ativa e pelo contorno verde; abrindo com Enter.");
                await context.Input.TapAsync(GameKey.Enter, cancellationToken);
                if (await WaitForCarGridAsync(cancellationToken, TimeSpan.FromSeconds(10)) is not null)
                {
                    return;
                }

                throw await CreateFailureAsync(
                    "TrocarDeCarroConfirmado",
                    "Enter foi enviado no bloco validado, mas a grade Meus Carros não apresentou seus dois marcadores exclusivos.");
            }

            context.Logger.State(Workflow, "AbrirAbaCarros", $"Avançando uma aba com RB/PgDn ({attempt}/7).");
            await context.Input.TapAsync(GameKey.PageDown, cancellationToken, 60);
            await Task.Delay(550, cancellationToken);
        }

        throw await CreateFailureAsync(
            "AbrirAbaCarros",
            "Não foi possível abrir a aba Carros a partir do menu da rua.");
    }

    private async Task SelectManufacturerAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        await context.Input.TapAsync(GameKey.Backspace, cancellationToken);
        var snapshot = await WaitForManufacturerOverlayAsync(
            requiredCar,
            cancellationToken,
            TimeSpan.FromSeconds(8));
        if (snapshot is null)
        {
            throw await CreateFailureAsync(
                "ListaDeFabricantes",
                "Backspace não abriu a grade exclusiva de fabricantes; o BOT não moverá o foco na tela anterior.");
        }

        if (snapshot.Target is not null)
        {
            await SelectVisibleManufacturerAsync(requiredCar, snapshot, cancellationToken);
            return;
        }

        if (snapshot.Focused is null)
        {
            throw await CreateFailureAsync(
                "ListaDeFabricantes",
                "A grade de fabricantes abriu, mas nenhum contorno verde confiável foi localizado.");
        }

        if (!IsCurrentCarManufacturerCell(snapshot.Focused.Text))
        {
            context.Logger.State(
                Workflow,
                "NormalizarFabricantes",
                "A marca não está visível; voltando ao início da lista, com nova leitura após cada movimento.");
            for (var step = 0; step < 24; step++)
            {
                await context.Input.TapAsync(GameKey.Up, cancellationToken, 45);
                await Task.Delay(120, cancellationToken);
                snapshot = await CaptureManufacturerOverlayAsync(requiredCar, cancellationToken);
                if (!snapshot.IsOverlay)
                {
                    throw await CreateFailureAsync(
                        "NormalizarFabricantes",
                        "A grade de fabricantes deixou de ser reconhecida durante a normalização.");
                }

                if (snapshot.Target is not null)
                {
                    await SelectVisibleManufacturerAsync(requiredCar, snapshot, cancellationToken);
                    return;
                }

                if (snapshot.Focused is not null && IsCurrentCarManufacturerCell(snapshot.Focused.Text))
                {
                    break;
                }
            }
        }

        context.Logger.State(
            Workflow,
            "ProcurarFabricante",
            $"{requiredCar.Manufacturer} não apareceu na primeira visão; descendo uma linha por vez e relendo o OCR.");
        for (var step = 1; step <= MaximumManufacturerSteps; step++)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken, 45);
            await Task.Delay(160, cancellationToken);
            snapshot = await CaptureManufacturerOverlayAsync(requiredCar, cancellationToken);
            if (!snapshot.IsOverlay)
            {
                throw await CreateFailureAsync(
                    "ProcurarFabricante",
                    "A grade de fabricantes deixou de ser reconhecida durante a busca.");
            }

            if (snapshot.Target is not null)
            {
                await SelectVisibleManufacturerAsync(requiredCar, snapshot, cancellationToken);
                return;
            }
        }

        throw await CreateFailureAsync(
            "ProcurarFabricante",
            $"A marca {requiredCar.Manufacturer} não foi localizada na varredura completa e limitada. " +
            "O BOT foi travado: confirme que há um carro dessa marca na sua garagem.");
    }

    private async Task<OcrDocument?> WaitForCarGridAsync(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await context.Vision.ReadScreenAsync(cancellationToken);
            if (IsCarGridDocument(document))
            {
                context.Logger.State(
                    Workflow,
                    "TrocarDeCarroConfirmado",
                    "Grade confirmada no mesmo frame por Meus Carros e Ir para Fabricante.");
                return document;
            }

            await Task.Delay(220, cancellationToken);
        }

        return null;
    }

    private static bool IsCarGridDocument(OcrDocument document)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        return normalized.Contains("IR PARA FABRICANTE", StringComparison.Ordinal) &&
               normalized.Contains("MEUS CARROS", StringComparison.Ordinal);
    }

    private async Task<ManufacturerOverlaySnapshot?> WaitForManufacturerOverlayAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await CaptureManufacturerOverlayAsync(requiredCar, cancellationToken);
            if (snapshot.IsOverlay)
            {
                context.Logger.State(
                    Workflow,
                    "ListaDeFabricantes",
                    $"Grade de fabricantes confirmada por título exato e {snapshot.CellCount} células OCR.");
                return snapshot;
            }

            await Task.Delay(220, cancellationToken);
        }

        return null;
    }

    private async Task<ManufacturerOverlaySnapshot> CaptureManufacturerOverlayAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken) =>
        await context.Vision.AnalyzeScreenAsync(
            (bitmap, document) => AnalyzeManufacturerOverlay(bitmap, document, requiredCar),
            cancellationToken);

    private static ManufacturerOverlaySnapshot AnalyzeManufacturerOverlay(
        Bitmap bitmap,
        OcrDocument document,
        RequiredCarDefinition requiredCar)
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
            .ToArray();
        var selectedIndex = measured
            .Select((cell, index) => new { Index = index, cell.FocusRatio })
            .OrderByDescending(item => item.FocusRatio)
            .FirstOrDefault();
        if (selectedIndex is not null && selectedIndex.FocusRatio >= 0.025)
        {
            measured[selectedIndex.Index] = measured[selectedIndex.Index] with { Selected = true };
        }

        var rowSpacing = MedianSpacing(ClusterCenters(
            measured.Select(cell => (double)cell.Center.Y),
            bitmap.Height * 0.018));
        var columnSpacing = MedianSpacing(ClusterCenters(
            measured.Select(cell => (double)cell.Center.X),
            bitmap.Width * 0.060));

        return new ManufacturerOverlaySnapshot(
            IsOverlay: hasExactTitle && measured.Length >= 8 && rowSpacing > 0 && columnSpacing > 0,
            Target: measured.FirstOrDefault(cell =>
                IsManufacturerLabel(cell.Text, requiredCar.Manufacturer)),
            Focused: measured.FirstOrDefault(cell => cell.Selected),
            measured.Length,
            rowSpacing,
            columnSpacing);
    }

    private static IReadOnlyList<double> ClusterCenters(IEnumerable<double> values, double tolerance)
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

    private static double MedianSpacing(IReadOnlyList<double> centers)
    {
        var spacings = centers
            .Zip(centers.Skip(1), (left, right) => right - left)
            .Where(spacing => spacing > 1)
            .Order()
            .ToArray();
        return spacings.Length == 0 ? 0 : spacings[spacings.Length / 2];
    }

    private static RectangleF ManufacturerCellRegion(Bitmap bitmap, OcrLine line)
    {
        const float width = 0.188f;
        const float height = 0.047f;
        var x = (float)(line.Center.X / (double)bitmap.Width) - width / 2;
        var y = (float)(line.Center.Y / (double)bitmap.Height) - height / 2;
        return new RectangleF(
            Math.Clamp(x, 0, 1 - width),
            Math.Clamp(y, 0, 1 - height),
            width,
            height);
    }

    private async Task SelectVisibleManufacturerAsync(
        RequiredCarDefinition requiredCar,
        ManufacturerOverlaySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Target is null || snapshot.Focused is null)
        {
            throw await CreateFailureAsync(
                "SelecionarFabricante",
                $"{requiredCar.Manufacturer} apareceu, mas o foco atual da grade não foi confirmado.");
        }

        if (snapshot.RowSpacing <= 0 || snapshot.ColumnSpacing <= 0)
        {
            throw await CreateFailureAsync(
                "SelecionarFabricante",
                "O espaçamento de linhas e colunas da grade de fabricantes ficou inconclusivo.");
        }

        var verticalMoves = (int)Math.Round(
            (snapshot.Target.Center.Y - snapshot.Focused.Center.Y) /
            snapshot.RowSpacing);
        var horizontalMoves = (int)Math.Round(
            (snapshot.Target.Center.X - snapshot.Focused.Center.X) /
            snapshot.ColumnSpacing);
        if (Math.Abs(verticalMoves) > 20 || Math.Abs(horizontalMoves) > 3)
        {
            throw await CreateFailureAsync(
                "SelecionarFabricante",
                $"A geometria OCR de {requiredCar.Manufacturer} ficou fora da grade de fabricantes esperada.");
        }

        context.Logger.State(
            Workflow,
            "SelecionarFabricante",
            $"{requiredCar.Manufacturer} localizado; movendo {Math.Abs(verticalMoves)} linha(s) e " +
            $"{Math.Abs(horizontalMoves)} coluna(s) somente com o controle.");
        await TapRepeatedAsync(
            verticalMoves < 0 ? GameKey.Up : GameKey.Down,
            Math.Abs(verticalMoves),
            cancellationToken);
        await TapRepeatedAsync(
            horizontalMoves < 0 ? GameKey.Left : GameKey.Right,
            Math.Abs(horizontalMoves),
            cancellationToken);
        await Task.Delay(350, cancellationToken);

        var expectedTargetRegion = snapshot.Target.Region;
        var focusConfirmations = 0;
        var observedFocusRatios = new List<double>(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var confirmation = await context.Vision.AnalyzeScreenAsync(
                (bitmap, document) =>
                {
                    var overlay = AnalyzeManufacturerOverlay(bitmap, document, requiredCar);
                    var expectedPixels = ToPixels(bitmap, expectedTargetRegion);
                    var conflictingLabel = document.Lines.Any(line =>
                        expectedPixels.Contains(
                            line.Center.X,
                            line.Center.Y) &&
                        LooksLikeManufacturerHeader(line.Text) &&
                        !IsManufacturerLabel(line.Text, requiredCar.Manufacturer));
                    return (
                        overlay.IsOverlay,
                        FocusRatio: LimeVerticalBorderRatioWithHorizontalTolerance(
                            bitmap,
                            expectedTargetRegion,
                            horizontalToleranceRatio: 0.040),
                        ConflictingLabel: conflictingLabel);
                },
                cancellationToken);
            observedFocusRatios.Add(confirmation.FocusRatio);
            if (confirmation.IsOverlay &&
                !confirmation.ConflictingLabel &&
                confirmation.FocusRatio >= 0.10)
            {
                focusConfirmations++;
            }

            if (attempt < 2)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        context.Logger.State(
            Workflow,
            "ConfirmarFabricante",
            $"Foco em {requiredCar.Manufacturer}: {focusConfirmations}/3 confirmações na célula ancorada por OCR; " +
            $"bordas=[{string.Join(", ", observedFocusRatios.Select(value => value.ToString("P1")))}].");
        if (focusConfirmations < 2)
        {
            throw await CreateFailureAsync(
                "ConfirmarFabricante",
                $"Os direcionais foram enviados, mas o contorno verde não confirmou exatamente {requiredCar.Manufacturer}.");
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        if (await WaitForCarGridAsync(cancellationToken, TimeSpan.FromSeconds(10)) is null)
        {
            throw await CreateFailureAsync(
                "ConfirmarFabricante",
                $"{requiredCar.Manufacturer} foi confirmado no filtro, mas a grade Meus Carros não retornou.");
        }

        var grid = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
        if (!grid.IsCarGrid || grid.ManufacturerState != ManufacturerHeaderState.Target)
        {
            throw await CreateFailureAsync(
                "ConfirmarFabricante",
                $"A grade retornou, mas a barra superior não confirmou {requiredCar.Manufacturer}.");
        }
    }

    private async Task TapRepeatedAsync(
        GameKey key,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            await context.Input.TapAsync(key, cancellationToken, 45);
        }
    }

    private static bool IsCurrentCarManufacturerCell(string text) =>
        GameVisionService.Normalize(text) == "CARRO ATUAL";

    private static CarCandidate? GetSingleFocusedRequiredCandidate(CarGridSnapshot snapshot)
    {
        var selectedCandidates = snapshot.Candidates
            .Where(candidate =>
                candidate.Selected &&
                candidate.CellIndex == snapshot.FocusedCellIndex)
            .ToArray();
        return selectedCandidates.Length == 1 ? selectedCandidates[0] : null;
    }

    private static bool AreOrthogonallyAdjacentCells(int firstCellIndex, int secondCellIndex)
    {
        var firstRow = firstCellIndex / 4;
        var firstColumn = firstCellIndex % 4;
        var secondRow = secondCellIndex / 4;
        var secondColumn = secondCellIndex % 4;
        return Math.Abs(firstRow - secondRow) + Math.Abs(firstColumn - secondColumn) == 1;
    }

    private static GameKey DirectionBetweenAdjacentCells(int sourceCellIndex, int targetCellIndex)
    {
        var sourceRow = sourceCellIndex / 4;
        var sourceColumn = sourceCellIndex % 4;
        var targetRow = targetCellIndex / 4;
        var targetColumn = targetCellIndex % 4;
        return (targetRow - sourceRow, targetColumn - sourceColumn) switch
        {
            (-1, 0) => GameKey.Up,
            (1, 0) => GameKey.Down,
            (0, -1) => GameKey.Left,
            (0, 1) => GameKey.Right,
            _ => throw new InvalidOperationException("As células informadas não são vizinhas ortogonais.")
        };
    }

    private async Task<int> FocusRequiredCarAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        const int maximumSearchIterations = MaximumCarColumns * 4;
        const int maximumIterationsWithoutInput = 8;
        var stalledRightMoves = 0;
        var evaluatedCells = new HashSet<int>();
        var evaluateNextFocusedTarget = false;
        var rightMoves = 0;
        var searchIterations = 0;
        var iterationsWithoutInput = 0;
        CarGridSnapshot? pendingSnapshot = null;

        while (searchIterations < maximumSearchIterations)
        {
            searchIterations++;
            iterationsWithoutInput++;
            if (iterationsWithoutInput > maximumIterationsWithoutInput)
            {
                throw await CreateFailureAsync(
                    "ProcurarCarro",
                    $"A busca acumulou {maximumIterationsWithoutInput} ciclos de captura sem enviar ou confirmar novo input; " +
                    "o BOT foi interrompido para não reler indefinidamente o mesmo cartão.");
            }

            var snapshot = pendingSnapshot ??
                           await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
            pendingSnapshot = null;
            if (!snapshot.IsCarGrid)
            {
                throw await CreateFailureAsync(
                    "ProcurarCarro",
                    "A tela Trocar de Carro deixou de ser reconhecida durante a busca.");
            }

            if (snapshot.ManufacturerState == ManufacturerHeaderState.Other)
            {
                throw await CreateFailureAsync(
                    "CarroNaoEncontrado",
                    $"Duas leituras estáveis confirmaram que a varredura passou de {requiredCar.Manufacturer} " +
                    $"para '{Shorten(snapshot.SelectedHeaderText)}' sem encontrar {requiredCar.DisplayName}. " +
                    "O BOT foi travado: confirme que esse carro requisito está na sua garagem.");
            }

            if (snapshot.ManufacturerState != ManufacturerHeaderState.Target)
            {
                throw await CreateFailureAsync(
                    "ConfirmarFabricante",
                    $"A barra superior ficou inconclusiva durante a busca de {requiredCar.Manufacturer}; " +
                    "nenhuma mudança de marca foi presumida.");
            }

            var candidate = evaluateNextFocusedTarget
                ? snapshot.Candidates.FirstOrDefault(item => item.Selected)
                : null;
            candidate ??= snapshot.Candidates
                .Where(item => !evaluatedCells.Contains(item.CellIndex))
                .OrderBy(item => item.CellIndex)
                .FirstOrDefault();
            if (candidate is not null)
            {
                if (!candidate.Selected)
                {
                    candidate = await MoveToCarCandidateAsync(
                        requiredCar,
                        snapshot,
                        candidate,
                        cancellationToken);
                    iterationsWithoutInput = 0;
                }

                evaluateNextFocusedTarget = false;

                if (!requiredCar.RequiresS1Pi800)
                {
                    context.Logger.State(
                        Workflow,
                        "CarroLocalizado",
                        $"{requiredCar.DisplayName} localizado por OCR e confirmado pela borda verde.");
                    return candidate.CellIndex;
                }

                var details = await ReadDetailsPerformanceIndexAsync(requiredCar, cancellationToken);
                var performanceIndex = details.Evidence;
                if (performanceIndex == PerformanceIndexEvidence.Required)
                {
                    var focusConfirmations = 0;
                    for (var confirmationAttempt = 0; confirmationAttempt < 3; confirmationAttempt++)
                    {
                        var confirmation = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
                        var selectedTarget = GetSingleFocusedRequiredCandidate(confirmation);
                        if (confirmation.ManufacturerState == ManufacturerHeaderState.Target &&
                            selectedTarget?.CellIndex == candidate.CellIndex)
                        {
                            focusConfirmations++;
                        }

                        if (confirmationAttempt < 2)
                        {
                            await Task.Delay(180, cancellationToken);
                        }
                    }

                    if (focusConfirmations < 2)
                    {
                        throw await CreateFailureAsync(
                            "ConfirmarCartao",
                            "O painel leu S1 800, mas uma segunda captura não manteve o foco verde na mesma S-Cargo.");
                    }

                    context.Logger.State(
                        Workflow,
                        "CarroLocalizado",
                        $"Nissan S-Cargo confirmada pelo cartão e pelo painel de detalhes S1 800. OCR: '{Shorten(details.ObservedText)}'.");
                    return candidate.CellIndex;
                }

                if (performanceIndex != PerformanceIndexEvidence.Wrong)
                {
                    throw await CreateFailureAsync(
                        "LerClasseDoCarro",
                        $"A S-Cargo focada foi confirmada, mas o painel não forneceu um PI único e conclusivo. " +
                        $"Nenhum cartão será rejeitado por suposição. OCR: '{Shorten(details.ObservedText)}'.");
                }

                context.Logger.State(
                    Workflow,
                    "ConfiguracaoIncorreta",
                    $"Uma S-Cargo foi encontrada, mas não é S1 800. OCR do painel: '{Shorten(details.ObservedText)}'. Procurando outra cópia.");
                evaluatedCells.Add(candidate.CellIndex);
                continue;
            }

            if (rightMoves >= MaximumCarColumns)
            {
                break;
            }

            var confirmedBefore = await ConfirmSearchCheckpointAsync(
                requiredCar,
                snapshot,
                cancellationToken);
            if (confirmedBefore is null)
            {
                continue;
            }

            await context.Input.TapAsync(GameKey.Right, cancellationToken, 55);
            rightMoves++;
            iterationsWithoutInput = 0;
            evaluateNextFocusedTarget = true;
            await Task.Delay(350, cancellationToken);

            var firstAfter = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
            var confirmedAfter = await ConfirmSearchCheckpointAsync(
                requiredCar,
                firstAfter,
                cancellationToken);
            if (confirmedAfter is null)
            {
                throw await CreateFailureAsync(
                    "ConfirmarProgressoCarros",
                    $"O movimento Right {rightMoves}/{MaximumCarColumns} foi enviado, mas a grade não produziu " +
                    "duas capturas pós-movimento concordantes; nenhum novo input será enviado.");
            }

            if (confirmedAfter.ManufacturerState == ManufacturerHeaderState.Target)
            {
                var contentChanged = !string.Equals(
                    confirmedBefore.ContentFingerprint,
                    confirmedAfter.ContentFingerprint,
                    StringComparison.Ordinal);
                var focusChanged = confirmedBefore.FocusedCellIndex != confirmedAfter.FocusedCellIndex;

                // A troca de foco dentro da mesma visão também pode alterar o OCR. Só uma mudança
                // textual estável mantendo a célula focada caracteriza scroll para uma nova visão.
                if (contentChanged && !focusChanged)
                {
                    evaluatedCells.Clear();
                }

                if (!contentChanged && !focusChanged)
                {
                    stalledRightMoves++;
                    if (stalledRightMoves >= 4)
                    {
                        throw await CreateFailureAsync(
                            "ProcurarCarro",
                            "A grade não apresentou progresso em quatro movimentos Right confirmados; " +
                            "a busca foi interrompida.");
                    }
                }
                else
                {
                    stalledRightMoves = 0;
                }
            }
            else
            {
                stalledRightMoves = 0;
            }

            pendingSnapshot = confirmedAfter;
        }

        throw await CreateFailureAsync(
            "ProcurarCarro",
            $"A busca atingiu o limite de {rightMoves} movimentos Right e {searchIterations} ciclos " +
            $"sem concluir a seleção de {requiredCar.DisplayName}.");
    }

    private async Task<CarGridSnapshot?> ConfirmSearchCheckpointAsync(
        RequiredCarDefinition requiredCar,
        CarGridSnapshot initial,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(160, cancellationToken);
            var current = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
            if (AreSameSearchCheckpoint(initial, current))
            {
                return current;
            }
        }

        return null;
    }

    private static bool AreSameSearchCheckpoint(CarGridSnapshot left, CarGridSnapshot right)
    {
        if (!left.IsCarGrid || !right.IsCarGrid ||
            left.ManufacturerState != right.ManufacturerState)
        {
            return false;
        }

        return left.ManufacturerState switch
        {
            ManufacturerHeaderState.Target =>
                left.FocusedCellIndex >= 0 &&
                right.FocusedCellIndex >= 0 &&
                left.FocusedCellIndex == right.FocusedCellIndex &&
                string.Equals(left.ContentFingerprint, right.ContentFingerprint, StringComparison.Ordinal),
            ManufacturerHeaderState.Other =>
                string.Equals(
                    GameVisionService.Normalize(left.SelectedHeaderText),
                    GameVisionService.Normalize(right.SelectedHeaderText),
                    StringComparison.Ordinal),
            _ => false
        };
    }

    private async Task<CarCandidate> MoveToCarCandidateAsync(
        RequiredCarDefinition requiredCar,
        CarGridSnapshot snapshot,
        CarCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (snapshot.FocusedCellIndex < 0 || candidate.CellIndex < 0)
        {
            throw await CreateFailureAsync(
                "ConfirmarCartao",
                "O cartão requisito apareceu no OCR, mas a célula atualmente focada não foi identificada pelo contorno verde.");
        }

        var currentRow = snapshot.FocusedCellIndex / 4;
        var currentColumn = snapshot.FocusedCellIndex % 4;
        var targetRow = candidate.CellIndex / 4;
        var targetColumn = candidate.CellIndex % 4;
        context.Logger.State(
            Workflow,
            "FocarCartao",
            $"Movendo pelo controle da linha {currentRow + 1}, coluna {currentColumn + 1} " +
            $"para {requiredCar.DisplayName} na linha {targetRow + 1}, coluna {targetColumn + 1}.");

        while (currentRow > targetRow)
        {
            await context.Input.TapAsync(GameKey.Up, cancellationToken, 55);
            currentRow--;
        }

        while (currentRow < targetRow)
        {
            await context.Input.TapAsync(GameKey.Down, cancellationToken, 55);
            currentRow++;
        }

        while (currentColumn > targetColumn)
        {
            await context.Input.TapAsync(GameKey.Left, cancellationToken, 55);
            currentColumn--;
        }

        while (currentColumn < targetColumn)
        {
            await context.Input.TapAsync(GameKey.Right, cancellationToken, 55);
            currentColumn++;
        }

        await Task.Delay(350, cancellationToken);
        const int maximumNeighborCorrections = 2;
        for (var correctionAttempt = 0;
             correctionAttempt <= maximumNeighborCorrections;
             correctionAttempt++)
        {
            var confirmationsByCell = new Dictionary<int, (int Count, CarCandidate Candidate)>();
            var correctionsByPair = new Dictionary<
                (int FocusedCellIndex, int TargetCellIndex),
                int>();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var confirmation = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
                CarCandidate? observedCandidate = null;
                if (confirmation.ManufacturerState == ManufacturerHeaderState.Target)
                {
                    observedCandidate = GetSingleFocusedRequiredCandidate(confirmation);
                    if (observedCandidate is null && confirmation.FocusedCellIndex >= 0)
                    {
                        var adjacentTargets = confirmation.Candidates
                            .Where(item =>
                                AreOrthogonallyAdjacentCells(
                                    confirmation.FocusedCellIndex,
                                    item.CellIndex))
                            .ToArray();
                        if (adjacentTargets.Length == 1)
                        {
                            var adjacentTarget = adjacentTargets[0];
                            var key = (confirmation.FocusedCellIndex, adjacentTarget.CellIndex);
                            correctionsByPair.TryGetValue(key, out var correctionCount);
                            correctionsByPair[key] = correctionCount + 1;
                        }
                    }
                }

                if (observedCandidate is not null)
                {
                    confirmationsByCell.TryGetValue(
                        observedCandidate.CellIndex,
                        out var observation);
                    confirmationsByCell[observedCandidate.CellIndex] = (
                        observation.Count + 1,
                        observedCandidate);
                }

                if (attempt < 2)
                {
                    await Task.Delay(180, cancellationToken);
                }
            }

            var confirmedCandidates = confirmationsByCell
                .Where(observation => observation.Value.Count >= 2)
                .Select(observation => observation.Value)
                .ToArray();
            if (confirmedCandidates.Length == 1)
            {
                var confirmed = confirmedCandidates[0];
                context.Logger.State(
                    Workflow,
                    "ConfirmarCartao",
                    $"Foco do cartão ancorado na célula {confirmed.Candidate.CellIndex + 1} confirmado " +
                    $"em {confirmed.Count}/3 capturas; alvo OCR inicial: célula {candidate.CellIndex + 1}.");
                return confirmed.Candidate;
            }

            var confirmedCorrections = correctionsByPair
                .Where(observation => observation.Value >= 2)
                .Select(observation => new
                {
                    observation.Key.FocusedCellIndex,
                    observation.Key.TargetCellIndex,
                    Count = observation.Value
                })
                .ToArray();
            if (correctionAttempt >= maximumNeighborCorrections ||
                confirmedCorrections.Length != 1)
            {
                break;
            }

            var correction = confirmedCorrections[0];
            var correctionKey = DirectionBetweenAdjacentCells(
                correction.FocusedCellIndex,
                correction.TargetCellIndex);
            context.Logger.State(
                Workflow,
                "CorrigirFocoCartao",
                $"O foco permaneceu na célula {correction.FocusedCellIndex + 1}, mas o único cartão requisito " +
                $"vizinho foi confirmado na célula {correction.TargetCellIndex + 1} em {correction.Count}/3 capturas; " +
                $"enviando uma correção limitada com {correctionKey}.");
            await context.Input.TapAsync(correctionKey, cancellationToken, 55);
            await Task.Delay(350, cancellationToken);
        }

        throw await CreateFailureAsync(
            "ConfirmarCartao",
            $"Os direcionais foram enviados, mas o mesmo cartão de {requiredCar.DisplayName} " +
            "não foi confirmado simultaneamente por OCR e pelo contorno verde.");
    }

    private async Task<PerformanceIndexObservation> ReadDetailsPerformanceIndexAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        var requiredCount = 0;
        var wrongCount = 0;
        var sawConflict = false;
        var observed = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var details = await context.Vision.ReadScaledRegionAsync(
                DetailsClassRegion,
                requestedScale: 4,
                cancellationToken);
            observed.Add(details.Text);
            switch (requiredCar.ClassifyPerformanceIndex(details.Text))
            {
                case PerformanceIndexEvidence.Required:
                    requiredCount++;
                    break;
                case PerformanceIndexEvidence.Wrong:
                    wrongCount++;
                    break;
                case PerformanceIndexEvidence.Conflicting:
                    sawConflict = true;
                    break;
            }

            if (attempt < 2)
            {
                await Task.Delay(160, cancellationToken);
            }
        }

        var evidence = !sawConflict && requiredCount >= 2 && wrongCount == 0
            ? PerformanceIndexEvidence.Required
            : !sawConflict && wrongCount >= 2 && requiredCount == 0
                ? PerformanceIndexEvidence.Wrong
                : sawConflict || (requiredCount > 0 && wrongCount > 0)
                    ? PerformanceIndexEvidence.Conflicting
                    : PerformanceIndexEvidence.Missing;
        return new PerformanceIndexObservation(evidence, string.Join(" | ", observed));
    }

    private async Task EnterFocusedCarAsync(
        RequiredCarDefinition requiredCar,
        int expectedCellIndex,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        var recentFocusConfirmations = new Queue<bool>(3);
        var focusConfirmed = false;
        while (DateTime.UtcNow < deadline)
        {
            var finalFocus = await CaptureStableGridSnapshotAsync(requiredCar, cancellationToken);
            var selectedTarget = GetSingleFocusedRequiredCandidate(finalFocus);
            var currentFocusConfirmed =
                finalFocus.ManufacturerState == ManufacturerHeaderState.Target &&
                selectedTarget?.CellIndex == expectedCellIndex;
            recentFocusConfirmations.Enqueue(currentFocusConfirmed);
            if (recentFocusConfirmations.Count > 3)
            {
                _ = recentFocusConfirmations.Dequeue();
            }

            if (currentFocusConfirmed &&
                recentFocusConfirmations.Count == 3 &&
                recentFocusConfirmations.Count(value => value) >= 2)
            {
                focusConfirmed = true;
                break;
            }

            await Task.Delay(180, cancellationToken);
        }

        if (!focusConfirmed)
        {
            throw await CreateFailureAsync(
                "ConfirmarCartaoAntesDeEntrar",
                $"O foco em {requiredCar.DisplayName} não foi readquirido com fabricante, título, célula e borda verde " +
                "em duas de três capturas dentro de 12 segundos antes de abrir as ações.");
        }

        if (requiredCar.RequiresS1Pi800)
        {
            var finalClass = await ReadDetailsPerformanceIndexAsync(requiredCar, cancellationToken);
            if (finalClass.Evidence != PerformanceIndexEvidence.Required)
            {
                throw await CreateFailureAsync(
                    "ConfirmarClasseAntesDeEntrar",
                    $"A confirmação final do painel não mostrou S1 800 em duas leituras concordantes. " +
                    $"OCR: '{Shorten(finalClass.ObservedText)}'.");
            }
        }

        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
        var action = await context.Vision.WaitForAnyTextAsync(
            Workflow,
            "AcoesDoCarro",
            ["ENTRAR NO CARRO"],
            cancellationToken,
            TimeSpan.FromSeconds(8));
        var game = context.GameWindow.GetRequiredGameWindow();
        var actionRegion = new RectangleF(
            0.32f,
            Math.Clamp((float)(action.Line.Center.Y / (double)game.ClientBounds.Height - 0.035), 0.20f, 0.75f),
            0.36f,
            0.07f);
        if (!await context.Vision.HasLimeSelectionAsync(actionRegion, cancellationToken, minimumRatio: 0.003))
        {
            throw await CreateFailureAsync(
                "ConfirmarEntrarNoCarro",
                "A ação Entrar no Carro apareceu, mas sua seleção não foi confirmada pela borda verde.");
        }

        context.Logger.State(
            Workflow,
            "EntrarNoCarro",
            $"Confirmando uma única vez a ação validada para {requiredCar.DisplayName}.");
        await context.Input.TapAsync(GameKey.Enter, cancellationToken);
    }

    private async Task WaitForStreetMenuAfterChangeAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(4_000, cancellationToken);
        var passiveDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        var deliveryLoadingObserved = false;
        var recentStreetObservations = new Queue<bool>(3);
        var streetConfirmed = false;
        while (DateTime.UtcNow < passiveDeadline)
        {
            if (await IsCarDeliveryLoadingAsync(cancellationToken))
            {
                if (!deliveryLoadingObserved)
                {
                    context.Logger.State(
                        Workflow,
                        "AguardarEntregaDoCarro",
                        "A tela 'Aguardando Entrega do Carro' foi confirmada; aguardando passivamente, sem enviar sondas de menu.");
                }

                deliveryLoadingObserved = true;
                recentStreetObservations.Clear();
                await Task.Delay(1_000, cancellationToken);
                continue;
            }

            var state = await context.GameContext.DetectAsync(cancellationToken);
            if (state.Kind == GameContextKind.StreetMenu)
            {
                return;
            }

            if (state.Kind is GameContextKind.Event or
                GameContextKind.EventMenu or
                GameContextKind.EventPreRaceMenu or
                GameContextKind.Garage or
                GameContextKind.ControllerDisconnected or
                GameContextKind.CarPositionResetConfirmation or
                GameContextKind.EventExitConfirmation)
            {
                throw await CreateFailureAsync(
                    "AguardarTroca",
                    $"A troca de carro terminou em um contexto inesperado: {state.Kind}.");
            }

            if (await IsStreetMenuTextAsync(cancellationToken))
            {
                return;
            }

            var isStreet = state.Kind == GameContextKind.Street;
            recentStreetObservations.Enqueue(isStreet);
            if (recentStreetObservations.Count > 3)
            {
                _ = recentStreetObservations.Dequeue();
            }

            if (isStreet &&
                recentStreetObservations.Count == 3 &&
                recentStreetObservations.Count(value => value) >= 2)
            {
                streetConfirmed = true;
                context.Logger.State(
                    Workflow,
                    "EntregaDoCarroConcluida",
                    deliveryLoadingObserved
                        ? "A entrega terminou e a rua foi confirmada em duas de três leituras; uma sonda limitada de menu está autorizada."
                        : "A rua foi confirmada em duas de três leituras após a troca; uma sonda limitada de menu está autorizada.");
                break;
            }

            await Task.Delay(1_000, cancellationToken);
        }

        if (!streetConfirmed)
        {
            throw await CreateFailureAsync(
                "AguardarTroca",
                deliveryLoadingObserved
                    ? "A tela de entrega não produziu uma confirmação positiva da rua dentro do limite passivo de dez minutos. " +
                      "Nenhuma sonda de menu foi enviada durante o carregamento ou em contexto desconhecido."
                    : "A rua e a tela 'Aguardando Entrega do Carro' não foram confirmadas dentro de dez minutos. " +
                      "Nenhuma sonda de menu foi enviada durante o estado inconclusivo.");
        }

        for (var probe = 1; probe <= 2; probe++)
        {
            if (await IsCarDeliveryLoadingAsync(cancellationToken))
            {
                throw await CreateFailureAsync(
                    "AguardarTroca",
                    "A tela 'Aguardando Entrega do Carro' reapareceu antes da sonda de menu. " +
                    "O BOT parou sem enviar entradas durante o carregamento.");
            }

            var street = await context.GameContext.DetectAsync(cancellationToken);
            if (street.Kind != GameContextKind.Street)
            {
                throw await CreateFailureAsync(
                    "AguardarTroca",
                    $"A confirmação atual da rua se perdeu antes da sonda {probe}/2 ({street.Kind}); " +
                    "nenhuma entrada adicional foi enviada.");
            }

            context.Logger.State(
                Workflow,
                "ConfirmarRetornoRua",
                $"Sondando o menu após reconfirmar a rua ({probe}/2).");
            await context.Input.TapAsync(GameKey.Menu, cancellationToken, 100);
            await Task.Delay(1_000, cancellationToken);
            var menu = await context.GameContext.DetectAsync(cancellationToken);
            if (menu.Kind == GameContextKind.StreetMenu ||
                await IsStreetMenuTextAsync(cancellationToken))
            {
                return;
            }

            if (menu.Kind is GameContextKind.Event or
                GameContextKind.EventMenu or
                GameContextKind.EventPreRaceMenu or
                GameContextKind.Garage or
                GameContextKind.ControllerDisconnected or
                GameContextKind.CarPositionResetConfirmation or
                GameContextKind.EventExitConfirmation)
            {
                throw await CreateFailureAsync(
                    "AguardarTroca",
                    $"A sonda após a troca terminou em um contexto inesperado: {menu.Kind}.");
            }

            if (menu.Kind != GameContextKind.Street)
            {
                throw await CreateFailureAsync(
                    "AguardarTroca",
                    $"A sonda {probe}/2 não confirmou nem o menu nem a permanência na rua ({menu.Kind}); " +
                    "nenhuma nova sonda foi autorizada.");
            }

            await Task.Delay(1_000, cancellationToken);
        }

        throw await CreateFailureAsync(
            "AguardarTroca",
            "O carregamento terminou, mas o menu da rua não foi confirmado após duas sondas limitadas e revalidadas.");
    }

    private async Task<bool> IsCarDeliveryLoadingAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScaledRegionAsync(
            CarDeliveryStatusRegion,
            requestedScale: 3,
            cancellationToken);
        var normalized = GameVisionService.Normalize(document.Text);
        return normalized.Contains("AGUARDANDO", StringComparison.Ordinal) &&
               normalized.Contains("ENTREGA", StringComparison.Ordinal) &&
               normalized.Contains("CARRO", StringComparison.Ordinal);
    }

    private async Task NormalizeCampaignTabAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= 7; attempt++)
        {
            var campaignSelected = await context.Vision.AnalyzeScreenAsync(
                (bitmap, _) => DarkFillRatio(bitmap, CampaignTabRegion) >= 0.45,
                cancellationToken);
            if (campaignSelected)
            {
                context.Logger.State(
                    Workflow,
                    "NormalizarCampanha",
                    "Aba Campanha confirmada pelo fundo escuro exclusivo da guia ativa.");
                return;
            }

            var document = await context.Vision.ReadScaledRegionAsync(
                CampaignPanelRegion,
                requestedScale: 2,
                cancellationToken);
            var normalized = GameVisionService.Normalize(document.Text);
            if (normalized.Contains("MAPA DO MUNDO", StringComparison.Ordinal) &&
                (normalized.Contains("DIARIO DE COLECAO", StringComparison.Ordinal) ||
                 normalized.Contains("O QUE VEM A SEGUIR", StringComparison.Ordinal)))
            {
                context.Logger.State(Workflow, "NormalizarCampanha", "Aba Campanha confirmada para o próximo fluxo.");
                return;
            }

            if (attempt == 7)
            {
                break;
            }

            await context.Input.TapAsync(GameKey.Shift, cancellationToken, 60);
            await Task.Delay(500, cancellationToken);
        }

        throw await CreateFailureAsync(
            "NormalizarCampanha",
            "O carro foi validado, mas não foi possível normalizar o menu na aba Campanha.");
    }

    private async Task<CarGridSnapshot> CaptureStableGridSnapshotAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        CarGridSnapshot? last = null;
        string? previousOtherHeader = null;
        string? previousTargetKey = null;
        var consecutiveTargetObservations = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var snapshot = await CaptureGridSnapshotAsync(requiredCar, cancellationToken);
            last = snapshot;
            if (snapshot.IsCarGrid &&
                snapshot.ManufacturerState == ManufacturerHeaderState.Target)
            {
                var key = $"{snapshot.FocusedCellIndex}:" + string.Join(
                    ",",
                    snapshot.Candidates
                        .Select(candidate => candidate.CellIndex)
                        .Distinct()
                        .Order());
                if (string.Equals(key, previousTargetKey, StringComparison.Ordinal))
                {
                    consecutiveTargetObservations++;
                }
                else
                {
                    previousTargetKey = key;
                    consecutiveTargetObservations = 1;
                }

                if (consecutiveTargetObservations >= 2)
                {
                    return snapshot;
                }

                previousOtherHeader = null;
            }
            else if (snapshot.IsCarGrid &&
                     snapshot.ManufacturerState == ManufacturerHeaderState.Other)
            {
                previousTargetKey = null;
                consecutiveTargetObservations = 0;
                var normalizedHeader = GameVisionService.Normalize(snapshot.SelectedHeaderText);
                if (normalizedHeader == previousOtherHeader)
                {
                    return snapshot;
                }

                previousOtherHeader = normalizedHeader;
            }
            else
            {
                previousTargetKey = null;
                consecutiveTargetObservations = 0;
                previousOtherHeader = null;
            }

            if (attempt < 3)
            {
                await Task.Delay(180, cancellationToken);
            }
        }

        return last is null
            ? throw new InvalidOperationException("A captura estável da grade não produziu frames.")
            : last with { ManufacturerState = ManufacturerHeaderState.Inconclusive };
    }

    private async Task<CarGridSnapshot> CaptureGridSnapshotAsync(
        RequiredCarDefinition requiredCar,
        CancellationToken cancellationToken)
    {
        var snapshot = await context.Vision.AnalyzeScreenAsync(
            (bitmap, document) => AnalyzeGrid(bitmap, document, requiredCar),
            cancellationToken);
        if (snapshot.SelectedHeaderRegion is null)
        {
            return snapshot;
        }

        var selectedHeader = await context.Vision.ReadScaledRegionAsync(
            snapshot.SelectedHeaderRegion.Value,
            requestedScale: 5,
            cancellationToken);
        return snapshot with
        {
            ManufacturerState = IsManufacturerLabel(selectedHeader.Text, requiredCar.Manufacturer)
                ? ManufacturerHeaderState.Target
                : LooksLikeManufacturerHeader(selectedHeader.Text)
                    ? ManufacturerHeaderState.Other
                    : ManufacturerHeaderState.Inconclusive,
            SelectedHeaderText = selectedHeader.Text
        };
    }

    private static CarGridSnapshot AnalyzeGrid(
        Bitmap bitmap,
        OcrDocument document,
        RequiredCarDefinition requiredCar)
    {
        var normalized = GameVisionService.Normalize(document.Text);
        var isCarGrid = normalized.Contains("IR PARA FABRICANTE", StringComparison.Ordinal) &&
                        normalized.Contains("MEUS CARROS", StringComparison.Ordinal);
        var selectedHeader = VisibleManufacturerHeaders
            .Select(region => new { Region = region, Ratio = DarkFillRatio(bitmap, region) })
            .OrderByDescending(item => item.Ratio)
            .First();
        RectangleF? selectedHeaderRegion = selectedHeader.Ratio >= 0.45
            ? selectedHeader.Region
            : null;

        var measuredCells = VisibleCarCells
            .Select((region, index) => new { Index = index, Ratio = LimeVerticalBorderRatio(bitmap, region) })
            .OrderByDescending(item => item.Ratio)
            .ToArray();
        var selectedCell = measuredCells[0];
        var runnerUpCell = measuredCells[1];
        var selectedCellIndex = selectedCell.Ratio >= 0.10 &&
                                runnerUpCell.Ratio < 0.025 &&
                                selectedCell.Ratio - runnerUpCell.Ratio >= 0.075
            ? selectedCell.Index
            : -1;

        var candidates = VisibleCarCells
            .Select((cell, cellIndex) =>
            {
                var titleBand = new RectangleF(
                    cell.X,
                    cell.Y,
                    cell.Width,
                    Math.Min(cell.Height, 0.085f));
                var titlePixels = ToPixels(bitmap, titleBand);
                var title = string.Join(
                    " ",
                    document.Tokens
                        .Where(token => titlePixels.Contains(token.Center))
                        .OrderBy(token => token.Y)
                        .ThenBy(token => token.X)
                        .Select(token => token.Text));
                if (!requiredCar.MatchesCardTitle(title))
                {
                    return null;
                }

                var center = new Point(
                    (int)Math.Round(bitmap.Width * (cell.X + cell.Width / 2)),
                    (int)Math.Round(bitmap.Height * (cell.Y + cell.Height / 2)));
                return new CarCandidate(
                    cell,
                    center,
                    cellIndex,
                    cellIndex == selectedCellIndex,
                    GameVisionService.Normalize(title));
            })
            .OfType<CarCandidate>()
            .ToArray();

        var contentFingerprint = string.Join(
            "|",
            document.Lines
                .Where(line =>
                    line.Center.X >= bitmap.Width * 0.20 &&
                    line.Center.X <= bitmap.Width * 0.96 &&
                    line.Center.Y >= bitmap.Height * 0.18 &&
                    line.Center.Y <= bitmap.Height * 0.72)
                .OrderBy(line => line.Center.Y)
                .ThenBy(line => line.Center.X)
                .Select(line => $"{GameVisionService.Normalize(line.Text)}@{line.Center.X / 20}:{line.Center.Y / 20}"));
        return new CarGridSnapshot(
            isCarGrid,
            ManufacturerHeaderState.Inconclusive,
            candidates,
            contentFingerprint,
            selectedCellIndex,
            selectedHeaderRegion,
            SelectedHeaderText: string.Empty);
    }

    private static double DarkFillRatio(Bitmap bitmap, RectangleF normalizedRegion)
    {
        var region = ToPixels(bitmap, normalizedRegion);
        var dark = 0;
        var sampled = 0;
        for (var y = region.Top; y < region.Bottom; y += 2)
        {
            for (var x = region.Left; x < region.Right; x += 2)
            {
                var color = bitmap.GetPixel(x, y);
                sampled++;
                if (color.R <= 75 && color.G <= 75 && color.B <= 75)
                {
                    dark++;
                }
            }
        }

        return sampled == 0 ? 0 : dark / (double)sampled;
    }

    private static double LimeVerticalBorderRatio(Bitmap bitmap, RectangleF normalizedRegion)
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

    private static double LimeVerticalBorderRatioWithHorizontalTolerance(
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

        // Em 1600x900, a ROI ancorada pelo OCR pode ficar alguns pixels mais
        // larga ou estreita que a célula real. A busca permanece limitada ao
        // entorno de cada borda esperada e ainda exige as duas bordas lime.
        return Math.Min(
            BestRatioAround(region.Left),
            BestRatioAround(region.Right - border));
    }

    private static Rectangle ToPixels(Bitmap bitmap, RectangleF normalized)
    {
        var x = Math.Clamp((int)Math.Round(bitmap.Width * normalized.X), 0, bitmap.Width - 1);
        var y = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Y), 0, bitmap.Height - 1);
        var width = Math.Clamp((int)Math.Round(bitmap.Width * normalized.Width), 1, bitmap.Width - x);
        var height = Math.Clamp((int)Math.Round(bitmap.Height * normalized.Height), 1, bitmap.Height - y);
        return new Rectangle(x, y, width, height);
    }

    private static RectangleF[] CreateVisibleCarCells()
    {
        return Enumerable.Range(0, 3)
            .SelectMany(row => Enumerable.Range(0, 4).Select(column =>
                new RectangleF(
                    0.208f + column * 0.174f,
                    0.195f + row * 0.232f,
                    0.178f,
                    0.225f)))
            .ToArray();
    }

    private static RectangleF[] CreateVisibleManufacturerHeaders()
    {
        return Enumerable.Range(0, 6)
            .Select(column => new RectangleF(
                0.09f + column * 0.137f,
                0.142f,
                0.136f,
                0.047f))
            .ToArray();
    }

    private static bool LooksLikeCarHeader(string text)
    {
        var normalized = GameVisionService.Normalize(text);
        if (Regex.IsMatch(normalized, @"\b(?:19|20)\d{2}\b", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (normalized.Contains("MEUS CARROS", StringComparison.Ordinal) &&
            normalized.Count(char.IsLetter) >= 8)
        {
            return true;
        }

        var compact = Regex.Replace(normalized, @"[^A-Z0-9]", string.Empty, RegexOptions.CultureInvariant);
        return Regex.IsMatch(compact, @"(?:S1|S2|SI|SLI|I|R|A|B|C|D)\d{3}", RegexOptions.CultureInvariant) &&
               normalized.Count(char.IsLetter) >= 5;
    }

    private static bool LooksLikeManufacturerHeader(string text)
    {
        var normalized = GameVisionService.Normalize(text).Trim();
        if (normalized.Length is < 2 or > 32 ||
            normalized is "CARRO ATUAL" or "MEUS CARROS" or "IR PARA FABRICANTE")
        {
            return false;
        }

        return normalized.Count(char.IsLetter) >= 2 &&
               Regex.IsMatch(normalized, @"^[A-Z0-9 .&'-]+$", RegexOptions.CultureInvariant);
    }

    private static bool IsManufacturerLabel(string text, string manufacturer)
    {
        var normalized = GameVisionService.Normalize(text);
        if (normalized == manufacturer)
        {
            return true;
        }

        if (Math.Abs(normalized.Length - manufacturer.Length) > 1)
        {
            return false;
        }

        return EditDistanceAtMostOne(normalized, manufacturer);
    }

    private static bool EditDistanceAtMostOne(string left, string right)
    {
        if (left == right)
        {
            return true;
        }

        if (Math.Abs(left.Length - right.Length) > 1)
        {
            return false;
        }

        var first = 0;
        var second = 0;
        var differences = 0;
        while (first < left.Length && second < right.Length)
        {
            if (left[first] == right[second])
            {
                first++;
                second++;
                continue;
            }

            if (++differences > 1)
            {
                return false;
            }

            if (left.Length > right.Length)
            {
                first++;
            }
            else if (right.Length > left.Length)
            {
                second++;
            }
            else
            {
                first++;
                second++;
            }
        }

        return differences + (left.Length - first) + (right.Length - second) <= 1;
    }

    private async Task<bool> IsHouseEntranceOpenAsync(CancellationToken cancellationToken) =>
        await context.Vision.ContainsAnyTextAsync(
            ["ENTRAR NA CASA"],
            cancellationToken);

    private async Task<bool> IsStreetMenuTextAsync(CancellationToken cancellationToken)
    {
        var document = await context.Vision.ReadScreenAsync(cancellationToken);
        var normalized = GameVisionService.Normalize(document.Text);
        if (normalized.Contains("REINICIAR EVENTO", StringComparison.Ordinal) ||
            normalized.Contains("SAIR DO EVENTO", StringComparison.Ordinal))
        {
            return false;
        }

        var tabs = new[] { "CAMPANHA", "CARROS", "MEU HORIZON", "ONLINE", "CENTRAL CRIATIVA" }
            .Count(tab => normalized.Contains(tab, StringComparison.Ordinal));
        return tabs >= 2 &&
               (normalized.Contains("MAPA DO MUNDO", StringComparison.Ordinal) ||
                normalized.Contains("TROCAR DE CARRO", StringComparison.Ordinal) ||
                normalized.Contains("DIARIO DE COLECAO", StringComparison.Ordinal));
    }

    private async Task<CalibrationRequiredException> CreateFailureAsync(string state, string message)
    {
        try
        {
            using var frame = await context.Capture.CaptureAsync(CancellationToken.None);
            var diagnostic = context.Capture.SaveDiagnostic(frame.Bitmap, Workflow, state);
            return new CalibrationRequiredException($"{message} Diagnóstico local: {diagnostic}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.Logger.Warn($"Não foi possível salvar o diagnóstico de '{state}': {exception.Message}");
            return new CalibrationRequiredException(message);
        }
    }

    private static string Shorten(string value)
    {
        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 220 ? compact : compact[..220] + "…";
    }

    private enum CurrentCarKind
    {
        Correct,
        Different,
        Inconclusive
    }

    private enum ManufacturerHeaderState
    {
        Target,
        Other,
        Inconclusive
    }

    private sealed record CurrentCarObservation(CurrentCarKind Kind, string ObservedText);

    private sealed record PerformanceIndexObservation(
        PerformanceIndexEvidence Evidence,
        string ObservedText);

    private sealed record CarGridSnapshot(
        bool IsCarGrid,
        ManufacturerHeaderState ManufacturerState,
        IReadOnlyList<CarCandidate> Candidates,
        string ContentFingerprint,
        int FocusedCellIndex,
        RectangleF? SelectedHeaderRegion,
        string SelectedHeaderText);

    private sealed record CarCandidate(
        RectangleF Region,
        Point Center,
        int CellIndex,
        bool Selected,
        string Text);

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
}
