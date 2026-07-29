using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NXProject.Models;
using NXProject.ViewModels;
using NXProject.Views;

namespace NXProject.Controls
{
    public partial class TaskGridControl : UserControl
    {
        public static readonly DependencyProperty TasksProperty =
            DependencyProperty.Register(nameof(Tasks), typeof(ObservableCollection<TaskViewModel>),
                typeof(TaskGridControl), new PropertyMetadata(null, OnTasksChanged));

        public ObservableCollection<TaskViewModel>? Tasks
        {
            get => (ObservableCollection<TaskViewModel>?)GetValue(TasksProperty);
            set
            {
                SetValue(TasksProperty, value);
                // Resetar cache quando tasks mudam, para forçar re-cálculo de layout
                _lastRowLayoutSignature = null;
            }
        }

        public static readonly DependencyProperty NameTextFontSizeProperty =
            DependencyProperty.Register(nameof(NameTextFontSize), typeof(double),
                typeof(TaskGridControl), new PropertyMetadata(11.0));

        public double NameTextFontSize
        {
            get => (double)GetValue(NameTextFontSizeProperty);
            set => SetValue(NameTextFontSizeProperty, value);
        }

        public void RefreshRows() => TaskGrid.Items.Refresh();

        public void ScrollToSelected()
        {
            var selected = TaskGrid.SelectedItem;
            if (selected == null) return;

            // Se o item já está totalmente visível, não faz nada.
            if (IsItemFullyVisible(selected)) return;

            // Em listas grandes, ScrollIntoView seguido de centralização gera dois saltos visíveis.
            // Como as linhas têm altura fixa, calculamos o destino antes e rolamos uma vez só.
            if (ScrollNearItemCenter(selected))
            {
                TaskGrid.UpdateLayout();
                if (IsItemFullyVisible(selected))
                    return;
            }

            // Fallback para realizar o container virtualizado quando a estimativa não bastar.
            TaskGrid.ScrollIntoView(selected);
            TaskGrid.UpdateLayout();
        }

        private bool IsItemFullyVisible(object item)
        {
            if (_scrollViewer == null || _scrollViewer.ViewportHeight <= 0) return false;

            // Container realizado: posição exata via transform.
            var row = TaskGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            if (row != null)
            {
                try
                {
                    var transform = row.TransformToAncestor(_scrollViewer);
                    var rect = transform.TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
                    return rect.Top >= 0 && rect.Bottom <= _scrollViewer.ViewportHeight;
                }
                catch { }
            }

            // Container ainda não realizado (virtualização): estima pela posição do índice.
            var index = TaskGrid.Items.IndexOf(item);
            if (index < 0) return false;
            var rowHeight = TaskGrid.RowHeight;
            if (double.IsNaN(rowHeight) || rowHeight <= 0) rowHeight = 22.0;
            var itemTop    = index * rowHeight - _scrollViewer.VerticalOffset;
            var itemBottom = itemTop + rowHeight;
            return itemTop >= 0 && itemBottom <= _scrollViewer.ViewportHeight;
        }

        private bool ScrollNearItemCenter(object item)
        {
            if (_scrollViewer == null || _scrollViewer.ViewportHeight <= 0)
                return false;

            var index = TaskGrid.Items.IndexOf(item);
            if (index < 0)
                return false;

            var rowHeight = TaskGrid.RowHeight;
            if (double.IsNaN(rowHeight) || rowHeight <= 0)
                rowHeight = 22.0;

            var targetOffset = (index * rowHeight)
                - (_scrollViewer.ViewportHeight / 2)
                + (rowHeight / 2);
            targetOffset = Math.Max(0, targetOffset);
            if (_scrollViewer.ScrollableHeight > 0)
                targetOffset = Math.Min(targetOffset, _scrollViewer.ScrollableHeight);

            _scrollViewer.ScrollToVerticalOffset(targetOffset);
            return true;
        }

        public static readonly DependencyProperty AvailableSprintsProperty =
            DependencyProperty.Register(nameof(AvailableSprints), typeof(ObservableCollection<Sprint>),
                typeof(TaskGridControl), new PropertyMetadata(null));

        /// <summary>Sprints disponíveis para escolher na coluna Sprint (vindas do DevOps).</summary>
        public ObservableCollection<Sprint>? AvailableSprints
        {
            get => (ObservableCollection<Sprint>?)GetValue(AvailableSprintsProperty);
            set => SetValue(AvailableSprintsProperty, value);
        }

        public static readonly DependencyProperty AvailableResourcesProperty =
            DependencyProperty.Register(nameof(AvailableResources), typeof(ObservableCollection<Resource>),
                typeof(TaskGridControl), new PropertyMetadata(null));

        /// <summary>Recursos disponíveis para atribuição nas tarefas.</summary>
        public ObservableCollection<Resource>? AvailableResources
        {
            get => (ObservableCollection<Resource>?)GetValue(AvailableResourcesProperty);
            set => SetValue(AvailableResourcesProperty, value);
        }

        /// <summary>Disparado quando o usuário escolhe outra sprint (ou "(sem sprint)" = null) para uma tarefa.</summary>
        public event Action<TaskViewModel, Sprint?>? TaskSprintChangeRequested;

        private TaskViewModel? _editSnapshotTask;
        private DataGridColumn? _editSnapshotColumn;
        private string _editSnapshotValue = string.Empty;
        private bool _suppressEditorLostFocusCommit;
        private bool _taskIdClickInProgress;


        public static readonly DependencyProperty ShowOriginalHoursColumnProperty =
            DependencyProperty.Register(nameof(ShowOriginalHoursColumn), typeof(bool),
                typeof(TaskGridControl), new PropertyMetadata(false, OnShowOriginalHoursColumnChanged));

        public bool ShowOriginalHoursColumn
        {
            get => (bool)GetValue(ShowOriginalHoursColumnProperty);
            set => SetValue(ShowOriginalHoursColumnProperty, value);
        }

        private static void OnShowOriginalHoursColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TaskGridControl ctrl)
                ctrl.OriginalHoursColumn.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        // Tipos DevOps que têm campo de classificação configurado.
        // "*" = todos os tipos. Populado pela janela principal com base nos TypeFieldMappings.
        private HashSet<string> _classificationTypes = new(StringComparer.OrdinalIgnoreCase);

