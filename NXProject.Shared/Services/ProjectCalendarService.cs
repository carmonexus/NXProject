// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    public static class ProjectCalendarService
    {
        public const string FileName = "nxproject_calender.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static ProjectCalendar Current { get; private set; } = new();

        /// <summary>Origem do calendário ativo, para alertar no banner do cronograma.</summary>
        public enum CalendarOrigin { General, Schedule, Error }

        /// <summary>Qual calendário está em uso: Geral (settings), Cronograma (.nxp) ou Erro (padrão 8h).</summary>
        public static CalendarOrigin Origin { get; private set; } = CalendarOrigin.General;

        public static string GetCalendarPath(string storageKey = "NXProject.Community")
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                storageKey);
            return Path.Combine(dir, FileName);
        }

        public static ProjectCalendar Load(string storageKey = "NXProject.Community")
        {
            var path = GetCalendarPath(storageKey);
            try
            {
                if (!File.Exists(path))
                {
                    Current = new ProjectCalendar();
                    Save(Current, storageKey);
                    Origin = CalendarOrigin.General;
                    return Current;
                }

                var json = File.ReadAllText(path);
                Current = Normalize(JsonSerializer.Deserialize<ProjectCalendar>(json));
                Origin = CalendarOrigin.General;
            }
            catch
            {
                // Falha ao ler/interpretar o calendário geral → cai no padrão 8h (alerta "Erro").
                Current = new ProjectCalendar();
                Origin = CalendarOrigin.Error;
            }

            return Current;
        }

        public static void Save(ProjectCalendar calendar, string storageKey = "NXProject.Community")
        {
            Current = Normalize(calendar);
            var path = GetCalendarPath(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
        }

        /// <summary>
        /// Define o calendário ativo em memória (sem gravar em disco). Usado ao
        /// abrir um cronograma que traz calendário próprio embutido no .nxp.
        /// </summary>
        public static void SetCurrent(ProjectCalendar calendar)
        {
            Current = Normalize(calendar);
            Origin = CalendarOrigin.Schedule;
        }

        /// <summary>Cópia profunda de um calendário (para copiar Geral ↔ Cronograma).</summary>
        public static ProjectCalendar Clone(ProjectCalendar source)
        {
            var copy = new ProjectCalendar
            {
                WorkingHoursPerDay = source.WorkingHoursPerDay,
                TreatSaturdayAsWorkday = source.TreatSaturdayAsWorkday,
                TreatSundayAsWorkday = source.TreatSundayAsWorkday
            };
            foreach (var h in source.Holidays)
                copy.Holidays.Add(new ProjectHoliday { Date = h.Date, Name = h.Name });
            return copy;
        }

        // Filtro de arquivo para exportar/importar o calendário (compartilhável).
        public const string FileFilter =
            "Calendário NXProject (*.nxcal;*.json)|*.nxcal;*.json|Todos os arquivos (*.*)|*.*";

        /// <summary>Exporta um calendário para um arquivo (ex.: em drive de rede compartilhado).</summary>
        public static void ExportToFile(ProjectCalendar calendar, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Normalize(calendar), JsonOptions));
        }

        /// <summary>Importa um calendário de um arquivo exportado.</summary>
        public static ProjectCalendar ImportFromFile(string path)
        {
            var json = File.ReadAllText(path);
            return Normalize(JsonSerializer.Deserialize<ProjectCalendar>(json));
        }

        public static bool IsWorkingDay(DateTime date) => IsWorkingDay(date, Current);

        public static double WorkingHoursPerDay =>
            Current.WorkingHoursPerDay <= 0 ? 8.0 : Current.WorkingHoursPerDay;

        /// <summary>Dia útil considerando também as ausências do(s) recurso(s):
        /// um dia em que a pessoa está ausente não produz para ela.</summary>
        public static bool IsWorkingDay(DateTime date, ProjectCalendar? calendar,
            IReadOnlyCollection<DateTime>? absentDays)
        {
            if (absentDays != null && absentDays.Count > 0 && absentDays.Contains(date.Date))
                return false;
            return IsWorkingDay(date, calendar);
        }

        public static bool IsWorkingDay(DateTime date, ProjectCalendar? calendar)
        {
            calendar ??= Current;
            var day = date.Date;
            if (calendar.Holidays.Any(h => h.Date.Date == day))
                return false;

            return day.DayOfWeek switch
            {
                DayOfWeek.Saturday => calendar.TreatSaturdayAsWorkday,
                DayOfWeek.Sunday => calendar.TreatSundayAsWorkday,
                _ => true
            };
        }

        public static DateTime AddWorkingDays(DateTime start, int days) => AddWorkingDays(start, days, Current);

        public static DateTime AddWorkingDays(DateTime start, int days, ProjectCalendar? calendar)
        {
            if (days <= 0)
                return start.Date;

            var date = start.Date;
            var added = 0;
            while (added < days)
            {
                date = date.AddDays(1);
                if (IsWorkingDay(date, calendar))
                    added++;
            }

            return date;
        }

        public static DateTime AddWorkingHours(DateTime start, double hours) => AddWorkingHours(start, hours, Current);

        /// <summary>Soma horas úteis pulando também os dias de ausência informados.</summary>
        public static DateTime AddWorkingHours(DateTime start, double hours,
            ProjectCalendar? calendar, IReadOnlyCollection<DateTime>? absentDays)
        {
            if (hours <= 0)
                return start;

            calendar ??= Current;
            var current = start;
            var remainingHours = hours;
            while (remainingHours > 0)
            {
                if (!IsWorkingDay(current.Date, calendar, absentDays))
                {
                    current = current.Date.AddDays(1);
                    continue;
                }

                var dayCapacity = WorkingHoursPerDay;
                var hoursToAdd = Math.Min(remainingHours, dayCapacity);
                current = current.AddDays(hoursToAdd / dayCapacity);
                remainingHours -= hoursToAdd;
            }

            return current;
        }

        public static DateTime AddWorkingHours(DateTime start, double hours, ProjectCalendar? calendar)
        {
            if (hours <= 0)
                return start;

            calendar ??= Current;
            var current = start;
            var remainingHours = hours;
            while (remainingHours > 0)
            {
                if (!IsWorkingDay(current.Date, calendar))
                {
                    current = current.Date.AddDays(1);
                    continue;
                }

                var dayCapacity = WorkingHoursPerDay;
                var hoursToAdd = Math.Min(remainingHours, dayCapacity);
                current = current.AddDays(hoursToAdd / dayCapacity);
                remainingHours -= hoursToAdd;
            }

            return current;
        }

        // Subtrai horas úteis de uma data de fim para obter o início correspondente.
        public static DateTime SubtractWorkingHours(DateTime finish, double hours)
        {
            if (hours <= 0) return finish;
            var calendar = Current;
            var current = finish;
            var remaining = hours;
            while (remaining > 0)
            {
                current = current.AddDays(-1);
                if (!IsWorkingDay(current.Date, calendar))
                    continue;
                var cap = WorkingHoursPerDay;
                var chunk = Math.Min(remaining, cap);
                remaining -= chunk;
            }
            return current;
        }

        public static double CountWorkingHours(DateTime start, DateTime finish) => CountWorkingHours(start, finish, Current);

        public static double CountWorkingHours(DateTime start, DateTime finish, ProjectCalendar? calendar)
        {
            if (finish <= start)
                return 0.0;

            calendar ??= Current;
            var hours = 0.0;
            var current = start;
            while (current < finish)
            {
                if (!IsWorkingDay(current.Date, calendar))
                {
                    current = current.Date.AddDays(1);
                    continue;
                }

                var nextBoundary = current.Date.AddDays(1);
                var intervalEnd = finish < nextBoundary ? finish : nextBoundary;
                hours += (intervalEnd - current).TotalDays * WorkingHoursPerDay;
                current = intervalEnd;
            }

            return hours;
        }

        public static DateTime GetInclusiveFinishDate(DateTime start, DateTime finish) =>
            GetInclusiveFinishDate(start, finish, Current);

        public static DateTime GetInclusiveFinishDate(DateTime start, DateTime finish, ProjectCalendar? calendar)
        {
            if (finish <= start)
                return finish.Date;

            calendar ??= Current;
            if (finish.TimeOfDay != TimeSpan.Zero)
                return finish.Date;

            var date = finish.Date.AddDays(-1);
            while (date > start.Date && !IsWorkingDay(date, calendar))
                date = date.AddDays(-1);

            return date;
        }

        /// <summary>
        /// Data de referência da atividade para fins de sprint ("onde a atividade está"):
        /// 0% → início; em andamento → início + (% × duração útil); 100% → fim inclusivo.
        /// </summary>
        public static DateTime GetProgressReferenceDate(DateTime start, DateTime finish, double percentComplete)
        {
            if (percentComplete <= 0)
                return start.Date;
            if (percentComplete >= 100)
                return GetInclusiveFinishDate(start, finish).Date;

            var total = CountWorkingHours(start, finish);
            if (total <= 0)
                return start.Date;

            var target = percentComplete / 100.0 * total;
            return AddWorkingHours(start, target).Date;
        }

        public static int CountWorkingDays(DateTime start, DateTime finish) => CountWorkingDays(start, finish, Current);

        public static int CountWorkingDays(DateTime start, DateTime finish, ProjectCalendar? calendar)
        {
            if (finish <= start)
                return 0;

            calendar ??= Current;
            var days = 0;
            var current = start.Date;
            var finishDate = finish.Date;
            while (current < finishDate)
            {
                if (IsWorkingDay(current, calendar))
                    days++;
                current = current.AddDays(1);
            }

            return days;
        }

        private static ProjectCalendar Normalize(ProjectCalendar? calendar)
        {
            calendar ??= new ProjectCalendar();
            var normalized = new ProjectCalendar
            {
                WorkingHoursPerDay = calendar.WorkingHoursPerDay <= 0 ? 8.0 : calendar.WorkingHoursPerDay,
                TreatSaturdayAsWorkday = calendar.TreatSaturdayAsWorkday,
                TreatSundayAsWorkday = calendar.TreatSundayAsWorkday
            };

            foreach (var holiday in calendar.Holidays
                         .Where(h => h.Date != default)
                         .GroupBy(h => h.Date.Date)
                         .Select(g => g.First())
                         .OrderBy(h => h.Date))
            {
                normalized.Holidays.Add(new ProjectHoliday
                {
                    Date = holiday.Date.Date,
                    Name = holiday.Name?.Trim() ?? string.Empty
                });
            }

            return normalized;
        }
    }
}
