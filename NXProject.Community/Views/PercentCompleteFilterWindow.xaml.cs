using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class PercentCompleteFilterWindow : Window
    {
        public double? MinPercent { get; private set; }
        public double? MaxPercent { get; private set; }
        public string? DateFilterMode { get; private set; }
        public DateTime? ReferenceDate { get; private set; }

        public PercentCompleteFilterWindow(
            double? minPercent,
            double? maxPercent,
            string? dateFilterMode = null,
            DateTime? referenceDate = null)
        {
            InitializeComponent();

            MinBox.Text = FormatPercent(minPercent);
            MaxBox.Text = FormatPercent(maxPercent);
            ReferenceDateBox.Text = (referenceDate ?? DateTime.Today).ToString("d", CultureInfo.CurrentCulture);
            ActiveOnlyCheck.IsChecked = !minPercent.HasValue && maxPercent == 99;
            ApplyDateFilterMode(dateFilterMode);
            ApplyActiveOnlyState();
        }

        private static string FormatPercent(double? value) =>
            value.HasValue
                ? value.Value.ToString("0", CultureInfo.CurrentCulture)
                : string.Empty;

        private bool TryReadPercent(TextBox box, string label, out double? value)
        {
            value = null;
            var text = box.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (!double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed))
            {
                ErrorText.Text = AppStrings.Get("PctFilter_NotANumber", label);
                box.Focus();
                box.SelectAll();
                return false;
            }

            if (parsed < 0 || parsed > 100)
            {
                ErrorText.Text = AppStrings.Get("PctFilter_OutOfRange", label);
                box.Focus();
                box.SelectAll();
                return false;
            }

            value = Math.Round(parsed);
            return true;
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            MinPercent = null;
            MaxPercent = null;
            DateFilterMode = null;
            ReferenceDate = null;
            DialogResult = true;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            ErrorText.Text = string.Empty;

            if (ActiveOnlyCheck.IsChecked == true)
            {
                if (!TryReadDateFilter(out var dateMode, out var referenceDate))
                    return;

                MinPercent = null;
                MaxPercent = 99;
                DateFilterMode = dateMode;
                ReferenceDate = referenceDate;
                DialogResult = true;
                return;
            }

            if (!TryReadPercent(MinBox, AppStrings.Get("PctFilter_Min"), out var min) ||
                !TryReadPercent(MaxBox, AppStrings.Get("PctFilter_Max"), out var max))
                return;

            if (min.HasValue && max.HasValue && min.Value > max.Value)
            {
                ErrorText.Text = AppStrings.Get("PctFilter_MinGtMax");
                MinBox.Focus();
                MinBox.SelectAll();
                return;
            }

            MinPercent = min;
            MaxPercent = max;
            if (!TryReadDateFilter(out var selectedDateMode, out var selectedReferenceDate))
                return;

            DateFilterMode = selectedDateMode;
            ReferenceDate = selectedReferenceDate;
            DialogResult = true;
        }

        private void OnActiveOnlyChanged(object sender, RoutedEventArgs e) => ApplyActiveOnlyState();

        private void ApplyActiveOnlyState()
        {
            if (ActiveOnlyCheck.IsChecked == true)
            {
                MinBox.Text = string.Empty;
                MaxBox.Text = "99";
                MinBox.IsEnabled = false;
                MaxBox.IsEnabled = false;
                ErrorText.Text = string.Empty;
            }
            else
            {
                MinBox.IsEnabled = true;
                MaxBox.IsEnabled = true;
            }
        }

        private void ApplyDateFilterMode(string? mode)
        {
            DateAnyRadio.IsChecked = true;
            if (mode is "StartDate" or "StartToday")
                DateStartTodayRadio.IsChecked = true;
            else if (mode is "FinishDate" or "FinishToday")
                DateFinishTodayRadio.IsChecked = true;
        }

        private string? ReadDateFilterMode()
        {
            if (DateStartTodayRadio.IsChecked == true)
                return "StartDate";
            if (DateFinishTodayRadio.IsChecked == true)
                return "FinishDate";
            return null;
        }

        private bool TryReadDateFilter(out string? dateMode, out DateTime? referenceDate)
        {
            dateMode = ReadDateFilterMode();
            referenceDate = null;

            if (dateMode == null)
                return true;

            var text = ReferenceDateBox.Text?.Trim();
            if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
            {
                ErrorText.Text = AppStrings.Get("PctFilter_InvalidDate");
                ReferenceDateBox.Focus();
                ReferenceDateBox.SelectAll();
                return false;
            }

            referenceDate = parsed.Date;
            return true;
        }
    }
}
