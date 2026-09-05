// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.ObjectModel;

namespace NXProject.Models
{
    public class ProjectCalendar
    {
        public double WorkingHoursPerDay { get; set; } = 8.0;
        public bool TreatSaturdayAsWorkday { get; set; }
        public bool TreatSundayAsWorkday { get; set; }
        public ObservableCollection<ProjectHoliday> Holidays { get; set; } = new();
    }

    public class ProjectHoliday
    {
        public DateTime Date { get; set; } = DateTime.Today;
        public string Name { get; set; } = string.Empty;
    }
}
