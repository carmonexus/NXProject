// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

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
            var styles = new XElement(DefaultNs + "Styles",
                StyledCell(DefaultNs, "Default"),
                StyledCell(DefaultNs, "H",  bg: "#1D3F73", fg: "#FFFFFF", bold: true),
                StyledCell(DefaultNs, "S0", bg: "#2B579A", fg: "#FFFFFF", bold: true),
                StyledCell(DefaultNs, "S1", bg: "#D6E4F7", fg: "#1E2840", bold: true, italic: true),
                StyledCell(DefaultNs, "S2", bg: "#F0F4FA", fg: "#1E2840"),
                StyledCell(DefaultNs, "SN", bg: "#F0F4FA", fg: "#1E2840", hAlign: "Right", numFmt: "0.0")
            );

            var tableChildren = new List<object>();

            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "40")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "250")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "40")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "90")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "90")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "70")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "70")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "90")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "50")));
            tableChildren.Add(new XElement(DefaultNs + "Column", new XAttribute(Ss + "Width", "70")));

            var hdrRow = new XElement(DefaultNs + "Row", new XAttribute(Ss + "Height", "22"));
            foreach (var h in new[] { "ID", "Nome", "Nível", "Início", "Fim", "Duração(d)", "% Completo", "Predecessoras", "Sprint", "Horas Est." })
                hdrRow.Add(StyledData(DefaultNs, h, "H"));
            tableChildren.Add(hdrRow);

            tableChildren.AddRange(tasks.Select(CreateTaskRow));

            var workbook = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(DefaultNs + "Workbook",
                    new XAttribute(XNamespace.Xmlns + "ss", Ss),
                    styles,
                    new XElement(DefaultNs + "Worksheet",
                        new XAttribute(Ss + "Name", "Tarefas"),
                        new XElement(DefaultNs + "Table", tableChildren))));

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
            string style = task.Level == 0 ? "S0" : task.IsSummary ? "S1" : "S2";
            var row = new XElement(DefaultNs + "Row", new XAttribute(Ss + "Height", "18"));
            row.Add(StyledData(DefaultNs, task.Id.ToString(CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, task.Name, style));
            row.Add(StyledData(DefaultNs, task.Level.ToString(CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, task.Start.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, task.Finish.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, ((int)(task.Finish - task.Start).TotalDays).ToString(CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, task.PercentComplete.ToString("0", CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs, string.Join(",", task.PredecessorIds), style));
            row.Add(StyledData(DefaultNs, task.SprintNumber.ToString(CultureInfo.InvariantCulture), style));
            row.Add(StyledData(DefaultNs,
                task.EstimatedHours.HasValue ? task.EstimatedHours.Value.ToString("0.0", CultureInfo.InvariantCulture) : "",
                task.EstimatedHours.HasValue ? "SN" : style));
            return row;
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

        private static XElement StyledCell(XNamespace ns, string id,
            string? bg = null, string fg = "#000000",
            bool bold = false, bool italic = false,
            string hAlign = "Left", string? numFmt = null)
        {
            var style = new XElement(ns + "Style", new XAttribute(ns + "ID", id));
            style.Add(new XElement(ns + "Alignment",
                new XAttribute(ns + "Horizontal", hAlign),
                new XAttribute(ns + "Vertical",   "Center")));
            var font = new XElement(ns + "Font",
                new XAttribute(ns + "FontName", "Calibri"),
                new XAttribute(ns + "Size", "10"),
                new XAttribute(ns + "Color", fg));
            if (bold)   font.Add(new XAttribute(ns + "Bold",   "1"));
            if (italic) font.Add(new XAttribute(ns + "Italic", "1"));
            style.Add(font);
            if (bg != null)
                style.Add(new XElement(ns + "Interior",
                    new XAttribute(ns + "Color",   bg),
                    new XAttribute(ns + "Pattern", "Solid")));
            style.Add(new XElement(ns + "Borders",
                StyledBorder(ns, "Bottom"), StyledBorder(ns, "Left"),
                StyledBorder(ns, "Right"),  StyledBorder(ns, "Top")));
            if (numFmt != null)
                style.Add(new XElement(ns + "NumberFormat",
                    new XAttribute(ns + "Format", numFmt)));
            return style;
        }

        private static XElement StyledBorder(XNamespace ns, string position) =>
            new(ns + "Border",
                new XAttribute(ns + "Position",  position),
                new XAttribute(ns + "LineStyle", "Continuous"),
                new XAttribute(ns + "Weight",    "1"),
                new XAttribute(ns + "Color",     "#C8D0DC"));

        private static XElement StyledData(XNamespace ns, string value, string styleId)
        {
            var cell = new XElement(ns + "Cell", new XAttribute(ns + "StyleID", styleId));
            cell.Add(new XElement(ns + "Data",
                new XAttribute(ns + "Type", "String"), value));
            return cell;
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
