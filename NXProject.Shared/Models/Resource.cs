using System;

namespace NXProject.Models
{
    public class Resource
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ResourceType Type { get; set; } = ResourceType.Work;

        // Capacidade em horas por dia (padrão 8h)
        public double MaxUnitsPerDay { get; set; } = 8.0;

        // Custo por hora (opcional)
        public decimal CostPerHour { get; set; } = 0;

        public string? Email { get; set; }
        public string? Notes { get; set; }

        public override string ToString() => Name;
    }

    public enum ResourceType
    {
        Work,       // Pessoa
        Material,   // Material/Equipamento
        Cost        // Custo fixo
    }

    public class TaskResource
    {
        public int ResourceId { get; set; }
        public Resource? Resource { get; set; }

        // % de alocação nesta tarefa (ex: 50 = 50%)
        public double AllocationPercent { get; set; } = 100.0;

        // Horas estimadas para esta tarefa (calculado ou manual)
        public double? EstimatedHours { get; set; }

        public override string ToString() =>
            $"{Resource?.Name} ({AllocationPercent}%)";
    }
}
