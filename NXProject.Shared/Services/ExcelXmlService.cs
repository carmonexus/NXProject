using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NXProject.Models;

namespace NXProject.Services
{
    public static class ExcelXmlService
    {
        private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";
        private static readonly XNamespace DefaultNs = "urn:schemas-microsoft-com:office:spreadsheet";

        public static void Export(Project project, List<ProjectTask> tasks, string filePath)
        {
            var workbook = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(DefaultNs + "Workbook",
                    new XAttribute(XNamespace.Xmlns + "ss", Ss),
                    new XElement(DefaultNs + "Worksheet",
                        new XAttribute(Ss + "Name", "Tarefas"),
                        new XElement(DefaultNs + "Table",
                            CreateRow("ID", "Nome", "Nível", "Início", "Fim", "Duração(d)", "% Completo", "Predecessoras", "Sprint", "Horas Est."),
                            tasks.Select(CreateTaskRow)))));

            workbook.Save(filePath);
        }

        public static Project Import(string filePath)
        {
            var document = XDocument.Load(filePath);
            var rows = document.Descendants(DefaultNs + "Row").ToList();
            if (rows.Count <= 1)
                return new Project { Name = Path.GetFileNameWithoutExtension(filePath), FilePath = filePath };

            var tasks = new List<ProjectTask>();
            var maxId = 0;

            foreach (var row in rows.Skip(1))
            {
                var values = ReadRowValues(row);
                if (values.Count == 0 || string.IsNullOrWhiteSpace(values.ElementAtOrDefault(1)))
                    continue;

                var task = new ProjectTask
                {
                    Id = ParseInt(values.ElementAtOrDefault(0), tasks.Count + 1),
                    Name = values.ElementAtOrDefault(1) ?? "Tarefa",
                    Level = ParseInt(values.ElementAtOrDefault(2), 0),
                    Start = ParseDate(values.ElementAtOrDefault(3)) ?? DateTime.Today,
                    Finish = ParseDate(values.ElementAtOrDefault(4)) ?? DateTime.Today.AddDays(1),
                    PercentComplete = ParseDouble(values.ElementAtOrDefault(6), 0),
                    SprintNumber = ParseInt(values.ElementAtOrDefault(8), 0),
                    EstimatedHours = ParseNullableDouble(values.ElementAtOrDefault(9))
                };

                foreach (var pred in SplitList(values.ElementAtOrDefault(7)))
                    task.PredecessorIds.Add(pred);

                if (task.Finish < task.Start)
                    task.Finish = task.Start;

                maxId = Math.Max(maxId, task.Id);
                tasks.Add(task);
            }

            var project = new Project
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                StartDate = tasks.Select(t => t.Start).DefaultIfEmpty(DateTime.Today).Min()
            };

            BuildHierarchy(tasks, project.Tasks);
            return project;
        }

        private static XElement CreateTaskRow(ProjectTask task)
        {
            return CreateRow(
                task.Id.ToString(CultureInfo.InvariantCulture),
                task.Name,
                task.Level.ToString(CultureInfo.InvariantCulture),
                task.Start.ToString("s", CultureInfo.InvariantCulture),
                task.Finish.ToString("s", CultureInfo.InvariantCulture),
                ((int)(task.Finish - task.Start).TotalDays).ToString(CultureInfo.InvariantCulture),
                task.PercentComplete.ToString("0", CultureInfo.InvariantCulture),
                string.Join(",", task.PredecessorIds),
                task.SprintNumber.ToString(CultureInfo.InvariantCulture),
                task.EstimatedHours?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty);
        }

        private static XElement CreateRow(params object?[] values)
        {
            return new XElement(DefaultNs + "Row",
                values.Select(CreateCell));
        }

        private static XElement CreateCell(object? value)
        {
            return new XElement(DefaultNs + "Cell",
                new XElement(DefaultNs + "Data",
                    new XAttribute(Ss + "Type", "String"),
                    value?.ToString() ?? string.Empty));
        }

        private static List<string> ReadRowValues(XElement row)
        {
            var values = new List<string>();
            var currentIndex = 1;

            foreach (var cell in row.Elements(DefaultNs + "Cell"))
            {
                var indexAttr = cell.Attribute(Ss + "Index");
                if (indexAttr != null && int.TryParse(indexAttr.Value, out var explicitIndex))
                {
                    while (currentIndex < explicitIndex)
                    {
                        values.Add(string.Empty);
                        currentIndex++;
                    }
                }

                values.Add(cell.Element(DefaultNs + "Data")?.Value ?? string.Empty);
                currentIndex++;
            }

            return values;
        }

        private static void BuildHierarchy(List<ProjectTask> flatTasks, System.Collections.ObjectModel.ObservableCollection<ProjectTask> rootTasks)
        {
            var parentStack = new Stack<ProjectTask>();

            foreach (var task in flatTasks)
            {
                while (parentStack.Count > task.Level)
                    parentStack.Pop();

                if (parentStack.Count == 0)
                {
                    task.Parent = null;
                    rootTasks.Add(task);
                }
                else
                {
                    task.Parent = parentStack.Peek();
                    task.Parent.Children.Add(task);
                    task.Parent.IsSummary = true;
                }

                parentStack.Push(task);
            }

            foreach (var task in rootTasks)
                task.RecalcSummary();
        }

        private static DateTime? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out var current))
                return current;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant))
                return invariant;

            return null;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static double ParseDouble(string? value, double fallback)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
                return invariant;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var current))
                return current;

            return fallback;
        }

        private static double? ParseNullableDouble(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return ParseDouble(value, 0);
        }

        private static IEnumerable<int> SplitList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    yield return parsed;
            }
        }
    }
}
