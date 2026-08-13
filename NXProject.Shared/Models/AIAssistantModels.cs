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
        public bool HasDurationHours { get; set; }
        public double DurationHours { get; set; }
        public int DurationDays { get; set; } = 1;
        public string PredecessorTaskName { get; set; } = string.Empty;
        public string Assignee { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    /// <summary>Resposta hierarquica do "Fazer Cronograma" (Epic/Feature/Story/Task).</summary>
    public class AIScheduleResponse
    {
        public bool Refused { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = new();
        public List<AIScheduleNode> Roots { get; set; } = new();
    }

    /// <summary>Nó da hierarquia do cronograma gerado pela IA.</summary>
    public class AIScheduleNode
    {
        public string Type { get; set; } = string.Empty;   // N1..N4 ou Epic/Feature/Story/Task
        public string Code { get; set; } = string.Empty;    // codigo WBS hierarquico (ex.: "1.1.2")
        public string Name { get; set; } = string.Empty;
        public double EstimatedHours { get; set; }          // horas de trabalho da folha
        public double DurationDays { get; set; }            // alternativa: dias uteis (convertidos em horas)
        public int Sprint { get; set; }                     // numero da sprint (01, 02, ...) no modo DevOps
        public string Assignee { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        // Âncoras do item EXISTENTE (a IA devolve inalteradas; vazio = item novo):
        public string Id { get; set; } = string.Empty;                 // DisplayId "123:T" / "45:I"
        public List<string> Predecessors { get; set; } = new();         // DisplayIds das predecessoras
        public bool StartFixed { get; set; }
        public bool FinishFixed { get; set; }
        public System.DateTime? FixedStart { get; set; }
        public System.DateTime? FixedFinish { get; set; }
        public double PercentComplete { get; set; }
        public double CurrentHours { get; set; }            // HH Atual (trabalho já feito)
        public double OriginalEstimatedHours { get; set; }  // HH Original (planejado, não muda com o progresso)
        public List<AIScheduleNode> Children { get; set; } = new();
    }
}
