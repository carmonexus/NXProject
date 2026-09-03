using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Cadastro de ausências de uma pessoa (férias, folga, feriado municipal de outra cidade).
    /// Nesses dias ela não produz e o fim das atividades dela é empurrado.
    /// </summary>
    public partial class ResourceAbsenceWindow : Window
    {
        private readonly Resource _resource;
        private readonly ObservableCollection<ResourceAbsence> _items = new();

        public ResourceAbsenceWindow(Resource resource)
        {
            InitializeComponent();
            _resource = resource ?? throw new ArgumentNullException(nameof(resource));
            PersonText.Text = AppStrings.Get("Abs_For", _resource.Name);

            foreach (var a in (_resource.Absences ?? new List<ResourceAbsence>()).OrderBy(a => a.Date))
                _items.Add(new ResourceAbsence { Date = a.Date, Reason = a.Reason });
            Grid.ItemsSource = _items;
            NewDate.SelectedDate = DateTime.Today;
        }

        // Aceita um dia ou um intervalo (Data → Até). Dias repetidos são ignorados.
        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (NewDate.SelectedDate is not { } start)
            {
                MessageBox.Show(this, AppStrings.Get("Abs_PickDate"), Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var end = NewDateEnd.SelectedDate ?? start;
            if (end < start) (start, end) = (end, start);

            var reason = (NewReason.Text ?? string.Empty).Trim();
            var added = 0;
            for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                if (_items.Any(x => x.Date.Date == d)) continue;
                _items.Add(new ResourceAbsence { Date = d, Reason = reason });
                added++;
            }
            if (added > 0)
            {
                var ordered = _items.OrderBy(x => x.Date).ToList();
                _items.Clear();
                foreach (var x in ordered) _items.Add(x);
            }
            NewReason.Text = string.Empty;
            NewDateEnd.SelectedDate = null;
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            foreach (var item in Grid.SelectedItems.Cast<ResourceAbsence>().ToList())
                _items.Remove(item);
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit();
            _resource.Absences = _items.OrderBy(a => a.Date)
                .Select(a => new ResourceAbsence { Date = a.Date.Date, Reason = a.Reason ?? string.Empty })
                .ToList();
            DialogResult = true;
        }
    }
}
