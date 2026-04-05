using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXProject.Views
{
    public partial class CommunityMainWindow : Window
    {
        private static readonly string LicenseAcceptanceDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXProject.Community");

        private static readonly string LicenseAcceptanceFile =
            Path.Combine(LicenseAcceptanceDirectory, "license.accepted");

        private bool _licenseAccepted;
        private bool _allowClose;
        private bool _aiOpenedOnFirstAccess;

        public CommunityMainWindow()
        {
            InitializeComponent();
            StatusLogoImage.Source = ProtectedLogoProvider.GetLogoImage();
            var vm = new MainViewModel();
            DataContext = vm;
            var syncingVerticalScroll = false;

            TaskGridCtrl.VerticalScrollChanged += offset =>
            {
                if (syncingVerticalScroll) return;
                syncingVerticalScroll = true;
                GanttCtrl.SyncVerticalOffset(offset);
                syncingVerticalScroll = false;
            };

            GanttCtrl.VerticalScrollChanged += offset =>
            {
                if (syncingVerticalScroll) return;
                syncingVerticalScroll = true;
                TaskGridCtrl.SyncVerticalOffset(offset);
                syncingVerticalScroll = false;
            };

            TaskGridCtrl.HeaderHeightMeasured += h => GanttCtrl.SetHeaderHeight(h);
            TaskGridCtrl.RowTopsMeasured += tops => GanttCtrl.SetRowTops(tops);
            TaskGridCtrl.TaskMoveRequested += (sourceTask, targetTask, insertAfter) =>
            {
                if (vm.MoveTaskByDrop(sourceTask, targetTask, insertAfter))
                    GanttCtrl.ForceRender();
            };

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SelectedTask))
                    GanttCtrl.SelectedTask = vm.SelectedTask;
            };

            GanttCtrl.TaskClicked += task =>
            {
                vm.SelectedTask = task;
            };

            SubscribeTaskEvents(vm.FlatTasks);
            vm.FlatTasks.CollectionChanged += (_, args) =>
            {
                if (args.OldItems != null)
                {
                    foreach (var item in args.OldItems)
                        if (item is TaskViewModel task)
                            task.PropertyChanged -= OnTaskPropertyChanged;
                }

                if (args.NewItems != null)
                {
                    foreach (var item in args.NewItems)
                        if (item is TaskViewModel task)
                            task.PropertyChanged += OnTaskPropertyChanged;
                }

                if (args.Action == NotifyCollectionChangedAction.Reset)
                    SubscribeTaskEvents(vm.FlatTasks);

                if (args.Action == NotifyCollectionChangedAction.Add ||
                    args.Action == NotifyCollectionChangedAction.Remove ||
                    args.Action == NotifyCollectionChangedAction.Reset)
                {
                    GanttCtrl.ForceRender();
                }
            };

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.Project))
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => GanttCtrl.ScrollToProjectStart());
            };

            Loaded += OnCommunityWindowLoaded;
            Closing += OnCommunityWindowClosing;
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            var about = new CommunityAboutWindow
            {
                Owner = this
            };
            about.ShowDialog();
        }

        private void OnLicenseClick(object sender, RoutedEventArgs e)
        {
            ShowLicenseDialog(requireAcceptance: false);
        }

        private void OnAiAssistantClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var aiWindow = new CommunityAIWindow(vm)
            {
                Owner = this
            };
            aiWindow.ShowDialog();
        }

        private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(TaskViewModel.PredecessorsText))
                GanttCtrl.ForceRender();
        }

        private void SubscribeTaskEvents(System.Collections.Generic.IEnumerable<TaskViewModel> tasks)
        {
            foreach (var task in tasks)
            {
                task.PropertyChanged -= OnTaskPropertyChanged;
                task.PropertyChanged += OnTaskPropertyChanged;
            }
        }

        private void OnCommunityWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (_licenseAccepted)
                return;

            if (HasAcceptedLicense())
            {
                _licenseAccepted = true;
                OpenAiAssistantOnFirstAccess();
                return;
            }

            _licenseAccepted = ShowLicenseDialog(requireAcceptance: true);
            if (!_licenseAccepted)
            {
                Close();
                return;
            }

            OpenAiAssistantOnFirstAccess();
        }

        private bool ShowLicenseDialog(bool requireAcceptance)
        {
            var licenseWindow = new CommunityLicenseWindow
            {
                Owner = this
            };

            var accepted = licenseWindow.ShowDialog() == true;
            if (!accepted && !requireAcceptance)
                return false;

            if (accepted && requireAcceptance)
                PersistLicenseAcceptance();

            return accepted;
        }

        private static bool HasAcceptedLicense()
        {
            return File.Exists(LicenseAcceptanceFile);
        }

        private static void PersistLicenseAcceptance()
        {
            Directory.CreateDirectory(LicenseAcceptanceDirectory);
            File.WriteAllText(LicenseAcceptanceFile, "accepted");
        }

        private void OnCommunityWindowClosing(object? sender, CancelEventArgs e)
        {
            if (_allowClose)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            if (!vm.Project.IsDirty)
                return;

            var decision = MessageBox.Show(
                "O projeto possui alteracoes nao salvas. Deseja salvar antes de fechar?",
                "Salvar projeto",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (decision == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (decision == MessageBoxResult.Yes)
            {
                vm.SaveProjectCommand.Execute(null);
                if (vm.Project.IsDirty)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _allowClose = true;
        }

        private void OpenAiAssistantOnFirstAccess()
        {
            if (_aiOpenedOnFirstAccess || DataContext is not MainViewModel vm)
                return;

            _aiOpenedOnFirstAccess = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!IsLoaded || !IsVisible)
                    return;

                var aiWindow = new CommunityAIWindow(vm)
                {
                    Owner = this
                };
                aiWindow.ShowDialog();
            }));
        }
    }
}
