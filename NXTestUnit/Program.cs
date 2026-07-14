using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using NXProject.Community.Services;
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
        ("Predecessor virtual: recalculo geral reposiciona tarefa com andamento", VirtualPredecessorRecalcMovesStartedSibling),
        ("Predecessor virtual: digitacao de HH usa anterior com predecessora explicita", DurationEditUsesPreviousSiblingEvenWhenPreviousHasExplicitPredecessor),
        ("Predecessora explicita: recalculo geral reposiciona tarefa com andamento", ExplicitPredecessorRecalcMovesStartedTask),
        ("Setup update: sem baseline conhecida nao dispara reinstalacao", SetupUpdateNoBaselineDoesNotTrigger),
        ("Setup update: asset igual a baseline nao dispara reinstalacao", SetupUpdateSameTimestampDoesNotTrigger),
        ("Setup update: asset mais antigo que a baseline nao dispara reinstalacao", SetupUpdateOlderAssetDoesNotTrigger),
        ("Setup update: asset mais novo que a baseline dispara reinstalacao", SetupUpdateNewerAssetTriggers),
        ("Import TFS: AvailabilityPercent existente e preservado sobre o valor importado", ImportPreservesExistingResourceAvailabilityPercent),
        ("Arquivo: AvailabilityPercent do recurso sobrevive a salvar/abrir", XmlRoundTripPreservesResourceAvailabilityPercent),
        ("Alocacao: recalcular repetidamente NAO infla o fim (sem compounding)", ResourceAllocationRecalcDoesNotCompoundFinish),
        ("Alocacao: cronograma, import TFS e abertura produzem o MESMO fim", CentralizedFinishCalcIsIdenticalAcrossPaths),
        ("Duracao: tarefa so com HH Atual NAO conta as horas em dobro", EffectiveDurationDoesNotDoubleCountCurrentOnlyHours),
        ("Sync conflito: gravacao do usuario atual libera se TFS aberto", SyncConflictCurrentUserOpenStateReleases),
        ("Sync conflito: NXProject 100% e outro usuario NAO libera no Sync geral", SyncConflictLocal100OtherUserBlocks),
        ("Sync conflito: NXProject 100% e TFS ja Closed NAO libera automaticamente", SyncConflictClosedOtherUserBlocks),
        ("Sync conflito: mesmo usuario e TFS Closed NAO libera automaticamente", SyncConflictCurrentUserClosedStateBlocks),
        ("Sync conflito: NXProject abaixo de 100% e outro usuario NAO libera", SyncConflictBelow100OtherUserBlocks),
        ("Sync conflito: versao a frente sem alteracao nao registra conflito", SyncConflictVersionAheadWithoutPendingWritesDoesNotBlock),
        ("Sync conflito: versao a frente em Feature/Epic nao bloqueia rollup", SyncConflictVersionAheadFeatureEpicDoesNotBlockRollup),
        ("Sync conflito: resolucao manual permite sobrescrever item iniciado", SyncConflictManualOverwriteAllowsStartedItem),
        ("Cronograma: digitacao 100% em Story exige TKs maior que zero", ManualStoryCompletionRequiresDevOpsTasks),
        ("Task Plan: criar e reabrir xlsx preserva colunas e linhas", TaskPlanCreateAndLoadRoundTrip),
        ("Task Plan: cabecalho detectado abaixo do bloco de resumo", TaskPlanDetectsHeaderBelowSummary),
        ("Task Plan: salvar preserva valores e cor de fundo", TaskPlanSavePreservesValuesAndColors),
        ("Task Plan: coluna nova grava no fim com prefixo e volta na posicao", TaskPlanNewColumnKeepsViewPosition),
        ("Task Plan: coluna excluida some da planilha ao salvar", TaskPlanDeletedColumnClearedOnSave),
        ("Task Plan: aplicar cria task interna no padrao do cronograma", TaskPlanApplyCreatesInternalTaskLikeSchedule),
        ("Task Plan: story iniciada NAO tem a duracao ajustada", TaskPlanStartedStoryKeepsDuration)
    ];

    private static int Main(string[] args)
    {
        var category = args.Length > 0 ? args[0] : "schedule";
        if (string.Equals(category, "simulate-openai", StringComparison.OrdinalIgnoreCase))
        {
            SimulateOpenAi();
            return 0;
        }
        _solutionRoot = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

        List<(string Name, Action Test)> tests = category.ToLowerInvariant() switch
        {
            "packaging-community" => [PackagingTests[0]],
            "packaging-setup" => [PackagingTests[1], PackagingTests[2]],
            "packaging" => PackagingTests,
            _ => ScheduleTests
        };

        // Qualquer categoria desconhecida cai nos testes de cronograma (default do switch),
        // entao "isSchedule" deve seguir a MESMA regra — senao os testes de cronograma
        // rodam sem ResetCalendar() e usam um calendario default (datas +1 dia).
        var isSchedule = ReferenceEquals(tests, ScheduleTests);

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
        ("Empacotamento: NXProject-Setup.zip contem runtime e libs essenciais", ValidateSetupZip),
        ("Setup: timestamp e intrinseco ao zip e igual ao embutido no build", ValidateSetupTimestampIntrinsic)
    ];

    /// <summary>Garante que a identidade do Setup (timestamp) FICA NO proprio Setup e que a
    /// release so cria a tag, sem recarimbar. O valor gravado dentro do NXProject-Setup.zip
    /// (setup-build-timestamp.txt) deve ser identico ao embutido no build do Community
    /// (known-setup-timestamp.txt). Se um dia a release voltar a gerar o timestamp por
    /// conta propria (ex.: mtime do arquivo ou UpdatedAt do GitHub), os dois divergem e
    /// este teste falha — evitando reinstalacoes falsas do Setup a cada release.</summary>
    private static void ValidateSetupTimestampIntrinsic()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "setup", "NXProject-Setup.zip");
        var embeddedPath = Path.Combine(_solutionRoot, "NXProject.Community", "Assets", "known-setup-timestamp.txt");

        if (!File.Exists(embeddedPath))
            throw new InvalidOperationException(
                "known-setup-timestamp.txt ausente — o release-nxproject-setup.ps1 deve grava-lo (identidade intrinseca do Setup).");

        var embedded = File.ReadAllText(embeddedPath).Trim();

        using var zip = ZipFile.OpenRead(zipPath);
        var stampEntry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "setup-build-timestamp.txt", StringComparison.OrdinalIgnoreCase));
        if (stampEntry is null)
            throw new InvalidOperationException(
                "'NXProject-Setup.zip' nao contem setup-build-timestamp.txt — o timestamp deve ficar DENTRO do proprio Setup.");

        using var reader = new StreamReader(stampEntry.Open());
        var inZip = reader.ReadToEnd().Trim();

        if (!string.Equals(embedded, inZip, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Timestamp do Setup divergente: embutido no build = '{embedded}', dentro do zip = '{inZip}'. " +
                "A release nao pode recarimbar o timestamp; ele deve ficar no Setup e so a tag muda.");
    }

    private static void ValidateCommunityReleaseZip()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "community", "NXProject.Community-Release.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "release-zip-required-files.json");
        ValidateZipAgainstManifest(zipPath, manifestPath);
        ValidateSelfContainedNotSingleFile(zipPath, "NXProject.Community.exe", "NXProject.Community.dll", "NXProject.Community.runtimeconfig.json");
    }

    private static void SimulateOpenAi()
    {
        Console.WriteLine("Simulacao: parse do retorno do script JS e fluxo de tratamento.");
        var samples = new Dictionary<string,string?>()
        {
            {"Valid text","{\"text\":\"Ola do assistente\"}"},
            {"Empty text","{\"text\":\"\"}"},
            {"Error http","{\"error\":\"http-403\",\"detail\":\"forbidden\"}"},
            {"Empty object","{}"},
            {"Missing text key","{\"foo\":\"bar\"}"}
        };

        foreach (var kv in samples)
        {
            Console.WriteLine();
            Console.WriteLine($"--- Sample: {kv.Key}");
            var raw = kv.Value;
            Console.WriteLine($"raw: {raw}");
            try
            {
                using var doc = JsonDocument.Parse(raw ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                    var msg = "Falha no envio (" + err.GetString() + "). " + (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail);
                    Console.WriteLine(msg);
                    continue;
                }

                var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("A IA nao retornou conteudo. Tente novamente.");
                    continue;
                }

                Console.WriteLine($"Resposta recebida ({text.Length} caracteres): {text}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao parsear raw: " + ex.Message);
            }
        }
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

    private static void XmlRoundTripPreservesResourceAvailabilityPercent()
    {
        var resource = new Resource { Id = 1, Name = "Dev Meio Periodo", AvailabilityPercent = 62.5 };
        var task = new ProjectTask
        {
            Id = 10,
            Name = "Tarefa",
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        task.Resources.Add(new TaskResource { Resource = resource, ResourceId = resource.Id, AllocationPercent = 100, EstimatedHours = 8 });

        var project = new Project { Name = "RoundTrip", StartDate = new DateTime(2026, 7, 6) };
        project.Resources.Add(resource);
        project.Tasks.Add(task);

        var tempFile = Path.Combine(Path.GetTempPath(), $"nxtest_roundtrip_{Guid.NewGuid():N}.xml");
        try
        {
            XmlProjectService.Save(project, tempFile);
            var loaded = XmlProjectService.Load(tempFile);

            var loadedResource = loaded.Resources.Single(r => r.Id == resource.Id);
            AssertEqual(62.5, loadedResource.AvailabilityPercent,
                "AvailabilityPercent do recurso deve sobreviver ao salvar e reabrir o arquivo (nao voltar para o padrao 100).");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // Regressao do bug "32h viram meses": recalcular a alocacao repetidamente inflava a
    // duracao porque o span de calendario era re-semeado como horas de trabalho e re-dividido
    // pelo fator a cada edicao. Deve ser idempotente.
    private static void ResourceAllocationRecalcDoesNotCompoundFinish()
    {
        // Cenario com HH explicito: recalcular repetidamente deve ser idempotente.
        var r1 = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 50 };
        var withHours = new ProjectTask { Id = 1, Name = "Tarefa 32h", Start = new DateTime(2026, 7, 6), EstimatedHours = 32 };
        withHours.Resources.Add(new TaskResource { Resource = r1, ResourceId = 1, AllocationPercent = 50, EstimatedHours = 32 });
        var p1 = new Project { Name = "P", StartDate = new DateTime(2026, 7, 6) };
        p1.Resources.Add(r1); p1.Tasks.Add(withHours);
        var vm1 = new MainViewModel("NXTestUnit") { Project = p1 };
        vm1.RebuildFlatTasks();
        var tvm1 = vm1.FlatTasks.First(t => t.Id == 1);
        tvm1.RecalcFinishFromPercAloc();
        var finishA = withHours.Finish;
        tvm1.RecalcFinishFromPercAloc();
        tvm1.RecalcFinishFromPercAloc();
        AssertEqual(finishA, withHours.Finish, "Recalcular alocacao varias vezes NAO deve mudar o fim (compounding).");
        if (withHours.Finish > new DateTime(2026, 8, 15))
            throw new InvalidOperationException($"32h a 50%/50% deveria terminar em ~julho/2026, mas terminou em {withHours.Finish:yyyy-MM-dd}.");

        // Cenario que disparava o bug: tarefa SEM HH explicito, so com um span de calendario.
        // O codigo antigo semeava EstimatedHours com o span e re-dividia pelo fator a cada
        // recalculo, inflando sem parar. Deve permanecer estavel.
        var r2 = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 50 };
        var noHours = new ProjectTask
        {
            Id = 1, Name = "Tarefa sem HH",
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 10) // span de ~4 dias uteis, sem EstimatedHours
        };
        noHours.Resources.Add(new TaskResource { Resource = r2, ResourceId = 1, AllocationPercent = 50 });
        var p2 = new Project { Name = "P2", StartDate = new DateTime(2026, 7, 6) };
        p2.Resources.Add(r2); p2.Tasks.Add(noHours);
        var vm2 = new MainViewModel("NXTestUnit") { Project = p2 };
        vm2.RebuildFlatTasks();
        var tvm2 = vm2.FlatTasks.First(t => t.Id == 1);
        tvm2.RecalcFinishFromPercAloc();
        var finishB = noHours.Finish;
        tvm2.RecalcFinishFromPercAloc();
        tvm2.RecalcFinishFromPercAloc();
        tvm2.RecalcFinishFromPercAloc();
        AssertEqual(finishB, noHours.Finish, "Tarefa sem HH: recalcular alocacao varias vezes NAO deve inflar o fim (compounding).");
        if (noHours.Finish > new DateTime(2026, 9, 1))
            throw new InvalidOperationException($"Tarefa sem HH (span ~4 dias) inflou para {noHours.Finish:yyyy-MM-dd} apos varios recalculos.");
    }

    // Regressao direta do double-count: GetEffectiveDurationHours (restante) tinha um
    // fallback que retornava task.DurationHours (= HH Atual + HH Restante). Numa tarefa
    // SO com HH Atual (restante = 0), esse fallback devolvia o HH Atual como se fosse
    // restante, e o mesmo HH Atual era contado de novo em GetEffectiveCurrentDurationHours.
    private static void EffectiveDurationDoesNotDoubleCountCurrentOnlyHours()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var task = new ProjectTask
        {
            Id = 1, Name = "So HH Atual",
            Start = new DateTime(2026, 7, 6),
            EstimatedHours = 0,   // sem HH Restante
            CurrentHours = 24,    // 24h de HH Atual
            PercentComplete = 40
        };
        task.Resources.Add(new TaskResource { Resource = resource, ResourceId = 1, AllocationPercent = 100, EstimatedHours = 0 });

        // Restante = 0 (nao ha HH Restante).
        AssertEqual(0.0, TaskScheduleService.GetEffectiveDurationHours(task),
            "Sem HH Restante, a duracao de trabalho restante deve ser 0 (nao o span nem o HH Atual).");

        // Atual = 24h a 100% de fator = 24h de calendario.
        AssertEqual(24.0, TaskScheduleService.GetEffectiveCurrentDurationHours(task),
            "HH Atual deve ser contado uma unica vez em GetEffectiveCurrentDurationHours.");

        // Total = 24h (nao 48h). Se contasse em dobro, daria 48h.
        AssertEqual(24.0, TaskScheduleService.GetEffectiveTotalDurationHours(task),
            "Tarefa so com HH Atual: total deve ser 24h (contagem unica), nunca 48h (dobro).");
    }

    // O fim calculado deve ser IDENTICO entre o cronograma (edicao), o import TFS e a
    // abertura de arquivo — todos usam TaskScheduleService.CalculateFinishFromAssignments.
    // Cobre os tres cenarios de horas: so HH Restante, so HH Atual, e HH Atual + HH Restante.
    private static void CentralizedFinishCalcIsIdenticalAcrossPaths()
    {
        AssertCentralizedFinishConsistent("so HH Restante",       remaining: 24, current: null);
        AssertCentralizedFinishConsistent("so HH Atual",          remaining: 0,  current: 24);
        AssertCentralizedFinishConsistent("HH Atual + Restante",  remaining: 24, current: 8);
    }

    private static void AssertCentralizedFinishConsistent(string caseName, double remaining, double? current)
    {
        ProjectTask NewTask()
        {
            var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 50 };
            var t = new ProjectTask
            {
                Id = 1, Name = "T",
                Start = new DateTime(2026, 7, 6),
                EstimatedHours = remaining,
                CurrentHours = current,
                PercentComplete = current is > 0 ? 40 : 0
            };
            t.Resources.Add(new TaskResource { Resource = resource, ResourceId = 1, AllocationPercent = 50, EstimatedHours = remaining });
            return t;
        }

        // Caminho 1: cálculo direto (o que o import TFS e a abertura de arquivo usam)
        var t1 = NewTask();
        var finishDirect = TaskScheduleService.CalculateFinishFromAssignments(t1, t1.Start);

        // Caminho 2: edição no cronograma (RecalcFinishFromPercAloc)
        var t2 = NewTask();
        var project = new Project { Name = "P", StartDate = new DateTime(2026, 7, 6) };
        project.Resources.Add(t2.Resources[0].Resource!);
        project.Tasks.Add(t2);
        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        vm.FlatTasks.First(t => t.Id == 1).RecalcFinishFromPercAloc();
        var finishGrid = t2.Finish;

        AssertEqual(finishDirect, finishGrid,
            $"[{caseName}] O fim do cronograma deve ser identico ao do import/abertura (mesma classe central).");

        // Sanidade: o fator de alocacao/disponibilidade tem que estender a duracao.
        // remaining/(0.5x0.5) + current/(0.5x0.5) horas de trabalho -> calendario.
        var expectedCalendarHours = (remaining + (current ?? 0)) / 0.25;
        var expectedFinish = ProjectCalendarService.AddWorkingHours(t1.Start, expectedCalendarHours);
        AssertEqual(expectedFinish, finishDirect,
            $"[{caseName}] HH (Atual+Restante) deve passar pelo fator alocacao x disponibilidade.");
    }

    private static void ImportPreservesExistingResourceAvailabilityPercent()
    {
        // Recurso configurado com 50% de disponibilidade no projeto ATUAL (em memoria).
        var currentResource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 50 };
        var currentTask = CreateAssignedTask(1, "Story A", currentResource, new DateTime(2026, 7, 6), null!);
        currentTask.TfsId = 1001;
        currentTask.TfsType = "Story";

        var currentProject = new Project { Name = "Atual", StartDate = new DateTime(2026, 7, 6) };
        currentProject.Resources.Add(currentResource);
        currentProject.Tasks.Add(currentTask);

        var vm = new MainViewModel("NXTestUnit") { Project = currentProject };
        vm.RebuildFlatTasks();

        // Reimport do TFS traz o MESMO recurso (mesmo nome/chave) com o padrao 100%
        // (como normalmente vem de uma importacao fresca, sem configuracao manual).
        var importedResource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var importedTask = CreateAssignedTask(2, "Story A", importedResource, new DateTime(2026, 7, 6), null!);
        importedTask.TfsId = 1001;
        importedTask.TfsType = "Story";

        var importedProject = new Project { Name = "Importado", StartDate = new DateTime(2026, 7, 6) };
        importedProject.Resources.Add(importedResource);
        importedProject.Tasks.Add(importedTask);

        vm.ApplyImportedProject(importedProject);

        var restoredResource = vm.Project.Resources.Single(r => r.Name == "Dev");
        AssertEqual(50, restoredResource.AvailabilityPercent,
            "Reimportar do TFS nao deve sobrescrever o AvailabilityPercent ja configurado no projeto atual com o padrao 100% do import.");
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

    private static void VirtualPredecessorRecalcMovesStartedSibling()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var (vm, first, second, _) = CreateVirtualPredecessorScenario(resource);

        second.Start = new DateTime(2026, 7, 24);
        second.Finish = new DateTime(2026, 7, 25);
        second.CurrentHours = 4;
        second.EstimatedHours = 4;
        second.PercentComplete = 50;
        second.Resources[0].EstimatedHours = 4;

        vm.ApplyVirtualPredecessorsToAll();

        AssertEqual(new DateTime(2026, 7, 6), first.Start, "A primeira tarefa deve manter o inicio original.");
        AssertEqual(new DateTime(2026, 7, 7), second.Start, "O recalculo geral deve reposicionar a tarefa iniciada pela predecessora virtual.");
        AssertEqual(new DateTime(2026, 7, 8), second.Finish, "O fim deve ser recalculado a partir do novo inicio da tarefa iniciada.");
    }

    private static void ExplicitPredecessorRecalcMovesStartedTask()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var (vm, first, second, _) = CreateVirtualPredecessorScenario(resource);

        second.PredecessorIds.Add(first.Id);
        second.Start = new DateTime(2026, 7, 24);
        second.Finish = new DateTime(2026, 7, 25);
        second.CurrentHours = 4;
        second.EstimatedHours = 4;
        second.PercentComplete = 50;
        second.Resources[0].EstimatedHours = 4;

        vm.ApplyVirtualPredecessorsToAll();

        AssertEqual(new DateTime(2026, 7, 7), second.Start, "O recalculo geral deve reposicionar a tarefa iniciada pela predecessora explicita.");
        AssertEqual(new DateTime(2026, 7, 8), second.Finish, "O fim da tarefa com predecessora explicita deve acompanhar o novo inicio.");
    }

    private static void DurationEditUsesPreviousSiblingEvenWhenPreviousHasExplicitPredecessor()
    {
        var resource = new Resource { Id = 1, Name = "Dev", AvailabilityPercent = 100 };
        var (vm, first, second, third) = CreateVirtualPredecessorScenario(resource);

        second.PredecessorIds.Add(first.Id);
        vm.ApplyVirtualPredecessorsToAll();

        third.Start = new DateTime(2026, 7, 24);
        third.Finish = new DateTime(2026, 7, 25);
        third.CurrentHours = 4;
        third.EstimatedHours = 4;
        third.PercentComplete = 50;
        third.Resources[0].EstimatedHours = 4;

        var thirdVm = vm.FlatTasks.First(t => ReferenceEquals(t.Model, third));
        thirdVm.DurationHours = 8;

        AssertEqual(new DateTime(2026, 7, 8), third.Start, "Ao digitar HH, a tarefa deve iniciar apos o fim da anterior do mesmo recurso.");
        AssertEqual(new DateTime(2026, 7, 9), third.Finish, "O fim deve ser recalculado a partir do inicio reposicionado.");
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

    // ── Sincronização: regra "fechamento vence" no conflito de versão ──────────────
    private static void SyncConflictCurrentUserOpenStateReleases()
    {
        // Última gravação foi do usuário atual e TFS ainda aberto → libera.
        if (!TfsImportService.ShouldReleaseSyncConflict(isCurrentSyncUser: true, localPercentComplete: 40, tfsState: "Active"))
            throw new InvalidOperationException("Conflito com última gravação do usuário atual e TFS aberto deveria liberar.");
    }

    private static void SyncConflictLocal100OtherUserBlocks()
    {
        // Se a versao do TFS esta a frente e foi outro usuario, 100% local nao libera no Sync geral.
        if (TfsImportService.ShouldReleaseSyncConflict(isCurrentSyncUser: false, localPercentComplete: 100, tfsState: "Active"))
            throw new InvalidOperationException("NXProject 100% com TFS a frente deve registrar conflito no Sync geral.");
    }

    private static void SyncConflictClosedOtherUserBlocks()
    {
        // NXProject 100% e TFS ja Closed tambem nao libera se a versao do TFS esta a frente.
        if (TfsImportService.ShouldReleaseSyncConflict(isCurrentSyncUser: false, localPercentComplete: 100, tfsState: "Closed"))
            throw new InvalidOperationException("Com TFS 100%/Closed e versao a frente, o conflito deve ficar rosa para resolucao manual.");
    }

    private static void SyncConflictCurrentUserClosedStateBlocks()
    {
        // Mesmo usuario: se o TFS ja esta Closed/100% e a versao esta a frente, o Sync geral nao deve reabrir/sobrescrever.
        if (TfsImportService.ShouldReleaseSyncConflict(isCurrentSyncUser: true, localPercentComplete: 40, tfsState: "Closed"))
            throw new InvalidOperationException("Mesmo usuario com TFS Closed e versao a frente deve registrar conflito para resolucao manual.");
    }

    private static void SyncConflictBelow100OtherUserBlocks()
    {
        // NXProject abaixo de 100% e gravação de outro usuário → conflito real, bloqueia.
        if (TfsImportService.ShouldReleaseSyncConflict(isCurrentSyncUser: false, localPercentComplete: 60, tfsState: "Active"))
            throw new InvalidOperationException("NXProject abaixo de 100% com outro usuário NÃO deveria liberar (conflito real).");
    }

    private static void SyncConflictVersionAheadWithoutPendingWritesDoesNotBlock()
    {
        if (TfsImportService.ShouldRegisterSyncConflict(
                tfsVersionAhead: true,
                hasPendingWrites: false,
                isStoryOrTask: true,
                isCurrentSyncUser: false,
                tfsState: "Active"))
            throw new InvalidOperationException("Versao TFS a frente sem atributo diferente a gravar nao deve registrar conflito.");
    }

    private static void SyncConflictVersionAheadFeatureEpicDoesNotBlockRollup()
    {
        if (TfsImportService.ShouldRegisterSyncConflict(
                tfsVersionAhead: true,
                hasPendingWrites: true,
                isStoryOrTask: false,
                isCurrentSyncUser: false,
                tfsState: "Active"))
            throw new InvalidOperationException("Feature/Epic com rollup a gravar nao deve bloquear pela regra de conflito de Story/Task.");

        if (!TfsImportService.ShouldRegisterSyncConflict(
                tfsVersionAhead: true,
                hasPendingWrites: true,
                isStoryOrTask: true,
                isCurrentSyncUser: false,
                tfsState: "Active"))
            throw new InvalidOperationException("Story/Task com versao a frente e atributo diferente deve registrar conflito.");
    }

    private static void SyncConflictManualOverwriteAllowsStartedItem()
    {
        var startedTask = new ProjectTask
        {
            Id = 1,
            Name = "Story iniciada",
            TfsId = 101,
            PercentComplete = 100
        };
        var notStartedTask = new ProjectTask
        {
            Id = 2,
            Name = "Story nao iniciada",
            TfsId = 102,
            PercentComplete = 0
        };

        var automaticStarted = new TfsImportService.SyncConflictItem
        {
            Task = startedTask,
            AllowStartedOverwrite = false
        };
        var manualStarted = new TfsImportService.SyncConflictItem
        {
            Task = startedTask,
            AllowStartedOverwrite = true
        };
        var automaticNotStarted = new TfsImportService.SyncConflictItem
        {
            Task = notStartedTask,
            AllowStartedOverwrite = false
        };

        if (automaticStarted.CanOverwrite)
            throw new InvalidOperationException("Fluxo automatico nao deve sobrescrever item iniciado.");
        if (!manualStarted.CanOverwrite)
            throw new InvalidOperationException("Resolucao manual deve permitir sobrescrever item iniciado/concluido.");
        if (!automaticNotStarted.CanOverwrite)
            throw new InvalidOperationException("Item nao iniciado deve continuar selecionavel no fluxo automatico.");
    }

    private static void ManualStoryCompletionRequiresDevOpsTasks()
    {
        var storyWithoutCount = new ProjectTask
        {
            Id = 1,
            Name = "Story sem TKs calculado",
            TfsId = 1001,
            TfsType = "Story",
            DevopsTaskCount = null
        };
        if (!TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(storyWithoutCount, 100))
            throw new InvalidOperationException("Story vinculada com TKs nulo deve bloquear a digitacao manual de 100%.");

        var storyWithoutTasks = new ProjectTask
        {
            Id = 2,
            Name = "Story sem Tasks",
            TfsId = 1002,
            TfsType = "User Story",
            DevopsTaskCount = 0
        };
        if (!TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(storyWithoutTasks, 100))
            throw new InvalidOperationException("Story vinculada com TKs = 0 deve bloquear a digitacao manual de 100%.");

        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(
                storyWithoutTasks,
                100,
                enforceStoryCompletionWithTasks: false))
            throw new InvalidOperationException("Configuracao 'Encerrar Story somente com Task' desmarcada deve liberar 100% mesmo com TKs = 0.");

        storyWithoutTasks.DevopsTaskCount = 1;
        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(storyWithoutTasks, 100))
            throw new InvalidOperationException("Story com TKs > 0 deve permitir a digitacao manual de 100%.");

        storyWithoutTasks.DevopsTaskCount = 0;
        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(storyWithoutTasks, 99))
            throw new InvalidOperationException("A trava deve valer apenas para digitacao de 100%.");

        var task = new ProjectTask
        {
            Id = 3,
            Name = "Task filha",
            TfsId = 1003,
            TfsType = "Task",
            DevopsTaskCount = 0
        };
        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(task, 100))
            throw new InvalidOperationException("A trava deve valer para Story, nao para Task.");

        var localStory = new ProjectTask
        {
            Id = 4,
            Name = "Story local",
            TfsType = "Story",
            DevopsTaskCount = 0
        };
        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(localStory, 100))
            throw new InvalidOperationException("Story sem vinculo DevOps nao deve exigir TKs.");

        var noDevOpsActivity = new ProjectTask
        {
            Id = 5,
            Name = "Atividade NoDevOps",
            TfsId = -1,
            TfsType = "NoDevops",
            DevopsTaskCount = 0
        };
        if (TfsImportService.ShouldBlockManualStoryCompletionWithoutDevOpsTasks(noDevOpsActivity, 100))
            throw new InvalidOperationException("Atividade NoDevOps nao deve exigir TKs para digitar 100%.");
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

    // ── Task Plan (Excel/ClosedXML): funções base ────────────────────────────

    private static string NewTempXlsx() =>
        Path.Combine(Path.GetTempPath(), $"nx-taskplan-{Guid.NewGuid():N}.xlsx");

    private static TaskPlanData NewPlan(params string[] cols)
    {
        var table = new System.Data.DataTable();
        foreach (var c in cols) table.Columns.Add(c, typeof(string));
        var data = new TaskPlanData { Table = table, SheetName = "Tarefas", HeaderRow = 1 };
        for (int i = 0; i < cols.Length; i++) data.ColumnSheetMap[cols[i]] = i + 1;
        return data;
    }

    private static void TaskPlanCreateAndLoadRoundTrip()
    {
        var path = NewTempXlsx();
        try
        {
            var data = NewPlan("Task", "Story", "ID Devops");
            data.Table.Rows.Add("Tarefa A", "Story 1", "100:T");
            data.Table.Rows.Add("Tarefa B", "Story 1", "5:I");
            ExcelTaskPlanService.CreateNew(path, data);

            var loaded = ExcelTaskPlanService.Load(path);
            AssertEqual(1, loaded.HeaderRow, "Cabecalho do arquivo novo deve estar na linha 1.");
            AssertEqual(2, loaded.Table.Rows.Count, "As duas linhas devem voltar.");
            if (!loaded.Table.Columns.Contains("Task") || !loaded.Table.Columns.Contains("ID Devops"))
                throw new InvalidOperationException("Colunas Task/ID Devops nao voltaram do arquivo.");
            if (loaded.Table.Rows[0]["ID Devops"]?.ToString() != "100:T")
                throw new InvalidOperationException("Valor do ID Devops (:T) nao foi preservado.");
        }
        finally { File.Delete(path); }
    }

    private static void TaskPlanDetectsHeaderBelowSummary()
    {
        var path = NewTempXlsx();
        try
        {
            using (var wb = new ClosedXML.Excel.XLWorkbook())
            {
                var ws = wb.AddWorksheet("Tarefas");
                ws.Cell(1, 1).Value = "Plano de Tasks";
                ws.Cell(3, 5).Value = "EPIC X";          // bloco de resumo
                ws.Cell(5, 1).Value = "Task";            // linha de titulos
                ws.Cell(5, 2).Value = "Story";
                ws.Cell(5, 3).Value = "ID Devops";
                ws.Cell(6, 1).Value = "Tarefa A";
                ws.Cell(6, 2).Value = "Story 1";
                ws.Cell(7, 1).Value = "Tarefa B";
                ws.Cell(7, 2).Value = "Story 2";
                wb.SaveAs(path);
            }

            var loaded = ExcelTaskPlanService.Load(path);
            AssertEqual(5, loaded.HeaderRow, "A linha de titulos deve ser reconhecida abaixo do resumo.");
            AssertEqual(2, loaded.Table.Rows.Count, "As linhas de dados devem ser lidas apos o cabecalho.");
            if (!loaded.Table.Columns.Contains("Story"))
                throw new InvalidOperationException("Coluna Story nao reconhecida no cabecalho.");
        }
        finally { File.Delete(path); }
    }

    private static void TaskPlanSavePreservesValuesAndColors()
    {
        var path = NewTempXlsx();
        try
        {
            var data = NewPlan("Task", "Story");
            data.Table.Rows.Add("Tarefa A", "Story 1");
            ExcelTaskPlanService.CreateNew(path, data);

            var loaded = ExcelTaskPlanService.Load(path);
            loaded.Table.Rows[0]["Task"] = "Tarefa A editada";
            loaded.Table.Columns.Add(ExcelTaskPlanService.ColorColPrefix + "Task", typeof(string));
            loaded.Table.Rows[0][ExcelTaskPlanService.ColorColPrefix + "Task"] = "#FFFF00";
            ExcelTaskPlanService.Save(path, loaded);

            var again = ExcelTaskPlanService.Load(path);
            if (again.Table.Rows[0]["Task"]?.ToString() != "Tarefa A editada")
                throw new InvalidOperationException("Valor editado nao foi gravado.");
            var color = again.Table.Columns.Contains(ExcelTaskPlanService.ColorColPrefix + "Task")
                ? again.Table.Rows[0][ExcelTaskPlanService.ColorColPrefix + "Task"]?.ToString()
                : null;
            if (!string.Equals(color, "#FFFF00", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cor de fundo nao voltou do arquivo (obtido: '{color}').");
        }
        finally { File.Delete(path); }
    }

    private static void TaskPlanNewColumnKeepsViewPosition()
    {
        var path = NewTempXlsx();
        try
        {
            var data = NewPlan("Task", "Story");
            data.Table.Rows.Add("Tarefa A", "Story 1");
            ExcelTaskPlanService.CreateNew(path, data);

            var loaded = ExcelTaskPlanService.Load(path);
            loaded.FixedNameColumns.Add("Task");
            loaded.FixedNameColumns.Add("Story");
            var nota = loaded.Table.Columns.Add("Nota", typeof(string));
            nota.SetOrdinal(1);   // visão: Task, Nota, Story
            ExcelTaskPlanService.Save(path, loaded);

            // Na planilha, a coluna nova fica no FIM (posicao 3) com o prefixo da visão.
            using (var wb = new ClosedXML.Excel.XLWorkbook(path))
            {
                var header = wb.Worksheet("Tarefas").Cell(1, 3).GetString();
                if (header != "2#_Nota")
                    throw new InvalidOperationException($"Cabecalho da coluna nova deveria ser '2#_Nota' no fim da aba (obtido: '{header}').");
            }

            // Ao reabrir, volta com o nome limpo e na posicao da visão.
            var again = ExcelTaskPlanService.Load(path);
            if (!again.Table.Columns.Contains("Nota"))
                throw new InvalidOperationException("Coluna nova nao voltou com o nome limpo.");
            AssertEqual(1, again.Table.Columns["Nota"]!.Ordinal, "Coluna nova deve voltar na posicao da visão (indice 1).");
        }
        finally { File.Delete(path); }
    }

    private static void TaskPlanDeletedColumnClearedOnSave()
    {
        var path = NewTempXlsx();
        try
        {
            var data = NewPlan("Task", "Story", "Obs");
            data.Table.Rows.Add("Tarefa A", "Story 1", "obs 1");
            ExcelTaskPlanService.CreateNew(path, data);

            var loaded = ExcelTaskPlanService.Load(path);
            loaded.RemovedSheetColumns.Add(loaded.ColumnSheetMap["Obs"]);
            loaded.ColumnSheetMap.Remove("Obs");
            loaded.Table.Columns.Remove("Obs");
            ExcelTaskPlanService.Save(path, loaded);

            var again = ExcelTaskPlanService.Load(path);
            if (again.Table.Columns.Contains("Obs"))
                throw new InvalidOperationException("Coluna excluida nao deveria voltar do arquivo.");
            AssertEqual(1, again.Table.Rows.Count, "As linhas restantes devem continuar integras.");
        }
        finally { File.Delete(path); }
    }

    private static void TaskPlanApplyCreatesInternalTaskLikeSchedule()
    {
        SetCurrentCalendar(new ProjectCalendar());
        var story = new ProjectTask
        {
            Id = 10, TfsId = 500, TfsType = "User Story", Name = "Story Nova",
            TfsState = "New", PercentComplete = 0, Level = 2,
            TfsIterationPath = @"Proj\Sprint 01", SprintNumber = 1,
            Start = new DateTime(2026, 7, 6), Finish = new DateTime(2026, 7, 7)
        };

        // "2d" convertido pelo calendário do cronograma (dias úteis → horas).
        var hours = TaskPlanScheduleRules.ParseEstimatedHours("2d")
            ?? throw new InvalidOperationException("'2d' deveria ser aceito como estimativa.");
        AssertEqual(2 * ProjectCalendarService.WorkingHoursPerDay, hours, "'2d' deve virar 2 dias úteis em horas.");

        var task = TaskPlanScheduleRules.CreateInternalTask(story, 99, "Task interna", "desc", hours);
        AssertEqual(0, task.TfsId ?? -1, "Task interna nasce com TfsId=0 ('criar no TFS'), padrão do AddSubtask.");
        if (task.TfsState != "New" || task.TfsType != "Task")
            throw new InvalidOperationException("Task interna deve nascer como Task em estado New.");
        if (task.HasTfsLink)
            throw new InvalidOperationException("Task interna não pode ter vínculo TFS (DisplayId deve ser '{Id}:I').");
        if (task.SprintNumber != story.SprintNumber || task.TfsIterationPath != story.TfsIterationPath)
            throw new InvalidOperationException("Task interna deve herdar sprint/iteração da Story.");
        // Story em New: a duração pode ser ajustada (fim calculado pode passar do fim da Story).
        AssertEqual(ProjectCalendarService.AddWorkingHours(story.Start, hours), task.Finish,
            "Story em New: o fim da task segue a estimativa, mesmo além do fim da Story.");
    }

    private static void TaskPlanStartedStoryKeepsDuration()
    {
        SetCurrentCalendar(new ProjectCalendar());
        var story = new ProjectTask
        {
            Id = 11, TfsId = 501, TfsType = "User Story", Name = "Story Ativa",
            TfsState = "Active", PercentComplete = 40, Level = 2,
            Start = new DateTime(2026, 7, 6), Finish = new DateTime(2026, 7, 7)
        };

        if (TaskPlanScheduleRules.CanAdjustStoryDuration(story))
            throw new InvalidOperationException("Story iniciada (Active/40%) não pode ter a duração ajustada.");

        // Estimativa maior que o período da Story: o fim fica contido no fim da Story.
        var task = TaskPlanScheduleRules.CreateInternalTask(story, 100, "Task interna", null, 40);
        if (task.Finish > story.Finish)
            throw new InvalidOperationException("Com a Story iniciada, o fim da task não pode passar do fim da Story.");
    }

    private static void SetCurrentCalendar(ProjectCalendar calendar)
    {
        var property = typeof(ProjectCalendarService).GetProperty(nameof(ProjectCalendarService.Current))
            ?? throw new InvalidOperationException("ProjectCalendarService.Current nao encontrado.");
        property.SetValue(null, calendar);
    }
}
