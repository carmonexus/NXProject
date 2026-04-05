using System.Collections.Generic;

namespace NXProject.Models
{
    public class AIAssistantResponse
    {
        public bool Refused { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = new();
        public List<AITaskSuggestion> Tasks { get; set; } = new();
    }

    public class AITaskSuggestion
    {
        public string Name { get; set; } = string.Empty;
        public int DurationDays { get; set; } = 1;
        public string PredecessorTaskName { get; set; } = string.Empty;
        public string Assignee { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }
}
