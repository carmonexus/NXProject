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
