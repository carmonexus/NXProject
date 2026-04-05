using System;
using System.Collections.Generic;

namespace NXProject.Models
{
    public class Sprint
    {
        public int Number { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Name => $"Sprint {Number}";

        public List<ProjectTask> Tasks { get; set; } = new();
    }
}
