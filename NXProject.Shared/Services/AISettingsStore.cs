// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NXProject.Models;

namespace NXProject.Services
{
    public static class AISettingsStore
    {
        // ── Formato legado (arquivo unico OpenRouter) ────────────────────
        private sealed class StoredAISettings
        {
            public string Provider { get; set; } = "OpenRouter";
            public string EncryptedApiKey { get; set; } = string.Empty;
            public string Endpoint { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public int TimeoutSeconds { get; set; } = 120;
        }

        // ── Formato multi-provedor ───────────────────────────────────────
        private sealed class StoredProviderProfile
        {
            public string Provider { get; set; } = "OpenRouter";
            public string AuthMode { get; set; } = "ApiKey";
            public string EncryptedApiKey { get; set; } = string.Empty;
            public string Endpoint { get; set; } = string.Empty;
            public string Model { get; set; } = string.Empty;
            public string CliWindowsCommand { get; set; } = string.Empty;
            public string CliWslCommand { get; set; } = string.Empty;
            public string LoginUrl { get; set; } = string.Empty;
            public int SessionExpirationHours { get; set; } = 24;
            public int TimeoutSeconds { get; set; } = 120;
        }

        private sealed class StoredActionType
        {
            public string Name { get; set; } = string.Empty;
            public string Prompt { get; set; } = string.Empty;
            public bool CreatesTasks { get; set; }
        }

        private sealed class StoredWorkspace
        {
            public string DefaultProvider { get; set; } = "CodexCli";
            public string ScheduleMode { get; set; } = "DevOps";
            public bool CreateTasks { get; set; }
            public int AnalysisTaskLimit { get; set; } = 30;
            public int ChatHistoryLimit { get; set; } = 10;
            public int ChatHistoryWindow { get; set; } = 8;
            public int ChatCompressThreshold { get; set; } = 350_000;
            public string LastPrompt { get; set; } = string.Empty;
            public List<StoredProviderProfile> Providers { get; set; } = new();
            public List<StoredActionType> ActionTypes { get; set; } = new();
            public string SelectedAction { get; set; } = AIActionType.ScheduleDevOpsActionName;
            public int ActionsSchemaVersion { get; set; }
        }

        private const string LegacyScheduleDevOpsActionName = "Fazer Cronograma Devops";
        private const string LegacyScheduleNoDevOpsActionName = "Cronograma NoDevops";

        // Versao atual do schema de acoes (v2: "Fazer Cronograma DevOps"; v3: merge de arquivo externo; v4: merge por hierarquia/ID; v5: ID interno nao bloqueia busca TFS; v6: incluir tasks na planilha; v7: consultar task na planilha; v8: incluir captura responsavel/esforco; v9: nome da task com verbo no infinitivo + descricao; v10: acao de ajuste de nome de task).
        private const int CurrentActionsSchemaVersion = 11;

        /// <summary>Prompt do merge de planilha externa (Task Plan) com as Tasks do DevOps.</summary>
        private const string LegacyMergeExternalActionPrompt =
            "Você é um assistente do NXProject que faz merge entre um arquivo externo de plano de tasks " +
            "e as Tasks reais do Azure DevOps. Você recebe duas listas: LINHAS (linhas do arquivo, com número, " +
            "nome da task, story e ID atual) e TASKS_DEVOPS (id, título, story, estado, prioridade). " +
            "Associe cada linha à Task do DevOps correspondente comparando os nomes (podem ter pequenas " +
            "diferenças de escrita, abreviações ou acentos), respeitando a Story quando informada. " +
            "Responda SOMENTE com um JSON válido, sem comentários, no formato: " +
            "[{\"linha\": 1, \"id_devops\": 123, \"task_devops\": \"título\", \"confianca\": \"alta|media|baixa\"}]. " +
            "Inclua apenas linhas com correspondência; não invente IDs.";

        public const string MergeExternalActionPrompt =
            "Você é um assistente do NXProject que faz merge entre um arquivo externo de plano de tasks " +
            "e as Tasks reais do Azure DevOps. Você recebe duas listas: LINHAS (linhas do arquivo, com número, " +
            "nome da task, ID Task atual, EPIC, ID EPIC, Feature, ID Feature, Story e ID Story) e TASKS_DEVOPS " +
            "(id, título, story, story_id, feature, feature_id, epic, epic_id, estado, prioridade). " +
            "Associe cada linha à Task do DevOps correspondente comparando os nomes (podem ter pequenas " +
            "diferenças de escrita, abreviações ou acentos), mas respeite rigorosamente a hierarquia e os IDs. " +
            "ID Task terminado em :T é vínculo TFS real; ID Task terminado em :I é apenas ID interno temporário e NÃO deve bloquear a busca: " +
            "nesse caso procure a Task TFS correspondente pelo nome da Task dentro da Story/Feature/EPIC informada. " +
            "Se a linha informar ID Story, ID Feature ou ID EPIC, só associe com uma Task cuja Story/Feature/EPIC tenha o mesmo ID. " +
            "Para IDs de Story/Feature/EPIC terminados em :I, use o nome e a hierarquia como referência; não trate :I como ID TFS real. " +
            "Se a linha informar Story, Feature ou EPIC por nome, só associe dentro dessa mesma hierarquia. " +
            "Nunca associe uma Task de uma Story de mesmo nome quando ela estiver em Feature ou EPIC diferente. " +
            "Responda SOMENTE com um JSON válido, sem comentários, no formato: " +
            "[{\"linha\": 1, \"id_devops\": 123, \"task_devops\": \"título\", \"confianca\": \"alta|media|baixa\"}]. " +
            "Inclua apenas linhas com correspondência; não invente IDs.";

