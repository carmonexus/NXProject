using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using NXProject.Services;

namespace NXProject.Models
{
    public class ProjectTask
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Level { get; set; } = 0;
        public bool IsSummary { get; set; } = false;
        public bool IsMilestone { get; set; } = false;

        public DateTime Start { get; set; } = DateTime.Today;
        public DateTime Finish { get; set; } = DateTime.Today.AddDays(1);
        public TimeSpan Duration => Finish - Start;
        public double DurationHours => ProjectCalendarService.CountWorkingHours(Start, Finish);

        public double PercentComplete { get; set; } = 0;

        public string? Notes { get; set; }

        public double? EstimatedHours { get; set; }

        // Estimativa original: gravada automaticamente quando % = 0 e horas são definidas.
        // Nunca sobrescrita após % > 0 — serve como baseline de comparação.
        public double? OriginalEstimatedHours { get; set; }

        // Horas atuais acumuladas (HH Atual). Somadas a EstimatedHours formam a duração total.
        public double? CurrentHours { get; set; }

        // Tipo de Centro de Custo para itens EPIC: "CAPEX", "OPEX", "DEFINIDO_NO_PROJETO".
        // Nulo ou "DEFINIDO_NO_PROJETO" = herda configuração do projeto; "CAPEX"/"OPEX" = sobrescreve.
        public string? TipoCentroCusto { get; set; }

        // Quando true, o Gantt usa OriginalEstimatedHours como duração em vez de EstimatedHours.
        // Não editável se PercentComplete > 0.
        public bool UseOriginalHoursView { get; set; }

        public double? SfpPoints { get; set; }

        // ── Vínculo com TFS / Azure DevOps ──────────────────────────────────
        // Id do work item no DevOps (distinto do Id interno, que é sequencial).
        // 0 = marcado para criar no DevOps na próxima sincronização (legado; preferir IsPendingTfsCreate).
        public int? TfsId { get; set; }

        // true = task criada localmente, ainda não existe no DevOps (pendente de criação na próxima sync).
        // Substitui a convenção TfsId == 0, que agora coexiste para compatibilidade.
        public bool IsPendingTfsCreate { get; set; } = false;

        // true = task tem vínculo real com o DevOps (TfsId positivo e não pendente de criação).
        // IDs negativos são internos de atividades No DevOps.
        public bool HasTfsLink => TfsId.HasValue && TfsId.Value > 0 && !IsPendingTfsCreate;
        // Id do work item PAI no DevOps (parent hierárquico) na época da importação,
        // usado para detectar reparenting e atualizar o link na sincronização.
        public int? TfsParentId { get; set; }
        // Tipo no DevOps: Project, Epic, Feature, Story.
        public string? TfsType { get; set; }
        // Valor do campo de classificação primário (compat). Para múltiplos campos, use CustomDevopsFieldValues.
        public string? TfsClassification { get; set; }
        // Valores dos campos de classificação por referência de campo (ex: {"Custom.Type":"Architecture"}).
        public Dictionary<string, string> CustomDevopsFieldValues { get; set; } = [];
        // Estado no DevOps: New, Active, Block, Closed, Removed, Resolved.
        public string? TfsState { get; set; }
        // Descrição (System.Description) importada do DevOps, usada na sincronização.
        public string? Description { get; set; }
        // Tags do DevOps (System.Tags), separadas por "; " (ex.: "Block"). Lidas no
        // import e sincronizadas de volta se mudarem.
        public string? Tags { get; set; }
        // Bloqueio DERIVADO de Tasks filhas com tag Block. SÓ visão no NXProject —
        // nunca é sincronizado de volta (distinto de Tags).
        public bool BlockedByChild { get; set; }
        // Ordem no backlog do DevOps (Microsoft.VSTS.Common.StackRank): menor = mais
        // acima. Importado, usado para ordenar irmãos e sincronizado se a ordem mudar.
        public double? TfsStackRank { get; set; }
        // Caminho da sprint no DevOps (System.IterationPath), ex.: "Proj\\Sprint 5".
        // Lido no import; se alterado no NXProject, sincronizado de volta.
        public string? TfsIterationPath { get; set; }

        // Recursos alocados nesta tarefa
        public List<TaskResource> Resources { get; set; } = new();

        // Predecessoras: lista de IDs de tarefas
        public List<int> PredecessorIds { get; set; } = new();

        // Data de início fixada manualmente (não recalculada pelo cronograma).
        // true  → envia Data_Inicio ao TFS na sincronização.
        // false → limpa Data_Inicio no TFS (se tarefa não estiver Closed).
        public bool StartFixed { get; set; } = false;

