using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class CostDrillDownWindow : Window
    {
        public sealed class StoryRow
        {
            public string FeatureName  { get; init; } = "";
            public string StoryName    { get; init; } = "";
            public string ResourceName { get; init; } = "";
            public string HoursText    { get; init; } = "";
            public string CostText     { get; init; } = "";
            public Brush  CostFg       { get; init; } = Brushes.Black;
            public Brush  RowBg        { get; init; } = Brushes.White;
        }

        public CostDrillDownWindow(string resource, string epic, string feature, bool isCapex,
            int? year, int? month, List<ResourceCostLine> lines)
        {
            InitializeComponent();

            // Título
            string tipo = isCapex ? "CAPEX" : "OPEX";
            string periodo = year.HasValue
                ? new DateTime(year.Value, month!.Value, 1).ToString("MMMM/yyyy")
                : AppStrings.Get("CostDD_TotalPeriod");

            TitleText.Text    = AppStrings.Get("CostDD_TitleFormat", resource, feature, tipo);
            SubtitleText.Text = AppStrings.Get("CostDD_SubtitleFormat", epic, periodo);

            // Agrupa por story (mesma story pode ter linhas em vários meses)
            var grouped = lines
                .GroupBy(l => (l.FeatureName, l.StoryName, l.ResourceName))
                .OrderBy(g => g.Key.FeatureName)
                .ThenBy(g => g.Key.StoryName)
                .ToList();

            var costFg = isCapex
                ? new SolidColorBrush(Color.FromRgb(120, 60, 10))
                : new SolidColorBrush(Color.FromRgb(20, 100, 20));

            var rows = new List<StoryRow>();
            bool alt = false;
            foreach (var g in grouped)
            {
                double totalHours = g.Sum(l => l.Hours);
                decimal totalCost = g.Sum(l => l.Cost);
                var bg = alt
                    ? new SolidColorBrush(Color.FromRgb(248, 251, 255))
                    : Brushes.White;
                rows.Add(new StoryRow
                {
                    FeatureName  = g.Key.FeatureName,
                    StoryName    = g.Key.StoryName,
                    ResourceName = g.Key.ResourceName,
                    HoursText    = totalHours > 0 ? totalHours.ToString("N1") : "–",
                    CostText     = totalCost  > 0 ? totalCost.ToString("N2", new CultureInfo("pt-BR")) : "–",
                    CostFg       = costFg,
                    RowBg        = bg,
                });
                alt = !alt;
            }

            StoryList.ItemsSource = rows;

            double sumH = lines.Sum(l => l.Hours);
            decimal sumC = lines.Sum(l => l.Cost);
            TotalHours.Text = sumH > 0 ? sumH.ToString("N1") : "–";
            TotalCost.Text  = sumC > 0 ? sumC.ToString("N2", new CultureInfo("pt-BR")) : "–";
        }
    }
}
