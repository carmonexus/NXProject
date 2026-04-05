using System;
using System.Collections.ObjectModel;

namespace NXProject.Models
{
    public class Project
    {
        public string Name { get; set; } = "Novo Projeto";
        public string? Description { get; set; }
        public string? Author { get; set; }

        public DateTime StartDate { get; set; } = DateTime.Today;

        // Duração do sprint em dias úteis
        public int SprintDurationDays { get; set; } = 14;

        // Número da primeira sprint exibida no cronograma
        public int FirstSprintNumber { get; set; } = 1;

        // Modo de numeracao das sprints: Sequencial, Par ou Impar
        public string SprintNumberingMode { get; set; } = "Sequencial";

        // Quantos dias de duracao equivalem a 1 SFP em cada faixa
        public double LowDaysPerSfp { get; set; } = 1.0;
        public double MediumDaysPerSfp { get; set; } = 1.0;
        public double HighDaysPerSfp { get; set; } = 1.0;

        // Tarefas raiz (hierarquia)
        public ObservableCollection<ProjectTask> Tasks { get; set; } = new();

        // Recursos do projeto
        public ObservableCollection<Resource> Resources { get; set; } = new();

        // Caminho do arquivo salvo
        public string? FilePath { get; set; }

        public bool IsDirty { get; set; } = false;
    }
}