        public void SetClassificationTypes(IEnumerable<string> types)
            => _classificationTypes = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);

        private bool HasClassificationForType(string? tfsType)
        {
            if (_classificationTypes.Count == 0) return false;
            if (_classificationTypes.Contains("*") || _classificationTypes.Contains("Todos")) return true;
            return !string.IsNullOrEmpty(tfsType) && _classificationTypes.Contains(tfsType);
        }

        /// <summary>Disparado quando o usuário alterna o modo de visualização do Gantt (original/restante).</summary>
        public event Action? GanttViewToggled;

        /// <summary>Disparado quando o DataGrid rola verticalmente.</summary>
        public event Action<double>? VerticalScrollChanged;

        /// <summary>Disparado quando a altura real do header do DataGrid e conhecida.</summary>
        public event Action<double>? HeaderHeightMeasured;

        /// <summary>Disparado quando as linhas reais do DataGrid mudam de posição.</summary>
        public event Action<IReadOnlyList<double>>? RowTopsMeasured;
        public event Action<TaskViewModel, TaskViewModel, bool>? TaskMoveRequested;

        /// <summary>Disparado quando o usuário clica no ID de uma tarefa (editar vínculo DevOps).</summary>
        public event Action<TaskViewModel>? TaskIdClicked;

        /// <summary>Disparado quando o usuário quer destacar as predecessoras de uma tarefa no Gantt.</summary>
        public event Action<TaskViewModel>? HighlightPredecessorsRequested;

        /// <summary>Disparado quando o usuário quer editar o % de alocação de uma tarefa via menu de contexto.</summary>
        public event Action<TaskViewModel>? EditPercAlocRequested;

        /// <summary>Disparado quando o usuário quer alterar a classificação (picklist DevOps) de uma tarefa.</summary>
        public event Action<TaskViewModel>? EditClassificationRequested;

        /// <summary>Disparado quando o usuário quer configurar o mapeamento de classificação.</summary>
        public event Action? ConfigureClassificationRequested;

        /// <summary>Disparado quando o usuário clica em "Ver Atividades Online..." no menu de contexto do nome.</summary>
        public event Action<TaskViewModel>? ViewOnlineChildrenRequested;

        /// <summary>Disparado quando o usuário clica em "Editar Descrição..." no menu de contexto do nome.</summary>
        public event Action<TaskViewModel>? EditDescriptionRequested;

        /// <summary>Disparado quando o usuário quer resolver manualmente um conflito de sincronização.</summary>
        public event Action<TaskViewModel>? ResolveManualConflictRequested;

        /// <summary>Disparado quando o usuário clica em "Atualizar duração pelas Tasks" no menu da coluna Duração.</summary>
        public event Action<TaskViewModel>? FetchTaskHoursRequested;

        /// <summary>Disparado antes de aplicar manualmente 100% na coluna % Compl.</summary>
        public event Func<TaskViewModel, double, System.Threading.Tasks.Task<bool>>? ManualPercentCompleteCommitRequested;

        /// <summary>Disparado quando o usuário clica em "Buscar Tasks (DevOps)" no menu do nome da tarefa.</summary>
        public event Action<TaskViewModel>? FetchChildTasksRequested;
        public event Action<TaskViewModel>? TksClickRequested;
        public event Action<TaskViewModel>? ExpandChildTasksRequested;

        /// <summary>Disparado quando o usuário clica em "Suprimir Tasks do cronograma".</summary>
        public event Action<TaskViewModel>? SuppressChildTasksRequested;

        /// <summary>Disparado quando o usuário clica em "Liberar Atividade" (story vira folha editável).</summary>
        public event Action<TaskViewModel>? ReleaseStoryRequested;

        /// <summary>Disparado quando o usuário clica em "Nova Task (DevOps)".</summary>
        public event Action<TaskViewModel>? AddDevOpsTaskRequested;
        /// <summary>Disparado quando o usuário clica em "Nova Atividade Interna".</summary>
        public event Action<TaskViewModel>? AddInternalTaskRequested;

        private bool _headerMeasured;
        private ScrollViewer? _scrollViewer;
        private bool _suppressScrollNotification;
        private double? _pendingVerticalOffset;
        private string? _lastRowLayoutSignature;
        private Point _dragStartPoint;
        private TaskViewModel? _dragSourceTask;

        public TaskGridControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            TaskGrid.LayoutUpdated += OnLayoutUpdated;
        }

        /// <summary>Altera a altura do cabeçalho do DataGrid para sincronizar com o Gantt (ex: modo Dia = 60px).</summary>
        public void SetColumnHeaderHeight(double height)
        {
            TaskGrid.ColumnHeaderHeight = height;
            _headerMeasured = false; // permite re-publicar após o resize
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            var header = FindChild<DataGridColumnHeadersPresenter>(TaskGrid);
            if (header == null || header.ActualHeight == 0) return;

            if (!_headerMeasured)
            {
                _headerMeasured = true;
                HeaderHeightMeasured?.Invoke(header.ActualHeight);
                // Publicar RowTops apenas na primeira vez que o header é medido
                PublishRowTops(header.ActualHeight);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = FindChild<ScrollViewer>(TaskGrid);
            if (_scrollViewer == null) return;

            // Registra com handledEventsToo=true para garantir que o Ctrl+Click
            // na coluna de data de início funcione mesmo quando CanContentScroll=False
            // faz o DataGrid marcar PreviewMouseLeftButtonDown como handled internamente.
            TaskGrid.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnTaskGridPreviewMouseLeftButtonDown),
                handledEventsToo: true);

            _scrollViewer.ScrollChanged += (_, args) =>
            {
                if (_suppressScrollNotification) return;
                if (args.VerticalChange != 0 || args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0)
                {
                    VerticalScrollChanged?.Invoke(_scrollViewer.VerticalOffset);
                    var hdr = FindChild<DataGridColumnHeadersPresenter>(TaskGrid);
                    if (hdr != null && hdr.ActualHeight > 0)
                        PublishRowTops(hdr.ActualHeight);
                }
            };
        }

        public void SyncVerticalOffset(double offset)
        {
            if (_scrollViewer == null) return;

            var wasSuppressed = _suppressScrollNotification;
            _suppressScrollNotification = true;
            try
            {
                _scrollViewer.ScrollToVerticalOffset(offset);
            }
            finally
            {
                _suppressScrollNotification = wasSuppressed;
            }
        }

        public void ResetVerticalOffset()
        {
            _pendingVerticalOffset = null;
            if (_scrollViewer == null) return;

            var wasSuppressed = _suppressScrollNotification;
            _suppressScrollNotification = true;
            try
            {
                _scrollViewer.ScrollToVerticalOffset(0);
            }
            finally
            {
                _suppressScrollNotification = wasSuppressed;
            }
        }

        public void PreserveVerticalOffsetOnNextReset()
        {
            if (_scrollViewer != null)
            {
                _pendingVerticalOffset = _scrollViewer.VerticalOffset;
                _suppressScrollNotification = true;
            }
        }

        // Colunas visíveis por padrão em cada modo
        private static readonly HashSet<string> DefaultVisibleColumns = new()
        {
            "ID", "T·E (DevOps)", "Dur.(h)", "Início", "Fim", "% Compl.", "Recursos", "Sprint"
        };
        private static readonly HashSet<string> DefaultVisibleColumnsExpanded = new()
        {
            "ID", "T·E (DevOps)", "Dur.(h)", "DU", "OrgH", "HH Atual", "HH Restante",
            "Início", "Fim", "% Compl.", "Predecessoras", "Recursos", "Sprint", "TKs"
        };

        /// <summary>Disparado quando o usuário salva as configurações de colunas. Args: (hiddenDefault, hiddenExpanded).</summary>
        public event Action<string, string>? ColumnSettingsSaved;

        private bool _hasCustomColumnConfig;
        private bool _isExpandedMode;

        public void ApplyHiddenColumns(string hiddenDefault, string hiddenExpanded, bool expanded)
        {
            _isExpandedMode = expanded;
            var csv = expanded ? hiddenExpanded : hiddenDefault;
            var defaults = expanded ? DefaultVisibleColumnsExpanded : DefaultVisibleColumns;

            _hasCustomColumnConfig = !string.IsNullOrWhiteSpace(csv);

            HashSet<string> hidden;
            if (!_hasCustomColumnConfig)
                hidden = new HashSet<string>(GetCustomizableColumns().Select(x => x.Label).Where(l => !defaults.Contains(l)));
            else if (csv.Trim() == "~custom~")
                hidden = [];
            else
                hidden = new HashSet<string>(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            foreach (var (label, col) in GetCustomizableColumns())
                col.Visibility = hidden.Contains(label) ? Visibility.Collapsed : Visibility.Visible;

            var orgHHidden = hidden.Contains("OrgH");
            if (ShowOriginalHoursColumn == orgHHidden)
                ShowOriginalHoursColumn = !orgHHidden;
        }

        private IEnumerable<(string Label, DataGridColumn Col)> GetCustomizableColumns() =>
        [
            ("ID",            IdColumn),
            ("T·E (DevOps)",  DevOpsColumn),
            ("Dur.(h)",       DurationColumn),
            ("SFP",           SfpColumn),
            ("OrgH",          OriginalHoursColumn),
            ("HH Atual",      RealizedHoursColumn),
            ("HH Restante",   EstimatedHoursColumn),
            ("Início",        StartColumn),
            ("Fim",           FinishColumn),
            ("TKs",           TksColumn),
            ("% Compl.",      PercentColumn),
            ("Predecessoras", PredecessorColumn),
            ("Recursos",      ResourcesColumn),
            ("Sprint",        SprintColumn),
            ("DU",            WorkingDaysColumn),
        ];

        public void ShowColumnCustomizer(string hiddenDefault, string hiddenExpanded)
        {
            var cols = GetCustomizableColumns().ToList();

            // Reconstrói os sets de ocultas para cada modo a partir das strings salvas
            HashSet<string> ToHiddenSet(string csv, HashSet<string> defaults) =>
                string.IsNullOrWhiteSpace(csv) ? new HashSet<string>(cols.Select(x => x.Label).Where(l => !defaults.Contains(l)))
                : csv.Trim() == "~custom~" ? []
                : new HashSet<string>(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var hiddenSetDefault  = ToHiddenSet(hiddenDefault,  DefaultVisibleColumns);
            var hiddenSetExpanded = ToHiddenSet(hiddenExpanded, DefaultVisibleColumnsExpanded);

            var outerPanel = new StackPanel { Margin = new Thickness(16) };

            // Dois grupos lado a lado
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var panelDefault  = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            var panelExpanded = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(panelDefault, 0);
            Grid.SetColumn(panelExpanded, 1);
            grid.Children.Add(panelDefault);
            grid.Children.Add(panelExpanded);

            void AddHeader(Panel p, string text) => p.Children.Add(new TextBlock
            {
                Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8), Foreground = new SolidColorBrush(Color.FromRgb(30, 78, 161))
            });

            AddHeader(panelDefault,  "Visão Padrão");
            AddHeader(panelExpanded, "Visão Expandida");

            var checksDefault  = new List<(CheckBox cb, string label)>();
            var checksExpanded = new List<(CheckBox cb, string label)>();

            foreach (var (label, _) in cols)
            {
                var cbD = new CheckBox { Content = label, IsChecked = !hiddenSetDefault.Contains(label),  Margin = new Thickness(0, 3, 0, 3), FontSize = 12 };
                var cbE = new CheckBox { Content = label, IsChecked = !hiddenSetExpanded.Contains(label), Margin = new Thickness(0, 3, 0, 3), FontSize = 12 };
                panelDefault.Children.Add(cbD);
                panelExpanded.Children.Add(cbE);
                checksDefault.Add((cbD, label));
                checksExpanded.Add((cbE, label));
            }

            outerPanel.Children.Add(grid);

            // Botões
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var btnRestore = new Button { Content = "Restaurar Padrão", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            var btnSave    = new Button { Content = "Salvar", Width = 80, Padding = new Thickness(0, 6, 0, 6), IsDefault = true };
            btnPanel.Children.Add(btnRestore);
            btnPanel.Children.Add(btnSave);
            outerPanel.Children.Add(btnPanel);

            var win = new Window
            {
                Title = "Customizar colunas",
                Content = new ScrollViewer { Content = outerPanel },
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this)
            };

            btnRestore.Click += (_, _) =>
            {
                foreach (var (cb, label) in checksDefault)  cb.IsChecked = DefaultVisibleColumns.Contains(label);
                foreach (var (cb, label) in checksExpanded) cb.IsChecked = DefaultVisibleColumnsExpanded.Contains(label);
            };

            btnSave.Click += (_, _) =>
            {
                string BuildCsv(List<(CheckBox cb, string label)> checks) {
                    var h = checks.Where(x => x.cb.IsChecked != true).Select(x => x.label).ToList();
                    return h.Count > 0 ? string.Join(",", h) : "~custom~";
                }
                var savedDefault  = BuildCsv(checksDefault);
                var savedExpanded = BuildCsv(checksExpanded);
                ColumnSettingsSaved?.Invoke(savedDefault, savedExpanded);

                // Aplica imediatamente ao modo atual
                ApplyHiddenColumns(savedDefault, savedExpanded, _isExpandedMode);
                win.Close();
            };

            win.ShowDialog();
        }

        public void SetPresentationMode(bool expanded, string hiddenDefault = "", string hiddenExpanded = "")
        {
            ApplyHiddenColumns(hiddenDefault, hiddenExpanded, expanded);

            IdColumn.Width = new DataGridLength(expanded ? 100 : 63);
            DevOpsColumn.Width = new DataGridLength(expanded ? 30 : 26);
            NameTextFontSize = 11.0;
            NameColumn.MinWidth = expanded ? 280 : 210;
            NameColumn.Width = expanded
                ? new DataGridLength(2.2, DataGridLengthUnitType.Star)
                : new DataGridLength(1, DataGridLengthUnitType.Star);
            DurationColumn.Width = new DataGridLength(expanded ? 72 : 52);
            SfpColumn.Width = new DataGridLength(expanded ? 56 : 52);
            OriginalHoursColumn.Width = new DataGridLength(expanded ? 58 : 50);
            RealizedHoursColumn.Width = new DataGridLength(expanded ? 72 : 80);
            EstimatedHoursColumn.Width = new DataGridLength(expanded ? 80 : 80);
            StartColumn.Width = new DataGridLength(expanded ? 124 : 90);
            FinishColumn.Width = new DataGridLength(expanded ? 118 : 76);
            // DU (dias uteis) e coluna customizavel: visivel por padrao na visao completa,
            // oculta na inicial; a visibilidade e definida por ApplyHiddenColumns.
            PercentColumn.Width = new DataGridLength(expanded ? 88 : 62);
            PredecessorColumn.Width = new DataGridLength(expanded ? 111 : 80);
            ResourcesColumn.Width = new DataGridLength(expanded ? 160 : 94);
            SprintColumn.Width = new DataGridLength(expanded ? 148 : 115);
        }

        public void RefreshColumnHeaders()
        {
            string R(string key) => Application.Current?.TryFindResource(key) as string ?? key;
            NameColumn.Header       = R("Col_TaskName");
            DurationColumn.Header   = R("Col_Duration");
            OriginalHoursColumn.Header  = R("Col_OrgH");
            RealizedHoursColumn.Header  = R("Col_HHAtual");
            EstimatedHoursColumn.Header = R("Col_HHRestante");
            StartColumn.Header      = R("Col_Start");
            FinishColumn.Header     = R("Col_Finish");
            PercentColumn.Header    = R("Col_PercentComplete");
            PredecessorColumn.Header = R("Col_Predecessors");
            ResourcesColumn.Header  = R("Col_Resources");
        }

        public void SetPrintMode()
        {
            TaskGrid.EnableRowVirtualization = false;
            TaskGrid.EnableColumnVirtualization = false;
            VirtualizingPanel.SetIsVirtualizing(TaskGrid, false);
            ScrollViewer.SetVerticalScrollBarVisibility(TaskGrid, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(TaskGrid, ScrollBarVisibility.Disabled);
        }

        public void FocusSelectedTask()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (TaskGrid.Items.Count == 0)
                    return;

                var item = TaskGrid.SelectedItem ?? TaskGrid.Items[0];
                if (item == null) return;

                TaskGrid.SelectedItem = item;

                if (IsItemFullyVisible(item))
                {
                    // Item já visível: só foca sem mexer no scroll.
                    // Setar CurrentCell aqui dispararia scroll automático do DataGrid.
                    var visRow = TaskGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                    if (visRow != null) { visRow.Focus(); Keyboard.Focus(visRow); }
                    else { TaskGrid.Focus(); Keyboard.Focus(TaskGrid); }
                    return;
                }

                TaskGrid.ScrollIntoView(item);
                TaskGrid.CurrentCell = new DataGridCellInfo(item, NameColumn);
                TaskGrid.UpdateLayout();

                var row = TaskGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row != null)
                {
                    var cell = FindCell(row, NameColumn);
                    if (cell != null)
                    {
                        cell.Focus();
                        Keyboard.Focus(cell);
                        return;
                    }
                }

                TaskGrid.Focus();
                Keyboard.Focus(TaskGrid);
            }));
        }

        private void RefreshGridPreservingSelection(object? selectedItem, DataGridColumn? selectedColumn)
        {
            selectedItem ??= TaskGrid.SelectedItem;
            selectedColumn ??= TaskGrid.CurrentCell.Column ?? NameColumn;

            // Encerra qualquer transação ativa (EditItem ou AddNew) antes de chamar Refresh.
            TaskGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            if (TaskGrid.Items is System.ComponentModel.IEditableCollectionView ecv)
            {
                if (ecv.IsAddingNew) ecv.CancelNew();
                else if (ecv.IsEditingItem) ecv.CommitEdit();
            }
            TaskGrid.Items.Refresh();

            if (selectedItem == null || !TaskGrid.Items.Contains(selectedItem))
                return;

            TaskGrid.SelectedItem = selectedItem;
            if (!IsItemFullyVisible(selectedItem))
                TaskGrid.ScrollIntoView(selectedItem, selectedColumn);
            TaskGrid.CurrentCell = new DataGridCellInfo(selectedItem, selectedColumn);
            TaskGrid.UpdateLayout();

            if (TaskGrid.ItemContainerGenerator.ContainerFromItem(selectedItem) is not DataGridRow row)
                return;

            row.IsSelected = true;
            var cell = FindCell(row, selectedColumn);
            if (cell != null)
            {
                cell.Focus();
                Keyboard.Focus(cell);
            }
        }

        private static DataGridCell? FindCell(DataGridRow row, DataGridColumn column)
        {
            var presenter = FindChild<DataGridCellsPresenter>(row);
            if (presenter == null) return null;

            var index = column.DisplayIndex;
            return presenter.ItemContainerGenerator.ContainerFromIndex(index) as DataGridCell;
        }

        private void PublishRowTops(double headerHeight)
        {
            var itemCount = TaskGrid.Items.Count;
            if (itemCount == 0) return;

            var rowTops = new double[itemCount];
            var measuredAnyRow = false;

            var scrollOffset = _scrollViewer?.VerticalOffset ?? 0;
            for (int i = 0; i < itemCount; i++)
            {
                var row = TaskGrid.ItemContainerGenerator.ContainerFromIndex(i) as DataGridRow;
                if (row != null)
                {
                    // TranslatePoint retorna posição relativa à viewport; somamos o scroll para obter posição absoluta no conteúdo
                    rowTops[i] = row.TranslatePoint(new Point(0, 0), TaskGrid).Y - headerHeight + scrollOffset;
                    measuredAnyRow = true;
                }
                else
                {
                    rowTops[i] = i * TaskGrid.RowHeight;
                }
            }

            if (!measuredAnyRow) return;

            // Apenas comparar assinatura para evitar renders redundantes
            var signature = string.Join("|", rowTops.Select(v => Math.Round(v, 2).ToString("0.##")));
            if (signature == _lastRowLayoutSignature) return;

            _lastRowLayoutSignature = signature;
            RowTopsMeasured?.Invoke(rowTops);
        }

        private static void OnTasksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctrl = (TaskGridControl)d;
            ctrl._lastRowLayoutSignature = null;

            // Desinscreve da coleção antiga
            if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= ctrl.OnTasksCollectionChanged;

            // Inscreve na nova coleção para capturar Reset e preservar scroll
            if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newCol)
                newCol.CollectionChanged += ctrl.OnTasksCollectionChanged;

            ctrl.Dispatcher.BeginInvoke(() =>
            {
                var header = FindChild<DataGridColumnHeadersPresenter>(ctrl.TaskGrid);
                if (header != null && header.ActualHeight > 0)
                    ctrl.PublishRowTops(header.ActualHeight);
                ctrl.RefreshPriorityColumnVisibility();
            });
        }

        private void RefreshPriorityColumnVisibility()
        {
            bool hasDevOpsTasks = Tasks?.Any(t => t.IsDevOpsTask) == true;
            PriorityColumn.Visibility = hasDevOpsTasks ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Coluna %Daily (esperado ate hoje) esta visivel?</summary>
        public bool IsDailyPercentColumnVisible => DailyPercentColumn.Visibility == Visibility.Visible;

        /// <summary>Mostra/oculta a coluna %Daily. Retorna o novo estado (visivel).</summary>
        public bool ToggleDailyPercentColumn()
        {
            var visible = DailyPercentColumn.Visibility != Visibility.Visible;
            DailyPercentColumn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return visible;
        }

        private void OnTasksCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RefreshPriorityColumnVisibility();

            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset
                && _scrollViewer != null)
            {
                // O offset explícito foi capturado antes do Clear. No evento Reset o DataGrid
                // pode já tê-lo zerado, dependendo da ordem dos handlers da coleção.
                var savedOffset = _pendingVerticalOffset ?? _scrollViewer.VerticalOffset;
                _pendingVerticalOffset = null;
                if (savedOffset <= 0)
                {
                    _suppressScrollNotification = false;
                    return;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
                {
                    if (_scrollViewer != null)
                        _scrollViewer.ScrollToVerticalOffset(savedOffset);

                    Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        _suppressScrollNotification = false;
                        if (_scrollViewer != null)
                            VerticalScrollChanged?.Invoke(_scrollViewer.VerticalOffset);
                    }));
                }));
            }
        }

        private void OnTaskGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<ToggleButton>(e.OriginalSource as DependencyObject) != null)
            {
                _dragSourceTask = null;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                var source = e.OriginalSource as DependencyObject;
                var cell = FindParent<DataGridCell>(source);
                if (cell?.Column == StartColumn && cell.DataContext is TaskViewModel vmCtrl)
                {
                    e.Handled = true;
                    TaskGrid.SelectedItem = vmCtrl;
                    ShowStartCalendar(vmCtrl.Start, vmCtrl, cellEditInProgress: false, placementTarget: cell);
                    return;
                }
            }

            if (e.ClickCount >= 2 && TryHandleComboDoubleClick(e, SprintColumn))
                return;

            if (e.ClickCount >= 2 && TryHandleComboDoubleClick(e, ResourcesColumn))
                return;

            if (e.ClickCount >= 2 && TryHandleReadOnlyDoubleClick(e))
                return;

            if (e.ClickCount >= 2 && TryHandleEditableDoubleClick(e))
                return;

            _dragStartPoint = e.GetPosition(TaskGrid);
            _dragSourceTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
        }


        private bool TryHandleComboDoubleClick(MouseButtonEventArgs e, DataGridColumn comboColumn)
        {
            var source = e.OriginalSource as DependencyObject;
            var cell = FindParent<DataGridCell>(source);
            var row = FindParent<DataGridRow>(source);
            if (cell?.Column != comboColumn || row?.Item is not TaskViewModel task)
                return false;

            if (!CanEditCell(task, comboColumn))
                return false;

            BeginControlledEditSwitch();
            _dragSourceTask = null;
            TaskGrid.SelectedItem = task;
            TaskGrid.CurrentCell = new DataGridCellInfo(task, comboColumn);
            e.Handled = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                TaskGrid.BeginEdit();
                TaskGrid.UpdateLayout();

                var rowContainer = TaskGrid.ItemContainerGenerator.ContainerFromItem(task) as DataGridRow;
                var editCell = rowContainer != null ? FindCell(rowContainer, comboColumn) : null;
                var combo = editCell != null ? FindChild<ComboBox>(editCell) : null;
                if (combo == null)
                    return;

                combo.Focus();
                Keyboard.Focus(combo);
                combo.IsDropDownOpen = true;
            }));

            return true;
        }

        private bool TryHandleEditableDoubleClick(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var cell = FindParent<DataGridCell>(source);
            var row = FindParent<DataGridRow>(source);
            if (cell?.Column == null || row?.Item is not TaskViewModel task)
                return false;

            if (!CanEditCell(task, cell.Column))
                return false;

            if (cell.Column == SprintColumn || cell.Column == ResourcesColumn)
                return false;

            BeginControlledEditSwitch();
            _dragSourceTask = null;
            TaskGrid.SelectedItem = task;
            TaskGrid.CurrentCell = new DataGridCellInfo(task, cell.Column);
            e.Handled = true;

            var editColumn = cell.Column;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                TaskGrid.BeginEdit();
                TaskGrid.UpdateLayout();

                var rowContainer = TaskGrid.ItemContainerGenerator.ContainerFromItem(task) as DataGridRow;
                var editCell = rowContainer != null ? FindCell(rowContainer, editColumn) : null;
                var editor = editCell != null ? FindChild<TextBox>(editCell) : null;
                if (editor == null)
                    return;

                editor.Focus();
                Keyboard.Focus(editor);
                editor.SelectAll();
            }));

            return true;
        }

        private bool TryHandleReadOnlyDoubleClick(MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            var cell = FindParent<DataGridCell>(source);
            var row = FindParent<DataGridRow>(source);
            if (cell?.Column == null || row?.Item is not TaskViewModel task)
                return false;

            if (CanEditCell(task, cell.Column))
                return false;

            BeginControlledEditSwitch();
            _dragSourceTask = null;
            TaskGrid.SelectedItem = task;
            TaskGrid.CurrentCell = new DataGridCellInfo(task, cell.Column);
            cell.Focus();
            Keyboard.Focus(cell);
            e.Handled = true;
            return true;
        }

        private void OnTaskGridPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragSourceTask == null)
                return;

            var currentPosition = e.GetPosition(TaskGrid);
            if (Math.Abs(currentPosition.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(currentPosition.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            var dragTask = _dragSourceTask;
            _dragSourceTask = null;
            DragDrop.DoDragDrop(TaskGrid, new DataObject(typeof(TaskViewModel), dragTask), DragDropEffects.Move);
        }

        private void OnTaskGridDragOver(object sender, DragEventArgs e)
        {
            var sourceTask = e.Data.GetData(typeof(TaskViewModel)) as TaskViewModel;
            var targetTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
            e.Effects = sourceTask != null && targetTask != null && sourceTask != targetTask
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnTaskGridDrop(object sender, DragEventArgs e)
        {
            var sourceTask = e.Data.GetData(typeof(TaskViewModel)) as TaskViewModel;
            var targetTask = FindTaskViewModel(e.OriginalSource as DependencyObject);
            if (sourceTask == null || targetTask == null || sourceTask == targetTask)
                return;

            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            var insertAfter = false;
            if (row != null)
            {
                var rowPosition = e.GetPosition(row);
                insertAfter = rowPosition.Y > row.ActualHeight / 2;
            }

            TaskMoveRequested?.Invoke(sourceTask, targetTask, insertAfter);
            e.Handled = true;
        }

        // Só Feature e Story têm sprint: cancela a edição da coluna Sprint para
        // Projeto/Epic (e qualquer tipo sem suporte a sprint).
        private void OnTaskGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Column == DurationColumn || e.Column == OriginalHoursColumn)
                return;

            if (e.EditAction == DataGridEditAction.Commit)
            {
                if (IsUnchangedEdit(e.Column, e.EditingElement as FrameworkElement))
                {
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(CancelCurrentEdit));
                    return;
                }

                // NameColumn usa UpdateSourceTrigger=PropertyChanged — já está atualizado.
                // Items.Refresh() causaria desalinhamento visual; basta sair do modo de edição.
                if (e.Column == NameColumn)
                {
                    ClearEditSnapshot();
                    return;
                }

                if (e.Column == PercentColumn
                    && e.Row?.Item is TaskViewModel task
                    && TryGetPercentEditValue(e.EditingElement as FrameworkElement, out var requestedPercent)
                    && Math.Clamp(requestedPercent, 0, 100) >= 100
                    && task.Model.PercentComplete < 100
                    && ManualPercentCompleteCommitRequested is { } requestCommit)
                {
                    e.Cancel = true;
                    ClearEditSnapshot();
                    var manualSelectedItem = e.Row.Item;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(async () =>
                    {
                        CancelCurrentEdit();
                        var normalized = Math.Clamp(requestedPercent, 0, 100);
                        if (await requestCommit(task, normalized))
                        {
                            task.PercentComplete = normalized;
                            RefreshGridPreservingSelection(manualSelectedItem, PercentColumn);
                        }
                    }));
                    return;
                }

                var selectedItem = e.Row?.Item ?? TaskGrid.SelectedItem;
                var selectedColumn = e.Column;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    () => RefreshGridPreservingSelection(selectedItem, selectedColumn));
                ClearEditSnapshot();
            }
        }

        private void OnTaskGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            if (e.Row?.Item is not TaskViewModel task)
                return;

            // Coluna de nome exige duplo clique para entrar em edição
            if (e.Column == NameColumn
                && e.EditingEventArgs is System.Windows.Input.MouseButtonEventArgs mb
                && mb.ClickCount < 2)
            {
                e.Cancel = true;
                return;
            }

            if (!CanEditCell(task, e.Column))
            {
                e.Cancel = true;
                return;
            }

            CaptureEditSnapshot(task, e.Column);
        }

        private bool CanEditCell(TaskViewModel task, DataGridColumn column)
        {
            if (column == IdColumn ||
                column == DevOpsColumn ||
                column == OriginalHoursColumn)
                return false;

            if (column == DurationColumn)
                return !task.IsDurationReadOnly;

            if (column == StartColumn || column == FinishColumn)
                // Tasks têm datas calculadas por prioridade — não são editáveis manualmente
                return task.Model.Children.Count == 0 && !task.IsDevOpsTask;

            if (column == PercentColumn)
                return task.CanEditPercentComplete;

            if (column == PredecessorColumn)
                return task.CanEditPredecessors;

            if (column == SprintColumn)
                return task.SupportsSprint;

            return true;
        }

        private void CaptureEditSnapshot(TaskViewModel task, DataGridColumn column)
        {
            _editSnapshotTask = task;
            _editSnapshotColumn = column;
            _editSnapshotValue = GetEditValue(task, column, null);
        }

        private bool IsUnchangedEdit(DataGridColumn column, FrameworkElement? editor)
        {
            if (_editSnapshotTask == null || _editSnapshotColumn != column)
                return false;

            var current = GetEditValue(_editSnapshotTask, column, editor);
            return string.Equals(current, _editSnapshotValue, StringComparison.OrdinalIgnoreCase);
        }

        private string GetEditValue(TaskViewModel task, DataGridColumn column, FrameworkElement? editor)
        {
            if (editor != null)
            {
                if (FindChild<ComboBox>(editor) is { } combo)
                    return GetComboEditValue(combo, column);

                if (FindChild<TextBox>(editor) is { } textBox)
                    return GetTextEditValue(textBox, column);

                if (editor is TextBox directTextBox)
                    return GetTextEditValue(directTextBox, column);

                if (editor is ComboBox directCombo)
                    return GetComboEditValue(directCombo, column);
            }

            return GetModelEditValue(task, column);
        }

        private string GetModelEditValue(TaskViewModel task, DataGridColumn column)
        {
            if (column == NameColumn)
                return NormalizeEditText(task.Name);
            if (column == DurationColumn)
                return NormalizeNumber(task.DurationHours);
            if (column == SfpColumn)
                return NormalizeNullableNumber(task.SfpPoints);
            if (column == StartColumn)
                return NormalizeDate(task.Start);
            if (column == FinishColumn)
                return NormalizeDate(task.Finish);
            if (column == PercentColumn)
                return NormalizeNumber(task.PercentComplete);
            if (column == PredecessorColumn)
                return NormalizeEditText(task.PredecessorsText);
            if (column == ResourcesColumn)
                return task.PrimaryResource?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? NormalizeEditText(task.ResourcesText);
            if (column == SprintColumn)
                return NormalizeEditText(task.SprintPath);

            return string.Empty;
        }

        private string GetComboEditValue(ComboBox combo, DataGridColumn column)
        {
            if (column == SprintColumn)
                return NormalizeEditText(combo.SelectedValue?.ToString());

            if (column == ResourcesColumn)
                return (combo.SelectedItem as Resource)?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? NormalizeEditText(combo.Text);

            return NormalizeEditText(combo.SelectedValue?.ToString() ?? combo.Text);
        }

        private string GetTextEditValue(TextBox textBox, DataGridColumn column)
        {
            var text = NormalizeEditText(textBox.Text);
            if ((column == StartColumn || column == FinishColumn) &&
                DateTime.TryParse(text, out var date))
            {
                return NormalizeDate(date);
            }

            if ((column == DurationColumn || column == PercentColumn || column == SfpColumn) &&
                double.TryParse(text,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture,
                    out var number))
            {
                return NormalizeNumber(number);
            }

            return text;
        }

        private static bool TryGetPercentEditValue(FrameworkElement? editor, out double value)
        {
            value = 0;
            var textBox = editor switch
            {
                TextBox direct => direct,
                null => null,
                _ => FindChild<TextBox>(editor)
            };

            var raw = textBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            return double.TryParse(raw, System.Globalization.NumberStyles.Any,
                       System.Globalization.CultureInfo.CurrentCulture, out value)
                   || double.TryParse(raw, System.Globalization.NumberStyles.Any,
                       System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string NormalizeEditText(string? value) => (value ?? string.Empty).Trim();

        private static string NormalizeDate(DateTime value) =>
            value.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        private static string NormalizeNumber(double value) =>
            Math.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        private static string NormalizeNullableNumber(double? value) =>
            value.HasValue ? NormalizeNumber(value.Value) : string.Empty;

        private void OnPredecessorCellClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TaskViewModel task })
                return;
            if (!task.CanEditPredecessors)
                return;

            _dragSourceTask = null;
            e.Handled = true;

            var candidates = (Tasks ?? new ObservableCollection<TaskViewModel>())
                .Where(t => !ReferenceEquals(t, task))
                .Where(t => t.Model.Children.Count == 0)
                .OrderBy(t => t.Name)
                .ToList();

            var owner = Window.GetWindow(this);
            var dialog = new PredecessorPickerWindow(task, candidates)
            {
                Owner = owner
            };

            if (dialog.ShowDialog() == true)
            {
                task.PredecessorsText = dialog.SelectedPredecessorsText;
                TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
        }

        private void OnHighlightPredecessorsClick(object sender, RoutedEventArgs e)
        {
            // O sender pode ser MenuItem dentro do ContextMenu; DataContext vem do PlacementTarget
            TaskViewModel? task = null;
            if (sender is FrameworkElement fe)
            {
                task = fe.DataContext as TaskViewModel;
                if (task == null && fe is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement pt)
                    task = pt.DataContext as TaskViewModel;
            }
            if (task == null) return;
            e.Handled = true;

            _highlightSourceTaskId = task.Model.Id;
            if (Tasks != null)
            {
                foreach (var t in Tasks)
                { t.IsHighlightedPredecessor = false; t.IsHighlightSource = false; }
                task.IsHighlightSource = true;
                var predIds = task.Model.PredecessorIds.ToHashSet();
                foreach (var t in Tasks)
                    if (predIds.Contains(t.Model.Id))
                        t.IsHighlightedPredecessor = true;
            }
            HighlightPredecessorsRequested?.Invoke(task);
        }

        private void OnClearPredecessorHighlightClick(object sender, RoutedEventArgs e)
        {
            ClearHighlightState();
            HighlightPredecessorsRequested?.Invoke(null!);
        }

        // Abre no DevOps as predecessoras fora do escopo (amarelas) — o ID é o TfsId
        // do work item que não está no cronograma, para o usuário verificar qual é.
        private void OnOpenPredecessorInDevOpsClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm == null) return;
            e.Handled = true;

            var ids = vm.UnresolvedPredecessorIds;
            if (ids.Count == 0) return;

            // Base = organização + Team Project do cronograma aberto; fallback config global.
            string org = "", proj = "";
            if (DataContext is ViewModels.MainViewModel mainVm)
            {
                org = mainVm.Project.DevOpsOrganizationUrl?.Trim() ?? "";
                proj = mainVm.Project.DevOpsTeamProject?.Trim() ?? "";
            }
            if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(proj))
            {
                var cfg = NXProject.Services.TfsConnectionStore.Load("NXProject.Community");
                if (string.IsNullOrWhiteSpace(org)) org = cfg.OrganizationUrl?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(proj)) proj = cfg.TeamProject?.Trim() ?? "";
            }
            if (string.IsNullOrWhiteSpace(org)) return;
            org = org.TrimEnd('/');

            foreach (var id in ids)
            {
                var url = string.IsNullOrWhiteSpace(proj)
                    ? $"{org}/_workitems/edit/{id}"
                    : $"{org}/{Uri.EscapeDataString(proj)}/_workitems/edit/{id}";
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            }
        }

        // Limpa TODAS as predecessoras da atividade (inclusive as fora do projeto,
        // amarelas — work items do TFS que não estão no cronograma).
        private void OnClearPredecessorsClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm == null) return;
            e.Handled = true;

            vm.ClearAllPredecessors();
            if (_highlightSourceTaskId == vm.Model.Id)
                ClearHighlightState();

            if (DataContext is ViewModels.MainViewModel mainVm)
            {
                mainVm.Project.IsDirty = true;
                mainVm.RebuildFlatTasks();
            }
        }

        private void OnTaskGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_highlightSourceTaskId.HasValue) return;
            var selected = TaskGrid.SelectedItem as TaskViewModel;
            if (selected == null || selected.Model.Id != _highlightSourceTaskId.Value)
            {
                ClearHighlightState();
                HighlightPredecessorsRequested?.Invoke(null!);
            }
        }

        private void ClearHighlightState()
        {
            _highlightSourceTaskId = null;
            if (Tasks == null) return;
            foreach (var t in Tasks)
            { t.IsHighlightedPredecessor = false; t.IsHighlightSource = false; }
        }

        private int? _highlightSourceTaskId;

        private void OnSprintComboDropDownOpened(object? sender, EventArgs e)
        {
            if (sender is ComboBox { DataContext: TaskViewModel task } combo)
                CaptureEditSnapshot(task, SprintColumn);
        }

        // Troca de sprint pela grade: ao fechar o dropdown, se a seleção mudou,
        // notifica o ViewModel (grava o IterationPath / limpa, desliza a barra) e
        // encerra a edição. "(sem sprint)" (Path nulo) é tratado como limpar.
        private void OnSprintComboDropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not ComboBox { DataContext: TaskViewModel task } combo)
                return;
            if (IsUnchangedEdit(SprintColumn, combo))
            {
                CancelCurrentEdit();
                return; // usuário não mudou a escolha
            }

            var sprint = combo.SelectedItem as Sprint;
            if (sprint != null && string.IsNullOrEmpty(sprint.Path))
                sprint = null; // opção "(sem sprint)"

            TaskSprintChangeRequested?.Invoke(task, sprint);
            TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void OnResourceComboDropDownClosed(object? sender, EventArgs e)
        {
            if (sender is not ComboBox { DataContext: TaskViewModel task } combo)
                return;

            var selected = combo.SelectedItem as Resource;
            var typed = NormalizeManualResourceName(combo.Text);
            if (IsUnchangedEdit(ResourcesColumn, combo))
            {
                CancelCurrentEdit();
                return;
            }

            if (selected == null)
            {
                if (string.IsNullOrEmpty(typed))
                {
                    CancelCurrentEdit();
                    return;
                }

                var existing = AvailableResources?.FirstOrDefault(r => string.Equals(r.Name, typed, StringComparison.OrdinalIgnoreCase));
                Resource res;
                if (existing == null)
                {
                    var nextId = (AvailableResources?.Select(r => r.Id).DefaultIfEmpty(0).Max() ?? 0) + 1;
                    res = new Resource { Id = nextId, Name = typed, IsImportedFromTfs = false };
                    AvailableResources?.Add(res);
                }
                else
                {
                    res = existing;
                }

                task.PrimaryResource = res;
            }
            else
            {
                task.PrimaryResource = selected;
            }

            TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void OnResourceComboDropDownOpened(object? sender, EventArgs e)
        {
            if (sender is ComboBox { DataContext: TaskViewModel task } combo)
                CaptureEditSnapshot(task, ResourcesColumn);
        }

        private void OnResourceComboKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not ComboBox { DataContext: TaskViewModel task } combo) return;
            OnResourceComboDropDownClosed(sender, EventArgs.Empty);
        }

        private static string NormalizeManualResourceName(string? name) =>
            (name ?? string.Empty).Trim().TrimStart('*').Trim();

        private void CommitDurationEdit(TextBox tb)
        {
            if (tb.DataContext is not TaskViewModel vm) return;
            if (IsUnchangedEdit(DurationColumn, tb))
            {
                // Mesmo sem mudança de duração, sincroniza HH Original se ainda não estiver definido
                if (vm.Model.PercentComplete < 0.0001 && !(vm.Model.OriginalEstimatedHours is > 0))
                {
                    var h = vm.DurationHours;
                    if (h > 0)
                    {
                        vm.Model.OriginalEstimatedHours = h;
                        vm.RefreshDerivedDisplayProperties();
                    }
                }
                CancelCurrentEdit();
                return;
            }

            if (TaskViewModel.TryParseDurationText(tb.Text, out var parsedHours) &&
                parsedHours <= 0.0001 &&
                !vm.CanUseZeroDuration)
            {
                System.Windows.MessageBox.Show(
                    "Duração zero cria um marco no cronograma.\n\n" +
                    "Use Tipo = No DevOps para marco local ou Tipo = Marco-Devops para marco anexado/sincronizável no DevOps.",
                    "Duração zero",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                CancelCurrentEdit();
                return;
            }

            if (TaskViewModel.TryParseDurationText(tb.Text, out parsedHours) &&
                parsedHours > 0.0001 &&
                vm.IsDevOpsMilestoneActivity)
            {
                System.Windows.MessageBox.Show(
                    "Atividade do tipo Marco-Devops é um marco anexado ao DevOps e só aceita Dur.(h) = 0.",
                    "Marco-Devops",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                CancelCurrentEdit();
                return;
            }

            vm.DurationText = tb.Text;
            // Notifica explicitamente as colunas read-only que dependem de DurationHours
            vm.RefreshDerivedDisplayProperties();
            TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ClearEditSnapshot();
        }

        private void OnEditTextBoxGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(tb.SelectAll);
        }

        private void OnDurationEditKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                CommitDurationEdit(tb);
                e.Handled = true;
            }
        }

        private void OnDurationEditLostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEditorLostFocusCommit)
                return;

            if (sender is TextBox tb)
                CommitDurationEdit(tb);
        }

        private void OnToggleOrgHColumnClick(object sender, RoutedEventArgs e)
        {
            ShowOriginalHoursColumn = !ShowOriginalHoursColumn;
        }

        private void OnTksClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TaskViewModel vm)
                TksClickRequested?.Invoke(vm);
        }

        private void OnFetchTaskHoursClick(object sender, RoutedEventArgs e)
        {
            var vm = _durationContextMenuVm;
            if (vm == null) return;
            FetchTaskHoursRequested?.Invoke(vm);
        }

        // Guarda a VM capturada no ContextMenuOpening para usar no click (PlacementTarget pode ser null no Click)
        private TaskViewModel? _durationContextMenuVm;

        private void OnToggleGanttOriginalViewClick(object sender, RoutedEventArgs e)
        {
            var vm = _durationContextMenuVm;
            if (vm == null) return;
            vm.SetOriginalHoursView(!vm.UseOriginalHoursView);
            GanttViewToggled?.Invoke();
        }

        // Atualiza os labels dos itens de menu e captura a VM antes de abrir o ContextMenu.
        private void OnDurationContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not Border { ContextMenu: { } cm }) return;

            _durationContextMenuVm = (sender as System.Windows.FrameworkElement)?.DataContext as TaskViewModel;
            var vm = _durationContextMenuVm;

            var orgHItem = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ToggleOrgHMenuItem");
            if (orgHItem != null)
                orgHItem.Header = ShowOriginalHoursColumn
                    ? "Ocultar coluna Estimativa Original (OrgH)"
                    : "Mostrar coluna Estimativa Original (OrgH)";

            var ganttItem = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ToggleGanttOriginalMenuItem");
            if (ganttItem != null)
                ganttItem.Header = vm?.UseOriginalHoursView == true
                    ? "Mostrar Gantt pela Duração (HH Atual + HH Restante)"
                    : "Mostrar Gantt pela Estimativa Original";

        }


        private void OnNameContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not System.Windows.FrameworkElement el) return;
            var vm = el.DataContext as TaskViewModel;
            var cm = el.ContextMenu;
            if (cm == null || vm == null) return;

            bool isTaskItem = Services.TfsImportService.IsTaskTypePublic(vm.Model.TfsType);

            // Block de Story (oculto quando for Task)
            var storyBlockItem = cm.Items.OfType<MenuItem>().FirstOrDefault();
            if (storyBlockItem != null)
            {
                storyBlockItem.Visibility = isTaskItem ? Visibility.Collapsed : Visibility.Visible;
                storyBlockItem.Header = vm.IsBlockedByStory ? "Retirar Block da Story" : "Adicionar Block na Story";
            }

            // Block de Task (visível apenas para tasks)
            var taskBlockItem = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ToggleTaskBlockMenuItem");
            if (taskBlockItem != null)
            {
                taskBlockItem.Visibility = isTaskItem ? Visibility.Visible : Visibility.Collapsed;
                taskBlockItem.Header = vm.IsBlockedByTask ? "✅ Retirar Block da Task" : "🔴 Adicionar Block na Task";
            }

            bool hasDevOps = vm.Model.TfsId is > 0;
            bool isStoryLike = !vm.Model.IsSummary && !vm.Model.IsMilestone &&
                               (Services.TfsImportService.IsStoryTypePublic(vm.Model.TfsType) ||
                                string.Equals(vm.Model.TfsType, "Feature", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(vm.Model.TfsType, "Epic", StringComparison.OrdinalIgnoreCase));
            bool hasTasks = vm.Model.Children.Any(c =>
                string.Equals(c.TfsType, "Task", StringComparison.OrdinalIgnoreCase));
            bool suppressed = vm.Model.TasksSuppressed;

            // "Grid de Tasks" aparece para stories com DevOps (sempre, para editar/revisar)
            var fetchItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "FetchChildTasksMenuItem");
            if (fetchItem != null)
                fetchItem.Visibility = (hasDevOps && isStoryLike) ? Visibility.Visible : Visibility.Collapsed;

            // "Resumo de tasks (alocação)" aparece para Story com resumo gravado (Sync/import do mapa).
            var allocSummaryItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "TaskAllocSummaryMenuItem");
            if (allocSummaryItem != null)
                allocSummaryItem.Visibility = (isStoryLike && vm.Model.TaskAllocations.Count > 0)
                    ? Visibility.Visible : Visibility.Collapsed;

            // "Expandir Tasks no cronograma" aparece quando suprimido (tasks já em memória)
            var expandItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "ExpandChildTasksMenuItem");
            if (expandItem != null)
                expandItem.Visibility = (hasDevOps && isStoryLike && suppressed) ? Visibility.Visible : Visibility.Collapsed;

            // "Liberar Atividade" aparece quando suprimido — confirma story como folha editável
            var releaseStoryItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "ReleaseStoryMenuItem");
            if (releaseStoryItem != null)
                releaseStoryItem.Visibility = (isStoryLike && suppressed) ? Visibility.Visible : Visibility.Collapsed;

            // "Suprimir" aparece quando há tasks no cronograma
            var suppressItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "SuppressChildTasksMenuItem");
            if (suppressItem != null)
                suppressItem.Visibility = hasTasks ? Visibility.Visible : Visibility.Collapsed;

            // "Nova Task DevOps" / "Nova Atividade Interna" aparecem para stories (com ou sem DevOps)
            bool isStory = !vm.Model.IsSummary && !vm.Model.IsMilestone &&
                           Services.TfsImportService.IsStoryTypePublic(vm.Model.TfsType);
            var sepAdd      = cm.Items.OfType<Separator>().FirstOrDefault(s => s.Name == "AddTaskSeparator");
            var addDevOps   = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "AddDevOpsTaskMenuItem");
            var addInternal = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "AddInternalTaskMenuItem");
            var showAdd = isStory;
            if (sepAdd      != null) sepAdd.Visibility      = showAdd ? Visibility.Visible : Visibility.Collapsed;
            if (addDevOps   != null) addDevOps.Visibility   = (showAdd && hasDevOps) ? Visibility.Visible : Visibility.Collapsed;
            if (addInternal != null) addInternal.Visibility = showAdd ? Visibility.Visible : Visibility.Collapsed;

            // "Alterar Classificação" — visível apenas para tipos com campo de classificação configurado
            bool isPendingCreate = vm.Model.TfsId == 0 || vm.Model.IsPendingTfsCreate;
            bool isDevOpsItem = !vm.Model.IsMilestone
                                && !string.Equals(vm.Model.TfsType, "No DevOps", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(vm.Model.TfsType)
                                && (isPendingCreate || vm.Model.TfsId is > 0);
            var classifSep    = cm.Items.OfType<Separator>().FirstOrDefault(s => s.Name == "ClassificationSeparator");
            var classifItem   = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "EditClassificationMenuItem");
            var configClassif = cm.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "ConfigureClassificationMenuItem");

            if (classifSep != null) classifSep.Visibility = isDevOpsItem ? Visibility.Visible : Visibility.Collapsed;

            if (classifItem != null)
            {
                classifItem.Visibility = isDevOpsItem ? Visibility.Visible : Visibility.Collapsed;
                var current = vm.TfsClassification ?? "";
                classifItem.Header = string.IsNullOrEmpty(current)
                    ? "🏷 Custom DevOps  (definir...)"
                    : $"🏷 Custom DevOps: {current}  (alterar...)";
            }

            if (configClassif != null)
                configClassif.Visibility = Visibility.Collapsed;

            var conflictItem = cm.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "ResolveManualConflictMenuItem");
            var conflictSep = cm.Items.OfType<Separator>()
                .FirstOrDefault(s => s.Name == "ResolveManualConflictSeparator");
            var canResolveConflict = vm.HasSyncConflict && vm.Model.TfsId is > 0;
            if (conflictItem != null)
                conflictItem.Visibility = canResolveConflict ? Visibility.Visible : Visibility.Collapsed;
            if (conflictSep != null)
                conflictSep.Visibility = canResolveConflict ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnToggleTaskBlockClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm == null) return;
            vm.ToggleTaskBlock();
            if (DataContext is ViewModels.MainViewModel mainVm)
                mainVm.Project.IsDirty = true;
        }

        private void OnToggleStoryBlockClick(object sender, RoutedEventArgs e)
        {
            // Sobe pela árvore visual para encontrar a DataContext (TaskViewModel)
            var el = sender as System.Windows.FrameworkElement;
            // ContextMenu não está na árvore visual normal; usa PlacementTarget
            if (el?.Parent is ContextMenu cm)
                el = cm.PlacementTarget as System.Windows.FrameworkElement;

            var vm = el?.DataContext as TaskViewModel
                  ?? (el?.TemplatedParent as System.Windows.FrameworkElement)?.DataContext as TaskViewModel;

            if (vm == null) return;
            vm.ToggleStoryBlock();
            // Marca projeto como dirty
            if (DataContext is ViewModels.MainViewModel mainVm)
                mainVm.Project.IsDirty = true;
        }

        // Botão direito na Story → mostra o resumo de tasks (dono + horas) usado na alocação.
        private void OnShowTaskAllocSummaryClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            var story = vm?.Model;
            if (story == null || story.TaskAllocations.Count == 0) return;

            double taskSum   = story.TaskAllocations.Sum(a => a.Hours);
            double storyHH   = (story.CurrentHours ?? 0) + (story.EstimatedHours ?? 0);
            double remainder = System.Math.Max(storyHH - taskSum, 0);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(story.Name ?? $"#{story.TfsId}");
            sb.AppendLine();
            foreach (var a in story.TaskAllocations.OrderByDescending(x => x.Hours))
                sb.AppendLine($"• {a.Resource}: {a.Hours:0.##}h");
            sb.AppendLine();
            sb.AppendLine($"Soma das tasks: {taskSum:0.##}h");
            sb.AppendLine($"HH da Story: {storyHH:0.##}h");
            var respName = story.Resources.FirstOrDefault()?.Resource?.Name;
            sb.AppendLine($"Restante do responsável{(string.IsNullOrWhiteSpace(respName) ? "" : $" ({respName})")}: {remainder:0.##}h");

            var title = TryFindResource("Grid_TaskAllocSummary") as string ?? "Resumo de tasks";
            MessageBox.Show(sb.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private TaskViewModel? GetTaskViewModelFromContextSender(object sender)
        {
            var el = sender as System.Windows.FrameworkElement;
            if (el?.Parent is ContextMenu cm)
                el = cm.PlacementTarget as System.Windows.FrameworkElement;
            return el?.DataContext as TaskViewModel
                ?? (el?.TemplatedParent as System.Windows.FrameworkElement)?.DataContext as TaskViewModel;
        }

        private void OnViewOnlineChildrenClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                ViewOnlineChildrenRequested?.Invoke(vm);
        }

        private void OnEditDescriptionClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                EditDescriptionRequested?.Invoke(vm);
        }

        private void OnResolveManualConflictClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                ResolveManualConflictRequested?.Invoke(vm);
        }

        private void OnFetchChildTasksClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                FetchChildTasksRequested?.Invoke(vm);
        }

        private void OnSuppressChildTasksClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                SuppressChildTasksRequested?.Invoke(vm);
        }

        private void OnReleaseStoryClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                ReleaseStoryRequested?.Invoke(vm);
        }

        private void OnExpandChildTasksClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                ExpandChildTasksRequested?.Invoke(vm);
        }

        private void OnAddDevOpsTaskClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                AddDevOpsTaskRequested?.Invoke(vm);
        }

        private void OnAddInternalTaskClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                AddInternalTaskRequested?.Invoke(vm);
        }

        private void OnPriorityEditLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox tb) return;
            if (tb.DataContext is not TaskViewModel vm || !vm.IsDevOpsTask) return;
            vm.PriorityDisplay = tb.Text;
            if (DataContext is ViewModels.MainViewModel mainVm)
            {
                mainVm.Project.IsDirty = true;
                var model = vm.Model;
                var modelId = model.Id;
                mainVm.RebuildFlatTasks();
                var rebuilt = mainVm.FlatTasks.FirstOrDefault(t => ReferenceEquals(t.Model, model))
                              ?? mainVm.FlatTasks.FirstOrDefault(t => t.Model.Id == modelId);
                if (rebuilt != null)
                {
                    mainVm.SelectedTask = rebuilt;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => SelectRebuiltTask(rebuilt, PriorityColumn));
                }
            }
        }

        private void SelectRebuiltTask(TaskViewModel task, DataGridColumn column)
        {
            if (!TaskGrid.Items.Contains(task))
                return;

            TaskGrid.SelectedItem = task;
            TaskGrid.CurrentCell = new DataGridCellInfo(task, column);
            TaskGrid.ScrollIntoView(task, column);
            TaskGrid.UpdateLayout();

            if (TaskGrid.ItemContainerGenerator.ContainerFromItem(task) is not DataGridRow row)
                return;

            row.IsSelected = true;
            var cell = FindCell(row, column);
            if (cell != null)
            {
                cell.Focus();
                Keyboard.Focus(cell);
                return;
            }

            row.Focus();
            Keyboard.Focus(row);
        }

        private void CommitStartEdit(TextBox tb)
        {
            if (tb.DataContext is not TaskViewModel vm) return;
            if (IsUnchangedEdit(StartColumn, tb))
            {
                CancelCurrentEdit();
                return;
            }

            var raw = tb.Text?.Trim() ?? string.Empty;
            var calculatedDate = vm.Start.Date;
            bool parsed = DateTime.TryParse(raw, out var typed);

            if (parsed && typed.Date == calculatedDate)
            {
                CancelCurrentEdit();
                return;
            }

            if (raw == "0")
            {
                vm.StartText = raw;
                TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
                ClearEditSnapshot();
                return;
            }

            if (parsed && NXProject.Services.ProjectCalendarService.IsWorkingDay(typed.Date))
            {
                vm.Start = typed.Date;
                TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
                ClearEditSnapshot();
                Dispatcher.InvokeAsync(() => RefreshGridPreservingSelection(vm, StartColumn),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            else
            {
                // Se digitou data não útil, pré-seleciona o próximo dia útil a partir do que foi digitado.
                // Se digitou valor inválido, usa a data calculada atual.
                DateTime calDate;
                if (parsed)
                {
                    var candidate = typed.Date;
                    while (!NXProject.Services.ProjectCalendarService.IsWorkingDay(candidate))
                        candidate = candidate.AddDays(1);
                    calDate = candidate;
                }
                else
                {
                    calDate = calculatedDate;
                }
                ShowStartCalendar(calDate, vm, cellEditInProgress: true);
            }
        }

        private void OnStartDisplayPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            if (sender is not FrameworkElement { DataContext: TaskViewModel vm })
                return;

            e.Handled = true;
            TaskGrid.SelectedItem = vm;
            var cell = FindParent<DataGridCell>(sender as DependencyObject);
            ShowStartCalendar(vm.Start, vm, cellEditInProgress: false, placementTarget: cell);
        }

        private void OnStartEditPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                return;

            if (sender is not TextBox { DataContext: TaskViewModel vm } tb)
                return;

            e.Handled = true;
            TaskGrid.SelectedItem = vm;
            var cell = FindParent<DataGridCell>(tb);
            ShowStartCalendar(vm.Start, vm, cellEditInProgress: true, placementTarget: cell is UIElement target ? target : tb);
        }

        private void OnStartEditKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                CommitStartEdit(tb);
                e.Handled = true;
            }
        }

        private void OnStartEditLostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEditorLostFocusCommit)
                return;

            if (sender is TextBox tb)
                CommitStartEdit(tb);
        }

        private void OnStartContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                var vm = (menu.PlacementTarget as FrameworkElement)?.DataContext as TaskViewModel;
                if (menu.Items[0] is MenuItem fixItem)
                    fixItem.IsEnabled = vm != null && !vm.StartFixed;
            }
        }

        private void OnFixStartClick(object sender, RoutedEventArgs e)
        {
            var vm = ((sender as MenuItem)?.Parent as ContextMenu)
                ?.PlacementTarget is FrameworkElement fe ? fe.DataContext as TaskViewModel : null;
            if (vm == null) return;

            vm.StartFixed = true;
        }



        private void ShowStartCalendar(DateTime selectedDate, TaskViewModel vm, bool cellEditInProgress, UIElement? placementTarget = null)
        {
            _suppressEditorLostFocusCommit = true;

            var calendar = new Calendar
            {
                SelectedDate = selectedDate,
                DisplayDate = selectedDate
            };

            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Owner = Window.GetWindow(this),
                Content = new Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    BorderBrush = System.Windows.Media.Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        BlurRadius = 8,
                        ShadowDepth = 2,
                        Opacity = 0.3
                    },
                    Child = calendar
                }
            };

            // Posicionar abaixo do elemento alvo (célula ou grid), corrigindo DPI
            var target = placementTarget ?? GetCurrentStartCell() ?? (UIElement)TaskGrid;
            var screenPt = target.PointToScreen(new Point(0, ((FrameworkElement)target).ActualHeight));
            var source = PresentationSource.FromVisual(target);
            var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            win.Left = screenPt.X / dpiX;
            win.Top  = screenPt.Y / dpiY;

            DateTime? chosen = null;
            bool ignoreFirstChange = true;

            calendar.AddHandler(
                ButtonBase.ClickEvent,
                new RoutedEventHandler((s, e) =>
                {
                    var dayButton = FindParent<CalendarDayButton>(e.OriginalSource as DependencyObject);
                    if (dayButton?.DataContext is DateTime day)
                    {
                        chosen = day.Date;
                        e.Handled = true;
                        win.Close();
                    }
                }),
                handledEventsToo: true);

            calendar.SelectedDatesChanged += (s, e) =>
            {
                if (ignoreFirstChange) return;
                chosen = calendar.SelectedDate?.Date;
                win.Close();
            };

            calendar.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    chosen = calendar.SelectedDate?.Date;
                    win.Close();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    win.Close();
                }
            };

            win.Loaded += (s, e) =>
            {
                ignoreFirstChange = false;
                calendar.Focus();
                Keyboard.Focus(calendar);
            };

            win.ShowDialog();

            if (chosen == null)
            {
                _suppressEditorLostFocusCommit = false;
                if (cellEditInProgress)
                    CancelCurrentEdit();
                return;
            }

            // Cancela o edit do TextBox (evita CommitStartEdit sobrescrever a data do calendário)
            // e aplica a data diretamente no ViewModel.
            if (cellEditInProgress)
                CancelCurrentEdit();
            _suppressEditorLostFocusCommit = false;
            vm.Start = chosen.Value;
            TaskGrid.Focus();
            Dispatcher.InvokeAsync(() => RefreshGridPreservingSelection(vm, StartColumn),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private DataGridCell? GetCurrentStartCell()
        {
            var row = TaskGrid.ItemContainerGenerator.ContainerFromItem(TaskGrid.CurrentItem) as DataGridRow;
            if (row == null) return null;
            var presenter = FindChild<System.Windows.Controls.Primitives.DataGridCellsPresenter>(row);
            if (presenter == null) return null;
            for (int i = 0; i < TaskGrid.Columns.Count; i++)
            {
                if (TaskGrid.Columns[i] == StartColumn)
                    return presenter.ItemContainerGenerator.ContainerFromIndex(i) as DataGridCell;
            }
            return null;
        }

        private void CommitFinishEdit(TextBox tb)
        {
            if (tb.DataContext is not TaskViewModel vm) return;
            if (IsUnchangedEdit(FinishColumn, tb))
            {
                CancelCurrentEdit();
                return;
            }

            var currentDur = vm.DurationHours;
            var result = System.Windows.MessageBox.Show(
                $"Alterar a data Fim mudará a duração (atual: {currentDur:0.#}h).\nDeseja continuar?",
                "Alterar data Fim",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                CancelCurrentEdit();
                return;
            }

            vm.FinishText = tb.Text;
            TaskGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TaskGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void CancelCurrentEdit()
        {
            TaskGrid.CancelEdit(DataGridEditingUnit.Cell);
            TaskGrid.CancelEdit(DataGridEditingUnit.Row);
            ClearEditSnapshot();
        }

        private void BeginControlledEditSwitch()
        {
            _suppressEditorLostFocusCommit = true;
            try
            {
                TaskGrid.CancelEdit(DataGridEditingUnit.Cell);
                TaskGrid.CancelEdit(DataGridEditingUnit.Row);
                ClearEditSnapshot();
            }
            finally
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    _suppressEditorLostFocusCommit = false;
                }));
            }
        }

        private void ClearEditSnapshot()
        {
            _editSnapshotTask = null;
            _editSnapshotColumn = null;
            _editSnapshotValue = string.Empty;
        }

        private void OnFinishEditKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                CommitFinishEdit(tb);
                e.Handled = true;
            }
        }

        private void OnFinishEditLostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEditorLostFocusCommit)
                return;

            if (sender is TextBox tb)
                CommitFinishEdit(tb);
        }

        private void OnIdCellPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
                return;

            _dragSourceTask = null;
            if (sender is FrameworkElement { DataContext: TaskViewModel task })
            {
                BeginControlledEditSwitch();
                TaskGrid.SelectedItem = task;
                TaskGrid.CurrentCell = new DataGridCellInfo(task, IdColumn);
            }

            e.Handled = true;
        }

        private void OnIdCellClick(object sender, RoutedEventArgs e)
        {
            _dragSourceTask = null;
            if (_taskIdClickInProgress)
            {
                e.Handled = true;
                return;
            }

            if (sender is FrameworkElement { DataContext: TaskViewModel task })
            {
                e.Handled = true;
                _taskIdClickInProgress = true;
                try
                {
                    BeginControlledEditSwitch();
                    TaskGrid.SelectedItem = task;
                    TaskGrid.CurrentCell = new DataGridCellInfo(task, IdColumn);
                    TaskIdClicked?.Invoke(task);
                }
                finally
                {
                    _taskIdClickInProgress = false;
                }
            }
        }

        private void OnHierarchyTogglePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragSourceTask = null;

            if (sender is ToggleButton { DataContext: TaskViewModel task })
            {
                task.IsExpanded = !task.IsExpanded;
                e.Handled = true;
            }
        }

        private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var found = FindChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static TaskViewModel? FindTaskViewModel(DependencyObject? source)
        {
            return FindParent<DataGridRow>(source)?.Item as TaskViewModel;
        }

        private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
        {
            var current = source;
            while (current != null)
            {
                if (current is T match)
                    return match;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void OnEditClassificationClick(object sender, RoutedEventArgs e)
        {
            var vm = GetTaskViewModelFromContextSender(sender);
            if (vm != null)
                EditClassificationRequested?.Invoke(vm);
        }

        private void OnConfigureClassificationClick(object sender, RoutedEventArgs e)
            => ConfigureClassificationRequested?.Invoke();

        private void OnEditPercAlocClick(object sender, RoutedEventArgs e)
        {
            TaskViewModel? task = null;
            if (sender is FrameworkElement fe)
            {
                task = fe.DataContext as TaskViewModel;
                if (task == null && fe.ContextMenu?.PlacementTarget is FrameworkElement pt)
                    task = pt.DataContext as TaskViewModel;
            }
            if (task == null || task.Model.Resources.Count == 0) return;

            EditPercAlocRequested?.Invoke(task);
        }
    }
}
