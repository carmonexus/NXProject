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

        public sealed record DevOpsUserInfo(string Name, string Email);

        public sealed record DevOpsTeamInfo(string Id, string Name);

        private sealed record TfsAuthContext(
            string OrgBase,
            string TeamProject,
            AuthenticationHeaderValue Authorization);

        private static TfsAuthContext CreateTfsAuthContext(
            TfsConnectionOptions options,
            string purpose,
            bool requireTeamProject = true)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var orgBase = options.OrganizationUrl?.Trim().TrimEnd('/') ?? string.Empty;
            var project = options.TeamProject?.Trim() ?? string.Empty;
            var pat = options.PersonalAccessToken?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(orgBase) ||
                (requireTeamProject && string.IsNullOrWhiteSpace(project)) ||
                string.IsNullOrWhiteSpace(pat))
            {
                throw new InvalidOperationException(
                    requireTeamProject
                        ? $"Conexão DevOps incompleta para {purpose}: informe URL, Team Project e PAT."
                        : $"Conexão DevOps incompleta para {purpose}: informe URL e PAT.");
            }

            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
            return new TfsAuthContext(orgBase, project, auth);
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
            { "Perc_Alocacao", "Perc_Alocação", "Perc_Aloc", "PercAloc", "Perc Aloc", "Percentual Alocacao", "Percentual_Alocacao" };
        private static readonly string[] PercConclusaoFieldNames =
            { "Perc_Conclusao", "Perc_Conclusão", "PercConclusao", "Percentual Conclusao", "Percentual_Conclusao" };
        private static readonly string[] TipoCentroCustoFieldNames =
            { "Tipo_Centro_Custo", "TipoCentroCusto", "Tipo Centro Custo" };

        public static async Task<ImportResult> ImportAsync(
            TfsConnectionOptions options,
            IProgress<string>? progress = null,
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
            progress?.Report("Conectando e lendo os campos do DevOps...");
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
            progress?.Report("Carregando as sprints (iterations) do projeto...");
            var allSprints = await LoadIterationsAsync(
                orgBase, options.TeamProject, authHeader, cancellationToken);
            var sprintStarts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in allSprints)
                if (!string.IsNullOrEmpty(s.Path))
                    sprintStarts[s.Path!] = s.Start;

            // 2) Query recursiva de links hierarquicos a partir da raiz.
            progress?.Report("Consultando a hierarquia (Epic → Feature → Story)...");
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
                "Microsoft.VSTS.Common.StackRank",
                "Microsoft.VSTS.Common.BacklogPriority"
            };
            var syncVersionRef = ResolveField(fieldMap, options.SyncVersionFieldName, new[] { "Sync_version", "SyncVersion", "Sync Version" });
            var syncNameRef    = ResolveField(fieldMap, options.SyncNameFieldName,    new[] { "Sync_Name", "SyncName", "Sync Name" });
            // Tipo do EPIC (opcional): EPIC de BACKLOG não soma horas no total do projeto.
            var epicTypeRef = options.EpicTypeFieldEnabled && !string.IsNullOrWhiteSpace(options.EpicTypeFieldName)
                ? ResolveField(fieldMap, options.EpicTypeFieldName, new[] { options.EpicTypeFieldName, "EPIC_TYPE", "Tipo_Epic" })
                : null;
            if (epicTypeRef != null && !requestedFields.Contains(epicTypeRef)) requestedFields.Add(epicTypeRef);

            // Campos do work item raiz (Project) usados no apontamento de horas.
            var pepElementRef  = ResolveField(fieldMap, null, new[] { "Elemento_PEP", "Elemento PEP", "ElementoPEP" });
            var pepProjectRef  = ResolveField(fieldMap, null, new[] { "Nome_Projeto_PEP", "Nome Projeto PEP", "NomeProjetoPEP" });
            if (pepElementRef != null && !requestedFields.Contains(pepElementRef)) requestedFields.Add(pepElementRef);
            if (pepProjectRef != null && !requestedFields.Contains(pepProjectRef)) requestedFields.Add(pepProjectRef);

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

            progress?.Report($"Baixando {allIds.Count} work item(s) em lote...");
            var items = await LoadWorkItemsAsync(
                orgBase, authHeader, allIds, requestedFields, cancellationToken, expandRelations: true);

            progress?.Report("Montando o cronograma...");
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
                FilePath = null,
                DevOpsProjectOwner = string.IsNullOrWhiteSpace(rootItem.AssigneeName) ? null : rootItem.AssigneeName,
                PepElement     = ReadFieldText(rootItem, pepElementRef),
                PepProjectName = ReadFieldText(rootItem, pepProjectRef)
            };
            if (sprintDuration.HasValue)
                project.SprintDurationDays = sprintDuration.Value;
            // Processo do DevOps (Agile/Scrum/CMMI/Basic): define o campo de ordem do backlog
            // e é exibido no banner e na config do portfólio.
            project.DevOpsProcess = await LoadProcessNameAsync(orgBase, options.TeamProject, authHeader, cancellationToken);
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
                EpicTypeRef = epicTypeRef,
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

            // Work item sem StackRank/BacklogPriority no DevOps: o NX CALCULA o rank que
            // falta a partir da posição em que o item veio, para a ordem ficar estável e
            // visível corretamente. O valor calculado sobe para o TFS no próximo
            // Export → Sincronizar (o Sync grava StackRank).
            var ranksCalculados = FillMissingBacklogRanks(project.Tasks);
            if (ranksCalculados.Count > 0)
            {
                // Informativo (não é falha nem aviso): o NX já mostra na ordem correta. Uma
                // linha só — antes listava cada item e inflava a contagem de "Avisos/Erros".
                context.Report.LogInfo($"Ordem do backlog: {ranksCalculados.Count} item(ns) vieram do DevOps sem StackRank. "
                    + "O NXProject calculou a ordem pela posição recebida e já mostra na ordem correta; "
                    + "use Export → Sincronizar para gravar esse rank no TFS.");
            }

            // Etapa 2: leitura separada dos links de predecessora via WIQL.
            progress?.Report("Lendo os links de predecessoras...");
            var depLinks = await LoadDependencyLinksAsync(
                orgBase, options.TeamProject, authHeader, allIds, cancellationToken);
            var externalPredTfsIds = ApplyTfsPredecessors(project.Tasks, depLinks);
            RepositionMarcosAfterPredecessors(project.Tasks);
            foreach (var t in project.Tasks)
                t.RecalcSummary();

            if (project.Tasks.Count == 0)
                throw new InvalidOperationException(
                    "Nenhum Epic/Feature/Story encontrado abaixo do work item raiz informado.");

            // Etapa 3: resolve predecessoras externas (fora do escopo deste import).
            if (externalPredTfsIds.Count > 0)
            {
                progress?.Report("Resolvendo predecessoras externas...");
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

            public bool HasIssues => Log.Any(e => e.Level == SyncLogLevel.Error);
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
            public bool AllowStartedOverwrite { get; init; }
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
            public DateTime? LocalFinish => GetTfsFinishDate(Task);
            public string TfsType => Task.TfsType ?? "";
            public int TfsId => Task.TfsId ?? 0;
            // Atividade já iniciada (% > 0) não deve ser sobrescrita automaticamente no merge.
            public double LocalPercentComplete => Task.PercentComplete;
            public bool IsStarted => Task.PercentComplete > 0.0001;
            public bool CanOverwrite => AllowStartedOverwrite || !IsStarted;
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
        /// <summary>
        /// Falha se houver atividades com o MESMO ID interno (estado inconsistente que
        /// poderia criar/atualizar o work item errado). Chamado no início do Sync.
        /// </summary>
        public static void EnsureNoDuplicateTaskIds(Project project)
        {
            var all = new List<ProjectTask>();
            void Walk(IEnumerable<ProjectTask> ts)
            {
                foreach (var t in ts) { all.Add(t); Walk(t.Children); }
            }
            Walk(project.Tasks);

            var dupIds = all.GroupBy(t => t.Id).Where(g => g.Count() > 1).OrderBy(g => g.Key).ToList();
            if (dupIds.Count > 0)
                throw new InvalidOperationException(
                    "Sincronização bloqueada: existem atividades com o MESMO ID interno — " +
                    string.Join("; ", dupIds.Select(g => $"ID {g.Key}: " + string.Join(" | ", g.Select(t => $"\"{t.Name}\"")))) +
                    ". Feche sem salvar e reabra o cronograma antes de sincronizar.");
        }

        /// <summary>
        /// Falha se alguma Story tiver duas Tasks filhas com o MESMO nome — a recuperação
        /// de vínculo por nome e o merge do Task Plan dependem do nome ser único na Story.
        /// </summary>
        public static void EnsureNoDuplicateTaskNamesInStory(Project project)
        {
            var problems = new List<string>();
            void Walk(IEnumerable<ProjectTask> ts)
            {
                foreach (var t in ts)
                {
                    if (IsStoryType(t.TfsType))
                    {
                        var dups = t.Children
                            .Where(c => IsTaskType(c.TfsType) && !string.IsNullOrWhiteSpace(c.Name))
                            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1)
                            .ToList();
                        foreach (var g in dups)
                            problems.Add($"Story \"{t.Name}\": Task \"{g.Key}\" (×{g.Count()})");
                    }
                    Walk(t.Children);
                }
            }
            Walk(project.Tasks);

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "Sincronização bloqueada: a mesma Story não pode ter duas Tasks com o MESMO nome — " +
                    string.Join("; ", problems) +
                    ". Renomeie ou remova as duplicadas antes de sincronizar.");
        }

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

            // Falha RÁPIDA (antes de qualquer chamada de rede): ID interno duplicado
            // e Tasks de mesmo nome na mesma Story.
            EnsureNoDuplicateTaskIds(project);
            EnsureNoDuplicateTaskNamesInStory(project);

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
            // Tipo do EPIC (opcional): só é gravado de volta quando MUDOU no cronograma.
            var syncEpicTypeRef = options.EpicTypeFieldEnabled && !string.IsNullOrWhiteSpace(options.EpicTypeFieldName)
                ? ResolveField(fieldMap, options.EpicTypeFieldName, new[] { options.EpicTypeFieldName, "EPIC_TYPE", "Tipo_Epic" })
                : null;

            // Campo de aprovação da Task (opcional): a sincronização OFICIALIZA a aprovação —
            // uma Task que chega como não aprovada sai daqui aprovada.
            var approvedRef = options.ApprovedFieldEnabled && !string.IsNullOrWhiteSpace(options.ApprovedFieldName)
                ? ResolveField(fieldMap, options.ApprovedFieldName, new[] { options.ApprovedFieldName, "Approved", "Aprovado" })
                : null;

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

            // Top-down: pais antes dos filhos (garante criar o pai antes de criar/reparentar o filho).
            var tasks = new List<ProjectTask>();
            CollectLinkedTasks(project.Tasks, tasks);
            var tasksById = tasks
                .GroupBy(t => t.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var report = new SyncReport();

            // Recalcula a ordem (StackRank) conforme a árvore do NXProject e AVISA quando
            // isso vai reordenar o backlog do DevOps (a ordem do cronograma sobrescreve o TFS).
            var rankChanges = new List<StackRankChange>();
            ApplyDesiredStackRanks(project.Tasks, rankChanges);
            if (rankChanges.Count > 0)
            {
                report.LogWarning($"⚠ Ordem do backlog: {rankChanges.Count} item(ns) terao a POSICAO reescrita no DevOps "
                    + "para refletir a ordem do cronograma. Se a ordem no NXProject estiver errada, ela sera propagada ao TFS — "
                    + "confira antes de continuar.");
                foreach (var c in rankChanges)
                    report.LogWarning($"    • \"{c.Name}\" (em \"{c.Parent}\"): rank "
                        + $"{(c.FromRank.HasValue ? c.FromRank.Value.ToString("0") : "sem rank")} → {c.ToRank:0}");
            }

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
            requested.Add("Microsoft.VSTS.Common.StackRank");
            requested.Add("Microsoft.VSTS.Common.BacklogPriority");

            // Campos Custom DevOps — necessário para comparar valor atual antes de enviar patch
            foreach (var kv in options.TypeFieldMappings)
                foreach (var fd in kv.Value.CustomDevopsFields)
                    if (!string.IsNullOrWhiteSpace(fd.Field) && !requested.Contains(fd.Field))
                        requested.Add(fd.Field);

            var current = existingIds.Count > 0
                ? await LoadWorkItemsAsync(orgBase, auth, existingIds, requested, cancellationToken, expandRelations: true)
                : new Dictionary<int, WorkItem>();

            var preCreatedIds = new List<int>();

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
                    await PostPlanObservationAsync(options, task, report, cancellationToken);
                    continue;
                }

                var createHoursRef  = ResolveForType(task.TfsType, c => c.EffortField,        hoursRef);
                var createStartRef  = ResolveForType(task.TfsType, c => c.StartField,         startRef);
                var createFinishRef = ResolveForType(task.TfsType, c => c.FinishField,        finishRef);
                var createPercAloc  = ResolveForType(task.TfsType, c => c.PercAlocField,      percAlocRef);
                var createPercConc  = ResolveForType(task.TfsType, c => c.PercConclusaoField, percConclusaoRef);

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
                    classificationFields: classFields,
                    percConcRef: createPercConc,
                    approvedRef: approvedRef,
                    process: project.DevOpsProcess);
                var newId = await CreateWorkItemAsync(orgBase, auth, options.TeamProject, createType, createOps, cancellationToken);
                task.TfsId = newId;
                task.TfsParentId = desiredParent;
                task.IsPendingTfsCreate = false;
                preCreatedIds.Add(newId);
                report.Created++;
                report.LogSuccess($"{createType} - #{newId} ({task.Name}): criado.");
                await PostPlanObservationAsync(options, task, report, cancellationToken);
            }

            if (preCreatedIds.Count > 0)
            {
                var createdItems = await LoadWorkItemsAsync(
                    orgBase,
                    auth,
                    preCreatedIds,
                    requested,
                    cancellationToken,
                    expandRelations: true);
                foreach (var item in createdItems)
                    current[item.Key] = item.Value;
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

                    // Task só grava/sincroniza sob uma STORY. Se o ancestral vinculado mais
                    // próximo não é Story (ex.: a Story pai ainda não ganhou o ID DevOps),
                    // pula com aviso — nunca cria/reparenta a Task sob Feature/Epic/Project.
                    var parentViolation = TaskParentViolation(task);
                    if (parentViolation != null)
                    {
                        report.LogWarning($"{TaskSyncLabel(task)} ({task.Name}): Task só pode ficar sob uma Story no DevOps — o pai vinculado mais próximo é \"{parentViolation}\". Sincronize/vincule a Story pai primeiro.");
                        continue;
                    }

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
                            await PostPlanObservationAsync(options, task, report, cancellationToken);
                        }
                        else
                        {
                            var createOps = BuildCreateOps(task, desiredParent, orgBase, createHoursRef, createStartRef, createFinishRef, tasksById, options.SyncPredecessorLinks, createPercAloc, originalHoursRef, remainingHoursRef, realizedHoursRef, options.ExtraCreateFields, classFields, createPercConc, approvedRef, project.DevOpsProcess);
                            var newId = await CreateWorkItemAsync(orgBase, auth, options.TeamProject, createType, createOps, cancellationToken);
                            task.TfsId = newId;
                            task.TfsParentId = desiredParent;
                            task.IsPendingTfsCreate = false;
                            report.Created++;
                            report.LogSuccess($"{createType} - #{newId} ({task.Name}): criado.");
                            await PostPlanObservationAsync(options, task, report, cancellationToken);
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
                    // Se o TFS tem versão maior, só registra conflito depois de comparar
                    // os atributos que realmente seriam gravados pelo NXProject.
                    int? versionAheadTfsVersion = null;
                    string? versionAheadSavedBy = null;
                    bool versionAheadByCurrentUser = false;
                    if (syncVersionRef != null && task.SyncVersion.HasValue)
                    {
                        var tfsVersion = (int)(ReadDouble(wi, syncVersionRef) ?? 0);
                        bool forcedOverwrite = forceOverwriteIds != null && task.TfsId.HasValue && forceOverwriteIds.Contains(task.TfsId.Value);
                        if (!forcedOverwrite && tfsVersion > task.SyncVersion.Value)
                        {
                            var whoSaved = ReadSyncUserName(wi, syncNameRef);
                            versionAheadTfsVersion = tfsVersion;
                            versionAheadSavedBy = whoSaved;
                            versionAheadByCurrentUser = IsCurrentSyncUser(whoSaved);
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

                    if (task.Description != null || !string.IsNullOrWhiteSpace(task.Justificativa))
                    {
                        var desiredDesc = MergeJustificativa(task.Description, task.Justificativa);
                        var currentDesc = wi.Description ?? string.Empty;
                        // A descrição no DevOps é HTML (pode ter formatação, tabelas e imagens),
                        // mas em grades/planilhas ela circula como texto puro. Se o texto desejado
                        // for só a projeção em texto do HTML atual, NÃO regrava: seria trocar o
                        // conteúdo rico por texto simples sem o usuário ter mudado nada.
                        bool sameAsPlainText =
                            !string.IsNullOrWhiteSpace(currentDesc) &&
                            string.Equals(ToPlainText(desiredDesc), ToPlainText(currentDesc), StringComparison.Ordinal);

                        if (!string.Equals(desiredDesc.Trim(), currentDesc.Trim(), StringComparison.Ordinal) && !sameAsPlainText)
                        {
                            ops.Add(PatchAdd("/fields/System.Description", desiredDesc));
                            changes.Add("descrição");
                        }
                    }

                    // % conclusão (Perc_Conclusao) — a Task também tem o campo no DevOps.
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

                    // Aprovação da Task: sincronizar oficializa. Se o campo está vazio ou
                    // "não aprovado", grava aprovado; já aprovado não gera operação.
                    if (isTask && approvedRef != null)
                    {
                        var currentApproved = IsApprovedValue(ReadFieldText(wi, approvedRef));
                        // Com valor definido no cronograma (grade do Tech Lead / Task Plan),
                        // manda o que está aqui — e só quando mudou. Sem valor, sincronizar
                        // apenas OFICIALIZA a aprovação.
                        var desiredApproved = task.Approved ?? true;
                        if (desiredApproved != currentApproved)
                        {
                            ops.Add(PatchAdd($"/fields/{approvedRef}",
                                ApprovedWriteValue(orgBase, wi, approvedRef, desiredApproved)));
                            changes.Add(desiredApproved ? "aprovação" : "aprovação removida");
                        }
                    }

                    // Tipo do EPIC: grava só se mudou (compara com o que está no DevOps).
                    if (syncEpicTypeRef != null && IsEpicType(task.TfsType)
                        && !string.IsNullOrWhiteSpace(task.EpicType))
                    {
                        var desiredEpicType = NormalizeEpicType(task.EpicType);
                        var currentEpicType = NormalizeEpicType(ReadFieldText(wi, syncEpicTypeRef));
                        if (desiredEpicType != null && !string.Equals(desiredEpicType, currentEpicType, StringComparison.OrdinalIgnoreCase))
                        {
                            ops.Add(PatchAdd($"/fields/{syncEpicTypeRef}", desiredEpicType));
                            changes.Add($"tipo do EPIC: {currentEpicType ?? "(vazio)"}→{desiredEpicType}");
                        }
                    }

                    // ── Campos exclusivos de Story / Feature / Epic (não Task) ──────────
                    if (!isTask)
                    {
                        // Tags (ex.: "Block") — sincroniza se o conjunto mudou.
                        if (!TagsEqual(task.Tags, wi.Tags))
                        {
                            ops.Add(PatchAdd("/fields/System.Tags", NormalizeTagsForWrite(task.Tags)));
                            changes.Add("tags");
                        }

                        // Ordem (StackRank) — sincroniza se o rank desejado mudou.
                        if (task.TfsStackRank.HasValue)
                        {
                            var currentRank = GetBacklogRank(wi.Fields);
                            if (currentRank == null || Math.Abs(currentRank.Value - task.TfsStackRank.Value) > 0.0001)
                            {
                                // Agile/CMMI ordenam por StackRank; Scrum, por BacklogPriority —
                                // grava nos campos que o work item tem, senao a ordem nao muda no board.
                                foreach (var campo in BacklogRankFieldsToWrite(wi.Fields, project.DevOpsProcess))
                                    ops.Add(PatchAdd($"/fields/{campo}", task.TfsStackRank.Value));
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
                            // % de alocação é decimal (2 casas) no NXProject/XML/TFS.
                            var primaryAloc = task.Resources.Count > 0 ? Math.Round(task.Resources[0].AllocationPercent, 2) : 100.0;
                            var currentAloc = ReadDouble(wi, typePercAloc);
                            if (currentAloc == null || Math.Abs(currentAloc.Value - primaryAloc) > 0.005)
                            {
                                ops.Add(PatchAdd($"/fields/{typePercAloc}", primaryAloc));
                                var oldA = currentAloc.HasValue ? $"{currentAloc.Value:0.##}%→" : "";
                                changes.Add($"% aloc.: {oldA}{primaryAloc:0.##}%");
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

                        // Data Fim: o modelo guarda Finish como limite exclusivo; o TFS deve receber a data exibida.
                        var tfsFinish = GetTfsFinishDate(task);
                        if (typeFinishRef != null && tfsFinish.HasValue)
                        {
                            var currentFinish = ReadDate(wi, typeFinishRef);
                            if (currentFinish == null || currentFinish.Value.Date != tfsFinish.Value.Date)
                            {
                                ops.Add(PatchAdd($"/fields/{typeFinishRef}", FormatDateForTfs(tfsFinish.Value)));
                                changes.Add($"fim: {tfsFinish.Value:dd/MM}");
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

                        // Priority (Microsoft.VSTS.Common.Priority). Padrão DevOps: 1–4;
                        // faixa personalizada opcional na configuração TFS.
                        var currentPriority = ReadDouble(wi, "Microsoft.VSTS.Common.Priority");
                        int rawPriority = task.Priority is > 0 ? task.Priority.Value : 4;
                        int desiredPriority = ClampTaskPriority(options, rawPriority);
                        if (currentPriority == null || (int)currentPriority.Value != desiredPriority)
                        {
                            ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Priority", desiredPriority));
                            changes.Add($"Priority: {desiredPriority}");
                        }
                        if (!task.Priority.HasValue && currentPriority.HasValue && currentPriority.Value > 0)
                            task.Priority = (int)currentPriority.Value;

                        // Mesmo com Perc_Conclusao configurado, 100% no cronograma fecha a Task pelo estado.
                        if (task.PercentComplete >= 100)
                        {
                            var currentState = task.TfsState?.Trim();
                            if (!string.Equals(currentState, "Closed", StringComparison.OrdinalIgnoreCase))
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

                    // TRAVA: Epic já criado no DevOps (TfsId>0) NÃO muda de pai (Work Item
                    // tipo Project) pelo NXProject — só direto no DevOps. Evita reparentar
                    // para outro root ao alternar cronogramas.
                    if (reparent && task.TfsId is > 0 &&
                        string.Equals(task.TfsType?.Trim(), "Epic", StringComparison.OrdinalIgnoreCase))
                    {
                        report.LogWarning($"Epic #{task.TfsId} ({task.Name}): a mudança de Work Item pai não é sincronizada pelo NXProject (Epic já criado). Faça a mudança de EPIC para outro Work Item Project no DevOps.");
                        reparent = false;
                    }

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

                    if (versionAheadTfsVersion.HasValue)
                    {
                        var isStoryOrTaskConflictType = isTask || IsStoryType(task.TfsType);
                        if (ShouldRegisterSyncConflict(
                                tfsVersionAhead: true,
                                hasPendingWrites: ops.Count > 0,
                                isStoryOrTask: isStoryOrTaskConflictType,
                                isCurrentSyncUser: versionAheadByCurrentUser,
                                tfsState: wi.State))
                        {
                            task.HasSyncConflict = true;
                            report.Conflicts++;
                            var by = string.IsNullOrWhiteSpace(versionAheadSavedBy) ? "" : $" (por {versionAheadSavedBy})";
                            report.LogError($"⚠ CONFLITO {TaskSyncLabel(task)} ({task.Name}): versão TFS={versionAheadTfsVersion.Value} > local={task.SyncVersion!.Value}{by}. Alterações descartadas — reimporte para atualizar.");
                            report.ConflictItems.Add(new SyncConflictItem
                            {
                                Task         = task,
                                TfsVersion   = versionAheadTfsVersion.Value,
                                LocalVersion = task.SyncVersion.Value,
                                ChangedBy    = versionAheadSavedBy ?? "",
                                TfsTitle     = wi.Title ?? "",
                                TfsState     = wi.State ?? "",
                                TfsTags      = wi.Tags ?? "",
                                TfsHours     = ReadDouble(wi, typeHoursRef),
                                TfsStart     = ReadDate(wi, typeStartRef),
                                TfsFinish    = ReadDate(wi, typeFinishRef),
                            });
                            report.Skipped++;
                            continue;
                        }

                        if (ops.Count == 0)
                        {
                            task.SyncVersion = versionAheadTfsVersion.Value;
                            task.HasSyncConflict = false;
                        }
                        else if (versionAheadByCurrentUser && !IsClosedState(wi.State))
                        {
                            var previousLocalVersion = task.SyncVersion!.Value;
                            task.SyncVersion = versionAheadTfsVersion.Value;
                            task.HasSyncConflict = false;
                            report.LogWarning($"{TaskSyncLabel(task)} ({task.Name}): versão TFS={versionAheadTfsVersion.Value} > local={previousLocalVersion}, mas a última gravação foi do usuário atual ({versionAheadSavedBy}); sincronização liberada.");
                        }
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

            // Atualiza o resumo de alocação (dono + horas das Tasks) por Story para a decomposição
            // do HH na alocação. Best-effort: não falha o Sync se o DevOps não responder.
            try { await UpdateTaskAllocationSummariesAsync(project, options, null, cancellationToken); }
            catch { /* resumo é auxiliar; não invalida o Sync já concluído */ }

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
        /// <summary>
        /// Reordenação que o Sync VAI gravar no DevOps: item cujo rank muda de valor, ou
        /// seja, cuja posição no backlog do TFS será reescrita pela ordem do cronograma.
        /// </summary>
        public sealed record StackRankChange(string Name, double? FromRank, double ToRank, string Parent);

        private static void ApplyDesiredStackRanks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> siblings,
            List<StackRankChange>? changes = null,
            string parentName = "raiz")
        {
            double? prev = null;
            for (int i = 0; i < siblings.Count; i++)
            {
                var child = siblings[i];
                if (child.TfsId.HasValue)
                {
                    var rankBefore = child.TfsId.Value > 0 ? child.TfsStackRank : null;
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

                    // Rank mudou = a posição desse item NO BACKLOG DO TFS será reescrita
                    // pela ordem do cronograma. O usuário precisa saber ANTES de exportar:
                    // se a ordem no NX veio errada (ex.: item importado sem rank), o Sync
                    // propaga o erro para o DevOps.
                    if (changes != null && child.TfsId.Value > 0
                        && (rankBefore == null || Math.Abs(rankBefore.Value - desired) > 0.0001))
                        changes.Add(new StackRankChange(child.Name ?? "", rankBefore, desired, parentName));
                }

                ApplyDesiredStackRanks(child.Children, changes, child.Name ?? parentName);
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
            var id = task.TfsId is > 0 ? $"{task.TfsId}:T" : $"{task.Id}:I";
            return $"{type} - {id}";
        }

        /// <summary>
        /// Task comum só pode ficar sob uma STORY no DevOps. Marco-DevOps é exceção:
        /// ele é criado como Task com tag MARCO-PROJECT e pode ficar sob Epic/Feature/Story
        /// ou outro tipo vinculado usado como contêiner do marco.
        /// Retorna o tipo do pai inválido quando o ancestral vinculado mais próximo NÃO é
        /// Story; null se ok.
        /// </summary>
        public static string? TaskParentViolation(ProjectTask task)
        {
            if (!IsTaskType(task.TfsType)) return null;
            if (IsDevOpsMilestoneType(task.TfsType)) return null;
            for (var p = task.Parent; p != null; p = p.Parent)
                if (p.TfsId is > 0)
                    return IsStoryType(p.TfsType) ? null : (p.TfsType ?? "?");
            return null;   // sem ancestral vinculado: o fluxo de "pai sem vínculo" já trata
        }

        private static int ResolveDesiredParent(ProjectTask task, int rootWorkItemId)
        {
            // Sem pai no cronograma, só Epic pode nascer direto sob o Work Item Project.
            // Feature/Story/Task órfãs não podem cair no raiz: isso esconderia perda de
            // hierarquia local e criaria o item no lugar errado no DevOps.
            if (task.Parent == null)
                return string.Equals(task.TfsType?.Trim(), "Epic", StringComparison.OrdinalIgnoreCase)
                    ? rootWorkItemId
                    : 0;

            var p = task.Parent;
            while (p != null)
            {
                if (p.TfsId is > 0)
                {
                    // Marco-DevOps pode ficar sob Epic/Feature/Story ou outro item vinculado,
                    // mas nunca direto sob o Work Item Project raiz.
                    if (IsDevOpsMilestoneType(task.TfsType) && p.TfsId.Value == rootWorkItemId)
                        return 0;
                    return p.TfsId.Value;
                }
                // Pai com TfsId=0 pode ter acabado de ser criado neste loop — valor já atualizado.
                // Pai "No DevOps" (TfsId negativo) é transparente: sobe para o avô.
                p = p.Parent;
            }

            // Tem pai no cronograma, mas NENHUM ancestral com vínculo DevOps: NÃO cai no
            // Work Item Project (senão a Task nasce fora da Story, no root). Retorna 0 →
            // o chamador avisa "crie/vincule o pai primeiro" e pula o item nesta sincronização.
            return 0;
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
                            if (fields.TryGetProperty("System.Tags", out var tg)) wi.Tags = tg.GetString() ?? "";
                            if (wi.Id == 0) continue;
                            result[wi.Id] = wi;
                        }
                    }
                }
                catch { /* ignora erros de itens inacessíveis */ }
            }
            return result;
        }

        /// <summary>
        /// Reposiciona cada marco (Marco-DevOps) pela predecessora que faz sentido na hierarquia:
        /// irmã no mesmo pai, ou o próprio pai. Predecessoras externas não movem o marco na árvore.
        /// </summary>
        private static void RepositionMarcosAfterPredecessors(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> roots)
        {
            var allTasks = new List<ProjectTask>();
            CollectAllTasks(roots, allTasks);
            var byId = allTasks.ToDictionary(t => t.Id);
            var byTfsId = allTasks
                .Where(t => t.TfsId is > 0)
                .GroupBy(t => t.TfsId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var marco in allTasks.Where(t => t.IsMilestone))
            {
                if (marco.Parent == null || marco.PredecessorIds.Count == 0) continue;

                var siblings = marco.Parent.Children;
                siblings.Remove(marco);

                var anchor = FindMarcoPositionAnchor(marco, siblings, byId, byTfsId);
                if (anchor == null)
                {
                    siblings.Add(marco);
                    continue;
                }

                if (ReferenceEquals(anchor, marco.Parent))
                {
                    siblings.Insert(0, marco);
                    continue;
                }

                var idx = siblings.IndexOf(anchor);
                if (idx < 0) { siblings.Add(marco); continue; }
                siblings.Insert(Math.Min(idx + 1, siblings.Count), marco);
            }
        }

        private static ProjectTask? FindMarcoPositionAnchor(
            ProjectTask marco,
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> siblings,
            Dictionary<int, ProjectTask> byId,
            Dictionary<int, ProjectTask> byTfsId)
        {
            ProjectTask? parentAnchor = null;
            ProjectTask? siblingAnchor = null;
            var siblingIndex = -1;
            DateTime? siblingFinish = null;

            foreach (var pid in marco.PredecessorIds)
            {
                var predecessor = ResolvePredecessorForPosition(pid, byId, byTfsId);
                if (predecessor == null)
                    continue;

                if (ReferenceEquals(predecessor, marco.Parent))
                {
                    parentAnchor = predecessor;
                    continue;
                }

                if (!ReferenceEquals(predecessor.Parent, marco.Parent))
                    continue;

                var idx = siblings.IndexOf(predecessor);
                if (idx < 0)
                    continue;

                var finish = ProjectCalendarService.GetInclusiveFinishDate(predecessor.Start, predecessor.Finish);
                if (siblingAnchor == null || finish > siblingFinish || (finish == siblingFinish && idx > siblingIndex))
                {
                    siblingAnchor = predecessor;
                    siblingFinish = finish;
                    siblingIndex = idx;
                }
            }

            return siblingAnchor ?? parentAnchor;
        }

        private static ProjectTask? ResolvePredecessorForPosition(
            int predecessorId,
            Dictionary<int, ProjectTask> byId,
            Dictionary<int, ProjectTask> byTfsId)
        {
            if (byId.TryGetValue(predecessorId, out var byInternalId))
                return byInternalId;

            return predecessorId > 0 && byTfsId.TryGetValue(predecessorId, out var byTfs)
                ? byTfs
                : null;
        }

        private static bool ShouldSyncPredecessors(ProjectTask task) =>
            task.Children.All(c => c.IsMilestone) &&
            (IsStoryType(task.TfsType) || IsTaskType(task.TfsType) ||
             IsEpicOrFeatureType(task.TfsType));

        private static bool CanBeDevOpsPredecessor(ProjectTask task) =>
            task.TfsId is > 0 &&
            !IsNoDevOpsType(task.TfsType) &&
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

        private static string ResolveImportType(string? workItemType, string? tags) =>
            string.Equals(workItemType, "Task", StringComparison.OrdinalIgnoreCase) && HasTag(tags, "MARCO-PROJECT")
                ? "Marco-DevOps"
                : workItemType ?? "";

        private static bool IsMarcoDevOpsItem(WorkItem item) =>
            string.Equals(ResolveImportType(item.WorkItemType, item.Tags), "Marco-DevOps", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Encontra recursivamente todas as Tasks marcadas MARCO-PROJECT abaixo de um item,
        /// descendo por Tasks comuns intermediárias (que não viram nó na árvore do cronograma).
        /// Não desce em Epic/Feature/Story filhos: esses constroem seus próprios marcos.
        /// </summary>
        private static void CollectMarcoDescendants(BuildContext ctx, int parentId, List<WorkItem> result)
        {
            if (!ctx.ChildrenByParent.TryGetValue(parentId, out var children)) return;
            foreach (var childId in children)
            {
                if (!ctx.Items.TryGetValue(childId, out var child)) continue;
                if (IsMarcoDevOpsItem(child))
                {
                    result.Add(child);
                    continue;
                }
                if (IsType(child, "Task"))
                    CollectMarcoDescendants(ctx, childId, result);
            }
        }

        /// <summary>Constrói uma Task marcada MARCO-PROJECT como marco (milestone) filho de Epic/Feature/Story.</summary>
        private static ProjectTask BuildMilestoneChild(BuildContext ctx, WorkItem item, int level, DateTime anchor)
        {
            return new ProjectTask
            {
                Id = item.Id,
                Name = item.Title,
                Level = level,
                IsSummary = false,
                IsMilestone = true,
                Start = anchor,
                Finish = anchor,
                PercentComplete = StateToPercent(item.State),
                TfsId = item.Id,
                TfsParentId = ctx.GetParent(item.Id),
                TfsType = "Marco-DevOps",
                TfsState = item.State,
                Description = item.Description,
                Tags = item.Tags,
                TfsStackRank = item.StackRank,
                TfsIterationPath = item.IterationPath,
                Notes = $"TFS #{item.Id} · Marco-DevOps · {item.State}"
            };
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
                    CanBeDevOpsPredecessor(predecessor))
                {
                    desired.Add(predecessor.TfsId!.Value);
                    continue;
                }

                // 2. O valor armazenado é o próprio TfsId (tarefa de outro escopo ou
                //    salva antes da resolução de IDs). Usa diretamente se > 0.
                if (storedId > 0 && !tasksById.ContainsKey(storedId))
                {
                    // Pode ser TfsId de tarefa interna (já resolvida acima) ou externa.
                    // Se bate com um TfsId do projeto, usa a tarefa interna.
                    if (tasksByTfsId.TryGetValue(storedId, out var byTfs) &&
                        CanBeDevOpsPredecessor(byTfs))
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

            if (desired.Count == 0 && IsDevOpsMilestoneType(task.TfsType))
            {
                var implicitPredecessor = ResolveImplicitMarcoPredecessorTfsId(task);
                if (implicitPredecessor is > 0)
                    desired.Add(implicitPredecessor.Value);
            }

            return invalidPredecessors.Count == 0;
        }

        private static int? ResolveImplicitMarcoPredecessorTfsId(ProjectTask task)
        {
            if (!IsDevOpsMilestoneType(task.TfsType))
                return null;

            var siblings = task.Parent?.Children;
            if (siblings != null)
            {
                var index = siblings.IndexOf(task);
                if (index > 0)
                {
                    for (var i = index - 1; i >= 0; i--)
                    {
                        var previous = siblings[i];
                        if (previous.TfsId is > 0)
                            return previous.TfsId.Value;
                    }
                }
            }

            return task.Parent?.TfsId is > 0 ? task.Parent.TfsId.Value : null;
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
            IReadOnlyList<ClassificationFieldDef>? classificationFields = null,
            string? percConcRef = null,
            string? approvedRef = null,
            string? process = null)
        {
            bool isTaskCreate        = IsTaskType(task.TfsType);
            bool isEpicOrFeatureCreate = IsEpicOrFeatureType(task.TfsType);

            var ops = new List<object>
            {
                PatchAdd("/fields/System.Title", task.Name ?? "Novo item")
            };

            // % conclusão (Perc_Conclusao) — Task também tem o campo no DevOps.
            if (percConcRef != null)
                ops.Add(PatchAdd($"/fields/{percConcRef}", (int)Math.Round(Math.Clamp(task.PercentComplete, 0, 100))));

            // Aprovação: Task criada pela sincronização já nasce aprovada.
            if (isTaskCreate && approvedRef != null)
                ops.Add(PatchAdd($"/fields/{approvedRef}",
                    IsBooleanField(orgBase, approvedRef) ? true : ApprovedTrueValue));

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

            if (!string.IsNullOrWhiteSpace(task.Description))
                ops.Add(PatchAdd("/fields/System.Description", MergeJustificativa(task.Description, task.Justificativa)));

            if (!isTaskCreate)
            {
                if (!string.IsNullOrWhiteSpace(task.Tags))
                    ops.Add(PatchAdd("/fields/System.Tags", NormalizeTagsForWrite(task.Tags)));

                if (!string.IsNullOrWhiteSpace(task.TfsState))
                    ops.Add(PatchAdd("/fields/System.State", task.TfsState.Trim()));

                if (task.TfsStackRank.HasValue)
                    // Criacao: ainda nao ha campos do work item para inspecionar — usa o campo
                    // do processo (Scrum = BacklogPriority; Agile/CMMI/Basic = StackRank).
                    foreach (var campo in BacklogRankFieldsToWrite(null, process))
                        ops.Add(PatchAdd($"/fields/{campo}", task.TfsStackRank.Value));

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
                    // % de alocação decimal (2 casas) na criação no DevOps.
                    var primaryAloc = task.Resources.Count > 0 ? Math.Round(task.Resources[0].AllocationPercent, 2) : 100.0;
                    ops.Add(PatchAdd($"/fields/{percAlocRef}", primaryAloc));
                }

                if (startRef != null && task.Start > DateTime.MinValue.AddYears(1))
                    ops.Add(PatchAdd($"/fields/{startRef}", FormatDateForTfs(task.Start)));

                var tfsFinish = GetTfsFinishDate(task);
                if (finishRef != null && IsClosedState(task.TfsState) && tfsFinish.HasValue)
                    ops.Add(PatchAdd($"/fields/{finishRef}", FormatDateForTfs(tfsFinish.Value)));
            }
            else
            {
                // Task: Original Estimate (Decimal) + Priority=5 na criação.
                var taskHours = task.EstimatedHours ?? task.CurrentHours;
                if (taskHours.HasValue)
                    ops.Add(PatchAdd("/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", taskHours.Value));
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Priority", 4));
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

        /// <summary>Decisão pura (sem rede) de liberar um conflito de versão na sincronização.
        /// Esta decisão só é consultada quando a versão do TFS está à frente da versão local.
        /// No Sync geral, só libera quando a última gravação foi do próprio usuário atual
        /// e o item online ainda não está encerrado. Itens 100%/Closed com versão à frente
        /// ficam em conflito e só podem ser sobrescritos pelo fluxo manual da linha rosa.</summary>
        public static bool ShouldReleaseSyncConflict(bool isCurrentSyncUser, double localPercentComplete, string? tfsState)
            => isCurrentSyncUser && !IsClosedState(tfsState);

        public static bool ShouldRegisterSyncConflict(
            bool tfsVersionAhead,
            bool hasPendingWrites,
            bool isStoryOrTask,
            bool isCurrentSyncUser,
            string? tfsState)
            => tfsVersionAhead
               && hasPendingWrites
               && isStoryOrTask
               && !ShouldReleaseSyncConflict(isCurrentSyncUser, 0, tfsState);

        public static async Task<SyncConflictItem> LoadManualConflictItemAsync(
            ProjectTask task,
            TfsConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (task.TfsId is not > 0)
                throw new InvalidOperationException("A atividade não está vinculada a um item do DevOps.");
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
            var startRef = ResolveField(fieldMap, options.StartFieldName, StartFieldNames);
            var finishRef = ResolveField(fieldMap, options.FinishFieldName, FinishFieldNames);
            var syncVersionRef = ResolveField(fieldMap, options.SyncVersionFieldName, new[] { "Sync_version", "SyncVersion", "Sync Version" });
            var syncNameRef = ResolveField(fieldMap, options.SyncNameFieldName, new[] { "Sync_Name", "SyncName", "Sync Name" });

            string? ResolveForType(string? tfsType, Func<TypeFieldConfig, string?> getter, string? globalRef)
            {
                if (tfsType != null &&
                    options.TypeFieldMappings.TryGetValue(tfsType, out var cfg) &&
                    !string.IsNullOrWhiteSpace(getter(cfg)))
                    return ResolveField(fieldMap, getter(cfg), Array.Empty<string>()) ?? getter(cfg)!.Trim();
                return globalRef;
            }

            var typeHoursRef = ResolveForType(task.TfsType, c => c.EffortField, hoursRef);
            var typeStartRef = ResolveForType(task.TfsType, c => c.StartField, startRef);
            var typeFinishRef = ResolveForType(task.TfsType, c => c.FinishField, finishRef);

            var requested = new List<string>
            {
                "System.Id",
                "System.Title",
                "System.WorkItemType",
                "System.State",
                "System.Tags",
                "System.ChangedBy"
            };
            if (typeHoursRef != null) requested.Add(typeHoursRef);
            if (typeStartRef != null) requested.Add(typeStartRef);
            if (typeFinishRef != null) requested.Add(typeFinishRef);
            if (syncVersionRef != null) requested.Add(syncVersionRef);
            if (syncNameRef != null) requested.Add(syncNameRef);

            var items = await LoadWorkItemsAsync(
                orgBase,
                auth,
                [task.TfsId.Value],
                requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                cancellationToken,
                expandRelations: false);

            if (!items.TryGetValue(task.TfsId.Value, out var wi))
                throw new InvalidOperationException($"Item #{task.TfsId.Value} não encontrado no DevOps.");

            var syncUser = ReadSyncUserName(wi, syncNameRef);
            var changedBy = string.IsNullOrWhiteSpace(syncUser)
                ? ReadSyncUserName(wi, "System.ChangedBy") ?? ""
                : syncUser!;

            return new SyncConflictItem
            {
                Task = task,
                TfsVersion = (int)(ReadDouble(wi, syncVersionRef) ?? 0),
                LocalVersion = task.SyncVersion ?? 0,
                ChangedBy = changedBy,
                AllowStartedOverwrite = true,
                TfsTitle = wi.Title,
                TfsState = wi.State,
                TfsTags = wi.Tags,
                TfsHours = ReadDouble(wi, typeHoursRef),
                TfsStart = ReadDate(wi, typeStartRef),
                TfsFinish = ReadDate(wi, typeFinishRef)
            };
        }

        private static bool IsClosedState(string? state) =>
            state?.Trim().ToLowerInvariant() switch
            {
                "closed" or "resolved" or "done" or "completed" => true,
                _ => false
            };

        private static bool IsActiveState(string? state) =>
            state?.Trim().ToLowerInvariant() switch
            {
                "active" or "actived" => true,
                _ => false
            };

        /// <summary>Normaliza o estado da task numa categoria estável para o resumo:
        /// "Closed", "Active", "New" ou "Other".</summary>
        public static string NormalizeTaskState(string? state) =>
            IsClosedState(state) ? "Closed"
            : IsActiveState(state) ? "Active"
            : IsNewState(state) ? "New"
            : "Other";

        /// <summary>Regra do mapa/alocação: um resumo de task conta quando o estado é Active ou
        /// Closed. Estado vazio = arquivo legado (sem estado gravado) → conta, para não sumir dados.</summary>
        public static bool AllocationCountsState(string? state) =>
            string.IsNullOrWhiteSpace(state) || IsClosedState(state) || IsActiveState(state);

        private static bool IsNewState(string? state) =>
            state?.Trim().ToLowerInvariant() switch
            {
                "new" or "novo" => true,
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

        private static DateTime? GetTfsFinishDate(ProjectTask task)
        {
            if (task.Finish <= DateTime.MinValue.AddYears(1))
                return null;

            return ProjectCalendarService.GetInclusiveFinishDate(task.Start, task.Finish);
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
            public string? EpicTypeRef;
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
                TfsType = ResolveImportType(item.WorkItemType, item.Tags),
                TfsState = item.State,
                Description = item.Description,
                Tags = item.Tags,
                BlockedByChild = summaryBlocked,
                TfsStackRank = item.StackRank,
                TfsIterationPath = item.IterationPath,
                Justificativa = ParseJustificativa(item.Description),
                TipoCentroCusto = ReadTipoCentroCusto(item, ctx.TipoCentroCustoRef),
                // EPIC_TYPE (opcional): BACKLOG tira o EPIC do total de horas do projeto.
                EpicType = NormalizeEpicType(ReadString(item, ctx.EpicTypeRef))
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

            // Marcos (Tasks MARCO-PROJECT) diretamente sob este Epic/Feature, ou sob
            // Tasks comuns intermediárias, viram filhos-marco aqui mesmo.
            var marcoItems = new List<WorkItem>();
            CollectMarcoDescendants(ctx, id, marcoItems);
            foreach (var m in marcoItems)
            {
                var marco = BuildMilestoneChild(ctx, m, level + 1, ctx.ProjectStart);
                marco.Parent = task;
                task.Children.Add(marco);
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

            var effectiveState = stateFixedToActive ? "Active" : item.State;
            // Item encerrado é 100% mesmo com o campo de % vazio ou zerado no DevOps.
            var percentComplete =
                IsClosedState(effectiveState) ? 100
                : ctx.PercConclusaoRef != null && ReadDouble(item, ctx.PercConclusaoRef) is { } pc && pc >= 0 && pc <= 100
                    ? pc
                    : StateToPercent(effectiveState);

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
            DateTime start = ResolveImportStart(explicitStart, baseStart, hasFixedTag, effectiveState, percentComplete);
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
                TfsType = ResolveImportType(item.WorkItemType, item.Tags),
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
            bool hasNonDefaultFactor =
                task.Resources.Any(r => Math.Abs(r.AllocationPercent - 100.0) > 0.01) ||
                task.Resources.Any(r => r.Resource != null && Math.Abs(r.Resource.AvailabilityPercent - 100.0) > 0.01);
            if (!task.FinishFixed && !task.IsMilestone &&
                task.Resources.Count > 0 && hasNonDefaultFactor)
            {
                // Cálculo único e centralizado (mesma fórmula do cronograma e da abertura de arquivo).
                var calcFinish = TaskScheduleService.CalculateFinishFromAssignments(task, task.Start);
                if (calcFinish > task.Start)
                {
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

            // Marcos (Tasks MARCO-PROJECT) diretamente sob esta Story, ou sob
            // Tasks comuns intermediárias, viram filhos-marco aqui mesmo.
            var marcoItems = new List<WorkItem>();
            CollectMarcoDescendants(ctx, item.Id, marcoItems);
            foreach (var m in marcoItems)
            {
                var marco = BuildMilestoneChild(ctx, m, level + 1, task.Finish);
                marco.Parent = task;
                task.Children.Add(marco);
            }

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

            // Ordena irmãos pelo rank do backlog do DevOps. Alguns processos usam
            // StackRank; outros expõem a mesma ordem como BacklogPriority (GetBacklogRank).
            var ordered = OrderSiblingsByBacklogRank(
                list.Select(id => (Id: id, Rank: items.TryGetValue(id, out var w) ? w.StackRank : null)).ToList());

            foreach (var id in ordered)
                yield return id;
        }

        /// <summary>
        /// Preenche o StackRank que o DevOps não trouxe, mantendo a POSIÇÃO em que o item
        /// veio: rank = anterior + passo (ou anterior do primeiro ranqueado − passo, quando
        /// o item está antes de todos). Sem nenhum rank no grupo, cria a escala do zero.
        /// Devolve os nomes dos itens calculados — eles precisam de um Sincronizar para o
        /// valor subir ao TFS. Mesma escala do <see cref="ApplyDesiredStackRanks"/> (passo 1000).
        /// </summary>
        public static List<string> FillMissingBacklogRanks(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> siblings)
        {
            const double step = 1000.0;
            const double baseRank = 1000000000.0;
            var calculated = new List<string>();

            void Fill(System.Collections.ObjectModel.ObservableCollection<ProjectTask> group)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    var item = group[i];
                    if (!item.TfsStackRank.HasValue)
                    {
                        // Anterior já ranqueado (inclusive um calculado agora) define a base.
                        double? prev = null;
                        for (int p = i - 1; p >= 0; p--)
                            if (group[p].TfsStackRank.HasValue) { prev = group[p].TfsStackRank; break; }

                        double? next = null;
                        for (int n = i + 1; n < group.Count; n++)
                            if (group[n].TfsStackRank.HasValue) { next = group[n].TfsStackRank; break; }

                        item.TfsStackRank = prev.HasValue && next.HasValue && next.Value - prev.Value > 0.0
                            ? prev.Value + (next.Value - prev.Value) / 2.0   // encaixa entre os vizinhos
                            : prev.HasValue ? prev.Value + step              // depois do anterior
                            : next.HasValue ? next.Value - step              // antes do primeiro ranqueado
                            : baseRank + i * step;                           // grupo inteiro sem rank
                        calculated.Add(item.Name ?? "");
                    }
                    Fill(item.Children);
                }
            }

            Fill(siblings);
            return calculated;
        }

        /// <summary>
        /// Ordem dos irmãos pelo rank do backlog, preservando a posição de quem NÃO tem
        /// rank: o item sem rank herda o rank do irmão ranqueado anterior (fica logo depois
        /// dele) em vez de ser jogado para o fim do grupo — antes, um único item sem rank
        /// no meio da lista descia para o final e a hierarquia saía fora da ordem do TFS.
        /// Público para teste: é a regra que garante "a ordem do cronograma = ordem do TFS".
        /// </summary>
        public static IReadOnlyList<int> OrderSiblingsByBacklogRank(IReadOnlyList<(int Id, double? Rank)> siblings)
        {
            var keys = new List<(int Id, double Key, int Index)>(siblings.Count);
            double? previousRank = null;
            for (int i = 0; i < siblings.Count; i++)
            {
                var rank = siblings[i].Rank;
                if (rank.HasValue) previousRank = rank.Value;
                // Sem nenhum ranqueado antes: fica no começo (onde a consulta o trouxe).
                keys.Add((siblings[i].Id, previousRank ?? double.MinValue, i));
            }

            return keys
                .OrderBy(x => x.Key)
                .ThenBy(x => x.Index) // estável: empate mantém a ordem da consulta
                .Select(x => x.Id)
                .ToList();
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

        // Cache por organização do mapa nome→referenceName dos campos (estável na sessão);
        // evita uma chamada extra de API por Story no fetch das Tasks filhas.
        private static readonly Dictionary<string, Dictionary<string, string>> FieldMapCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Limpa os caches de metadados do DevOps (mapa e tipo de campos). Chamado quando a
        /// configuração muda — organização, projeto ou PAT novo não devem reaproveitar nada
        /// lido com a conexão anterior.
        /// </summary>
        public static void ResetMetadataCaches()
        {
            lock (FieldMapCache)  FieldMapCache.Clear();
            lock (FieldTypeCache) FieldTypeCache.Clear();
        }

        private static async Task<Dictionary<string, string>> LoadFieldMapCachedAsync(
            string orgBase, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            lock (FieldMapCache)
                if (FieldMapCache.TryGetValue(orgBase, out var cached))
                    return cached;
            var map = await LoadFieldMapAsync(orgBase, auth, ct);
            lock (FieldMapCache)
                FieldMapCache[orgBase] = map;
            return map;
        }

        private static async Task<Dictionary<string, string>> LoadFieldMapAsync(
            string orgBase, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            var url = $"{orgBase}/_apis/wit/fields?{ApiVersion}";
            using var doc = await GetJsonAsync(url, auth, ct);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var refName = f.TryGetProperty("referenceName", out var r) ? r.GetString() : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(refName)) continue;

                    map[Normalize(name)] = refName!;
                    // Tipo declarado no processo (boolean, string, integer...): usado para gravar
                    // o valor no formato certo mesmo quando o campo ainda está vazio no item.
                    if (f.TryGetProperty("type", out var t) && t.GetString() is { } tp)
                        types[refName!] = tp;
                }
            }
            lock (FieldTypeCache) FieldTypeCache[orgBase] = types;
            return map;
        }

        /// <summary>Tipo declarado de cada campo (por referenceName), por organização.</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> FieldTypeCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool IsBooleanField(string orgBase, string? refName)
        {
            if (string.IsNullOrEmpty(refName)) return false;
            lock (FieldTypeCache)
                return FieldTypeCache.TryGetValue(orgBase, out var types)
                    && types.TryGetValue(refName!, out var t)
                    && string.Equals(t, "boolean", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Carrega a arvore de iterations (sprints) do team project. Cada sprint com
        /// startDate vira um <see cref="Sprint"/> com nome (folha), caminho completo
        /// (System.IterationPath, ex.: "Projeto\\Pasta\\Sprint"), inicio e fim. A
        /// numeracao sequencial (1..N) e atribuida depois, na ordem cronologica.
        /// </summary>
        /// <summary>Lê o processo do Team Project (Agile/Scrum/CMMI/Basic) a partir das opções
        /// de conexão. Devolve null se indisponível. Usado pelo Discovery do Portfólio.</summary>
        public static async Task<string?> GetProcessAsync(
            TfsConnectionOptions options, CancellationToken ct = default)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.OrganizationUrl)
                || string.IsNullOrWhiteSpace(options.TeamProject)
                || string.IsNullOrWhiteSpace(options.PersonalAccessToken))
                return null;
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));
            return await LoadProcessNameAsync(orgBase, options.TeamProject, auth, ct);
        }

        /// <summary>Lê o processo do Team Project (Agile, Scrum, CMMI, Basic) via API de
        /// projetos: capabilities.processTemplate.templateName. Devolve null se indisponível.</summary>
        private static async Task<string?> LoadProcessNameAsync(
            string orgBase, string project, AuthenticationHeaderValue auth, CancellationToken ct)
        {
            var url = $"{orgBase}/_apis/projects/{Uri.EscapeDataString(project)}?includeCapabilities=true&{ApiVersion}";
            try
            {
                using var doc = await GetJsonAsync(url, auth, ct);
                if (doc.RootElement.TryGetProperty("capabilities", out var caps)
                    && caps.TryGetProperty("processTemplate", out var pt)
                    && pt.TryGetProperty("templateName", out var name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    var n = name.GetString();
                    return string.IsNullOrWhiteSpace(n) ? null : n!.Trim();
                }
            }
            catch { /* sem acesso a capabilities: segue sem o processo */ }
            return null;
        }

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

        /// <summary>Tipo EPIC (o campo EPIC_TYPE só existe nesse nível).</summary>
        public static bool IsEpicType(string? type)
            => string.Equals(type?.Trim(), "Epic", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Limita a prioridade da Task à faixa em vigor: padrão do DevOps (1–4) ou a faixa
        /// personalizada da configuração TFS, quando habilitada. Valor acima do que o
        /// processo do DevOps aceita causa erro na gravação — o erro aparece no log do sync.
        /// </summary>
        public static int ClampTaskPriority(TfsConnectionOptions? options, int priority)
        {
            int min = 1, max = 4;
            if (options is { TaskPriorityRangeEnabled: true })
            {
                min = Math.Max(1, options.TaskPriorityMin);
                max = Math.Max(min, options.TaskPriorityMax);
            }
            return Math.Clamp(priority, min, max);
        }
        public static bool IsTaskTypePublic(string? type)  => IsTaskType(type);

        /// <summary>Busca no DevOps as Tasks de cada Story do projeto e grava/atualiza o resumo de
        /// alocação (dono + horas). Best-effort: falha de uma Story não interrompe. Pula Stories
        /// com DevopsTaskCount == 0 (sabidamente sem tasks) para evitar rede desnecessária.</summary>
        public static async Task UpdateTaskAllocationSummariesAsync(
            Project project, TfsConnectionOptions options,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var stories = FlattenTasks(project.Tasks)
                .Where(t => IsStoryType(t.TfsType) && t.TfsId is > 0 && t.DevopsTaskCount != 0)
                .ToList();
            for (int i = 0; i < stories.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var story = stories[i];
                progress?.Report($"Resumo de tasks {i + 1}/{stories.Count}...");
                try
                {
                    var tasks = await FetchChildTasksFromDevOpsAsync(options, story.TfsId!.Value, ct);
                    story.TaskAllocations = tasks != null && tasks.Count > 0
                        ? BuildTaskAllocationSummary(tasks)
                        : new List<TaskAllocationSummary>();
                }
                catch { /* uma Story falhar não interrompe */ }
            }
        }

        private static IEnumerable<ProjectTask> FlattenTasks(IEnumerable<ProjectTask> tasks)
        {
            foreach (var t in tasks)
            {
                yield return t;
                foreach (var c in FlattenTasks(t.Children)) yield return c;
            }
        }

        /// <summary>Monta o resumo de alocação (recurso → horas) a partir das Tasks do DevOps de
        /// uma Story. Horas por task: Closed → Completed (HH Atual); senão → Estimate. Agrupa por
        /// responsável (usa o displayName quando houver). Task sem responsável é ignorada.</summary>
        public static List<TaskAllocationSummary> BuildTaskAllocationSummary(IEnumerable<DevOpsTaskInfo> tasks)
        {
            // Agrupa por recurso + estado (para permitir filtrar por estado ao ler o resumo).
            var byKey = new Dictionary<(string Resource, string State), (double Hours, int Tasks)>();
            foreach (var t in tasks ?? Enumerable.Empty<DevOpsTaskInfo>())
            {
                // Com o campo de aprovação habilitado na configuração TFS, Task não aprovada
                // NÃO entra no resumo: ela só existe no planejamento (Task Plan) até ser
                // aprovada/sincronizada. Campo desligado ou ausente → Approved == null → conta.
                if (t.Approved != null && !IsApprovedValue(t.Approved)) continue;

                var resource = !string.IsNullOrWhiteSpace(t.AssignedToDisplay) ? t.AssignedToDisplay!.Trim()
                             : !string.IsNullOrWhiteSpace(t.AssignedTo) ? t.AssignedTo!.Trim()
                             : null;
                if (resource == null) continue;

                double hours = IsClosedState(t.State) ? t.CompletedHours : t.EstimatedHours;
                if (hours <= 0) continue;

                var key = (resource, State: NormalizeTaskState(t.State));
                byKey.TryGetValue(key, out var acc);
                byKey[key] = (acc.Hours + hours, acc.Tasks + 1);
            }
            return byKey
                .Select(kv => new TaskAllocationSummary
                {
                    Resource = kv.Key.Resource,
                    State    = kv.Key.State,
                    Hours    = kv.Value.Hours,
                    Tasks    = kv.Value.Tasks
                })
                .OrderByDescending(a => a.Hours)
                .ToList();
        }

        /// <summary>Converte a descrição HTML do work item em texto puro (para exibir em grade/planilha).
        /// O HTML original permanece no DevOps — este texto é só para leitura.</summary>
        public static string ToPlainTextPublic(string? html) => ToPlainText(html);

        /// <summary>Indica se a descrição HTML tem conteúdo que se perderia ao regravar como texto
        /// puro (imagem, tabela, lista, link ou anexo). Usado para bloquear a gravação vinda de
        /// telas em grade/planilha, que só conhecem o texto.</summary>
        public static bool HasRichDescriptionContent(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return false;
            return Regex.IsMatch(html, @"<\s*(img|table|tr|td|ul|ol|li|a|video|iframe|object|embed)\b",
                RegexOptions.IgnoreCase);
        }

        /// <summary>Converte texto puro em HTML simples (um &lt;div&gt; por linha), no formato que o
        /// DevOps usa no campo Description.</summary>
        public static string PlainTextToSimpleHtml(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return string.Concat(lines.Select(l => $"<div>{System.Net.WebUtility.HtmlEncode(l)}</div>"));
        }

        /// <summary>
        /// Data de início na importação. O planejamento por fila (pessoa/sprint) só vale para o
        /// que ainda NÃO começou: item encerrado, em andamento ou com % de conclusão > 0 mantém
        /// o Data_Inicio real do DevOps — senão o cronograma empurraria para o futuro um
        /// trabalho já executado. A tag de data negociada fixa a data em qualquer estado.
        /// </summary>
        /// <summary>
        /// Normaliza o valor do campo EPIC_TYPE: só "BACKLOG" muda o comportamento; qualquer
        /// outro valor (inclusive vazio ou o campo desligado) é tratado como DELIVERY.
        /// </summary>
        public static string? NormalizeEpicType(string? raw)
        {
            var v = raw?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return EpicTypes.IsBacklog(v) ? EpicTypes.Backlog : EpicTypes.Delivery;
        }

        public static DateTime ResolveImportStart(
            DateTime? explicitStart, DateTime queueStart, bool hasFixedStartTag,
            string? state, double percentComplete = 0)
        {
            if (explicitStart is not { } start) return queueStart;

            // Trabalho já iniciado ou encerrado tem data REAL: só muda se o usuário fixar outra
            // data (tag de data negociada). O planejamento por fila vale para o que não começou.
            bool alreadyStarted = IsClosedState(state) || IsActiveState(state) || percentComplete > 0;
            return hasFixedStartTag || alreadyStarted ? start : queueStart;
        }

        public static double PercentCompleteFromState(
            string? state,
            double completedHours = 0,
            double estimatedHours = 0)
        {
            if (IsClosedState(state) || string.Equals(state?.Trim(), "Removed", StringComparison.OrdinalIgnoreCase))
                return 100;

            if (IsNewState(state))
                return 0;

            if (completedHours > 0 && estimatedHours > 0)
                return Math.Min(100, completedHours / estimatedHours * 100);

            // Active sem horas: a Task inicia com 10% (marca "começou").
            return IsActiveState(state) ? 10 : 0;
        }

        public static bool ShouldBlockManualStoryCompletionWithoutDevOpsTasks(
            ProjectTask? task,
            double requestedPercentComplete,
            bool enforceStoryCompletionWithTasks = true)
        {
            if (!enforceStoryCompletionWithTasks || task == null || requestedPercentComplete < 100)
                return false;

            return task.TfsId is > 0
                   && IsStoryType(task.TfsType)
                   && (task.DevopsTaskCount ?? 0) <= 0;
        }

        public static DateTime? GetTfsFinishDateForTests(ProjectTask task) => GetTfsFinishDate(task);
        public static int ResolveDesiredParentForTests(ProjectTask task, int rootWorkItemId) =>
            ResolveDesiredParent(task, rootWorkItemId);

        public static List<object> BuildCreateOpsForTests(
            ProjectTask task,
            int parentId,
            Dictionary<int, ProjectTask> tasksById,
            bool syncPredecessorLinks = true) =>
            BuildCreateOps(task, parentId, "https://dev.azure.com/test", null, null, null, tasksById, syncPredecessorLinks);

        public static void RepositionMarcosAfterPredecessorsForTests(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> roots) =>
            RepositionMarcosAfterPredecessors(roots);

        public static List<int> ApplyTfsPredecessorsForTests(
            System.Collections.ObjectModel.ObservableCollection<ProjectTask> roots,
            List<(int predecessor, int successor)> depLinks) =>
            ApplyTfsPredecessors(roots, depLinks);

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
            public string? Description { get; init; }
            public string? Activity { get; init; }
            public string? Tags { get; init; }
            public double? BacklogRank { get; init; }
            /// <summary>Data de criação (System.CreatedDate) do work item no DevOps, se disponível.</summary>
            public DateTime? CreatedDate { get; init; }
            /// <summary>Quantidade de comentários (tramites) do work item — evita buscar o último quando é zero.</summary>
            public int CommentCount { get; init; }
            /// <summary>Valor do campo de aprovação configurado (opcional). Null = campo desligado
            /// na configuração TFS ou inexistente no processo do DevOps.</summary>
            public string? Approved { get; init; }
        }

        /// <summary>Último comentário (discussão/tramite) do work item, em texto plano; null se não houver.</summary>
        public static async Task<string?> GetLastWorkItemCommentAsync(
            TfsConnectionOptions options, int tfsId, CancellationToken ct = default)
        {
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));
            // A API de comentários é por projeto e só existe na versão preview.
            var url = $"{orgBase}/{Uri.EscapeDataString(options.TeamProject)}/_apis/wit/workItems/{tfsId}/comments?api-version=7.1-preview.3";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            string? lastText = null;
            DateTime lastDate = DateTime.MinValue;
            foreach (var c in arr.EnumerateArray())
            {
                var text = c.TryGetProperty("text", out var tp) ? tp.GetString() : null;
                var date = c.TryGetProperty("createdDate", out var dp)
                    && DateTime.TryParse(dp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTime.MinValue;
                if (text != null && date >= lastDate) { lastDate = date; lastText = text; }
            }
            return lastText;
        }

        /// <summary>
        /// Registra <paramref name="text"/> como comentário (tramite) do work item, mas só
        /// quando difere do último comentário (comparação em texto plano, sem HTML).
        /// Retorna true se registrou.
        /// </summary>
        public static async Task<bool> AddWorkItemCommentIfChangedAsync(
            TfsConnectionOptions options, int tfsId, string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text) || tfsId <= 0) return false;

            var last = await GetLastWorkItemCommentAsync(options, tfsId, ct);
            if (last != null && string.Equals(NormalizeCommentText(last), NormalizeCommentText(text), StringComparison.OrdinalIgnoreCase))
                return false;

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));
            var url = $"{orgBase}/{Uri.EscapeDataString(options.TeamProject)}/_apis/wit/workItems/{tfsId}/comments?api-version=7.1-preview.3";
            var body = JsonSerializer.Serialize(new { text = text.Trim() });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }

        /// <summary>Texto plano do comentário: remove tags HTML, decodifica entidades e colapsa espaços.</summary>
        public static string NormalizeCommentText(string? html)
        {
            var text = Regex.Replace(html ?? "", "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        /// <summary>Registra a Observação da planilha (PlanObservation) como tramite assim que a
        /// Task ganha o ID do TFS. Falha de rede não interrompe o sync (fica registrada no log).</summary>
        private static async Task PostPlanObservationAsync(
            TfsConnectionOptions options, ProjectTask task, SyncReport report, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(task.PlanObservation) || task.TfsId is not > 0) return;
            try
            {
                if (await AddWorkItemCommentIfChangedAsync(options, task.TfsId.Value, task.PlanObservation!, ct))
                    report.LogSuccess($"Task - #{task.TfsId} ({task.Name}): observação registrada como tramite.");
            }
            catch (Exception ex)
            {
                report.LogError($"Task - #{task.TfsId} ({task.Name}): falha ao registrar a observação como tramite: {ex.Message}");
            }
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

            // % conclusão (Perc_Conclusao): a Task também tem o campo custom no DevOps.
            string? percConcRef = null;
            try
            {
                var fieldMap = await LoadFieldMapCachedAsync(orgBase, auth, ct);
                percConcRef = ResolveField(fieldMap, options.PercConclusaoFieldName, PercConclusaoFieldNames);
            }
            catch { /* sem o campo, cai no cálculo por estado/horas */ }

            // Campo de aprovação da Task (opcional, configurável na tela TFS/DevOps).
            string? approvedRef = null;
            if (options.ApprovedFieldEnabled && !string.IsNullOrWhiteSpace(options.ApprovedFieldName))
            {
                try
                {
                    var fieldMap = await LoadFieldMapCachedAsync(orgBase, auth, ct);
                    approvedRef = ResolveField(fieldMap, options.ApprovedFieldName, new[] { options.ApprovedFieldName, "Approved", "Aprovado" });
                }
                catch { /* campo inexistente no processo: segue sem ele */ }
            }

            var fields = $"System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo,System.Description,System.CreatedDate,System.CommentCount,System.Tags,{OrigEstRef},{CompletedRef},Microsoft.VSTS.Common.Priority,Microsoft.VSTS.Common.StackRank,Microsoft.VSTS.Common.BacklogPriority,{ActivityRef}";
            if (percConcRef != null) fields += $",{percConcRef}";
            if (approvedRef != null) fields += $",{approvedRef}";
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

                    // Perc_Conclusao explícito manda (exceto Closed → sempre 100%);
                    // sem o campo: Closed → 100%; Active → 10% sem CompletedWork calculável.
                    var pct = percConcRef != null && !IsClosedState(state)
                        && GetDoubleField(f, percConcRef) is { } cpc && cpc is >= 0 and <= 100
                        ? cpc
                        : PercentCompleteFromState(state, completed, hours);

                    var activity = f.TryGetProperty(ActivityRef, out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;
                    var description = f.TryGetProperty("System.Description", out var dp) && dp.ValueKind == JsonValueKind.String ? dp.GetString() : null;
                    var tags     = f.TryGetProperty("System.Tags", out var tgp) && tgp.ValueKind == JsonValueKind.String ? tgp.GetString() : null;
                    DateTime? createdDate = f.TryGetProperty("System.CreatedDate", out var cdp)
                        && cdp.ValueKind == JsonValueKind.String
                        && DateTime.TryParse(cdp.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal, out var cd)
                        ? cd.ToLocalTime()
                        : null;

                    result.Add(new DevOpsTaskInfo
                    {
                        TfsId = tid, Title = title, State = state,
                        EstimatedHours = hours, CompletedHours = completed,
                        PercentComplete = pct, AssignedTo = assignee, AssignedToDisplay = assigneeDisplay,
                        Priority = prio, Description = description, Activity = activity, Tags = tags,
                        BacklogRank = GetBacklogRank(f),
                        CreatedDate = createdDate,
                        CommentCount = f.TryGetProperty("System.CommentCount", out var ccp) && ccp.ValueKind == JsonValueKind.Number
                            ? ccp.GetInt32() : 0,
                        Approved = approvedRef != null && f.TryGetProperty(approvedRef, out var apv)
                            ? apv.ValueKind switch
                            {
                                JsonValueKind.String => apv.GetString(),
                                JsonValueKind.True   => "Sim",
                                JsonValueKind.False  => "Não",
                                JsonValueKind.Number => apv.GetDouble().ToString(CultureInfo.InvariantCulture),
                                _ => null
                            }
                            : null
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

        /// <summary>
        /// Retorna o System.WorkItemType real do item no DevOps (null se não existir).
        /// Usado para validar que o ID vinculado é do tipo esperado antes de excluir/sincronizar.
        /// </summary>
        public static async Task<string?> GetWorkItemTypeAsync(
            TfsConnectionOptions options, int workItemId, CancellationToken ct = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (workItemId <= 0) return null;

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var url = $"{orgBase}/_apis/wit/workitems/{workItemId}?fields=System.WorkItemType&{ApiVersion}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = auth;
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Falha ao consultar #{workItemId}: {resp.StatusCode} — {body}");
            }

            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("fields", out var f) &&
                f.TryGetProperty("System.WorkItemType", out var wt))
                return wt.GetString();
            return null;
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

        public static async Task<int> CreateChildTaskAsync(
            TfsConnectionOptions options,
            int parentTfsId,
            string title,
            string? assignedTo = null,
            string? activity = null,
            string? iterationPath = null,
            CancellationToken ct = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (parentTfsId <= 0) throw new ArgumentOutOfRangeException(nameof(parentTfsId));

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var ops = new List<object>
            {
                PatchAdd("/fields/System.Title", string.IsNullOrWhiteSpace(title) ? "Nova Task" : title.Trim()),
                PatchAdd("/fields/Microsoft.VSTS.Common.Priority", 4),
                new
                {
                    op = "add",
                    path = "/relations/-",
                    value = new
                    {
                        rel = "System.LinkTypes.Hierarchy-Reverse",
                        url = $"{orgBase}/_apis/wit/workItems/{parentTfsId}"
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(assignedTo))
                ops.Add(PatchAdd("/fields/System.AssignedTo", assignedTo));
            if (!string.IsNullOrWhiteSpace(activity))
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Common.Activity", activity));
            if (!string.IsNullOrWhiteSpace(iterationPath))
                ops.Add(PatchAdd("/fields/System.IterationPath", iterationPath.Trim()));

            return await CreateWorkItemAsync(orgBase, auth, options.TeamProject, "Task", ops, ct);
        }

        /// <summary>
        /// Grava a aprovação de uma Task no DevOps quando ela difere do que está lá. Devolve
        /// true se chegou a gravar. Sem o campo habilitado/encontrado, não faz nada.
        /// </summary>
        public static async Task<bool> UpdateTaskApprovedAsync(
            TfsConnectionOptions options, int taskId, bool approved, CancellationToken ct = default)
        {
            if (options == null || taskId <= 0) return false;
            if (!options.ApprovedFieldEnabled || string.IsNullOrWhiteSpace(options.ApprovedFieldName)) return false;

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var fieldMap = await LoadFieldMapCachedAsync(orgBase, auth, ct);
            var approvedRef = ResolveField(fieldMap, options.ApprovedFieldName,
                new[] { options.ApprovedFieldName, "Approved", "Aprovado" });
            if (approvedRef == null) return false;

            var items = await LoadWorkItemsAsync(orgBase, auth, new[] { taskId },
                new List<string> { "System.Id", approvedRef }, ct, expandRelations: false);
            if (!items.TryGetValue(taskId, out var wi)) return false;

            if (IsApprovedValue(ReadFieldText(wi, approvedRef)) == approved) return false;

            var ops = new List<object>
            {
                PatchAdd($"/fields/{approvedRef}", ApprovedWriteValue(orgBase, wi, approvedRef, approved))
            };
            await PatchWorkItemAsync(orgBase, auth, taskId, ops, ct);
            return true;
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
            string? descriptionHtml = null,
            CancellationToken ct = default)
        {
            if (options == null || taskId <= 0) return;
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var auth    = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken)));

            var ops = new List<object>();
            if (!string.IsNullOrWhiteSpace(title))
                ops.Add(PatchAdd("/fields/System.Title", title));
            if (estimatedHours >= 0)
                ops.Add(PatchAdd("/fields/Microsoft.VSTS.Scheduling.OriginalEstimate", estimatedHours));
            if (completedHours >= 0)
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
            // Só grava a descrição quando explicitamente informada (null = não mexe no que
            // está no DevOps, preservando formatação/imagens).
            if (descriptionHtml != null)
                ops.Add(PatchAdd("/fields/System.Description", descriptionHtml));

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
        // Extrai o displayName de um campo de identidade do DevOps (AssignedTo/CreatedBy),
        // que pode vir como objeto {displayName,...} ou, em APIs antigas, como string simples.
        private static string ReadIdentityDisplayName(JsonElement fields, string fieldRef)
        {
            if (!fields.TryGetProperty(fieldRef, out var el)) return "";
            if (el.ValueKind == JsonValueKind.Object)
                return el.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            if (el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? "";
            return "";
        }

        public static async Task<List<(int Id, string Title, string Type, string Owner)>> FetchRootWorkItemsAsync(
            TfsConnectionOptions options, CancellationToken ct = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.OrganizationUrl) ||
                string.IsNullOrWhiteSpace(options.TeamProject) ||
                string.IsNullOrWhiteSpace(options.PersonalAccessToken))
            {
                throw new InvalidOperationException("Conexão DevOps incompleta para Discovery: informe URL, Team Project e PAT.");
            }

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
            if (!wiqlResp.IsSuccessStatusCode)
            {
                var err = await wiqlResp.Content.ReadAsStringAsync(ct);
                if (wiqlResp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    wiqlResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(BuildDiscoveryTokenPermissionError(
                        "Discovery de projetos DevOps",
                        (int)wiqlResp.StatusCode,
                        wiqlResp.ReasonPhrase,
                        err,
                        options.TeamProject,
                        wiqlUrl));
                }

                throw new InvalidOperationException(
                    $"Falha no Discovery DevOps.\nURL: {wiqlUrl}\nStatus: {(int)wiqlResp.StatusCode} {wiqlResp.ReasonPhrase}\nResposta: {err}");
            }

            var wiqlJson = await wiqlResp.Content.ReadAsStringAsync(ct);
            using var wiqlDoc = JsonDocument.Parse(wiqlJson);
            if (!wiqlDoc.RootElement.TryGetProperty("workItems", out var wiItems)) return [];

            var ids = wiItems.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0)
                .Where(x => x > 0).ToList();
            if (ids.Count == 0) return [];

            var result = new List<(int, string, string, string)>();
            // Busca em lotes de 200
            for (int i = 0; i < ids.Count; i += 200)
            {
                var batch = ids.Skip(i).Take(200).ToList();
                var batchIds = string.Join(",", batch);
                var batchUrl = $"{orgBase}/_apis/wit/workitems?ids={batchIds}&fields=System.Id,System.Title,System.WorkItemType,System.AssignedTo&{ApiVersion}";
                using var batchReq = new HttpRequestMessage(HttpMethod.Get, batchUrl);
                batchReq.Headers.Authorization = auth;
                using var batchResp = await Http.SendAsync(batchReq, ct);
                if (!batchResp.IsSuccessStatusCode)
                {
                    var err = await batchResp.Content.ReadAsStringAsync(ct);
                    if (batchResp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        batchResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException(BuildDiscoveryTokenPermissionError(
                            "Discovery de projetos DevOps",
                            (int)batchResp.StatusCode,
                            batchResp.ReasonPhrase,
                            err,
                            options.TeamProject,
                            batchUrl));
                    }

                    throw new InvalidOperationException(
                        $"Falha ao carregar detalhes dos itens do Discovery.\nURL: {batchUrl}\nStatus: {(int)batchResp.StatusCode} {batchResp.ReasonPhrase}\nResposta: {err}");
                }
                var batchJson = await batchResp.Content.ReadAsStringAsync(ct);
                using var batchDoc = JsonDocument.Parse(batchJson);
                if (!batchDoc.RootElement.TryGetProperty("value", out var values)) continue;
                foreach (var item in values.EnumerateArray())
                {
                    if (!item.TryGetProperty("fields", out var f)) continue;
                    var id    = f.TryGetProperty("System.Id",           out var ip) ? ip.GetInt32()    : 0;
                    var title = f.TryGetProperty("System.Title",        out var tp) ? tp.GetString() ?? "" : "";
                    var type  = f.TryGetProperty("System.WorkItemType", out var wt) ? wt.GetString() ?? "" : "";
                    // "Owner" = responsavel do work item (System.AssignedTo).
                    var owner = ReadIdentityDisplayName(f, "System.AssignedTo");
                    if (id > 0 && string.Equals(type, "Project", StringComparison.OrdinalIgnoreCase))
                        result.Add((id, title, type, owner));
                }
            }
            return result;
        }

        /// <summary>
        /// Busca pessoas via Identity Picker (o mesmo seletor de @mencao do DevOps).
        /// Retorna null se a API nao respondeu com sucesso (para cair no fallback).
        /// </summary>
        private static async Task<List<DevOpsUserInfo>?> TryIdentityPickerAsync(
            TfsAuthContext conn, string filter, CancellationToken ct, List<string> failures)
        {
            foreach (var orgBase in GetDevOpsApiBaseCandidates(conn.OrgBase))
            {
                var url = $"{orgBase.TrimEnd('/')}/_apis/IdentityPicker/Identities?api-version=5.2-preview.1";
                try
                {
                    var payload = new
                    {
                        query = filter,
                        identityTypes = new[] { "user" },
                        operationScopes = new[] { "ims", "source" },
                        properties = new[] { "DisplayName", "Account", "Mail", "SignInAddress", "SamAccountName" },
                        options = new { MinResults = 5, MaxResults = 50 }
                    };

                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    req.Headers.Authorization = conn.Authorization;

                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        failures.Add($"[IdentityPicker] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                     (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                        continue;
                    }

                    var text = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                        continue;

                    var found = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var result in results.EnumerateArray())
                    {
                        if (!result.TryGetProperty("identities", out var ids) || ids.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var id in ids.EnumerateArray())
                        {
                            var name = ReadPickerString(id, "displayName");
                            var email = ReadPickerString(id, "signInAddress")
                                        ?? ReadPickerString(id, "mail")
                                        ?? ReadPickerString(id, "samAccountName");
                            if (string.IsNullOrWhiteSpace(name))
                                name = email;
                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                            found.TryAdd(key, new DevOpsUserInfo(name!.Trim(), (email ?? string.Empty).Trim()));
                        }
                    }

                    if (found.Count > 0)
                        return found.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex)
                {
                    failures.Add($"[IdentityPicker] URL: {url}\nExcecao: {ex.Message}");
                }
            }

            return null;
        }

        private static string? ReadPickerString(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        /// <summary>
        /// Lista usuarios da organizacao pela Graph API (vssps). Usada quando o
        /// filtro esta vazio. Retorna null se a API nao respondeu com sucesso.
        /// </summary>
        private static async Task<List<DevOpsUserInfo>?> TryGraphUsersAsync(
            TfsAuthContext conn, CancellationToken ct, List<string> failures)
        {
            foreach (var vsspsBase in GetDevOpsIdentityApiBaseCandidates(conn.OrgBase))
            {
                if (!vsspsBase.Contains("vssps", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = $"{vsspsBase.TrimEnd('/')}/_apis/graph/users?api-version=6.0-preview.1";
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = conn.Authorization;

                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        failures.Add($"[Graph] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                     (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                        continue;
                    }

                    var text = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                        continue;

                    var found = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var u in value.EnumerateArray())
                    {
                        // Ignora identidades que nao sao de usuario real (grupos de servico etc.)
                        var subjectKind = ReadPickerString(u, "subjectKind");
                        if (!string.IsNullOrWhiteSpace(subjectKind) &&
                            !subjectKind.Equals("user", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var name = ReadPickerString(u, "displayName");
                        var email = ReadPickerString(u, "mailAddress") ?? ReadPickerString(u, "principalName");
                        if (string.IsNullOrWhiteSpace(name))
                            name = email;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                        found.TryAdd(key, new DevOpsUserInfo(name!.Trim(), (email ?? string.Empty).Trim()));
                        if (found.Count >= 200)
                            break;
                    }

                    if (found.Count > 0)
                        return found.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex)
                {
                    failures.Add($"[Graph] URL: {url}\nExcecao: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Lista membros da organizacao pela Member Entitlement Management API (vsaex).
        /// Usada quando o filtro esta vazio, como alternativa a Graph API.
        /// </summary>
        private static async Task<List<DevOpsUserInfo>?> TryUserEntitlementsAsync(
            TfsAuthContext conn, CancellationToken ct, List<string> failures)
        {
            foreach (var vsaexBase in GetDevOpsVsaexBaseCandidates(conn.OrgBase))
            {
                var url = $"{vsaexBase.TrimEnd('/')}/_apis/userentitlements?top=200&api-version=6.0-preview.3";
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = conn.Authorization;

                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        failures.Add($"[UserEntitlements] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                     (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                        continue;
                    }

                    var text = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    // Resposta pode ter "members" (paginado) ou "value".
                    if (!doc.RootElement.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
                        if (!doc.RootElement.TryGetProperty("value", out members) || members.ValueKind != JsonValueKind.Array)
                            continue;

                    var found = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in members.EnumerateArray())
                    {
                        var user = m.TryGetProperty("user", out var uEl) ? uEl : m;
                        var name = ReadPickerString(user, "displayName");
                        var email = ReadPickerString(user, "mailAddress") ?? ReadPickerString(user, "principalName");
                        if (string.IsNullOrWhiteSpace(name))
                            name = email;
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                        found.TryAdd(key, new DevOpsUserInfo(name!.Trim(), (email ?? string.Empty).Trim()));
                    }

                    if (found.Count > 0)
                        return found.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex)
                {
                    failures.Add($"[UserEntitlements] URL: {url}\nExcecao: {ex.Message}");
                }
            }

            return null;
        }

        private static IEnumerable<string> GetDevOpsVsaexBaseCandidates(string orgBase)
        {
            orgBase = orgBase.TrimEnd('/');
            if (Uri.TryCreate(orgBase, UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                {
                    var org = host[..^".visualstudio.com".Length];
                    if (!string.IsNullOrWhiteSpace(org))
                    {
                        yield return $"https://vsaex.dev.azure.com/{org}";
                        yield return $"https://{org}.vsaex.visualstudio.com";
                    }
                }
                else if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    var org = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(org))
                        yield return $"https://vsaex.dev.azure.com/{org}";
                }
            }
        }

        /// <summary>
        /// Lista pessoas da organizacao paginando (User Entitlements + Graph) ate
        /// <paramref name="maxResults"/>. Usada pelo seletor de pessoas para "trazer
        /// tudo" ate um limite configuravel (a org pode ter centenas de milhares).
        /// </summary>
        public static async Task<List<DevOpsUserInfo>> FetchOrgUsersAsync(
            TfsConnectionOptions options, int maxResults = 1000, CancellationToken ct = default)
        {
            if (maxResults <= 0) maxResults = 1000;
            var conn = CreateTfsAuthContext(options, "Discovery", requireTeamProject: false);
            var failures = new List<string>();
            var users = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);

            void Absorb(JsonElement member)
            {
                var user = member.TryGetProperty("user", out var uEl) ? uEl : member;
                var name = ReadPickerString(user, "displayName");
                var email = ReadPickerString(user, "mailAddress") ?? ReadPickerString(user, "principalName");
                var subjectKind = ReadPickerString(user, "subjectKind");
                if (!string.IsNullOrWhiteSpace(subjectKind) && !subjectKind.Equals("user", StringComparison.OrdinalIgnoreCase))
                    return;
                if (string.IsNullOrWhiteSpace(name)) name = email;
                if (string.IsNullOrWhiteSpace(name)) return;
                var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                users.TryAdd(key, new DevOpsUserInfo(name!.Trim(), (email ?? string.Empty).Trim()));
            }

            // 1) User Entitlements (vsaex), paginado por continuationToken.
            foreach (var vsaexBase in GetDevOpsVsaexBaseCandidates(conn.OrgBase))
            {
                if (users.Count >= maxResults) break;
                try
                {
                    string? continuation = null;
                    var safety = 0;
                    do
                    {
                        var url = $"{vsaexBase.TrimEnd('/')}/_apis/userentitlements?top=100&api-version=6.0-preview.3" +
                                  (string.IsNullOrEmpty(continuation) ? string.Empty : $"&continuationToken={Uri.EscapeDataString(continuation)}");
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Authorization = conn.Authorization;
                        using var resp = await Http.SendAsync(req, ct);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            failures.Add($"[UserEntitlements] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                         (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                            break;
                        }

                        var text = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(text);
                        JsonElement members = default;
                        var hasMembers = (doc.RootElement.TryGetProperty("members", out members) && members.ValueKind == JsonValueKind.Array)
                                         || (doc.RootElement.TryGetProperty("value", out members) && members.ValueKind == JsonValueKind.Array);
                        if (!hasMembers) break;

                        var before = users.Count;
                        foreach (var m in members.EnumerateArray())
                        {
                            Absorb(m);
                            if (users.Count >= maxResults) break;
                        }
                        if (users.Count >= maxResults) break;
                        if (users.Count == before && members.GetArrayLength() == 0) break;

                        continuation = doc.RootElement.TryGetProperty("continuationToken", out var ctk) && ctk.ValueKind == JsonValueKind.String
                            ? ctk.GetString() : null;
                        if (string.IsNullOrEmpty(continuation) && resp.Headers.TryGetValues("X-MS-ContinuationToken", out var hv))
                            continuation = hv.FirstOrDefault();
                    }
                    while (!string.IsNullOrEmpty(continuation) && users.Count < maxResults && ++safety < 100000);

                    if (users.Count > 0)
                        return users.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex)
                {
                    failures.Add($"[UserEntitlements] Excecao: {ex.Message}");
                }
            }

            // 2) Graph (vssps), paginado por X-MS-ContinuationToken.
            foreach (var vsspsBase in GetDevOpsIdentityApiBaseCandidates(conn.OrgBase))
            {
                if (users.Count >= maxResults) break;
                if (!vsspsBase.Contains("vssps", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string? continuation = null;
                    var safety = 0;
                    do
                    {
                        var url = $"{vsspsBase.TrimEnd('/')}/_apis/graph/users?api-version=6.0-preview.1" +
                                  (string.IsNullOrEmpty(continuation) ? string.Empty : $"&continuationToken={Uri.EscapeDataString(continuation)}");
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Authorization = conn.Authorization;
                        using var resp = await Http.SendAsync(req, ct);
                        if (!resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            failures.Add($"[Graph] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                         (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                            break;
                        }

                        var text = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(text);
                        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) break;

                        foreach (var u in value.EnumerateArray())
                        {
                            Absorb(u);
                            if (users.Count >= maxResults) break;
                        }
                        if (users.Count >= maxResults) break;

                        continuation = resp.Headers.TryGetValues("X-MS-ContinuationToken", out var hv) ? hv.FirstOrDefault() : null;
                    }
                    while (!string.IsNullOrEmpty(continuation) && users.Count < maxResults && ++safety < 100000);

                    if (users.Count > 0)
                        return users.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                }
                catch (Exception ex)
                {
                    failures.Add($"[Graph] Excecao: {ex.Message}");
                }
            }

            if (users.Count > 0)
                return users.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            throw new InvalidOperationException(BuildDiscoveryUserError(
                0, "Org users listing failed", string.Join("\n\n---\n\n", failures), conn.TeamProject));
        }

        /// <summary>
        /// Lista as equipes (Teams) do Team Project configurado.
        /// </summary>
        public static async Task<List<DevOpsTeamInfo>> FetchTeamsAsync(
            TfsConnectionOptions options, CancellationToken ct = default)
        {
            var conn = CreateTfsAuthContext(options, "Discovery Teams");
            var teams = new List<DevOpsTeamInfo>();
            var failures = new List<string>();

            var top = 200;
            var skip = 0;
            var safety = 0;
            while (++safety < 1000)
            {
                var url = $"{conn.OrgBase.TrimEnd('/')}/_apis/projects/{Uri.EscapeDataString(conn.TeamProject)}/teams" +
                          $"?$top={top}&$skip={skip}&api-version=6.0";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = conn.Authorization;
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    failures.Add($"[Teams] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                 (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                    break;

                var count = 0;
                foreach (var t in value.EnumerateArray())
                {
                    var id = ReadPickerString(t, "id");
                    var name = ReadPickerString(t, "name");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        teams.Add(new DevOpsTeamInfo(id!, name!.Trim()));
                    count++;
                }
                if (count < top) break;
                skip += top;
            }

            if (teams.Count > 0)
                return teams.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            throw new InvalidOperationException(BuildDiscoveryUserError(
                0, "Teams listing failed", string.Join("\n\n---\n\n", failures), conn.TeamProject));
        }

        /// <summary>
        /// Lista os membros de uma equipe (Team) do Team Project configurado.
        /// </summary>
        public static async Task<List<DevOpsUserInfo>> FetchTeamMembersAsync(
            TfsConnectionOptions options, string teamId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(teamId))
                return new List<DevOpsUserInfo>();

            var conn = CreateTfsAuthContext(options, "Discovery Team Members");
            var users = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();

            var top = 200;
            var skip = 0;
            var safety = 0;
            while (++safety < 1000)
            {
                var url = $"{conn.OrgBase.TrimEnd('/')}/_apis/projects/{Uri.EscapeDataString(conn.TeamProject)}" +
                          $"/teams/{Uri.EscapeDataString(teamId)}/members?$top={top}&$skip={skip}&api-version=6.0";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = conn.Authorization;
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    failures.Add($"[TeamMembers] URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                                 (string.IsNullOrWhiteSpace(body) ? string.Empty : $"\nResposta: {TrimDiscoveryResponse(body)}"));
                    break;
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                    break;

                var count = 0;
                foreach (var m in value.EnumerateArray())
                {
                    var identity = m.TryGetProperty("identity", out var idEl) ? idEl : m;
                    var name = ReadPickerString(identity, "displayName");
                    var email = ReadPickerString(identity, "mailAddress") ?? ReadPickerString(identity, "uniqueName");
                    if (email != null && !email.Contains('@')) email = null;
                    if (string.IsNullOrWhiteSpace(name)) name = email;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                        users.TryAdd(key, new DevOpsUserInfo(name!.Trim(), (email ?? string.Empty).Trim()));
                    }
                    count++;
                }
                if (count < top) break;
                skip += top;
            }

            if (users.Count > 0 || failures.Count == 0)
                return users.Values.OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            throw new InvalidOperationException(BuildDiscoveryUserError(
                0, "Team members listing failed", string.Join("\n\n---\n\n", failures), conn.TeamProject));
        }

        public static async Task<List<DevOpsUserInfo>> FetchUsersByFilterAsync(
            TfsConnectionOptions options, string filter, CancellationToken ct = default)
        {
            const int maxResults = 50;

            var conn = CreateTfsAuthContext(options, "Discovery", requireTeamProject: false);
            filter = filter?.Trim() ?? string.Empty;
            if (filter.Length > 30)
                filter = filter[..30];

            // 1) Identity Picker — mesma API usada pelo seletor de @mencao do DevOps.
            //    Faz busca por substring de forma confiavel (inclusive orgs AAD).
            //    So funciona com um termo de busca; filtro vazio cai no endpoint de identidades.
            var failures = new List<string>();

            if (!string.IsNullOrWhiteSpace(filter))
            {
                var picked = await TryIdentityPickerAsync(conn, filter, ct, failures);
                if (picked is { Count: > 0 })
                    return picked;
            }
            else
            {
                // Filtro vazio: lista os usuarios da organizacao (Graph + User Entitlements).
                var all = await TryGraphUsersAsync(conn, ct, failures)
                          ?? await TryUserEntitlementsAsync(conn, ct, failures);
                if (all is { Count: > 0 })
                    return all;
            }

            var users = new Dictionary<string, DevOpsUserInfo>(StringComparer.OrdinalIgnoreCase);
            JsonDocument? doc = null;

            foreach (var identityBase in GetDevOpsIdentityApiBaseCandidates(conn.OrgBase))
            {
                var url =
                    $"{identityBase}/_apis/identities" +
                    $"?searchFilter=General" +
                    (string.IsNullOrWhiteSpace(filter) ? string.Empty : $"&filterValue={Uri.EscapeDataString(filter)}") +
                    $"&queryMembership=None" +
                    $"&{ApiVersion}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = conn.Authorization;
                using var resp = await Http.SendAsync(req, ct);
                var responseText = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    doc = JsonDocument.Parse(responseText);
                    break;
                }

                failures.Add(
                    $"URL: {url}\nStatus HTTP: {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                    (string.IsNullOrWhiteSpace(responseText)
                        ? string.Empty
                        : $"\nResposta: {TrimDiscoveryResponse(responseText)}"));
            }

            if (doc == null)
                throw new InvalidOperationException(BuildDiscoveryUserError(
                    0,
                    "Identity endpoint failed",
                    string.Join("\n\n---\n\n", failures),
                    conn.TeamProject));

            using (doc)
            {
            if (!doc.RootElement.TryGetProperty("value", out var values) ||
                values.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var identity in values.EnumerateArray())
            {
                var name =
                    ReadIdentitySearchProperty(identity, "displayName") ??
                    ReadIdentitySearchProperty(identity, "providerDisplayName") ??
                    ReadIdentitySearchProperty(identity, "customDisplayName") ??
                    string.Empty;
                var email =
                    ReadIdentitySearchProperty(identity, "uniqueName") ??
                    ReadIdentitySearchProperty(identity, "mail") ??
                    ReadIdentitySearchProperty(identity, "Mail") ??
                    ReadIdentitySearchProperty(identity, "Account") ??
                    ReadIdentitySearchProperty(identity, "signInAddress") ??
                    string.Empty;

                if (string.IsNullOrWhiteSpace(name))
                    name = email;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (!string.IsNullOrWhiteSpace(filter) && !MatchesUserFilter(name, email, filter))
                    continue;

                var key = !string.IsNullOrWhiteSpace(email) ? email : name;
                users.TryAdd(key, new DevOpsUserInfo(name.Trim(), (email ?? string.Empty).Trim()));
                if (users.Count >= maxResults)
                    break;
            }
            }

            return users.Values
                .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> GetDevOpsApiBaseCandidates(string orgBase)
        {
            orgBase = orgBase.TrimEnd('/');
            yield return orgBase;

            if (orgBase.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase) &&
                !orgBase.EndsWith("/DefaultCollection", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{orgBase}/DefaultCollection";
            }
        }

        private static IEnumerable<string> GetDevOpsIdentityApiBaseCandidates(string orgBase)
        {
            orgBase = orgBase.TrimEnd('/');
            yield return orgBase;

            if (Uri.TryCreate(orgBase, UriKind.Absolute, out var uri))
            {
                var host = uri.Host;
                if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                {
                    var org = host[..^".visualstudio.com".Length];
                    if (!string.IsNullOrWhiteSpace(org))
                    {
                        yield return $"https://vssps.dev.azure.com/{org}";
                        yield return $"https://{org}.vssps.visualstudio.com";
                    }
                }
                else if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    var org = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(org))
                        yield return $"https://vssps.dev.azure.com/{org}";
                }
            }

            if (orgBase.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase) &&
                !orgBase.EndsWith("/DefaultCollection", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{orgBase}/DefaultCollection";
            }
        }

        private static bool MatchesUserFilter(string? name, string? email, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return false;

            return (!string.IsNullOrWhiteSpace(name) &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (!string.IsNullOrWhiteSpace(email) &&
                    email.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string? ReadIdentitySearchProperty(JsonElement identity, string name)
        {
            if (identity.TryGetProperty(name, out var direct))
                return ReadIdentitySearchValue(direct);

            if (identity.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object &&
                props.TryGetProperty(name, out var prop))
            {
                return ReadIdentitySearchValue(prop);
            }

            return null;
        }

        private static string? ReadIdentitySearchValue(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$value", out var typedValue) &&
                    typedValue.ValueKind == JsonValueKind.String)
                    return typedValue.GetString();
                if (value.TryGetProperty("value", out var plainValue) &&
                    plainValue.ValueKind == JsonValueKind.String)
                    return plainValue.GetString();
            }

            return null;
        }

        private static string TrimDiscoveryResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return string.Empty;

            var compact = Regex.Replace(response, "<.*?>", " ");
            compact = Regex.Replace(compact, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(compact))
                compact = response.Trim();

            const int max = 700;
            return compact.Length <= max ? compact : compact[..max] + "...";
        }

        private static string BuildDiscoveryUserError(int statusCode, string? reason, string response, string project, string? url = null)
        {
            var scope = string.IsNullOrWhiteSpace(project)
                ? "Escopo: organização"
                : $"Projeto configurado: {project}";
            var urlText = string.IsNullOrWhiteSpace(url)
                ? string.Empty
                : $"URL: {url}\n\n";

            if (statusCode == 401)
            {
                return BuildDiscoveryTokenPermissionError(
                    "Discovery de pessoas DevOps/TFS",
                    statusCode,
                    reason,
                    response,
                    project,
                    url);
            }

            if (statusCode == 403)
            {
                if (!string.IsNullOrWhiteSpace(url) &&
                    url.Contains("/_apis/identities", StringComparison.OrdinalIgnoreCase))
                {
                    return BuildDiscoveryTokenPermissionError(
                        "Discovery de pessoas DevOps/TFS",
                        statusCode,
                        reason,
                        response,
                        project,
                        url);
                }

                return
                    "O Azure DevOps/TFS autenticou o token, mas bloqueou o acesso aos work items deste projeto.\n\n" +
                    urlText +
                    $"Status HTTP: {statusCode} {reason}\n\n" +
                    "Verifique se o usuário dono do PAT tem acesso ao Team Project e permissão para ler Work Items.\n\n" +
                    scope +
                    (string.IsNullOrWhiteSpace(response) ? string.Empty : $"\n\nResposta: {response}");
            }

            return
                $"Falha no Discovery de usuários DevOps.\n\n" +
                urlText +
                $"Status: {statusCode} {reason}\n" +
                (string.IsNullOrWhiteSpace(response) ? string.Empty : $"Resposta: {response}");
        }

        private static string BuildDiscoveryTokenPermissionError(
            string action,
            int statusCode,
            string? reason,
            string response,
            string project,
            string? url = null)
        {
            var scope = string.IsNullOrWhiteSpace(project)
                ? "Projeto configurado: (não informado)"
                : $"Projeto configurado: {project}";
            var technical = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(url))
                technical.AppendLine($"URL: {url}");
            if (statusCode > 0)
                technical.AppendLine($"Detalhe técnico: HTTP {statusCode} {reason}");
            if (!string.IsNullOrWhiteSpace(response))
            {
                technical.AppendLine("Resposta técnica:");
                technical.AppendLine(response);
            }

            return
                $"Não foi possível autenticar no Azure DevOps/TFS para executar: {action}.\n\n" +
                "Atualize o Personal Access Token (PAT) desta organização e salve novamente no NXProject com a opção de lembrar token.\n\n" +
                "No Azure DevOps, habilite no PAT:\n" +
                "- Identity: Read (necessário para Discovery de pessoas)\n" +
                "- Work Items: Read ou Work Items: Read & Write (necessário para cronograma, importação, sync e Discovery de projetos)\n\n" +
                "Se o token já foi alterado no portal, cole o novo valor na configuração TFS/DevOps do NXProject e salve.\n\n" +
                scope +
                (technical.Length == 0 ? string.Empty : $"\n\n{technical.ToString().TrimEnd()}");
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
                        StackRank = GetBacklogRank(f),
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

        // ── Campo de aprovação da Task ───────────────────────────────────────────
        /// <summary>Valor gravado quando a sincronização oficializa a aprovação de uma Task.</summary>
        private const string ApprovedTrueValue  = "Sim";
        private const string ApprovedFalseValue = "Não";

        /// <summary>Lê o campo de aprovação como texto (bool, número ou string no DevOps).</summary>
        private static string? ReadFieldText(WorkItem item, string? refName)
        {
            if (refName == null || item.Fields.ValueKind != JsonValueKind.Object) return null;
            if (!item.Fields.TryGetProperty(refName, out var el)) return null;

            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                JsonValueKind.Number => el.GetDouble().ToString(CultureInfo.InvariantCulture),
                _ => null
            };
        }

        /// <summary>Interpreta o valor do campo de aprovação: aprovado ou não.</summary>
        public static bool IsApprovedValue(string? value)
        {
            var v = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(v)) return false;
            return v is "sim" or "yes" or "true" or "1" or "aprovado" or "aprovada" or "approved";
        }

        /// <summary>
        /// Valor a gravar respeitando o tipo do campo no processo: boolean recebe true,
        /// os demais recebem o texto padrão.
        /// </summary>
        private static object ApprovedWriteValue(string orgBase, WorkItem item, string refName, bool approved = true)
        {
            // Campo booleano (caso do "Approved" tipo Boolean) recebe true/false; texto, Sim/Não.
            if (IsBooleanField(orgBase, refName)) return approved;

            if (item.Fields.ValueKind == JsonValueKind.Object &&
                item.Fields.TryGetProperty(refName, out var el) &&
                el.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return approved;

            return approved ? ApprovedTrueValue : ApprovedFalseValue;
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

        private const string StackRankField = "Microsoft.VSTS.Common.StackRank";
        private const string BacklogPriorityField = "Microsoft.VSTS.Common.BacklogPriority";

        private static double? GetBacklogRank(JsonElement fields) =>
            GetDoubleField(fields, StackRankField)
            ?? GetDoubleField(fields, BacklogPriorityField);

        /// <summary>
        /// Campos de ordem do backlog a gravar: Agile/CMMI ordenam por StackRank; Scrum, por
        /// BacklogPriority. Grava nos campos que o work item REALMENTE tem (gravar campo
        /// inexistente no processo faz o PATCH falhar); sem nenhum dos dois — ou na criação,
        /// quando ainda não há campos —, usa StackRank.
        /// </summary>
        public static IReadOnlyList<string> BacklogRankFieldsToWrite(JsonElement? fields, string? process = null)
        {
            // Fallback quando o work item não traz NENHUM dos dois campos (item sem rank ainda):
            // Scrum ordena por BacklogPriority; Agile/CMMI/Basic, por StackRank.
            var fallback = IsScrumProcess(process)
                ? new[] { BacklogPriorityField }
                : new[] { StackRankField };

            if (fields is not { } f || f.ValueKind != JsonValueKind.Object)
                return fallback;

            var campos = new List<string>(2);
            if (f.TryGetProperty(StackRankField, out _)) campos.Add(StackRankField);
            if (f.TryGetProperty(BacklogPriorityField, out _)) campos.Add(BacklogPriorityField);
            return campos.Count > 0 ? campos : fallback;
        }

        /// <summary>Processo Scrum? (ordena o backlog por BacklogPriority, não StackRank.)</summary>
        public static bool IsScrumProcess(string? process)
            => !string.IsNullOrWhiteSpace(process)
               && process.Trim().IndexOf("scrum", StringComparison.OrdinalIgnoreCase) >= 0;

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
            PercentCompleteFromState(state);

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
