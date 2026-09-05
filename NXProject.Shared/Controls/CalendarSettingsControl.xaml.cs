// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Controls
{
    public partial class CalendarSettingsControl : UserControl, INotifyPropertyChanged
    {
        private readonly string _storageKey;
        private readonly Project? _project;

        public CalendarSettingsControl(string storageKey = "NXProject.Community", Project? project = null)
        {
            InitializeComponent();
            _storageKey = storageKey;
            _project = project;

            GeneralCalendar = ProjectCalendarService.Load(storageKey);
            CalendarPath = ProjectCalendarService.GetCalendarPath(storageKey);

            // Calendário do cronograma: cópia do que está no projeto (ou vazio até incluir).
            ProjectCalendarModel = project?.Calendar != null
                ? ProjectCalendarService.Clone(project.Calendar)
                : new ProjectCalendar();
            HasProjectCalendar = project?.Calendar != null;

            DataContext = this;
            Loaded += (_, _) => UpdateProjectTabVisibility();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ProjectCalendar GeneralCalendar { get; private set; }
        public ProjectCalendar ProjectCalendarModel { get; private set; }
        public string CalendarPath { get; }

        private bool _hasProjectCalendar;
        public bool HasProjectCalendar
        {
            get => _hasProjectCalendar;
            private set { _hasProjectCalendar = value; OnPropertyChanged(); }
        }

        public event EventHandler? Saved;

        // ── aba Geral ────────────────────────────────────────────────────────
        private void OnAddTodayGeneralClick(object sender, RoutedEventArgs e)
        {
            GeneralCalendar.Holidays.Add(new ProjectHoliday { Date = DateTime.Today, Name = "Feriado" });
            UpdateCopyEnabled();
        }

        private void OnRemoveGeneralClick(object sender, RoutedEventArgs e)
        {
            if (GeneralGrid.SelectedItem is ProjectHoliday h)
                GeneralCalendar.Holidays.Remove(h);
            UpdateCopyEnabled();
        }

        // Exportar o calendário Geral para um arquivo (ex.: drive de rede compartilhado).
        private void OnExportGeneralClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = AppStringsSafe("Calendar_ExportTitle", "Exportar calendário"),
                Filter = ProjectCalendarService.FileFilter,
                FileName = "calendario-nxproject.nxcal"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ProjectCalendarService.ExportToFile(GeneralCalendar, dlg.FileName);
                MessageBox.Show(Window.GetWindow(this),
                    AppStringsSafe("Calendar_ExportedMsg", "Calendário exportado."),
                    AppStringsSafe("Calendar_TabGeneral", "Geral"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message,
                    AppStringsSafe("Calendar_TabGeneral", "Geral"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Importar um calendário de arquivo para o Geral (compartilhado entre times).
        private void OnImportGeneralClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppStringsSafe("Calendar_ImportTitle", "Importar calendário"),
                Filter = ProjectCalendarService.FileFilter
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                GeneralCalendar = ProjectCalendarService.ImportFromFile(dlg.FileName);
                OnPropertyChanged(nameof(GeneralCalendar));
                ProjectCalendarService.Save(GeneralCalendar, _storageKey);
                UpdateCopyEnabled();
                MessageBox.Show(Window.GetWindow(this),
                    AppStringsSafe("Calendar_ImportedMsg", "Calendário importado para o Geral."),
                    AppStringsSafe("Calendar_TabGeneral", "Geral"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message,
                    AppStringsSafe("Calendar_TabGeneral", "Geral"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Incluir/atualizar o calendário do cronograma a partir do Geral.
        private void OnIncludeInProjectClick(object sender, RoutedEventArgs e)
        {
            ProjectCalendarModel = ProjectCalendarService.Clone(GeneralCalendar);
            OnPropertyChanged(nameof(ProjectCalendarModel));
            HasProjectCalendar = true;
            UpdateProjectTabVisibility();
            MessageBox.Show(Window.GetWindow(this),
                AppStringsSafe("Calendar_IncludedMsg", "Calendário incluído no cronograma. Clique em Salvar para gravar no arquivo."),
                AppStringsSafe("Calendar_TabProject", "Cronograma"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── aba Cronograma ───────────────────────────────────────────────────
        private void OnAddTodayProjectClick(object sender, RoutedEventArgs e)
            => ProjectCalendarModel.Holidays.Add(new ProjectHoliday { Date = DateTime.Today, Name = "Feriado" });

        private void OnRemoveProjectClick(object sender, RoutedEventArgs e)
        {
            if (ProjectGrid.SelectedItem is ProjectHoliday h)
                ProjectCalendarModel.Holidays.Remove(h);
        }

        // Copiar o calendário do cronograma para o Geral (só quando o Geral está sem registros).
        private void OnCopyToGeneralClick(object sender, RoutedEventArgs e)
        {
            GeneralCalendar = ProjectCalendarService.Clone(ProjectCalendarModel);
            OnPropertyChanged(nameof(GeneralCalendar));
            ProjectCalendarService.Save(GeneralCalendar, _storageKey);
            UpdateCopyEnabled();
            MessageBox.Show(Window.GetWindow(this),
                AppStringsSafe("Calendar_CopiedToGeneralMsg", "Calendário copiado para o Geral (da máquina)."),
                AppStringsSafe("Calendar_TabGeneral", "Geral"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            // Geral: sempre grava na máquina.
            ProjectCalendarService.Save(GeneralCalendar, _storageKey);

            // Cronograma: grava no projeto (viaja no .nxp) e passa a ser o calendário ativo.
            if (_project != null)
            {
                if (HasProjectCalendar)
                {
                    _project.Calendar = ProjectCalendarService.Clone(ProjectCalendarModel);
                    ProjectCalendarService.SetCurrent(_project.Calendar);
                }
                else
                {
                    _project.Calendar = null; // usa o Geral (já definido como Current pelo Save)
                }
                _project.IsDirty = true;
            }

            Saved?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateProjectTabVisibility()
        {
            ProjectCalendarPanel.Visibility = HasProjectCalendar ? Visibility.Visible : Visibility.Collapsed;
            NoProjectCalendarMsg.Visibility = HasProjectCalendar ? Visibility.Collapsed : Visibility.Visible;
            UpdateCopyEnabled();
        }

        private void UpdateCopyEnabled()
        {
            // Só permite copiar para o Geral quando o Geral está sem registros de feriado.
            CopyToGeneralBtn.IsEnabled = GeneralCalendar.Holidays.Count == 0;
        }

        private static string AppStringsSafe(string key, string fallback)
        {
            var val = Application.Current?.TryFindResource(key) as string;
            return string.IsNullOrEmpty(val) ? fallback : val;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
