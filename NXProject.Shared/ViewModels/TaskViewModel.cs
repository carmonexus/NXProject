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
        private readonly double _lowDaysPerSfp;
        private readonly double _mediumDaysPerSfp;
        private readonly double _highDaysPerSfp;

        public TaskViewModel(
            ProjectTask task,
            int depth = 0,
            double lowDaysPerSfp = 1.0,
            double mediumDaysPerSfp = 1.0,
            double highDaysPerSfp = 1.0)
        {
            _task = task;
            Depth = depth;
            _lowDaysPerSfp = Math.Max(0, lowDaysPerSfp);
            _mediumDaysPerSfp = Math.Max(0, mediumDaysPerSfp);
            _highDaysPerSfp = Math.Max(0, highDaysPerSfp);

            if (UsesSfpEstimate)
                ApplySfpDuration();
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
                RecalcAncestorSummaries();
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
                RecalcAncestorSummaries();
            }
        }

        public int DurationDays
        {
            get => (int)(_task.Finish - _task.Start).TotalDays;
            set
            {
                if (UsesSfpEstimate)
                    return;

                if (value >= 0)
                {
                    _task.Finish = _task.Start.AddDays(value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Finish));
                    OnPropertyChanged(nameof(DisplayAsMilestone));
                    RecalcAncestorSummaries();
                }
            }
        }

        public double? SfpPoints
        {
            get => _task.SfpPoints;
            set
            {
                _task.SfpPoints = value.HasValue && value.Value > 0 ? value.Value : null;
                if (UsesSfpEstimate)
                    ApplySfpDuration();

                OnPropertyChanged();
                OnPropertyChanged(nameof(UsesSfpEstimate));
                OnPropertyChanged(nameof(DurationDays));
                OnPropertyChanged(nameof(Finish));
                OnPropertyChanged(nameof(DisplayAsMilestone));
            }
        }

        public bool UsesSfpEstimate => (SfpPoints ?? 0) > 0;

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

        public void ShiftSchedule(int dayDelta)
        {
            if (dayDelta == 0)
                return;

            _task.Start = _task.Start.AddDays(dayDelta);
            _task.Finish = _task.Finish.AddDays(dayDelta);
            OnPropertyChanged(nameof(Start));
            OnPropertyChanged(nameof(Finish));
            OnPropertyChanged(nameof(DurationDays));
            OnPropertyChanged(nameof(DisplayAsMilestone));
            RecalcAncestorSummaries();
        }

        private void RecalcAncestorSummaries()
        {
            var current = _task.Parent;
            while (current != null)
            {
                current.RecalcSummary();
                current = current.Parent;
            }
        }

        private void ApplySfpDuration()
        {
            if (!UsesSfpEstimate)
                return;

            var sfpPoints = SfpPoints ?? 0;
            var daysPerSfp = sfpPoints <= 3
                ? _lowDaysPerSfp
                : sfpPoints < 6
                    ? _mediumDaysPerSfp
                    : _highDaysPerSfp;
            var calculatedDuration = Math.Max(1, (int)Math.Ceiling(sfpPoints * daysPerSfp));
            _task.Finish = _task.Start.AddDays(calculatedDuration);
            RecalcAncestorSummaries();
        }
    }
}
