using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;

namespace NXTestUnit;

internal static class Program
{
    private static string _solutionRoot = "";

    private static readonly List<(string Name, Action Test)> ScheduleTests =
    [
        ("Calendario: fim exclusivo aparece como fim inclusivo correto", CalendarInclusiveFinishUsesPreviousWorkingDay),
        ("Calendario: horas uteis ignoram fim de semana", CalendarWorkingHoursSkipWeekend),
        ("Calendario: feriado nao consome capacidade", CalendarHolidayIsNotWorkingCapacity),
        ("Cronograma: HH estimado calcula fim exclusivo", ScheduleEstimatedHoursCalculatesExclusiveFinish),
        ("Cronograma: percentual de alocacao aumenta duracao calendario", ScheduleAllocationPercentChangesCalendarDuration),
        ("Cronograma: matriz de percentual de alocacao do recurso", ScheduleResourceAllocationPercentMatrix),
        ("Cronograma: disponibilidade do recurso aumenta duracao calendario", ScheduleResourceAvailabilityPercentChangesCalendarDuration),
        ("Cronograma: tarefa 100% nao recalcula fim", ScheduleCompletedTaskKeepsFinish),
        ("Cronograma: rebuild preserva fim de tarefa 100%", RebuildKeepsCompletedTaskFinish),
        ("Cronograma: No DevOps aceita duracao zero como marco", ScheduleNoDevOpsZeroDurationCreatesMilestone),
        ("Cronograma: Marco-Devops aceita somente duracao zero", ScheduleDevOpsMilestoneAcceptsOnlyZeroDuration),
        ("Cronograma: DevOps nao aceita duracao zero como marco local", ScheduleDevOpsZeroDurationIsIgnored),
        ("Cronograma: ID negativo NoDevOps aparece como interno", NoDevOpsNegativeTfsIdDisplaysAsInternal),
        ("Cronograma: DevOps pendente continua com ID interno", PendingDevOpsCreateDisplaysAsInternal),
        ("Cronograma: DevOps aceita predecessor I apenas se I tambem for DevOps", DevOpsPredecessorAcceptsInternalDevOpsOnly),
        ("Cronograma: NoDevOps aceita predecessor I de qualquer tipo", NoDevOpsPredecessorAcceptsAnyInternalType),
        ("Cronograma: botao marco cria Marco-Devops irmao para selecao DevOps", AddMilestoneCreatesDevOpsSiblingForDevOpsSelection),
        ("Cronograma: Ctrl botao marco cria Marco-Devops filho", AddMilestoneCreatesDevOpsChildWithCtrl),
        ("Cronograma: Ctrl botao marco nao cria filho em marco", AddMilestoneDoesNotCreateChildUnderMilestone),
        ("Sync TFS: Marco-Devops cria Task com tag MARCO-PROJECT", DevOpsMilestoneCreateOpsAddsMarcoProjectTag),
        ("Sync TFS: data fim usa fim inclusivo", TfsSyncFinishUsesInclusiveDate),
        ("Sync TFS: Marco-Devops usa irmao anterior como predecessora implicita", DevOpsMilestoneUsesPreviousSiblingAsImplicitPredecessor),
        ("Sync TFS: Marco-Devops sem irmao anterior usa pai como predecessora implicita", DevOpsMilestoneUsesParentAsImplicitPredecessor),
        ("Sync TFS: Marco-Devops resolve predecessora explicita com filhos", DevOpsMilestoneResolvesExplicitPredecessorWithChildren),
        ("Import TFS: Marco-Devops ignora predecessora fora da hierarquia para posicionar", DevOpsMilestonePositionIgnoresExternalHierarchyPredecessor),
        ("Import TFS: Marco-Devops usa pai como ancora de posicionamento", DevOpsMilestonePositionUsesParentAnchor),
        ("Import TFS: NoDevOps preserva posicao para predecessora virtual", ImportPreservesNoDevOpsSiblingPosition),
        ("Import TFS: atividades internas DevOps sao vinculadas por nome ou preservadas", ImportMatchesOrPreservesInternalDevOpsActivities),
        ("Resumo: datas e percentual consolidam filhos", SummaryRollupUsesChildrenDatesAndHours),
        ("Predecessor virtual: aplica fila por mesmo recurso e recalcula fim", VirtualPredecessorQueuesSameResourceSiblings),
        ("Predecessor virtual: mudanca de duracao recalcula inicio e fim das seguintes", VirtualPredecessorDurationChangeCascadesFinish),
        ("Setup update: sem baseline conhecida nao dispara reinstalacao", SetupUpdateNoBaselineDoesNotTrigger),
        ("Setup update: asset igual a baseline nao dispara reinstalacao", SetupUpdateSameTimestampDoesNotTrigger),
        ("Setup update: asset mais antigo que a baseline nao dispara reinstalacao", SetupUpdateOlderAssetDoesNotTrigger),
        ("Setup update: asset mais novo que a baseline dispara reinstalacao", SetupUpdateNewerAssetTriggers)
    ];