        /// <summary>Prompt v1 da inclusão de tasks (mantido para a migração v8 detectar prompt não editado).</summary>
        private const string PlanIncludeActionPromptV1 =
            "Você é um assistente do NXProject que inclui tasks na planilha do Task Plan a partir de uma " +
            "lista de atividades citadas em reunião. Você recebe: STORIES (Stories do cronograma, com id, " +
            "nome, feature e epic), TASKS_PLANILHA (tasks que já existem na planilha, com story e task) e " +
            "TEXTO (lista de atividades, tendo no mínimo a story e o nome da task em cada item). " +
            "Para cada atividade do TEXTO, encontre a Story do cronograma cujo nome melhor corresponde ao " +
            "citado (tolere abreviações, acentos e pequenas diferenças de escrita). " +
            "Responda SOMENTE com um JSON válido, sem comentários, no formato: " +
            "[{\"story_id\": 123, \"story\": \"nome da story do cronograma\", \"task\": \"nome da task\", \"obs\": \"\"}]. " +
            "Use story_id EXATAMENTE como veio em STORIES; não invente stories nem ids. " +
            "Não inclua atividades que já existem em TASKS_PLANILHA com o mesmo nome de task na mesma Story. " +
            "Se não encontrar Story correspondente para um item, devolva-o com story_id 0 e o motivo em obs.";

        /// <summary>Prompt v2 da inclusão de tasks (mantido para a migração v9 detectar prompt não editado).</summary>
        private const string PlanIncludeActionPromptV2 =
            "Você é um assistente do NXProject que inclui tasks na planilha do Task Plan a partir de uma " +
            "lista de atividades citadas em reunião. Você recebe: STORIES (Stories do cronograma, com id, " +
            "nome, feature e epic), RECURSOS (nomes das pessoas do cronograma), TASKS_PLANILHA (tasks que " +
            "já existem na planilha, com story e task) e TEXTO (lista de atividades, tendo no mínimo a " +
            "story e o nome da task em cada item; pode citar também o responsável e o esforço). " +
            "Para cada atividade do TEXTO, encontre a Story do cronograma cujo nome melhor corresponde ao " +
            "citado (tolere abreviações, acentos e pequenas diferenças de escrita). " +
            "Responda SOMENTE com um JSON válido, sem comentários, no formato: " +
            "[{\"story_id\": 123, \"story\": \"nome da story\", \"task\": \"nome da task\", " +
            "\"responsavel\": \"\", \"esforco\": \"\", \"obs\": \"\"}]. " +
            "Use story_id EXATAMENTE como veio em STORIES; não invente stories nem ids. " +
            "responsavel: quando o TEXTO citar quem fará a task (mesmo só parte do nome, apelido ou primeiro " +
            "nome), devolva o nome EXATO correspondente da lista RECURSOS; sem correspondência, deixe vazio. " +
            "esforco: quando o TEXTO citar o esforço, devolva número em HORAS (ex.: \"8\", \"6,5\"); se o texto " +
            "der em DIAS, devolva o número com sufixo d (ex.: \"2d\"); sem esforço citado, deixe vazio. " +
            "Não inclua atividades que já existem em TASKS_PLANILHA com o mesmo nome de task na mesma Story. " +
            "Se não encontrar Story correspondente para um item, devolva-o com story_id 0 e o motivo em obs.";

