// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using System.Windows.Input;
using NXProject.Services;

namespace NXProject.Views;

public partial class PercAlocEditWindow : Window
{
    private readonly int      _maxPercent;
    private readonly DateTime _taskStart;
    private readonly double   _totalHours; // CurrentHours + EstimatedHours

    public double ResultPercent { get; private set; }

    public PercAlocEditWindow(string taskName, double currentPercent, int maxPercent = 100,
        DateTime taskStart = default, double totalHours = 0)
    {
        InitializeComponent();
        _maxPercent = Math.Clamp(maxPercent, 1, 120);
        _taskStart  = taskStart == default ? DateTime.Today : taskStart;
        _totalHours = totalHours;

        TaskNameText.Text = taskName;
        RangeText.Text    = AppStrings.Get("PercAloc_Range", _maxPercent);
        // % de alocação exibido com até 2 casas decimais.
        PercAlocBox.Text  = Math.Round(currentPercent, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // HH/dia pré-preenchido
        var hpd = ProjectCalendarService.WorkingHoursPerDay * currentPercent / 100.0;
        if (hpd > 0)
            HhDiaBox.Text = $"{hpd:0.##}";

        // Label da seção de data fim
        if (totalHours > 0)
            FinishCalcLabel.Text = AppStrings.Get("PercAloc_ByFinishTotal", totalHours);
        else
            FinishCalcLabel.Text = AppStrings.Get("PercAloc_ByFinish");

        // Foco no campo de % de alocação ao abrir (com o texto selecionado).
        Loaded += (_, _) =>
        {
            PercAlocBox.Focus();
            PercAlocBox.SelectAll();
        };
    }

    private void OnCalculatePercent(object sender, RoutedEventArgs e)
    {
        var raw = HhDiaBox.Text.Replace(',', '.').Trim();
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var hh) || hh <= 0)
        {
            ShowError(AppStrings.Get("PercAloc_InvalidHhDay"));
            HhDiaBox.Focus();
            return;
        }

        var perc = Math.Round(hh / ProjectCalendarService.WorkingHoursPerDay * 100.0, 2);
        perc = Math.Clamp(perc, 1, _maxPercent);
        PercAlocBox.Text = perc.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        HideError();
        PercAlocBox.Focus();
        PercAlocBox.SelectAll();
    }

    private void OnCalculateFromFinish(object sender, RoutedEventArgs e)
    {
        // Parse da data fim
        var raw = FinishDateBox.Text.Trim();
        DateTime finish;
        if (!DateTime.TryParseExact(raw,
                new[] { "dd/MM/yyyy", "dd/MM/yy", "d/M/yyyy", "d/M/yy" },
                System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.None, out finish))
        {
            ShowError(AppStrings.Get("PercAloc_InvalidFinish"));
            FinishDateBox.Focus();
            return;
        }

        if (finish <= _taskStart)
        {
            ShowError(AppStrings.Get("PercAloc_FinishBeforeStart"));
            FinishDateBox.Focus();
            return;
        }

        double hours = _totalHours > 0 ? _totalHours : 0;
        if (hours <= 0)
        {
            // Sem horas definidas, usa um dia como base
            ShowError(AppStrings.Get("PercAloc_NoHours"));
            return;
        }

        // Horas úteis disponíveis no período Start → Finish
        double availableHours = ProjectCalendarService.CountWorkingHours(_taskStart, finish);
        if (availableHours <= 0)
        {
            ShowError(AppStrings.Get("PercAloc_NoWorkingDays"));
            return;
        }

        // % = horas necessárias / horas disponíveis × 100.
        // Trunca para 2 casas (piso) para não antecipar a data.
        double perc = Math.Floor(hours / availableHours * 100.0 * 100.0) / 100.0;
        perc = Math.Clamp(perc, 1, _maxPercent);
        PercAlocBox.Text = perc.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        // Também atualiza o HH/dia correspondente
        var hpd = ProjectCalendarService.WorkingHoursPerDay * perc / 100.0;
        HhDiaBox.Text = $"{hpd:0.##}";

        HideError();
        PercAlocBox.Focus();
        PercAlocBox.SelectAll();
    }

    private void OnPreviewDecimalInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.' || c == ',');
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var raw = PercAlocBox.Text.Replace(',', '.').Trim();
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 1 || v > _maxPercent)
        {
            ShowError(AppStrings.Get("PercAloc_RangeError", _maxPercent));
            PercAlocBox.Focus();
            PercAlocBox.SelectAll();
            return;
        }

        // Persiste com até 2 casas decimais.
        ResultPercent = Math.Round(v, 2);
        DialogResult  = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text       = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
