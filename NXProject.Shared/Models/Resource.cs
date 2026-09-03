using System;
using System.Collections.Generic;

namespace NXProject.Models
{
    public class Resource
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ResourceType Type { get; set; } = ResourceType.Work;

        /// <summary>Project (padrão) aparece no Mapa de Alocação. Internal aparece na aba Interno.</summary>
        public ResourceKind Kind { get; set; } = ResourceKind.Project;

        // Capacidade em horas por dia (padrão 8h)
        public double MaxUnitsPerDay { get; set; } = 8.0;

        // Custo por hora (opcional, legado)
        public decimal CostPerHour { get; set; } = 0;

        // Modelo de custo: por hora ou por mês
        public ResourceCostType CostType { get; set; } = ResourceCostType.Hourly;

        // Valor mensal (quando CostType == Monthly)
        public decimal MonthlyRate { get; set; } = 0;

        public string? Email { get; set; }
        public string? Notes { get; set; }

        /// <summary>
        /// Equipe (Team) do Azure DevOps de onde a pessoa foi importada.
        /// Vazio/nulo quando a pessoa veio direto do cronograma (exibido como "Cronograma").
        /// </summary>
        public string? Team { get; set; }

        public bool IsImportedFromTfs { get; set; }

        /// <summary>
        /// % do dia útil disponível para este projeto (0–100, padrão 100).
        /// Afeta o prazo das tarefas: 80 % → uma atividade de 40 h leva 40/(8×0,8)=6,25 dias.
        /// Diferente de AllocationPercent (por tarefa): este é o teto global da pessoa.
        /// </summary>
        public double AvailabilityPercent { get; set; } = 100.0;

        /// <summary>
        /// Ausências da pessoa (férias, folga, feriado municipal de outra cidade etc.).
        /// Nesses dias ela não produz: o cálculo de fim da atividade pula essas datas.
        /// Diferente do feriado do calendário, que vale para o cronograma inteiro.
        /// </summary>
        public List<ResourceAbsence> Absences { get; set; } = new();

        public string DisplayName => IsImportedFromTfs ? Name : $"*{Name}";

        public override string ToString() => Name;
    }

    /// <summary>Um dia de ausência da pessoa. Motivo é opcional (só informativo).</summary>
    public class ResourceAbsence
    {
        public DateTime Date { get; set; } = DateTime.Today;
        public string Reason { get; set; } = string.Empty;
    }

    public enum ResourceType
    {
        Work,       // Pessoa
        Material,   // Material/Equipamento
        Cost        // Custo fixo
    }

    public enum ResourceCostType
    {
        Hourly,   // Valor/hora × HH estimadas
        Monthly   // Valor/mês distribuído pelo % de HH no mês
    }

    public enum ResourceKind
    {
        Project,    // Aparece nas abas OPEX/CAPEX do Mapa de Alocação
        Internal    // Aparece na aba Interno do Mapa de Alocação
    }

    public class TaskResource
    {
        public int ResourceId { get; set; }
        public Resource? Resource { get; set; }

        // % de alocação nesta tarefa (ex: 50 = 50%)
        public double AllocationPercent { get; set; } = 100.0;

        // Horas estimadas para esta tarefa (calculado ou manual)
        public double? EstimatedHours { get; set; }

        public override string ToString()
        {
            var name = Resource?.DisplayName ?? Resource?.Name ?? string.Empty;
            return Math.Abs(AllocationPercent - 100.0) < 0.01
                ? name
                : $"({AllocationPercent:0}%) {name}";
        }
    }
}