        /// <summary>Prompt da inclusão de tasks na planilha do Task Plan a partir de texto de reunião.</summary>
        private const string PlanIncludeActionPromptV3 =
            "Você é um assistente do NXProject que inclui tasks na planilha do Task Plan a partir de uma " +
            "lista de atividades citadas em reunião. Você recebe: STORIES (tabela \"id | nome | feature | " +
            "epic\" com as Stories EM ABERTO do cronograma — pode haver Stories de MESMO NOME em feature/epic " +
            "diferentes; use feature e epic para distinguir), RECURSOS (nomes das pessoas) e TEXTO (lista de " +
            "atividades, tendo no mínimo a story e o nome da task em cada item; pode citar também o " +
            "responsável, o esforço e detalhes). " +
            "Convenção de nomes do NXProject: nome de STORY é NOMINAL e NUNCA se inicia com verbo (identifica " +
            "o tema/entrega, ex.: \"Backup de dados\", \"Condição Y014\"); nome de TASK SEMPRE começa com VERBO " +
            "NO INFINITIVO (identifica a ação, ex.: \"Validar cargas de backup\"). Use essa convenção para " +
            "distinguir, no TEXTO, o que é Story e o que é task. " +
            "Para cada atividade do TEXTO, encontre a Story do cronograma cujo nome melhor corresponde ao " +
            "citado (tolere abreviações, acentos e pequenas diferenças de escrita). " +
            "Responda SOMENTE com um JSON válido, COMPACTO (uma linha, sem espaços desnecessários), sem " +
            "comentários, no formato: [{\"story_id\":123,\"task\":\"nome da task\",\"responsavel\":\"\"," +
            "\"esforco\":\"\",\"descricao\":\"\",\"obs\":\"\"}]. NÃO repita o nome da story na resposta e " +
            "OMITA os campos vazios — só story_id e task são obrigatórios. " +
            "Cada atividade do TEXTO gera EXATAMENTE UM item na resposta: escolha a ÚNICA Story mais " +
            "provável (desempate por feature/epic e pelo contexto do TEXTO) — NUNCA repita a mesma task " +
            "em várias stories, e a resposta NUNCA tem mais itens que as linhas de atividade do TEXTO. " +
            "Use story_id EXATAMENTE como veio em STORIES; não invente stories nem ids. " +
            "task: nome CURTO da atividade começando com um VERBO NO INFINITIVO (ex.: \"Ajustar views de " +
            "condição\", \"Criar tabela T001W\"). NUNCA use o nome da Story (nem tema sem verbo) como nome da " +
            "task — Story identifica o tema, task identifica a AÇÃO. Se o TEXTO trouxer um tema sem verbo e a " +
            "ação em seguida, componha \"tema - Verbo + complemento\" ou apenas \"Verbo + complemento (tema)\". " +
            "descricao: o detalhe adicional citado no TEXTO sobre a atividade (contexto, escopo, observação " +
            "técnica); omita se não houver — NÃO repita o nome da task. " +
            "responsavel: quando o TEXTO citar quem fará a task (mesmo só parte do nome, apelido ou primeiro " +
            "nome), devolva o nome EXATO correspondente da lista RECURSOS; sem correspondência na lista, " +
            "devolva o nome COMO CITADO no TEXTO (o NX registra na observação) — só omita se o TEXTO não " +
            "citar ninguém. " +
            "esforco: quando o TEXTO citar o esforço, devolva número em HORAS (ex.: \"8\", \"6,5\"); se o texto " +
            "der em DIAS, devolva o número com sufixo d (ex.: \"2d\"); sem esforço citado, omita. " +
            "Se não encontrar Story correspondente para um item, devolva-o com story_id 0 e o motivo em obs.";

        public const string PlanIncludeActionPrompt =
            "Você é um assistente do NXProject que inclui tasks na planilha do Task Plan a partir de uma " +
            "lista de atividades citadas em reunião. Você recebe: STORIES (tabela \"id | nome | feature | " +
            "epic\" com as Stories EM ABERTO do cronograma — pode haver Stories de MESMO NOME em feature/epic " +
            "diferentes; use feature e epic para distinguir), RECURSOS (nomes das pessoas) e TEXTO (lista de " +
            "atividades, tendo no mínimo a story e o nome da task em cada item; pode citar também o " +
            "responsável, o esforço e detalhes). " +
            "Convenção de nomes do NXProject: nome de STORY é NOMINAL e NUNCA se inicia com verbo (identifica " +
            "o tema/entrega, ex.: \"Backup de dados\", \"Condição Y014\"); nome de TASK SEMPRE começa com VERBO " +
            "NO INFINITIVO (identifica a ação, ex.: \"Validar cargas de backup\"). Use essa convenção para " +
            "distinguir, no TEXTO, o que é Story e o que é task. " +
            "REGRA DE REAPROVEITAMENTO DE STORY (obrigatória): só use uma Story de STORIES quando o nome citado " +
            "no TEXTO for IGUAL ao nome dela — ignore APENAS acentuação, maiúsc./minúsc., espaços e pequenos " +
            "erros de digitação. NÃO reaproveite por semelhança parcial, por abreviação, nem por compartilhar " +
            "algumas palavras. Se o nome citado NÃO for essencialmente o MESMO de uma Story existente, então é " +
            "uma Story NOVA: devolva story_id 0 e escreva o nome citado da story em obs (NÃO force o encaixe " +
            "numa Story parecida). Havendo Stories de mesmo nome, desempate por feature/epic e contexto. " +
            "Responda SOMENTE com um JSON válido, COMPACTO (uma linha, sem espaços desnecessários), sem " +
            "comentários, no formato: [{\"story_id\":123,\"task\":\"nome da task\",\"responsavel\":\"\"," +
            "\"esforco\":\"\",\"descricao\":\"\",\"obs\":\"\"}]. NÃO repita o nome da story na resposta e " +
            "OMITA os campos vazios — só story_id e task são obrigatórios. " +
            "Cada atividade do TEXTO gera EXATAMENTE UM item na resposta: NUNCA repita a mesma task " +
            "em várias stories, e a resposta NUNCA tem mais itens que as linhas de atividade do TEXTO. " +
            "Use story_id EXATAMENTE como veio em STORIES; não invente stories nem ids. " +
            "task: nome CURTO da atividade começando com um VERBO NO INFINITIVO (ex.: \"Ajustar views de " +
            "condição\", \"Criar tabela T001W\"). NUNCA use o nome da Story (nem tema sem verbo) como nome da " +
            "task — Story identifica o tema, task identifica a AÇÃO. Se o TEXTO trouxer um tema sem verbo e a " +
            "ação em seguida, componha \"tema - Verbo + complemento\" ou apenas \"Verbo + complemento (tema)\". " +
            "descricao: o detalhe adicional citado no TEXTO sobre a atividade (contexto, escopo, observação " +
            "técnica); omita se não houver — NÃO repita o nome da task. " +
            "responsavel: quando o TEXTO citar quem fará a task (mesmo só parte do nome, apelido ou primeiro " +
            "nome), devolva o nome EXATO correspondente da lista RECURSOS; sem correspondência na lista, " +
            "devolva o nome COMO CITADO no TEXTO (o NX registra na observação) — só omita se o TEXTO não " +
            "citar ninguém. " +
            "esforco: quando o TEXTO citar o esforço, devolva número em HORAS (ex.: \"8\", \"6,5\"); se o texto " +
            "der em DIAS, devolva o número com sufixo d (ex.: \"2d\"); sem esforço citado, omita. " +
            "Se não encontrar Story com nome IGUAL para um item, devolva-o com story_id 0 e o nome citado em obs.";

