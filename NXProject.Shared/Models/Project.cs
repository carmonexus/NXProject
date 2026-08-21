using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace NXProject.Models
{
    public class GapJustification
    {
        public int ResourceId { get; set; }
        public DateTime GapStart { get; set; }
        public DateTime GapEnd { get; set; }
        public string Justification { get; set; } = string.Empty;
    }

    public class SprintSettingsProfile
    {
        public int SprintDurationDays { get; set; } = 14;
        public int FirstSprintNumber { get; set; } = 1;
        public string SprintNumberingMode { get; set; } = "Sequencial";
        public double LowDaysPerSfp { get; set; } = 1.0;
        public double MediumDaysPerSfp { get; set; } = 1.0;
        public double HighDaysPerSfp { get; set; } = 1.0;
        public double CriticalPathRiskSlackDays { get; set; } = 2.0;
        public double CriticalPathCriticalSlackDays { get; set; } = 1.0;
    }

    public class Project
    {
        public string Name { get; set; } = "Novo Projeto";
        public string? Description { get; set; }
        public string? Author { get; set; }

        public DateTime StartDate { get; set; } = DateTime.Today;

        // Duração do sprint em dias úteis
        public int SprintDurationDays { get; set; } = 14;

        // Número da primeira sprint exibida no cronograma
        public int FirstSprintNumber { get; set; } = 1;

        // Modo de numeracao das sprints: Sequencial, Par ou Impar
        public string SprintNumberingMode { get; set; } = "Sequencial";

        // Último nível de zoom selecionado pelo usuário
        public string LastZoom { get; set; } = "Mês";

        // Vínculo com o Azure DevOps: nome, ID e owner (AssignedTo) do work item raiz importado
        public string? DevOpsProjectName { get; set; }
        public int DevOpsRootWorkItemId { get; set; }
        public string? DevOpsProjectOwner { get; set; }

        // Campos do work item raiz (Project) usados no apontamento de horas (time sheet):
        // elemento PEP e o nome do projeto no PEP (Projeto Capex).
        public string? PepElement { get; set; }
        public string? PepProjectName { get; set; }

        // Organização + Team Project (API) de onde este cronograma foi importado.
        // A sincronização usa ESTES valores (o projeto aberto), não a config global,
        // para não sincronizar no projeto errado ao alternar entre cronogramas.
        public string? DevOpsOrganizationUrl { get; set; }
        public string? DevOpsTeamProject { get; set; }

        // Processo do Team Project no DevOps (Agile, Scrum, CMMI, Basic), lido da API na
        // importação. Determina o campo de ordem do backlog (Agile/CMMI = StackRank; Scrum =
        // BacklogPriority) e é exibido no banner e na configuração do portfólio.
        public string? DevOpsProcess { get; set; }

        // Somente leitura: cronograma importado de um projeto DevOps marcado como "somente
        // leitura" no Portfólio. Bloqueia o Export/Sincronizar no DevOps (edição local e Task
        // Plan continuam livres). Liberação só na configuração do Projeto no Portfólio.
        public bool ReadOnly { get; set; }

        // Grupo administrador do NX (campo Adm_NX do work item Project no DevOps): nome do grupo
        // cujos membros podem sincronizar. Vazio/ausente = liberado para todos. Lido na importação.
        public string? AdmGroupName { get; set; }

        // Membros resolvidos do grupo administrador (e-mails e nomes de exibição, minúsculos),
        // usados para validar quem pode acionar o Sincronizar. Resolvido na importação.
        public List<string> AdmGroupMembers { get; set; } = new();

        // % de Supervisão de Story: no rateio de HH da Story (Task Plan), quando o Owner da
        // Story NÃO tem Task própria, reserva este % para acompanhamento e distribui o restante
        // entre as Tasks sem HH. Configurável na tela de Alocação de Recursos. Padrão 20%.
        public double StorySupervisionPercent { get; set; } = 20;

        // Planilha de Plan Task associada a este cronograma (último .xlsx usado). O log de
        // atualização de IDs pós-sincronização fica na pasta desse arquivo.
        public string? PlanSheetPath { get; set; }

        // Exibe coluna "OrgH" (Estimativa Original) no cronograma
        public bool ShowOriginalHoursColumn { get; set; } = false;

        // Nomes das colunas ocultas pelo usuário (ex: "SFP,Pred.,T·E")
        public string HiddenColumns { get; set; } = "";
        public string HiddenColumnsExpanded { get; set; } = "";

        // Quantos dias de duracao equivalem a 1 SFP em cada faixa
        public double LowDaysPerSfp { get; set; } = 1.0;
        public double MediumDaysPerSfp { get; set; } = 1.0;
        public double HighDaysPerSfp { get; set; } = 1.0;

        // Sprints reais lidas do DevOps (nome + janela), numeradas de 1 em diante.
        // Vazio em projetos sem vinculo TFS (cai na numeracao sintetica do Gantt).
        public ObservableCollection<Sprint> Sprints { get; set; } = new();

        // Tarefas raiz (hierarquia)
        public ObservableCollection<ProjectTask> Tasks { get; set; } = new();

        // Recursos do projeto
        public ObservableCollection<Resource> Resources { get; set; } = new();

        /// <summary>
        /// Calendário embutido no cronograma (dias úteis, feriados, horas/dia).
        /// Quando presente, é usado no lugar do calendário Geral (da máquina) e
        /// viaja junto no arquivo .nxp. NUNCA é sincronizado com o TFS/DevOps.
        /// Null = usa o calendário Geral da máquina.
        /// </summary>
        public ProjectCalendar? Calendar { get; set; }

        // Justificativas de gaps no cronograma de recursos
        public List<GapJustification> GapJustifications { get; set; } = new();

        // Paleta de cores por nível de hierarquia (degradê)
        public bool UseHierarchyColors { get; set; } = false;
        // Cores em hex por profundidade: [0]=raiz, [1]=Epic, [2]=Feature, [3]=Story, [4+]=Task
        public List<string> HierarchyLevelColors { get; set; } =
        [
            "#D8D8D8",  // depth 0 — raiz/projeto
            "#E3E3E3",  // depth 1 — Epic
            "#EEEEEE",  // depth 2 — Feature
            "#F5F5F5",  // depth 3 — Story
            "#FAFAFA",  // depth 4+ — Task
        ];

        // Caminho do arquivo salvo
        public string? FilePath { get; set; }

        // Baseline ativo = barras cinzas aparecem no Gantt. false = baseline desativado visualmente (mas .nxb existe).
        public bool BaselineActive { get; set; } = true;

        // Preferências do Diagrama de Atividades: larguras por nível e nível de expansão
        public string DiagramLevelWidths    { get; set; } = "";   // CSV: "160,160,160,..."
        public string DiagramExpandedLevels { get; set; } = "";   // CSV: "0,1"

        // Caminho Crítico: exibe borda vermelha nas tarefas críticas no Gantt
        public bool ShowCriticalPath { get; set; } = false;

        // Folga negociada (em dias) para sinalizar tarefas entrando no caminho crítico.
        public double CriticalPathRiskSlackDays { get; set; } = 2.0;
        public double CriticalPathCriticalSlackDays { get; set; } = 1.0;

        public bool IsDirty { get; set; } = false;

        public SprintSettingsProfile GetSprintSettingsProfile()
        {
            return new SprintSettingsProfile
            {
                SprintDurationDays = SprintDurationDays,
                FirstSprintNumber = FirstSprintNumber,
                SprintNumberingMode = SprintNumberingMode,
                LowDaysPerSfp = LowDaysPerSfp,
                MediumDaysPerSfp = MediumDaysPerSfp,
                HighDaysPerSfp = HighDaysPerSfp,
                CriticalPathRiskSlackDays = CriticalPathRiskSlackDays,
                CriticalPathCriticalSlackDays = CriticalPathCriticalSlackDays
            };
        }

        public void ApplySprintSettingsProfile(SprintSettingsProfile? profile)
        {
            if (profile == null)
                return;

            SprintDurationDays = Math.Max(1, profile.SprintDurationDays);
            FirstSprintNumber = Math.Max(1, profile.FirstSprintNumber);
            SprintNumberingMode = string.IsNullOrWhiteSpace(profile.SprintNumberingMode)
                ? "Sequencial"
                : profile.SprintNumberingMode.Trim();
            LowDaysPerSfp = Math.Max(0, profile.LowDaysPerSfp);
            MediumDaysPerSfp = Math.Max(0, profile.MediumDaysPerSfp);
            HighDaysPerSfp = Math.Max(0, profile.HighDaysPerSfp);
            CriticalPathCriticalSlackDays = Math.Max(0, profile.CriticalPathCriticalSlackDays);
            CriticalPathRiskSlackDays = Math.Max(
                CriticalPathCriticalSlackDays,
                Math.Max(0, profile.CriticalPathRiskSlackDays));
        }
    }
}
