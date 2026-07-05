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
        RangeText.Text    = $"  (1 a {_maxPercent})";
        PercAlocBox.Text  = ((int)currentPercent).ToString();

        // HH/dia pré-preenchido
        var hpd = ProjectCalendarService.WorkingHoursPerDay * currentPercent / 100.0;
        if (hpd > 0)
            HhDiaBox.Text = $"{hpd:0.##}";

        // Label da seção de data fim
        if (totalHours > 0)
            FinishCalcLabel.Text = $"Calcular pela data fim  ({totalHours:0.#}h total):";
        else
            FinishCalcLabel.Text = "Calcular pela data fim:";

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
            ShowError("Digite um valor válido para HH/dia.");
            HhDiaBox.Focus();
            return;
        }

        var perc = (int)Math.Round(hh / ProjectCalendarService.WorkingHoursPerDay * 100.0);
        perc = Math.Clamp(perc, 1, _maxPercent);
        PercAlocBox.Text = perc.ToString();
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
            ShowError("Data fim inválida. Use dd/MM/aaaa.");
            FinishDateBox.Focus();
            return;
        }

        if (finish <= _taskStart)
        {
            ShowError("A data fim deve ser posterior à data de início.");
            FinishDateBox.Focus();
            return;
        }

        double hours = _totalHours > 0 ? _totalHours : 0;
        if (hours <= 0)
        {
            // Sem horas definidas, usa um dia como base
            ShowError("A atividade não tem horas estimadas definidas para o cálculo.");
            return;
        }

        // Horas úteis disponíveis no período Start → Finish
        double availableHours = ProjectCalendarService.CountWorkingHours(_taskStart, finish);
        if (availableHours <= 0)
        {
            ShowError("Não há dias úteis no período informado.");
            return;
        }

        // % = horas necessárias / horas disponíveis × 100 (trunca para não antecipar a data)
        int perc = (int)(hours / availableHours * 100.0);
        perc = Math.Clamp(perc, 1, _maxPercent);
        PercAlocBox.Text = perc.ToString();

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
        if (!int.TryParse(PercAlocBox.Text, out var v) || v < 1 || v > _maxPercent)
        {
            ShowError($"Digite um valor entre 1 e {_maxPercent}.");
            PercAlocBox.Focus();
            PercAlocBox.SelectAll();
            return;
        }

        ResultPercent = v;
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