        /// <summary>
        /// Sufixo do modo STORY NOVA da inclusão do Task Plan: a Story do TEXTO ainda não
        /// existe — o contexto traz FEATURES e a IA devolve feature_id + nome da Story nova.
        /// </summary>
        public const string PlanIncludeNewStorySuffix =
            "\n\nMODO STORY NOVA: as Stories do TEXTO ainda NÃO existem no cronograma — NÃO tente casar " +
            "Story; em vez de STORIES você recebe FEATURES (tabela \"id | nome | epic\" com as Features " +
            "existentes). Para cada atividade, devolva o nome da Story NOVA citada no TEXTO e a Feature " +
            "existente onde ela deve nascer (a mais provável pelo contexto). O JSON passa a ser: " +
            "[{\"feature_id\":123,\"story\":\"nome da story nova\",\"task\":\"nome da task\"," +
            "\"responsavel\":\"\",\"esforco\":\"\",\"descricao\":\"\",\"obs\":\"\"}] — feature_id, story e " +
            "task são obrigatórios; use feature_id EXATAMENTE como veio em FEATURES, não invente ids. " +
            "Tasks da mesma Story nova repetem o MESMO nome de story. " +
            "ATENÇÃO: CADA linha de atividade do TEXTO gera UM item na resposta — uma Story com N " +
            "atividades gera N itens (repetindo a story); NUNCA resuma as atividades de uma Story em um " +
            "único item. Use as chaves EXATAMENTE como no formato, SEM acentos (esforco, responsavel, " +
            "descricao). Se o TEXTO der a estimativa por Story (não por atividade), distribua as horas " +
            "da Story entre as suas tasks. Seja COMPACTO: OMITA descricao quando ela apenas repetir o " +
            "nome da task e OMITA os campos vazios.";

        /// <summary>
        /// Sufixo do modo FEATURE NOVA da inclusão do Task Plan: Feature e Story do TEXTO
        /// ainda não existem — o contexto traz EPICS e a IA devolve epic_id + nomes novos.
        /// </summary>
        public const string PlanIncludeNewFeatureSuffix =
            "\n\nMODO FEATURE NOVA: as Features e Stories do TEXTO ainda NÃO existem no cronograma — NÃO " +
            "tente casar; em vez de STORIES você recebe EPICS (tabela \"id | nome\" com os EPICs " +
            "existentes). Para cada atividade, devolva a Feature NOVA e a Story NOVA citadas no TEXTO e o " +
            "EPIC existente onde a Feature deve nascer (o mais provável pelo contexto). O JSON passa a " +
            "ser: [{\"epic_id\":123,\"feature\":\"nome da feature nova\",\"story\":\"nome da story nova\"," +
            "\"task\":\"nome da task\",\"responsavel\":\"\",\"esforco\":\"\",\"descricao\":\"\",\"obs\":\"\"}] " +
            "— epic_id, feature, story e task são obrigatórios; use epic_id EXATAMENTE como veio em " +
            "EPICS, não invente ids. Tasks da mesma Story repetem os MESMOS nomes de feature e story. " +
            "ATENÇÃO: CADA linha de atividade do TEXTO gera UM item na resposta — uma Story com N " +
            "atividades gera N itens (repetindo feature e story); NUNCA resuma as atividades de uma Story " +
            "em um único item. Use as chaves EXATAMENTE como no formato, SEM acentos (esforco, " +
            "responsavel, descricao). Se o TEXTO der a estimativa por Story (não por atividade), " +
            "distribua as horas da Story entre as suas tasks. Seja COMPACTO: OMITA descricao quando ela " +
            "apenas repetir o nome da task e OMITA os campos vazios.";

        /// <summary>Prompt da consulta/localização de tasks na planilha do Task Plan.</summary>
        public const string PlanFindActionPrompt =
            "Você é um assistente do NXProject que localiza atividades na planilha do Task Plan. " +
            "Você recebe: LINHAS (linhas da planilha, com número, story, task e id) e TEXTO (descrição " +
            "da(s) atividade(s) procurada(s), podendo citar story e task). Encontre as linhas que " +
            "correspondem ao TEXTO, tolerando abreviações, acentos e pequenas diferenças de escrita; " +
            "quando o TEXTO citar a story, respeite-a. Responda SOMENTE com um JSON válido, sem " +
            "comentários, no formato: [{\"linha\": 1, \"task\": \"nome da task\", \"obs\": \"\"}]. " +
            "Inclua apenas correspondências reais; não invente linhas. Se nada corresponder, devolva [].";