    private static int Main(string[] args)
    {
        var category = args.Length > 0 ? args[0] : "schedule";
        _solutionRoot = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
        var isSchedule = category.Equals("schedule", StringComparison.OrdinalIgnoreCase);

        List<(string Name, Action Test)> tests = category.ToLowerInvariant() switch
        {
            "packaging-community" => [PackagingTests[0]],
            "packaging-setup" => [PackagingTests[1]],
            "packaging" => PackagingTests,
            _ => ScheduleTests
        };

        if (isSchedule)
            SetCurrentCalendar(new ProjectCalendar());

        var failures = new List<string>();
        Console.WriteLine(isSchedule
            ? "NXTestUnit - testes criticos de cronograma"
            : "NXTestUnit - validacao de empacotamento (zips de release)");
        Console.WriteLine();

        foreach (var (name, test) in tests)
        {
            try
            {
                if (isSchedule)
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
            Console.WriteLine($"NXTestUnit concluido: {tests.Count} testes passaram.");
            return 0;
        }

        Console.WriteLine($"NXTestUnit falhou: {failures.Count} de {tests.Count} testes falharam.");
        foreach (var failure in failures)
            Console.WriteLine($" - {failure}");

        return 1;
    }

    private static readonly List<(string Name, Action Test)> PackagingTests =
    [
        ("Empacotamento: NXProject.Community-Release.zip contem arquivos essenciais", ValidateCommunityReleaseZip),
        ("Empacotamento: NXProject-Setup.zip contem runtime e libs essenciais", ValidateSetupZip)
    ];

    private static void ValidateCommunityReleaseZip()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "community", "NXProject.Community-Release.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "release-zip-required-files.json");
        ValidateZipAgainstManifest(zipPath, manifestPath);
        ValidateSelfContainedNotSingleFile(zipPath, "NXProject.Community.exe", "NXProject.Community.dll", "NXProject.Community.runtimeconfig.json");
    }