        // Prioridade da Task no DevOps (Microsoft.VSTS.Common.Priority). Usado para
        // ordenar Tasks dentro da Story e calcular datas sequenciais.
        public int? Priority { get; set; }

        // true quando as Tasks filhas foram suprimidas do cronograma pelo usuário
        // (existem no DevOps mas não estão nos Children). Controla o menu "Expandir Tasks".
        public bool TasksSuppressed { get; set; }

        // Fim calculado com base em HH + % alocação, ignorando a data fixada.
        // Preenchido apenas quando StartFixed = true e o fim calculado difere do Finish.
        // Usado pelo Gantt para colorir a barra de vermelho e exibir hint.
        public DateTime? CalculatedFinish { get; set; }

        // Data de fim fixada (prazo comprometido). true → envia Data_Fim ao TFS.
        public bool FinishFixed { get; set; } = false;

        // ── Baseline ─────────────────────────────────────────────────────────
        // Gravado externamente no .nxb; aplicado em memória para exibição no Gantt.
        public DateTime? BaselineStart  { get; set; }
        public DateTime? BaselineFinish { get; set; }
        public double?   BaselineHours  { get; set; }

        // Controle de concorrência de sincronização TFS.
        // SyncVersion: valor lido no último import (null = nunca importado).
        // HasSyncConflict: true quando outro usuário gravou após o nosso último import.
        public int? SyncVersion { get; set; }
        public bool HasSyncConflict { get; set; }
        // Predecessora com TfsId inexistente/inacessível no DevOps — link quebrado.
        public bool HasBrokenPredecessorLink { get; set; }

        // Quantidade de Tasks filhas no DevOps (null = não calculado, 0 = sem tasks → vermelho).
        public int? DevopsTaskCount { get; set; }

        /// <summary>
        /// Tipo do EPIC vindo do DevOps (campo EPIC_TYPE): "DELIVERY" (padrão) ou "BACKLOG".
        /// EPIC de BACKLOG não soma horas no total do projeto. Vazio = DELIVERY.
        /// </summary>
        public string? EpicType { get; set; }

        // Resumo das Tasks filhas no DevOps por recurso (dono + horas), gravado no Sync e no
        // import do Mapa de Alocação. Permite a alocação decompor o HH da Story entre as pessoas
        // das tasks sem carregar as tasks inteiras no cronograma. Horas: Closed → Completed;
        // senão → Estimate. Lista vazia = sem tasks (ou arquivo/versão antiga).
        public List<TaskAllocationSummary> TaskAllocations { get; set; } = new();

        // Justificativa de atraso ou observação relevante. Persiste na description
        // do DevOps como "Justificativa: <texto>." e é lida de volta no import.
        public string? Justificativa { get; set; }

        // Origem: Task criada pela Planilha de Plan Task (para, após a sincronização,
        // atualizar o ID interno (:I) para o ID DevOps (:T) na planilha de origem).
        // SourcePlanPath = caminho do .xlsx; SourcePlanRowKey = ID interno gravado na
        // planilha no momento da aplicação (ex.: "115:I").
        public string? SourcePlanPath { get; set; }
        public string? SourcePlanRowKey { get; set; }

        // Observação da planilha de Task Plan: registrada como tramite (comentário)
        // no DevOps quando a Task ganha o ID do TFS na sincronização.
        public string? PlanObservation { get; set; }

        // Subtarefas
        public ObservableCollection<ProjectTask> Children { get; set; } = new();

        // Referência ao pai
        public ProjectTask? Parent { get; set; }

        public int SprintNumber { get; set; } = 0;

        // Recalcula datas de tarefas de resumo com base nos filhos
        public void RecalcSummary()
        {
            if (!IsSummary || Children.Count == 0) return;

            var minStart = DateTime.MaxValue;
            var maxFinish = DateTime.MinValue;

            foreach (var child in Children)
            {
                child.RecalcSummary();
                if (child.Start < minStart) minStart = child.Start;
                if (child.Finish > maxFinish) maxFinish = child.Finish;
            }

            Start = minStart;
            Finish = maxFinish;
            RecalcPercentComplete();
        }

