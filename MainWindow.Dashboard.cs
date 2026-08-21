using FH6OpenAssist.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FH6OpenAssist;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _sessionTimer;

    private void InitializeDashboard()
    {
        PopulateBotCards();
        UpdateBotContext(_selectedMacro);
        RenderSessionSnapshot(_telemetry.Snapshot);

        _telemetry.Changed += Telemetry_Changed;
        _sessionTimer = _dispatcherQueue.CreateTimer();
        _sessionTimer.Interval = TimeSpan.FromSeconds(1);
        _sessionTimer.IsRepeating = true;
        _sessionTimer.Tick += SessionTimer_Tick;
        _sessionTimer.Start();
    }

    private void ShutdownDashboard()
    {
        _telemetry.Changed -= Telemetry_Changed;
        if (_sessionTimer is null)
        {
            return;
        }

        _sessionTimer.Stop();
        _sessionTimer.Tick -= SessionTimer_Tick;
        _sessionTimer = null;
    }

    private void PopulateBotCards()
    {
        ApplyBotDefinitionToCard(
            BotCatalog.Get(MacroKind.FarmarSp),
            SpCardTitleText,
            SpCardDescriptionText,
            SpCardResourceText);
        ApplyBotDefinitionToCard(
            BotCatalog.Get(MacroKind.Farmar200kMin),
            CrCardTitleText,
            CrCardDescriptionText,
            CrCardResourceText);
        ApplyBotDefinitionToCard(
            BotCatalog.Get(MacroKind.FarmarWheelspins),
            WheelspinCardTitleText,
            WheelspinCardDescriptionText,
            WheelspinCardResourceText);
        ApplyBotDefinitionToCard(
            BotCatalog.Get(MacroKind.GastarWheelspins),
            SpendWheelspinCardTitleText,
            SpendWheelspinCardDescriptionText,
            SpendWheelspinCardResourceText);
    }

    private void SetBotSelectionAvailability(bool available)
    {
        SpMacro.IsEnabled = available;
        CrMacro.IsEnabled = available;
        WheelspinMacro.IsEnabled = available;
        SpendWheelspinMacro.IsEnabled = available;
    }

    private static void ApplyBotDefinitionToCard(
        BotDefinition definition,
        TextBlock title,
        TextBlock description,
        TextBlock resource)
    {
        title.Text = definition.Name;
        description.Text = definition.Description;
        resource.Text = definition.ResourceSummary.Contains(" e ", StringComparison.OrdinalIgnoreCase)
            ? $"Recursos · {definition.ResourceSummary}"
            : $"Recurso · {definition.ResourceSummary}";
    }

    private void UpdateBotContext(MacroKind kind)
    {
        var definition = BotCatalog.Get(kind);
        ContextBotNameText.Text = definition.Name;
        ContextBotDescriptionText.Text = definition.Description;
        ContextBotStartText.Text = definition.StartContext;

        ContextBotBadgesPanel.Children.Clear();
        ContextBotBadgesPanel.Children.Add(CreateBadge(definition.ResourceSummary, accent: true));
        ContextBotBadgesPanel.Children.Add(CreateBadge(
            definition.SupportsBackground ? "Segundo plano compatível" : "Somente primeiro plano"));
        if (definition.RequiresViGEm)
        {
            ContextBotBadgesPanel.Children.Add(CreateBadge("Requer ViGEmBus", warning: true));
        }

        if (definition.Experimental)
        {
            ContextBotBadgesPanel.Children.Add(CreateBadge("Experimental", warning: true));
        }

        ContextBotRequirementsPanel.Children.Clear();
        foreach (var requirement in definition.Requirements)
        {
            ContextBotRequirementsPanel.Children.Add(CreateRequirementRow(requirement));
        }

        AutomationProperties.SetName(
            ContextBotRequirementsPanel,
            $"Pré-requisitos de {definition.Name}");
    }

    private Border CreateBadge(string text, bool accent = false, bool warning = false)
    {
        var badge = new Border
        {
            Style = (Style)Application.Current.Resources["DashboardBadgeStyle"]
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = warning
                ? ThemeBrush("WarningBrush")
                : accent ? ThemeBrush("ShellAccentForegroundBrush") : ThemeBrush("TextBrush")
        };

        if (accent)
        {
            badge.Background = ThemeBrush("ShellAccentSubtleBrush");
        }

        badge.Child = label;
        AutomationProperties.SetName(badge, text);
        return badge;
    }

    private Grid CreateRequirementRow(BotRequirement requirement)
    {
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var prefix = requirement.Kind switch
        {
            BotRequirementKind.Automated => "Automático",
            BotRequirementKind.Required => "Pré-requisito",
            _ => "Aviso"
        };

        var icon = new FontIcon
        {
            FontSize = 12,
            Foreground = ThemeBrush(requirement.Kind switch
            {
                BotRequirementKind.Automated => "SuccessBrush",
                BotRequirementKind.Required => "TextBrush",
                _ => "WarningBrush"
            }),
            Glyph = requirement.Kind == BotRequirementKind.Advisory ? "\uE7BA" : "\uE73E",
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = $"{prefix} · {requirement.Text}",
            FontSize = 12,
            Foreground = ThemeBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(icon);
        row.Children.Add(label);
        AutomationProperties.SetName(
            row,
            $"{prefix}: {requirement.Text}");
        return row;
    }

    private void Telemetry_Changed(SessionTelemetrySnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() => RenderSessionSnapshot(snapshot));
    }

    private void SessionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        RenderSessionSnapshot(_telemetry.Snapshot);
    }

    private void RenderSessionSnapshot(SessionTelemetrySnapshot snapshot)
    {
        var cyclesAreMeasured = snapshot.Bot != MacroKind.FarmarSp || snapshot.StartedAt is null;
        SessionElapsedText.Text = FormatElapsed(snapshot.Elapsed);
        SessionCyclesText.Text = cyclesAreMeasured ? snapshot.Cycles.ToString("N0") : "—";
        SessionRecoveriesText.Text = snapshot.Recoveries.ToString("N0");
        SessionFailuresText.Text = snapshot.Failures.ToString("N0");
        SessionStageText.Text = snapshot.StartedAt is null
            ? _currentState == MacroRunState.Armado
                ? "BOT armado · Pressione F8 para iniciar a sessão."
                : "Aguardando ativação"
            : $"{snapshot.Stage} · {snapshot.Description}";

        AutomationProperties.SetName(
            SessionStageText,
            $"Etapa atual: {SessionStageText.Text}");
        AutomationProperties.SetName(SessionElapsedText, $"Tempo da sessão: {SessionElapsedText.Text}");
        AutomationProperties.SetName(
            SessionCyclesText,
            cyclesAreMeasured
                ? $"Ciclos confirmados: {SessionCyclesText.Text}"
                : "Ciclos confirmados: não medidos para Skill Points");
        AutomationProperties.SetName(SessionRecoveriesText, $"Recuperações: {SessionRecoveriesText.Text}");
        AutomationProperties.SetName(SessionFailuresText, $"Falhas: {SessionFailuresText.Text}");
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var hours = Math.Max(0, (long)elapsed.TotalHours);
        return $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
