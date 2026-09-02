using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using NXProject.Community.Services;
using NXProject.Models;
using NXProject.Services;
using NXProject.ViewModels;
using StoredConv = NXProject.Services.AiChatHistoryStore.StoredConversation;

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
        ("Cronograma: resumo soma HH rateado (OriginalEstimated), nao span do calendario", SummaryHoursUseRatedOriginalEstimateNotCalendarSpan),
        ("Recursos: Strings.*.xaml nao tem x:Key duplicada (evita crash no load)", StringsHaveNoDuplicateResourceKeys),
        ("Prioridade: faixa central (config/descoberta) clampa e cicla corretamente", TaskPriorityRangeResolvesAndClamps),
        ("TaskBoard: colunas de estado na ordem canonica (desconhecido ao fim)", TaskboardStatesAreOrderedCanonically),
        ("TaskBoard: tag Doing/Done troca preservando as demais (andamento)", DoingTagMergePreservesOtherTags),
        ("Import TFS: task fechada nao dobra HH Original dentro do HH Atual", ClosedTaskDoesNotDoubleOriginalIntoCurrent),
        ("Import TFS: folha encerrada nao herda esforco como restante (import principal)", ImportClosedLeafHasNoRemainingHours),
        ("Cronograma: ID negativo NoDevOps aparece como interno", NoDevOpsNegativeTfsIdDisplaysAsInternal),
        ("Cronograma: DevOps pendente continua com ID interno", PendingDevOpsCreateDisplaysAsInternal),
        ("Cronograma: DevOps aceita predecessor I apenas se I tambem for DevOps", DevOpsPredecessorAcceptsInternalDevOpsOnly),
        ("Cronograma: NoDevOps aceita predecessor I de qualquer tipo", NoDevOpsPredecessorAcceptsAnyInternalType),
        ("Cronograma: arrasto permite mover Feature para outro Epic", DragDropMovesFeatureToAnotherEpic),
        ("Cronograma: arrasto permite mover Story para outra Feature", DragDropMovesStoryToAnotherFeature),
        ("Cronograma: arrasto permite mover Task para outra Story", DragDropMovesTaskToAnotherStory),
        ("Cronograma: arrasto entre pais aceita soltar sobre irmao destino", DragDropMovesHierarchyItemsToSiblingInAnotherParent),
        ("Cronograma: arrasto bloqueia troca fora da hierarquia DevOps", DragDropBlocksInvalidHierarchyMoves),
        ("Import TFS: irmaos entram na ordem do backlog (rank), sem jogar item sem rank pro fim", ImportOrdersSiblingsByBacklogRank),
        ("Import TFS: ordem gravada no cronograma e a ordem recebida do DevOps", ImportedOrderMatchesReceivedOrder),
        ("Import TFS: item sem StackRank recebe rank calculado na posicao recebida", ImportFillsMissingBacklogRank),
        ("Sync TFS: reordenacao do backlog no DevOps gera aviso no relatorio", SyncWarnsWhenBacklogOrderIsRewritten),
        ("Sync TFS: ordem grava em StackRank (Agile) ou BacklogPriority (Scrum)", SyncWritesRankOnTheProcessField),
        ("Cronograma: Feature e Story preservam ordem do DevOps independente da prioridade", RebuildPreservesDevOpsHierarchyOrderIgnoringPriority),
        ("Cronograma: Task ordena por prioridade e desempata pela ordem do DevOps", RebuildOrdersTasksByPriorityThenDevOpsRank),
        ("Cronograma: botao marco cria Marco-Devops irmao para selecao DevOps", AddMilestoneCreatesDevOpsSiblingForDevOpsSelection),
        ("Cronograma: Ctrl botao marco cria Marco-Devops filho", AddMilestoneCreatesDevOpsChildWithCtrl),
        ("Cronograma: Ctrl botao marco nao cria filho em marco", AddMilestoneDoesNotCreateChildUnderMilestone),
        ("Sync TFS: Marco-Devops cria Task com tag MARCO-PROJECT", DevOpsMilestoneCreateOpsAddsMarcoProjectTag),
        ("Sync TFS: data fim usa fim inclusivo", TfsSyncFinishUsesInclusiveDate),
        ("Sync TFS: Task cria descricao no mesmo padrao da Story", TfsSyncTaskCreateOpsIncludesDescription),
        ("Import TFS: estado da Task define percentual padrao", TfsImportTaskStateDefinesDefaultPercent),
        ("Import TFS: Story iniciada/encerrada NAO e replanejada (usa Data_Inicio real)", ImportClosedStoryKeepsExplicitStart),
        ("Cronograma: editar estado da Task ajusta percentual", ScheduleStateEditUpdatesTaskPercent),
        ("Sync TFS: cadeia nova cria Feature, Story e Task no pai correto", TfsSyncNewHierarchyUsesImmediateDevOpsParent),
        ("Sync TFS: Task orfa nao cai no Work Item Project raiz", TfsSyncOrphanTaskDoesNotUseRootProject),
        ("Sync TFS: Marco-Devops usa irmao anterior como predecessora implicita", DevOpsMilestoneUsesPreviousSiblingAsImplicitPredecessor),
        ("Sync TFS: Marco-Devops sem irmao anterior usa pai como predecessora implicita", DevOpsMilestoneUsesParentAsImplicitPredecessor),
        ("Sync TFS: Marco-Devops resolve predecessora explicita com filhos", DevOpsMilestoneResolvesExplicitPredecessorWithChildren),
        ("Import TFS: Marco-Devops ignora predecessora fora da hierarquia para posicionar", DevOpsMilestonePositionIgnoresExternalHierarchyPredecessor),
        ("Import TFS: predecessora externa gera aviso sem erro", ExternalPredecessorWarnsWithoutImportError),
        ("Import TFS: Marco-Devops usa pai como ancora de posicionamento", DevOpsMilestonePositionUsesParentAnchor),
        ("Import TFS: NoDevOps preserva posicao para predecessora virtual", ImportPreservesNoDevOpsSiblingPosition),
        ("Import TFS: atividades internas DevOps sao vinculadas por nome ou preservadas", ImportMatchesOrPreservesInternalDevOpsActivities),
        ("Resumo: datas e percentual consolidam filhos", SummaryRollupUsesChildrenDatesAndHours),
        ("Resumo: atividade 100% absorve o HH Restante (antecipacao)", CompletedTaskAbsorbsRemainingHours),
        ("Alocacao: decompoe HH da Story (restante p/ responsavel, corte se estoura)", AllocationStoryDecompositionFactors),
        ("Alocacao: resumo de tasks por recurso (Closed=Completed, senao Estimate)", TaskAllocationSummaryFromDevOps),
        ("Arquivo: resumo de tasks por recurso sobrevive salvar/abrir", TaskAllocationSummaryRoundTrips),
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
        ("Task Plan: backup antes de salvar cria copia e aplica retencao", TaskPlanBackupBeforeSaveCreatesCopyAndRetains15Days),
        ("Task Plan: visao de filtro (EPIC/cor/coluna) sobrevive a serializacao", TaskPlanFilterViewRoundTrips),
        ("Task Plan: coluna nova grava no fim com prefixo e volta na posicao", TaskPlanNewColumnKeepsViewPosition),
        ("Task Plan: coluna excluida some da planilha ao salvar", TaskPlanDeletedColumnClearedOnSave),
        ("Task Plan: aplicar cria task interna no padrao do cronograma", TaskPlanApplyCreatesInternalTaskLikeSchedule),
        ("Task Plan IA: esforco aceita sufixo h e chave acentuada", TaskPlanAiResponseAcceptsHourSuffixAndAccentedKeys),
        ("Task Plan IA: responsavel casa nome invertido/parcial e recusa ambiguo", TaskPlanResourceMatcherHandlesCitedNames),
        ("Task Plan: story iniciada NAO tem a duracao ajustada", TaskPlanStartedStoryKeepsDuration),
        ("Task Plan: log de sync atualiza ID interno para ID DevOps na planilha", TaskPlanBackfillIdsFromSyncLog),
        ("Arquivo: gravar com ID interno duplicado e BLOQUEADO com a atividade na mensagem", SaveBlocksDuplicateTaskIds),
        ("Arquivo: leitura normaliza ID interno duplicado de arquivo legado", LoadNormalizesDuplicateTaskIds),
        ("Sync TFS: ID interno duplicado bloqueia a sincronizacao", SyncBlocksDuplicateTaskIds),
        ("Sync TFS: Task so grava sob Story (pai Feature/Epic e bloqueado)", SyncBlocksTaskWithoutStoryParent),
        ("Sync TFS: Marco-Devops pode ficar fora de Story", SyncAllowsDevOpsMilestoneOutsideStory),
        ("Sync TFS: duas Tasks de mesmo nome na Story bloqueiam a sincronizacao", SyncBlocksDuplicateTaskNamesInStory),
        ("IA ETA: historico persiste, adapta a duracao real e escala pelos bytes", AiRunStatsPersistsAdaptsAndScales),
        ("IA Chat: historico por cronograma respeita limite e 0=infinito", AiChatHistoryPersistsPerProjectWithLimit)
    ];

    private static int Main(string[] args)
    {
        var category = args.Length > 0 ? args[0] : "schedule";
        if (string.Equals(category, "simulate-openai", StringComparison.OrdinalIgnoreCase))
        {
            SimulateOpenAi();
            return 0;
        }
        if (string.Equals(category, "ai-sim", StringComparison.OrdinalIgnoreCase))
            return RunAiIncludeSimulation(
                args.Length > 1 ? args[1] : "",
                args.Length > 2 ? args[2] : "",
                args.Length > 3 ? args[3] : "").GetAwaiter().GetResult();
        _solutionRoot = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

        List<(string Name, Action Test)> tests = category.ToLowerInvariant() switch
        {
            "packaging-community" =>
            [
                ("Empacotamento: NXProject.Community-Release.zip contem arquivos essenciais", ValidateCommunityReleaseZip),
                ("Empacotamento: Release.zip nao vira standalone", ValidateCommunityReleaseIsNotStandalone),
                ("Empacotamento: toda DLL de terceiros do publish esta no NXProject-Setup.zip", ValidateThirdPartyLibsAreInSetupZip)
            ],
            "packaging-setup" =>
            [
                ("Empacotamento: NXProject-Setup.zip contem runtime e libs essenciais", ValidateSetupZip),
                ("Empacotamento: NXProject-Setup.zip contem DLLs necessarias", ValidateSetupZipKeepsRequiredDlls),
                ("Setup: timestamp e intrinseco ao zip e igual ao embutido no build", ValidateSetupTimestampIntrinsic),
                ("Empacotamento: toda DLL de terceiros do publish esta no NXProject-Setup.zip", ValidateThirdPartyLibsAreInSetupZip)
            ],
            "packaging" => PackagingTests,
            "ai" => AiIntegrationTests,
            _ => ScheduleTests
        };

        // Integração com IA Local: só roda quando os recursos estão instalados na máquina
        // de compilação (pasta configurada com llama.dll + modelo .gguf); senão, pula.
        if (ReferenceEquals(tests, AiIntegrationTests) && !IsLocalAiInstalled())
        {
            Console.WriteLine("NXTestUnit - integracao com IA Local");
            Console.WriteLine();
            Console.WriteLine("[SKIP] IA Local nao instalada nesta maquina - bloco de integracao ignorado.");
            return 0;
        }

        // Qualquer categoria desconhecida cai nos testes de cronograma (default do switch),
        // entao "isSchedule" deve seguir a MESMA regra — senao os testes de cronograma
        // rodam sem ResetCalendar() e usam um calendario default (datas +1 dia).
        var isSchedule = ReferenceEquals(tests, ScheduleTests);

        if (isSchedule)
            SetCurrentCalendar(new ProjectCalendar());

        var failures = new List<string>();
        Console.WriteLine(isSchedule
            ? "NXTestUnit - testes criticos de cronograma"
            : ReferenceEquals(tests, AiIntegrationTests)
                ? "NXTestUnit - integracao com IA Local"
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
        var suiteLabel = ReferenceEquals(tests, AiIntegrationTests) ? "Integracao IA Local" : "NXTestUnit";
        if (failures.Count == 0)
        {
            Console.WriteLine($"{suiteLabel} concluido: {tests.Count} testes passaram.");
            return 0;
        }

        Console.WriteLine($"{suiteLabel} falhou: {failures.Count} de {tests.Count} testes falharam.");
        foreach (var failure in failures)
            Console.WriteLine($" - {failure}");

        return 1;
    }

    private static readonly List<(string Name, Action Test)> PackagingTests =
    [
        ("Empacotamento: NXProject.Community-Release.zip contem arquivos essenciais", ValidateCommunityReleaseZip),
        ("Empacotamento: Release.zip nao vira standalone", ValidateCommunityReleaseIsNotStandalone),
        ("Empacotamento: NXProject-Setup.zip contem runtime e libs essenciais", ValidateSetupZip),
        ("Empacotamento: NXProject-Setup.zip contem DLLs necessarias", ValidateSetupZipKeepsRequiredDlls),
        ("Setup: timestamp e intrinseco ao zip e igual ao embutido no build", ValidateSetupTimestampIntrinsic),
        ("Empacotamento: toda DLL de terceiros do publish esta no NXProject-Setup.zip", ValidateThirdPartyLibsAreInSetupZip)
    ];

    // ── Integração com IA Local (LLaMA) — roda só com os recursos instalados ─────
    private static readonly List<(string Name, Action Test)> AiIntegrationTests =
    [
        ("IA Local: instalacao valida (llama.dll carrega e modelo GGUF integro)", AiLocalInstallationIsValid),
        ("IA Local: inferencia responde a um prompt minimo", AiLocalInferenceResponds)
    ];

    private static bool IsLocalAiInstalled()
    {
        var folder = LocalAIResourceStore.LoadFolder();
        return !string.IsNullOrWhiteSpace(folder)
               && Directory.Exists(folder)
               && File.Exists(Path.Combine(folder, LocalAIResourceStore.NativeDllName))
               && Directory.EnumerateFiles(folder, "*.gguf").Any();
    }

    private static void AiLocalInstallationIsValid()
    {
        var folder = LocalAIResourceStore.LoadFolder();
        var r = LocalAIResourceStore.Validate(folder);
        if (!r.NativePresent || !r.NativeLoads)
            throw new InvalidOperationException(r.NativeMessage ?? "llama.dll nao carregou.");
        if (!r.ModelPresent || !r.ModelValid)
            throw new InvalidOperationException(r.ModelMessage ?? "modelo GGUF invalido.");
    }

    /// <summary>
    /// Simulação da ação "Incluir Tasks na Planilha" com a IA Local, no formato de contexto
    /// OFICIAL do Task Plan (Stories em aberto, tabela compacta "id | nome | feature | epic",
    /// sem tasks da planilha). Mede prefill (contexto), geração (tokens/s) e tempo total.
    /// Uso: NXTestUnit.exe ai-sim "cronograma.xml" "texto.txt" [feature-nova|story-nova]
    /// </summary>
    private static async Task<int> RunAiIncludeSimulation(string xmlPath, string textPath, string mode = "")
    {
        if (!File.Exists(xmlPath) || !File.Exists(textPath))
        {
            Console.WriteLine($"Arquivos nao encontrados:\n  {xmlPath}\n  {textPath}");
            return 1;
        }

        var project = XmlProjectService.Load(xmlPath);
        var text = File.ReadAllText(textPath);

        var stories = new List<ProjectTask>();
        void Collect(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if (TfsImportService.IsStoryTypePublic(t.TfsType) && t.PercentComplete < 100)
                    stories.Add(t);
                Collect(t.Children);
            }
        }
        Collect(project.Tasks);

        static string AncestorName(ProjectTask t, string type)
        {
            for (var p = t.Parent; p != null; p = p.Parent)
                if (string.Equals(p.TfsType?.Trim(), type, StringComparison.OrdinalIgnoreCase))
                    return p.Name ?? "";
            return "";
        }

        var resources = project.Resources
            .Where(r => r.Type == ResourceType.Work)
            .Select(r => string.IsNullOrWhiteSpace(r.Name) ? (r.DisplayName ?? "").TrimStart('*').Trim() : r.Name.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Modos "Story nova"/"Feature nova" (checkboxes do painel): contexto e prompt
        // iguais aos do Task Plan — Features ou EPICs no lugar das Stories.
        var newFeature = string.Equals(mode, "feature-nova", StringComparison.OrdinalIgnoreCase);
        var newStory = newFeature || string.Equals(mode, "story-nova", StringComparison.OrdinalIgnoreCase);

        var allNodes = new List<ProjectTask>();
        void CollectAll(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks) { allNodes.Add(t); CollectAll(t.Children); }
        }
        CollectAll(project.Tasks);
        bool IsType(ProjectTask t, string type) => string.Equals(t.TfsType?.Trim(), type, StringComparison.OrdinalIgnoreCase);

        var ctx = new System.Text.StringBuilder();
        if (newFeature)
        {
            var epics = allNodes.Where(t => IsType(t, "Epic")).ToList();
            ctx.AppendLine("EPICS (id | nome) — EPICs existentes no cronograma:");
            foreach (var ep in epics) ctx.AppendLine($"{ep.Id} | {ep.Name}");
        }
        else if (newStory)
        {
            var features = allNodes.Where(t => IsType(t, "Feature") && t.PercentComplete < 100).ToList();
            ctx.AppendLine("FEATURES (id | nome | epic) — Features existentes no cronograma:");
            foreach (var f in features) ctx.AppendLine($"{f.Id} | {f.Name} | {AncestorName(f, "Epic")}");
        }
        else
        {
            ctx.AppendLine("STORIES (id | nome | feature | epic) — somente Stories em aberto:");
            foreach (var s in stories)
                ctx.AppendLine($"{s.Id} | {s.Name} | {AncestorName(s, "Feature")} | {AncestorName(s, "Epic")}");
        }
        ctx.AppendLine().AppendLine("RECURSOS: " + string.Join(" | ", resources));
        ctx.AppendLine().AppendLine("TEXTO:").AppendLine(text);

        Console.WriteLine($"Cronograma: {stories.Count} stories em aberto | recursos: {resources.Count} | contexto: {ctx.Length:N0} chars"
            + (newFeature ? " | modo FEATURE NOVA" : newStory ? " | modo STORY NOVA" : ""));
        Console.WriteLine();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var monitor = new System.Timers.Timer(10000);
        monitor.Elapsed += (_, _) => Console.WriteLine($"   [{sw.Elapsed:mm\\:ss}] {LocalLlamaService.CurrentStatus ?? "..."}");
        monitor.Start();
        try
        {
            var systemPrompt = AISettingsStore.PlanIncludeActionPrompt
                + (newFeature ? AISettingsStore.PlanIncludeNewFeatureSuffix
                    : newStory ? AISettingsStore.PlanIncludeNewStorySuffix : "");
            var userMsg = newFeature
                ? "Encontre o EPIC de cada Feature nova do TEXTO e devolva o JSON."
                : newStory
                    ? "Encontre a Feature de cada Story nova do TEXTO e devolva o JSON."
                    : "Encontre a Story de cada atividade do TEXTO e devolva o JSON.";
            var answer = await LocalLlamaService.GenerateAsync(systemPrompt, userMsg + "\n\n" + ctx, cts.Token);
            monitor.Stop();
            sw.Stop();
            Console.WriteLine($"Tempo total: {sw.Elapsed:mm\\:ss} | backend: {LocalLlamaService.ActiveBackendLabel ?? "?"} | resposta: {answer.Length:N0} chars");
            Console.WriteLine("Resposta: " + answer);

            // Mesmo pós-processamento do Task Plan: extração com reparo + leitura dos campos.
            var (json, truncated) = TaskPlanScheduleRules.ExtractJsonArray(answer);
            if (json == null)
            {
                Console.WriteLine("RESULTADO: nenhum JSON de atividades recuperável — o Task Plan não incluiria nada.");
                return 1;
            }
            if (truncated)
                Console.WriteLine("AVISO: resposta truncada — itens completos aproveitados pelo reparo.");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var n = 0;
            var unmatched = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var task = TaskPlanScheduleRules.GetJsonString(item, "task");
                if (string.IsNullOrWhiteSpace(task)) continue;
                n++;
                var story = TaskPlanScheduleRules.GetJsonString(item, "story");
                var hours = TaskPlanScheduleRules.ParseEstimatedHours(
                    TaskPlanScheduleRules.GetJsonString(item, "esforco", "esforço")) ?? 1.0;
                var resp = TaskPlanScheduleRules.GetJsonString(item, "responsavel", "responsável");
                // Mesmo casamento do Task Plan: sem recurso, o nome cai na Observação.
                var matched = TaskPlanResourceMatcher.Find(project.Resources, resp);
                var respText = resp == null ? ""
                    : matched != null
                        ? $" — Responsável: {TaskPlanResourceMatcher.PlanName(matched)}"
                        : $" — SEM recurso (vai para Observação): \"{resp}\"";
                if (matched == null && resp != null) unmatched++;
                Console.WriteLine($"  {n,2}. [{story}] {task} — {hours:0.##}h{respText}");
            }
            Console.WriteLine($"RESULTADO: {n} task(s) seriam incluídas na planilha"
                + (unmatched > 0 ? $"; {unmatched} com responsável NÃO casado (Observação)." : "; responsáveis todos casados."));
            // IDs de hierarquia: no modo Feature/Story nova só o nível EXISTENTE tem ID —
            // Feature/Story novas nascem no cronograma no Aplicar (CreateStoryPath).
            if (newStory)
                Console.WriteLine(newFeature
                    ? "IDs: só o ID do EPIC é preenchido; Feature e Story novas ficam sem ID até o Aplicar."
                    : "IDs: só os IDs de EPIC/Feature são preenchidos; a Story nova fica sem ID até o Aplicar.");
            return 0;
        }
        catch (Exception ex)
        {
            monitor.Stop();
            Console.WriteLine($"FALHOU apos {sw.Elapsed:mm\\:ss}: {ex.Message}");
            return 1;
        }
    }

    private static void AiLocalInferenceResponds()
    {
        // Prompt mínimo: valida o ciclo completo (carga do modelo + geração) em CPU.
        var answer = LocalLlamaService.GenerateAsync(
                "Responda SEMPRE com uma unica palavra: OK",
                "Diga OK.",
                new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token)
            .GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("A inferencia local devolveu resposta vazia.");
    }

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

    private static void ValidateCommunityReleaseIsNotStandalone()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "community", "NXProject.Community-Release.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "release-zip-forbidden-standalone-files.json");
        ValidateZipDoesNotContainManifest(zipPath, manifestPath);
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

    /// <summary>
    /// O Release.zip do Community leva SO os binarios do nucleo; as bibliotecas de
    /// terceiros vem do Setup. Este teste garante que TODA .dll de terceiros presente
    /// no publish atual existe dentro do NXProject-Setup.zip — pega dependencia NuGet
    /// nova (ex.: ClosedXML) sem o Setup regenerado, antes de publicar.
    /// </summary>
    private static void ValidateThirdPartyLibsAreInSetupZip()
    {
        var publishDir = Path.Combine(_solutionRoot, "dist", "community", "publish-win-x64");
        var setupZip = Path.Combine(_solutionRoot, "dist", "setup", "NXProject-Setup.zip");
        if (!Directory.Exists(publishDir))
            throw new InvalidOperationException($"Publish nao encontrado: {publishDir}. Rode a release do Community antes.");
        if (!File.Exists(setupZip))
            throw new InvalidOperationException($"Setup nao encontrado: {setupZip}. Rode release-nxproject-setup.ps1.");

        using var zip = ZipFile.OpenRead(setupZip);
        var setupEntries = zip.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Directory.EnumerateFiles(publishDir, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(n => n != null
                && !n.StartsWith("NXProject.Community", StringComparison.OrdinalIgnoreCase)
                && !n.StartsWith("NXProject.Shared", StringComparison.OrdinalIgnoreCase)
                && !setupEntries.Contains(n!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"{missing.Count} DLL(s) de terceiros do publish NAO estao no NXProject-Setup.zip " +
                $"(regenerar com release-nxproject-setup.ps1): {string.Join(", ", missing)}");
    }

    private static void ValidateSetupZip()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "setup", "NXProject-Setup.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "setup-zip-required-files.json");
        ValidateZipAgainstManifest(zipPath, manifestPath);
        ValidateSelfContainedNotSingleFile(zipPath, "NXProject-Setup.exe", "NXProject-Setup.dll", "NXProject-Setup.runtimeconfig.json");
    }

    private static void ValidateSetupZipKeepsRequiredDlls()
    {
        var zipPath = Path.Combine(_solutionRoot, "dist", "setup", "NXProject-Setup.zip");
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "PackagingManifests", "dlls-necessarias.md");
        ValidateZipContainsMarkdownDllList(zipPath, manifestPath);
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

    private static void ValidateZipDoesNotContainManifest(string zipPath, string manifestPath)
    {
        if (!File.Exists(zipPath))
            throw new InvalidOperationException($"Zip nao encontrado: {zipPath}");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Manifest nao encontrado: {manifestPath}");

        var forbiddenNames = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException($"Manifest vazio ou invalido: {manifestPath}");

        using var zip = ZipFile.OpenRead(zipPath);
        var entryNames = zip.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var present = forbiddenNames.Where(n => entryNames.Contains(n)).ToList();
        if (present.Count > 0)
            throw new InvalidOperationException(
                $"'{Path.GetFileName(zipPath)}' virou pacote standalone/base. " +
                $"Arquivos que pertencem ao NXProject-Setup.zip encontrados: {string.Join(", ", present)}");
    }

    private static void ValidateZipContainsMarkdownDllList(string zipPath, string manifestPath)
    {
        if (!File.Exists(zipPath))
            throw new InvalidOperationException($"Zip nao encontrado: {zipPath}");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Lista de DLLs necessarias nao encontrada: {manifestPath}");

        var requiredDlls = ReadRequiredDllsFromMarkdown(manifestPath);
        if (requiredDlls.Count == 0)
            throw new InvalidOperationException($"Lista de DLLs necessarias vazia: {manifestPath}");

        using var zip = ZipFile.OpenRead(zipPath);
        var entryNames = zip.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = requiredDlls.Where(n => !entryNames.Contains(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"'{Path.GetFileName(zipPath)}' nao contem DLL(s) necessarias: {string.Join(", ", missing)}. " +
                $"Atualize release-nxproject-setup.ps1 ou revise {Path.GetFileName(manifestPath)}.");
    }

    private static List<string> ReadRequiredDllsFromMarkdown(string manifestPath) =>
        File.ReadLines(manifestPath)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    // Regressao do "duracao louca" (699h): um resumo cujas folhas so tem
    // OriginalEstimatedHours (HH veio do rateio; CurrentHours=EstimatedHours=0) deve
    // somar essas horas rateadas, e NAO o span do calendario entre Start e Finish.
    // Uma x:Key duplicada num ResourceDictionary estoura em runtime ("DeferrableContent
    // iniciou uma exceção"). Este teste varre os Strings.*.xaml e falha se houver duplicata,
    // sugerindo qualificar com a tela/fonte (ver ResourceKeyRegistry).
    // Crítico: as colunas do taskboard precisam sair na ordem de fluxo (New→Active→Resolved→
    // Closed…), com estados desconhecidos ao fim — senão o board fica ilegível.
    private static void TaskboardStatesAreOrderedCanonically()
    {
        var ordered = TfsImportService.OrderTaskboardStates(
            new[] { "Closed", "New", "Zebra", "Active", "Resolved", "New" });
        AssertEqual("New", ordered[0], "New deve vir primeiro.");
        AssertEqual("Active", ordered[1], "Active depois de New.");
        AssertEqual("Resolved", ordered[2], "Resolved depois de Active.");
        AssertEqual("Closed", ordered[3], "Closed depois de Resolved.");
        AssertEqual("Zebra", ordered[4], "Estado desconhecido vai para o fim.");
        AssertEqual(5, ordered.Count, "Duplicatas (New) sao removidas.");
    }

    // Crítico: marcar/tirar Doing e virar Done não pode perder as outras tags do work item.
    private static void DoingTagMergePreservesOtherTags()
    {
        AssertEqual("MARCO-PROJECT; Doing",
            TfsImportService.MergeDoingTag("MARCO-PROJECT", "Doing"),
            "Adiciona Doing preservando a tag existente.");
        AssertEqual("MARCO-PROJECT; Done",
            TfsImportService.MergeDoingTag("MARCO-PROJECT; Doing", "Done"),
            "Doing vira Done (troca), mantendo as demais.");
        AssertEqual("MARCO-PROJECT",
            TfsImportService.MergeDoingTag("MARCO-PROJECT; Done", null),
            "Remover (null) tira Done e mantem as demais.");
        AssertEqual("Doing",
            TfsImportService.MergeDoingTag("", "Doing"),
            "Sem tags previas, fica so a Doing.");
        AssertEqual("A; B",
            TfsImportService.MergeDoingTag("A; Doing; B", null),
            "Remove Doing do meio, preserva ordem das demais.");
    }

    private static void TaskPriorityRangeResolvesAndClamps()
    {
        // Sem config habilitada → padrão 1..9 (o do formulário do DevOps).
        var def = TaskPriorityRange.FromOptions(null);
        AssertEqual(1, def.Min, "Faixa padrao comeca em 1.");
        AssertEqual(9, def.Max, "Faixa padrao vai ate 9.");
        AssertEqual(9, def.Clamp(100), "Clamp limita ao maximo.");
        AssertEqual(1, def.Clamp(0), "Clamp limita ao minimo.");
        AssertEqual(1, def.Next(9), "Ao ciclar do maximo volta ao minimo.");
        AssertEqual(4, def.Next(3), "Ciclo incrementa dentro da faixa.");

        // Config habilitada (2..5) manda quando nao ha descoberta.
        var opts = new TfsConnectionOptions { TaskPriorityRangeEnabled = true, TaskPriorityMin = 2, TaskPriorityMax = 5 };
        var cfg = TaskPriorityRange.FromOptions(opts);
        AssertEqual(2, cfg.Min, "Config define o minimo.");
        AssertEqual(5, cfg.Max, "Config define o maximo.");

        // Maximo descoberto no template manda sobre o da config.
        var disc = TaskPriorityRange.FromOptions(opts, discoveredMax: 9);
        AssertEqual(9, disc.Max, "Maximo descoberto (validateOnly) sobrepoe o da config.");
    }

    private static void StringsHaveNoDuplicateResourceKeys()
    {
        static string? FindUp(string rel)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var p = Path.Combine(dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
        }

        var files = new[]
        {
            "NXProject.Community/Strings/Strings.pt-BR.xaml",
            "NXProject.Community/Strings/Strings.en-US.xaml"
        };

        var problems = new List<string>();
        var checkedAny = false;
        foreach (var rel in files)
        {
            var path = FindUp(rel);
            if (path == null) continue; // fora do checkout (ex.: rodando do zip) → ignora
            checkedAny = true;
            var content = File.ReadAllText(path);
            var firstLine = new Dictionary<string, int>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(content, "x:Key=\"([^\"]+)\""))
            {
                var key = m.Groups[1].Value;
                var line = content.Take(m.Index).Count(ch => ch == '\n') + 1;
                if (firstLine.TryGetValue(key, out var prev))
                    problems.Add($"{Path.GetFileName(path)}: chave duplicada '{key}' (linhas {prev} e {line})");
                else
                    firstLine[key] = line;
            }
        }

        if (!checkedAny)
            return; // não achou os fontes (execução fora do repositório): não falha
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Chaves de recurso duplicadas (qualifique com o nome da tela/fonte):\n  "
                + string.Join("\n  ", problems));
    }

    private static void SummaryHoursUseRatedOriginalEstimateNotCalendarSpan()
    {
        var start = new DateTime(2026, 8, 12);
        var finish = new DateTime(2026, 8, 31); // intervalo largo de proposito
        var story = new ProjectTask
        {
            Id = 2200,
            Name = "Story rateada",
            TfsType = "Story",
            Level = 0,
            IsSummary = true,
            Start = start,
            Finish = finish
        };
        // Tres tasks fechadas sem HH do DevOps, so com a fracao do rateio.
        foreach (var i in new[] { 1, 2, 3 })
        {
            story.Children.Add(new ProjectTask
            {
                Id = 2200 + i,
                Name = "Task rateada " + i,
                TfsType = "Task",
                Parent = story,
                Level = 1,
                Start = start,
                Finish = finish,
                CurrentHours = 0,
                EstimatedHours = 0,
                OriginalEstimatedHours = 12.333333333333334
            });
        }

        var vm = new TaskViewModel(story);

        // 3 x 12,3333 = 37h (rateio), nao ~440h de span do calendario.
        AssertEqual(37.0, Math.Round(vm.DurationHours, 3),
            "Resumo deve somar OriginalEstimatedHours das folhas, nao o span do calendario.");
    }

    // Regressao do "rateio + task" (task fechada dobrando o HH): uma Task fechada com
    // CompletedWork=30 e OriginalEstimate=30 nao pode virar 60. O Original nao e "restante";
    // o AbsorbRemainingHoursWhenComplete somaria Original em cima do Atual e dobraria.
    private static void ClosedTaskDoesNotDoubleOriginalIntoCurrent()
    {
        // Fechada com apontamento: Atual=Completed, Restante=0 (nao 30+30).
        var (cur, est) = TfsImportService.ResolveTaskScheduleHours(
            originalEstimate: 30, completedWork: 30, percentComplete: 100);
        AssertEqual(30.0, cur ?? -1, "Task fechada: HH Atual = CompletedWork (nao Completed+Original).");
        if (est != null)
            throw new InvalidOperationException("Task fechada nao tem HH Restante (Original nao e restante).");

        // Fechada sem apontamento: usa o Original como esforco concluido.
        var (cur2, est2) = TfsImportService.ResolveTaskScheduleHours(30, 0, 100);
        AssertEqual(30.0, cur2 ?? -1, "Task fechada sem CompletedWork: usa o Original como Atual.");
        if (est2 != null)
            throw new InvalidOperationException("Task fechada sem apontamento tambem tem restante 0.");

        // Em andamento: mantem Original como restante e Completed como atual.
        var (cur3, est3) = TfsImportService.ResolveTaskScheduleHours(30, 10, 50);
        AssertEqual(10.0, cur3 ?? -1, "Task em andamento: HH Atual = CompletedWork.");
        AssertEqual(30.0, est3 ?? -1, "Task em andamento: HH Restante = Original.");

        // Nova (0%): sem atual, restante = Original.
        var (cur4, est4) = TfsImportService.ResolveTaskScheduleHours(30, 0, 0);
        if (cur4 != null)
            throw new InvalidOperationException("Task nova nao tem HH Atual.");
        AssertEqual(30.0, est4 ?? -1, "Task nova: HH Restante = Original.");

        // Resumo com duas tasks fechadas (30 e 10) deve dar 40 no cronograma, nao 80.
        var story = new ProjectTask { Id = 3300, Name = "Story", TfsType = "Story", IsSummary = true,
            Start = new DateTime(2026, 8, 4), Finish = new DateTime(2026, 8, 10) };
        foreach (var (orig, comp, id) in new[] { (30.0, 30.0, 3301), (10.0, 10.0, 3302) })
        {
            var (c, e) = TfsImportService.ResolveTaskScheduleHours(orig, comp, 100);
            story.Children.Add(new ProjectTask { Id = id, Name = "Task " + id, TfsType = "Task",
                Parent = story, PercentComplete = 100, CurrentHours = c, EstimatedHours = e,
                Start = story.Start, Finish = story.Finish });
        }
        AssertEqual(40.0, new TaskViewModel(story).DurationHours,
            "Resumo de tasks fechadas deve somar CompletedWork (40), nao dobrar com o Original (80).");
    }

    // Regressao do import principal (BuildStory): a folha encerrada sem RemainingWork explicito
    // nao pode herdar o esforco estimado como "restante" — senao AbsorbRemaining o soma no HH
    // Atual (CompletedWork) e dobra (ex.: 30 vira 60). Trava a regra ResolveImportRemainingHours.
    private static void ImportClosedLeafHasNoRemainingHours()
    {
        // Encerrada sem RemainingWork explicito: restante = null (nao o estimado de 30).
        if (TfsImportService.ResolveImportRemainingHours(remainingWork: null, plannedHours: 30, isClosed: true) != null)
            throw new InvalidOperationException("Folha encerrada sem RemainingWork nao tem restante (evita dobrar o HH Atual).");

        // Encerrada com RemainingWork explicito: respeita o valor informado.
        AssertEqual(5.0,
            TfsImportService.ResolveImportRemainingHours(5, 30, true) ?? -1,
            "Folha encerrada com RemainingWork explicito usa o valor informado.");

        // Em andamento/nova: restante = valor planejado (RemainingWork ou esforco).
        AssertEqual(30.0,
            TfsImportService.ResolveImportRemainingHours(null, 30, false) ?? -1,
            "Folha em andamento usa o HH planejado como restante.");
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

    private static void DragDropMovesFeatureToAnotherEpic()
    {
        var epicA = new ProjectTask
        {
            Id = 500,
            Name = "Epic A",
            TfsType = "Epic",
            Level = 0,
            IsSummary = true
        };
        var epicB = new ProjectTask
        {
            Id = 600,
            Name = "Epic B",
            TfsType = "Epic",
            Level = 0,
            IsSummary = true
        };
        var feature = new ProjectTask
        {
            Id = 501,
            Name = "Feature movida",
            TfsType = "Feature",
            Parent = epicA,
            Level = 1,
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7)
        };
        var story = new ProjectTask
        {
            Id = 502,
            Name = "Story filha",
            TfsType = "Story",
            Parent = feature,
            Level = 2,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        var existingFeature = new ProjectTask
        {
            Id = 601,
            Name = "Feature destino",
            TfsType = "Feature",
            Parent = epicB,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        feature.Children.Add(story);
        epicA.Children.Add(feature);
        epicB.Children.Add(existingFeature);

        var project = new Project { Name = "Teste drag feature", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(epicA);
        project.Tasks.Add(epicB);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        var sourceVm = vm.FlatTasks.First(t => t.Id == feature.Id);
        var targetVm = vm.FlatTasks.First(t => t.Id == epicB.Id);

        if (!vm.MoveTaskByDrop(sourceVm, targetVm, insertAfter: true))
            throw new InvalidOperationException($"Arrasto deve aceitar Feature para outro Epic. Status: {vm.StatusMessage}");

        AssertEqual(epicB.Id, feature.Parent?.Id ?? -1, "Feature arrastada deve trocar para o Epic destino.");
        AssertEqual(0, epicA.Children.Count, "Epic antigo deve perder a Feature.");
        AssertEqual(2, epicB.Children.Count, "Epic destino deve receber a Feature.");
        AssertEqual(1, feature.Level, "Feature movida deve manter nivel abaixo do Epic.");
        AssertEqual(2, story.Level, "Filhos da Feature movida devem acompanhar o novo nivel.");
        if (!project.IsDirty)
            throw new InvalidOperationException("Mover Feature entre Epics deve marcar o projeto como alterado.");
    }

    private static void DragDropMovesStoryToAnotherFeature()
    {
        var featureA = new ProjectTask
        {
            Id = 100,
            Name = "Feature A",
            TfsType = "Feature",
            Level = 0,
            IsSummary = true
        };
        var featureB = new ProjectTask
        {
            Id = 200,
            Name = "Feature B",
            TfsType = "Feature",
            Level = 0,
            IsSummary = true
        };
        var story = new ProjectTask
        {
            Id = 101,
            Name = "Story movida",
            TfsType = "Story",
            Parent = featureA,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        var existingStory = new ProjectTask
        {
            Id = 201,
            Name = "Story destino",
            TfsType = "Story",
            Parent = featureB,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        featureA.Children.Add(story);
        featureB.Children.Add(existingStory);

        var project = new Project { Name = "Teste drag", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(featureA);
        project.Tasks.Add(featureB);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        var sourceVm = vm.FlatTasks.First(t => t.Id == story.Id);
        var targetVm = vm.FlatTasks.First(t => t.Id == featureB.Id);

        if (!vm.MoveTaskByDrop(sourceVm, targetVm, insertAfter: true))
            throw new InvalidOperationException($"Arrasto deve aceitar Story para outra Feature. Status: {vm.StatusMessage}");

        AssertEqual(featureB.Id, story.Parent?.Id ?? -1, "Story arrastada deve trocar para a Feature destino.");
        AssertEqual(0, featureA.Children.Count, "Feature antiga deve perder a Story.");
        AssertEqual(2, featureB.Children.Count, "Feature destino deve receber a Story.");
        AssertEqual(1, story.Level, "Story movida deve manter nivel abaixo da Feature.");
        if (!project.IsDirty)
            throw new InvalidOperationException("Mover Story entre Features deve marcar o projeto como alterado.");
    }

    private static void DragDropMovesTaskToAnotherStory()
    {
        var storyA = new ProjectTask
        {
            Id = 300,
            Name = "Story A",
            TfsType = "Story",
            Level = 0,
            IsSummary = true
        };
        var storyB = new ProjectTask
        {
            Id = 400,
            Name = "Story B",
            TfsType = "Story",
            Level = 0,
            IsSummary = true
        };
        var task = new ProjectTask
        {
            Id = 301,
            Name = "Task movida",
            TfsType = "Task",
            Parent = storyA,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        var existingTask = new ProjectTask
        {
            Id = 401,
            Name = "Task destino",
            TfsType = "Task",
            Parent = storyB,
            Level = 1,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 7),
            EstimatedHours = 8
        };
        storyA.Children.Add(task);
        storyB.Children.Add(existingTask);

        var project = new Project { Name = "Teste drag task", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(storyA);
        project.Tasks.Add(storyB);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();
        var sourceVm = vm.FlatTasks.First(t => t.Id == task.Id);
        var targetVm = vm.FlatTasks.First(t => t.Id == storyB.Id);

        if (!vm.MoveTaskByDrop(sourceVm, targetVm, insertAfter: true))
            throw new InvalidOperationException($"Arrasto deve aceitar Task para outra Story. Status: {vm.StatusMessage}");

        AssertEqual(storyB.Id, task.Parent?.Id ?? -1, "Task arrastada deve trocar para a Story destino.");
        AssertEqual(0, storyA.Children.Count, "Story antiga deve perder a Task.");
        AssertEqual(2, storyB.Children.Count, "Story destino deve receber a Task.");
        AssertEqual(1, task.Level, "Task movida deve manter nivel abaixo da Story.");
        if (!project.IsDirty)
            throw new InvalidOperationException("Mover Task entre Stories deve marcar o projeto como alterado.");
    }

    private static void DragDropMovesHierarchyItemsToSiblingInAnotherParent()
    {
        var epicA = new ProjectTask { Id = 700, Name = "Epic A", TfsType = "Epic", Level = 0, IsSummary = true };
        var epicB = new ProjectTask { Id = 800, Name = "Epic B", TfsType = "Epic", Level = 0, IsSummary = true };
        var featureA = new ProjectTask { Id = 701, Name = "Feature A", TfsType = "Feature", Parent = epicA, Level = 1, IsSummary = true };
        var featureB1 = new ProjectTask { Id = 801, Name = "Feature B1", TfsType = "Feature", Parent = epicB, Level = 1 };
        var featureB2 = new ProjectTask { Id = 802, Name = "Feature B2", TfsType = "Feature", Parent = epicB, Level = 1 };
        var storyA = new ProjectTask { Id = 702, Name = "Story A", TfsType = "Story", Parent = featureA, Level = 2, IsSummary = true };
        var storyB1 = new ProjectTask { Id = 803, Name = "Story B1", TfsType = "Story", Parent = featureB1, Level = 2 };
        var storyB2 = new ProjectTask { Id = 804, Name = "Story B2", TfsType = "Story", Parent = featureB1, Level = 2 };
        var taskA = new ProjectTask { Id = 703, Name = "Task A", TfsType = "Task", Parent = storyA, Level = 3, Priority = 2 };
        var taskB1 = new ProjectTask { Id = 805, Name = "Task B1", TfsType = "Task", Parent = storyB1, Level = 3, Priority = 1 };
        var taskB2 = new ProjectTask { Id = 806, Name = "Task B2", TfsType = "Task", Parent = storyB1, Level = 3, Priority = 3 };

        storyA.Children.Add(taskA);
        featureA.Children.Add(storyA);
        epicA.Children.Add(featureA);
        storyB1.Children.Add(taskB1);
        storyB1.Children.Add(taskB2);
        featureB1.Children.Add(storyB1);
        featureB1.Children.Add(storyB2);
        epicB.Children.Add(featureB1);
        epicB.Children.Add(featureB2);

        var project = new Project { Name = "Teste drag irmao", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(epicA);
        project.Tasks.Add(epicB);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();

        MoveByDrop(vm, featureA.Id, featureB1.Id, insertAfter: true, "Feature deve poder ser solta apos Feature irma no Epic destino.");
        AssertEqual(epicB.Id, featureA.Parent?.Id ?? -1, "Feature deve trocar para o Epic da Feature alvo.");
        AssertEqual(featureA.Id, epicB.Children[1].Id, "Feature deve entrar logo apos a Feature alvo.");

        MoveByDrop(vm, storyA.Id, storyB1.Id, insertAfter: false, "Story deve poder ser solta antes de Story irma na Feature destino.");
        AssertEqual(featureB1.Id, storyA.Parent?.Id ?? -1, "Story deve trocar para a Feature da Story alvo.");
        AssertEqual(storyA.Id, featureB1.Children[0].Id, "Story deve entrar antes da Story alvo.");

        MoveByDrop(vm, taskA.Id, taskB1.Id, insertAfter: true, "Task deve poder ser solta apos Task irma na Story destino.");
        AssertEqual(storyB1.Id, taskA.Parent?.Id ?? -1, "Task deve trocar para a Story da Task alvo.");
        AssertEqual(taskA.Id, storyB1.Children[1].Id, "Task deve entrar logo apos a Task alvo.");
        AssertEqual(1, featureA.Level, "Feature movida deve manter nivel abaixo do Epic.");
        AssertEqual(2, storyA.Level, "Story movida deve manter nivel abaixo da Feature.");
        AssertEqual(3, taskA.Level, "Task movida deve manter nivel abaixo da Story.");
    }

    private static void DragDropBlocksInvalidHierarchyMoves()
    {
        var epic = new ProjectTask { Id = 900, Name = "Epic", TfsType = "Epic", Level = 0, IsSummary = true };
        var feature = new ProjectTask { Id = 901, Name = "Feature", TfsType = "Feature", Parent = epic, Level = 1, IsSummary = true };
        var story = new ProjectTask { Id = 902, Name = "Story", TfsType = "Story", Parent = feature, Level = 2, IsSummary = true };
        var task = new ProjectTask { Id = 903, Name = "Task", TfsType = "Task", Parent = story, Level = 3 };
        var otherEpic = new ProjectTask { Id = 910, Name = "Outro Epic", TfsType = "Epic", Level = 0, IsSummary = true };
        var otherFeature = new ProjectTask { Id = 911, Name = "Outra Feature", TfsType = "Feature", Parent = otherEpic, Level = 1 };
        var otherStory = new ProjectTask { Id = 912, Name = "Outra Story", TfsType = "Story", Parent = otherFeature, Level = 2 };

        story.Children.Add(task);
        feature.Children.Add(story);
        epic.Children.Add(feature);
        otherFeature.Children.Add(otherStory);
        otherEpic.Children.Add(otherFeature);

        var project = new Project { Name = "Teste drag invalido", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(epic);
        project.Tasks.Add(otherEpic);

        var vm = new MainViewModel("NXTestUnit") { Project = project };
        vm.RebuildFlatTasks();

        AssertBlockedDrop(vm, task.Id, otherFeature.Id, "Task nao deve ser movida diretamente para Feature.");
        AssertBlockedDrop(vm, story.Id, otherEpic.Id, "Story nao deve ser movida diretamente para Epic.");
        AssertBlockedDrop(vm, feature.Id, otherStory.Id, "Feature nao deve ser movida diretamente para Story.");
        AssertEqual(story.Id, task.Parent?.Id ?? -1, "Task bloqueada deve continuar na Story original.");
        AssertEqual(feature.Id, story.Parent?.Id ?? -1, "Story bloqueada deve continuar na Feature original.");
        AssertEqual(epic.Id, feature.Parent?.Id ?? -1, "Feature bloqueada deve continuar no Epic original.");
    }

    // Guarda da ORDEM vinda do TFS: irmãos entram no cronograma na ordem do backlog
    // (StackRank/BacklogPriority) e quem não tem rank NÃO pode ser jogado para o fim.
    private static void ImportOrdersSiblingsByBacklogRank()
    {
        static string Ids(IEnumerable<int> ids) => string.Join(",", ids);
        static void AssertOrder(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException($"{message} Esperado: {expected}; Atual: {actual}.");
        }

        // Ordem do backlog manda, mesmo que a consulta traga embaralhado.
        AssertOrder("30,10,20",
            Ids(TfsImportService.OrderSiblingsByBacklogRank([(10, 2000), (20, 3000), (30, 1000)])),
            "Irmaos devem entrar na ordem do rank do backlog do DevOps.");

        // Item SEM rank no meio: fica onde estava (depois do irmao ranqueado anterior),
        // e nao no fim do grupo — este era o defeito que tirava a hierarquia de ordem.
        AssertOrder("10,20,30",
            Ids(TfsImportService.OrderSiblingsByBacklogRank([(10, 1000), (20, null), (30, 2000)])),
            "Irmao sem rank deve seguir o ranqueado anterior, nao ir para o fim.");

        // Sem nenhum ranqueado antes: preserva a posicao da consulta.
        AssertOrder("10,20,30",
            Ids(TfsImportService.OrderSiblingsByBacklogRank([(10, null), (20, 1000), (30, 2000)])),
            "Item sem rank antes de qualquer ranqueado deve ficar no comeco.");

        // Grupo inteiro sem rank: ordem da consulta preservada (nada de cair para ID).
        AssertOrder("30,10,20",
            Ids(TfsImportService.OrderSiblingsByBacklogRank([(30, null), (10, null), (20, null)])),
            "Grupo sem rank deve preservar a ordem da consulta de hierarquia.");

        // Ranks iguais: empate mantem a ordem da consulta (ordenacao estavel).
        AssertOrder("20,10",
            Ids(TfsImportService.OrderSiblingsByBacklogRank([(20, 1000), (10, 1000)])),
            "Rank empatado deve manter a ordem da consulta.");
    }

    // ORDEM RECEBIDA (consulta de hierarquia do DevOps) x ORDEM GRAVADA (cronograma):
    // reproduz uma resposta do DevOps com ranks e monta a hierarquia como o import faz,
    // conferindo cada grupo de irmãos. Caso real: item sem rank ia para o fim do grupo.
    private static void ImportedOrderMatchesReceivedOrder()
    {
        // Recebido do DevOps (ordem da consulta) com o rank de cada item.
        var recebidoFeatures = new (int Id, double? Rank)[]
        {
            (101, 1000000000),  // Especificacao
            (102, null),        // Desenvolvimento — SEM rank (campo vazio no work item)
            (103, 1000032622),  // Quantitativo
        };
        var nomes = new Dictionary<int, string>
        {
            [101] = "Especificacao", [102] = "Desenvolvimento", [103] = "Quantitativo",
            [201] = "Ingestao", [202] = "Implementacao", [203] = "Homologacao",
        };
        string Gravada(IEnumerable<(int Id, double? Rank)> recebido) => string.Join(" | ",
            TfsImportService.OrderSiblingsByBacklogRank(recebido.ToList()).Select(id => nomes[id]));

        AssertOrderEquals("Especificacao | Desenvolvimento | Quantitativo", Gravada(recebidoFeatures),
            "A ordem gravada deve ser a ordem RECEBIDA do DevOps — item sem rank nao pode ir para o fim.");

        // Consulta fora de ordem: aí sim o rank manda (é a ordem do backlog).
        var foraDeOrdem = new (int Id, double? Rank)[] { (203, 3000), (201, 1000), (202, 2000) };
        AssertOrderEquals("Ingestao | Implementacao | Homologacao", Gravada(foraDeOrdem),
            "Com todos ranqueados, a ordem gravada segue o rank do backlog.");

        // Todos sem rank: a ordem recebida é preservada integralmente.
        var semRank = new (int Id, double? Rank)[] { (203, null), (201, null), (202, null) };
        AssertOrderEquals("Homologacao | Ingestao | Implementacao", Gravada(semRank),
            "Grupo inteiro sem rank deve preservar a ordem recebida na consulta.");
    }

    // Import: item que veio do DevOps SEM StackRank tem o rank CALCULADO pela posição
    // recebida (ordem estável na tela) e entra na lista de "precisa sincronizar".
    private static void ImportFillsMissingBacklogRank()
    {
        // O EPIC ja vem ranqueado do DevOps: so a Feature do meio esta sem rank.
        var epic = new ProjectTask { Id = 1, Name = "Epic", TfsType = "Epic", TfsId = 900, TfsStackRank = 285680480, IsSummary = true };
        ProjectTask Feature(int id, string name, double? rank)
        {
            var f = new ProjectTask { Id = id, Name = name, TfsType = "Feature", TfsId = 1000 + id, TfsStackRank = rank, Parent = epic };
            epic.Children.Add(f);
            return f;
        }
        var primeira = Feature(2, "Especificacao", 1000000000);
        var semRank  = Feature(3, "Desenvolvimento", null);   // veio sem rank do DevOps
        var ultima   = Feature(4, "Quantitativo", 1000032622);

        var raiz = new System.Collections.ObjectModel.ObservableCollection<ProjectTask> { epic };
        var calculados = TfsImportService.FillMissingBacklogRanks(raiz);

        AssertEqual(1, calculados.Count, "So o item sem rank deve ser calculado.");
        if (calculados[0] != "Desenvolvimento")
            throw new InvalidOperationException("O item calculado deve ser o que veio sem rank.");
        if (semRank.TfsStackRank is not { } novo)
            throw new InvalidOperationException("Item sem rank deve receber um rank calculado.");
        if (!(novo > primeira.TfsStackRank!.Value && novo < ultima.TfsStackRank!.Value))
            throw new InvalidOperationException(
                $"O rank calculado ({novo}) deve manter a POSICAO recebida, entre {primeira.TfsStackRank} e {ultima.TfsStackRank}.");

        // Rodar de novo não recalcula nada (idempotente) — nada de rank novo a sincronizar.
        AssertEqual(0, TfsImportService.FillMissingBacklogRanks(raiz).Count,
            "Segunda passada nao pode recalcular rank ja preenchido.");

        // Grupo inteiro sem rank: escala criada do zero, preservando a ordem recebida.
        var story = new ProjectTask { Id = 10, Name = "Story", TfsType = "Story", TfsId = 800, TfsStackRank = 500, IsSummary = true };
        var a = new ProjectTask { Id = 11, Name = "A", TfsId = 801, Parent = story };
        var b = new ProjectTask { Id = 12, Name = "B", TfsId = 802, Parent = story };
        story.Children.Add(a); story.Children.Add(b);
        TfsImportService.FillMissingBacklogRanks(new System.Collections.ObjectModel.ObservableCollection<ProjectTask> { story });
        if (!(a.TfsStackRank < b.TfsStackRank))
            throw new InvalidOperationException("Grupo sem nenhum rank deve receber escala crescente na ordem recebida.");
    }

    // A ordem tem que ser gravada no campo que o PROCESSO usa: Agile/CMMI = StackRank,
    // Scrum = BacklogPriority. Gravar campo inexistente faz o PATCH falhar.
    private static void SyncWritesRankOnTheProcessField()
    {
        static System.Text.Json.JsonElement Fields(string json) =>
            System.Text.Json.JsonDocument.Parse(json).RootElement;

        var agile = TfsImportService.BacklogRankFieldsToWrite(
            Fields("""{"Microsoft.VSTS.Common.StackRank": 1000, "System.Title": "x"}"""));
        AssertOrderEquals("Microsoft.VSTS.Common.StackRank", string.Join(",", agile),
            "Work item com StackRank deve gravar em StackRank.");

        var scrum = TfsImportService.BacklogRankFieldsToWrite(
            Fields("""{"Microsoft.VSTS.Common.BacklogPriority": 1000, "System.Title": "x"}"""));
        AssertOrderEquals("Microsoft.VSTS.Common.BacklogPriority", string.Join(",", scrum),
            "Work item so com BacklogPriority (Scrum) deve gravar em BacklogPriority.");

        var ambos = TfsImportService.BacklogRankFieldsToWrite(
            Fields("""{"Microsoft.VSTS.Common.StackRank": 1, "Microsoft.VSTS.Common.BacklogPriority": 2}"""));
        AssertOrderEquals("Microsoft.VSTS.Common.StackRank,Microsoft.VSTS.Common.BacklogPriority",
            string.Join(",", ambos), "Com os dois campos, a ordem vai para ambos.");

        var nenhum = TfsImportService.BacklogRankFieldsToWrite(Fields("""{"System.Title": "x"}"""));
        AssertOrderEquals("Microsoft.VSTS.Common.StackRank", string.Join(",", nenhum),
            "Sem nenhum campo de ordem, usa StackRank como padrao.");

        var criacao = TfsImportService.BacklogRankFieldsToWrite(null);
        AssertOrderEquals("Microsoft.VSTS.Common.StackRank", string.Join(",", criacao),
            "Na criacao (sem campos para inspecionar) usa StackRank.");
    }

    // O Sync avisa quando vai REESCREVER a posição do item no backlog do DevOps.
    private static void SyncWarnsWhenBacklogOrderIsRewritten()
    {
        var report = new TfsImportService.SyncReport();
        report.LogWarning("⚠ Ordem do backlog: 1 item(ns) terao a POSICAO reescrita no DevOps");
        var texto = report.ToString();
        if (!texto.Contains("Ordem do backlog", StringComparison.Ordinal))
            throw new InvalidOperationException("O aviso de reordenacao deve aparecer no relatorio do Sync.");
        if (report.Messages.Count == 0)
            throw new InvalidOperationException("O aviso deve entrar na lista de mensagens (nao-sucesso) do relatorio.");
    }

    private static void AssertOrderEquals(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message} Esperado: {expected}; Atual: {actual}.");
    }

    private static void RebuildPreservesDevOpsHierarchyOrderIgnoringPriority()
    {
        var epic = new ProjectTask { Id = 1000, Name = "Epic", TfsType = "Epic", Level = 0, IsSummary = true };
        var featureFirst = new ProjectTask
        {
            Id = 1001,
            Name = "Feature primeira no DevOps",
            TfsType = "Feature",
            Parent = epic,
            Level = 1,
            Priority = 4,
            TfsStackRank = 2000,
            IsSummary = true
        };
        var featureSecond = new ProjectTask
        {
            Id = 1002,
            Name = "Feature segunda no DevOps",
            TfsType = "Feature",
            Parent = epic,
            Level = 1,
            Priority = 1,
            TfsStackRank = 1000,
            IsSummary = true
        };
        var storyFirst = new ProjectTask
        {
            Id = 1003,
            Name = "Story primeira no DevOps",
            TfsType = "Story",
            Parent = featureFirst,
            Level = 2,
            Priority = 4,
            TfsStackRank = 2000
        };
        var storySecond = new ProjectTask
        {
            Id = 1004,
            Name = "Story segunda no DevOps",
            TfsType = "Story",
            Parent = featureFirst,
            Level = 2,
            Priority = 1,
            TfsStackRank = 1000
        };

        featureFirst.Children.Add(storyFirst);
        featureFirst.Children.Add(storySecond);
        epic.Children.Add(featureFirst);
        epic.Children.Add(featureSecond);

        var project = new Project { Name = "Ordem hierarquia", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(epic);
        var vm = new MainViewModel("NXTestUnit") { Project = project };

        vm.RebuildFlatTasks();

        AssertEqual(featureFirst.Id, epic.Children[0].Id,
            "Feature deve preservar a sequencia importada do DevOps, mesmo com prioridade maior.");
        AssertEqual(featureSecond.Id, epic.Children[1].Id,
            "Feature com prioridade menor nao deve subir se o DevOps deixou depois.");
        AssertEqual(storyFirst.Id, featureFirst.Children[0].Id,
            "Story deve preservar a sequencia importada do DevOps, mesmo com prioridade maior.");
        AssertEqual(storySecond.Id, featureFirst.Children[1].Id,
            "Story com prioridade menor nao deve subir se o DevOps deixou depois.");
    }

    private static void RebuildOrdersTasksByPriorityThenDevOpsRank()
    {
        var story = new ProjectTask
        {
            Id = 1100,
            Name = "Story",
            TfsType = "Story",
            Level = 0,
            IsSummary = true,
            Start = new DateTime(2026, 7, 6),
            Finish = new DateTime(2026, 7, 9),
            OriginalEstimatedHours = 24
        };
        var lowPriorityEarlyRank = new ProjectTask
        {
            Id = 1101,
            Name = "Prioridade 3 rank cedo",
            TfsType = "Task",
            Parent = story,
            Level = 1,
            Priority = 3,
            TfsStackRank = 100,
            OriginalEstimatedHours = 8
        };
        var highPriorityLateRank = new ProjectTask
        {
            Id = 1102,
            Name = "Prioridade 1 rank tarde",
            TfsType = "Task",
            Parent = story,
            Level = 1,
            Priority = 1,
            TfsStackRank = 900,
            OriginalEstimatedHours = 8
        };
        var samePriorityLateRank = new ProjectTask
        {
            Id = 1103,
            Name = "Prioridade 2 rank tarde",
            TfsType = "Task",
            Parent = story,
            Level = 1,
            Priority = 2,
            TfsStackRank = 800,
            OriginalEstimatedHours = 8
        };
        var samePriorityEarlyRank = new ProjectTask
        {
            Id = 1104,
            Name = "Prioridade 2 rank cedo",
            TfsType = "Task",
            Parent = story,
            Level = 1,
            Priority = 2,
            TfsStackRank = 200,
            OriginalEstimatedHours = 8
        };

        story.Children.Add(lowPriorityEarlyRank);
        story.Children.Add(samePriorityLateRank);
        story.Children.Add(samePriorityEarlyRank);
        story.Children.Add(highPriorityLateRank);

        var project = new Project { Name = "Ordem tasks", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(story);
        var vm = new MainViewModel("NXTestUnit") { Project = project };

        vm.RebuildFlatTasks();

        var orderedIds = story.Children.Select(t => t.Id).ToList();
        var expected = new[] { highPriorityLateRank.Id, samePriorityEarlyRank.Id, samePriorityLateRank.Id, lowPriorityEarlyRank.Id };
        if (!orderedIds.SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                "Tasks devem ordenar por Prioridade e, na mesma Prioridade, pela sequencia/rank do DevOps. " +
                $"Esperado: {string.Join(", ", expected)}; Atual: {string.Join(", ", orderedIds)}.");
        }
    }

    private static void MoveByDrop(MainViewModel vm, int sourceId, int targetId, bool insertAfter, string message)
    {
        var sourceVm = vm.FlatTasks.First(t => t.Id == sourceId);
        var targetVm = vm.FlatTasks.First(t => t.Id == targetId);
        if (!vm.MoveTaskByDrop(sourceVm, targetVm, insertAfter))
            throw new InvalidOperationException($"{message} Status: {vm.StatusMessage}");
    }

    private static void AssertBlockedDrop(MainViewModel vm, int sourceId, int targetId, string message)
    {
        var sourceVm = vm.FlatTasks.First(t => t.Id == sourceId);
        var targetVm = vm.FlatTasks.First(t => t.Id == targetId);
        if (vm.MoveTaskByDrop(sourceVm, targetVm, insertAfter: true))
            throw new InvalidOperationException(message);
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

    private static void TfsSyncTaskCreateOpsIncludesDescription()
    {
        var story = new ProjectTask { Id = 1, TfsId = 1000, TfsType = "User Story", Name = "Story pai" };
        var task = new ProjectTask
        {
            Id = 2,
            Name = "Task com descricao",
            TfsType = "Task",
            TfsId = 0,
            Parent = story,
            Description = "<p>Descricao vinda do Task Plan</p>",
            EstimatedHours = 8
        };
        story.Children.Add(task);

        var ops = TfsImportService.BuildCreateOpsForTests(
            task,
            parentId: story.TfsId!.Value,
            tasksById: new Dictionary<int, ProjectTask> { [story.Id] = story, [task.Id] = task },
            syncPredecessorLinks: false);
        var json = JsonSerializer.Serialize(ops);

        if (!json.Contains("System.Description", StringComparison.OrdinalIgnoreCase) ||
            !json.Contains("Descricao vinda do Task Plan", StringComparison.Ordinal))
            throw new InvalidOperationException("Task deve criar System.Description no DevOps, igual ao padrão de Story.");
    }

    private static void TfsImportTaskStateDefinesDefaultPercent()
    {
        AssertEqual(0, TfsImportService.PercentCompleteFromState("New"), "Task New importada deve ficar 0%.");
        AssertEqual(0, TfsImportService.PercentCompleteFromState("New", completedHours: 8, estimatedHours: 8),
            "Task New deve ficar 0% mesmo se o DevOps trouxer CompletedWork preenchido.");
        AssertEqual(100, TfsImportService.PercentCompleteFromState("Closed"), "Task Closed importada deve ficar 100%.");
        AssertEqual(10, TfsImportService.PercentCompleteFromState("Active"), "Task Active importada sem horas deve iniciar com 10%.");
        AssertEqual(10, TfsImportService.PercentCompleteFromState("Actived"), "Task Actived importada sem horas deve iniciar com 10%.");
        AssertEqual(50, TfsImportService.PercentCompleteFromState("Active", completedHours: 4, estimatedHours: 8),
            "Quando houver CompletedWork calculável, o percentual importado deve respeitar HH realizado/estimado.");
    }

    // Trabalho iniciado ou encerrado tem data REAL: replanejar pela fila jogaria para o futuro
    // algo que já aconteceu, abrindo buraco (gap) no período em que foi de fato executado.
    private static void ImportClosedStoryKeepsExplicitStart()
    {
        var real = new DateTime(2026, 7, 14);
        var fila = new DateTime(2026, 7, 20);

        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, false, "Closed"),
            "Story Closed com Data_Inicio deve manter a data real, nao a posicao da fila.");
        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, false, "Closed", percentComplete: 100),
            "Story Closed a 100% NAO pode ter o inicio empurrado pela fila.");
        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, false, "Done"),
            "Estado concluido equivalente (Done) tambem mantem a data real.");
        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, false, "Active"),
            "Story em andamento mantem a data real de inicio.");
        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, false, "New", percentComplete: 20),
            "Story com % de conclusao > 0 ja comecou: mantem a data real.");
        AssertEqual(real, TfsImportService.ResolveImportStart(real, fila, true, "New"),
            "Data fixada pelo usuario (tag) manda em qualquer estado.");

        AssertEqual(fila, TfsImportService.ResolveImportStart(real, fila, false, "New"),
            "Story nao iniciada (New, 0%) continua sendo planejada pela fila (pessoa/sprint).");
        AssertEqual(fila, TfsImportService.ResolveImportStart(null, fila, false, "Closed"),
            "Sem Data_Inicio no DevOps, mesmo encerrada, a Story cai na fila.");
    }

    private static void ScheduleStateEditUpdatesTaskPercent()
    {
        var task = new ProjectTask
        {
            Id = 91,
            Name = "Task editada",
            TfsType = "Task",
            PercentComplete = 0
        };
        var vm = new TaskViewModel(task);

        vm.TfsState = "Active";
        AssertEqual(10, task.PercentComplete, "Editar estado para Active no cronograma deve ajustar para 10%.");

        vm.TfsState = "Closed";
        AssertEqual(100, task.PercentComplete, "Editar estado para Closed no cronograma deve ajustar para 100%.");

        task.TfsState = "Active";
        task.PercentComplete = 25;
        vm.PercentComplete = 100;
        if (!string.Equals(task.TfsState, "Closed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Digitar 100% em Task no cronograma deve mudar o estado para Closed.");

        task.TfsState = "Active";
        task.PercentComplete = 100;
        vm.PercentComplete = 100;
        if (!string.Equals(task.TfsState, "Closed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Confirmar 100% em Task ja concluida deve corrigir o estado para Closed.");
    }

    private static void TfsSyncNewHierarchyUsesImmediateDevOpsParent()
    {
        var epic = new ProjectTask { Id = 1, Name = "Epic existente", TfsType = "Epic", TfsId = 1000 };
        var feature = new ProjectTask { Id = 2, Name = "Feature nova", TfsType = "Feature", TfsId = 0, Parent = epic };
        var story = new ProjectTask { Id = 3, Name = "Story nova", TfsType = "User Story", TfsId = 0, Parent = feature };
        var task = new ProjectTask { Id = 4, Name = "Task nova", TfsType = "Task", TfsId = 0, Parent = story, EstimatedHours = 8 };
        epic.Children.Add(feature);
        feature.Children.Add(story);
        story.Children.Add(task);

        var tasksById = new Dictionary<int, ProjectTask>
        {
            [epic.Id] = epic,
            [feature.Id] = feature,
            [story.Id] = story,
            [task.Id] = task
        };

        AssertEqual(1000, TfsImportService.ResolveDesiredParentForTests(feature, 999),
            "Feature nova deve ser criada sob o Epic existente, nao no Project raiz.");
        AssertCreateParent(feature, 1000, tasksById, "Feature nova deve apontar para o Epic.");

        feature.TfsId = 2000;
        AssertEqual(2000, TfsImportService.ResolveDesiredParentForTests(story, 999),
            "Story nova deve ser criada sob a Feature recem-criada.");
        AssertCreateParent(story, 2000, tasksById, "Story nova deve apontar para a Feature.");

        story.TfsId = 3000;
        AssertEqual(3000, TfsImportService.ResolveDesiredParentForTests(task, 999),
            "Task nova deve ser criada sob a Story recem-criada ou existente.");
        AssertCreateParent(task, 3000, tasksById, "Task nova deve apontar para a Story.");
    }

    private static void TfsSyncOrphanTaskDoesNotUseRootProject()
    {
        var task = new ProjectTask { Id = 10, Name = "Task sem pai", TfsType = "Task", TfsId = 0 };
        var story = new ProjectTask { Id = 11, Name = "Story sem pai", TfsType = "User Story", TfsId = 0 };
        var feature = new ProjectTask { Id = 12, Name = "Feature sem pai", TfsType = "Feature", TfsId = 0 };
        var epic = new ProjectTask { Id = 13, Name = "Epic sem pai", TfsType = "Epic", TfsId = 0 };

        AssertEqual(0, TfsImportService.ResolveDesiredParentForTests(task, 999),
            "Task sem Parent local deve ser pulada, nunca criada no Work Item Project raiz.");
        AssertEqual(0, TfsImportService.ResolveDesiredParentForTests(story, 999),
            "Story sem Parent local deve ser pulada, nunca criada no Work Item Project raiz.");
        AssertEqual(0, TfsImportService.ResolveDesiredParentForTests(feature, 999),
            "Feature sem Parent local deve ser pulada, nunca criada no Work Item Project raiz.");
        AssertEqual(999, TfsImportService.ResolveDesiredParentForTests(epic, 999),
            "Apenas Epic de topo pode ser criada sob o Work Item Project raiz.");
    }

    private static void AssertCreateParent(
        ProjectTask task,
        int expectedParentId,
        Dictionary<int, ProjectTask> tasksById,
        string message)
    {
        var ops = TfsImportService.BuildCreateOpsForTests(task, expectedParentId, tasksById, syncPredecessorLinks: false);
        var json = JsonSerializer.Serialize(ops);
        if (!json.Contains("System.LinkTypes.Hierarchy-Reverse", StringComparison.OrdinalIgnoreCase) ||
            !json.Contains($"/workItems/{expectedParentId}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message);
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

    private static void ExternalPredecessorWarnsWithoutImportError()
    {
        var parent = new ProjectTask { Id = 200, TfsId = 2000, TfsType = "Feature", Name = "Feature" };
        var marco = new ProjectTask
        {
            Id = 201,
            TfsId = 2010,
            TfsType = "Marco-Devops",
            Name = "Marco",
            Parent = parent,
            IsMilestone = true
        };
        parent.Children.Add(marco);

        var roots = new System.Collections.ObjectModel.ObservableCollection<ProjectTask> { parent };
        var external = TfsImportService.ApplyTfsPredecessorsForTests(
            roots,
            new List<(int predecessor, int successor)> { (1013393, 2010) });

        AssertEqual(1, external.Count, "Predecessora externa deve ser reportada como aviso PRED EXTERNA.");
        AssertEqual(1013393, marco.PredecessorIds.Single(), "Marco-Devops deve manter o TfsId externo da predecessora.");

        var previous = new ProjectTask
        {
            Id = 202,
            TfsId = 2020,
            TfsType = "Story",
            Name = "Irmao anterior",
            Parent = parent
        };
        var anchoredMarco = new ProjectTask
        {
            Id = 203,
            TfsId = 2030,
            TfsType = "Marco-Devops",
            Name = "Marco ancorado",
            Parent = parent,
            IsMilestone = true
        };
        parent.Children.Clear();
        parent.Children.Add(previous);
        parent.Children.Add(anchoredMarco);

        external = TfsImportService.ApplyTfsPredecessorsForTests(
            roots,
            new List<(int predecessor, int successor)> { (1013393, 2020) });

        AssertEqual(1, external.Count, "Predecessora externa no irmao anterior do Marco tambem deve ser reportada como aviso.");
        AssertEqual(1013393, previous.PredecessorIds.Single(), "Irmao anterior do Marco deve manter o TfsId externo da predecessora.");

        var report = new TfsImportService.ImportReport();
        report.LogWarning("[PRED EXTERNA] #1013393 \"Inteligência de Riscos - Fases 2 e 3\" fora de escopo (type=Ideia, state=L2 - Planejada).");
        if (report.HasIssues)
            throw new InvalidOperationException("Aviso de predecessora externa nao deve fazer o import ser tratado como erro.");
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

    private static void TaskAllocationSummaryFromDevOps()
    {
        var tasks = new List<TfsImportService.DevOpsTaskInfo>
        {
            // Closed → usa Completed (5), ignora Estimate (8).
            new() { TfsId = 1, State = "Closed", EstimatedHours = 8, CompletedHours = 5, AssignedToDisplay = "Maria" },
            // Aberta → usa Estimate (3).
            new() { TfsId = 2, State = "Active", EstimatedHours = 3, CompletedHours = 1, AssignedToDisplay = "Joao" },
            // Mesma pessoa acumula (Maria +2 estimate aberta).
            new() { TfsId = 3, State = "New", EstimatedHours = 2, CompletedHours = 0, AssignedToDisplay = "Maria" },
            // Sem responsável → ignorada.
            new() { TfsId = 4, State = "Active", EstimatedHours = 4, CompletedHours = 0, AssignedToDisplay = "" },
            // Zero horas → ignorada.
            new() { TfsId = 5, State = "Active", EstimatedHours = 0, CompletedHours = 0, AssignedToDisplay = "Ana" },
        };

        var summary = TfsImportService.BuildTaskAllocationSummary(tasks);
        double HoursOf(string r) => summary.Where(a => a.Resource == r).Sum(a => a.Hours);

        if (Math.Abs(HoursOf("Maria") - 7) > 0.0001)   // 5 (closed) + 2 (aberta)
            throw new InvalidOperationException($"Maria deveria ter 7h, veio {HoursOf("Maria")}.");
        if (Math.Abs(HoursOf("Joao") - 3) > 0.0001)
            throw new InvalidOperationException($"Joao deveria ter 3h, veio {HoursOf("Joao")}.");
        if (summary.Any(a => a.Resource == "Ana"))
            throw new InvalidOperationException("Ana (0h) não deveria entrar no resumo.");
        // Agrupa por recurso + estado: Maria vira 2 entradas (Closed 5h + New 2h), Joao 1 (Active).
        if (summary.Count != 3)
            throw new InvalidOperationException($"Resumo deveria ter 3 entradas (recurso+estado), veio {summary.Count}.");
        int TasksOf(string r) => summary.Where(a => a.Resource == r).Sum(a => a.Tasks);
        if (TasksOf("Maria") != 2)   // 2 tasks (1 closed + 1 aberta) contam para Maria
            throw new InvalidOperationException($"Maria deveria ter 2 tasks, veio {TasksOf("Maria")}.");
        if (TasksOf("Joao") != 1)
            throw new InvalidOperationException($"Joao deveria ter 1 task, veio {TasksOf("Joao")}.");
        double MariaState(string s) => summary.Where(a => a.Resource == "Maria" && a.State == s).Sum(a => a.Hours);
        if (Math.Abs(MariaState("Closed") - 5) > 0.0001 || Math.Abs(MariaState("New") - 2) > 0.0001)
            throw new InvalidOperationException("Estado das tasks de Maria (Closed=5, New=2) não bateu.");
        if (summary.First(a => a.Resource == "Joao").State != "Active")
            throw new InvalidOperationException("Joao deveria estar como Active.");
    }

    private static void TaskAllocationSummaryRoundTrips()
    {
        var project = new Project { Name = "Alloc", StartDate = new DateTime(2026, 6, 1) };
        var story = new ProjectTask { Id = 10, TfsId = 1015217, TfsType = "User Story", Name = "Story A" };
        story.TaskAllocations.Add(new TaskAllocationSummary { Resource = "Maria", Hours = 7, Tasks = 2, State = "Closed" });
        story.TaskAllocations.Add(new TaskAllocationSummary { Resource = "Joao", Hours = 3, Tasks = 1, State = "Active" });
        project.Tasks.Add(story);

        var path = Path.Combine(Path.GetTempPath(), $"nx-alloc-{Guid.NewGuid():N}.nxp");
        try
        {
            XmlProjectService.Save(project, path);
            var loaded = XmlProjectService.Load(path);
            var s = loaded.Tasks.First(t => t.TfsId == 1015217);
            if (s.TaskAllocations.Count != 2)
                throw new InvalidOperationException($"Esperados 2 resumos, veio {s.TaskAllocations.Count}.");
            var maria = s.TaskAllocations.FirstOrDefault(a => a.Resource == "Maria");
            if (maria == null || Math.Abs(maria.Hours - 7) > 0.0001)
                throw new InvalidOperationException("Resumo de Maria (7h) não sobreviveu ao salvar/abrir.");
            if (maria.Tasks != 2)
                throw new InvalidOperationException($"Qtd de tasks de Maria (2) não sobreviveu ao salvar/abrir, veio {maria.Tasks}.");
            if (maria.State != "Closed")
                throw new InvalidOperationException($"Estado de Maria (Closed) não sobreviveu ao salvar/abrir, veio '{maria.State}'.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void AllocationStoryDecompositionFactors()
    {
        static void Near(double actual, double expected, string what)
        {
            if (Math.Abs(actual - expected) > 0.0001)
                throw new InvalidOperationException($"{what}: esperado {expected:0.####}, veio {actual:0.####}.");
        }

        // Story 40h, tasks somam 22h (cabem): responsavel fica com 18/40; tasks inteiras.
        Near(NXProject.Services.TaskScheduleService.StoryResponsibleFactor(40, 22), 18.0 / 40.0, "restante responsavel (cabe)");
        Near(NXProject.Services.TaskScheduleService.StoryTaskCutFactor(40, 22), 1.0, "corte task (cabe)");

        // Tasks somam 50h (estouram 40): responsavel 0; tasks cortadas por 40/50.
        Near(NXProject.Services.TaskScheduleService.StoryResponsibleFactor(40, 50), 0.0, "restante responsavel (estoura)");
        Near(NXProject.Services.TaskScheduleService.StoryTaskCutFactor(40, 50), 40.0 / 50.0, "corte task (estoura)");

        // Sem tasks: responsavel fica com a Story inteira.
        Near(NXProject.Services.TaskScheduleService.StoryResponsibleFactor(40, 0), 1.0, "restante sem tasks");

        // Uma unica task maior que a Story: cortada para caber (trava).
        Near(NXProject.Services.TaskScheduleService.StoryTaskCutFactor(40, 60), 40.0 / 60.0, "trava task > story");

        // Story sem estimativa (0): nao capa nada.
        Near(NXProject.Services.TaskScheduleService.StoryResponsibleFactor(0, 10), 1.0, "story sem estimativa (resp)");
        Near(NXProject.Services.TaskScheduleService.StoryTaskCutFactor(0, 10), 1.0, "story sem estimativa (task)");

        // Total fecha: responsavel + tasks(cortadas) = HH da Story.
        double storyHours = 40, taskSum = 50;
        double respHours = storyHours * NXProject.Services.TaskScheduleService.StoryResponsibleFactor(storyHours, taskSum);
        double tasksHours = taskSum * NXProject.Services.TaskScheduleService.StoryTaskCutFactor(storyHours, taskSum);
        Near(respHours + tasksHours, storyHours, "total = HH da Story (estoura)");

        storyHours = 40; taskSum = 22;
        respHours = storyHours * NXProject.Services.TaskScheduleService.StoryResponsibleFactor(storyHours, taskSum);
        tasksHours = taskSum * NXProject.Services.TaskScheduleService.StoryTaskCutFactor(storyHours, taskSum);
        Near(respHours + tasksHours, storyHours, "total = HH da Story (cabe)");
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

    // Caso real (A2026 - Inteligência de Riscos): Story em 100% com HH Restante lançado
    // deixava a Feature em 94% (43,2 de 48h). O restante deve ser ABSORVIDO pelo HH Atual
    // (antecipação/esforço extra) sem mudar a duração total.
    private static void CompletedTaskAbsorbsRemainingHours()
    {
        SetCurrentCalendar(new ProjectCalendar());
        var feature = new ProjectTask
        {
            Id = 1, Name = "Ajuste Power BI BSC Fornecedor - Desenvolvimento",
            TfsType = "Feature", IsSummary = true,
            Start = new DateTime(2026, 7, 6), Finish = new DateTime(2026, 7, 10)
        };
        ProjectTask Story(int id, string name, double cur, double est, double pct)
        {
            var s = new ProjectTask
            {
                Id = id, Name = name, TfsType = "User Story", Parent = feature,
                Start = new DateTime(2026, 7, 6), Finish = new DateTime(2026, 7, 10),
                CurrentHours = cur, EstimatedHours = est, OriginalEstimatedHours = cur + est,
                PercentComplete = pct
            };
            feature.Children.Add(s);
            return s;
        }
        Story(2, "Implementar Ingestao", 24, 0, 100);
        var early = Story(3, "Realizar implementacao Power BI", 43.2, 4.8, 100); // 100% com restante
        Story(4, "Realizar Homologacao", 8, 0, 100);
        Story(5, "Atualizar Documentacao", 8, 0, 100);

        var project = new Project { Name = "Riscos", StartDate = new DateTime(2026, 7, 6) };
        project.Tasks.Add(feature);
        var vm = new MainViewModel("NXTestUnit") { Project = project };

        // Invariante: duração TOTAL (HH Atual + HH Restante) não pode mudar com a absorção —
        // só a repartição entre os dois campos muda.
        var durationBefore = TaskScheduleService.GetEffectiveTotalDurationHours(early);
        vm.RebuildFlatTasks();

        AssertEqual(48, early.CurrentHours ?? -1, "HH Restante de atividade 100% deve ser somado ao HH Atual.");
        AssertEqual(0, early.EstimatedHours ?? -1, "HH Restante deve ficar zerado apos a absorcao.");
        AssertEqual(durationBefore, TaskScheduleService.GetEffectiveTotalDurationHours(early),
            "A absorcao NAO pode mudar a duracao total da atividade.");
        AssertEqual(100, feature.PercentComplete, "Feature com todas as filhas 100% deve fechar em 100%.");

        // Atividade em andamento (< 100%) continua com o restante intacto.
        var running = Story(6, "Em andamento", 5, 5, 50);
        vm.RebuildFlatTasks();
        AssertEqual(5, running.CurrentHours ?? -1, "Atividade abaixo de 100% mantem o HH Atual.");
        AssertEqual(5, running.EstimatedHours ?? -1, "Atividade abaixo de 100% mantem o HH Restante.");
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

    // Garante que a estimativa de tempo da IA (relógio de contagem regressiva) realmente
    // persiste em disco, ADAPTA à duração real observada e ESCALA pelo volume de bytes.
    // Regressão do bug "sempre o mesmo tempo e nao muda".
    private static void AiRunStatsPersistsAdaptsAndScales()
    {
        var storageKey = $"NXProject.Test.Stats-{Guid.NewGuid():N}";
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            storageKey, "ai-run-stats.json");
        try
        {
            const string prov = "Codex", act = "chat", sched = "Projeto X";

            if (NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, prov, act, sched, 1000) != null)
                throw new InvalidOperationException("Sem historico deveria retornar null.");

            // 1ª amostra: 40s com 1000 bytes -> estima ~40s para o mesmo tamanho.
            NXProject.Services.AiRunStatsStore.Record(storageKey, prov, act, sched, 40, 1000);
            if (!File.Exists(file))
                throw new InvalidOperationException("O historico NAO foi gravado em disco.");
            var first = NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, prov, act, sched, 1000)
                        ?? throw new InvalidOperationException("Historico gravado mas leitura retornou null.");
            AssertEqual(40, first, "Primeira estimativa deve refletir a duracao gravada.", 1);

            // Execucoes reais bem mais rapidas: a estimativa tem de CAIR (adaptar), nao ficar fixa.
            for (int i = 0; i < 5; i++)
                NXProject.Services.AiRunStatsStore.Record(storageKey, prov, act, sched, 5, 1000);
            var adapted = NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, prov, act, sched, 1000)
                          ?? throw new InvalidOperationException("Leitura retornou null apos varias amostras.");
            if (adapted >= first)
                throw new InvalidOperationException($"A estimativa nao adaptou: continuou {adapted}s (era {first}s).");

            // Escala pelo volume: o DOBRO de bytes deve estimar mais tempo que o tamanho base.
            var baseEta = NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, prov, act, sched, 1000)!.Value;
            var bigEta = NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, prov, act, sched, 2000)!.Value;
            if (bigEta <= baseEta)
                throw new InvalidOperationException($"Estimativa nao escalou por bytes: {bigEta}s <= {baseEta}s.");

            // Provedor diferente NAO deve herdar o historico (chave por tipo de IA).
            if (NXProject.Services.AiRunStatsStore.EstimateSeconds(storageKey, "IA Local", act, sched, 1000) != null)
                throw new InvalidOperationException("Outro provedor nao deveria ter historico (chave por IA).");
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            try { var d = Path.GetDirectoryName(file); if (d != null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }

    // Histórico do chat de IA: guarda por CRONOGRAMA (projectKey), respeita o limite N
    // (mais recentes primeiro) e 0 = infinito. Chaves diferentes não se misturam.
    private static void AiChatHistoryPersistsPerProjectWithLimit()
    {
        var storageKey = $"NXProject.Test.Chat-{Guid.NewGuid():N}";
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            storageKey, "ai-chat-history.json");
        try
        {
            StoredConv Make(string title) => new()
            {
                Title = title,
                Messages = { new NXProject.Services.AiChatHistoryStore.StoredMessage { Role = "Usuário", Text = title } }
            };

            if (NXProject.Services.AiChatHistoryStore.Load(storageKey, "TFS-100").Count != 0)
                throw new InvalidOperationException("Projeto sem historico deveria vir vazio.");

            // 3 conversas com limite 2 -> guarda só as 2 primeiras (mais recentes).
            var convs = new[] { Make("c3"), Make("c2"), Make("c1") };
            NXProject.Services.AiChatHistoryStore.Save(storageKey, "TFS-100", convs, limit: 2);
            var back = NXProject.Services.AiChatHistoryStore.Load(storageKey, "TFS-100");
            if (back.Count != 2 || back[0].Title != "c3" || back[1].Title != "c2")
                throw new InvalidOperationException($"Limite 2 falhou: {back.Count} conversas ({string.Join(",", back.Select(c => c.Title))}).");

            // Limite 0 = infinito: guarda todas.
            NXProject.Services.AiChatHistoryStore.Save(storageKey, "TFS-100", convs, limit: 0);
            if (NXProject.Services.AiChatHistoryStore.Load(storageKey, "TFS-100").Count != 3)
                throw new InvalidOperationException("Limite 0 deveria guardar todas as conversas.");

            // Outro cronograma NAO herda o historico (chave por projeto).
            if (NXProject.Services.AiChatHistoryStore.Load(storageKey, "NXProject").Count != 0)
                throw new InvalidOperationException("Outro cronograma nao deveria ter historico.");

            // Conversa vazia nao e guardada.
            NXProject.Services.AiChatHistoryStore.Save(storageKey, "NXProject", new[] { new StoredConv { Title = "vazia" } }, limit: 10);
            if (NXProject.Services.AiChatHistoryStore.Load(storageKey, "NXProject").Count != 0)
                throw new InvalidOperationException("Conversa sem mensagens nao deveria ser gravada.");
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            try { var d = Path.GetDirectoryName(file); if (d != null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
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

    private static void AssertEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message} Esperado: '{expected}'; Atual: '{actual}'.");
    }

    private static void AssertEqual(int expected, int actual, string message)
    {
        if (expected != actual)
            throw new InvalidOperationException($"{message} Esperado: {expected}; Atual: {actual}.");
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

    private static void TaskPlanBackfillIdsFromSyncLog()
    {
        var path = NewTempXlsx();
        try
        {
            var data = NewPlan("Task", "ID Feature", "ID Story", "ID Task");
            var r = data.Table.NewRow();
            r["Task"] = "Tarefa A"; r["ID Feature"] = "5:I"; r["ID Story"] = "9:I"; r["ID Task"] = "115:I";
            data.Table.Rows.Add(r);
            ExcelTaskPlanService.CreateNew(path, data);

            var entries = new List<NXProject.Community.Services.BackfillEntry>
            {
                new() { TaskKey = "115:I", NewTaskId = "1234:T", NewStoryId = "900:T", NewFeatureId = "800:T" }
            };

            // Log lateral: grava, lê de volta e confere o nome do arquivo.
            ExcelTaskPlanService.WritePendingSidecar(path, entries);
            var sidecar = ExcelTaskPlanService.SidecarPath(path);
            if (!System.IO.Path.GetFileName(sidecar).EndsWith("_Sync_NXProject.xml"))
                throw new InvalidOperationException($"Nome do log inesperado: {System.IO.Path.GetFileName(sidecar)}");
            var read = ExcelTaskPlanService.ReadPendingSidecar(path);
            if (read == null || read.Count != 1 || read[0].NewTaskId != "1234:T")
                throw new InvalidOperationException("Log lateral não voltou corretamente.");

            // Aplica direto no .xlsx: linha com ID Task "115:I" vira "1234:T" (idem Story/Feature).
            var n = ExcelTaskPlanService.TryBackfillIds(path, entries);
            if (n != 1) throw new InvalidOperationException($"Esperada 1 linha atualizada, obtidas {n}.");

            var loaded = ExcelTaskPlanService.Load(path);
            var row = loaded.Table.Rows[0];
            if (row["ID Task"]?.ToString() != "1234:T") throw new InvalidOperationException("ID Task não foi atualizado.");
            if (row["ID Story"]?.ToString() != "900:T") throw new InvalidOperationException("ID Story não foi atualizado.");
            if (row["ID Feature"]?.ToString() != "800:T") throw new InvalidOperationException("ID Feature não foi atualizado.");

            ExcelTaskPlanService.DeletePendingSidecar(path);
            if (ExcelTaskPlanService.ReadPendingSidecar(path) != null)
                throw new InvalidOperationException("Log lateral deveria ter sido removido.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            var sc = ExcelTaskPlanService.SidecarPath(path);
            if (File.Exists(sc)) File.Delete(sc);
        }
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

    private static void TaskPlanFilterViewRoundTrips()
    {
        var settings = new TaskPlanSettings();
        var view = new TaskPlanFilterView { Epic = "EPIC A", Feature = "Feature 1", OpenTasksOnly = true };
        view.ColumnFilters["Status"] = new System.Collections.Generic.List<string> { "Active", "New" };
        view.ColorFilters["Task"] = "#FFF2CC";
        settings.SetFilterView(@"C:\Planos\Plano Oficial.xlsx", view);

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<TaskPlanSettings>(json)
            ?? throw new InvalidOperationException("Desserializacao retornou nulo.");

        // A chave deve resolver por caminho normalizado, case-insensitive.
        var got = back.GetFilterView(@"c:\planos\plano oficial.xlsx")
            ?? throw new InvalidOperationException("Visao de filtro nao sobreviveu a serializacao.");
        if (got.Epic != "EPIC A") throw new InvalidOperationException($"EPIC perdido: '{got.Epic}'.");
        if (got.Feature != "Feature 1") throw new InvalidOperationException($"Feature perdida: '{got.Feature}'.");
        if (!got.OpenTasksOnly) throw new InvalidOperationException("OpenTasksOnly perdido.");
        if (!got.ColumnFilters.TryGetValue("Status", out var st) || st.Count != 2)
            throw new InvalidOperationException("Filtro de coluna Status perdido.");
        if (!got.ColorFilters.TryGetValue("Task", out var cor) || cor != "#FFF2CC")
            throw new InvalidOperationException("Filtro de cor perdido.");

        settings.RemoveFilterView(@"C:\PLANOS\PLANO OFICIAL.XLSX");
        if (settings.GetFilterView(@"C:\Planos\Plano Oficial.xlsx") != null)
            throw new InvalidOperationException("RemoveFilterView nao removeu por caminho case-insensitive.");
    }

    private static void TaskPlanBackupBeforeSaveCreatesCopyAndRetains15Days()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nx-taskplan-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Plano Oficial.xlsx");
        try
        {
            var data = NewPlan("Aprovada", "Task", "ID Task");
            data.Table.Rows.Add("Nao", "Tarefa interna", "77:I");
            ExcelTaskPlanService.CreateNew(path, data);

            var backupDir = Path.Combine(dir, "Backup");
            Directory.CreateDirectory(backupDir);
            var oldBackup = Path.Combine(backupDir, "Plano_Oficial_bkp_2026_01_01_10_00_00_merge_user.xlsx");
            File.Copy(path, oldBackup);
            File.SetLastWriteTime(oldBackup, new DateTime(2026, 6, 1, 10, 0, 0));

            var backup = ExcelTaskPlanService.CreateBackupBeforeSave(
                path,
                "merge",
                @"DOMINIO\usuario teste",
                new DateTime(2026, 7, 20, 12, 34, 56));

            if (!File.Exists(backup))
                throw new InvalidOperationException("Backup nao foi criado.");
            var name = Path.GetFileName(backup);
            if (name != "Plano_Oficial_bkp_2026_07_20_12_34_56_merge_DOMINIO_usuario_teste.xlsx")
                throw new InvalidOperationException($"Nome do backup inesperado: {name}");
            if (File.Exists(oldBackup))
                throw new InvalidOperationException("Backup antigo deveria ter sido removido pela retencao de 15 dias.");

            data.Table.Rows[0]["ID Task"] = "123:T";
            ExcelTaskPlanService.Save(path, data);

            var backupData = ExcelTaskPlanService.Load(backup);
            if (backupData.Table.Rows[0]["ID Task"]?.ToString() != "77:I")
                throw new InvalidOperationException("Backup deve preservar o conteudo anterior ao salvar.");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
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

    // Simulação com resposta REAL da IA (modo Feature nova): sufixo "h" no esforço e
    // chaves acentuadas ("esforço", "responsável") — antes viravam 1h e responsável perdido.
    private static void TaskPlanAiResponseAcceptsHourSuffixAndAccentedKeys()
    {
        AssertEqual(4, TaskPlanScheduleRules.ParseEstimatedHours("4h") ?? -1, "'4h' deve virar 4 horas.");
        AssertEqual(3, TaskPlanScheduleRules.ParseEstimatedHours("3 horas") ?? -1, "'3 horas' deve virar 3 horas.");
        AssertEqual(6.5, TaskPlanScheduleRules.ParseEstimatedHours("6,5") ?? -1, "'6,5' segue aceito.");
        AssertEqual(2 * ProjectCalendarService.WorkingHoursPerDay,
            TaskPlanScheduleRules.ParseEstimatedHours("2 dias") ?? -1, "'2 dias' converte pelo expediente.");
        if (TaskPlanScheduleRules.ParseEstimatedHours("horas") != null)
            throw new InvalidOperationException("Unidade sem número não pode virar estimativa.");

        // Trecho literal da resposta da IA registrada em produção (itens 1 e 3 do log).
        const string raw = """
            [{"epic_id":14,"feature":"BI de Torre de Controle – Planos Outbound","story":"User Story 1 – Organizar a base","task":"Corrigir nomes e informações das colunas","responsavel":"Oliveira, Alice De Muylder (Contractor)","esforco":"4h","obs":""},
             {"epic_id":14,"feature":"BI de Torre de Controle – Planos Outbound","story":"User Story 3 – Configurar os filtros","task":"Sincronizar todos os filtros","responsável":"Oliveira, Alice De Muylder (Contractor)","esforço":"2h","obs":""}]
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var items = doc.RootElement.EnumerateArray().ToList();

        var h1 = TaskPlanScheduleRules.ParseEstimatedHours(
            TaskPlanScheduleRules.GetJsonString(items[0], "esforco", "esforço"));
        AssertEqual(4, h1 ?? -1, "Item com chave 'esforco' e sufixo h deve dar 4 horas.");

        var h2 = TaskPlanScheduleRules.ParseEstimatedHours(
            TaskPlanScheduleRules.GetJsonString(items[1], "esforco", "esforço"));
        AssertEqual(2, h2 ?? -1, "Item com chave ACENTUADA 'esforço' deve dar 2 horas (fallback).");

        var resp = TaskPlanScheduleRules.GetJsonString(items[1], "responsavel", "responsável");
        if (resp != "Oliveira, Alice De Muylder (Contractor)")
            throw new InvalidOperationException("Chave acentuada 'responsável' deve ser lida pelo fallback.");

        // Resposta TRUNCADA pelo teto de tokens (final real do log de produção: corta em
        // "...,{"epic_id"): o reparo aproveita os objetos completos em vez de descartar tudo.
        const string truncatedRaw =
            """[{"epic_id":24,"story":"Formatar o painel","task":"Padronizar as cores","esforço":"3"},{"epic_id""";
        var (json, truncated) = TaskPlanScheduleRules.ExtractJsonArray(truncatedRaw);
        if (json == null || !truncated)
            throw new InvalidOperationException("Resposta truncada deve ser reparada (itens completos aproveitados).");
        using var repaired = System.Text.Json.JsonDocument.Parse(json);
        AssertEqual(1, repaired.RootElement.GetArrayLength(), "Reparo deve manter só o objeto completo.");

        var (okJson, okTrunc) = TaskPlanScheduleRules.ExtractJsonArray("""bla [{"task":"x"}] bla""");
        if (okJson == null || okTrunc)
            throw new InvalidOperationException("Array íntegro não pode ser marcado como truncado.");
        if (TaskPlanScheduleRules.ExtractJsonArray("sem json aqui").Json != null)
            throw new InvalidOperationException("Texto sem array não pode devolver JSON.");
    }

    // Casamento do responsável citado com o recurso do cronograma (nomes reais do
    // cadastro "Sobrenome, Nome (Contractor)"): exato, invertido, sem acento, só o
    // primeiro nome e citado no meio de uma frase. Ambiguidade NAO vira palpite.
    private static void TaskPlanResourceMatcherHandlesCitedNames()
    {
        var people = new List<Resource>
        {
            new() { Id = 1, Name = "Domingues, Joao Pedro Araujo", Type = ResourceType.Work },
            new() { Id = 2, Name = "Melo, Carmo C (Contractor)", Type = ResourceType.Work },
            new() { Id = 3, Name = "Fenoci, Mateus Rezende (Contractor)", Type = ResourceType.Work },
            new() { Id = 4, Name = "Monteiro, Emneh Dias (Contractor)", Type = ResourceType.Work },
            new() { Id = 5, Name = "Rodrig, Caio Siquara Nascimento (Contractor)", Type = ResourceType.Work },
            new() { Id = 6, Name = "Oliveira, Alice De Muylder (Contractor)", Type = ResourceType.Work },
        };

        void Match(string cited, int expectedId, string why)
        {
            var r = TaskPlanResourceMatcher.Find(people, cited);
            if (r == null) throw new InvalidOperationException($"\"{cited}\" deveria casar ({why}), mas nao casou.");
            AssertEqual(expectedId, r.Id, $"\"{cited}\" deveria casar com o recurso {expectedId} ({why}).");
        }

        Match("Oliveira, Alice De Muylder (Contractor)", 6, "nome exato do cadastro");
        Match("Oliveira, Alice De Muylder", 6, "sem o sufixo (Contractor)");
        Match("Alice Oliveira", 6, "nome invertido, sem sobrenome do meio");
        Match("alice de muylder oliveira", 6, "minusculas e ordem trocada");
        Match("Alice", 6, "so o primeiro nome, unico no projeto");
        Match("Joao Pedro", 1, "primeiro nome composto");
        Match("João Pedro Domingues", 1, "com acento contra cadastro sem acento");
        Match("Falar com Fenoci, Mateus Rezende (Contractor) sobre a carga", 3, "nome citado dentro da frase");

        if (TaskPlanResourceMatcher.Find(people, "Fulano da Silva") != null)
            throw new InvalidOperationException("Nome de fora do projeto nao pode casar com ninguem.");
        if (TaskPlanResourceMatcher.Find(people, "") != null)
            throw new InvalidOperationException("Nome vazio nao pode casar.");

        // Ambiguidade: dois "Carmo" no projeto — melhor Observacao do que errar a pessoa.
        var ambiguous = new List<Resource>(people)
        {
            new() { Id = 7, Name = "Souza, Carmo T (Contractor)", Type = ResourceType.Work },
        };
        if (TaskPlanResourceMatcher.Find(ambiguous, "Carmo") != null)
            throw new InvalidOperationException("Primeiro nome ambiguo nao pode escolher um recurso no palpite.");
        AssertEqual(2, TaskPlanResourceMatcher.Find(ambiguous, "Carmo Melo")?.Id ?? -1,
            "Nome + sobrenome desempata entre dois recursos de mesmo primeiro nome.");

        // Recurso material (nao-pessoa) nunca casa.
        var material = new List<Resource> { new() { Id = 9, Name = "Licenca Power BI", Type = ResourceType.Material } };
        if (TaskPlanResourceMatcher.Find(material, "Licenca Power BI") != null)
            throw new InvalidOperationException("Recurso Material nao pode virar responsavel.");
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
        if (task.Description != "desc")
            throw new InvalidOperationException("Task Plan deve levar a descrição da Task para o cronograma.");
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

    // ── ID interno duplicado: gravar bloqueia, ler normaliza, sync recusa ────

    private static Project NewProjectWithDuplicateIds()
    {
        var project = new Project { Name = "Dup", StartDate = new DateTime(2026, 7, 6) };
        var epic = new ProjectTask { Id = 1, TfsId = 900, TfsType = "Epic", Name = "Epic A" };
        var s1 = new ProjectTask { Id = 115, TfsId = 901, TfsType = "User Story", Name = "Story 1", Parent = epic };
        var s2 = new ProjectTask { Id = 115, TfsId = 0, TfsType = "User Story", Name = "Story duplicada", Parent = epic };
        epic.Children.Add(s1);
        epic.Children.Add(s2);
        epic.IsSummary = true;
        project.Tasks.Add(epic);
        return project;
    }

    private static void SaveBlocksDuplicateTaskIds()
    {
        var project = NewProjectWithDuplicateIds();
        var path = Path.Combine(Path.GetTempPath(), $"nx-dup-{Guid.NewGuid():N}.nxp");
        try
        {
            XmlProjectService.Save(project, path);
            throw new InvalidOperationException("Save deveria BLOQUEAR projeto com ID duplicado.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MESMO ID"))
        {
            if (!ex.Message.Contains("115") || !ex.Message.Contains("Story duplicada"))
                throw new InvalidOperationException("A mensagem deve dizer o ID (115) e a atividade gravada errada.");
            if (File.Exists(path))
                throw new InvalidOperationException("Nenhum arquivo deveria ter sido gravado.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void LoadNormalizesDuplicateTaskIds()
    {
        // Simula um arquivo LEGADO (gravado antes da trava) com dois IDs 115:
        // grava com IDs válidos e duplica no XML por texto.
        var project = NewProjectWithDuplicateIds();
        var all = new List<ProjectTask> { project.Tasks[0] };
        all.AddRange(project.Tasks[0].Children);
        project.Tasks[0].Children[1].Id = 777;   // deixa válido para gravar

        var path = Path.Combine(Path.GetTempPath(), $"nx-dup-{Guid.NewGuid():N}.nxp");
        try
        {
            XmlProjectService.Save(project, path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(">777<", ">115<"));

            var loaded = XmlProjectService.Load(path);
            var ids = new List<int>();
            void Walk(IEnumerable<ProjectTask> ts) { foreach (var t in ts) { ids.Add(t.Id); Walk(t.Children); } }
            Walk(loaded.Tasks);

            if (ids.Count != 3)
                throw new InvalidOperationException($"Esperadas 3 atividades, obtidas {ids.Count}.");
            if (ids.Distinct().Count() != ids.Count)
                throw new InvalidOperationException("A leitura deve normalizar IDs duplicados (todos distintos).");
            if (!ids.Contains(115))
                throw new InvalidOperationException("O primeiro ID 115 deve ser preservado.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void SyncBlocksDuplicateTaskIds()
    {
        var project = NewProjectWithDuplicateIds();
        try
        {
            TfsImportService.EnsureNoDuplicateTaskIds(project);
            throw new InvalidOperationException("Sync deveria BLOQUEAR projeto com ID duplicado.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Sincronização bloqueada"))
        {
            if (!ex.Message.Contains("115"))
                throw new InvalidOperationException("A mensagem do sync deve citar o ID duplicado.");
        }
    }

    private static void SyncBlocksTaskWithoutStoryParent()
    {
        var epic    = new ProjectTask { Id = 1, TfsId = 900, TfsType = "Epic", Name = "Epic A" };
        var feature = new ProjectTask { Id = 2, TfsId = 901, TfsType = "Feature", Name = "Feature A", Parent = epic };
        var story   = new ProjectTask { Id = 3, TfsId = 0, TfsType = "User Story", Name = "Story ainda sem ID", Parent = feature };
        var task    = new ProjectTask { Id = 4, TfsId = 0, TfsType = "Task", Name = "Task nova", Parent = story };

        // Story pai ainda sem ID DevOps: o ancestral vinculado mais próximo é a Feature —
        // a Task NÃO pode ser criada/reparentada ali.
        var violation = TfsImportService.TaskParentViolation(task);
        if (violation == null || !violation.Contains("Feature"))
            throw new InvalidOperationException($"Task sob Story sem ID deveria acusar o pai Feature (obtido: '{violation}').");

        // Com a Story vinculada, a Task é aceita.
        story.TfsId = 902;
        if (TfsImportService.TaskParentViolation(task) != null)
            throw new InvalidOperationException("Task sob Story vinculada deve ser aceita.");

        // Story/Feature não são afetadas pela regra (só Task).
        if (TfsImportService.TaskParentViolation(story) != null)
            throw new InvalidOperationException("A regra vale apenas para o tipo Task.");
    }

    private static void SyncAllowsDevOpsMilestoneOutsideStory()
    {
        var feature = new ProjectTask { Id = 1, TfsId = 901, TfsType = "Feature", Name = "Feature A" };
        var marcoFeature = new ProjectTask
        {
            Id = 2,
            TfsId = 0,
            TfsType = "Marco-Devops",
            Tags = "MARCO-PROJECT",
            Name = "Marco sob Feature",
            Parent = feature
        };
        feature.Children.Add(marcoFeature);

        if (TfsImportService.TaskParentViolation(marcoFeature) != null)
            throw new InvalidOperationException("Marco-Devops deve poder ficar sob Feature, mesmo sendo criado como Task no DevOps.");
        AssertEqual(901, TfsImportService.ResolveDesiredParentForTests(marcoFeature, 999),
            "Marco-Devops sob Feature deve usar a Feature como pai DevOps.");

        var epic = new ProjectTask { Id = 3, TfsId = 1000, TfsType = "Epic", Name = "Epic comum" };
        var marcoEpic = new ProjectTask
        {
            Id = 4,
            TfsId = 0,
            TfsType = "Marco-DevOps",
            Tags = "MARCO-PROJECT",
            Name = "Marco sob Epic",
            Parent = epic
        };
        epic.Children.Add(marcoEpic);

        if (TfsImportService.TaskParentViolation(marcoEpic) != null)
            throw new InvalidOperationException("Marco-Devops deve poder ficar sob Epic comum quando estiver no nivel de Feature.");
        AssertEqual(1000, TfsImportService.ResolveDesiredParentForTests(marcoEpic, 999),
            "Marco-Devops sob Epic comum deve usar o Epic como pai DevOps.");

        var rootWorkItemProject = new ProjectTask { Id = 5, TfsId = 999, TfsType = "Epic", Name = "Work Item Project" };
        var marcoRoot = new ProjectTask
        {
            Id = 6,
            TfsId = 0,
            TfsType = "Marco-DevOps",
            Tags = "MARCO-PROJECT",
            Name = "Marco no raiz",
            Parent = rootWorkItemProject
        };
        rootWorkItemProject.Children.Add(marcoRoot);

        AssertEqual(0, TfsImportService.ResolveDesiredParentForTests(marcoRoot, 999),
            "Marco-Devops nao pode ser criado direto no Work Item Project raiz.");

        var ideia = new ProjectTask { Id = 7, TfsId = 1013393, TfsType = "Ideia", Name = "Ideia vinculada" };
        var marcoIdeia = new ProjectTask
        {
            Id = 8,
            TfsId = 0,
            TfsType = "Marco-DevOps",
            Tags = "MARCO-PROJECT",
            Name = "Marco sob Ideia",
            Parent = ideia
        };
        ideia.Children.Add(marcoIdeia);

        if (TfsImportService.TaskParentViolation(marcoIdeia) != null)
            throw new InvalidOperationException("Marco-Devops deve poder ficar sob tipo customizado vinculado, como Ideia.");
        AssertEqual(1013393, TfsImportService.ResolveDesiredParentForTests(marcoIdeia, 999),
            "Marco-Devops sob Ideia deve usar a Ideia como pai DevOps.");
    }

    private static void SyncBlocksDuplicateTaskNamesInStory()
    {
        var project = new Project { Name = "Dup nomes", StartDate = new DateTime(2026, 7, 6) };
        var epic    = new ProjectTask { Id = 1, TfsId = 900, TfsType = "Epic", Name = "Epic A" };
        var story   = new ProjectTask { Id = 2, TfsId = 901, TfsType = "User Story", Name = "Story A", Parent = epic };
        var t1      = new ProjectTask { Id = 3, TfsId = 0, TfsType = "Task", Name = "Ajustar view", Parent = story };
        var t2      = new ProjectTask { Id = 4, TfsId = 0, TfsType = "Task", Name = "ajustar view", Parent = story }; // mesmo nome (case-insensitive)
        var t3      = new ProjectTask { Id = 5, TfsId = 0, TfsType = "Task", Name = "Outra task", Parent = story };
        story.Children.Add(t1); story.Children.Add(t2); story.Children.Add(t3);
        epic.Children.Add(story);
        project.Tasks.Add(epic);

        try
        {
            TfsImportService.EnsureNoDuplicateTaskNamesInStory(project);
            throw new InvalidOperationException("Sync deveria BLOQUEAR duas Tasks de mesmo nome na Story.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MESMO nome"))
        {
            if (!ex.Message.Contains("Story A") || !ex.Message.Contains("Ajustar view"))
                throw new InvalidOperationException("A mensagem deve citar a Story e o nome da Task duplicada.");
        }

        // Sem duplicidade → passa.
        t2.Name = "Ajustar outra view";
        TfsImportService.EnsureNoDuplicateTaskNamesInStory(project);
    }

    private static void SetCurrentCalendar(ProjectCalendar calendar)
    {
        var property = typeof(ProjectCalendarService).GetProperty(nameof(ProjectCalendarService.Current))
            ?? throw new InvalidOperationException("ProjectCalendarService.Current nao encontrado.");
        property.SetValue(null, calendar);
    }
}