        private void RecalcPercentComplete()
        {
            if (!IsSummary || Children.Count == 0)
                return;

            double totalHoursAll   = 0.0;
            double currentHoursAll = 0.0;
            CollectLeafHours(this, ref currentHoursAll, ref totalHoursAll);

            if (totalHoursAll > 0)
            {
                PercentComplete = currentHoursAll / totalHoursAll * 100.0;
            }
            else
            {
                // Fallback: média ponderada do PercentComplete armazenado
                double totalWeight    = 0.0;
                double weightedPercent = 0.0;
                foreach (var child in Children)
                {
                    var weight = child.IsSummary
                        ? Math.Max(1.0, SumDescendantHours(child))
                        : Math.Max(1.0, TaskScheduleService.GetEffectiveDurationHours(child));
                    weightedPercent += child.PercentComplete * weight;
                    totalWeight     += weight;
                }
                PercentComplete = totalWeight > 0
                    ? weightedPercent / totalWeight
                    : Children.Average(c => c.PercentComplete);
            }
        }

        // Soma recursiva dos HH Atual e HH Total de todas as folhas abaixo de 'task'.
        // Quando HH Atual está disponível, o % real é derivado das horas (cur / total).
        // Quando não há HH Atual, usa PercentComplete armazenado ponderado pelo peso planejado.
        private static void CollectLeafHours(ProjectTask task, ref double currentSum, ref double totalSum)
        {
            if (!task.IsSummary || task.Children.Count == 0)
            {
                var cur = task.CurrentHours ?? 0;
                var est = task.EstimatedHours ?? 0;

                if (cur > 0)
                {
                    // Temos HH Atual: % real = HH Atual / (HH Atual + HH Restante)
                    var total = cur + est;
                    currentSum += cur;
                    totalSum   += total;
                }
                else
                {
                    // Sem HH Atual: usa % armazenado × peso (OrgH > HH Estimado > calendário)
                    double weight = task.OriginalEstimatedHours is > 0
                        ? task.OriginalEstimatedHours.Value
                        : est > 0
                            ? est
                            : Math.Max(1.0, ProjectCalendarService.CountWorkingHours(task.Start, task.Finish));
                    currentSum += task.PercentComplete / 100.0 * weight;
                    totalSum   += weight;
                }
                return;
            }
            foreach (var child in task.Children)
                CollectLeafHours(child, ref currentSum, ref totalSum);
        }

        private static double SumDescendantHours(ProjectTask task)
        {
            if (!task.IsSummary || task.Children.Count == 0)
            {
                // Prioridade: HH Original (não muda com progresso) → HH Atual+Restante → calendário
                if (task.OriginalEstimatedHours is > 0)
                    return task.OriginalEstimatedHours.Value;
                var cur = task.CurrentHours ?? 0;
                var est = task.EstimatedHours ?? 0;
                if (cur > 0 || est > 0)
                    return cur + est;
                var cal = ProjectCalendarService.CountWorkingHours(task.Start, task.Finish);
                return cal > 0 ? cal : 1.0;
            }
            return task.Children.Sum(SumDescendantHours);
        }

        public override string ToString() => $"{Id} - {Name}";
    }

    public enum DependencyType
    {
        FinishToStart,
        StartToStart,
        FinishToFinish,
        StartToFinish
    }

    public class TaskDependency
    {
        public int PredecessorId { get; set; }
        public int SuccessorId { get; set; }
        public DependencyType Type { get; set; } = DependencyType.FinishToStart;
        public int LagDays { get; set; } = 0;
    }

    /// <summary>Resumo de uma Task filha (no DevOps) por recurso: quem a executa e quantas horas.
    /// Usado pela decomposição de alocação sem precisar carregar as Tasks no cronograma.</summary>
    /// <summary>Valores do campo EPIC_TYPE importado do DevOps.</summary>
    public static class EpicTypes
    {
        /// <summary>EPIC de entrega — comportamento padrão, soma horas no total do projeto.</summary>
        public const string Delivery = "DELIVERY";

        /// <summary>EPIC de backlog — NÃO soma horas no total do projeto.</summary>
        public const string Backlog  = "BACKLOG";

        public static bool IsBacklog(string? epicType)
            => string.Equals(epicType?.Trim(), Backlog, StringComparison.OrdinalIgnoreCase);
    }

    public class TaskAllocationSummary
    {
        public string Resource { get; set; } = string.Empty;
        public double Hours { get; set; }
        /// <summary>Quantidade de tasks deste recurso que compõem as horas.</summary>
        public int Tasks { get; set; }
        /// <summary>Estado agregado das tasks: "Active", "Closed", "New" ou "Other".
        /// Vazio = arquivo legado (sem estado gravado).</summary>
        public string State { get; set; } = string.Empty;
    }
}
