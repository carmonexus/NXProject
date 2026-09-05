// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class TfsOnlineChildTasksWindow : Window
    {
        public sealed class ChildRow
        {
            public int TfsId { get; init; }          // >0 = online, 0 = pendente criação
            public string IdDisplay { get; init; } = "";
            public string Type { get; init; } = "";
            public string Name { get; set; } = "";
            public string State { get; init; } = "";
            public string Tags { get; init; } = "";
            public string Description { get; init; } = "";
            public string LastHistory { get; init; } = "";
            public bool IsPending { get; init; }
            // Referência à tarefa local para operações de edição/exclusão
            public ProjectTask? LocalTask { get; init; }
        }

        private readonly ProjectTask _parent;
        private readonly MainViewModel? _mainVm;
        private readonly ObservableCollection<ChildRow> _rows = new();
        private bool _changed;

        // Tipo de filho derivado do tipo do pai (Epic→Feature, Feature→Story, _→Task)
        private string ChildType => _parent.TfsType?.Trim() switch
        {
            "Epic"    => "Feature",
            "Feature" => "Story",
            _         => "Task"
        };

        /// <summary>Abre a lista vinda do TaskBoard: a origem e o ID do DevOps, nao uma atividade
        /// do cronograma. Sem cronograma por tras, a tela fica somente leitura (nao cria nem exclui
        /// atividade local) e serve para ver TODAS as filhas da Story — inclusive as de outras
        /// pessoas e as que nem estao no cronograma aberto.</summary>
        public static TfsOnlineChildTasksWindow FromTaskBoard(int tfsId, string title, string tfsType = "Story") =>
            new(new ProjectTask { TfsId = tfsId, Name = title ?? "", TfsType = tfsType }, mainVm: null);

        public TfsOnlineChildTasksWindow(ProjectTask parent, MainViewModel? mainVm = null)
        {
            InitializeComponent();
            _parent = parent;
            _mainVm = mainVm;

            ItemsGrid.ItemsSource = _rows;
            TitleText.Text = AppStrings.Get("Online_TitleFormat", parent.TfsId ?? 0, parent.Name ?? "");
            StatusText.Text = AppStrings.Get("Online_Loading");

            Loaded += async (_, _) =>
            {
                if (_mainVm == null)
                {
                    // Modo somente leitura: oculta barra de criação
                    var addBar = (System.Windows.Controls.Border)((System.Windows.Controls.Grid)Content).Children[2];
                    addBar.Visibility = Visibility.Collapsed;
                }
                await LoadAsync();
            };
        }

        private async Task LoadAsync()
        {
            try
            {
                var options = TfsConnectionStore.Load("NXProject.Community");
                var online = await TfsImportService.LoadOnlineChildTasksAsync(options, _parent.TfsId!.Value);

                _rows.Clear();

                foreach (var r in online)
                    _rows.Add(new ChildRow
                    {
                        TfsId       = r.Id,
                        IdDisplay   = r.IdText,
                        Type        = r.Type,
                        Name        = r.Name,
                        State       = r.State,
                        Tags        = r.Tags,
                        Description = r.Description,
                        LastHistory = r.LastHistory,
                        IsPending   = false
                    });

                // Filhos locais pendentes de criação (IsPendingTfsCreate ou TfsId == 0)
                foreach (var local in _parent.Children.Where(c => c.IsPendingTfsCreate || c.TfsId == 0))
                    _rows.Add(new ChildRow
                    {
                        TfsId       = 0,
                        IdDisplay   = AppStrings.Get("Online_Pending"),
                        Type        = local.TfsType ?? ChildType,
                        Name        = local.Name,
                        State       = local.TfsState ?? "New",
                        IsPending   = true,
                        LocalTask   = local
                    });

                StatusText.Text = _rows.Count == 1
                    ? AppStrings.Get("Online_OneFound")
                    : AppStrings.Get("Online_CountFound", _rows.Count, _rows.Count(r => r.IsPending));
            }
            catch (Exception ex)
            {
                StatusText.Text = AppStrings.Get("Online_FetchError", ex.Message);
            }
        }

        private void OnAddPendingClick(object sender, RoutedEventArgs e)
        {
            if (_mainVm == null) return;
            var name = NewNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(AppStrings.Get("Online_NameRequired"), AppStrings.Get("Online_NameRequiredTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK,
                    MessageBoxOptions.None);
                NewNameBox.Focus();
                return;
            }

            var task = new ProjectTask
            {
                Id     = _mainVm.NextId(),
                Name   = name,
                Start  = _parent.Start,
                Finish = ProjectCalendarService.AddWorkingHours(_parent.Start, ProjectCalendarService.WorkingHoursPerDay * 3.0),
                Level  = _parent.Level + 1,
                Parent = _parent,
                TfsType          = ChildType,
                TfsIterationPath = _parent.TfsIterationPath,
                TfsId            = 0,
                TfsState         = "New"
            };

            _parent.Children.Add(task);
            _parent.IsSummary = true;
            _parent.RecalcSummary();
            if (string.Equals(ChildType, "Task", StringComparison.OrdinalIgnoreCase))
                _parent.DevopsTaskCount = (_parent.DevopsTaskCount ?? 0) + 1;
            _mainVm.Project.IsDirty = true;
            _mainVm.RebuildFlatTasks();
            _changed = true;

            _rows.Add(new ChildRow
            {
                TfsId     = 0,
                IdDisplay = AppStrings.Get("Online_Pending"),
                Type      = ChildType,
                Name      = name,
                State     = "New",
                IsPending = true,
                LocalTask = task
            });

            NewNameBox.Clear();
            StatusText.Text = AppStrings.Get("Online_AddedPending", name);
        }

        private void OnEditPendingClick(object sender, RoutedEventArgs e)
        {
            if (_mainVm == null) return;
            if ((sender as FrameworkElement)?.Tag is not ChildRow row || !row.IsPending || row.LocalTask == null)
                return;

            var dialog = new Window
            {
                Title = AppStrings.Get("Online_EditPendingTitle"),
                Width = 420, Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = AppStrings.Get("Online_NameLabel"), Margin = new Thickness(0, 0, 0, 4) });
            var tb = new System.Windows.Controls.TextBox { Text = row.Name, Height = 26, Padding = new Thickness(4, 0, 4, 0) };
            panel.Children.Add(tb);
            var btnPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var btnOk = new System.Windows.Controls.Button { Content = AppStrings.Get("Online_Save"), Width = 80, Height = 28, IsDefault = true };
            var btnCancel = new System.Windows.Controls.Button { Content = AppStrings.Get("Online_Cancel"), Width = 80, Height = 28, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            btnOk.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            panel.Children.Add(btnPanel);
            dialog.Content = panel;

            if (dialog.ShowDialog() != true) return;
            var newName = tb.Text.Trim();
            if (string.IsNullOrEmpty(newName)) return;

            row.LocalTask.Name = newName;
            _mainVm.Project.IsDirty = true;
            _mainVm.RebuildFlatTasks();
            _changed = true;

            // Atualiza a linha na coleção (recria pois ChildRow.Name pode ser property set)
            var idx = _rows.IndexOf(row);
            if (idx >= 0)
            {
                _rows[idx] = new ChildRow
                {
                    TfsId     = 0,
                    IdDisplay = AppStrings.Get("Online_Pending"),
                    Type      = row.Type,
                    Name      = newName,
                    State     = row.State,
                    IsPending = true,
                    LocalTask = row.LocalTask
                };
            }
            StatusText.Text = AppStrings.Get("Online_Renamed", newName);
        }

        private void OnDeletePendingClick(object sender, RoutedEventArgs e)
        {
            if (_mainVm == null) return;
            if ((sender as FrameworkElement)?.Tag is not ChildRow row || !row.IsPending || row.LocalTask == null)
                return;

            var result = MessageBox.Show(
                AppStrings.Get("Online_DeleteConfirm", row.Name),
                AppStrings.Get("Online_DeleteConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _parent.Children.Remove(row.LocalTask);
            if (_parent.Children.Count == 0)
                _parent.IsSummary = false;
            _parent.RecalcSummary();
            _mainVm.Project.IsDirty = true;
            _mainVm.RebuildFlatTasks();
            _changed = true;

            _rows.Remove(row);
            StatusText.Text = AppStrings.Get("Online_Deleted", row.Name);
        }

        private void OnNewNameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                OnAddPendingClick(sender, e);
        }

        public bool HasChanges => _changed;
    }
}
