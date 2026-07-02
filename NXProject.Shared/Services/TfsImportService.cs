using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NXProject.Models;

namespace NXProject.Services
{
    /// <summary>
    /// Importa um projeto do Azure DevOps / TFS via API REST (PAT) montando a
    /// hierarquia Project -> Epic -> Feature -> Story. Tarefas (Task) sao ignoradas.
    ///
    /// Cada Story recebe o esforco (campo "HH Estimado") convertido de horas para
    /// dias uteis. Se a Story tem data de inicio ("Data_Inicio"), a barra comeca
    /// nela e conta os dias uteis a partir dai; se tem data de fim ("Data_Fim"),
    /// ela e usada diretamente. Sem data de inicio, as Stories sao encadeadas em
    /// sequencia a partir da data do projeto.
    /// </summary>
    public static class TfsImportService
    {
        private const string ApiVersion = "api-version=6.0";
        private static readonly HttpClient Http = new();

        public sealed record OnlineChildTaskInfo(
            int Id,
            string Name,
            string Type,
            string State,
            string Tags,
            string Description,
            string LastHistory)
        {
            public string IdText => $"#{Id}";
        }

        // Nomes de exibicao dos campos customizados procurados, na ordem de
        // preferencia. O rotulo "HH Estimado" no formulario corresponde ao campo
        // "Esforço Estimado"; o inicio/fim sao "Data_Inicio"/"Data_Fim" (com
        // underscore — distintos de "Data Inicio"/"Data Fim"). Casamos pelo nome
        // EXATO (case-insensitive), sem remover espacos/underscores, para nao
        // confundir campos diferentes.
        private static readonly string[] HoursFieldNames =
            { "Esforço Estimado", "Esforco Estimado", "HH Estimado", "HH_Estimado" };
        private static readonly string[] OriginalHoursFieldNames =
            { "HH_Original_float", "Esforço Estimado", "Esforco Estimado", "HH Estimado", "HH_Estimado", "HH Original", "HH_Original" };
        private static readonly string[] RemainingHoursFieldNames =
            { "HH_Restante_float", "HH_Restante", "HH Restante", "HHRestante" };
        private static readonly string[] CurrentHoursFieldNames =
            { "HH_Atual_float", "HH_Atual", "HH Atual", "HHAtual", "HH Realizado", "HH_Realizado", "HHRealizado" };
        private static readonly string[] StartFieldNames =
            { "Data_Inicio", "Data Inicio", "DataInicio" };
        private static readonly string[] FinishFieldNames =
            { "Data_Fim", "Data Fim", "DataFim" };
        private static readonly string[] PercAlocFieldNames =
            { "Perc_Alocação", "Perc_Aloc", "PercAloc", "Perc Aloc", "Percentual Alocacao", "Percentual_Alocacao" };
        private static readonly string[] PercConclusaoFieldNames =
            { "Perc_Conclusao", "Perc_Conclusão", "PercConclusao", "Percentual Conclusao", "Percentual_Conclusao" };
        private static readonly string[] TipoCentroCustoFieldNames =
            { "Tipo_Centro_Custo", "TipoCentroCusto", "Tipo Centro Custo" };

        public static async Task<ImportResult> ImportAsync(
            TfsConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (!options.IsValid)
                throw new InvalidOperationException("Conexão TFS incompleta: informe organização, projeto, PAT e o ID do work item raiz.");

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var authHeader = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            // 1) Descobre os reference names dos campos customizados. Usa o nome
            //    configurado (config_nxproject.json) e, se nao achar, tenta os
            //    candidatos conhecidos como fallback.
            var fieldMap = await LoadFieldMapAsync(orgBase, authHeader, cancellationToken);
            var hoursRef = ResolveField(fieldMap, options.EffortFieldName, HoursFieldNames);
            var remainingHoursRefImport = ResolveField(fieldMap, null, RemainingHoursFieldNames);
            var originalHoursRef = ResolveField(fieldMap, null, OriginalHoursFieldNames);
            var startRef = ResolveField(fieldMap, options.StartFieldName, StartFieldNames);
            var finishRef = ResolveField(fieldMap, options.FinishFieldName, FinishFieldNames);
            var percAlocRef = ResolveField(fieldMap, options.PercAlocFieldName, PercAlocFieldNames);
            var percConclusaoRef   = ResolveField(fieldMap, options.PercConclusaoFieldName, PercConclusaoFieldNames);
            var tipoCentroCustoRef = ResolveField(fieldMap, null, TipoCentroCustoFieldNames);
            var realizedHoursRef   = ResolveField(fieldMap, null, CurrentHoursFieldNames);

            // Sprints (iterations) do projeto. Carrega TODAS para o mapa de datas
            // (ancora das Stories sem data); as numeradas/exibidas serao so as
            // efetivamente usadas pelos work items (definidas apos baixar os itens).
            var allSprints = await LoadIterationsAsync(
                orgBase, options.TeamProject, authHeader, cancellationToken);
            var sprintStarts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in allSprints)
                if (!string.IsNullOrEmpty(s.Path))
                    sprintStarts[s.Path!] = s.Start;

            // 2) Query recursiva de links hierarquicos a partir da raiz.
            var edges = await LoadHierarchyEdgesAsync(orgBase, options.TeamProject, authHeader, options.RootWorkItemId, cancellationToken);

            // 3) Coleta todos os ids e baixa os campos em lote.
            var allIds = new HashSet<int> { options.RootWorkItemId };
            foreach (var (parent, child) in edges)
            {
                allIds.Add(parent);
                allIds.Add(child);
            }

            var requestedFields = new List<string>
            {
                "System.Id", "System.Title", "System.WorkItemType", "System.State",
                "System.AssignedTo", "System.IterationPath", "System.Description", "System.Tags",
                "Microsoft.VSTS.Common.StackRank"
            };
            var syncVersionRef = ResolveField(fieldMap, options.SyncVersionFieldName, new[] { "Sync_version", "SyncVersion", "Sync Version" });
            var syncNameRef    = ResolveField(fieldMap, options.SyncNameFieldName,    new[] { "Sync_Name", "SyncName", "Sync Name" });

            if (hoursRef != null) requestedFields.Add(hoursRef);
            if (remainingHoursRefImport != null && !requestedFields.Contains(remainingHoursRefImport)) requestedFields.Add(remainingHoursRefImport);
            if (originalHoursRef != null) requestedFields.Add(originalHoursRef);
            if (startRef != null) requestedFields.Add(startRef);
            if (finishRef != null) requestedFields.Add(finishRef);
            if (percAlocRef != null) requestedFields.Add(percAlocRef);
            if (percConclusaoRef != null && !requestedFields.Contains(percConclusaoRef)) requestedFields.Add(percConclusaoRef);
            if (tipoCentroCustoRef != null) requestedFields.Add(tipoCentroCustoRef);
            if (realizedHoursRef   != null) requestedFields.Add(realizedHoursRef);
            if (syncVersionRef != null) requestedFields.Add(syncVersionRef);
            if (syncNameRef != null) requestedFields.Add(syncNameRef);

            // Campos Custom DevOps mapeados por tipo — adiciona ao requestedFields
            foreach (var kv in options.TypeFieldMappings)
                foreach (var fd in kv.Value.CustomDevopsFields)
                    if (!string.IsNullOrWhiteSpace(fd.Field) && !requestedFields.Contains(fd.Field))
                        requestedFields.Add(fd.Field);

            var items = await LoadWorkItemsAsync(
                orgBase, authHeader, allIds, requestedFields, cancellationToken, expandRelations: true);

            if (!items.TryGetValue(options.RootWorkItemId, out var rootItem))
                throw new InvalidOperationException(
                    $"Work item raiz {options.RootWorkItemId} não encontrado ou sem acesso no projeto '{options.TeamProject}'.");

            // 4) Indexa filhos por pai (e pai por filho, para gravar TfsParentId).
            var childrenByParent = new Dictionary<int, List<int>>();
            var parentByChild = new Dictionary<int, int>();
            foreach (var (parent, child) in edges)
            {
                if (!childrenByParent.TryGetValue(parent, out var list))
                    childrenByParent[parent] = list = new List<int>();
                list.Add(child);
                parentByChild[child] = parent;
            }

            // 5) Monta o projeto NXProject.
            var rootStart = ReadDate(rootItem, startRef);

            // Lê as sprints (iterations) do DevOps para ancorar a numeração: a 1a
            // sprint usada pelo projeto vira "Sprint 1" e as seguintes contam em
            // sequência (2, 3, ...). Para isso, alinhamos a grade do cronograma —
            // início do projeto na 1a sprint e duração = cadência real das sprints.
            var (sprintAnchor, sprintDuration) = ComputeSprintAnchor(items.Values, sprintStarts);

            var project = new Project
            {
                Name = rootItem.Title,
                Description = $"Importado do TFS — {options.TeamProject} (#{options.RootWorkItemId})",
                StartDate = sprintAnchor ?? rootStart ?? DateTime.Today,
                FirstSprintNumber = 1,
                SprintNumberingMode = "Sequencial",
                FilePath = null
            };
            if (sprintDuration.HasValue)
                project.SprintDurationDays = sprintDuration.Value;
            var resourcesByKey = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
            AddResourceIfAssigned(project, resourcesByKey, rootItem, options.HoursPerDay);

            // Sprints exibidas/numeradas: usadas pelos work items + futuras dentro da
            // janela configurada (FutureSprintDays) para o dropdown de escolha de sprint.
            foreach (var s in SelectUsedSprints(items.Values, allSprints, options.FutureSprintDays))
                project.Sprints.Add(s);

            var context = new BuildContext
            {
                Items = items,
                ChildrenByParent = childrenByParent,
                HoursRef = hoursRef,
                RemainingHoursRef = remainingHoursRefImport,
                OriginalHoursRef = originalHoursRef,
                StartRef = startRef,
                FinishRef = finishRef,
                PercAlocRef = percAlocRef,
                PercConclusaoRef = percConclusaoRef,
                TipoCentroCustoRef = tipoCentroCustoRef,
                CurrentHoursRef    = realizedHoursRef,
                SyncVersionRef = syncVersionRef,
                HoursPerDay = options.HoursPerDay <= 0 ? ProjectCalendarService.WorkingHoursPerDay : options.HoursPerDay,
                ProjectStart = project.StartDate,
                SprintStartByPath = sprintStarts,
                ParentByChild = parentByChild,
                Project = project,
                ResourcesByKey = resourcesByKey,
                FixedStartTagName = string.IsNullOrWhiteSpace(options.FixedStartTagName) ? "DT-INI-NEG" : options.FixedStartTagName.Trim(),
                CustomDevopsFieldsByType = options.TypeFieldMappings
                    .Where(kv => kv.Value.CustomDevopsFields.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.CustomDevopsFields, StringComparer.OrdinalIgnoreCase)
            };

            // Filhos diretos do raiz viram ramos do cronograma quando forem
            // Epic, Feature ou Story. Em alguns backlogs, Stories em New ficam
            // diretamente abaixo do item raiz, e nao dentro de um Epic.
            foreach (var childId in OrderedChildren(childrenByParent, options.RootWorkItemId, items))
            {
                if (!items.TryGetValue(childId, out var child)) continue;
                if (!IsImportRootType(child.WorkItemType)) continue;

                var task = BuildBranch(context, childId, level: 0);
                if (task != null)
                    project.Tasks.Add(task);
            }

            NormalizeIds(project.Tasks);

            // Etapa 2: leitura separada dos links de predecessora via WIQL.
            var depLinks = await LoadDependencyLinksAsync(
                orgBase, options.TeamProject, authHeader, allIds, cancellationToken);
            var externalPredTfsIds = ApplyTfsPredecessors(project.Tasks, depLinks);
            foreach (var t in project.Tasks)
                t.RecalcSummary();

            if (project.Tasks.Count == 0)
                throw new InvalidOperationException(
                    "Nenhum Epic/Feature/Story encontrado abaixo do work item raiz informado.");

            // Etapa 3: resolve predecessoras externas (fora do escopo deste import).
            if (externalPredTfsIds.Count > 0)
            {
                var extItems = await FetchWorkItemsByIdsAsync(
                    orgBase, options.TeamProject, authHeader, externalPredTfsIds, cancellationToken);
                foreach (var extId in externalPredTfsIds.OrderBy(x => x))
                {
                    context.Report.ExternalPredecessors++;
                    if (extItems.TryGetValue(extId, out var extItem))
                    {
                        if (IsStoryType(extItem.WorkItemType))
                            context.Report.LogWarning(
                                $"[PRED EXTERNA] #{extId} \"{extItem.Title}\" é uma Story fora do escopo deste import (type={extItem.WorkItemType}, state={extItem.State}).");
                        else
                            context.Report.LogWarning(
                                $"[PRED EXTERNA] #{extId} \"{extItem.Title}\" fora de escopo (type={extItem.WorkItemType}, state={extItem.State}).");
                    }
                    else
                    {
                        context.Report.LogWarning($"[PRED EXTERNA] #{extId} não encontrado no DevOps ou sem acesso.");
                    }
                }
            }

            return new ImportResult(project, context.Report);
        }

        // ── Sincronizacao (Export -> TFS/DevOps) ─────────────────────────────

        // ── Relatório de Importação ──────────────────────────────────────────

        public sealed class ImportReport
        {
            public int StoriesStateFixed;
            public int ExternalPredecessors;
            public List<SyncLogEntry> Log = new();

            public void LogInfo(string msg)    => Log.Add(new SyncLogEntry(SyncLogLevel.Success, msg));
            public void LogWarning(string msg) => Log.Add(new SyncLogEntry(SyncLogLevel.Warning, msg));
            public void LogError(string msg)   => Log.Add(new SyncLogEntry(SyncLogLevel.Error,   msg));

            public bool HasIssues => Log.Any(e => e.Level != SyncLogLevel.Success);
        }

        public sealed class ImportResult
        {
            public Project Project { get; }
            public ImportReport Report { get; }
            public ImportResult(Project project, ImportReport report) { Project = project; Report = report; }
        }

        public enum SyncLogLevel { Success, Warning, Error }

        public sealed class SyncLogEntry
        {
            public SyncLogLevel Level { get; }
            public string Message { get; }
            public SyncLogEntry(SyncLogLevel level, string message) { Level = level; Message = message; }
        }

        public sealed class SyncConflictItem
        {
            public ProjectTask Task { get; init; } = null!;
            public int TfsVersion { get; init; }
            public int LocalVersion { get; init; }
            public string ChangedBy { get; init; } = "";
            // Snapshot dos valores TFS no momento do conflito
            public string TfsTitle  { get; init; } = "";
            public string TfsState  { get; init; } = "";
            public string TfsTags   { get; init; } = "";
            public double? TfsHours { get; init; }
            public DateTime? TfsStart  { get; init; }
            public DateTime? TfsFinish { get; init; }
            // Valores locais (lidos diretamente da tarefa)
            public string LocalTitle  => Task.Name ?? "";
            public string LocalState  => Task.TfsState ?? "";
            public string LocalTags   => Task.Tags ?? "";
            public double? LocalHours => Task.EstimatedHours;
            public DateTime? LocalStart  => Task.Start == default ? null : Task.Start;
            public DateTime? LocalFinish => Task.Finish == default ? null : Task.Finish;
            public string TfsType => Task.TfsType ?? "";
            public int TfsId => Task.TfsId ?? 0;
            // Atividade já iniciada (% > 0) não deve ser sobrescrita automaticamente no merge.
            public double LocalPercentComplete => Task.PercentComplete;
            public bool IsStarted => Task.PercentComplete > 0.0001;
        }

        public sealed class SyncReport
        {
            public int Updated;
            public int Created;
            public int Reparented;
            public int Skipped;
            public int NotFound;
            public int Conflicts;
            // Detalhes por item (sucesso, aviso, erro).
            public List<SyncLogEntry> Log = new();
            // Features/Stories que ficaram sem sprint (IterationPath vazio).
            public List<string> WithoutSprint = new();
            // Itens com conflito de concorrência, para resolução manual.
            public List<SyncConflictItem> ConflictItems = new();

            // Mantido para compatibilidade; redireciona para Log.
            public List<string> Messages => Log
                .Where(e => e.Level != SyncLogLevel.Success)
                .Select(e => e.Message)
                .ToList();

            public void LogSuccess(string msg) => Log.Add(new SyncLogEntry(SyncLogLevel.Success, msg));
            public void LogWarning(string msg) => Log.Add(new SyncLogEntry(SyncLogLevel.Warning, msg));
            public void LogError(string msg)   => Log.Add(new SyncLogEntry(SyncLogLevel.Error,   msg));

            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Atualizados: {Updated}");
                if (Created > 0) sb.AppendLine($"Criados no DevOps: {Created}");
                if (Reparented > 0) sb.AppendLine($"Reparentados (parent atualizado): {Reparented}");
                sb.AppendLine($"Sem alteracao: {Skipped}");
                if (NotFound > 0) sb.AppendLine($"Nao encontrados no DevOps: {NotFound}");
                if (Conflicts > 0) sb.AppendLine($"⚠ CONFLITOS DE CONCORRÊNCIA: {Conflicts} item(ns) descartados — verifique o log (vermelho) e reimporte se necessário.");
                foreach (var e in Log.Where(e => e.Level != SyncLogLevel.Success))
                    sb.AppendLine(e.Message);
                if (WithoutSprint.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Atividades sem sprint ({WithoutSprint.Count}):");
                    foreach (var name in WithoutSprint)
                        sb.AppendLine($"  • {name}");
                }
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Sincroniza tarefas vinculadas de volta para o DevOps:
        ///  - TfsId == 0 → CRIA o work item (Epic/Feature/Story) e grava o id retornado;
        ///  - TfsId > 0 → atualiza Título/Descrição e horas quando mudaram;
        ///    início só se o TFS já tiver início não-nulo; fim só se o estado encerrado;
        ///    e reparenta no DevOps se o pai hierárquico mudou (validando antes).
        /// </summary>
        public static async Task<SyncReport> SyncAsync(
            Project project, TfsConnectionOptions options, CancellationToken cancellationToken = default,
            HashSet<int>? forceOverwriteIds = null)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
                throw new InvalidOperationException("Conexão TFS incompleta: informe organização, projeto e PAT (use Importar → TFS para configurar).");

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var fieldMap = await LoadFieldMapAsync(orgBase, auth, cancellationToken);
            var hoursRef = ResolveField(fieldMap, options.EffortFieldName, HoursFieldNames);
            var originalHoursRef  = ResolveField(fieldMap, null, OriginalHoursFieldNames);
            var remainingHoursRef = ResolveField(fieldMap, null, RemainingHoursFieldNames);
            var realizedHoursRef  = ResolveField(fieldMap, null, CurrentHoursFieldNames);
            var startRef = ResolveField(fieldMap, options.StartFieldName, StartFieldNames);
            var finishRef = ResolveField(fieldMap, options.FinishFieldName, FinishFieldNames);
            var percAlocRef = ResolveField(fieldMap, options.PercAlocFieldName, PercAlocFieldNames);
            var percConclusaoRef = ResolveField(fieldMap, options.PercConclusaoFieldName, PercConclusaoFieldNames);
            var syncVersionRef = ResolveField(fieldMap, options.SyncVersionFieldName, new[] { "Sync_version", "SyncVersion", "Sync Version" });
            var syncNameRef    = ResolveField(fieldMap, options.SyncNameFieldName,    new[] { "Sync_Name", "SyncName", "Sync Name" });

