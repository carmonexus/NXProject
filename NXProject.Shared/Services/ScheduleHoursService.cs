using System;
using NXProject.Models;

namespace NXProject.Services
{
    public readonly record struct ProgressHours(double CurrentHours, double RemainingHours);

    public static class ScheduleHoursService
    {
        public static ProgressHours SplitByPercentComplete(double totalHours, double percentComplete)
        {
            var total = Math.Max(0, totalHours);
            var percent = Math.Clamp(percentComplete, 0, 100);
            var current = Math.Round(total * (percent / 100.0), 4);
            var remaining = Math.Round(Math.Max(0, total - current), 4);
            return new ProgressHours(current, remaining);
        }

        public static bool ApplyMissingProgressHours(ProjectTask task, double? plannedHours = null)
        {
            if (task.IsSummary || task.PercentComplete >= 100)
                return false;

            if (task.CurrentHours is > 0 || task.EstimatedHours is > 0)
                return false;

            var totalHours =
                plannedHours is > 0 ? plannedHours.Value :
                task.OriginalEstimatedHours is > 0 ? task.OriginalEstimatedHours.Value :
                ProjectCalendarService.CountWorkingHours(task.Start, task.Finish);

            if (totalHours <= 0)
                return false;

            var split = SplitByPercentComplete(totalHours, task.PercentComplete);
            task.CurrentHours = split.CurrentHours > 0 ? split.CurrentHours : task.PercentComplete < 0.0001 ? 0 : null;
            task.EstimatedHours = split.RemainingHours;
            task.IsMilestone = false;
            return true;
        }
    }
}