        /// <summary>Prompt do ajuste de nomes de task (verbo no infinitivo) — usado pelo parser do Task Plan.</summary>
        public const string TaskNameFixActionPrompt =
            "Você recebe NOMES de tasks de projeto. Reescreva cada um para começar com um VERBO NO " +
            "INFINITIVO em português, curto e mantendo o significado (ex.: \"Validação do PBI\" → " +
            "\"Validar o PBI\"; \"Import de Backup TK13\" → \"Importar Backup TK13\"). " +
            "Responda SOMENTE com JSON compacto: [{\"de\":\"nome original\",\"para\":\"nome ajustado\"}].";

        /// <summary>Prompt do modo livre: responde no dominio de projeto, sem gerar tarefas.</summary>
        private const string FreeActionPrompt =
            "Você é um assistente do NXProject Community. Ajude com planejamento e execução de projetos " +
            "(cronograma, atividades, dependências, estimativas, distribuição de trabalho). Responda em texto " +
            "claro e objetivo, sem formato JSON. Não gere tarefas automaticamente; apenas responda ao pedido.";

        /// <summary>Ações padrão do assistente (usadas no 1º uso e no botão "Restaurar padrão").</summary>
        public static List<AIActionType> GetDefaultActions() => new()
        {
            new AIActionType
            {
                Name = AIActionType.ScheduleDevOpsActionName,
                Prompt = ProjectAIAssistantService.BuildScheduleDeveloperPrompt(false),
                CreatesTasks = true
            },
            new AIActionType
            {
                Name = AIActionType.ScheduleNoDevOpsActionName,
                Prompt = ProjectAIAssistantService.BuildScheduleDeveloperPrompt(false),
                CreatesTasks = true
            },
            new AIActionType
            {
                Name = AIActionType.FreeActionName,
                Prompt = FreeActionPrompt,
                CreatesTasks = false
            },
            new AIActionType
            {
                Name = AIActionType.AnalysisActionName,
                Prompt = ProjectAIAssistantService.ScheduleAnalysisPrompt,
                CreatesTasks = false
            },
            new AIActionType
            {
                Name = AIActionType.MergeExternalActionName,
                Prompt = MergeExternalActionPrompt,
                CreatesTasks = false
            },
            new AIActionType
            {
                Name = AIActionType.PlanIncludeActionName,
                Prompt = PlanIncludeActionPrompt,
                CreatesTasks = false
            },
            new AIActionType
            {
                Name = AIActionType.PlanFindActionName,
                Prompt = PlanFindActionPrompt,
                CreatesTasks = false
            },
            new AIActionType
            {
                Name = AIActionType.TaskNameFixActionName,
                Prompt = TaskNameFixActionPrompt,
                CreatesTasks = false
            },
        };

