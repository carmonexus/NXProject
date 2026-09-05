// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NXProject.Services
{
    /// <summary>
    /// Mapeamento de um estado TFS para um label de exibição no gráfico de status.
    /// </summary>
    public sealed class StoryStatusMapping
    {
        /// <summary>Estado exato no DevOps (ex.: "Corrigindo Causa Raiz").</summary>
        public string TfsState { get; set; } = string.Empty;

        /// <summary>Label agrupador exibido no gráfico (ex.: "Corrigindo").</summary>
        public string ChartLabel { get; set; } = string.Empty;

        /// <summary>Cor da barra em hex RRGGBB (ex.: "FFA726"). Vazio = cor automática.</summary>
        public string ColorHex { get; set; } = string.Empty;

        /// <summary>Ordem de exibição da barra (menor = mais à esquerda).</summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// Configuração de um projeto no portfólio (tipo OPEX/CAPEX e centro de custo).
    /// </summary>
    public sealed class PortfolioProjectConfig
    {
        /// <summary>Nome do projeto DevOps (chave de associação).</summary>
        public string ProjectName { get; set; } = string.Empty;
        /// <summary>Caminho do arquivo .nxproject local correspondente.</summary>
        public string FilePath    { get; set; } = string.Empty;
        public bool   IsOpex           { get; set; } = true;
        public string CostCenter       { get; set; } = string.Empty;
        // "CAPEX", "OPEX" ou "EPIC". Vazio = derivado de IsOpex.
        public string CostCenterSource { get; set; } = string.Empty;
    }

    /// <summary>Campo fixo extra enviado na criação de work items.</summary>
    public sealed class ExtraWorkItemField
    {
        /// <summary>Referência do campo no DevOps, ex.: "Custom.Type".</summary>
        public string Ref { get; set; } = string.Empty;
        /// <summary>Valor fixo a ser enviado, ex.: "Atividade".</summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dados de conexao com o Azure DevOps / TFS usados pelo import.
    /// </summary>
    public sealed class TfsConnectionOptions
    {
        /// <summary>URL da organizacao, ex.: https://dev.azure.com/sua-organizacao </summary>
        public string OrganizationUrl { get; set; } = "";

        /// <summary>Nome do team project, ex.: Seu Projeto </summary>
        public string TeamProject { get; set; } = "";

        /// <summary>Personal Access Token (Work Items - Read).</summary>
        public string PersonalAccessToken { get; set; } = string.Empty;

        /// <summary>ID do work item raiz (tipo Project) a ser importado.</summary>
        public int RootWorkItemId { get; set; }

        /// <summary>Horas que equivalem a 1 dia util ao converter esforco.</summary>
        public double HoursPerDay { get; set; } = 8.0;

        /// <summary>Nome do campo de esforco em horas (rotulo "HH Estimado").</summary>
        public string EffortFieldName { get; set; } = "HH Estimado";

        /// <summary>Nome do campo de data de inicio da Story.</summary>
        public string StartFieldName { get; set; } = "Data_Inicio";

        /// <summary>Nome do campo de data de fim da Story.</summary>
        public string FinishFieldName { get; set; } = "Data_Fim";

        /// <summary>Nome do campo de percentual de alocação do recurso na Story (ex.: "Perc_Alocacao").</summary>
        public string PercAlocFieldName { get; set; } = "Perc_Alocacao";

        /// <summary>Nome do campo de percentual de conclusão da Story (ex.: "Perc_Conclusao").</summary>
        public string PercConclusaoFieldName { get; set; } = "Perc_Conclusao";

        /// <summary>Campo inteiro que armazena a versão de sincronização (controle de concorrência).</summary>
        public string SyncVersionFieldName { get; set; } = "Sync_version";

        /// <summary>Campo texto que armazena o nome de quem realizou a última sincronização.</summary>
        public string SyncNameFieldName { get; set; } = "Sync_Name";

        /// <summary>
        /// Habilita a leitura do campo de aprovação da Task no DevOps para preencher a coluna
        /// "Aprovada" do Task Plan. Desligado, a coluna segue com o padrão do NXProject.
        /// </summary>
        public bool ApprovedFieldEnabled { get; set; } = true;

        /// <summary>
        /// Nome (ou reference name) do campo de aprovação da Task no DevOps — ex.: "Approved"
        /// ou "Custom.Approved". Só é usado quando <see cref="ApprovedFieldEnabled"/> está ligado.
        /// </summary>
        public string ApprovedFieldName { get; set; } = "Approved";

        /// <summary>
        /// Habilita a leitura do tipo do EPIC no DevOps (ENTREGA/BACKLOG). Desligado, todo EPIC
        /// conta as horas normalmente. Padrão: habilitado.
        /// </summary>
        public bool EpicTypeFieldEnabled { get; set; } = true;

        /// <summary>
        /// Nome (ou reference name) do campo de tipo do EPIC — ex.: "EPIC_TYPE" ou
        /// "Custom.EPIC_TYPE". Só é usado com <see cref="EpicTypeFieldEnabled"/> ligado.
        /// </summary>
        public string EpicTypeFieldName { get; set; } = "EPIC_TYPE";

        /// <summary>
        /// Habilita a leitura do grupo administrador do NX no work item Project (campo
        /// <see cref="AdmGroupFieldName"/>). Os membros desse grupo do DevOps são os únicos que
        /// podem sincronizar (Export → Sincronizar). Desligado, ou campo vazio, libera para todos.
        /// </summary>
        public bool AdmGroupFieldEnabled { get; set; } = true;

        /// <summary>
        /// Nome (ou reference name) do campo do work item Project que aponta o grupo administrador
        /// do NX — ex.: "Adm_NX" ou "Custom.Adm_NX". O valor é o nome de um grupo do DevOps cujos
        /// membros podem sincronizar. Só é usado com <see cref="AdmGroupFieldEnabled"/> ligado.
        /// </summary>
        public string AdmGroupFieldName { get; set; } = "Adm_NX";

        /// <summary>
        /// Habilita faixa personalizada de prioridade da Task. Desligado (padrão), a gravação
        /// usa a faixa padrão do DevOps (1–4). Ligue apenas se o processo do DevOps aceitar
        /// valores maiores — valor fora da faixa do processo dá erro na gravação do TFS.
        /// </summary>
        public bool TaskPriorityRangeEnabled { get; set; } = false;

        /// <summary>Prioridade mínima da Task quando a faixa personalizada está habilitada.</summary>
        public int TaskPriorityMin { get; set; } = 1;

        /// <summary>Prioridade máxima da Task quando a faixa personalizada está habilitada.</summary>
        public int TaskPriorityMax { get; set; } = 4;

        /// <summary>Tag DevOps que marca data de início fixada/negociada (ex.: "DT-INI-NEG").</summary>
        public string FixedStartTagName { get; set; } = "DT-INI-NEG";

        /// <summary>Tag que marca a Task como NÃO PLANEJADA no DevOps. Aparece em destaque
        /// no card do TaskBoard quando a Task tem essa tag.</summary>
        public string UnplannedTagName { get; set; } = "NP";

        /// <summary>Tag que marca a Task como acima do limite de WIP configurado no TaskBoard.</summary>
        public string WipTagName { get; set; } = "WIP";

        /// <summary>Tag que marca a Story/Task como BLOQUEADA no DevOps.</summary>
        public string BlockedTagName { get; set; } = "BLOCK";

        /// <summary>Sincroniza links de predecessora no DevOps durante Export → Sincronizar.</summary>
        public bool SyncPredecessorLinks { get; set; } = true;

        /// <summary>Exige TKs > 0 para permitir digitar 100% manualmente em Story DevOps.</summary>
        public bool EnforceStoryCompletionWithTasks { get; set; } = true;

        /// <summary>
        /// Habilita a descoberta ampla de pessoas na organização (Discovery TFS).
        /// Requer o escopo Graph (Read) no PAT. Quando desabilitado, o botão
        /// "Discovery TFS" nem aparece na tela de Pessoas. Padrão: desabilitado.
        /// </summary>
        public bool EnableOrgPeopleDiscovery { get; set; } = false;

        /// <summary>
        /// Janela de dias futuros para incluir sprints no dropdown mesmo sem work items.
        /// Padrão 90 dias. Use 0 para incluir apenas sprints com itens importados.
        /// </summary>
        public int FutureSprintDays { get; set; } = 90;

        /// <summary>Caminho do arquivo JSON da lista de projetos DevOps (compartilhável entre usuários).</summary>
        public string DevOpsProjectListPath { get; set; } = string.Empty;

        /// <summary>Código do idioma selecionado pelo usuário (ex: "pt-BR", "en-US"). Vazio = detectar do Windows.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Nome da empresa exibido no cabeçalho do PDF exportado.</summary>
        public string CompanyName { get; set; } = string.Empty;

        /// <summary>Logo da empresa em Base64 PNG (normalizado 300×80px) para cabeçalho do PDF. Vazio = sem logo.</summary>
        public string CompanyLogoBase64 { get; set; } = string.Empty;

        /// <summary>Habilita log de diagnóstico em %LocalAppData%\NXProject.Community\sprint_alert_debug.log.</summary>
        public bool DebugLogEnabled { get; set; } = false;

        /// <summary>Carrega automaticamente o .nxb ao abrir o projeto. false = apenas via menu Abrir Baseline.</summary>
        public bool AutoLoadBaseline { get; set; } = true;

        /// <summary>Mapeamentos customizados de estado TFS → label do gráfico de status.</summary>
        public List<StoryStatusMapping> StoryStatusMappings { get; set; } = [];

        /// <summary>Caminhos de arquivos conhecidos no portfólio (Mapa de Alocação).</summary>
        public List<string> PortfolioProjectPaths { get; set; } = [];

        /// <summary>Configuração por projeto do portfólio (OPEX/CAPEX, centro de custo).</summary>
        public List<PortfolioProjectConfig> PortfolioProjectConfigs { get; set; } = [];

        /// <summary>
        /// Campos fixos enviados em toda criação de work item (útil para campos obrigatórios
        /// do processo do cliente que o NXProject não gerencia).
        /// Exemplo: [{ "ref": "Custom.Type", "value": "Atividade" }]
        /// </summary>
        public List<ExtraWorkItemField> ExtraCreateFields { get; set; } = [];

        /// <summary>
        /// Mapeamentos de campos por tipo de work item (Epic, Feature, Story, Task).
        /// Sobrescreve os campos globais (EffortFieldName, StartFieldName, FinishFieldName etc.)
        /// para o tipo especificado.
        /// Exemplo: { "Epic": { "EffortField": "Effort", "StartField": "Start Date", "FinishField": "Target Date" } }
        /// </summary>
        public Dictionary<string, TypeFieldConfig> TypeFieldMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Valores do picklist de classificação (campo customizado obrigatório na criação).
        /// Exibidos no dropdown "Alterar Classificação" no NXProject.
        /// Vazio = usa os valores padrão embutidos.
        /// </summary>
        public List<string> ClassificationPicklistValues { get; set; } = [];

        /// <summary>Lista de categorias de atividade (campo Activity do DevOps). Configurável pelo usuário.</summary>
        public List<string> TaskActivityList { get; set; } =
        [
            "Deployment",
            "Design",
            "Development",
            "Documentation",
            "Requirements",
            "Testing"
        ];

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(OrganizationUrl) &&
            !string.IsNullOrWhiteSpace(TeamProject) &&
            !string.IsNullOrWhiteSpace(PersonalAccessToken) &&
            RootWorkItemId > 0;
    }

    /// <summary>
    /// Configuração de campos do DevOps específica por tipo de work item.
    /// Campos nulos herdam o valor global de TfsConnectionOptions.
    /// </summary>
    public sealed class TypeFieldConfig
    {
        /// <summary>Campo de esforço/horas estimadas (ex.: "Effort", "HH Estimado").</summary>
        public string? EffortField { get; set; }

        /// <summary>Campo de data de início (ex.: "Start Date", "Data_Inicio").</summary>
        public string? StartField { get; set; }

        /// <summary>Campo de data de fim (ex.: "Target Date", "Data_Fim").</summary>
        public string? FinishField { get; set; }

        /// <summary>Campo de percentual de alocação.</summary>
        public string? PercAlocField { get; set; }

        /// <summary>Campo de percentual de conclusão.</summary>
        public string? PercConclusaoField { get; set; }

        /// <summary>Lista de campos Custom DevOps configurados para este tipo (ex.: Custom.Type).</summary>
        public List<ClassificationFieldDef> CustomDevopsFields { get; set; } = [];
    }

    /// <summary>Definição de um campo Custom DevOps dentro de um TypeFieldConfig.</summary>
    public sealed class ClassificationFieldDef
    {
        /// <summary>Referência do campo no DevOps (ex.: "Custom.Type").</summary>
        public string Field { get; set; } = string.Empty;
        /// <summary>Tipo do campo no DevOps: Picklist, Integer, Text, Date.</summary>
        public string FieldType { get; set; } = "Picklist";
        /// <summary>Valores separados por vírgula; viram combo ao editar classificação.</summary>
        public string? Values { get; set; }
    }

    /// <summary>
    /// Persiste a conexao do TFS (org, projeto, ultimo ID, horas/dia) e,
    /// opcionalmente, o PAT cifrado via DPAPI no escopo do usuario.
    /// </summary>
    public static class TfsConnectionStore
    {
        private sealed class StoredConnection
        {
            public string OrganizationUrl { get; set; } = "";
            public string TeamProject { get; set; } = "";
            public int RootWorkItemId { get; set; }
            public double HoursPerDay { get; set; } = 8.0;
            public string EffortFieldName { get; set; } = "HH Estimado";
            public string StartFieldName { get; set; } = "Data_Inicio";
            public string FinishFieldName { get; set; } = "Data_Fim";
            public string PercAlocFieldName { get; set; } = "Perc_Alocacao";
            public string PercConclusaoFieldName { get; set; } = "Perc_Conclusao";
            public string SyncVersionFieldName { get; set; } = "Sync_version";
            public string SyncNameFieldName { get; set; } = "Sync_Name";
            public string FixedStartTagName { get; set; } = "DT-INI-NEG";

        /// <summary>Tag que marca a Task como NÃO PLANEJADA no DevOps. Aparece em destaque
        /// no card do TaskBoard quando a Task tem essa tag.</summary>
        public string UnplannedTagName { get; set; } = "NP";
        /// <summary>Tag que marca a Task como acima do limite de WIP configurado no TaskBoard.</summary>
        public string WipTagName { get; set; } = "WIP";

        /// <summary>Tag que marca a Story/Task como BLOQUEADA no DevOps.</summary>
        public string BlockedTagName { get; set; } = "BLOCK";

            public bool   ApprovedFieldEnabled { get; set; } = true;
            public string ApprovedFieldName { get; set; } = "Approved";
            public bool   EpicTypeFieldEnabled { get; set; } = true;
            public string EpicTypeFieldName { get; set; } = "EPIC_TYPE";
            public bool   AdmGroupFieldEnabled { get; set; } = true;
            public string AdmGroupFieldName { get; set; } = "Adm_NX";
            public bool   TaskPriorityRangeEnabled { get; set; }
            public int    TaskPriorityMin { get; set; } = 1;
            public int    TaskPriorityMax { get; set; } = 4;
            public bool SyncPredecessorLinks { get; set; } = true;
            public bool EnforceStoryCompletionWithTasks { get; set; } = true;
            public bool EnableOrgPeopleDiscovery { get; set; }
            public int FutureSprintDays { get; set; } = 90;
            public bool RememberToken { get; set; }
            public string EncryptedToken { get; set; } = string.Empty;
            public string DevOpsProjectListPath { get; set; } = string.Empty;
            public string Language { get; set; } = string.Empty;
            public string CompanyName { get; set; } = string.Empty;
            public string CompanyLogoBase64 { get; set; } = string.Empty;
            public List<StoryStatusMapping> StoryStatusMappings { get; set; } = [];
            public List<string> PortfolioProjectPaths { get; set; } = [];
            public List<PortfolioProjectConfig> PortfolioProjectConfigs { get; set; } = [];
            public bool DebugLogEnabled { get; set; } = false;
            public bool AutoLoadBaseline { get; set; } = true;
            public Dictionary<string, TypeFieldConfig> TypeFieldMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> ClassificationPicklistValues { get; set; } = [];
            public List<ExtraWorkItemField> ExtraCreateFields { get; set; } = [];
        }

        public static TfsConnectionOptions Load(string storageKey = "NXProject.Community")
        {
            var file = GetSettingsFile(storageKey);
            var options = new TfsConnectionOptions
            {
                HoursPerDay = ProjectCalendarService.WorkingHoursPerDay
            };
            if (!File.Exists(file))
                return options;

            try
            {
                var stored = JsonSerializer.Deserialize<StoredConnection>(File.ReadAllText(file));
                if (stored == null)
                    return options;

                options.OrganizationUrl = string.IsNullOrWhiteSpace(stored.OrganizationUrl)
                    ? options.OrganizationUrl
                    : stored.OrganizationUrl.Trim();
                options.TeamProject = string.IsNullOrWhiteSpace(stored.TeamProject)
                    ? options.TeamProject
                    : stored.TeamProject.Trim();
                options.RootWorkItemId = stored.RootWorkItemId;
                options.HoursPerDay = stored.HoursPerDay <= 0 ? 8.0 : stored.HoursPerDay;
                options.EffortFieldName = string.IsNullOrWhiteSpace(stored.EffortFieldName)
                    ? options.EffortFieldName : stored.EffortFieldName.Trim();
                options.StartFieldName = string.IsNullOrWhiteSpace(stored.StartFieldName)
                    ? options.StartFieldName : stored.StartFieldName.Trim();
                options.FinishFieldName = string.IsNullOrWhiteSpace(stored.FinishFieldName)
                    ? options.FinishFieldName : stored.FinishFieldName.Trim();
                options.PercAlocFieldName = string.IsNullOrWhiteSpace(stored.PercAlocFieldName)
                    ? options.PercAlocFieldName : stored.PercAlocFieldName.Trim();
                // Migração: nome antigo acentuado → ASCII (o campo no TFS é Perc_Alocacao).
                if (string.Equals(options.PercAlocFieldName, "Perc_Alocação", StringComparison.Ordinal))
                    options.PercAlocFieldName = "Perc_Alocacao";
                options.PercConclusaoFieldName = string.IsNullOrWhiteSpace(stored.PercConclusaoFieldName)
                    ? options.PercConclusaoFieldName : stored.PercConclusaoFieldName.Trim();
                options.SyncVersionFieldName = string.IsNullOrWhiteSpace(stored.SyncVersionFieldName)
                    ? options.SyncVersionFieldName : stored.SyncVersionFieldName.Trim();
                options.SyncNameFieldName = string.IsNullOrWhiteSpace(stored.SyncNameFieldName)
                    ? options.SyncNameFieldName : stored.SyncNameFieldName.Trim();
                options.FixedStartTagName = string.IsNullOrWhiteSpace(stored.FixedStartTagName)
                    ? options.FixedStartTagName : stored.FixedStartTagName.Trim();
                options.UnplannedTagName = string.IsNullOrWhiteSpace(stored.UnplannedTagName)
                    ? options.UnplannedTagName : stored.UnplannedTagName.Trim();
                options.WipTagName = string.IsNullOrWhiteSpace(stored.WipTagName)
                    ? options.WipTagName : stored.WipTagName.Trim();
                options.BlockedTagName = string.IsNullOrWhiteSpace(stored.BlockedTagName)
                    ? options.BlockedTagName : stored.BlockedTagName.Trim();
                // O cronograma lê a tag de bloqueio por um ponto estático (ViewModels não têm
                // as opções em mãos) — mantém os dois lados com o mesmo nome.
                TfsImportService.BlockTagName = options.BlockedTagName;
                options.ApprovedFieldEnabled = stored.ApprovedFieldEnabled;
                options.ApprovedFieldName = string.IsNullOrWhiteSpace(stored.ApprovedFieldName)
                    ? options.ApprovedFieldName : stored.ApprovedFieldName.Trim();
                options.EpicTypeFieldEnabled = stored.EpicTypeFieldEnabled;
                options.EpicTypeFieldName = string.IsNullOrWhiteSpace(stored.EpicTypeFieldName)
                    ? options.EpicTypeFieldName : stored.EpicTypeFieldName.Trim();
                options.AdmGroupFieldEnabled = stored.AdmGroupFieldEnabled;
                options.AdmGroupFieldName = string.IsNullOrWhiteSpace(stored.AdmGroupFieldName)
                    ? options.AdmGroupFieldName : stored.AdmGroupFieldName.Trim();
                options.TaskPriorityRangeEnabled = stored.TaskPriorityRangeEnabled;
                options.TaskPriorityMin = stored.TaskPriorityMin >= 1 ? stored.TaskPriorityMin : 1;
                options.TaskPriorityMax = stored.TaskPriorityMax >= options.TaskPriorityMin
                    ? stored.TaskPriorityMax : Math.Max(options.TaskPriorityMin, 4);
                options.SyncPredecessorLinks = stored.SyncPredecessorLinks;
                options.EnforceStoryCompletionWithTasks = stored.EnforceStoryCompletionWithTasks;
                options.EnableOrgPeopleDiscovery = stored.EnableOrgPeopleDiscovery;
                options.FutureSprintDays = stored.FutureSprintDays >= 0 ? stored.FutureSprintDays : 90;
                if (stored.RememberToken)
                    options.PersonalAccessToken = WindowsDataProtection.Decrypt(stored.EncryptedToken);
                options.DevOpsProjectListPath = stored.DevOpsProjectListPath ?? string.Empty;
                options.Language = stored.Language ?? string.Empty;
                options.CompanyName = stored.CompanyName ?? string.Empty;
                options.CompanyLogoBase64 = stored.CompanyLogoBase64 ?? string.Empty;
                options.StoryStatusMappings = stored.StoryStatusMappings ?? [];
                options.PortfolioProjectPaths = stored.PortfolioProjectPaths ?? [];
                options.PortfolioProjectConfigs = stored.PortfolioProjectConfigs ?? [];
                options.DebugLogEnabled  = stored.DebugLogEnabled;
                options.AutoLoadBaseline = stored.AutoLoadBaseline;
                options.TypeFieldMappings = stored.TypeFieldMappings ?? new(StringComparer.OrdinalIgnoreCase);
                options.ClassificationPicklistValues = stored.ClassificationPicklistValues ?? [];
                options.ExtraCreateFields = stored.ExtraCreateFields ?? [];
            }
            catch
            {
                return new TfsConnectionOptions();
            }

            return options;
        }

        public static void Save(TfsConnectionOptions options, bool rememberToken, string storageKey = "NXProject.Community")
        {
            var directory = GetSettingsDirectory(storageKey);
            Directory.CreateDirectory(directory);

            var payload = new StoredConnection
            {
                OrganizationUrl = options.OrganizationUrl?.Trim() ?? string.Empty,
                TeamProject = options.TeamProject?.Trim() ?? string.Empty,
                RootWorkItemId = options.RootWorkItemId,
                HoursPerDay = options.HoursPerDay <= 0 ? 8.0 : options.HoursPerDay,
                EffortFieldName = string.IsNullOrWhiteSpace(options.EffortFieldName) ? "HH Estimado" : options.EffortFieldName.Trim(),
                StartFieldName = string.IsNullOrWhiteSpace(options.StartFieldName) ? "Data_Inicio" : options.StartFieldName.Trim(),
                FinishFieldName = string.IsNullOrWhiteSpace(options.FinishFieldName) ? "Data_Fim" : options.FinishFieldName.Trim(),
                PercAlocFieldName = string.IsNullOrWhiteSpace(options.PercAlocFieldName) ? "Perc_Alocacao" : options.PercAlocFieldName.Trim(),
                PercConclusaoFieldName = string.IsNullOrWhiteSpace(options.PercConclusaoFieldName) ? "Perc_Conclusao" : options.PercConclusaoFieldName.Trim(),
                SyncVersionFieldName = string.IsNullOrWhiteSpace(options.SyncVersionFieldName) ? "Sync_version" : options.SyncVersionFieldName.Trim(),
                SyncNameFieldName = string.IsNullOrWhiteSpace(options.SyncNameFieldName) ? "Sync_Name" : options.SyncNameFieldName.Trim(),
                FixedStartTagName = string.IsNullOrWhiteSpace(options.FixedStartTagName) ? "DT-INI-NEG" : options.FixedStartTagName.Trim(),
                UnplannedTagName = string.IsNullOrWhiteSpace(options.UnplannedTagName) ? "NP" : options.UnplannedTagName.Trim(),
                WipTagName = string.IsNullOrWhiteSpace(options.WipTagName) ? "WIP" : options.WipTagName.Trim(),
                BlockedTagName = string.IsNullOrWhiteSpace(options.BlockedTagName) ? "BLOCK" : options.BlockedTagName.Trim(),
                ApprovedFieldEnabled = options.ApprovedFieldEnabled,
                ApprovedFieldName = string.IsNullOrWhiteSpace(options.ApprovedFieldName) ? "Approved" : options.ApprovedFieldName.Trim(),
                EpicTypeFieldEnabled = options.EpicTypeFieldEnabled,
                EpicTypeFieldName = string.IsNullOrWhiteSpace(options.EpicTypeFieldName) ? "EPIC_TYPE" : options.EpicTypeFieldName.Trim(),
                AdmGroupFieldEnabled = options.AdmGroupFieldEnabled,
                AdmGroupFieldName = string.IsNullOrWhiteSpace(options.AdmGroupFieldName) ? "Adm_NX" : options.AdmGroupFieldName.Trim(),
                TaskPriorityRangeEnabled = options.TaskPriorityRangeEnabled,
                TaskPriorityMin = options.TaskPriorityMin >= 1 ? options.TaskPriorityMin : 1,
                TaskPriorityMax = options.TaskPriorityMax >= 1 ? options.TaskPriorityMax : 4,
                SyncPredecessorLinks = options.SyncPredecessorLinks,
                EnforceStoryCompletionWithTasks = options.EnforceStoryCompletionWithTasks,
                EnableOrgPeopleDiscovery = options.EnableOrgPeopleDiscovery,
                FutureSprintDays = options.FutureSprintDays,
                RememberToken = rememberToken,
                EncryptedToken = rememberToken
                    ? WindowsDataProtection.Encrypt(options.PersonalAccessToken ?? string.Empty, "NXProject.Tfs")
                    : string.Empty,
                DevOpsProjectListPath = options.DevOpsProjectListPath ?? string.Empty,
                Language = options.Language ?? string.Empty,
                CompanyName = options.CompanyName ?? string.Empty,
                CompanyLogoBase64 = options.CompanyLogoBase64 ?? string.Empty,
                StoryStatusMappings = options.StoryStatusMappings ?? [],
                PortfolioProjectPaths = options.PortfolioProjectPaths ?? [],
                PortfolioProjectConfigs = options.PortfolioProjectConfigs ?? [],
                DebugLogEnabled  = options.DebugLogEnabled,
                AutoLoadBaseline = options.AutoLoadBaseline,
                TypeFieldMappings = options.TypeFieldMappings ?? new(StringComparer.OrdinalIgnoreCase),
                ClassificationPicklistValues = options.ClassificationPicklistValues ?? [],
                ExtraCreateFields = options.ExtraCreateFields ?? []
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetSettingsFile(storageKey), json);
        }

        private static string GetSettingsDirectory(string storageKey)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim());
        }

        private static string GetSettingsFile(string storageKey)
        {
            return Path.Combine(GetSettingsDirectory(storageKey), "config_nxproject.json");
        }
    }
}