            // Resolve refs por tipo (TypeFieldMappings sobrescreve os globais por tipo)
            string? ResolveForType(string? tfsType, Func<TypeFieldConfig, string?> getter, string? globalRef)
            {
                if (tfsType != null &&
                    options.TypeFieldMappings.TryGetValue(tfsType, out var cfg) &&
                    !string.IsNullOrWhiteSpace(getter(cfg)))
                    return ResolveField(fieldMap, getter(cfg), Array.Empty<string>()) ?? getter(cfg)!.Trim();
                return globalRef;
            }

            // Atualiza datas de início/fim das sprints a partir do DevOps.
            var allSprints = await LoadIterationsAsync(orgBase, options.TeamProject, auth, cancellationToken);
            foreach (var s in allSprints)
            {
                if (string.IsNullOrEmpty(s.Path)) continue;
                var existing = project.Sprints.FirstOrDefault(ps =>
                    string.Equals(ps.Path, s.Path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Start = s.Start;
                    existing.End   = s.End;
                }
            }

            // Recalcula a ordem (StackRank) conforme a árvore do NXProject.
            ApplyDesiredStackRanks(project.Tasks);

            // Top-down: pais antes dos filhos (garante criar o pai antes de criar/reparentar o filho).
            var tasks = new List<ProjectTask>();
            CollectLinkedTasks(project.Tasks, tasks);
            var tasksById = tasks
                .GroupBy(t => t.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var report = new SyncReport();
            report.LogSuccess($"[config] hoursRef={hoursRef ?? "(não resolvido)"} | startRef={startRef ?? "(não resolvido)"} | finishRef={finishRef ?? "(não resolvido)"} | percAlocRef={percAlocRef ?? "(não resolvido)"} | percConclusaoRef={percConclusaoRef ?? "(não resolvido)"}");

            if (tasks.Count == 0)
            {
                report.LogWarning("Nenhuma tarefa vinculada ao DevOps (clique no ID para vincular ou digite 0 para criar).");
                return report;
            }

            // Lê os itens existentes (TfsId > 0) com relations (para detectar o pai atual).
            var existingIds = tasks.Where(t => t.TfsId.HasValue && t.TfsId.Value > 0).Select(t => t.TfsId!.Value).ToList();
            var requested = new List<string> { "System.Id", "System.Title", "System.State", "System.Description" };
            if (hoursRef != null) requested.Add(hoursRef);
            if (originalHoursRef  != null && !requested.Contains(originalHoursRef))  requested.Add(originalHoursRef);
            if (remainingHoursRef != null && !requested.Contains(remainingHoursRef)) requested.Add(remainingHoursRef);
            if (realizedHoursRef  != null && !requested.Contains(realizedHoursRef))  requested.Add(realizedHoursRef);
            if (startRef != null) requested.Add(startRef);
            if (finishRef != null) requested.Add(finishRef);
            if (percAlocRef != null) requested.Add(percAlocRef);
            if (syncVersionRef != null) requested.Add(syncVersionRef);
            if (syncNameRef != null) requested.Add(syncNameRef);
            requested.Add("Microsoft.VSTS.Common.Priority"); // Priority para Tasks

            // Campos Custom DevOps — necessário para comparar valor atual antes de enviar patch
            foreach (var kv in options.TypeFieldMappings)
                foreach (var fd in kv.Value.CustomDevopsFields)
                    if (!string.IsNullOrWhiteSpace(fd.Field) && !requested.Contains(fd.Field))
                        requested.Add(fd.Field);

            var current = existingIds.Count > 0
                ? await LoadWorkItemsAsync(orgBase, auth, existingIds, requested, cancellationToken, expandRelations: true)
                : new Dictionary<int, WorkItem>();

            // Pré-etapa: qualquer atividade interna (I:) que já tenha tipo DevOps precisa
            // virar TfsId antes de sincronizarmos links de predecessora de outras atividades.
            foreach (var task in tasks)
            {
                if (IsNoDevOpsType(task.TfsType)
                    || task.HasTfsLink)
                    continue;

                var desiredParent = ResolveDesiredParent(task, options.RootWorkItemId);
                if (desiredParent <= 0)
                    continue;

                var createType = task.TfsType;
                if (string.IsNullOrWhiteSpace(createType))
                {
                    createType = task.Parent?.TfsType switch
                    {
                        "Epic"    => "Feature",
                        "Feature" => "User Story",
                        _         => "User Story"
                    };
                    task.TfsType = createType;
                }

                var existingChild = await FindExistingChildByTitleAndTypeAsync(
                    orgBase,
                    options.TeamProject,
                    auth,
                    desiredParent,
                    createType,
                    task.Name,
                    cancellationToken);

                if (existingChild != null)
                {
                    task.TfsId = existingChild.Id;
                    task.TfsParentId = desiredParent;
                    task.IsPendingTfsCreate = false;
                    current[existingChild.Id] = existingChild;
                    report.LogSuccess($"{createType} - #{existingChild.Id} ({task.Name}): já existia no DevOps; vínculo recuperado pelo nome no mesmo pai.");
                    continue;
                }

                var createHoursRef  = ResolveForType(task.TfsType, c => c.EffortField,        hoursRef);
                var createStartRef  = ResolveForType(task.TfsType, c => c.StartField,         startRef);
                var createFinishRef = ResolveForType(task.TfsType, c => c.FinishField,        finishRef);
                var createPercAloc  = ResolveForType(task.TfsType, c => c.PercAlocField,      percAlocRef);

                options.TypeFieldMappings.TryGetValue(task.TfsType ?? "", out var cfgForClass);
                if (cfgForClass == null) options.TypeFieldMappings.TryGetValue("*", out cfgForClass);
                var classFields = cfgForClass?.CustomDevopsFields ?? [];
                if (classFields.Count == 0
                    && string.Equals(task.TfsType, "Feature", StringComparison.OrdinalIgnoreCase))
                {
                    classFields = [new ClassificationFieldDef { Field = "Custom.Type", FieldType = "Picklist" }];
                    if (string.IsNullOrWhiteSpace(task.TfsClassification))
                        task.TfsClassification = "Feature";
                }

                var createOps = BuildCreateOps(
                    task,
                    desiredParent,
                    orgBase,
                    createHoursRef,
                    createStartRef,
                    createFinishRef,
                    tasksById,
                    syncPredecessorLinks: false,
                    percAlocRef: createPercAloc,
                    originalHoursRef: originalHoursRef,
                    remainingHoursRef: remainingHoursRef,
                    realizedHoursRef: realizedHoursRef,
                    extraFields: options.ExtraCreateFields,
                    classificationFields: classFields);
                var newId = await CreateWorkItemAsync(orgBase, auth, options.TeamProject, createType, createOps, cancellationToken);
                task.TfsId = newId;
                task.TfsParentId = desiredParent;
                task.IsPendingTfsCreate = false;
                report.Created++;
                report.LogSuccess($"{createType} - #{newId} ({task.Name}): criado.");
            }

            foreach (var task in tasks)
            {
                // Declarados antes do try para ficarem acessíveis no catch (retry sem predecessoras).
                var ops              = new List<object>();
                var changes          = new List<string>();
                var predecessorAddOps = new List<object>();
                try
                {
                    // Tarefas marcadas como "No DevOps" ou com TfsId negativo nunca são enviadas ao TFS.
                    if (IsNoDevOpsType(task.TfsType)
                        || task.TfsId < 0)
                        continue;

                    // Pai desejado = pai na hierarquia do NXProject (usa TfsId atualizado, inclusive
                    // se o pai acabou de ser criado nesta mesma execução de SyncAsync).
                    // IsPendingTfsCreate ou TfsId == 0/null → item ainda não existe no DevOps.
                    var parentTask = task.Parent;
                    int desiredParent = ResolveDesiredParent(task, options.RootWorkItemId);

                    if (task.IsPendingTfsCreate || !task.TfsId.HasValue || task.TfsId.Value == 0)
                    {
                        // CRIAR no DevOps.
                        if (desiredParent <= 0)
                        {
                            // Se o pai tem TfsType != "No DevOps" e está no cronograma, é provável
                            // que o pai precise ser criado primeiro e está fora da ordem de coleção.
                            if (parentTask != null && !string.Equals(parentTask.TfsType?.Trim(), "No DevOps", StringComparison.OrdinalIgnoreCase))
                                report.LogWarning($"{TaskSyncLabel(task)} ({task.Name}): o pai \"{parentTask.Name}\" ainda não tem vínculo DevOps. Sincronize novamente após vincular/criar o pai.");
                            else
                                report.LogWarning($"{TaskSyncLabel(task)} ({task.Name}): pai sem vínculo DevOps; vincule/crie o pai primeiro.");
                            continue;
                        }

                        // Infere o tipo a partir do pai quando não definido.
                        var createType = task.TfsType;
                        if (string.IsNullOrWhiteSpace(createType))
                        {
                            createType = task.Parent?.TfsType switch
                            {
                                "Epic"    => "Feature",
                                "Feature" => "User Story",
                                _         => "User Story"
                            };
                            task.TfsType = createType;
                        }

                        // Resolve refs por tipo para criação (antes de ter o loop principal que define typeHoursRef etc.)
                        var createHoursRef  = ResolveForType(task.TfsType, c => c.EffortField,        hoursRef);
                        var createStartRef  = ResolveForType(task.TfsType, c => c.StartField,         startRef);
                        var createFinishRef = ResolveForType(task.TfsType, c => c.FinishField,        finishRef);
                        var createPercAloc  = ResolveForType(task.TfsType, c => c.PercAlocField,      percAlocRef);
                        var createPercConc  = ResolveForType(task.TfsType, c => c.PercConclusaoField, percConclusaoRef);
                        // Campos de classificação configurados para este tipo
                        options.TypeFieldMappings.TryGetValue(task.TfsType ?? "", out var cfgForClass);
                        if (cfgForClass == null) options.TypeFieldMappings.TryGetValue("*", out cfgForClass);
                        var classFields = cfgForClass?.CustomDevopsFields ?? [];
                        // Compat: Feature sem mapeamento → Custom.Type como padrão
                        if (classFields.Count == 0
                            && string.Equals(task.TfsType, "Feature", StringComparison.OrdinalIgnoreCase))
                        {
                            classFields = [new ClassificationFieldDef { Field = "Custom.Type", FieldType = "Picklist" }];
                            if (string.IsNullOrWhiteSpace(task.TfsClassification))
                                task.TfsClassification = "Feature";
                        }
                        // Atividade exibida como I: ainda não tem vínculo real com DevOps.
                        // Antes de criar, tenta recuperar um filho já existente no mesmo pai,
                        // com o mesmo tipo e nome, e reaproveitar o ID do DevOps.
                        var shouldRecoverInternalLink = !task.HasTfsLink;
                        var existingChild = shouldRecoverInternalLink
                            ? await FindExistingChildByTitleAndTypeAsync(
                                orgBase,
                                options.TeamProject,
                                auth,
                                desiredParent,
                                createType,
                                task.Name,
                                cancellationToken)
                            : null;

                        if (existingChild != null)
                        {
                            task.TfsId = existingChild.Id;
                            task.TfsParentId = desiredParent;
                            task.IsPendingTfsCreate = false;
                            current[existingChild.Id] = existingChild;
                            report.LogSuccess($"{createType} - #{existingChild.Id} ({task.Name}): já existia no DevOps; vínculo recuperado pelo nome no mesmo pai.");
                        }
                        else
                        {
                            var createOps = BuildCreateOps(task, desiredParent, orgBase, createHoursRef, createStartRef, createFinishRef, tasksById, options.SyncPredecessorLinks, createPercAloc, originalHoursRef, remainingHoursRef, realizedHoursRef, options.ExtraCreateFields, classFields);
                            var newId = await CreateWorkItemAsync(orgBase, auth, options.TeamProject, createType, createOps, cancellationToken);
                            task.TfsId = newId;
                            task.TfsParentId = desiredParent;
                            task.IsPendingTfsCreate = false;
                            report.Created++;
                            report.LogSuccess($"{createType} - #{newId} ({task.Name}): criado.");
                            continue;
                        }
                    }

                    // ATUALIZAR item existente.
                    if (!current.TryGetValue(task.TfsId.Value, out var wi))
                    {
                        report.NotFound++;
                        report.LogError($"{TaskSyncLabel(task)} ({task.Name}): não encontrado no DevOps.");
                        continue;
                    }

                    // ── Controle de concorrência ────────────────────────────────────────
                    // Compara a versão que temos (importada) com a versão atual no TFS.
                    // Se o TFS tem versão maior, outro usuário gravou depois de nós → conflito.
                    if (syncVersionRef != null && task.SyncVersion.HasValue)
                    {
                        var tfsVersion = (int)(ReadDouble(wi, syncVersionRef) ?? 0);
                        bool forcedOverwrite = forceOverwriteIds != null && task.TfsId.HasValue && forceOverwriteIds.Contains(task.TfsId.Value);
                        if (!forcedOverwrite && tfsVersion > task.SyncVersion.Value)
                        {
                            var whoSaved = ReadSyncUserName(wi, syncNameRef);
                            if (IsCurrentSyncUser(whoSaved))
                            {
                                var previousLocalVersion = task.SyncVersion.Value;
                                task.SyncVersion = tfsVersion;
                                task.HasSyncConflict = false;
                                report.LogWarning($"{TaskSyncLabel(task)} ({task.Name}): versão TFS={tfsVersion} > local={previousLocalVersion}, mas a última gravação foi do usuário atual ({whoSaved}); sincronização liberada.");
                            }
                            else
                            {
                                task.HasSyncConflict = true;
                                report.Conflicts++;
                                var by = string.IsNullOrWhiteSpace(whoSaved) ? "" : $" (por {whoSaved})";
                                report.LogError($"⚠ CONFLITO {TaskSyncLabel(task)} ({task.Name}): versão TFS={tfsVersion} > local={task.SyncVersion.Value}{by}. Alterações descartadas — reimporte para atualizar.");
                                var typeHoursRefConflict  = ResolveForType(task.TfsType, c => c.EffortField, hoursRef);
                                var typeStartRefConflict  = ResolveForType(task.TfsType, c => c.StartField,  startRef);
                                var typeFinishRefConflict = ResolveForType(task.TfsType, c => c.FinishField, finishRef);
                                report.ConflictItems.Add(new SyncConflictItem
                                {
                                    Task         = task,
                                    TfsVersion   = tfsVersion,
                                    LocalVersion = task.SyncVersion.Value,
                                    ChangedBy    = whoSaved ?? "",
                                    TfsTitle     = wi.Title,
                                    TfsState     = wi.State,
                                    TfsTags      = wi.Tags,
                                    TfsHours     = ReadDouble(wi, typeHoursRefConflict),
                                    TfsStart     = ReadDate(wi, typeStartRefConflict),
                                    TfsFinish    = ReadDate(wi, typeFinishRefConflict),
                                });
                                report.Skipped++;
                                continue;
                            }
                        }
                        else if (task.HasSyncConflict)
                        {
                            task.HasSyncConflict = false;
                        }
                    }

                    ops.Clear();
                    changes.Clear();
                    predecessorAddOps.Clear();

                    bool isTask             = IsTaskType(task.TfsType);
                    bool isEpicOrFeature    = IsEpicOrFeatureType(task.TfsType);
                    bool isStoryLike        = IsStoryType(task.TfsType) || isEpicOrFeature;

                    // Campos possivelmente sobrescritos por tipo via TypeFieldMappings
                    var typeHoursRef    = ResolveForType(task.TfsType, c => c.EffortField,       hoursRef);
                    var typeStartRef    = ResolveForType(task.TfsType, c => c.StartField,        startRef);
                    var typeFinishRef   = ResolveForType(task.TfsType, c => c.FinishField,       finishRef);
                    var typePercAloc    = ResolveForType(task.TfsType, c => c.PercAlocField,     percAlocRef);
                    var typePercConc    = ResolveForType(task.TfsType, c => c.PercConclusaoField,percConclusaoRef);

                    if (!string.Equals((task.Name ?? string.Empty).Trim(), (wi.Title ?? string.Empty).Trim(), StringComparison.Ordinal))
                    {
                        ops.Add(PatchAdd("/fields/System.Title", task.Name ?? string.Empty));
                        changes.Add("título");
                    }

                    // ── Campos exclusivos de Story / Feature / Epic (não Task) ──────────
                    if (!isTask)
                    {
                        if (task.Description != null || !string.IsNullOrWhiteSpace(task.Justificativa))
                        {
                            var desiredDesc = MergeJustificativa(task.Description, task.Justificativa);
                            if (!string.Equals(desiredDesc.Trim(), (wi.Description ?? string.Empty).Trim(), StringComparison.Ordinal))
                            {
                                ops.Add(PatchAdd("/fields/System.Description", desiredDesc));
                                changes.Add("descrição");
                            }
                        }

                        // Tags (ex.: "Block") — sincroniza se o conjunto mudou.
                        if (!TagsEqual(task.Tags, wi.Tags))
                        {
                            ops.Add(PatchAdd("/fields/System.Tags", NormalizeTagsForWrite(task.Tags)));
                            changes.Add("tags");
                        }

                        // Ordem (StackRank) — sincroniza se o rank desejado mudou.
                        if (task.TfsStackRank.HasValue)
                        {
                            var currentRank = ReadDouble(wi, "Microsoft.VSTS.Common.StackRank");
                            if (currentRank == null || Math.Abs(currentRank.Value - task.TfsStackRank.Value) > 0.0001)
                            {
                                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.StackRank", task.TfsStackRank.Value));
                                changes.Add("ordem");
                            }
                        }

                        // HH Estimado (Esforço Estimado / Effort).
                        var desiredHours = GetSyncHours(task);
                        if (typeHoursRef != null && desiredHours.HasValue && typeHoursRef != originalHoursRef)
                        {
                            var currentHours = ReadDouble(wi, typeHoursRef);
                            if (currentHours == null || Math.Abs(currentHours.Value - desiredHours.Value) > 0.0001)
                            {
                                ops.Add(PatchAdd($"/fields/{typeHoursRef}", desiredHours.Value));
                                var oldH = currentHours.HasValue ? $"{currentHours.Value:0.##}→" : "";
                                changes.Add($"HH: {oldH}{desiredHours.Value:0.##}h");
                            }
                        }

                        // HH Original — só envia se o valor realmente diferir do TFS.
                        if (originalHoursRef != null && task.OriginalEstimatedHours is > 0)
                        {
                            var currentOrigH = ReadDouble(wi, originalHoursRef);
                            bool differs = currentOrigH == null
                                || Math.Abs(currentOrigH.Value - task.OriginalEstimatedHours.Value) > 0.0001;
                            if (differs)
                            {
                                ops.Add(PatchAdd($"/fields/{originalHoursRef}", task.OriginalEstimatedHours.Value));
                                var oldH = currentOrigH.HasValue ? $"{currentOrigH.Value:0.##}→" : "";
                                changes.Add($"HH Original: {oldH}{task.OriginalEstimatedHours.Value:0.##}h");
                            }
                        }

                        var desiredAssignee = GetDesiredAssigneeEmail(task);
                        if (!string.IsNullOrWhiteSpace(desiredAssignee) && !AssigneeEquals(wi, desiredAssignee))
                        {
                            ops.Add(PatchAdd("/fields/System.AssignedTo", desiredAssignee));
                            changes.Add($"responsável: {desiredAssignee}");
                        }

                        if (typePercAloc != null)
                        {
                            var primaryAloc = task.Resources.Count > 0 ? task.Resources[0].AllocationPercent : 100.0;
                            var primaryAlocInt = (int)Math.Round(primaryAloc);
                            var currentAloc = ReadDouble(wi, typePercAloc);
                            if (currentAloc == null || Math.Abs(currentAloc.Value - primaryAloc) > 0.5)
                            {
                                ops.Add(PatchAdd($"/fields/{typePercAloc}", primaryAlocInt));
                                var oldA = currentAloc.HasValue ? $"{currentAloc.Value:0}%→" : "";
                                changes.Add($"% aloc.: {oldA}{primaryAlocInt}%");
                            }
                        }

                        if (typePercConc != null)
                        {
                            var percConc = (int)Math.Round(Math.Clamp(task.PercentComplete, 0, 100));
                            var currentConc = ReadDouble(wi, typePercConc);
                            if (currentConc == null || Math.Abs(currentConc.Value - percConc) > 0.5)
                            {
                                ops.Add(PatchAdd($"/fields/{typePercConc}", percConc));
                                var oldC = currentConc.HasValue ? $"{currentConc.Value:0}%→" : "";
                                changes.Add($"% conclusão: {oldC}{percConc}%");
                            }
                        }

                        if (typeStartRef != null && task.Start > DateTime.MinValue.AddYears(1))
                        {
                            var currentStart = ReadDate(wi, typeStartRef);
                            var effectiveStateForStart = string.IsNullOrWhiteSpace(task.TfsState) ? wi.State : task.TfsState;
                            bool isClosed = IsClosedState(effectiveStateForStart) || task.PercentComplete >= 100;
                            var sprintObj = string.IsNullOrWhiteSpace(task.TfsIterationPath)
                                ? null
                                : project.Sprints.FirstOrDefault(s =>
                                    string.Equals(s.Path, task.TfsIterationPath, StringComparison.OrdinalIgnoreCase));
                            bool startDiffersFromSprint = sprintObj == null || task.Start.Date != sprintObj.Start.Date;
                            if (isClosed || task.StartFixed || startDiffersFromSprint)
                            {
                                if (currentStart == null || currentStart.Value.Date != task.Start.Date)
                                {
                                    ops.Add(PatchAdd($"/fields/{typeStartRef}", FormatDateForTfs(task.Start)));
                                    changes.Add(task.StartFixed
                                        ? $"início: {task.Start:dd/MM} (fixado)"
                                        : $"início: {task.Start:dd/MM}");
                                }
                            }
                        }

                        // Data Fim: sincroniza sempre que a data local difere do TFS (inclusive quando vazia no TFS).
                        if (typeFinishRef != null && task.Finish > DateTime.MinValue.AddYears(1))
                        {
                            var currentFinish = ReadDate(wi, typeFinishRef);
                            if (currentFinish == null || currentFinish.Value.Date != task.Finish.Date)
                            {
                                ops.Add(PatchAdd($"/fields/{typeFinishRef}", FormatDateForTfs(task.Finish)));
                                changes.Add($"fim: {task.Finish:dd/MM}");
                            }
                        }

                        // Tag de data fixada.
                        {
                            var fixedTag      = string.IsNullOrWhiteSpace(options.FixedStartTagName) ? "DT-INI-NEG" : options.FixedStartTagName.Trim();
                            var fixedTagAliases = GetFixedStartTagAliases(fixedTag);
                            var currentTags   = wi.Tags ?? string.Empty;
                            bool hasFixedTagNow = fixedTagAliases.Any(tag => HasTag(currentTags, tag));
                            if (task.StartFixed && !hasFixedTagNow)
                            {
                                var newTags = (currentTags.Trim().TrimEnd(';') + "; " + fixedTag).Trim().TrimStart(';').Trim();
                                ops.Add(PatchAdd("/fields/System.Tags", newTags));
                                changes.Add($"tag: +{fixedTag}");
                            }
                            else if (!task.StartFixed && hasFixedTagNow)
                            {
                                var parts = currentTags
                                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Where(t => !fixedTagAliases.Any(tag => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
                                ops.Add(PatchAdd("/fields/System.Tags", string.Join("; ", parts)));
                                changes.Add($"tag: -{string.Join("/", fixedTagAliases)}");
                                // Não fazemos PatchRemove do campo de data aqui: ao deixar de ser
                                // fixada, a data calculada prevalece e já é gravada pelo bloco
                                // acima (typeStartRef), evitando duas atualizações do mesmo campo
                                // no mesmo patch (erro VS403691 do DevOps).
                            }
                        }

                        // Sprint (System.IterationPath).
                        if (!string.IsNullOrWhiteSpace(task.TfsIterationPath) &&
                            !string.Equals(task.TfsIterationPath.Trim(), (wi.IterationPath ?? string.Empty).Trim(),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ops.Add(PatchAdd("/fields/System.IterationPath", task.TfsIterationPath.Trim()));
                            var sprintName = task.TfsIterationPath.Trim().Split('\\').LastOrDefault() ?? task.TfsIterationPath.Trim();
                            changes.Add($"sprint: {sprintName}");
                        }
                    } // end !isTask

                    if (isTask && IsDevOpsMilestoneType(task.TfsType))
                    {
                        var currentTags = wi.Tags ?? string.Empty;
                        if (!HasTag(currentTags, "MARCO-PROJECT"))
                        {
                            var newTags = AddTag(currentTags, "MARCO-PROJECT");
                            ops.Add(PatchAdd("/fields/System.Tags", newTags));
                            changes.Add("tag: +MARCO-PROJECT");
                        }
                    }

                    // ── Campos de horas para Story/Feature/Epic ────────────────────────────
                    if (!isTask)
                    {
                        // HH Restante.
                        if (remainingHoursRef != null && task.EstimatedHours is >= 0)
                        {
                            var remainingH  = task.EstimatedHours.Value;
                            var currentRemH = ReadDouble(wi, remainingHoursRef);
                            if (currentRemH == null || Math.Abs(currentRemH.Value - remainingH) > 0.0001)
                            {
                                ops.Add(PatchAdd($"/fields/{remainingHoursRef}", remainingH));
                                var oldRem = currentRemH.HasValue ? $"{currentRemH.Value:0.##}→" : "";
                                changes.Add($"HH Restante: {oldRem}{remainingH:0.##}h");
                            }
                        }

                        // HH Atual.
                        if (realizedHoursRef != null && task.CurrentHours.HasValue)
                        {
                            var currentH    = task.CurrentHours.Value;
                            var currentTfsH = ReadDouble(wi, realizedHoursRef);
                            if (currentTfsH == null || Math.Abs(currentTfsH.Value - currentH) > 0.0001)
                            {
                                ops.Add(PatchAdd($"/fields/{realizedHoursRef}", currentH));
                                var oldR = currentTfsH.HasValue ? $"{currentTfsH.Value:0.##}→" : "";
                                changes.Add($"HH Atual: {oldR}{currentH:0.##}h");
                            }
                        }
                    }

                    // ── Task: Original Estimate (Decimal) + Priority (Integer, default 5) ──
                    if (isTask)
                    {
                        // Original Estimate (Microsoft.VSTS.Scheduling.OriginalEstimate).
                        const string OriginalEstimateRef = "Microsoft.VSTS.Scheduling.OriginalEstimate";
                        const string CompletedWorkRef    = "Microsoft.VSTS.Scheduling.CompletedWork";

                        var taskHours = task.EstimatedHours ?? task.CurrentHours;
                        if (taskHours.HasValue)
                        {
                            var currentOrig = ReadDouble(wi, OriginalEstimateRef);
                            if (currentOrig == null || Math.Abs(currentOrig.Value - taskHours.Value) > 0.0001)
                            {
                                ops.Add(PatchAdd($"/fields/{OriginalEstimateRef}", taskHours.Value));
                                var oldH = currentOrig.HasValue ? $"{currentOrig.Value:0.##}→" : "";
                                changes.Add($"Original Estimate: {oldH}{taskHours.Value:0.##}h");
                            }
                        }

                        // Completed Work = HH Atual (CurrentHours).
                        if (task.CurrentHours.HasValue && task.CurrentHours.Value > 0)
                        {
                            var currentCompleted = ReadDouble(wi, CompletedWorkRef);
                            if (currentCompleted == null || Math.Abs(currentCompleted.Value - task.CurrentHours.Value) > 0.0001)
                            {
                                ops.Add(PatchAdd($"/fields/{CompletedWorkRef}", task.CurrentHours.Value));
                                var oldC = currentCompleted.HasValue ? $"{currentCompleted.Value:0.##}→" : "";
                                changes.Add($"Completed Work: {oldC}{task.CurrentHours.Value:0.##}h");
                            }
                        }

                        // Priority (Microsoft.VSTS.Common.Priority). DevOps aceita apenas 1–4.
                        var currentPriority = ReadDouble(wi, "Microsoft.VSTS.Common.Priority");
                        int rawPriority = task.Priority is > 0 ? task.Priority.Value : 2;
                        int desiredPriority = Math.Clamp(rawPriority, 1, 4);
                        if (currentPriority == null || (int)currentPriority.Value != desiredPriority)
                        {
                            ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Priority", desiredPriority));
                            changes.Add($"Priority: {desiredPriority}");
                        }
                        if (!task.Priority.HasValue && currentPriority.HasValue && currentPriority.Value > 0)
                            task.Priority = (int)currentPriority.Value;

                        // Fecha a Task no TFS quando 100% concluída e não estiver Closed nem New.
                        if (task.PercentComplete >= 100)
                        {
                            var currentState = task.TfsState?.Trim();
                            if (!string.Equals(currentState, "Closed", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(currentState, "New",    StringComparison.OrdinalIgnoreCase))
                            {
                                task.TfsState = "Closed";
                                changes.Add("State: → Closed (100%)");
                            }
                        }
                    }

                    // Ajuste automático de estado baseado no % de conclusão (Story, Feature e Epic).
                    if (isStoryLike)
                    {
                        if (task.PercentComplete >= 100 &&
                            !string.Equals(task.TfsState?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase))
                        {
                            task.TfsState = "Closed";
                        }
                        else if (task.PercentComplete < 100 &&
                                 string.Equals(task.TfsState?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase))
                        {
                            task.TfsState = "Active";
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(task.TfsState) &&
                        !string.Equals(task.TfsState.Trim(), wi.State?.Trim() ?? string.Empty, StringComparison.Ordinal))
                    {
                        ops.Add(PatchAdd("/fields/System.State", task.TfsState.Trim()));
                        changes.Add($"estado: {task.TfsState.Trim()}");
                    }

                    // Campos Custom DevOps: sincroniza valores que mudaram no NXProject → DevOps
                    if (task.CustomDevopsFieldValues.Count > 0
                        && options.TypeFieldMappings.TryGetValue(task.TfsType ?? "", out var cfgCustom)
                        && cfgCustom.CustomDevopsFields.Count > 0)
                    {
                        foreach (var fd in cfgCustom.CustomDevopsFields)
                        {
                            if (!task.CustomDevopsFieldValues.TryGetValue(fd.Field, out var desiredVal)
                                || string.IsNullOrWhiteSpace(desiredVal)) continue;
                            var currentVal = ReadString(wi, fd.Field) ?? "";
                            if (!string.Equals(desiredVal, currentVal, StringComparison.Ordinal))
                            {
                                ops.Add(PatchAdd($"/fields/{fd.Field}", desiredVal));
                                changes.Add($"{fd.Field}: {desiredVal}");
                            }
                        }
                    }

                    // Parent: reparenta SÓ se o pai mudou em relação ao que está no DevOps.
                    var (currentParent, relIndex) = FindParentRelation(wi);
                    bool reparent = desiredParent > 0 && desiredParent != currentParent;
                    var relationRemovals = new List<int>();
                    if (reparent && relIndex >= 0)
                        relationRemovals.Add(relIndex);

                    if (options.SyncPredecessorLinks && ShouldSyncPredecessors(task))
                    {
                        TryGetDesiredPredecessorTfsIds(task, tasksById, out var desiredPredecessors, out var invalidPredecessors);

                        // Avisa sobre IDs não resolvíveis, mas ainda assim sincroniza
                        // os válidos e remove os que saíram do cronograma.
                        if (invalidPredecessors.Count > 0)
                            report.LogWarning(
                                $"#{task.TfsId} ({task.Name}): predecessora(s) não resolvida(s) no cronograma ({string.Join(", ", invalidPredecessors)}) — ignoradas na sincronização.");

                        var currentPredecessorRelations = FindPredecessorRelations(wi);
                        var currentPredecessors = currentPredecessorRelations
                            .Select(p => p.id)
                            .ToHashSet();

                        if (!currentPredecessors.SetEquals(desiredPredecessors))
                        {
                            // Remove links que existem no TFS mas não estão mais no cronograma.
                            foreach (var predecessor in currentPredecessorRelations)
                            {
                                if (!desiredPredecessors.Contains(predecessor.id))
                                    relationRemovals.Add(predecessor.index);
                            }
                            // Adiciona links que estão no cronograma mas não existem no TFS.
                            foreach (var predecessorId in desiredPredecessors)
                            {
                                if (!currentPredecessors.Contains(predecessorId))
                                {
                                    var predOp = AddPredecessorRelation(orgBase, predecessorId);
                                    ops.Add(predOp);
                                    predecessorAddOps.Add(predOp);
                                }
                            }
                            changes.Add("predecessoras");
                        }
                    }

                    foreach (var index in relationRemovals.Distinct().OrderByDescending(i => i))
                        ops.Add(new { op = "remove", path = $"/relations/{index}" });

                    if (reparent)
                    {
                        ops.Add(new
                        {
                            op = "add",
                            path = "/relations/-",
                            value = new
                            {
                                rel = "System.LinkTypes.Hierarchy-Reverse",
                                url = $"{orgBase}/_apis/wit/workItems/{desiredParent}"
                            }
                        });
                        changes.Add($"pai→#{desiredParent}");
                    }

                    // Sem mudanças reais → pula sem incrementar versão.
                    if (ops.Count == 0)
                    {
                        task.HasBrokenPredecessorLink = false;
                        report.Skipped++;
                        continue;
                    }

                    // Há mudanças reais → incrementa versão de concorrência.
                    if (syncVersionRef != null)
                    {
                        var tfsVersion = (int)(ReadDouble(wi, syncVersionRef) ?? 0);
                        var newVersion = tfsVersion >= int.MaxValue ? 1 : tfsVersion + 1;
                        ops.Add(PatchAdd($"/fields/{syncVersionRef}", newVersion));
                        task.SyncVersion = newVersion;
                        task.HasSyncConflict = false;
                        changes.Add($"syncVer:{newVersion}");
                    }
                    if (syncNameRef != null)
                        ops.Add(PatchAdd($"/fields/{syncNameRef}", Environment.UserName));

                    // bypassRules=true garante que campos customizados (Perc_Aloc, Sync_version,
                    // Sync_Name identity) e itens fechados sejam gravados sem bloqueio de regras.
                    await PatchWorkItemAsync(orgBase, auth, task.TfsId.Value, ops, cancellationToken, bypassRules: true);
                    task.HasBrokenPredecessorLink = false;
                    report.Updated++;
                    report.LogSuccess($"{TaskSyncLabel(task)} ({task.Name ?? "(sem nome)"}): [{string.Join(", ", changes)}]");
                    if (reparent)
                    {
                        report.Reparented++;
                        task.TfsParentId = desiredParent;
                    }
                }
                catch (Exception ex)
                {
                    // 404 de acesso a link (work item predecessora excluído ou sem permissão):
                    // retry sem as ops de predecessora e marca a task no cronograma.
                    var msg = ex.Message;
                    var linkMatch = System.Text.RegularExpressions.Regex.Match(
                        msg, @"Work item (\d+) does not exist", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (linkMatch.Success && msg.Contains("404") && msg.Contains("WorkItemLink")
                        && predecessorAddOps.Count > 0 && task.TfsId.HasValue)
                    {
                        var missingId   = int.Parse(linkMatch.Groups[1].Value);
                        var missingName = tasks.FirstOrDefault(t => t.TfsId == missingId)?.Name ?? $"#{missingId}";
                        report.LogWarning(
                            $"{TaskSyncLabel(task)} ({task.Name}): predecessora \"{missingName}\" (#{missingId}) não existe mais no DevOps — link marcado no cronograma.");

                        // Retry sem ops de predecessora (demais campos ainda precisam ser sincronizados).
                        var opsWithoutPredecessors = ops.Except(predecessorAddOps).ToList();
                        try
                        {
                            if (opsWithoutPredecessors.Count > 0)
                            {
                                await PatchWorkItemAsync(orgBase, auth, task.TfsId.Value, opsWithoutPredecessors, cancellationToken, bypassRules: true);
                                report.Updated++;
                                report.LogSuccess($"{TaskSyncLabel(task)} ({task.Name}): sincronizado sem predecessora quebrada.");
                            }
                        }
                        catch (Exception retryEx)
                        {
                            report.LogError($"{TaskSyncLabel(task)} ({task.Name}): erro no retry — {retryEx.Message}");
                        }

                        task.HasBrokenPredecessorLink = true;
                    }
                    else
                    {
                        report.LogError($"{TaskSyncLabel(task)} ({task.Name}): erro — {msg}");
                    }
                }
            }

            // Aviso final: Features/Stories que ficaram sem sprint associada.
            foreach (var task in tasks)
                if (IsFeatureOrStory(task) && string.IsNullOrWhiteSpace(task.TfsIterationPath))
                    report.WithoutSprint.Add(task.Name);

            return report;
        }

        private static bool IsFeatureOrStory(ProjectTask task) =>
            string.Equals(task.TfsType, "Feature", StringComparison.OrdinalIgnoreCase) ||
            IsStoryType(task.TfsType);

        /// <summary>
        /// Recalcula o StackRank desejado para refletir a ordem do NXProject, por
        /// grupo de irmãos: itens já em ordem crescente mantêm o rank (idempotente);
        /// itens movidos ou novos (id=0) recebem um rank encaixado entre o irmão
        /// anterior e o próximo (ou anterior + passo). Só itens com vínculo DevOps
        /// participam. Muta task.TfsStackRank (que vira o valor a sincronizar).
        /// </summary>
        private static void ApplyDesiredStackRanks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> siblings)
        {
            double? prev = null;
            for (int i = 0; i < siblings.Count; i++)
            {
                var child = siblings[i];
                if (child.TfsId.HasValue)
                {
                    double? cur = child.TfsId.Value > 0 ? child.TfsStackRank : null;
                    double desired;
                    if (cur.HasValue && (!prev.HasValue || cur.Value > prev.Value))
                    {
                        desired = cur.Value; // já em ordem -> mantém
                    }
                    else
                    {
                        // Próximo irmão com rank maior que o anterior (limite superior).
                        double? next = null;
                        for (int j = i + 1; j < siblings.Count; j++)
                        {
                            var s = siblings[j];
                            if (s.TfsId is > 0 && s.TfsStackRank.HasValue &&
                                (!prev.HasValue || s.TfsStackRank.Value > prev.Value))
                            { next = s.TfsStackRank.Value; break; }
                        }

                        if (prev.HasValue && next.HasValue && next.Value - prev.Value > 0.0)
                            desired = prev.Value + (next.Value - prev.Value) / 2.0; // encaixa no meio
                        else if (prev.HasValue)
                            desired = prev.Value + 1000.0;                          // encadeia após o anterior
                        else if (next.HasValue)
                            desired = next.Value - 1000.0;                          // antes do primeiro com rank
                        else
                            desired = 1000000000.0;                                 // grupo sem nenhum rank
                    }

                    child.TfsStackRank = desired;
                    prev = desired;
                }

                ApplyDesiredStackRanks(child.Children);
            }
        }

        // Inclui tarefas vinculadas (TfsId definido, inclusive 0 = criar) e tarefas sem
        // vínculo ainda (TfsId null) cujo pai já tem TfsId — serão criadas automaticamente.
        /// <summary>
        /// Resolve o pai DevOps de uma task subindo a árvore até encontrar um ancestral com TfsId > 0.
        /// Se nenhum ancestral tiver TfsId, usa rootWorkItemId como pai.
        /// </summary>
        private static string TaskSyncLabel(ProjectTask task)
        {
            var type = task.TfsType?.Trim() switch
            {
                "Epic"                              => "Epic",
                "Feature"                           => "Feature",
                "User Story" or "Story"             => "Story",
                "Task"                              => "Task",
                { } t when !string.IsNullOrEmpty(t) => t,
                _                                   => "Item"
            };
            var id = task.TfsId is > 0 ? $"#{task.TfsId}" : $"I:{task.Id}";
            return $"{type} - {id}";
        }

        private static int ResolveDesiredParent(ProjectTask task, int rootWorkItemId)
        {
            var p = task.Parent;
            while (p != null)
            {
                if (p.TfsId is > 0) return p.TfsId.Value;
                // Pai com TfsId=0 pode ter acabado de ser criado neste loop — valor já atualizado
                p = p.Parent;
            }
            return rootWorkItemId;
        }

        private static void CollectLinkedTasks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks,
            List<ProjectTask> acc,
            bool parentIsLinked = false)
        {
            foreach (var t in tasks)
            {
                // Inclui se: tem TfsId (mesmo = 0 = "a criar"), ou se o pai tem vínculo,
                // ou se algum descendente tem TfsId (para garantir que o pai seja criado primeiro).
                bool isNoDevOps = IsNoDevOpsType(t.TfsType)
                                  || t.TfsId < 0;
                bool hasLinkedDescendant = !isNoDevOps && HasLinkedDescendant(t.Children);
                var include = (!isNoDevOps && (t.TfsId.HasValue || t.IsPendingTfsCreate)) || parentIsLinked || hasLinkedDescendant;
                if (include)
                    acc.Add(t);
                CollectLinkedTasks(t.Children, acc, include || (t.TfsId is > 0));
            }
        }

        private static bool HasLinkedDescendant(System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                if ((t.TfsId.HasValue || t.IsPendingTfsCreate) && !IsNoDevOpsType(t.TfsType))
                    return true;
                if (HasLinkedDescendant(t.Children)) return true;
            }
            return false;
        }

        private static List<int> ApplyTfsPredecessors(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks,
            List<(int predecessor, int successor)> depLinks)
        {
            var externalIds = new List<int>();
            if (depLinks.Count == 0)
                return externalIds;

            var flatTasks = new List<ProjectTask>();
            CollectAllTasks(tasks, flatTasks);

            // Índice TfsId → tarefa interna.
            var taskByTfsId = flatTasks
                .Where(t => t.TfsId.HasValue && t.TfsId.Value > 0)
                .GroupBy(t => t.TfsId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            // Conjunto de todos os TfsIds do projeto para detectar externos.
            var projectTfsIds = new HashSet<int>(taskByTfsId.Keys);

            // Agrupa links por successor.
            var linksBySuccessor = depLinks
                .GroupBy(l => l.successor)
                .ToDictionary(g => g.Key, g => g.Select(l => l.predecessor).Distinct().ToList());

            foreach (var task in flatTasks)
            {
                if (!task.TfsId.HasValue || task.TfsId.Value <= 0)
                    continue;
                if (!linksBySuccessor.TryGetValue(task.TfsId.Value, out var predecessorTfsIds))
                    continue;

                task.PredecessorIds.Clear();
                foreach (var predTfsId in predecessorTfsIds)
                {
                    if (taskByTfsId.TryGetValue(predTfsId, out var predecessor))
                    {
                        // Predecessora está no projeto: armazena Id interno (estável).
                        task.PredecessorIds.Add(predecessor.Id);
                    }
                    else
                    {
                        // Predecessora externa ao projeto: armazena o TfsId diretamente.
                        task.PredecessorIds.Add(predTfsId);
                        if (!externalIds.Contains(predTfsId))
                            externalIds.Add(predTfsId);
                    }
                }
            }
            return externalIds;
        }

        private static void CollectAllTasks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> tasks, List<ProjectTask> acc)
        {
            foreach (var task in tasks)
            {
                acc.Add(task);
                CollectAllTasks(task.Children, acc);
            }
        }

        private static async Task<Dictionary<int, WorkItem>> FetchWorkItemsByIdsAsync(
            string orgBase, string teamProject,
            AuthenticationHeaderValue authHeader,
            IEnumerable<int> ids,
            CancellationToken ct)
        {
            var result = new Dictionary<int, WorkItem>();
            var idList = ids.Distinct().ToList();
            if (idList.Count == 0) return result;

            // API suporta até 200 IDs por chamada.
            const int batchSize = 200;
            for (int i = 0; i < idList.Count; i += batchSize)
            {
                var batch = idList.Skip(i).Take(batchSize);
                var idsParam = string.Join(",", batch);
                var url = $"{orgBase}/{Uri.EscapeDataString(teamProject)}/_apis/wit/workitems?ids={idsParam}&$expand=none&{ApiVersion}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = authHeader;
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                try
                {
                    using var doc = await SendAsync(req, ct);
                    if (doc.RootElement.TryGetProperty("value", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (!el.TryGetProperty("fields", out var fields)) continue;
                            var wi = new WorkItem
                            {
                                Id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0,
                                Fields = fields
                            };
                            if (fields.TryGetProperty("System.Title", out var t)) wi.Title = t.GetString() ?? "";
                            if (fields.TryGetProperty("System.WorkItemType", out var wt)) wi.WorkItemType = wt.GetString() ?? "";
                            if (fields.TryGetProperty("System.State", out var st)) wi.State = st.GetString() ?? "";
                            if (wi.Id == 0) continue;
                            result[wi.Id] = wi;
                        }
                    }
                }
                catch { /* ignora erros de itens inacessíveis */ }
            }
            return result;
        }

        private static bool ShouldSyncPredecessors(ProjectTask task) =>
            task.Children.Count == 0 &&
            (IsStoryType(task.TfsType) || IsTaskType(task.TfsType) ||
             IsEpicOrFeatureType(task.TfsType));

        private static bool IsStoryTask(ProjectTask task) =>
            IsStoryType(task.TfsType);

        private static bool IsNoDevOpsType(string? type)
        {
            var normalized = (type ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return string.Equals(normalized, "NoDevOps", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDevOpsMilestoneType(string? type)
        {
            var normalized = (type ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
            return string.Equals(normalized, "MarcoDevops", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTaskType(string? type) =>
            string.Equals(type, "Task", StringComparison.OrdinalIgnoreCase) ||
            IsDevOpsMilestoneType(type);

        private static bool IsEpicOrFeatureType(string? type) =>
            string.Equals(type, "Epic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase);

        private static HashSet<int> GetDesiredPredecessorTfsIds(
            ProjectTask task,
            Dictionary<int, ProjectTask> tasksById)
        {
            TryGetDesiredPredecessorTfsIds(task, tasksById, out var desired, out _);
            return desired;
        }

        private static bool TryGetDesiredPredecessorTfsIds(
            ProjectTask task,
            Dictionary<int, ProjectTask> tasksById,
            out HashSet<int> desired,
            out List<int> invalidPredecessors)
        {
            desired = new HashSet<int>();
            invalidPredecessors = new List<int>();

            // Índice auxiliar: TfsId → tarefa, para detectar IDs armazenados como TfsId.
            var tasksByTfsId = tasksById.Values
                .Where(t => t.TfsId.HasValue && t.TfsId.Value > 0)
                .GroupBy(t => t.TfsId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var storedId in task.PredecessorIds)
            {
                // IDs negativos são internos de tarefas "No DevOps" — nunca enviar ao DevOps.
                if (storedId < 0)
                    continue;

                // 1. Tarefa interna (Id interno) com TfsId resolvível.
                if (tasksById.TryGetValue(storedId, out var predecessor) &&
                    ShouldSyncPredecessors(predecessor) &&
                    predecessor.TfsId.HasValue && predecessor.TfsId.Value > 0)
                {
                    desired.Add(predecessor.TfsId.Value);
                    continue;
                }

                // 2. O valor armazenado é o próprio TfsId (tarefa de outro escopo ou
                //    salva antes da resolução de IDs). Usa diretamente se > 0.
                if (storedId > 0 && !tasksById.ContainsKey(storedId))
                {
                    // Pode ser TfsId de tarefa interna (já resolvida acima) ou externa.
                    // Se bate com um TfsId do projeto, usa a tarefa interna.
                    if (tasksByTfsId.TryGetValue(storedId, out var byTfs) &&
                        ShouldSyncPredecessors(byTfs))
                    {
                        desired.Add(storedId);
                        continue;
                    }
                    // Externo: aceita diretamente como TfsId do DevOps.
                    desired.Add(storedId);
                    continue;
                }

                invalidPredecessors.Add(storedId);
            }

            return invalidPredecessors.Count == 0;
        }

        private static object AddPredecessorRelation(string orgBase, int predecessorId) =>
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Dependency-Reverse",
                    url = $"{orgBase}/_apis/wit/workItems/{predecessorId}"
                }
            };

        // Retorna apenas o e-mail do primeiro recurso alocado.
        // Usamos só e-mail (uniqueName no Azure DevOps) para evitar ambiguidade de
        // nomes de exibição que o TFS não consegue resolver e retornaria erro 400,
        // derrubando todo o PATCH — inclusive as horas.
        private static string? GetDesiredAssigneeEmail(ProjectTask task)
        {
            var resource = task.Resources
                .Select(r => r.Resource)
                .FirstOrDefault(r => r != null);

            return string.IsNullOrWhiteSpace(resource?.Email) ? null : resource.Email.Trim();
        }

        private static bool AssigneeEquals(WorkItem wi, string desiredEmail)
        {
            var desired = desiredEmail.Trim();
            return string.Equals(desired, wi.AssigneeEmail?.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(desired, wi.Assignee?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // Horas a enviar para o TFS.
        // Ordem de prioridade:
        //   1. task.EstimatedHours — valor que o usuário editou explicitamente
        //   2. Soma de resource.EstimatedHours — alocações com HH manual
        //   3. Soma de GetAssignmentHours — derivado de duração × %alocação
        // Retorna null só se a tarefa for milestone (duração 0) ou não tiver recurso e duração zero.
        private static double? GetSyncHours(ProjectTask task)
        {
            if (task.EstimatedHours.HasValue && task.EstimatedHours.Value > 0)
                return task.EstimatedHours.Value;

            var assignmentExplicit = task.Resources
                .Where(r => r.EstimatedHours.HasValue && r.EstimatedHours.Value > 0)
                .Sum(r => r.EstimatedHours!.Value);
            if (assignmentExplicit > 0)
                return assignmentExplicit;

            // Fallback: duração * % de alocação por recurso.
            if (task.Resources.Count > 0)
            {
                var durationBased = task.Resources
                    .Sum(r => TaskScheduleService.GetAssignmentHours(task, r));
                if (durationBased > 0)
                    return durationBased;
            }
            else if (task.DurationHours > 0)
            {
                // Sem recurso: usa a duração diretamente.
                return task.DurationHours;
            }

            return null;
        }

        private static List<object> BuildCreateOps(
            ProjectTask task, int parentId, string orgBase,
            string? hoursRef, string? startRef, string? finishRef,
            Dictionary<int, ProjectTask> tasksById,
            bool syncPredecessorLinks = true,
            string? percAlocRef = null,
            string? originalHoursRef = null,
            string? remainingHoursRef = null,
            string? realizedHoursRef = null,
            IEnumerable<ExtraWorkItemField>? extraFields = null,
            IReadOnlyList<ClassificationFieldDef>? classificationFields = null)
        {
            bool isTaskCreate        = IsTaskType(task.TfsType);
            bool isEpicOrFeatureCreate = IsEpicOrFeatureType(task.TfsType);

            var ops = new List<object>
            {
                PatchAdd("/fields/System.Title", task.Name ?? "Novo item")
            };

            // Campos de classificação (picklist obrigatórios na criação, ex.: Custom.Type).
            if (classificationFields != null)
            {
                bool first = true;
                foreach (var fd in classificationFields.Where(f => !string.IsNullOrWhiteSpace(f.Field)))
                {
                    // Valor: dict por campo → TfsClassification (só para o primeiro campo, compat) → TfsType
                    string classValue;
                    if (task.CustomDevopsFieldValues.TryGetValue(fd.Field, out var dictVal) && !string.IsNullOrWhiteSpace(dictVal))
                        classValue = dictVal;
                    else if (first && !string.IsNullOrWhiteSpace(task.TfsClassification))
                        classValue = task.TfsClassification;
                    else
                        classValue = task.TfsType ?? "";
                    ops.Add(PatchAdd($"/fields/{fd.Field}", classValue));
                    first = false;
                }
            }

            // Campos fixos obrigatórios do processo do cliente (ex.: Custom.Type).
            if (extraFields != null)
                foreach (var f in extraFields)
                    if (!string.IsNullOrWhiteSpace(f.Ref) && f.Value != null)
                        ops.Add(PatchAdd($"/fields/{f.Ref}", f.Value));

            if (!isTaskCreate)
            {
                if (!string.IsNullOrWhiteSpace(task.Description))
                    ops.Add(PatchAdd("/fields/System.Description", task.Description!));

                if (!string.IsNullOrWhiteSpace(task.Tags))
                    ops.Add(PatchAdd("/fields/System.Tags", NormalizeTagsForWrite(task.Tags)));

                if (!string.IsNullOrWhiteSpace(task.TfsState))
                    ops.Add(PatchAdd("/fields/System.State", task.TfsState.Trim()));

                if (task.TfsStackRank.HasValue)
                    ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.StackRank", task.TfsStackRank.Value));

                var desiredHours = GetSyncHours(task);
                if (hoursRef != null && desiredHours.HasValue && hoursRef != originalHoursRef)
                    ops.Add(PatchAdd($"/fields/{hoursRef}", desiredHours.Value));

                if (originalHoursRef != null && task.OriginalEstimatedHours is > 0 && task.PercentComplete < 0.0001)
                    ops.Add(PatchAdd($"/fields/{originalHoursRef}", task.OriginalEstimatedHours.Value));

                var desiredAssignee = GetDesiredAssigneeEmail(task);
                if (!string.IsNullOrWhiteSpace(desiredAssignee))
                    ops.Add(PatchAdd("/fields/System.AssignedTo", desiredAssignee));

                if (percAlocRef != null)
                {
                    var primaryAloc = task.Resources.Count > 0 ? task.Resources[0].AllocationPercent : 100.0;
                    ops.Add(PatchAdd($"/fields/{percAlocRef}", (int)Math.Round(primaryAloc)));
                }

                if (startRef != null && task.Start > DateTime.MinValue.AddYears(1))
                    ops.Add(PatchAdd($"/fields/{startRef}", FormatDateForTfs(task.Start)));

                if (finishRef != null && IsClosedState(task.TfsState) && task.Finish > DateTime.MinValue.AddYears(1))
                    ops.Add(PatchAdd($"/fields/{finishRef}", FormatDateForTfs(task.Finish)));
            }
            else
            {
                // Task: Original Estimate (Decimal) + Priority=5 na criação.
                var taskHours = task.EstimatedHours ?? task.CurrentHours;
                if (taskHours.HasValue)
                    ops.Add(PatchAdd("/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", taskHours.Value));
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Priority", 2));
                if (IsDevOpsMilestoneType(task.TfsType))
                    ops.Add(PatchAdd("/fields/System.Tags", AddTag(task.Tags, "MARCO-PROJECT")));
            }

            // HH Restante e HH Atual: apenas para Story/Feature/Epic.
            if (!isTaskCreate)
            {
                if (remainingHoursRef != null && task.EstimatedHours is >= 0)
                    ops.Add(PatchAdd($"/fields/{remainingHoursRef}", task.EstimatedHours.Value));

                if (realizedHoursRef != null && task.CurrentHours is > 0)
                    ops.Add(PatchAdd($"/fields/{realizedHoursRef}", task.CurrentHours.Value));
            }

            ops.Add(new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{orgBase}/_apis/wit/workItems/{parentId}"
                }
            });

            if (syncPredecessorLinks && ShouldSyncPredecessors(task))
            {
                foreach (var predecessorId in GetDesiredPredecessorTfsIds(task, tasksById))
                    ops.Add(AddPredecessorRelation(orgBase, predecessorId));
            }

            return ops;
        }

        // Mapeia o tipo do NXProject para o nome do work item type no DevOps.
        private static string MapWorkItemType(string type) =>
            string.Equals(type, "Story", StringComparison.OrdinalIgnoreCase) ? "User Story" :
            IsDevOpsMilestoneType(type) ? "Task" :
            type;

        private static async Task<WorkItem?> FindExistingChildByTitleAndTypeAsync(
            string orgBase,
            string project,
            AuthenticationHeaderValue auth,
            int parentId,
            string type,
            string? title,
            CancellationToken ct)
        {
            if (parentId <= 0 || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(title))
                return null;

            var edges = await LoadDirectHierarchyEdgesAsync(orgBase, project, auth, parentId, ct);
            var childIds = edges
                .Where(e => e.parent == parentId)
                .Select(e => e.child)
                .Distinct()
                .ToList();
            if (childIds.Count == 0)
                return null;

            var items = await LoadWorkItemsAsync(
                orgBase,
                auth,
                childIds,
                ["System.Id", "System.Title", "System.WorkItemType", "System.State"],
                ct,
                expandRelations: true);

            var mappedType = MapWorkItemType(type);
            var normalizedTitle = NormalizeTitleForMatch(title);

            return items.Values.FirstOrDefault(item =>
                string.Equals(MapWorkItemType(item.WorkItemType), mappedType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeTitleForMatch(item.Title), normalizedTitle, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeTitleForMatch(string? value) =>
            Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

        private static async Task<int> CreateWorkItemAsync(
            string orgBase, AuthenticationHeaderValue auth, string project, string type,
            List<object> ops, CancellationToken ct)
        {
            var typeName = MapWorkItemType(type);
            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/{Uri.EscapeDataString("$" + typeName)}?{ApiVersion}";
            var body = JsonSerializer.Serialize(ops);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json")
            };
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var doc = await SendAsync(req, ct);
            return doc.RootElement.GetProperty("id").GetInt32();
        }

        private static object PatchAdd(string path, object? value) =>
            new { op = "add", path, value = value ?? string.Empty };

        private static object PatchRemove(string path) =>
            new { op = "remove", path };

        private static string[] SplitTags(string? tags) =>
            string.IsNullOrWhiteSpace(tags)
                ? Array.Empty<string>()
                : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Compara dois conjuntos de tags ignorando ordem, espaços e caixa.</summary>
        private static bool TagsEqual(string? a, string? b)
        {
            var sa = new HashSet<string>(SplitTags(a), StringComparer.OrdinalIgnoreCase);
            var sb = new HashSet<string>(SplitTags(b), StringComparer.OrdinalIgnoreCase);
            return sa.SetEquals(sb);
        }

        /// <summary>Normaliza tags para o formato aceito pelo DevOps ("tag1; tag2").</summary>
        private static string NormalizeTagsForWrite(string? tags) =>
            string.Join("; ", SplitTags(tags));

        private static string AddTag(string? tags, string tag)
        {
            var parts = SplitTags(tags).ToList();
            if (!parts.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                parts.Add(tag);
            return string.Join("; ", parts);
        }

        public static bool IsClosedStateName(string? state) => IsClosedState(state);

        private static bool IsClosedState(string? state) =>
            state?.Trim().ToLowerInvariant() switch
            {
                "closed" or "resolved" or "done" or "completed" => true,
                _ => false
            };

        /// <summary>Formata a data como meia-noite local em UTC (ex.: 04/05 BRT -> 2026-05-04T03:00:00Z),
        /// para casar com o formato que o DevOps já usa nesses campos.</summary>
        private static string FormatDateForTfs(DateTime date)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(date.Date);
            var local = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, offset);
            return local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        private static async Task PatchWorkItemAsync(
            string orgBase, AuthenticationHeaderValue auth, int id, List<object> ops, CancellationToken ct,
            bool bypassRules = false)
        {
            var url = bypassRules
                ? $"{orgBase}/_apis/wit/workitems/{id}?{ApiVersion}&bypassRules=true"
                : $"{orgBase}/_apis/wit/workitems/{id}?{ApiVersion}";
            var body = JsonSerializer.Serialize(ops);

            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json")
            };
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var doc = await SendAsync(req, ct);
        }

        // ── Construcao da hierarquia ─────────────────────────────────────────

        private sealed class BuildContext
        {
            public Dictionary<int, WorkItem> Items = new();
            public Dictionary<int, List<int>> ChildrenByParent = new();
            public string? HoursRef;
            public string? RemainingHoursRef;
            public string? OriginalHoursRef;
            public string? StartRef;
            public string? FinishRef;
            public string? PercAlocRef;
            public string? PercConclusaoRef;
            public string? SyncVersionRef;
            public string? TipoCentroCustoRef;
            public string? CurrentHoursRef;
            /// <summary>Campos Custom DevOps por tipo DevOps (ex: "Feature" → [Custom.Type, ...]).</summary>
            public Dictionary<string, List<ClassificationFieldDef>> CustomDevopsFieldsByType = new(StringComparer.OrdinalIgnoreCase);
            public double HoursPerDay = 8.0;
            public DateTime ProjectStart;

            // Data de inicio de cada sprint (System.IterationPath -> startDate).
            public Dictionary<string, DateTime> SprintStartByPath = new();

            // Pai (DevOps id) de cada work item, para gravar TfsParentId.
            public Dictionary<int, int> ParentByChild = new();
            public Project Project = null!;
            public Dictionary<string, Resource> ResourcesByKey = new(StringComparer.OrdinalIgnoreCase);

            public int? GetParent(int devOpsId) =>
                ParentByChild.TryGetValue(devOpsId, out var p) ? p : null;

            // Cursor de encadeamento por FILA = (responsavel, sprint). Cada fila
            // comeca na data de inicio da sprint; pessoas diferentes (ou sprints
            // diferentes) correm em paralelo. Assim, mover a Story para outra
            // sprint a faz escorregar para a janela da nova sprint.
            public Dictionary<string, DateTime> CursorByLane = new();
            public string FixedStartTagName = "DT-INI-NEG";
            public ImportReport Report = new();

            public DateTime? GetSprintStart(string? iterationPath)
            {
                if (string.IsNullOrWhiteSpace(iterationPath)) return null;
                return SprintStartByPath.TryGetValue(iterationPath.Trim(), out var d) ? d : null;
            }
        }

        /// <summary>
        /// Constroi recursivamente um ramo da arvore:
        /// nivel 0 = Epic, 1 = Feature, 2 = Story. Tasks (e abaixo) sao ignoradas.
        /// </summary>
        private static ProjectTask? BuildBranch(BuildContext ctx, int id, int level)
        {
            if (!ctx.Items.TryGetValue(id, out var item))
                return null;

            // Story/User Story vira atividade de cronograma independentemente do
            // nivel em que apareceu na hierarquia do DevOps. Isso evita perder
            // Stories em estado New quando o backlog nao esta exatamente em
            // Epic -> Feature -> Story.
            if (IsStoryType(item.WorkItemType))
                return BuildStory(ctx, item, Math.Max(level, 2));

            // Nivel 2+ sem ser Story: nao descemos para Task nem tipos auxiliares.
            if (level >= 2)
            {
                if (IsType(item, "Task"))
                    return null;
                return null;
            }

            // Bloqueio derivado: Task filha direta com tag Block (só visão).
            bool summaryBlocked = ctx.ChildrenByParent.TryGetValue(item.Id, out var directChildren) &&
                directChildren.Any(cid => ctx.Items.TryGetValue(cid, out var c) &&
                    IsType(c, "Task") && HasBlockTag(c.Tags));

            var task = new ProjectTask
            {
                Id = id,
                Name = item.Title,
                Level = level,
                IsSummary = true,
                PercentComplete = StateToPercent(item.State),
                TfsId = item.Id,
                TfsParentId = ctx.GetParent(item.Id),
                TfsType = item.WorkItemType,
                TfsState = item.State,
                Description = item.Description,
                Tags = item.Tags,
                BlockedByChild = summaryBlocked,
                TfsStackRank = item.StackRank,
                TfsIterationPath = item.IterationPath,
                Justificativa = ParseJustificativa(item.Description),
                TipoCentroCusto = ReadTipoCentroCusto(item, ctx.TipoCentroCustoRef)
            };

            // Lê valores dos campos Custom DevOps para Epic/Feature
            if (ctx.CustomDevopsFieldsByType.TryGetValue(item.WorkItemType ?? "", out var summaryCustomFields)
                || ctx.CustomDevopsFieldsByType.TryGetValue("*", out summaryCustomFields))
            {
                foreach (var fd in summaryCustomFields)
                {
                    var val = ReadString(item, fd.Field);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        task.CustomDevopsFieldValues[fd.Field] = val;
                        if (string.IsNullOrWhiteSpace(task.TfsClassification))
                            task.TfsClassification = val;
                    }
                }
            }

            AssignResource(ctx, task, item);

            foreach (var childId in OrderedChildren(ctx.ChildrenByParent, id, ctx.Items))
            {
                if (ctx.Items.TryGetValue(childId, out var child) && IsType(child, "Task"))
                    continue;

                var childTask = BuildBranch(ctx, childId, level + 1);
                if (childTask != null)
                {
                    childTask.Parent = task;
                    task.Children.Add(childTask);
                }
            }

            if (task.Children.Count > 0)
            {
                task.RecalcSummary();
            }
            else
            {
                // Epic/Feature sem Story: mantem como resumo vazio com datas neutras.
                task.Start = ctx.ProjectStart;
                task.Finish = ctx.ProjectStart;
            }

            return task;
        }

        private static ProjectTask BuildStory(BuildContext ctx, WorkItem item, int level)
        {
            // HH Restante tem prioridade; fallback para HoursRef (campo de esforço geral).
            var remainingHours = ctx.RemainingHoursRef != null ? ReadDouble(item, ctx.RemainingHoursRef) : null;
            var effortHours = ReadDouble(item, ctx.HoursRef);
            var hours = remainingHours ?? effortHours;
            var explicitStart = ReadDate(item, ctx.StartRef);
            var explicitFinish = ReadDate(item, ctx.FinishRef);
            // HH Atual lido diretamente para usar no cálculo de duração total.
            var currentHoursRaw = ctx.CurrentHoursRef != null && ReadDouble(item, ctx.CurrentHoursRef) is { } rh2 && rh2 > 0 ? rh2 : (double?)null;
            var percAlocRaw = ctx.PercAlocRef != null ? ReadDouble(item, ctx.PercAlocRef) : null;
            var allocationFactor = (percAlocRaw is > 0 and <= 100 ? percAlocRaw.Value : 100.0) / 100.0;

            // HH ausente/vazia -> 1 dia util. Milestone real exige duração total 0:
            // HH Atual + HH Restante == 0. Uma atividade concluída costuma ter
            // HH Restante = 0, mas HH Atual > 0, e não deve virar milestone.
            var totalRawHours = (currentHoursRaw ?? 0) + (hours ?? 0);
            bool isMilestone = hours.HasValue && totalRawHours <= 0.0001;
            int workDays = hours.HasValue && hours.Value > 0
                ? Math.Max(1, (int)Math.Ceiling(hours.Value / ctx.HoursPerDay))
                : 1;

            var assignee = string.IsNullOrWhiteSpace(item.Assignee) ? "(sem responsável)" : item.Assignee;

            // Bloqueio derivado: se qualquer Task filha tem a tag Block, a Story
            // aparece bloqueada (só visão — nunca sincronizado de volta).
            bool blockedByChild = false;
            if (ctx.ChildrenByParent.TryGetValue(item.Id, out var taskChildren))
                blockedByChild = taskChildren.Any(cid =>
                    ctx.Items.TryGetValue(cid, out var c) && HasBlockTag(c.Tags));

            // Story fechada/resolvida com Task filha ainda aberta → corrige estado para Active e loga.
            bool stateFixedToActive = false;
            if (IsCompletedState(item.State) && ctx.ChildrenByParent.TryGetValue(item.Id, out var childTaskIds))
            {
                bool hasOpenTask = childTaskIds.Any(cid =>
                    ctx.Items.TryGetValue(cid, out var c) &&
                    IsType(c, "Task") &&
                    IsOpenState(c.State));
                if (hasOpenTask)
                {
                    stateFixedToActive = true;
                    ctx.Report.StoriesStateFixed++;
                    ctx.Report.LogInfo(
                        $"[ESTADO CORRIGIDO] Story #{item.Id} \"{item.Title}\" estava {item.State} mas tem Tasks em aberto → ajustado para Active.");
                }
            }

            // Sem Data_Inicio, a Story ancora no inicio da SPRINT dela. A fila e
            // por (pessoa, sprint): a 1a Story da fila comeca na data de inicio da
            // sprint e as seguintes encadeiam; ao mover a Story para outra sprint,
            // ela cai em outra fila e escorrega para a janela da nova sprint.
            var sprintStart = ctx.GetSprintStart(item.IterationPath) ?? ctx.ProjectStart;
            var laneKey = assignee + " @@ " + (item.IterationPath ?? string.Empty);

            DateTime baseStart = ctx.CursorByLane.TryGetValue(laneKey, out var cursor)
                ? cursor
                : sprintStart;

            bool hasFixedTag = GetFixedStartTagAliases(ctx.FixedStartTagName)
                .Any(tag => HasTag(item.Tags, tag));
            DateTime start = (hasFixedTag && explicitStart != null) ? explicitStart.Value : baseStart;
            var durationHours = hours.HasValue
                ? Math.Max(0.0, hours.Value)
                : ctx.HoursPerDay > 0
                    ? ctx.HoursPerDay
                    : ProjectCalendarService.WorkingHoursPerDay;

            // Duração total = HH Atual + HH Restante quando HH Atual disponível.
            var totalDurationHours = currentHoursRaw is > 0
                ? currentHoursRaw.Value + durationHours
                : durationHours;

            DateTime finish = isMilestone
                ? start
                : (explicitFinish ?? ProjectCalendarService.AddWorkingHours(start, totalDurationHours));
            if (finish < start) finish = isMilestone ? start : ProjectCalendarService.AddWorkingHours(start, totalDurationHours);

            // Avanca a fila (pessoa, sprint) — SO para frente. Uma Story com data
            // explicita anterior nao pode puxar o cursor para tras (senao as
            // proximas se sobreporiam).
            ctx.CursorByLane[laneKey] = finish > baseStart ? finish : baseStart;

            var effectiveState = stateFixedToActive ? "Active" : item.State;
            var percentComplete =
                ctx.PercConclusaoRef != null && ReadDouble(item, ctx.PercConclusaoRef) is { } pc && pc >= 0 && pc <= 100
                    ? pc
                    : StateToPercent(effectiveState);

            var task = new ProjectTask
            {
                Id = item.Id,
                Name = item.Title,
                Level = level,
                IsSummary = false,
                IsMilestone = isMilestone,
                Start = start,
                Finish = finish,
                EstimatedHours = hours,
                OriginalEstimatedHours = ReadDouble(item, ctx.OriginalHoursRef) is { } origH && origH > 0 ? origH : null,
                PercentComplete = percentComplete,
                TfsId = item.Id,
                TfsParentId = ctx.GetParent(item.Id),
                TfsType = item.WorkItemType,
                TfsState = effectiveState,
                Description = item.Description,
                Tags = item.Tags,
                BlockedByChild = blockedByChild,
                TfsStackRank = item.StackRank,
                TfsIterationPath = item.IterationPath,
                StartFixed = hasFixedTag,
                FinishFixed = false,
                Justificativa = ParseJustificativa(item.Description),
                TipoCentroCusto = ReadTipoCentroCusto(item, ctx.TipoCentroCustoRef),
                CurrentHours  = ctx.CurrentHoursRef  != null && ReadDouble(item, ctx.CurrentHoursRef)  is { } rh && rh > 0 ? rh : null,
                SyncVersion = ctx.SyncVersionRef != null ? (int?)ReadDouble(item, ctx.SyncVersionRef).GetValueOrDefault(0) : null,
                HasSyncConflict = false,
                Notes = $"TFS #{item.Id} · {item.WorkItemType} · {effectiveState}"
                    + (string.IsNullOrWhiteSpace(item.Assignee) ? "" : $" · {item.Assignee}")
            };

            if (!(remainingHours is > 0))
            {
                var plannedHours =
                    task.OriginalEstimatedHours is > 0 ? task.OriginalEstimatedHours.Value :
                    effortHours is > 0 ? effortHours.Value :
                    totalDurationHours > 0 ? totalDurationHours :
                    ProjectCalendarService.CountWorkingHours(task.Start, task.Finish);
                ScheduleHoursService.ApplyMissingProgressHours(task, plannedHours);
            }

            // Lê valores dos campos Custom DevOps mapeados para este tipo
            if (ctx.CustomDevopsFieldsByType.TryGetValue(item.WorkItemType ?? "", out var customFields)
                || ctx.CustomDevopsFieldsByType.TryGetValue("*", out customFields))
            {
                foreach (var fd in customFields)
                {
                    var val = ReadString(item, fd.Field);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        task.CustomDevopsFieldValues[fd.Field] = val;
                        // compat: primeiro campo → TfsClassification
                        if (string.IsNullOrWhiteSpace(task.TfsClassification))
                            task.TfsClassification = val;
                    }
                }
            }

            AssignResource(ctx, task, item, task.EstimatedHours);

            // Recalcula o fim considerando o % de alocação do recurso (apenas quando não fixado).
            // AssignResource é chamado depois do cálculo inicial do finish, então precisamos corrigir.
            // Quando StartFixed, a duração é negociada — não recalculamos o Finish, mas guardamos
            // o Finish calculado em CalculatedFinish para o Gantt exibir como alerta visual.
            if (!task.FinishFixed && !task.IsMilestone &&
                task.Resources.Count > 0 &&
                task.Resources.Any(r => Math.Abs(r.AllocationPercent - 100.0) > 0.01))
            {
                var effectiveDuration = TaskScheduleService.GetEffectiveDurationHours(task);
                var currentEffective  = (task.CurrentHours ?? 0) / Math.Max(0.01, allocationFactor);
                var totalEffective    = effectiveDuration + currentEffective;
                if (totalEffective > 0)
                {
                    var calcFinish = ProjectCalendarService.AddWorkingHours(task.Start, totalEffective);
                    if (task.StartFixed)
                    {
                        // Duração negociada: mantém Finish, registra o calculado para alerta visual.
                        if (calcFinish.Date != task.Finish.Date)
                            task.CalculatedFinish = calcFinish;
                    }
                    else
                    {
                        task.Finish = calcFinish;
                        ctx.CursorByLane[laneKey] = task.Finish > baseStart ? task.Finish : baseStart;
                    }
                }
            }

            // Conta Tasks filhas no DevOps para a coluna TKs
            task.DevopsTaskCount = ctx.ChildrenByParent.TryGetValue(item.Id, out var tkChildren)
                ? tkChildren.Count(cid => ctx.Items.TryGetValue(cid, out var tk) && IsType(tk, "Task"))
                : 0;

            return task;
        }

        private static void AssignResource(BuildContext ctx, ProjectTask task, WorkItem item, double? estimatedHours = null)
        {
            var resource = AddResourceIfAssigned(ctx.Project, ctx.ResourcesByKey, item, ctx.HoursPerDay);
            if (resource == null)
                return;

            var percAloc = ctx.PercAlocRef != null ? ReadDouble(item, ctx.PercAlocRef) : null;

            task.Resources.Add(new TaskResource
            {
                ResourceId = resource.Id,
                Resource = resource,
                AllocationPercent = (percAloc.HasValue && percAloc.Value > 0 && percAloc.Value <= 100) ? percAloc.Value : 100,
                EstimatedHours = estimatedHours
            });
        }

        private static Resource? AddResourceIfAssigned(
            Project project,
            Dictionary<string, Resource> resourcesByKey,
            WorkItem item,
            double hoursPerDay)
        {
            var key = !string.IsNullOrWhiteSpace(item.AssigneeEmail)
                ? item.AssigneeEmail.Trim()
                : item.Assignee.Trim();
            if (string.IsNullOrWhiteSpace(key))
                return null;

            if (resourcesByKey.TryGetValue(key, out var existing))
                return existing;

            var resource = new Resource
            {
                Id = project.Resources.Select(r => r.Id).DefaultIfEmpty(0).Max() + 1,
                Name = string.IsNullOrWhiteSpace(item.AssigneeName) ? key : item.AssigneeName.Trim(),
                Email = string.IsNullOrWhiteSpace(item.AssigneeEmail) ? null : item.AssigneeEmail.Trim(),
                MaxUnitsPerDay = hoursPerDay <= 0 ? ProjectCalendarService.WorkingHoursPerDay : hoursPerDay,
                IsImportedFromTfs = true
            };
            project.Resources.Add(resource);
            resourcesByKey[key] = resource;
            return resource;
        }

        private static IEnumerable<int> OrderedChildren(
            Dictionary<int, List<int>> childrenByParent, int parentId, Dictionary<int, WorkItem> items)
        {
            if (!childrenByParent.TryGetValue(parentId, out var list))
                yield break;

            // Ordena irmãos pelo StackRank (backlog do DevOps); sem rank vai por último.
            var ordered = list
                .OrderBy(id => items.TryGetValue(id, out var w) && w.StackRank.HasValue
                    ? w.StackRank!.Value
                    : double.MaxValue)
                .ThenBy(id => id);

            foreach (var id in ordered)
                yield return id;
        }

        private static void NormalizeIds(System.Collections.ObjectModel.ObservableCollection<ProjectTask> roots)
        {
            int next = 1;
            void Walk(ProjectTask t)
            {
                t.Id = next++;
                foreach (var c in t.Children)
                    Walk(c);
            }
            foreach (var r in roots)
                Walk(r);
        }

        // ── Chamadas REST ────────────────────────────────────────────────────

        public static async Task<string> LoadWorkItemDescriptionAsync(
            TfsConnectionOptions options,
            int workItemId,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var authHeader = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var fields = new List<string> { "System.Description" };
            var items = await LoadWorkItemsAsync(orgBase, authHeader, new[] { workItemId }, fields, cancellationToken, expandRelations: false);

            if (!items.TryGetValue(workItemId, out var item))
                return string.Empty;

            return ToPlainText(item.Description);
        }

        /// <summary>Retorna o HTML original da descrição, sem conversão para texto.</summary>
        public static async Task<string> LoadWorkItemDescriptionHtmlAsync(
            TfsConnectionOptions options,
            int workItemId,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var authHeader = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var fields = new List<string> { "System.Description" };
            var items = await LoadWorkItemsAsync(orgBase, authHeader, new[] { workItemId }, fields, cancellationToken, expandRelations: false);

            if (!items.TryGetValue(workItemId, out var item))
                return string.Empty;

            return item.Description ?? string.Empty;
        }


        public static async Task<List<OnlineChildTaskInfo>> LoadOnlineChildTasksAsync(
            TfsConnectionOptions options,
            int parentWorkItemId,
            CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
                throw new InvalidOperationException("Conexão TFS incompleta: configure organização, projeto, PAT e lembre o token.");
            if (parentWorkItemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(parentWorkItemId));

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var authHeader = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var edges = await LoadDirectHierarchyEdgesAsync(
                orgBase,
                options.TeamProject,
                authHeader,
                parentWorkItemId,
                cancellationToken);

            var childIds = edges
                .Where(e => e.parent == parentWorkItemId)
                .Select(e => e.child)
                .Distinct()
                .ToList();

            if (childIds.Count == 0)
                return new List<OnlineChildTaskInfo>();

            var fields = new List<string>
            {
                "System.Id",
                "System.Title",
                "System.WorkItemType",
                "System.State",
                "System.Tags",
                "System.Description"
            };

            var items = await LoadWorkItemsAsync(
                orgBase,
                authHeader,
                childIds,
                fields,
                cancellationToken,
                expandRelations: false);

            var rows = new List<OnlineChildTaskInfo>();
            foreach (var id in childIds)
            {
                if (!items.TryGetValue(id, out var item))
                    continue;

                rows.Add(new OnlineChildTaskInfo(
                    item.Id,
                    item.Title,
                    item.WorkItemType,
                    item.State,
                    item.Tags,
                    ToPlainText(item.Description),
                    await LoadLatestHistoryAsync(orgBase, options.TeamProject, authHeader, item.Id, cancellationToken)));
            }

            return rows.OrderBy(r => r.Id).ToList();
        }

        private static async Task<List<(int parent, int child)>> LoadDirectHierarchyEdgesAsync(
            string orgBase, string project, AuthenticationHeaderValue auth, int parentId, CancellationToken ct)
        {
            var wiql =
                "SELECT [System.Id] FROM WorkItemLinks " +
                $"WHERE [Source].[System.Id] = {parentId} " +
                "AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' " +
                "MODE(MayContain)";

            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?{ApiVersion}";
            var body = JsonSerializer.Serialize(new { query = wiql });

            using var doc = await PostJsonAsync(url, body, auth, ct);
            var edges = new List<(int, int)>();
            if (doc.RootElement.TryGetProperty("workItemRelations", out var rels))
            {
                foreach (var rel in rels.EnumerateArray())
                {
                    if (!rel.TryGetProperty("source", out var source) ||
                        source.ValueKind != JsonValueKind.Object ||
                        !source.TryGetProperty("id", out var sid))
                        continue;
                    if (!rel.TryGetProperty("target", out var target) ||
                        target.ValueKind != JsonValueKind.Object ||
                        !target.TryGetProperty("id", out var tid))
                        continue;

                    edges.Add((sid.GetInt32(), tid.GetInt32()));
                }
            }
            return edges;
        }

        private static async Task<string> LoadLatestHistoryAsync(
            string orgBase,
            string project,
            AuthenticationHeaderValue auth,
            int workItemId,
            CancellationToken ct)
        {
            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/workItems/{workItemId}/updates?{ApiVersion}";
            using var doc = await GetJsonAsync(url, auth, ct);
            if (!doc.RootElement.TryGetProperty("value", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return string.Empty;

            string fallback = string.Empty;
            foreach (var update in arr.EnumerateArray())
            {
                fallback = BuildUpdateSummary(update);
                if (update.TryGetProperty("fields", out var fields) &&
                    fields.ValueKind == JsonValueKind.Object &&
                    fields.TryGetProperty("System.History", out var history) &&
                    history.ValueKind == JsonValueKind.Object &&
                    history.TryGetProperty("newValue", out var newValue))
                {
                    var text = ToPlainText(newValue.GetString());
                    if (!string.IsNullOrWhiteSpace(text))
                        fallback = text;
                }
            }

            return fallback;
        }

        private static string BuildUpdateSummary(JsonElement update)
        {
            var changedBy = "";
            if (update.TryGetProperty("revisedBy", out var revisedBy) &&
                revisedBy.ValueKind == JsonValueKind.Object)
            {
                changedBy = revisedBy.TryGetProperty("displayName", out var dn)
                    ? dn.GetString() ?? ""
                    : "";
            }

            var changedAt = update.TryGetProperty("revisedDate", out var revisedDate)
                ? revisedDate.GetString() ?? ""
                : "";

            if (!string.IsNullOrWhiteSpace(changedBy) && !string.IsNullOrWhiteSpace(changedAt))
                return $"{changedBy} em {changedAt}";
            if (!string.IsNullOrWhiteSpace(changedBy))
                return changedBy;
            return changedAt;
        }

        private static async Task<Dictionary<string, string>> LoadFieldMapAsync(
            string orgBase, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            var url = $"{orgBase}/_apis/wit/fields?{ApiVersion}";
            using var doc = await GetJsonAsync(url, auth, ct);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var refName = f.TryGetProperty("referenceName", out var r) ? r.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(refName))
                        map[Normalize(name)] = refName!;
                }
            }
            return map;
        }

        /// <summary>
        /// Carrega a arvore de iterations (sprints) do team project. Cada sprint com
        /// startDate vira um <see cref="Sprint"/> com nome (folha), caminho completo
        /// (System.IterationPath, ex.: "Projeto\\Pasta\\Sprint"), inicio e fim. A
        /// numeracao sequencial (1..N) e atribuida depois, na ordem cronologica.
        /// </summary>
        private static async Task<List<Sprint>> LoadIterationsAsync(
            string orgBase, string project, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            var list = new List<Sprint>();
            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/classificationnodes/iterations?$depth=10&{ApiVersion}";

            JsonDocument doc;
            try { doc = await GetJsonAsync(url, auth, ct); }
            catch { return list; } // sem datas de sprint, cai no inicio do projeto

            using (doc)
                Walk(doc.RootElement, null, list);

            return list;

            static void Walk(JsonElement node, string? prefix, List<Sprint> acc)
            {
                var name = node.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) return;

                var path = string.IsNullOrEmpty(prefix) ? name : prefix + "\\" + name;

                if (node.TryGetProperty("attributes", out var attrs) &&
                    attrs.ValueKind == JsonValueKind.Object &&
                    attrs.TryGetProperty("startDate", out var sd) &&
                    sd.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(sd.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
                {
                    DateTime end = start.Date;
                    if (attrs.TryGetProperty("finishDate", out var fd) &&
                        fd.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(fd.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var finish))
                    {
                        // DevOps retorna finishDate como dia seguinte exclusivo; subtrai 1 para o último dia inclusivo.
                        var finishDay = finish.Date;
                        end = finishDay > start.Date ? finishDay.AddDays(-1) : finishDay;
                    }

                    acc.Add(new Sprint
                    {
                        DisplayName = name,
                        Path = path,
                        Start = start.Date,
                        End = end
                    });
                }

                if (node.TryGetProperty("children", out var children) &&
                    children.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in children.EnumerateArray())
                        Walk(child, path, acc);
                }
            }
        }

        /// <summary>
        /// Retorna as sprints que devem aparecer no projeto:
        /// (1) Todas efetivamente usadas pelos work items importados.
        /// (2) Sprints futuras cujo início está dentro de <paramref name="futureSprintDays"/>
        ///     dias a partir de hoje — permite ao usuário mover tarefas para sprints
        ///     que ainda não têm itens associados. 0 = só as usadas.
        /// </summary>
        private static List<Sprint> SelectUsedSprints(
            IEnumerable<WorkItem> items, List<Sprint> allSprints, int futureSprintDays = 90)
        {
            // Sprints referenciadas por Feature/Story
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
                if (IsFeatureOrStoryType(it.WorkItemType) && !string.IsNullOrWhiteSpace(it.IterationPath))
                    used.Add(it.IterationPath.Trim());

            // Sprints futuras dentro da janela configurada
            IEnumerable<Sprint> candidates = allSprints.Where(s => s.Path != null && used.Contains(s.Path!));
            if (futureSprintDays > 0)
            {
                var horizon = DateTime.Today.AddDays(futureSprintDays);
                var future = allSprints.Where(s =>
                    s.Path != null &&
                    !used.Contains(s.Path!) &&
                    s.Start != default &&
                    s.Start.Date >= DateTime.Today &&
                    s.Start.Date <= horizon.Date);
                candidates = candidates.Concat(future);
            }

            return NumberSprints(candidates);
        }

        private static bool IsFeatureOrStoryType(string? type) =>
            string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase) ||
            IsStoryType(type);

        private static bool IsImportRootType(string? type) =>
            string.Equals(type, "Epic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase) ||
            IsStoryType(type);

        public static bool IsStoryTypePublic(string? type) => IsStoryType(type);
        public static bool IsTaskTypePublic(string? type)  => IsTaskType(type);
        public static List<object> BuildCreateOpsForTests(
            ProjectTask task,
            int parentId,
            Dictionary<int, ProjectTask> tasksById,
            bool syncPredecessorLinks = true) =>
            BuildCreateOps(task, parentId, "https://dev.azure.com/test", null, null, null, tasksById, syncPredecessorLinks);

        /// <summary>
        /// Busca os HH das Tasks filhas usando o campo padrão Microsoft.VSTS.Scheduling.OriginalEstimate.
        /// </summary>
        public sealed class ChildTaskHoursResult
        {
            public double TotalHours { get; init; }
            public int TaskCount { get; init; }
            public List<string> TasksWithoutHours { get; init; } = [];
        }

        public sealed class DevOpsTaskInfo
        {
            public int TfsId { get; init; }
            public string Title { get; init; } = "";
            public double EstimatedHours { get; init; }
            public double CompletedHours { get; init; }
            public double PercentComplete { get; init; }
            public string? AssignedTo { get; init; }
            public string? AssignedToDisplay { get; init; }
            public int Priority { get; init; } = 5;
            public string? State { get; init; }
            public string? Activity { get; init; }
            public string? Tags { get; init; }
        }

        /// <summary>
        /// Busca todas as Tasks filhas de um work item pai no DevOps.
        /// </summary>
        public static async Task<List<DevOpsTaskInfo>?> FetchChildTasksFromDevOpsAsync(
            TfsConnectionOptions options, int parentTfsId, CancellationToken ct = default)
        {
            if (options == null || parentTfsId <= 0) return null;
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth    = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            // 1. Busca o pai com relações
            var parentUrl = $"{orgBase}/_apis/wit/workitems/{parentTfsId}?$expand=relations&{ApiVersion}";
            using var parentReq = new HttpRequestMessage(HttpMethod.Get, parentUrl);
            parentReq.Headers.Authorization = auth;
            parentReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            using var parentResp = await Http.SendAsync(parentReq, ct);
            if (!parentResp.IsSuccessStatusCode) return null;

            var parentJson = await parentResp.Content.ReadAsStringAsync(ct);
            using var parentDoc = JsonDocument.Parse(parentJson);
            var childIds = new List<int>();
            if (parentDoc.RootElement.TryGetProperty("relations", out var rels))
            {
                foreach (var rel in rels.EnumerateArray())
                {
                    if (!rel.TryGetProperty("rel", out var relType)) continue;
                    if (!string.Equals(relType.GetString(), "System.LinkTypes.Hierarchy-Forward", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!rel.TryGetProperty("url", out var urlProp)) continue;
                    if (int.TryParse((urlProp.GetString() ?? "").Split('/').LastOrDefault(), out var cid))
                        childIds.Add(cid);
                }
            }
            if (childIds.Count == 0) return [];

            // 2. Busca os filhos em batch com os campos necessários
            const string OrigEstRef      = "Microsoft.VSTS.Scheduling.OriginalEstimate";
            const string CompletedRef    = "Microsoft.VSTS.Scheduling.CompletedWork";
            var ids = string.Join(",", childIds);
            const string ActivityRef = "Microsoft.VSTS.Common.Activity";
            var fields = $"System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo,System.Tags,{OrigEstRef},{CompletedRef},Microsoft.VSTS.Common.Priority,{ActivityRef}";
            var batchUrl = $"{orgBase}/_apis/wit/workitems?ids={ids}&fields={fields}&{ApiVersion}";
            using var batchReq = new HttpRequestMessage(HttpMethod.Get, batchUrl);
            batchReq.Headers.Authorization = auth;
            batchReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            using var batchResp = await Http.SendAsync(batchReq, ct);
            if (!batchResp.IsSuccessStatusCode) return null;

            var batchJson = await batchResp.Content.ReadAsStringAsync(ct);
            using var batchDoc = JsonDocument.Parse(batchJson);

            var result = new List<DevOpsTaskInfo>();
            if (batchDoc.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (!item.TryGetProperty("fields", out var f)) continue;
                    if (!f.TryGetProperty("System.WorkItemType", out var wt) || !IsTaskType(wt.GetString())) continue;

                    var tid       = f.TryGetProperty("System.Id",    out var ip) ? ip.GetInt32() : 0;
                    var title     = f.TryGetProperty("System.Title", out var tp) ? tp.GetString() ?? "" : "";
                    var state     = f.TryGetProperty("System.State", out var sp) ? sp.GetString() : null;

                    // Ignora atividades removidas
                    if (string.Equals(state, "Removed", StringComparison.OrdinalIgnoreCase)) continue;

                    var hours     = f.TryGetProperty(OrigEstRef,   out var hp) && hp.ValueKind == JsonValueKind.Number ? hp.GetDouble() : 0;
                    var completed = f.TryGetProperty(CompletedRef, out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetDouble() : 0;
                    var prio      = f.TryGetProperty("Microsoft.VSTS.Common.Priority", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetInt32() : 5;
                    string? assignee = null;
                    string? assigneeDisplay = null;
                    if (f.TryGetProperty("System.AssignedTo", out var at))
                    {
                        if (at.ValueKind == JsonValueKind.Object)
                        {
                            assignee        = at.TryGetProperty("uniqueName",   out var un) ? un.GetString() : null;
                            assigneeDisplay = at.TryGetProperty("displayName",  out var dn) ? dn.GetString() : assignee;
                        }
                        else
                        {
                            assignee = assigneeDisplay = at.GetString();
                        }
                    }

                    // Closed → 100%; caso contrário calcula pelo CompletedWork
                    double pct = 0;
                    bool isClosed = string.Equals(state, "Closed", StringComparison.OrdinalIgnoreCase);
                    if (isClosed)
                        pct = 100;
                    else if (completed > 0 && hours > 0)
                        pct = Math.Min(100, completed / hours * 100);

                    var activity = f.TryGetProperty(ActivityRef, out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;
                    var tags     = f.TryGetProperty("System.Tags", out var tgp) && tgp.ValueKind == JsonValueKind.String ? tgp.GetString() : null;

                    result.Add(new DevOpsTaskInfo
                    {
                        TfsId = tid, Title = title, State = state,
                        EstimatedHours = hours, CompletedHours = completed,
                        PercentComplete = pct, AssignedTo = assignee, AssignedToDisplay = assigneeDisplay,
                        Priority = prio, Activity = activity, Tags = tags
                    });
                }
            }
            return result;
        }

        public static Task<ChildTaskHoursResult?> FetchChildTaskHoursAsync(
            TfsConnectionOptions options, int parentTfsId, CancellationToken ct = default)
            => FetchChildTaskHoursAsync(options, parentTfsId, "Microsoft.VSTS.Scheduling.OriginalEstimate", ct);

        /// <summary>
        /// Busca as Tasks filhas de um work item no DevOps e retorna a soma dos HH Estimados (campo hoursRef).
        /// Retorna null se não foi possível obter dados.
        /// </summary>
        public static async Task<ChildTaskHoursResult?> FetchChildTaskHoursAsync(
            TfsConnectionOptions options, int parentTfsId, string hoursRef,
            CancellationToken ct = default)
        {
            if (options == null || parentTfsId <= 0 || string.IsNullOrWhiteSpace(hoursRef))
                return null;

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth    = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            // 1. Busca o work item pai com as relações para encontrar filhos Task
            var fieldsToRequest = $"System.Id,System.WorkItemType,{hoursRef}";
            var parentUrl = $"{orgBase}/_apis/wit/workitems/{parentTfsId}?$expand=relations&{ApiVersion}";
            using var parentReq = new HttpRequestMessage(HttpMethod.Get, parentUrl);
            parentReq.Headers.Authorization = auth;
            parentReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var parentResp = await Http.SendAsync(parentReq, ct);
            if (!parentResp.IsSuccessStatusCode) return null;

            var parentJson = await parentResp.Content.ReadAsStringAsync(ct);
            using var parentDoc = JsonDocument.Parse(parentJson);

            // Coleta IDs dos filhos diretos (Hierarchy-Forward = filhos)
            var childIds = new List<int>();
            if (parentDoc.RootElement.TryGetProperty("relations", out var rels))
            {
                foreach (var rel in rels.EnumerateArray())
                {
                    if (!rel.TryGetProperty("rel", out var relType)) continue;
                    if (!string.Equals(relType.GetString(), "System.LinkTypes.Hierarchy-Forward", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!rel.TryGetProperty("url", out var urlProp)) continue;
                    var urlStr = urlProp.GetString() ?? "";
                    if (int.TryParse(urlStr.Split('/').LastOrDefault(), out var cid))
                        childIds.Add(cid);
                }
            }
            if (childIds.Count == 0)
                return new ChildTaskHoursResult { TotalHours = 0, TaskCount = 0 };

            // 2. Busca os work items filhos em batch
            var ids = string.Join(",", childIds);
            var batchUrl = $"{orgBase}/_apis/wit/workitems?ids={ids}&fields=System.Id,System.Title,System.WorkItemType,{hoursRef}&{ApiVersion}";
            using var batchReq = new HttpRequestMessage(HttpMethod.Get, batchUrl);
            batchReq.Headers.Authorization = auth;
            batchReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var batchResp = await Http.SendAsync(batchReq, ct);
            if (!batchResp.IsSuccessStatusCode) return null;

            var batchJson = await batchResp.Content.ReadAsStringAsync(ct);
            using var batchDoc = JsonDocument.Parse(batchJson);

            double total = 0;
            int taskCount = 0;
            var tasksWithoutHours = new List<string>();

            if (batchDoc.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (!item.TryGetProperty("fields", out var fields)) continue;
                    if (!fields.TryGetProperty("System.WorkItemType", out var wt)) continue;
                    if (!IsTaskType(wt.GetString())) continue;

                    taskCount++;
                    var title = fields.TryGetProperty("System.Title", out var tt) ? tt.GetString() ?? "" : "";
                    var itemId = fields.TryGetProperty("System.Id", out var idp) ? idp.GetInt32().ToString() : "?";

                    if (fields.TryGetProperty(hoursRef, out var hProp) && hProp.ValueKind == JsonValueKind.Number)
                    {
                        var h = hProp.GetDouble();
                        if (h > 0.0001)
                            total += h;
                        else
                            tasksWithoutHours.Add($"#{itemId} {title}");
                    }
                    else
                    {
                        tasksWithoutHours.Add($"#{itemId} {title}");
                    }
                }
            }
            return new ChildTaskHoursResult
            {
                TotalHours       = total,
                TaskCount        = taskCount,
                TasksWithoutHours = tasksWithoutHours
            };
        }

        public static async Task DeleteWorkItemAsync(TfsConnectionOptions options, int workItemId, CancellationToken ct = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            // DELETE /wit/workitems/{id}?destroy=true remove permanentemente (sem lixeira)
            var url = $"{orgBase}/_apis/wit/workitems/{workItemId}?destroy=true&{ApiVersion}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Falha ao excluir #{workItemId}: {resp.StatusCode} — {body}");
            }
        }
        /// <summary>
        /// Atualiza campos de uma Task individual diretamente no DevOps (usado pelo Tech Lead Review).
        /// </summary>
        public static async Task UpdateTaskFieldsAsync(
            TfsConnectionOptions options, int taskId,
            double estimatedHours = 0, double completedHours = 0,
            int priority = 5, string? assignedTo = null,
            string? state = null, string? title = null,
            string? activity = null, string? tags = null,
            CancellationToken ct = default)
        {
            if (options == null || taskId <= 0) return;
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth    = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var ops = new List<object>();
            if (!string.IsNullOrWhiteSpace(title))
                ops.Add(PatchAdd("/fields/System.Title", title));
            if (estimatedHours > 0)
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", estimatedHours));
            if (completedHours > 0)
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Scheduling.CompletedWork", completedHours));
            if (priority > 0)
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Priority", priority));
            if (!string.IsNullOrWhiteSpace(assignedTo))
                ops.Add(PatchAdd("/fields/System.AssignedTo", assignedTo));
            if (!string.IsNullOrWhiteSpace(state))
                ops.Add(PatchAdd("/fields/System.State", state));
            if (!string.IsNullOrWhiteSpace(activity))
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Activity", activity));
            if (tags != null)
                ops.Add(PatchAdd("/fields/System.Tags", tags));

            if (ops.Count == 0) return;

            var url     = $"{orgBase}/_apis/wit/workitems/{taskId}?{ApiVersion}";
            var body    = System.Text.Json.JsonSerializer.Serialize(ops);
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
            req.Headers.Authorization = auth;
            req.Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"DevOps rejeitou atualização da Task {taskId}: {resp.StatusCode} — {err}");
            }
        }

        /// <summary>
        /// Busca work items de nível raiz (sem pai) do Team Project para discovery do portfólio.
        /// Retorna lista de (Id, Title, Type).
        /// </summary>
        public static async Task<List<(int Id, string Title, string Type)>> FetchRootWorkItemsAsync(
            TfsConnectionOptions options, CancellationToken ct = default)
        {
            if (options == null || !options.IsValid) return [];
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth    = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            // WIQL: work items sem pai no projeto
            var wiql = new { query = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{options.TeamProject.Replace("'", "''")}' AND [System.WorkItemType] = 'Project' AND [System.State] <> 'Removed' AND [System.AreaPath] UNDER '{options.TeamProject.Replace("'", "''")}' ORDER BY [System.Id]" };
            var wiqlBody = System.Text.Json.JsonSerializer.Serialize(wiql);
            var wiqlUrl  = $"{orgBase}/{Uri.EscapeDataString(options.TeamProject)}/_apis/wit/wiql?{ApiVersion}";
            using var wiqlReq = new HttpRequestMessage(HttpMethod.Post, wiqlUrl);
            wiqlReq.Headers.Authorization = auth;
            wiqlReq.Content = new StringContent(wiqlBody, Encoding.UTF8, "application/json");
            using var wiqlResp = await Http.SendAsync(wiqlReq, ct);
            if (!wiqlResp.IsSuccessStatusCode) return [];

            var wiqlJson = await wiqlResp.Content.ReadAsStringAsync(ct);
            using var wiqlDoc = JsonDocument.Parse(wiqlJson);
            if (!wiqlDoc.RootElement.TryGetProperty("workItems", out var wiItems)) return [];

            var ids = wiItems.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0)
                .Where(x => x > 0).ToList();
            if (ids.Count == 0) return [];

            var result = new List<(int, string, string)>();
            // Busca em lotes de 200
            for (int i = 0; i < ids.Count; i += 200)
            {
                var batch = ids.Skip(i).Take(200).ToList();
                var batchIds = string.Join(",", batch);
                var batchUrl = $"{orgBase}/_apis/wit/workitems?ids={batchIds}&fields=System.Id,System.Title,System.WorkItemType&{ApiVersion}";
                using var batchReq = new HttpRequestMessage(HttpMethod.Get, batchUrl);
                batchReq.Headers.Authorization = auth;
                using var batchResp = await Http.SendAsync(batchReq, ct);
                if (!batchResp.IsSuccessStatusCode) continue;
                var batchJson = await batchResp.Content.ReadAsStringAsync(ct);
                using var batchDoc = JsonDocument.Parse(batchJson);
                if (!batchDoc.RootElement.TryGetProperty("value", out var values)) continue;
                foreach (var item in values.EnumerateArray())
                {
                    if (!item.TryGetProperty("fields", out var f)) continue;
                    var id    = f.TryGetProperty("System.Id",           out var ip) ? ip.GetInt32()    : 0;
                    var title = f.TryGetProperty("System.Title",        out var tp) ? tp.GetString() ?? "" : "";
                    var type  = f.TryGetProperty("System.WorkItemType", out var wt) ? wt.GetString() ?? "" : "";
                    if (id > 0 && string.Equals(type, "Project", StringComparison.OrdinalIgnoreCase))
                        result.Add((id, title, type));
                }
            }
            return result;
        }

        private static bool IsStoryType(string? type) =>
            string.Equals(type, "Story", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "User Story", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Product Backlog Item", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Requirement", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Historia de Usuario", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "História de Usuário", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Ordena as sprints por inicio, atribui numeros sequenciais (1..N) e,
        /// quando uma sprint nao traz finishDate, fecha a janela no inicio da
        /// proxima (sprints contiguas). Devolve a lista numerada.
        /// </summary>
        private static List<Sprint> NumberSprints(IEnumerable<Sprint> sprints)
        {
            var ordered = sprints
                .GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(s => s.Start)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Number = i + 1;
                // Sem fim explicito (ou fim antes do inicio): usa o inicio da proxima.
                if (ordered[i].End <= ordered[i].Start && i + 1 < ordered.Count)
                    ordered[i].End = ordered[i + 1].Start;
            }

            return ordered;
        }

        /// <summary>
        /// A partir dos work items importados e do mapa de datas das sprints,
        /// calcula como ancorar a numeração sequencial das sprints (começando da 1):
        ///  - <c>anchor</c>: data de início da PRIMEIRA sprint efetivamente usada
        ///    pelos work items (a que vira "Sprint 1"); null se nenhum item tem sprint.
        ///  - <c>durationDays</c>: cadência real das sprints (dias corridos) — a
        ///    diferença mais frequente entre inícios de sprints consecutivas, usando
        ///    TODAS as iterations (não só as usadas) para não inflar com sprints
        ///    puladas; null se não há dados suficientes (mantém o padrão do projeto).
        /// </summary>
        private static (DateTime? anchor, int? durationDays) ComputeSprintAnchor(
            IEnumerable<WorkItem> items, Dictionary<string, DateTime> sprintStarts)
        {
            // Início da 1a sprint usada por uma Feature/Story (Project/Epic não contam).
            DateTime? anchor = null;
            foreach (var it in items)
            {
                if (!IsFeatureOrStoryType(it.WorkItemType)) continue;
                if (string.IsNullOrWhiteSpace(it.IterationPath)) continue;
                if (!sprintStarts.TryGetValue(it.IterationPath.Trim(), out var s)) continue;
                if (anchor == null || s.Date < anchor.Value)
                    anchor = s.Date;
            }

            // Cadência: diferença mais comum (em dias corridos) entre inícios de
            // sprints consecutivas, considerando todas as iterations conhecidas.
            int? duration = null;
            var allStarts = sprintStarts.Values
                .Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
            if (allStarts.Count >= 2)
            {
                var gapCounts = new Dictionary<int, int>();
                for (int i = 1; i < allStarts.Count; i++)
                {
                    int gap = (int)Math.Round((allStarts[i] - allStarts[i - 1]).TotalDays);
                    if (gap > 0)
                        gapCounts[gap] = gapCounts.TryGetValue(gap, out var c) ? c + 1 : 1;
                }
                if (gapCounts.Count > 0)
                    duration = gapCounts
                        .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                        .First().Key;
            }

            return (anchor, duration);
        }

        private static async Task<List<(int parent, int child)>> LoadHierarchyEdgesAsync(
            string orgBase, string project, AuthenticationHeaderValue auth, int rootId, CancellationToken ct)
        {
            var wiql =
                "SELECT [System.Id] FROM WorkItemLinks " +
                $"WHERE [Source].[System.Id] = {rootId} " +
                "AND [System.Links.LinkType] = 'System.LinkTypes.Hierarchy-Forward' " +
                "MODE(Recursive)";

            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?{ApiVersion}";
            var body = JsonSerializer.Serialize(new { query = wiql });

            using var doc = await PostJsonAsync(url, body, auth, ct);
            var edges = new List<(int, int)>();
            if (doc.RootElement.TryGetProperty("workItemRelations", out var rels))
            {
                foreach (var rel in rels.EnumerateArray())
                {
                    if (!rel.TryGetProperty("target", out var target) ||
                        target.ValueKind != JsonValueKind.Object)
                        continue;

                    int childId = target.GetProperty("id").GetInt32();

                    if (rel.TryGetProperty("source", out var source) &&
                        source.ValueKind == JsonValueKind.Object &&
                        source.TryGetProperty("id", out var sid))
                    {
                        edges.Add((sid.GetInt32(), childId));
                    }
                }
            }
            return edges;
        }

        // Retorna pares (predecessorTfsId, successorTfsId) para todos os links
        // Dependency-Reverse dentro do escopo de IDs fornecido.
        private static async Task<List<(int predecessor, int successor)>> LoadDependencyLinksAsync(
            string orgBase, string project, AuthenticationHeaderValue auth,
            IEnumerable<int> scopeIds, CancellationToken ct)
        {
            var idSet = scopeIds.ToHashSet();
            if (idSet.Count == 0)
                return new List<(int, int)>();

            // WIQL: todos os links de dependência cujo SOURCE está no escopo.
            // "Dependency-Reverse" visto do successor = "A predecessor of B":
            //   source = successor (B), target = predecessor (A).
            var idList = string.Join(",", idSet);
            var wiql =
                "SELECT [System.Id] FROM WorkItemLinks " +
                $"WHERE [Source].[System.Id] IN ({idList}) " +
                "AND [System.Links.LinkType] = 'System.LinkTypes.Dependency-Reverse' " +
                "MODE(MayContain)";

            var url = $"{orgBase}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?{ApiVersion}";
            var body = JsonSerializer.Serialize(new { query = wiql });

            using var doc = await PostJsonAsync(url, body, auth, ct);
            var links = new List<(int, int)>();
            if (!doc.RootElement.TryGetProperty("workItemRelations", out var rels))
                return links;

            foreach (var rel in rels.EnumerateArray())
            {
                if (!rel.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object)
                    continue;
                if (!rel.TryGetProperty("target", out var tgt) || tgt.ValueKind != JsonValueKind.Object)
                    continue;

                int successor   = src.GetProperty("id").GetInt32();
                int predecessor = tgt.GetProperty("id").GetInt32();
                links.Add((predecessor, successor));
            }
            return links;
        }

        private static async Task<Dictionary<int, WorkItem>> LoadWorkItemsAsync(
            string orgBase, AuthenticationHeaderValue auth, IEnumerable<int> ids,
            List<string> fields, CancellationToken ct, bool expandRelations = false)
        {
            var result = new Dictionary<int, WorkItem>();
            var idList = ids.Distinct().ToList();
            const int batchSize = 200;

            for (int i = 0; i < idList.Count; i += batchSize)
            {
                var chunk = idList.Skip(i).Take(batchSize).ToArray();
                var url = $"{orgBase}/_apis/wit/workitemsbatch?{ApiVersion}";
                // workitemsbatch nao aceita "fields" junto com "$expand": com relations,
                // pedimos "all" (traz todos os campos + relations).
                var body = expandRelations
                    ? JsonSerializer.Serialize(new Dictionary<string, object> { ["ids"] = chunk, ["$expand"] = "all" })
                    : JsonSerializer.Serialize(new { ids = chunk, fields });

                using var doc = await PostJsonAsync(url, body, auth, ct);
                if (!doc.RootElement.TryGetProperty("value", out var arr))
                    continue;

                foreach (var wi in arr.EnumerateArray())
                {
                    int id = wi.GetProperty("id").GetInt32();
                    var f = wi.GetProperty("fields");
                    result[id] = new WorkItem
                    {
                        Id = id,
                        Title = GetString(f, "System.Title") ?? $"#{id}",
                        WorkItemType = GetString(f, "System.WorkItemType") ?? string.Empty,
                        State = GetString(f, "System.State") ?? string.Empty,
                        Assignee = GetIdentityName(f, "System.AssignedTo"),
                        AssigneeName = GetIdentityDisplayName(f, "System.AssignedTo"),
                        AssigneeEmail = GetIdentityUniqueName(f, "System.AssignedTo"),
                        IterationPath = GetString(f, "System.IterationPath") ?? string.Empty,
                        Description = GetString(f, "System.Description") ?? string.Empty,
                        Tags = GetString(f, "System.Tags") ?? string.Empty,
                        StackRank = GetDoubleField(f, "Microsoft.VSTS.Common.StackRank"),
                        Fields = f.Clone(),
                        Relations = wi.TryGetProperty("relations", out var relEl) &&
                                    relEl.ValueKind == JsonValueKind.Array
                            ? relEl.Clone()
                            : (JsonElement?)null
                    };
                }
            }
            return result;
        }

        /// <summary>
        /// Acha o link de pai (System.LinkTypes.Hierarchy-Reverse) nas relations de
        /// um work item: devolve o id do pai e o índice da relação (para remoção).
        /// </summary>
        private static (int? parentId, int relIndex) FindParentRelation(WorkItem wi)
        {
            if (wi.Relations is not { ValueKind: JsonValueKind.Array } rels)
                return (null, -1);

            int index = 0;
            foreach (var rel in rels.EnumerateArray())
            {
                var relType = rel.TryGetProperty("rel", out var rt) ? rt.GetString() : null;
                if (string.Equals(relType, "System.LinkTypes.Hierarchy-Reverse", StringComparison.OrdinalIgnoreCase))
                {
                    var url = rel.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var pid = ParseIdFromUrl(url);
                    return (pid, index);
                }
                index++;
            }
            return (null, -1);
        }

        private static List<(int id, int index)> FindPredecessorRelations(WorkItem wi)
        {
            var result = new List<(int id, int index)>();
            if (wi.Relations is not { ValueKind: JsonValueKind.Array } rels)
                return result;

            int index = 0;
            foreach (var rel in rels.EnumerateArray())
            {
                var relType = rel.TryGetProperty("rel", out var rt) ? rt.GetString() : null;
                if (string.Equals(relType, "System.LinkTypes.Dependency-Reverse", StringComparison.OrdinalIgnoreCase))
                {
                    var url = rel.TryGetProperty("url", out var u) ? u.GetString() : null;
                    var predecessorId = ParseIdFromUrl(url);
                    if (predecessorId.HasValue)
                        result.Add((predecessorId.Value, index));
                }
                index++;
            }

            return result;
        }

        private static int? ParseIdFromUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var slash = url.LastIndexOf('/');
            return slash >= 0 && int.TryParse(url[(slash + 1)..], out var id) ? id : null;
        }

        private static string ToPlainText(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            var text = Regex.Replace(html, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "</p\\s*>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<.*?>", string.Empty);
            text = System.Net.WebUtility.HtmlDecode(text);
            return Regex.Replace(text, "[ \\t\\r\\f\\v]+", " ").Trim();
        }

        private static async Task<JsonDocument> GetJsonAsync(
            string url, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await SendAsync(req, ct);
        }

        private static async Task<JsonDocument> PostJsonAsync(
            string url, string body, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await SendAsync(req, ct);
        }

        private static string ParseTfsError(int statusCode, string content)
        {
            if (statusCode == 400)
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    // Tenta extrair erros de validação de campo obrigatório
                    if (root.TryGetProperty("customProperties", out var cp) &&
                        cp.TryGetProperty("RuleValidationErrors", out var errs) &&
                        errs.ValueKind == JsonValueKind.Array)
                    {
                        var missingFields = new List<string>();
                        foreach (var err in errs.EnumerateArray())
                        {
                            if (err.TryGetProperty("fieldReferenceName", out var fieldRef) &&
                                err.TryGetProperty("fieldStatusFlags", out var flags))
                            {
                                var flagStr = flags.GetString() ?? "";
                                if (flagStr.Contains("required") || flagStr.Contains("invalidEmpty"))
                                    missingFields.Add(fieldRef.GetString() ?? "");
                            }
                        }

                        if (missingFields.Count > 0)
                        {
                            var fields = string.Join(", ", missingFields.Where(f => !string.IsNullOrEmpty(f)));
                            return $"O DevOps rejeitou a criação porque o(s) campo(s) obrigatório(s) não foram preenchidos: {fields}.\n\n" +
                                   $"Para corrigir: vá em Arquivo → Configurar Integração Azure DevOps → expanda \"⚙ Campos avançados\" → seção \"Campos obrigatórios na criação\", " +
                                   $"e adicione uma entrada para cada campo com o valor padrão que o DevOps exige.\n\n" +
                                   $"Exemplo: campo \"{missingFields[0]}\" com o valor que o processo do seu DevOps aceita (ex.: \"Atividade\", \"Development\", etc.).";
                        }
                    }
                }
                catch { /* JSON inválido: cai no fallback abaixo */ }
            }

            return $"Erro do TFS ({statusCode}): {Truncate(content, 500)}";
        }

        private static async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            using var resp = await Http.SendAsync(req, ct);
            var content = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Uma resposta HTML de login indica PAT invalido/expirado.
                if (content.TrimStart().StartsWith("<", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Falha de autenticação ({(int)resp.StatusCode}). Verifique o PAT e a URL da organização.");

                throw new InvalidOperationException(ParseTfsError((int)resp.StatusCode, content));
            }

            if (content.TrimStart().StartsWith("<", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "O TFS respondeu com uma página HTML (provável sessão/login). Verifique o PAT e a URL.");

            return JsonDocument.Parse(content);
        }

        // ── Helpers de campo ─────────────────────────────────────────────────

        private sealed class WorkItem
        {
            public int Id;
            public string Title = string.Empty;
            public string WorkItemType = string.Empty;
            public string State = string.Empty;
            public string Assignee = string.Empty;
            public string AssigneeName = string.Empty;
            public string AssigneeEmail = string.Empty;
            public string IterationPath = string.Empty;
            public string Description = string.Empty;
            public string Tags = string.Empty;
            public double? StackRank;
            public JsonElement Fields;
            public JsonElement? Relations;
        }

        private static bool HasBlockTag(string? tags) => HasTag(tags, "Block");

        private static bool HasTag(string? tags, string tag) =>
            !string.IsNullOrWhiteSpace(tags) &&
            tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

        private static string[] GetFixedStartTagAliases(string? configuredTag)
        {
            var primary = string.IsNullOrWhiteSpace(configuredTag)
                ? "DT-INI-NEG"
                : configuredTag.Trim();

            return new[] { primary, "DT-INI-NEG", "DT_INI_NEG" }
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // Extrai o texto do bloco "Justificativa: <texto>." da descrição do DevOps.
        internal static string? ParseJustificativa(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return null;
            const string marker = "Justificativa:";
            var idx = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;
            var start = idx + marker.Length;
            var end = description.IndexOf('.', start);
            var text = end >= 0 ? description[start..end] : description[start..];
            return text.Trim() is { Length: > 0 } t ? t : null;
        }

        // Substitui/insere o bloco "Justificativa: <texto>." na descrição.
        internal static string MergeJustificativa(string? description, string? justificativa)
        {
            var baseDesc = description ?? string.Empty;
            // Remove bloco existente
            const string marker = "Justificativa:";
            var idx = baseDesc.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var end = baseDesc.IndexOf('.', idx + marker.Length);
                baseDesc = end >= 0
                    ? (baseDesc[..idx] + baseDesc[(end + 1)..]).Trim()
                    : baseDesc[..idx].Trim();
            }
            if (!string.IsNullOrWhiteSpace(justificativa))
            {
                var sep = string.IsNullOrWhiteSpace(baseDesc) ? string.Empty : "\n";
                baseDesc = baseDesc + sep + $"Justificativa: {justificativa.Trim()}.";
            }
            return baseDesc;
        }

        private static bool IsType(WorkItem item, string type) =>
            string.Equals(item.WorkItemType, type, StringComparison.OrdinalIgnoreCase);

        private static bool IsCompletedState(string? state) =>
            state != null && (
                string.Equals(state, "Closed",    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Resolved",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Done",      StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase));

        private static bool IsOpenState(string? state) =>
            state != null && (
                string.Equals(state, "Active",      StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "New",         StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "In Progress", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Committed",   StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolve o reference name de um campo. Tenta, em ordem: o nome configurado
        /// (por display name exato; ou direto, se ja for um reference name como
        /// "Custom.xxx"); depois os candidatos de fallback por display name.
        /// </summary>
        private static string? ResolveField(
            Dictionary<string, string> fieldMap, string? configuredName, string[] fallbackCandidates)
        {
            if (!string.IsNullOrWhiteSpace(configuredName))
            {
                var name = configuredName.Trim();
                if (fieldMap.TryGetValue(Normalize(name), out var byDisplay))
                    return byDisplay;
                // Pode ja ser um reference name (ex.: Custom.Data_Inicio, System.X).
                if (name.Contains('.') && fieldMap.Values.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return name;
            }

            foreach (var c in fallbackCandidates)
                if (fieldMap.TryGetValue(Normalize(c), out var refName))
                    return refName;

            return null;
        }

        private static double? ReadDouble(WorkItem item, string? refName)
        {
            if (refName == null) return null;
            if (item.Fields.ValueKind != JsonValueKind.Object) return null;
            if (!item.Fields.TryGetProperty(refName, out var el)) return null;

            switch (el.ValueKind)
            {
                case JsonValueKind.Number:
                    return el.GetDouble();
                case JsonValueKind.String:
                    return double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                        || double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out v)
                        ? v
                        : null;
                default:
                    return null;
            }
        }

        // Lê Tipo_Centro_Custo do TFS. Se campo ausente/nulo, retorna null (= DEFINIDO_NO_PROJETO).
        private static string? ReadTipoCentroCusto(WorkItem item, string? refName)
        {
            var raw = ReadString(item, refName)?.Trim().ToUpperInvariant();
            if (raw == "CAPEX" || raw == "OPEX") return raw;
            return null;
        }

        private static string? ReadString(WorkItem item, string? refName)
        {
            if (refName == null) return null;
            if (item.Fields.ValueKind != JsonValueKind.Object) return null;
            if (!item.Fields.TryGetProperty(refName, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }

        private static string? ReadSyncUserName(WorkItem item, string? refName)
        {
            if (refName == null) return null;
            if (item.Fields.ValueKind != JsonValueKind.Object) return null;
            if (!item.Fields.TryGetProperty(refName, out var el)) return null;

            if (el.ValueKind == JsonValueKind.String)
                return el.GetString();
            if (el.ValueKind == JsonValueKind.Object)
            {
                if (el.TryGetProperty("displayName", out var displayName) &&
                    displayName.ValueKind == JsonValueKind.String)
                    return displayName.GetString();
                if (el.TryGetProperty("uniqueName", out var uniqueName) &&
                    uniqueName.ValueKind == JsonValueKind.String)
                    return uniqueName.GetString();
            }

            return el.ToString();
        }

        private static bool IsCurrentSyncUser(string? userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
                return false;

            var current = Environment.UserName.Trim();
            var normalized = userName.Trim();
            if (string.Equals(normalized, current, StringComparison.OrdinalIgnoreCase))
                return true;

            return normalized.Contains($"\"displayName\":\"{current}\"", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains($"\"uniqueName\":\"{current}\"", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime? ReadDate(WorkItem item, string? refName)
        {
            if (refName == null) return null;
            if (item.Fields.ValueKind != JsonValueKind.Object) return null;
            if (!item.Fields.TryGetProperty(refName, out var el)) return null;
            if (el.ValueKind != JsonValueKind.String) return null;

            var s = el.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            // O TFS devolve ISO 8601 em UTC (ex.: 2026-05-04T03:00:00Z = 04/05 00:00 BRT).
            // Convertemos para UTC e usamos a data, de forma independente do fuso da maquina.
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt.Date
                : (DateTime.TryParse(s, out var dt2) ? dt2.Date : null);
        }

        private static string? GetString(JsonElement fields, string refName) =>
            fields.TryGetProperty(refName, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

        private static double? GetDoubleField(JsonElement fields, string refName)
        {
            if (!fields.TryGetProperty(refName, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String => double.TryParse(el.GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null,
                _ => null
            };
        }

        private static string GetIdentityName(JsonElement fields, string refName)
        {
            var displayName = GetIdentityDisplayName(fields, refName);
            return string.IsNullOrWhiteSpace(displayName)
                ? GetIdentityUniqueName(fields, refName)
                : displayName;
        }

        private static string GetIdentityDisplayName(JsonElement fields, string refName)
        {
            if (!fields.TryGetProperty(refName, out var el) || el.ValueKind != JsonValueKind.Object)
                return string.Empty;
            if (el.TryGetProperty("displayName", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static string GetIdentityUniqueName(JsonElement fields, string refName)
        {
            if (!fields.TryGetProperty(refName, out var el) || el.ValueKind != JsonValueKind.Object)
                return string.Empty;
            if (el.TryGetProperty("uniqueName", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static double StateToPercent(string state) =>
            state?.Trim().ToLowerInvariant() switch
            {
                "closed" or "done" or "completed" or "resolved" or "removed" => 100,
                _ => 0
            };

        /// <summary>Avanca <paramref name="days"/> dias uteis (seg-sex) a partir de <paramref name="start"/>.</summary>
        private static DateTime AddWorkingDays(DateTime start, int days)
            => ProjectCalendarService.AddWorkingDays(start, days);

        // Normaliza apenas com trim + minusculas, preservando espacos e underscores
        // para nao colapsar campos distintos (ex.: "Data_Inicio" vs "Data Inicio").
        private static string Normalize(string value) =>
            value.Trim().ToLowerInvariant();

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