        private static void SeedDefaultActions(AIWorkspaceSettings workspace)
        {
            if (workspace.SelectedAction == LegacyScheduleDevOpsActionName)
                workspace.SelectedAction = AIActionType.ScheduleDevOpsActionName;
            if (workspace.SelectedAction == LegacyScheduleNoDevOpsActionName)
                workspace.SelectedAction = AIActionType.ScheduleNoDevOpsActionName;

            foreach (var action in workspace.ActionTypes)
            {
                if (action.Name == LegacyScheduleDevOpsActionName)
                    action.Name = AIActionType.ScheduleDevOpsActionName;
                if (action.Name == LegacyScheduleNoDevOpsActionName)
                    action.Name = AIActionType.ScheduleNoDevOpsActionName;
            }

            // Migra o nome legado "Fazer Cronograma" -> "Cronograma NoDevOps".
            var legacyName = workspace.SelectedAction == AIActionType.ScheduleActionName;
            var legacy = workspace.ActionTypes.FirstOrDefault(a => a.Name == AIActionType.ScheduleActionName);
            if (legacy != null) legacy.Name = AIActionType.ScheduleNoDevOpsActionName;

            // Primeiro uso (lista vazia): popula todos os padrões.
            if (workspace.ActionTypes.Count == 0)
            {
                workspace.ActionTypes.AddRange(GetDefaultActions());
                workspace.SelectedAction = AIActionType.ScheduleDevOpsActionName;
            }
            // Migracao unica para v2: garante a acao "Fazer Cronograma DevOps" e a torna padrao.
            else if (workspace.ActionsSchemaVersion < CurrentActionsSchemaVersion)
            {
                if (!workspace.ActionTypes.Any(a => a.Name == AIActionType.ScheduleDevOpsActionName))
                {
                    var devops = GetDefaultActions().First(a => a.Name == AIActionType.ScheduleDevOpsActionName);
                    workspace.ActionTypes.Insert(0, devops);
                }
                if (legacyName || string.IsNullOrWhiteSpace(workspace.SelectedAction))
                    workspace.SelectedAction = AIActionType.ScheduleDevOpsActionName;

                // v3: garante a acao de merge de arquivo externo (usada pelo Task Plan).
                if (!workspace.ActionTypes.Any(a => a.Name == AIActionType.MergeExternalActionName))
                    workspace.ActionTypes.Add(GetDefaultActions().First(a => a.Name == AIActionType.MergeExternalActionName));

                // v4/v5: atualiza apenas prompts padrao antigos; prompts editados pelo usuario ficam preservados.
                var merge = workspace.ActionTypes.FirstOrDefault(a => a.Name == AIActionType.MergeExternalActionName);
                if (merge != null && IsDefaultMergeExternalPrompt(merge.Prompt))
                    merge.Prompt = MergeExternalActionPrompt;

                // v6: garante a acao "Incluir Tasks na Planilha" (usada pelo painel de IA do Task Plan).
                if (!workspace.ActionTypes.Any(a => a.Name == AIActionType.PlanIncludeActionName))
                    workspace.ActionTypes.Add(GetDefaultActions().First(a => a.Name == AIActionType.PlanIncludeActionName));

                // v7: garante a acao "Consultar Task na Planilha" (botão Consultar do painel de IA).
                if (!workspace.ActionTypes.Any(a => a.Name == AIActionType.PlanFindActionName))
                    workspace.ActionTypes.Add(GetDefaultActions().First(a => a.Name == AIActionType.PlanFindActionName));

                // v10: garante a acao "Ajustar Nome de Task (verbo)" (parser do Task Plan).
                if (!workspace.ActionTypes.Any(a => a.Name == AIActionType.TaskNameFixActionName))
                    workspace.ActionTypes.Add(GetDefaultActions().First(a => a.Name == AIActionType.TaskNameFixActionName));

                // v8/v9/v11: prompts padrao antigos do incluir sao atualizados (v11 = reaproveitar
                // Story so com nome IGUAL); prompts editados pelo usuario ficam preservados.
                var planInclude = workspace.ActionTypes.FirstOrDefault(a => a.Name == AIActionType.PlanIncludeActionName);
                if (planInclude != null
                    && (string.Equals(planInclude.Prompt, PlanIncludeActionPromptV1, StringComparison.Ordinal)
                        || string.Equals(planInclude.Prompt, PlanIncludeActionPromptV2, StringComparison.Ordinal)
                        || string.Equals(planInclude.Prompt, PlanIncludeActionPromptV3, StringComparison.Ordinal)))
                    planInclude.Prompt = PlanIncludeActionPrompt;
            }

            workspace.ActionsSchemaVersion = CurrentActionsSchemaVersion;

            if (string.IsNullOrWhiteSpace(workspace.SelectedAction) ||
                !workspace.ActionTypes.Any(a => a.Name == workspace.SelectedAction))
                workspace.SelectedAction = workspace.ActionTypes.FirstOrDefault()?.Name ?? AIActionType.ScheduleDevOpsActionName;
        }

        private static bool IsDefaultMergeExternalPrompt(string? prompt)
        {
            var currentWithoutInternalRule = MergeExternalActionPrompt
                .Replace("ID Task terminado em :T é vínculo TFS real; ID Task terminado em :I é apenas ID interno temporário e NÃO deve bloquear a busca: nesse caso procure a Task TFS correspondente pelo nome da Task dentro da Story/Feature/EPIC informada. ", "", StringComparison.Ordinal)
                .Replace("Para IDs de Story/Feature/EPIC terminados em :I, use o nome e a hierarquia como referência; não trate :I como ID TFS real. ", "", StringComparison.Ordinal);

            return string.Equals(prompt, LegacyMergeExternalActionPrompt, StringComparison.Ordinal)
                || string.Equals(prompt, currentWithoutInternalRule, StringComparison.Ordinal);
        }

        /// <summary>Carrega a configuracao completa (perfis + padrao + modo de cronograma).</summary>
        public static AIWorkspaceSettings LoadWorkspace(string storageKey = "NXProject.Community")
        {
            var workspaceFile = GetWorkspaceFile(storageKey);
            if (File.Exists(workspaceFile))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<StoredWorkspace>(File.ReadAllText(workspaceFile));
                    if (stored != null)
                        return FromStored(stored);
                }
                catch { /* cai no default abaixo */ }
            }

            // Migracao do formato legado (ai-settings.json = OpenRouter unico).
            var legacy = TryLoadLegacy(storageKey);
            if (legacy != null)
                return legacy;

            return DefaultWorkspace();
        }

