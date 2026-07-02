using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXTestUnit;

internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests =
    [
        ("Calendario: fim exclusivo aparece como fim inclusivo correto", CalendarInclusiveFinishUsesPreviousWorkingDay),
        ("Calendario: horas uteis ignoram fim de semana", CalendarWorkingHoursSkipWeekend),
        ("Calendario: feriado nao consome capacidade", CalendarHolidayIsNotWorkingCapacity),
        ("Cronograma: HH estimado calcula fim exclusivo", ScheduleEstimatedHoursCalculatesExclusiveFinish),
        ("Cronograma: percentual de alocacao aumenta duracao calendario", ScheduleAllocationPercentChangesCalendarDuration),
        ("Cronograma: matriz de percentual de alocacao do recurso", ScheduleResourceAllocationPercentMatrix),
        ("Cronograma: disponibilidade do recurso aumenta duracao calendario", ScheduleResourceAvailabilityPercentChangesCalendarDuration),
        ("Cronograma: tarefa 100% nao recalcula fim", ScheduleCompletedTaskKeepsFinish),
        ("Resumo: datas e percentual consolidam filhos", SummaryRollupUsesChildrenDatesAndHours),
        ("Predecessor virtual: aplica fila por mesmo recurso e recalcula fim", VirtualPredecessorQueuesSameResourceSiblings),
        ("Predecessor virtual: mudanca de duracao recalcula inicio e fim das seguintes", VirtualPredecessorDurationChangeCascadesFinish)
    ];

    private static int Main()
    {
        SetCurrentCalendar(new ProjectCalendar());

        var failures = new List<string>();
        Console.WriteLine("NXTestUnit - testes criticos de cronograma");
        Console.WriteLine();

        foreach (var (name, test) in Tests)
        {
            try
            {
                ResetCalendar();
                test();
                Console.WriteLine($"[OK]   {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"[FAIL] {name}");
                Console.WriteLine($"       {ex.Message}");
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine($"NXTestUnit concluido: {Tests.Count} testes passaram.");
            return 0;
        }

        Console.WriteLine($"NXTestUnit falhou: {failures.Count} de {Tests.Count} testes falharam.");
        foreach (var failure in failures)
            Console.WriteLine($" - {failure}");

        return 1;
    }

    private static void ResetCalendar()
    {
        SetCurrentCalendar(new ProjectCalendar
        {
            WorkingHoursPerDay = 8,
            TreatSaturdayAsWorkday = false,
            TreatSundayAsWorkday = false
        });
    }

    private static void CalendarInclusiveFinishUsesPreviousWorkingDay()
    {
        var start = new DateTime(2026, 7, 3); // sexta-feira
        var finish = new DateTime(2026, 7, 7); // terca-feira 00:00, fim exclusivo apos segunda

        var inclusive = ProjectCalendarService.GetInclusiveFinishDate(start, finish);

        AssertEqual(new DateTime(2026, 7, 6), inclusive, "O fim exibido deve ser a segunda-feira trabalhada, nao a terca exclusiva.");
    }

    private static void CalendarWorkingHoursSkipWeekend()
    {
        var start = new DateTime(2026, 7, 3); // sexta-feira
        var finish = ProjectCalendarService.AddWorkingHours(start, 16);

        AssertEqual(new DateTime(2026, 7, 7), finish, "16h iniciando sexta devem terminar na terca 00:00.");
        AssertEqual(16, ProjectCalendarService.CountWorkingHours(start, finish), "A contagem deve ignorar sabado e domingo.");
    }

    private static void CalendarHolidayIsNotWorkingCapacity()
    {
        SetCurrentCalendar(new ProjectCalendar
        {
            WorkingHoursPerDay = 8,
            Holidays = { new ProjectHoliday { Date = new DateTime(2026, 7, 6), Name = "Feriado teste" } }
        });

        var start = new DateTime(2026, 7, 3); // sexta-feira
        var finish = ProjectCalendarService.AddWorkingHours(start, 16);

        AssertEqual(new DateTime(2026, 7, 8), finish, "Com segunda feriado, 16h iniciando sexta devem terminar quarta 00:00.");
        AssertEqual(new DateTime(2026, 7, 7), ProjectCalendarService.GetInclusiveFinishDate(start, finish), "O fim inclusivo deve pular o feriado.");
    }

    private static void ScheduleEstimatedHoursCalculatesExclusiveFinish()
    {
        var task = new ProjectTask
        {
            Name = "Task 16h",
            Start = new DateTime(2026, 7, 6),
            EstimatedHours = 16
        };

        task.Finish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);

        AssertEqual(new DateTime(2026, 7, 8), task.Finish, "16h devem ocupar dois dias uteis e terminar no limite exclusivo do terceiro dia.");
        AssertEqual(new DateTime(2026, 7, 7), ProjectCalendarService.GetInclusiveFinishDate(task.Start, task.Finish), "A data exibida deve ser o segundo dia util.");
    }

    private static void ScheduleAllocationPercentChangesCalendarDuration()
    {
        var task = new ProjectTask
        {
            Name = "Task 8h a 50%",
            Start = new DateTime(2026, 7, 6)
        };
        task.Resources.Add(new TaskResource { AllocationPercent = 50, EstimatedHours = 8 });

        var finish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);

        AssertEqual(new DateTime(2026, 7, 8), finish, "8h a 50% devem virar 16h de calendario.");
    }

    private static void ScheduleResourceAllocationPercentMatrix()
    {
        AssertAllocationFinish(25, 8, new DateTime(2026, 7, 10), "8h a 25% devem virar 32h de calendario.");
        AssertAllocationFinish(50, 8, new DateTime(2026, 7, 8), "8h a 50% devem virar 16h de calendario.");
        AssertAllocationFinish(100, 8, new DateTime(2026, 7, 7), "8h a 100% devem ocupar 1 dia util.");
        AssertAllocationFinish(120, 12, new DateTime(2026, 7, 7, 6, 0, 0), "12h a 120% devem virar 10h de calendario.");
    }

    private static void ScheduleResourceAvailabilityPercentChangesCalendarDuration()
    {
        var resource = new Resource { Id = 1, Name = "Dev 50%", AvailabilityPercent = 50 };
        var task = new ProjectTask
        {
            Name = "Task 8h com pessoa 50%",
            Start = new DateTime(2026, 7, 6)
        };
        task.Resources.Add(new TaskResource
        {
            Resource = resource,
            ResourceId = resource.Id,
            AllocationPercent = 100,
            EstimatedHours = 8
        });

        var finish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);

        AssertEqual(new DateTime(2026, 7, 8), finish, "Recurso disponivel 50% no projeto deve dobrar a duracao calendario.");
    }

    private static void ScheduleCompletedTaskKeepsFinish()
    {
        var originalFinish = new DateTime(2026, 7, 7);
        var task = new ProjectTask
        {
            Name = "Task fechada",
            Start = new DateTime(2026, 7, 6),
            Finish = originalFinish,
            EstimatedHours = 80,
            PercentComplete = 100
        };

        TaskScheduleService.RecalculateFinishFromAssignments(task);

        AssertEqual(originalFinish, task.Finish, "Task 100% nao deve ter fim recalculado por HH restante.");
    }

    private static void SummaryRollupUsesChildrenDatesAndHours()
    {
        var summary = new ProjectTask
        {
            Name = "Feature",
            IsSummary = true
        };
        var first = new ProjectTask
        {
            Name = "A",
            Parent = summary,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            CurrentHours = 2,
            EstimatedHours = 6
        };
        var second = new ProjectTask
        {
            Name = "B",
            Parent = summary,
            Start = new DateTime(2026, 7, 7),
            Finish = new DateTime(2026, 7, 9),
            CurrentHours = 4,
            EstimatedHours = 4
        };
        summary.Children.Add(first);
        summary.Children.Add(second);

        summary.RecalcSummary();

        AssertEqual(new DateTime(2026, 7, 6), summary.Start, "Resumo deve iniciar no menor inicio dos filhos.");
        AssertEqual(new DateTime(2026, 7, 9), summary.Finish, "Resumo deve terminar no maior fim exclusivo dos filhos.");
        AssertEqual(37.5, summary.PercentComplete, "Percentual do resumo deve usar HH Atual / (HH Atual + HH Restante).");
    }

    private static void VirtualPredecessorQueuesSameResourceSiblings()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var (vm, first, second, third) = CreateVirtualPredecessorScenario(resource);

        vm.ApplyVirtualPredecessorsToAll();

        AssertEqual(new DateTime(2026, 7, 6), first.Start, "A primeira tarefa deve manter o inicio original.");
        AssertEqual(new DateTime(2026, 7, 7), first.Finish, "A primeira tarefa de 8h deve terminar no limite exclusivo do dia seguinte.");
        AssertEqual(new DateTime(2026, 7, 7), second.Start, "A segunda tarefa deve iniciar no proximo dia util apos o fim visivel da primeira.");
        AssertEqual(new DateTime(2026, 7, 8), second.Finish, "A segunda tarefa deve ter o fim recalculado a partir do novo inicio.");
        AssertEqual(new DateTime(2026, 7, 8), third.Start, "A terceira tarefa deve encadear apos a segunda.");
        AssertEqual(new DateTime(2026, 7, 9), third.Finish, "A terceira tarefa deve ter o fim recalculado a partir do novo inicio.");
    }

    private static void VirtualPredecessorDurationChangeCascadesFinish()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var (vm, first, second, third) = CreateVirtualPredecessorScenario(resource);
        vm.ApplyVirtualPredecessorsToAll();

        var firstVm = vm.FlatTasks.First(t => ReferenceEquals(t.Model, first));
        firstVm.DurationHours = 16;

        AssertEqual(new DateTime(2026, 7, 8), first.Finish, "Ao aumentar a primeira para 16h, seu fim exclusivo deve mudar.");
        AssertEqual(new DateTime(2026, 7, 8), second.Start, "A segunda deve ser empurrada pela predecessora virtual recalculada.");
        AssertEqual(new DateTime(2026, 7, 9), second.Finish, "A segunda deve recalcular o fim a partir do novo inicio.");
        AssertEqual(new DateTime(2026, 7, 9), third.Start, "A terceira deve acompanhar a cascata da segunda.");
        AssertEqual(new DateTime(2026, 7, 10), third.Finish, "A terceira deve recalcular o fim a partir do novo inicio.");
    }

    private static (MainViewModel Vm, ProjectTask First, ProjectTask Second, ProjectTask Third) CreateVirtualPredecessorScenario(Resource resource)
    {
        var parent = new ProjectTask
        {
            Id = 10,
            Name = "Story",
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7)
        };
        var first = CreateAssignedTask(1, "A", resource, new DateTime(2026, 7, 6), parent);
        var second = CreateAssignedTask(2, "B", resource, new DateTime(2026, 7, 6), parent);
        var third = CreateAssignedTask(3, "C", resource, new DateTime(2026, 7, 6), parent);
        parent.Children.Add(first);
        parent.Children.Add(second);
        parent.Children.Add(third);

        var project = new Project
        {
            Name = "Teste predecessor virtual",
            StartDate = new DateTime(2026, 7, 6)
        };
        project.Resources.Add(resource);
        project.Tasks.Add(parent);

        var vm = new MainViewModel("NXTestUnit")
        {
            Project = project
        };
        vm.RebuildFlatTasks();

        return (vm, first, second, third);
    }

    private static ProjectTask CreateAssignedTask(int id, string name, Resource resource, DateTime start, ProjectTask parent)
    {
        var task = new ProjectTask
        {
            Id = id,
            Name = name,
            Parent = parent,
            Start = start,
            Finish = start.AddDays(1),
            EstimatedHours = 8
        };
        task.Resources.Add(new TaskResource
        {
            Resource = resource,
            ResourceId = resource.Id,
            AllocationPercent = 100,
            EstimatedHours = 8
        });
        return task;
    }

    private static void AssertAllocationFinish(double allocationPercent, double estimatedHours, DateTime expectedFinish, string message)
    {
        var resource = new Resource { Id = 1, Name = $"Dev {allocationPercent:0}%", AvailabilityPercent = 100 };
        var task = new ProjectTask
        {
            Name = $"Task {estimatedHours:0.#}h a {allocationPercent:0}%",
            Start = new DateTime(2026, 7, 6)
        };
        task.Resources.Add(new TaskResource
        {
            Resource = resource,
            ResourceId = resource.Id,
            AllocationPercent = allocationPercent,
            EstimatedHours = estimatedHours
        });

        var finish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);

        AssertEqual(expectedFinish, finish, message);
    }

    private static void AssertEqual(DateTime expected, DateTime actual, string message)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{message} Esperado: {expected:yyyy-MM-dd HH:mm}; Atual: {actual:yyyy-MM-dd HH:mm}.");
    }

    private static void AssertEqual(double expected, double actual, string message, double tolerance = 0.0001)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message} Esperado: {expected:0.####}; Atual: {actual:0.####}.");
    }

    private static void SetCurrentCalendar(ProjectCalendar calendar)
    {
        var property = typeof(ProjectCalendarService).GetProperty(nameof(ProjectCalendarService.Current))
            ?? throw new InvalidOperationException("ProjectCalendarService.Current nao encontrado.");
        property.SetValue(null, calendar);
    }
}
