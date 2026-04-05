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

        // Tarefas raiz (hierarquia)
        public ObservableCollection<ProjectTask> Tasks { get; set; } = new();

        // Recursos do projeto
        public ObservableCollection<Resource> Resources { get; set; } = new();

        // Caminho do arquivo salvo
        public string? FilePath { get; set; }

        public bool IsDirty { get; set; } = false;
    }
}