        /// <summary>Salva a configuracao completa (todas as chaves cifradas por perfil).</summary>
        public static void SaveWorkspace(AIWorkspaceSettings workspace, string storageKey = "NXProject.Community")
        {
            var dir = GetSettingsDirectory(storageKey);
            Directory.CreateDirectory(dir);

            var payload = new StoredWorkspace
            {
                DefaultProvider = workspace.DefaultProvider.ToString(),
                ScheduleMode = workspace.ScheduleMode.ToString(),
                CreateTasks = workspace.CreateTasks,
                AnalysisTaskLimit = workspace.AnalysisTaskLimit <= 0 ? 30 : workspace.AnalysisTaskLimit,
                ChatHistoryLimit = workspace.ChatHistoryLimit < 0 ? 10 : workspace.ChatHistoryLimit,
                ChatHistoryWindow = ClampChatWindow(workspace.ChatHistoryWindow),
                ChatCompressThreshold = ClampCompress(workspace.ChatCompressThreshold),
                LastPrompt = workspace.LastPrompt ?? string.Empty,
                Providers = workspace.Providers.Select(p => new StoredProviderProfile
                {
                    Provider = p.Provider.ToString(),
                    AuthMode = p.AuthMode.ToString(),
                    EncryptedApiKey = Encrypt(SanitizeSecret(p.ApiKey)),
                    Endpoint = p.Endpoint?.Trim() ?? string.Empty,
                    Model = p.Model?.Trim() ?? string.Empty,
                    CliWindowsCommand = p.CliWindowsCommand?.Trim() ?? string.Empty,
                    CliWslCommand = p.CliWslCommand?.Trim() ?? string.Empty,
                    LoginUrl = p.LoginUrl?.Trim() ?? string.Empty,
                    SessionExpirationHours = p.SessionExpirationHours <= 0 ? 24 : p.SessionExpirationHours,
                    TimeoutSeconds = p.TimeoutSeconds <= 0 ? 120 : p.TimeoutSeconds
                }).ToList(),
                ActionTypes = workspace.ActionTypes.Select(a => new StoredActionType
                {
                    Name = a.Name,
                    Prompt = a.Prompt,
                    CreatesTasks = a.CreatesTasks
                }).ToList(),
                SelectedAction = workspace.SelectedAction,
                ActionsSchemaVersion = workspace.ActionsSchemaVersion
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetWorkspaceFile(storageKey), json);
        }

        private static AIWorkspaceSettings FromStored(StoredWorkspace stored)
        {
            var workspace = new AIWorkspaceSettings
            {
                DefaultProvider = ParseProvider(stored.DefaultProvider, AIProvider.CodexCli),
                ScheduleMode = Enum.TryParse<ScheduleCreationMode>(stored.ScheduleMode, out var sm) ? sm : ScheduleCreationMode.DevOps,
                CreateTasks = stored.CreateTasks,
                AnalysisTaskLimit = stored.AnalysisTaskLimit <= 0 ? 30 : stored.AnalysisTaskLimit,
                ChatHistoryLimit = stored.ChatHistoryLimit < 0 ? 10 : stored.ChatHistoryLimit,
                ChatHistoryWindow = ClampChatWindow(stored.ChatHistoryWindow),
                ChatCompressThreshold = ClampCompress(stored.ChatCompressThreshold),
                LastPrompt = stored.LastPrompt ?? string.Empty,
                Providers = (stored.Providers ?? new()).Select(p => new AIProviderProfile
                {
                    Provider = ParseProvider(p.Provider, AIProvider.OpenRouter),
                    AuthMode = Enum.TryParse<AIAuthMode>(p.AuthMode, out var am) ? am : AIAuthMode.ApiKey,
                    ApiKey = Decrypt(p.EncryptedApiKey),
                    Endpoint = p.Endpoint ?? string.Empty,
                    Model = p.Model ?? string.Empty,
                    CliWindowsCommand = p.CliWindowsCommand ?? string.Empty,
                    CliWslCommand = p.CliWslCommand ?? string.Empty,
                    LoginUrl = p.LoginUrl ?? string.Empty,
                    SessionExpirationHours = p.SessionExpirationHours <= 0 ? 24 : p.SessionExpirationHours,
                    TimeoutSeconds = p.TimeoutSeconds <= 0 ? 120 : p.TimeoutSeconds
                }).ToList()
            };

            workspace.ActionTypes = (stored.ActionTypes ?? new()).Select(a => new AIActionType
            {
                Name = a.Name ?? string.Empty,
                Prompt = a.Prompt ?? string.Empty,
                CreatesTasks = a.CreatesTasks
            }).Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
            workspace.SelectedAction = stored.SelectedAction ?? AIActionType.ScheduleDevOpsActionName;
            workspace.ActionsSchemaVersion = stored.ActionsSchemaVersion;

            // Garante que os provedores configuraveis sempre existam.
            foreach (var provider in AIWorkspaceSettings.ConfigurableProviders)
                workspace.GetOrCreate(provider);
            SeedDefaultActions(workspace);

            return workspace;
        }

        private static AIWorkspaceSettings DefaultWorkspace()
        {
            var workspace = new AIWorkspaceSettings
            {
                DefaultProvider = AIProvider.OpenRouter,
                ScheduleMode = ScheduleCreationMode.DevOps
            };
            foreach (var provider in AIWorkspaceSettings.ConfigurableProviders)
                workspace.GetOrCreate(provider);
            SeedDefaultActions(workspace);
            return workspace;
        }

        private static AIWorkspaceSettings? TryLoadLegacy(string storageKey)
        {
            var legacyFile = GetLegacyFile(storageKey);
            if (!File.Exists(legacyFile))
                return null;

            try
            {
                var stored = JsonSerializer.Deserialize<StoredAISettings>(File.ReadAllText(legacyFile));
                if (stored == null)
                    return null;

                var workspace = DefaultWorkspace();
                var openRouter = workspace.GetOrCreate(AIProvider.OpenRouter);
                openRouter.ApiKey = Decrypt(stored.EncryptedApiKey);
                if (!string.IsNullOrWhiteSpace(stored.Endpoint))
                    openRouter.Endpoint = stored.Endpoint;
                if (!string.IsNullOrWhiteSpace(stored.Model))
                    openRouter.Model = stored.Model;
                openRouter.TimeoutSeconds = stored.TimeoutSeconds <= 0 ? 120 : stored.TimeoutSeconds;
                workspace.DefaultProvider = AIProvider.OpenRouter;
                return workspace;
            }
            catch
            {
                return null;
            }
        }

