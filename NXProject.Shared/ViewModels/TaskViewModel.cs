using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NXProject.Models;

namespace NXProject.ViewModels
{
    public partial class TaskViewModel : ObservableObject
    {
        private readonly ProjectTask _task;

        public TaskViewModel(ProjectTask task, int depth = 0)
        {
            _task = task;
            Depth = depth;
        }

        public ProjectTask Model => _task;

        public int Depth { get; }

        // Indentacao visual na grade
        public double Indent => Depth * 16.0;

        [ObservableProperty] private bool _isExpanded = true;
        [ObservableProperty] private bool _isSelected;

        public int Id
        {
            get => _task.Id;
            set { _task.Id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _task.Name;
            set { _task.Name = value; OnPropertyChanged(); _task.Parent?.RecalcSummary(); }
        }

        public DateTime Start
        {
            get => _task.Start;
            set
            {
                var durationDays = DurationDays;
                _task.Start = value;
                _task.Finish = value.AddDays(durationDays);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Finish));
                OnPropertyChanged(nameof(DurationDays));
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public DateTime Finish
        {
            get => _task.Finish;
            set
            {
                var minimumFinish = _task.Start.AddDays(DurationDays);
                if (value < minimumFinish)
                {
                    MessageBox.Show(
                        "Para reduzir a data de termino abaixo da duracao atual, altere primeiro a duracao da atividade.",
                        "Alterar duracao",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                _task.Finish = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationDays));
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public int DurationDays
        {
            get => (int)(_task.Finish - _task.Start).TotalDays;
            set
            {
                if (value >= 0)
                {
                    _task.Finish = _task.Start.AddDays(value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Finish));
                    OnPropertyChanged(nameof(DisplayAsMilestone));
                }
            }
        }

        public double PercentComplete
        {
            get => _task.PercentComplete;
            set { _task.PercentComplete = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
        }

        public bool IsMilestone
        {
            get => _task.IsMilestone;
            set
            {
                _task.IsMilestone = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public bool DisplayAsMilestone => IsMilestone || DurationDays == 0;

        public bool IsSummary
        {
            get => _task.IsSummary;
            set { _task.IsSummary = value; OnPropertyChanged(); }
        }

        public int SprintNumber
        {
            get => _task.SprintNumber;
            set { _task.SprintNumber = value; OnPropertyChanged(); }
        }

        public string PredecessorsText
        {
            get => string.Join(",", _task.PredecessorIds);
            set
            {
                _task.PredecessorIds.Clear();
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(part.Trim(), out int id))
                        _task.PredecessorIds.Add(id);
                OnPropertyChanged();
            }
        }

        public string ResourcesText
        {
            get
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var r in _task.Resources)
                    names.Add(r.ToString());
                return string.Join(", ", names);
            }
        }

        public string? Notes
        {
            get => _task.Notes;
            set { _task.Notes = value; OnPropertyChanged(); }
        }
    }
}