    private static void ValidateSetupZip()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "setup", "NXProject-Setup.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "setup-zip-required-files.json");
        ValidateZipAgainstManifest(zipPath, manifestPath);
        ValidateSelfContainedNotSingleFile(zipPath, "NXProject-Setup.exe", "NXProject-Setup.dll", "NXProject-Setup.runtimeconfig.json");
    }

    /// <summary>Confirma que o publish foi self-contained (runtimeconfig.json com
    /// "includedFrameworks", nao "frameworks") e nao foi PublishSingleFile (o .dll
    /// companheiro do .exe existe como entrada separada no zip, nao embutido).</summary>
    private static void ValidateSelfContainedNotSingleFile(string zipPath, string exeName, string dllName, string runtimeConfigName)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var zipLabel = Path.GetFileName(zipPath);

        var dllEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, dllName, StringComparison.OrdinalIgnoreCase));
        if (dllEntry is null)
            throw new InvalidOperationException(
                $"'{zipLabel}': '{dllName}' nao encontrado como arquivo separado — parece que o publish usou PublishSingleFile.");

        var configEntry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, runtimeConfigName, StringComparison.OrdinalIgnoreCase));
        if (configEntry is null)
            throw new InvalidOperationException($"'{zipLabel}': '{runtimeConfigName}' nao encontrado no zip.");

        using var stream = configEntry.Open();
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement.GetProperty("runtimeOptions");

        if (root.TryGetProperty("frameworks", out _) && !root.TryGetProperty("includedFrameworks", out _))
            throw new InvalidOperationException(
                $"'{zipLabel}': '{runtimeConfigName}' usa 'frameworks' (framework-dependent) em vez de 'includedFrameworks' (self-contained) — {exeName} exige .NET pre-instalado no sistema.");

        if (!root.TryGetProperty("includedFrameworks", out _))
            throw new InvalidOperationException(
                $"'{zipLabel}': '{runtimeConfigName}' nao contem 'includedFrameworks' — publish nao parece self-contained.");
    }

    private static void ValidateZipAgainstManifest(string zipPath, string manifestPath)
    {
        if (!File.Exists(zipPath))
            throw new InvalidOperationException($"Zip nao encontrado: {zipPath}");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Manifest nao encontrado: {manifestPath}");

        var requiredNames = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException($"Manifest vazio ou invalido: {manifestPath}");

        using var zip = ZipFile.OpenRead(zipPath);
        var entryNames = zip.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = requiredNames.Where(n => !entryNames.Contains(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"'{Path.GetFileName(zipPath)}' esta incompleto. Arquivos faltando: {string.Join(", ", missing)}");
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

    private static void RebuildKeepsCompletedTaskFinish()
    {
        var originalFinish = new DateTime(2026, 7, 7);
        var project = new Project { Name = "Teste", StartDate = new DateTime(2026, 7, 6) };
        var task = new ProjectTask
        {
            Id = 1,
            Name = "Atividade sincronizada",
            TfsType = "Story",
            Start = new DateTime(2026, 7, 6),
            Finish = originalFinish,
            EstimatedHours = 320,
            PercentComplete = 100
        };
        project.Tasks.Add(task);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();

        AssertEqual(originalFinish, task.Finish, "Rebuild apos sync/import nao deve empurrar tarefa 100% por HH/alocacao.");
    }

    private static void ScheduleNoDevOpsZeroDurationCreatesMilestone()
    {
        var start = new DateTime(2026, 7, 6);
        var task = new ProjectTask
        {
            Name = "Marco local",
            TfsType = "NODEVOPS",
            Start = start,
            Finish = ProjectCalendarService.AddWorkingHours(start, 8),
            EstimatedHours = 8,
            OriginalEstimatedHours = 8
        };
        var vm = new TaskViewModel(task);

        vm.DurationText = "0";

        AssertEqual(0, vm.DurationHours, "No DevOps com duracao zero deve ficar com 0h.");
        AssertEqual(start, task.Finish, "Marco deve terminar na mesma data de inicio.");
        if (!task.IsMilestone)
            throw new InvalidOperationException("No DevOps com duracao zero deve ser marcado como milestone.");
    }

    private static void ScheduleDevOpsZeroDurationIsIgnored()
    {
        var start = new DateTime(2026, 7, 6);
        var finish = ProjectCalendarService.AddWorkingHours(start, 8);
        var task = new ProjectTask
        {
            Name = "Story DevOps",
            TfsType = "Story",
            Start = start,
            Finish = finish,
            EstimatedHours = 8,
            OriginalEstimatedHours = 8
        };
        var vm = new TaskViewModel(task);

        vm.DurationText = "0";

        AssertEqual(8, vm.DurationHours, "Atividade DevOps nao deve aceitar duracao zero.");
        AssertEqual(finish, task.Finish, "Atividade DevOps deve manter o fim original.");
        if (task.IsMilestone)
            throw new InvalidOperationException("Atividade DevOps nao deve virar milestone local com duracao zero.");
    }

    private static void ScheduleDevOpsMilestoneAcceptsOnlyZeroDuration()
    {
        var start = new DateTime(2026, 7, 6);
        var task = new ProjectTask
        {
            Name = "Marco DevOps",
            TfsType = "Marco-Devops",
            Start = start,
            Finish = ProjectCalendarService.AddWorkingHours(start, 8),
            EstimatedHours = 8,
            OriginalEstimatedHours = 8
        };
        var vm = new TaskViewModel(task);

        vm.DurationText = "0";

        AssertEqual(0, vm.DurationHours, "Marco-Devops deve aceitar duracao zero.");
        AssertEqual(start, task.Finish, "Marco-Devops deve terminar na mesma data de inicio.");
        if (!task.IsMilestone)
            throw new InvalidOperationException("Marco-Devops com duracao zero deve ser marcado como milestone.");

        vm.DurationText = "8";

        AssertEqual(0, vm.DurationHours, "Marco-Devops nao deve aceitar duracao positiva.");
        AssertEqual(start, task.Finish, "Marco-Devops deve continuar como marco apos tentativa de duracao positiva.");
    }

    private static void NoDevOpsNegativeTfsIdDisplaysAsInternal()
    {
        var task = new ProjectTask
        {
            Id = 42,
            TfsId = -1,
            TfsType = "NoDevops",
            Name = "Atividade interna"
        };
        var vm = new TaskViewModel(task);

        if (task.HasTfsLink)
            throw new InvalidOperationException("TfsId negativo nao deve ser tratado como vinculo DevOps.");
        if (vm.HasDevOpsLink)
            throw new InvalidOperationException("ViewModel nao deve indicar vinculo DevOps para TfsId negativo.");
        if (vm.DisplayId != "42:I")
            throw new InvalidOperationException($"ID negativo NoDevOps deve aparecer como 42:I. Atual: {vm.DisplayId}.");
    }

    private static void PendingDevOpsCreateDisplaysAsInternal()
    {
        var task = new ProjectTask
        {
            Id = 43,
            TfsId = null,
            TfsType = "Story",
            IsPendingTfsCreate = true,
            Name = "Atividade pendente DevOps"
        };
        var vm = new TaskViewModel(task);

        if (task.HasTfsLink)
            throw new InvalidOperationException("Atividade pendente sem TfsId positivo nao deve ser tratada como vinculo DevOps.");
        if (vm.DisplayId != "43:I")
            throw new InvalidOperationException($"Atividade DevOps pendente deve continuar como 43:I ate sincronizar. Atual: {vm.DisplayId}.");

        task.TfsId = 1234;
        task.IsPendingTfsCreate = false;
        vm.TfsId = 1234;

        if (vm.DisplayId != "1234:T")
            throw new InvalidOperationException($"Apos receber TfsId positivo, deve aparecer como 1234:T. Atual: {vm.DisplayId}.");
    }

    private static void DevOpsPredecessorAcceptsInternalDevOpsOnly()
    {
        var internalDevOps = new ProjectTask { Id = 11, TfsType = "Story", Name = "Interna DevOps" };
        var internalNoDevOps = new ProjectTask { Id = 12, TfsId = -1, TfsType = "NoDevops", Name = "Interna local" };
        var target = new ProjectTask { Id = 13, TfsId = 1300, TfsType = "Story", Name = "Story destino" };

        var internalDevOpsVm = new TaskViewModel(internalDevOps);
        var internalNoDevOpsVm = new TaskViewModel(internalNoDevOps);
        var targetVm = new TaskViewModel(target);
        ConfigurePredecessorLookups(targetVm, internalDevOpsVm, internalNoDevOpsVm, targetVm);

        targetVm.PredecessorsText = "I:11,I:12";

        AssertEqual(1, target.PredecessorIds.Count, "Story DevOps deve aceitar apenas predecessor interno que tambem seja DevOps.");
        AssertEqual(11, target.PredecessorIds[0], "Story DevOps deve manter o predecessor I:11.");

        targetVm.PredecessorsText = "12";
        AssertEqual(0, target.PredecessorIds.Count, "Numero interno puro de NoDevOps tambem deve ser bloqueado para Story DevOps.");
    }

    private static void NoDevOpsPredecessorAcceptsAnyInternalType()
    {
        var internalDevOps = new ProjectTask { Id = 21, TfsType = "Story", Name = "Interna DevOps" };
        var internalNoDevOps = new ProjectTask { Id = 22, TfsId = -1, TfsType = "No DevOps", Name = "Interna local" };
        var target = new ProjectTask { Id = 23, TfsId = -2, TfsType = "NoDevops", Name = "Marco local" };

        var internalDevOpsVm = new TaskViewModel(internalDevOps);
        var internalNoDevOpsVm = new TaskViewModel(internalNoDevOps);
        var targetVm = new TaskViewModel(target);
        ConfigurePredecessorLookups(targetVm, internalDevOpsVm, internalNoDevOpsVm, targetVm);

        targetVm.PredecessorsText = "I:21,I:22";

        AssertEqual(2, target.PredecessorIds.Count, "NoDevOps deve aceitar predecessor interno de qualquer tipo.");
        AssertEqual(21, target.PredecessorIds[0], "NoDevOps deve aceitar predecessor interno DevOps.");
        AssertEqual(22, target.PredecessorIds[1], "NoDevOps deve aceitar predecessor interno NoDevOps.");
    }

    private static void AddMilestoneCreatesDevOpsSiblingForDevOpsSelection()
    {
        var project = new Project { Name = "Teste", StartDate = new DateTime(2026, 7, 6) };
        var story = new ProjectTask
        {
            Id = 1,
            Name = "Story",
            TfsType = "Story",
            TfsId = 1001,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 8)
        };
        project.Tasks.Add(story);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        vm.SelectedTask = vm.FlatTasks.First(t => t.Id == story.Id);

        vm.AddMilestone(asChild: false);

        AssertEqual(2, project.Tasks.Count, "Botao de marco deve criar irmao abaixo da selecao.");
        var marco = project.Tasks[1];
        if (marco.TfsType != "Marco-Devops")
            throw new InvalidOperationException($"Selecao DevOps deve criar Marco-Devops. Atual: {marco.TfsType}.");
        AssertEqual(0, marco.EstimatedHours ?? -1, "Marco deve nascer com duracao zero.");
        AssertEqual(story.Id, marco.PredecessorIds.Single(), "Marco irmao deve usar atividade selecionada como predecessora.");
        if (!marco.IsMilestone || !marco.IsPendingTfsCreate || marco.TfsId != 0)
            throw new InvalidOperationException("Marco-Devops irmao deve nascer como milestone pendente de criacao no DevOps.");
    }

    private static void AddMilestoneCreatesDevOpsChildWithCtrl()
    {
        var project = new Project { Name = "Teste", StartDate = new DateTime(2026, 7, 6) };
        var feature = new ProjectTask
        {
            Id = 10,
            Name = "Feature",
            TfsType = "Feature",
            TfsId = 1010,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 10),
            IsSummary = true
        };
        var story = new ProjectTask
        {
            Id = 11,
            Name = "Story filha",
            TfsType = "Story",
            TfsId = 1011,
            Parent = feature,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 8)
        };
        feature.Children.Add(story);
        project.Tasks.Add(feature);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        vm.SelectedTask = vm.FlatTasks.First(t => t.Id == feature.Id);

        vm.AddMilestone(asChild: true);

        AssertEqual(2, feature.Children.Count, "Ctrl+botao marco deve criar filho abaixo dos filhos existentes.");
        var marco = feature.Children[1];
        if (marco.TfsType != "Marco-Devops")
            throw new InvalidOperationException($"Filho de selecao DevOps deve ser Marco-Devops. Atual: {marco.TfsType}.");
        AssertEqual(feature.Id, marco.Parent?.Id ?? -1, "Marco filho deve manter o pai selecionado.");
        AssertEqual(1, marco.Level, "Marco filho deve nascer no nivel abaixo do pai.");
        AssertEqual(story.Id, marco.PredecessorIds.Single(), "Marco filho deve usar irmao anterior como predecessora.");
    }

    private static void AddMilestoneDoesNotCreateChildUnderMilestone()
    {
        var project = new Project { Name = "Teste", StartDate = new DateTime(2026, 7, 6) };
        var marco = new ProjectTask
        {
            Id = 20,
            Name = "Marco existente",
            TfsType = "Marco-Devops",
            TfsId = 1020,
            IsMilestone = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 6)
        };
        project.Tasks.Add(marco);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        vm.SelectedTask = vm.FlatTasks.First(t => t.Id == marco.Id);

        vm.AddMilestone(asChild: true);

        AssertEqual(0, marco.Children.Count, "Ctrl+botao marco nao deve criar filho dentro de outro marco.");
        AssertEqual(1, project.Tasks.Count, "Bloqueio de marco filho nao deve criar atividade em outro nivel.");
        if (!vm.StatusMessage.Contains("Marco nao pode ter outro marco como filho", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Status deve explicar bloqueio de marco filho. Atual: {vm.StatusMessage}");
    }

    private static void DevOpsMilestoneCreateOpsAddsMarcoProjectTag()
    {
        var task = new ProjectTask
        {
            Id = 31,
            Name = "Marco de aceite",
            TfsType = "Marco-Devops",
            Tags = "Planejamento",
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 6),
            IsMilestone = true,
            EstimatedHours = 0
        };

        var ops = TfsImportService.BuildCreateOpsForTests(
            task,
            parentId: 1000,
            tasksById: new Dictionary<int, ProjectTask> { [task.Id] = task },
            syncPredecessorLinks: false);

        var text = string.Join("\n", ops.Select(o => o.ToString()));
        if (!text.Contains("System.Tags") || !text.Contains("MARCO-PROJECT"))
            throw new InvalidOperationException("Marco-Devops deve criar Task no DevOps com tag MARCO-PROJECT.");
    }

    private static void TfsSyncFinishUsesInclusiveDate()
    {
        var task = new ProjectTask
        {
            Name = "Atividade concluida",
            Start = new DateTime(2026, 7, 3),
            Finish = new DateTime(2026, 7, 4),
            PercentComplete = 100,
            TfsType = "Story"
        };

        var tfsFinish = TfsImportService.GetTfsFinishDateForTests(task);

        if (!tfsFinish.HasValue)
            throw new InvalidOperationException("Sync TFS deve calcular uma data fim para atividade com Finish valido.");
        AssertEqual(new DateTime(2026, 7, 3), tfsFinish.Value, "Sync TFS deve enviar a data fim inclusiva, nao o limite exclusivo interno.");
    }

    private static void DevOpsMilestoneUsesPreviousSiblingAsImplicitPredecessor()
    {
        var parent = new ProjectTask { Id = 40, TfsId = 4000, TfsType = "Feature", Name = "Feature" };
        var previous = new ProjectTask { Id = 41, TfsId = 4100, TfsType = "Story", Name = "Story anterior", Parent = parent };
        var marco = new ProjectTask
        {
            Id = 42,
            TfsType = "Marco-Devops",
            Name = "Marco",
            Parent = parent,
            IsMilestone = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 6)
        };
        parent.Children.Add(previous);
        parent.Children.Add(marco);

        var ops = TfsImportService.BuildCreateOpsForTests(
            marco,
            parentId: parent.TfsId!.Value,
            tasksById: new Dictionary<int, ProjectTask>
            {
                [parent.Id] = parent,
                [previous.Id] = previous,
                [marco.Id] = marco
            });
        var json = JsonSerializer.Serialize(ops);

        AssertEqual(1, CountOccurrences(json, "System.LinkTypes.Dependency-Reverse"), "Marco-Devops deve criar uma predecessora implicita.");
        if (!json.Contains("/workItems/4100"))
            throw new InvalidOperationException("Marco-Devops deve usar o irmao anterior como predecessora implicita.");
    }

    private static void DevOpsMilestoneUsesParentAsImplicitPredecessor()
    {
        var parent = new ProjectTask { Id = 50, TfsId = 5000, TfsType = "Feature", Name = "Feature" };
        var marco = new ProjectTask
        {
            Id = 51,
            TfsType = "Marco-Devops",
            Name = "Marco inicial",
            Parent = parent,
            IsMilestone = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 6)
        };
        parent.Children.Add(marco);

        var ops = TfsImportService.BuildCreateOpsForTests(
            marco,
            parentId: parent.TfsId!.Value,
            tasksById: new Dictionary<int, ProjectTask>
            {
                [parent.Id] = parent,
                [marco.Id] = marco
            });
        var json = JsonSerializer.Serialize(ops);

        AssertEqual(1, CountOccurrences(json, "System.LinkTypes.Dependency-Reverse"), "Marco-Devops sem irmao anterior deve criar predecessora implicita no pai.");
        if (!json.Contains("/workItems/5000"))
            throw new InvalidOperationException("Marco-Devops sem irmao anterior deve usar o pai como predecessora implicita.");
    }

    private static void DevOpsMilestoneResolvesExplicitPredecessorWithChildren()
    {
        var parent = new ProjectTask { Id = 100, TfsId = 1000, TfsType = "Feature", Name = "Feature" };
        var predecessor = new ProjectTask
        {
            Id = 101,
            TfsId = 1076961,
            TfsType = "Story",
            Name = "Power BI / Dashboard",
            Parent = parent
        };
        predecessor.Children.Add(new ProjectTask
        {
            Id = 102,
            TfsId = 1077000,
            TfsType = "Task",
            Name = "Atividade filha",
            Parent = predecessor
        });
        var marco = new ProjectTask
        {
            Id = 103,
            TfsType = "Marco-Devops",
            Name = "Condicao de Infraestrutura",
            Parent = parent,
            IsMilestone = true,
            Start = new DateTime(2026, 11, 11),
            Finish = new DateTime(2026, 11, 11)
        };
        marco.PredecessorIds.Add(101);
        parent.Children.Add(predecessor);
        parent.Children.Add(marco);

        var ops = TfsImportService.BuildCreateOpsForTests(
            marco,
            parentId: parent.TfsId!.Value,
            tasksById: new Dictionary<int, ProjectTask>
            {
                [parent.Id] = parent,
                [predecessor.Id] = predecessor,
                [marco.Id] = marco
            });
        var json = JsonSerializer.Serialize(ops);

        AssertEqual(1, CountOccurrences(json, "System.LinkTypes.Dependency-Reverse"), "Marco-Devops deve criar uma predecessora explicita.");
        if (!json.Contains("/workItems/1076961"))
            throw new InvalidOperationException("Marco-Devops deve resolver predecessor interno 101 para o TfsId 1076961.");
    }

    private static void DevOpsMilestonePositionIgnoresExternalHierarchyPredecessor()
    {
        var parent = new ProjectTask { Id = 110, TfsId = 1100, TfsType = "Feature", Name = "Feature" };
        var previousEarly = new ProjectTask
        {
            Id = 111,
            TfsId = 1110,
            TfsType = "Story",
            Name = "Anterior menor",
            Parent = parent,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7)
        };
        var previousLate = new ProjectTask
        {
            Id = 112,
            TfsId = 1120,
            TfsType = "Story",
            Name = "Anterior maior",
            Parent = parent,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 10)
        };
        var marco = new ProjectTask
        {
            Id = 113,
            TfsId = 1130,
            TfsType = "Marco-Devops",
            Name = "Marco",
            Parent = parent,
            IsMilestone = true,
            Start = new DateTime(2026, 7, 11),
            Finish = new DateTime(2026, 7, 11)
        };

        var otherParent = new ProjectTask { Id = 120, TfsId = 1200, TfsType = "Feature", Name = "Outra Feature" };
        var outside = new ProjectTask
        {
            Id = 121,
            TfsId = 1210,
            TfsType = "Story",
            Name = "Fora da hierarquia",
            Parent = otherParent,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 20)
        };

        marco.PredecessorIds.Add(outside.Id);
        marco.PredecessorIds.Add(previousEarly.Id);
        marco.PredecessorIds.Add(previousLate.Id);
        parent.Children.Add(previousEarly);
        parent.Children.Add(marco);
        parent.Children.Add(previousLate);
        otherParent.Children.Add(outside);

        var roots = new System.Collections.ObjectModel.ObservableCollection<ProjectTask> { parent, otherParent };
        TfsImportService.RepositionMarcosAfterPredecessorsForTests(roots);

        AssertEqual(2, parent.Children.IndexOf(marco), "Marco-Devops deve ser posicionado apos a irma predecessora que termina mais tarde.");
        AssertEqual(previousLate.Id, parent.Children[1].Id, "Predecessora fora da hierarquia nao deve influenciar a posicao do marco.");
    }

    private static void DevOpsMilestonePositionUsesParentAnchor()
    {
        var parent = new ProjectTask { Id = 130, TfsId = 1300, TfsType = "Feature", Name = "Feature" };
        var first = new ProjectTask { Id = 131, TfsId = 1310, TfsType = "Story", Name = "Story", Parent = parent };
        var marco = new ProjectTask
        {
            Id = 132,
            TfsId = 1320,
            TfsType = "Marco-Devops",
            Name = "Marco inicial",
            Parent = parent,
            IsMilestone = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 6)
        };

        marco.PredecessorIds.Add(parent.Id);
        parent.Children.Add(first);
        parent.Children.Add(marco);

        var roots = new System.Collections.ObjectModel.ObservableCollection<ProjectTask> { parent };
        TfsImportService.RepositionMarcosAfterPredecessorsForTests(roots);

        AssertEqual(0, parent.Children.IndexOf(marco), "Marco-Devops com predecessora no pai deve ficar no inicio dos filhos.");
    }

    private static void ImportPreservesNoDevOpsSiblingPosition()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var currentParent = new ProjectTask
        {
            Id = 100,
            TfsId = 1000,
            TfsType = "Feature",
            Name = "Feature",
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 9)
        };
        var currentA = CreateAssignedTask(101, "Story A", resource, new DateTime(2026, 7, 6), currentParent);
        currentA.TfsId = 1001;
        currentA.TfsType = "Story";
        var localMarker = CreateAssignedTask(102, "Marco local", resource, new DateTime(2026, 7, 7), currentParent);
        localMarker.TfsId = -1;
        localMarker.TfsType = "NoDevops";
        var currentB = CreateAssignedTask(103, "Story B", resource, new DateTime(2026, 7, 8), currentParent);
        currentB.TfsId = 1002;
        currentB.TfsType = "Story";
        currentParent.Children.Add(currentA);
        currentParent.Children.Add(localMarker);
        currentParent.Children.Add(currentB);

        var currentProject = new Project
        {
            Name = "Atual",
            StartDate = new DateTime(2026, 7, 6)
        };
        currentProject.Resources.Add(resource);
        currentProject.Tasks.Add(currentParent);

        var vm = new MainViewModel("NXTestUnit") { Project = currentProject };
        vm.RebuildFlatTasks();

        var importedParent = new ProjectTask
        {
            Id = 200,
            TfsId = 1000,
            TfsType = "Feature",
            Name = "Feature",
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 8)
        };
        var importedA = CreateAssignedTask(201, "Story A", resource, new DateTime(2026, 7, 6), importedParent);
        importedA.TfsId = 1001;
        importedA.TfsType = "Story";
        var importedB = CreateAssignedTask(202, "Story B", resource, new DateTime(2026, 7, 6), importedParent);
        importedB.TfsId = 1002;
        importedB.TfsType = "Story";
        importedParent.Children.Add(importedA);
        importedParent.Children.Add(importedB);

        var importedProject = new Project
        {
            Name = "Importado",
            StartDate = new DateTime(2026, 7, 6)
        };
        importedProject.Resources.Add(resource);
        importedProject.Tasks.Add(importedParent);

        vm.ApplyImportedProject(importedProject);

        var restoredParent = vm.Project.Tasks.Single(t => t.TfsId == 1000);
        var names = string.Join("|", restoredParent.Children.Select(t => t.Name));
        if (names != "Story A|Marco local|Story B")
            throw new InvalidOperationException($"NoDevOps deveria voltar na mesma posicao relativa. Ordem atual: {names}.");

        AssertEqual(new DateTime(2026, 7, 7), localMarker.Start, "Marco local deve ficar depois da Story A pela predecessora virtual.");
        AssertEqual(new DateTime(2026, 7, 8), importedB.Start, "Story B deve ficar depois do NoDevOps restaurado pela predecessora virtual.");
    }

    private static void ImportMatchesOrPreservesInternalDevOpsActivities()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var currentParent = new ProjectTask
        {
            Id = 300,
            TfsId = 3000,
            TfsType = "Feature",
            Name = "Feature",
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 9)
        };
        var currentA = CreateAssignedTask(301, "Story A", resource, new DateTime(2026, 7, 6), currentParent);
        currentA.TfsId = 3001;
        currentA.TfsType = "Story";
        var internalSameName = CreateAssignedTask(302, "Story B", resource, new DateTime(2026, 7, 7), currentParent);
        internalSameName.TfsId = 0;
        internalSameName.TfsType = "Story";
        internalSameName.IsPendingTfsCreate = true;
        var internalNew = CreateAssignedTask(303, "Story C nova", resource, new DateTime(2026, 7, 8), currentParent);
        internalNew.TfsId = 0;
        internalNew.TfsType = "Story";
        internalNew.IsPendingTfsCreate = true;
        currentParent.Children.Add(currentA);
        currentParent.Children.Add(internalNew);
        currentParent.Children.Add(internalSameName);

        var currentProject = new Project { Name = "Atual", StartDate = new DateTime(2026, 7, 6) };
        currentProject.Resources.Add(resource);
        currentProject.Tasks.Add(currentParent);

        var vm = new MainViewModel("NXTestUnit") { Project = currentProject };
        vm.RebuildFlatTasks();

        var importedParent = new ProjectTask
        {
            Id = 400,
            TfsId = 3000,
            TfsType = "Feature",
            Name = "Feature",
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 8)
        };
        var importedA = CreateAssignedTask(401, "Story A", resource, new DateTime(2026, 7, 6), importedParent);
        importedA.TfsId = 3001;
        importedA.TfsType = "Story";
        var importedB = CreateAssignedTask(402, "Story B", resource, new DateTime(2026, 7, 7), importedParent);
        importedB.TfsId = 3002;
        importedB.TfsType = "Story";
        importedParent.Children.Add(importedA);
        importedParent.Children.Add(importedB);

        var importedProject = new Project { Name = "Importado", StartDate = new DateTime(2026, 7, 6) };
        importedProject.Resources.Add(resource);
        importedProject.Tasks.Add(importedParent);

        vm.ApplyImportedProject(importedProject);

        var restoredParent = vm.Project.Tasks.Single(t => t.TfsId == 3000);
        var names = string.Join("|", restoredParent.Children.Select(t => t.Name));
        if (names != "Story A|Story C nova|Story B")
            throw new InvalidOperationException($"Import deve preservar Story C nova e vincular Story B por nome. Ordem atual: {names}.");

        AssertEqual(1, restoredParent.Children.Count(t => t.Name == "Story B"), "Atividade I com mesmo nome da importada nao deve duplicar.");
        var restoredB = restoredParent.Children.Single(t => t.Name == "Story B");
        AssertEqual(3002, restoredB.TfsId ?? 0, "Atividade I com mesmo nome deve virar a atividade T importada.");
        var restoredNew = restoredParent.Children.Single(t => t.Name == "Story C nova");
        if (restoredNew.HasTfsLink)
            throw new InvalidOperationException("Atividade DevOps nova sem match no TFS deve continuar interna.");
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

    private static int CountOccurrences(string value, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static void ConfigurePredecessorLookups(TaskViewModel target, params TaskViewModel[] tasks)
    {
        var byInternalId = tasks.ToDictionary(t => t.Model.Id);
        target.FindByInternalId = id => byInternalId.TryGetValue(id, out var vm) ? vm : null;
        target.FindByDisplayId = displayId =>
        {
            if (displayId.StartsWith("I:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(displayId[2..], out var internalId) &&
                byInternalId.ContainsKey(internalId))
                return internalId;

            if (displayId.StartsWith("T:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(displayId[2..], out var tfsId))
            {
                var match = tasks.FirstOrDefault(t => t.Model.TfsId == tfsId);
                return match?.Model.Id;
            }

            if (displayId.EndsWith(":I", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(displayId[..^2], out var gridInternalId) &&
                byInternalId.ContainsKey(gridInternalId))
                return gridInternalId;

            if (displayId.EndsWith(":T", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(displayId[..^2], out var gridTfsId))
            {
                var match = tasks.FirstOrDefault(t => t.Model.TfsId == gridTfsId);
                return match?.Model.Id;
            }

            if (int.TryParse(displayId, out var rawId) && byInternalId.ContainsKey(rawId))
                return rawId;

            return null;
        };
    }

    private static void SetupUpdateNoBaselineDoesNotTrigger()
    {
        var remote = DateTimeOffset.UtcNow;
        var result = UpdateService.ShouldTriggerSetupUpdate(null, remote);
        if (result)
            throw new InvalidOperationException("Sem baseline conhecida, nao deveria disparar reinstalacao (evita falso positivo).");
    }

    private static void SetupUpdateSameTimestampDoesNotTrigger()
    {
        var t = DateTimeOffset.UtcNow;
        var result = UpdateService.ShouldTriggerSetupUpdate(t, t);
        if (result)
            throw new InvalidOperationException("Asset com o mesmo timestamp da baseline nao deveria disparar reinstalacao.");
    }

    private static void SetupUpdateOlderAssetDoesNotTrigger()
    {
        var known = DateTimeOffset.UtcNow;
        var remote = known.AddHours(-1);
        var result = UpdateService.ShouldTriggerSetupUpdate(known, remote);
        if (result)
            throw new InvalidOperationException("Asset mais antigo que a baseline nao deveria disparar reinstalacao.");
    }

    private static void SetupUpdateNewerAssetTriggers()
    {
        var known = DateTimeOffset.UtcNow;
        var remote = known.AddHours(1);
        var result = UpdateService.ShouldTriggerSetupUpdate(known, remote);
        if (!result)
            throw new InvalidOperationException("Asset mais novo que a baseline deveria disparar reinstalacao.");
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