        private static AIProvider ParseProvider(string? value, AIProvider fallback)
            => Enum.TryParse<AIProvider>(value, out var p) ? p : fallback;

        // ── API legada (mantida para compatibilidade) ────────────────────

        /// <summary>Configuracao efetiva do provedor padrao.</summary>
        public static AISettings Load(string storageKey = "NXProject.Community")
            => LoadWorkspace(storageKey).ResolveActiveSettings();

        /// <summary>Atualiza o perfil do provedor informado dentro do workspace.</summary>
        public static void Save(AISettings settings, string storageKey = "NXProject.Community")
        {
            var workspace = LoadWorkspace(storageKey);
            var provider = settings.Provider == AIProvider.None ? workspace.DefaultProvider : settings.Provider;
            var profile = workspace.GetOrCreate(provider);
            profile.AuthMode = settings.AuthMode;
            profile.ApiKey = settings.ApiKey;
            profile.Endpoint = settings.Endpoint;
            profile.Model = settings.Model;
            profile.TimeoutSeconds = settings.TimeoutSeconds;
            SaveWorkspace(workspace, storageKey);
        }

        // Janela de histórico do chat: 2 a 20 mensagens (teto p/ não estourar token); padrão 8.
        public const int ChatHistoryWindowDefault = 8;
        public const int ChatHistoryWindowMax = 20;
        private static int ClampChatWindow(int n)
            => n <= 0 ? ChatHistoryWindowDefault : Math.Clamp(n, 2, ChatHistoryWindowMax);

        // "Compress" do histórico: 0 = desligado; senão dispara entre 20 mil e 500 mil caracteres.
        public const int ChatCompressDefault = 350_000;
        public const int ChatCompressMax = 500_000;
        private static int ClampCompress(int n)
            => n <= 0 ? 0 : Math.Clamp(n, 20_000, ChatCompressMax);

        private static string GetSettingsDirectory(string storageKey)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                string.IsNullOrWhiteSpace(storageKey) ? "NXProject.Community" : storageKey.Trim());
        }

        private static string GetWorkspaceFile(string storageKey)
            => Path.Combine(GetSettingsDirectory(storageKey), "ai-workspace.json");

        private static string GetLegacyFile(string storageKey)
            => Path.Combine(GetSettingsDirectory(storageKey), "ai-settings.json");

        public static string SanitizeSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            var builder = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '"' || c == '\'' || c == '`')
                    continue;

                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\uFEFF')
                    continue;

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>Cifra um segredo com DPAPI do usuário atual (mesma proteção do PAT/chave de IA).</summary>
        public static string EncryptSecret(string value) => Encrypt(value);

        /// <summary>Decifra um segredo gravado por <see cref="EncryptSecret"/>.</summary>
        public static string DecryptSecret(string encryptedValue) => Decrypt(encryptedValue);

        private static string Encrypt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectForCurrentUser(bytes);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string Decrypt(string encryptedValue)
        {
            if (string.IsNullOrWhiteSpace(encryptedValue))
                return string.Empty;

            try
            {
                var protectedBytes = Convert.FromBase64String(encryptedValue);
                var bytes = UnprotectForCurrentUser(protectedBytes);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] ProtectForCurrentUser(byte[] plainBytes)
        {
            var input = CreateBlob(plainBytes);
            DATA_BLOB output = default;

            try
            {
                if (!CryptProtectData(ref input, "NXProject.Community.AI", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    throw new InvalidOperationException("Nao foi possivel criptografar o token localmente.");

                return CopyBlob(output);
            }
            finally
            {
                FreeBlob(input);
                FreeProtectedBlob(output);
            }
        }

        private static byte[] UnprotectForCurrentUser(byte[] protectedBytes)
        {
            var input = CreateBlob(protectedBytes);
            DATA_BLOB output = default;

            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                    throw new InvalidOperationException("Nao foi possivel descriptografar o token localmente.");

                return CopyBlob(output);
            }
            finally
            {
                FreeBlob(input);
                FreeProtectedBlob(output);
            }
        }

        private static DATA_BLOB CreateBlob(byte[] bytes)
        {
            var blob = new DATA_BLOB
            {
                cbData = bytes.Length,
                pbData = Marshal.AllocHGlobal(bytes.Length)
            };

            Marshal.Copy(bytes, 0, blob.pbData, bytes.Length);
            return blob;
        }

        private static byte[] CopyBlob(DATA_BLOB blob)
        {
            if (blob.pbData == IntPtr.Zero || blob.cbData <= 0)
                return Array.Empty<byte>();

            var bytes = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
            return bytes;
        }

        private static void FreeBlob(DATA_BLOB blob)
        {
            if (blob.pbData != IntPtr.Zero)
                Marshal.FreeHGlobal(blob.pbData);
        }

        private static void FreeProtectedBlob(DATA_BLOB blob)
        {
            if (blob.pbData != IntPtr.Zero)
                LocalFree(blob.pbData);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
