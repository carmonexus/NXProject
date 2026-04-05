using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NXProject.ViewModels
{
    public class SprintGroup
    {
        public string Header { get; set; } = string.Empty;
        public ObservableCollection<TaskViewModel> Tasks { get; set; } = new();
    }

    public partial class ResourceAllocationGroup : ObservableObject
    {
        public string ResourceName { get; set; } = string.Empty;
        public string CapacityText { get; set; } = string.Empty;
        [ObservableProperty] private bool _isOverAllocated;
        public ObservableCollection<ResourceTaskRow> Tasks { get; set; } = new();
    }

    public class ResourceTaskRow
    {
        public int SprintNumber { get; set; }
        public string TaskName { get; set; } = string.Empty;
        public double AllocationPercent { get; set; }
        public double EstimatedHours { get; set; }
    }
}
