using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class FeaturesHelpWindow : Window
    {
        private readonly List<(string Title, string Subtitle, List<(string Head, string Body)> Sections, string? Tip)> _topics;

        public FeaturesHelpWindow()
        {
            InitializeComponent();
            _topics = LanguageService.CurrentLanguage == "en-US" ? BuildTopicsEn() : BuildTopics();
            TopicList.SelectedIndex = 0;
        }

        private void OnTopicChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TopicList.SelectedIndex < 0 || TopicList.SelectedIndex >= _topics.Count) return;
            RenderTopic(_topics[TopicList.SelectedIndex]);
        }

        // ── Busca por palavra/texto nos tópicos ───────────────────────────
        private string _searchTerm = string.Empty;

        private static int IndexOfTerm(string text, string term, int start = 0)
            => System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                text, term, start,
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);

        private static bool ContainsTerm(string? text, string term)
            => !string.IsNullOrEmpty(text) && IndexOfTerm(text!, term) >= 0;

        private static bool TopicMatches(
            (string Title, string Subtitle, List<(string Head, string Body)> Sections, string? Tip) topic, string term)
            => ContainsTerm(topic.Title, term)
               || ContainsTerm(topic.Subtitle, term)
               || ContainsTerm(topic.Tip, term)
               || topic.Sections.Exists(s => ContainsTerm(s.Head, term) || ContainsTerm(s.Body, term));

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_topics == null || TopicList == null) return;
            _searchTerm = SearchBox.Text?.Trim() ?? string.Empty;

            int first = -1;
            for (int i = 0; i < TopicList.Items.Count && i < _topics.Count; i++)
            {
                bool match = _searchTerm.Length == 0 || TopicMatches(_topics[i], _searchTerm);
                if (TopicList.Items[i] is ListBoxItem item)
                    item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match && first < 0) first = i;
            }

            if (first < 0)
            {
                TopicList.SelectedIndex = -1;
                ContentPanel.Children.Clear();
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = AppStrings.Get("Help_SearchNoResult"),
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 60, 60)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            // Mantém o tópico atual se ele continua visível; senão vai para o primeiro que casa.
            var current = TopicList.SelectedIndex;
            bool currentVisible = current >= 0 && current < TopicList.Items.Count
                && TopicList.Items[current] is ListBoxItem cur && cur.Visibility == Visibility.Visible;
            if (!currentVisible)
                TopicList.SelectedIndex = first;
            else
                RenderTopic(_topics[current]); // re-renderiza para atualizar o destaque
        }

        /// <summary>Preenche o TextBlock destacando as ocorrências do termo pesquisado.</summary>
        private void SetHighlightedText(TextBlock tb, string text)
        {
            if (_searchTerm.Length == 0 || string.IsNullOrEmpty(text))
            {
                tb.Text = text;
                return;
            }

            var highlight = new SolidColorBrush(Color.FromRgb(255, 235, 130));
            int pos = 0;
            while (pos <= text.Length)
            {
                var i = IndexOfTerm(text, _searchTerm, pos);
                if (i < 0)
                {
                    tb.Inlines.Add(new Run(text[pos..]));
                    break;
                }
                if (i > pos) tb.Inlines.Add(new Run(text[pos..i]));
                var len = Math.Min(_searchTerm.Length, text.Length - i);
                tb.Inlines.Add(new Run(text.Substring(i, len))
                {
                    Background = highlight,
                    FontWeight = FontWeights.SemiBold
                });
                pos = i + len;
            }
        }

        private void RenderTopic((string Title, string Subtitle, List<(string Head, string Body)> Sections, string? Tip) topic)
        {
            ContentPanel.Children.Clear();

            // Título
            var titleTb = new TextBlock
            {
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            SetHighlightedText(titleTb, topic.Title);
            ContentPanel.Children.Add(titleTb);

            // Subtítulo
            if (!string.IsNullOrWhiteSpace(topic.Subtitle))
            {
                var subTb = new TextBlock
                {
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 90, 110)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 18)
                };
                SetHighlightedText(subTb, topic.Subtitle);
                ContentPanel.Children.Add(subTb);
            }

            // Seções
            foreach (var (head, body) in topic.Sections)
            {
                var headTb = new TextBlock
                {
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(43, 87, 154)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 14, 0, 6)
                };
                SetHighlightedText(headTb, head);
                ContentPanel.Children.Add(headTb);

                // Corpo: linhas iniciadas com "• " viram bullets; demais são parágrafo normal
                foreach (var line in body.Split('\n'))
                {
                    var trimmed = line.TrimStart();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    bool isBullet = trimmed.StartsWith("•");
                    var tb = new TextBlock
                    {
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(40, 45, 55)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = isBullet
                            ? new Thickness(16, 2, 0, 2)
                            : new Thickness(0, 2, 0, 4)
                    };
                    SetHighlightedText(tb, trimmed);
                    ContentPanel.Children.Add(tb);
                }
            }

            // Dica final
            if (!string.IsNullOrWhiteSpace(topic.Tip))
            {
                var tipBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(255, 247, 232)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(232, 211, 154)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 20, 0, 0)
                };
                var tipTb = new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(60, 50, 20))
                };
                SetHighlightedText(tipTb, "💡 " + topic.Tip);
                tipBorder.Child = tipTb;
                ContentPanel.Children.Add(tipBorder);
            }
        }

        private static List<(string, string, List<(string, string)>, string?)> BuildTopics() => new()
        {
            (
                "Visão Geral",

                "O NXProject é um gerenciador de projetos de TI que une o rigor técnico do Azure DevOps com a visão de cronograma que gestores e líderes precisam para tomar decisões.",
                new()
                {
                    ("Filosofia de planejamento",
                     "O NXProject planeja até o nível de Story, permitindo que o Desenvolvedor tenha liberdade para detalhar e criar as tarefas durante a execução.\n\n" +
                     "Inspirado no conceito matemático de grau de liberdade — utilizado para modelar sistemas complexos — o NXProject aplica o mesmo princípio ao planejamento: estrutura a complexidade da tecnologia sem engessar o processo de desenvolvimento.\n\n" +
                     "Assim como em um sistema físico onde os graus de liberdade definem o espaço de movimento possível, o NXProject define os limites (datas, recursos, dependências) e preserva o espaço necessário para que o time técnico navegue com autonomia dentro deles."),
                    ("O que o NXProject faz",
                     "O NXProject importa a hierarquia do Azure DevOps (Project → Epic → Feature → Story) e transforma esses dados em um cronograma com datas, dependências, alocação de recursos e Gantt.\n" +
                     "A equipe técnica continua no Azure DevOps como sempre. O NXProject é uma camada de leitura e planejamento sobre esses dados.\n" +
                     "O objetivo da Nexus Xdata é transparência: deixar claro por que cada data, duração, percentual e alerta aparece no cronograma."),
                    ("Quem usa e para quê",
                     "• Gerente de Projeto: cronograma integrado ao backlog, alertas de atraso, visão de dependências.\n" +
                     "• Scrum Master / RTE: capacidade por sprint, conflito de alocação, impacto de mudanças de data.\n" +
                     "• Tech Lead: visão de Features e Stories com predecessoras e estimativas em horas.\n" +
                     "• PMO: exportação para MS Project / Excel, visão consolidada do projeto."),
                    ("Arquivo de projeto (.nxp)",
                     "O cronograma é salvo em um arquivo .nxp que pode ser compartilhado. Ele armazena todas as tarefas, datas, dependências, recursos, configurações de sprint e o vínculo com o Azure DevOps.")
                },
                "Use Arquivo → Importar → TFS / Azure DevOps para criar o cronograma a partir do seu backlog existente."
            ),
            // ── Tópico 1: Project / Epic / Feature / Story / Task ────────────
            (
                "Project / Epic / Feature / Story / Task",
                "Entenda o papel de cada nível da hierarquia do Azure DevOps no NXProject e as regras que governam campos, datas e sincronização.",
                new()
                {
                    ("Project (item raiz)",
                     "O Project é o item raiz do cronograma: o work item que agrupa todos os Epics do projeto.\n\n" +
                     "ATENÇÃO: 'Project' NÃO é um tipo de work item padrão do Azure DevOps — o padrão vai só até o Epic. É um tipo PERSONALIZADO que a organização cria no processo para servir de container acima dos Epics.\n\n" +
                     "No NXProject:\n" +
                     "• Não é uma linha do cronograma nem uma barra do Gantt — ele É o projeto aberto.\n" +
                     "• O título do item raiz vira o nome do projeto no NXProject.\n" +
                     "• A data de início do projeto é lida do campo Data_Inicio do item raiz (quando não há sprint para ancorar).\n" +
                     "• O responsável (Assigned To) do item raiz vira o dono do projeto.\n" +
                     "• Os filhos do item raiz são importados seguindo Epic → Feature → Story → Task.\n\n" +
                     "Campos: crie no tipo 'Project' os MESMOS campos personalizados do Epic (HH Estimado, Data_Inicio, Data_Fim, Sync_version, Sync_Name) — na prática ele costuma ser uma cópia do Epic.\n\n" +
                     "Na importação: informe o ID do item raiz. O tipo dele não precisa ser exatamente 'Project' — pode ser qualquer work item pai dos Epics (até um Epic, se quiser importar só ele). Já o Discovery (Portfólio → Discovery DevOps) procura automaticamente work items do tipo 'Project', então para ele funcionar o tipo personalizado precisa existir."),
                    ("Epic",
                     "O Epic representa uma grande iniciativa ou objetivo estratégico, geralmente com duração de meses.\n\n" +
                     "No NXProject:\n" +
                     "• É um agrupador de Features — suas datas são calculadas a partir das datas das Features filhas.\n" +
                     "• Não possui HH Estimado próprio; a duração é derivada da soma dos filhos.\n" +
                     "• Pode ter predecessoras para sequenciar grandes blocos de trabalho.\n" +
                     "• Sincroniza com o DevOps os campos: State, título e datas (se configurados).\n" +
                     "• Aparece na barra do Gantt como agrupador (cor cinza-azulada)."),
                    ("Feature",
                     "A Feature representa uma capacidade de negócio entregável, normalmente agrupando várias Stories.\n\n" +
                     "No NXProject:\n" +
                     "• É um agrupador de Stories — datas e percentual de conclusão calculados pelos filhos.\n" +
                     "• Pode ter predecessoras entre Features (dependências de entrega).\n" +
                     "• HH Estimado: calculado como soma dos HH das Stories filhas.\n" +
                     "• Alerta de sprint é exibido quando a Feature cruza mais de uma sprint sem estar concluída.\n" +
                     "• Sincroniza State e datas com o DevOps."),
                    ("Story (User Story / PBI)",
                     "A Story é a unidade central de planejamento do NXProject. Representa uma entrega de valor ao usuário.\n\n" +
                     "No NXProject:\n" +
                     "• Possui HH Estimado, Data Início, Data Fim, Sprint e Recurso alocado.\n" +
                     "• Datas são calculadas pela fila do recurso e pela duração em HH.\n" +
                     "• Percentual de conclusão (%) vem do campo configurado no DevOps (ex: Perc_Conclusao).\n" +
                     "• Block: se a Story tem a tag 'Block' no DevOps, é exibida com ícone ⛔ no cronograma.\n" +
                     "• Tasks filhas: podem ser buscadas/expandidas no cronograma via menu de contexto.\n" +
                     "• Sincroniza: HH Estimado, datas, state, % conclusão, alocação e predecessoras.\n" +
                     "• Ao exportar (Sincronizar), o NXProject atualiza apenas campos alterados localmente."),
                    ("Task",
                     "A Task representa uma atividade técnica dentro de uma Story, executada por um desenvolvedor.\n\n" +
                     "No NXProject:\n" +
                     "• Campos principais: HH Estimado (Original Estimate), HH Atual (Completed Work), Prioridade, Responsável, State e Categoria (Activity).\n" +
                     "• HH Estimado = 0 e HH Atual = 0: a Task recebe rateio proporcional da duração da Story ao ser incluída no cronograma.\n" +
                     "• HH Estimado = 0 e HH Atual > 0: o HH Atual é usado como duração estimada para cálculo.\n" +
                     "• Prioridade define a ordem de execução dentro da Story; pode ser editada no cronograma ou na Grid de Tasks.\n" +
                     "• State 'Closed' com 100% = Task encerrada.\n" +
                     "• Block: menu de contexto na Task permite marcar/retirar Block — altera o campo BlockedByChild da Story pai.\n" +
                     "• Grid de Tasks: acessível pelo menu de contexto da Story → 'Grid de Tasks (DevOps)'. Permite editar, ratear HH, reordenar por drag-drop e sincronizar com o DevOps.\n" +
                     "• Sincroniza: Title, Original Estimate, Completed Work, Priority, AssignedTo, State e Activity.")
                },
                "A hierarquia Project → Epic → Feature → Story → Task espelha o backlog do Azure DevOps (sendo 'Project' um tipo personalizado no topo). O NXProject planeja até a Story e oferece visibilidade das Tasks sem engessá-las."
            ),
            // ── Tópico 2: Tech Lead ──────────────────────────────────────────
            (
                "Tech Lead",
                "A janela Tech Lead é o ponto de controle de Tasks técnicas: busca as Tasks de cada Story diretamente no Azure DevOps, permite criar, editar e sincronizar Tasks sem sair do NXProject.",
                new()
                {
                    ("Abrir pelo botão da toolbar",
                     "Clique no ícone 👷 Tech Lead na toolbar para abrir a janela em modo cascata:\n\n" +
                     "1. Selecione um Epic na combo → as Features daquele Epic são carregadas automaticamente.\n" +
                     "2. Selecione uma Feature → as Stories daquela Feature são carregadas.\n" +
                     "3. Selecione a Story desejada (ou '(Todas)' para ver todas as Stories da Feature) → clique 🔍 Buscar Tasks.\n\n" +
                     "As Tasks são buscadas diretamente no Azure DevOps no momento do clique."),
                    ("Abrir pelo menu de contexto da Story",
                     "Clique com o botão direito em uma Story na grade → 'Grid de Tasks (DevOps)...'.\n\n" +
                     "A janela abre com a Story já pré-selecionada e as Tasks são carregadas automaticamente — sem precisar usar as combos de Epic e Feature."),
                    ("O que o Tech Lead pode fazer",
                     "• Visualizar todas as Tasks de uma ou mais Stories (ID, título, estado, HH, prioridade, responsável).\n" +
                     "• Editar estimativas (HH Estimado, HH Atual), prioridade, responsável, estado e tipo de atividade.\n" +
                     "• Criar novas Tasks pendentes — serão criadas no Azure DevOps na próxima sincronização.\n" +
                     "• Adicionar Tasks ao cronograma: Tasks do DevOps ainda não presentes no cronograma podem ser incluídas com um clique.\n" +
                     "• Sincronizar alterações de volta ao Azure DevOps (botão 'Salvar Alterações')."),
                    ("Coluna TKs",
                     "Ao buscar Tasks no Tech Lead, a coluna TKs no cronograma é atualizada automaticamente com a contagem de Tasks encontradas por Story.\n\n" +
                     "Isso permite enxergar de relance quais Stories já têm Tasks técnicas criadas no DevOps e quais ainda não têm (valor 0 em vermelho).")
                },
                "Use o modo cascata (toolbar) para planejar as Tasks de uma Feature inteira de uma vez. Use o menu de contexto da Story para acesso rápido a uma Story específica durante a execução."
            ),
            // ── Tópico 3: Cronograma ─────────────────────────────────────────
            (
                "Cronograma",
                "A grade de tarefas é onde você visualiza e edita a estrutura do projeto: hierarquia, datas, duração, recursos, percentual de conclusão e dependências.",
                new()
                {
                    ("Hierarquia de tarefas",
                     "O projeto é organizado em níveis: Feature → Story → Task ou qualquer agrupamento que faça sentido. Tarefas filhas são indentadas abaixo da tarefa pai.\n" +
                     "• Use Editar → Criar Subtarefa para indentar uma tarefa.\n" +
                     "• Use Editar → Promover Tarefa para subir um nível.\n" +
                     "• Tarefas agrupamento (com filhos) calculam datas e duração automaticamente a partir dos filhos.\n" +
                     "• As linhas de Task ficam com um cinza sutil quando não selecionadas, para diferenciá-las de EPIC/Feature/Story."),
                    ("Expandir e recolher",
                     "• O botão Expandir a hierarquia abre UM NÍVEL por vez: EPIC → Feature → Story → Task (as Tasks já carregadas no cronograma). Cada clique mostra o próximo nível e recolhe os mais profundos.\n" +
                     "• Expandir nível da selecionada abre o nível das atividades irmãs da selecionada; Recolher tudo fecha toda a hierarquia."),
                    ("Load Task ToDo",
                     "O ícone Load Task ToDo na toolbar carrega do DevOps as Tasks das Stories com % de conclusão abaixo de 100% (ainda a fazer) e as aplica no cronograma. Traz TODAS as Tasks da Story, inclusive as já concluídas (Closed), para a duração e a soma de HH ficarem corretas. Não duplica as que já existem no cronograma.\n" +
                     "• Ctrl + Clique no ícone pergunta se as Stories já 100% concluídas também devem entrar. Respondendo Sim, as Tasks de TODAS as Stories vinculadas ao DevOps são carregadas — útil para conferir visualmente o projeto inteiro no Gantt. Respondendo Não, vale o comportamento padrão (só abaixo de 100%)."),
                    ("Duração e datas",
                     "• Coluna Dur.(h): informe em horas (ex: 8) ou em dias úteis com d (ex: 2d = 2 dias úteis).\n" +
                     "• A data Fim é calculada automaticamente: Início + Dur.(h) respeitando o calendário de trabalho.\n" +
                     "• Para fixar a data de Início, digite a data no campo — ela fica marcada com 📌. Se a data digitada diferir da calculada, um calendário é aberto para confirmação visual.\n" +
                     "• Use Ctrl + Clique na célula de Início para abrir o calendário diretamente sem precisar digitar.\n" +
                     "• Para fixar a data de Fim, informe a data no campo Fim ou arraste a borda direita da barra no Gantt com o botão direito do mouse (na barra já selecionada).\n" +
                     "• Para remover fixação de Início, digite 0 no campo Início — o cronograma recalcula a data automaticamente."),
                    ("Percentual de conclusão",
                     "• O campo % Compl. registra o avanço da tarefa (0 a 100).\n" +
                     "• Na grade, percentuais baixos usam texto escuro sobre o fundo claro; percentuais maiores usam texto branco sobre a área preenchida.\n" +
                     "• Tarefas agrupamento calculam o percentual como média ponderada das horas dos filhos.\n" +
                     "• Se a data Fim estiver no passado e o percentual for menor que 100, o sistema alerta automaticamente no Health Check."),
                    ("Criação de Atividade",
                     "Ao adicionar uma nova atividade (botão + ou Editar → Adicionar Atividade):\n" +
                     "• O Tipo, o Recurso e a Sprint são copiados automaticamente da atividade selecionada no momento do clique.\n" +
                     "• O ID DevOps é definido como 0, indicando que a atividade será criada no Azure DevOps na próxima sincronização (Export → Sincronizar).\n" +
                     "• Atividades com Tipo = 'No DevOps' nunca são enviadas ao Azure DevOps — servem apenas para controle local no cronograma.\n" +
                     "• Atividades sem Tipo definido são automaticamente classificadas como 'No DevOps' para evitar criação acidental no DevOps."),
                    ("Atualização de Atividade no DevOps",
                     "• Atividades com ID DevOps > 0 são atualizadas no Azure DevOps ao executar Export → Sincronizar.\n" +
                     "• Atividades com ID DevOps = 0 (e Tipo diferente de 'No DevOps') são criadas como novos work items no Azure DevOps, e o ID retornado é gravado no cronograma.\n" +
                     "• Atividades com Tipo 'No DevOps' são ignoradas pela sincronização, mesmo que tenham ID = 0.\n" +
                     "• No Import: se um work item do Azure DevOps tiver o mesmo nome que uma atividade 'No DevOps' local, o NXProject vincula automaticamente a atividade local ao item importado, atualizando seu Tipo para o tipo do DevOps."),
                    ("Bloqueio (tag BLOCK)",
                     "O NXProject diferencia dois tipos de bloqueio visíveis na coluna Nome:\n" +
                     "• ⛔ BLOCK (vermelho) — a própria Story/atividade tem a tag 'Block'. Quando ambos existem, apenas este ícone é exibido.\n" +
                     "• 🔴 BLOCK (amarelo) — bloqueio herdado de uma Task filha no DevOps que tem a tag 'Block'.\n\n" +
                     "Para adicionar ou retirar o Block da Story, clique com o botão direito no nome da atividade e use o menu 'Adicionar/Retirar Block da Story'.\n\n" +
                     "Sincronização da tag Block:\n" +
                     "• Se a Story no NXProject tem Block e o DevOps não tem → a tag é adicionada no DevOps ao sincronizar.\n" +
                     "• Se a Story no NXProject não tem Block e o DevOps tem → a tag é removida do DevOps ao sincronizar.\n\n" +
                     "Na importação, o NXProject lê a tag Block tanto da própria Story (registrada nas tags da atividade) quanto das Tasks filhas (refletida como bloqueio herdado)."),
                    ("Editar o nome da atividade",
                     "A coluna Nome da atividade requer duplo clique para entrar em modo de edição, evitando alterações acidentais ao navegar pelas células.\n\n" +
                     "As demais colunas (Início, Fim, Dur.(h), % Compl. etc.) continuam ativando a edição com um único clique."),
                    ("Coluna TKs",
                     "A coluna TKs (visível apenas no modo expandido) exibe a quantidade de Tasks filhas de cada Story no Azure DevOps.\n\n" +
                     "• Valor numérico: quantidade de Tasks encontradas no DevOps para esta Story.\n" +
                     "• 0 em vermelho: Story sem nenhuma Task técnica criada no DevOps.\n" +
                     "• Célula vazia: contagem ainda não calculada (Story não importada do DevOps ou não consultada via Tech Lead).\n\n" +
                     "O valor é atualizado automaticamente:\n" +
                     "• Na importação do Azure DevOps.\n" +
                     "• Ao buscar Tasks no Tech Lead.\n" +
                     "• Ao adicionar Tasks ao cronograma ou criar Tasks na Grid de Tasks.")
                },
                "Informe Início e Dur.(h) — o Fim é calculado pelo calendário. Para dependências, use a coluna Pred."
            ),
            (
                "Datas da Atividade",
                "As datas de uma atividade são calculadas a partir do Início, das horas reais de trabalho, do calendário, da % de alocação do recurso, do percentual de conclusão e das regras de cascata. Em linha com o objetivo de transparência da Nexus Xdata, esta seção explicita as regras usadas pelo cronograma.",
                new()
                {
                    ("Início, horas e fim",
                     "• Início é a data em que a atividade começa no cronograma.\n" +
                     "• Dur.(h), HH Atual e HH Restante são HH reais de esforço, não prazo de calendário.\n" +
                     "• Dur.(h) é o esforço total da atividade: HH Atual + HH Restante.\n" +
                     "• A % de alocação não reduz nem aumenta esses HH; ela só converte o esforço em duração de calendário.\n" +
                     "• Fim é calculado por Início + HH reais convertidos pela % de alocação, respeitando dias úteis, feriados e horas úteis por dia.\n" +
                     "• Exemplo: 40h com 50% de alocação continuam sendo 40h de esforço, mas ocupam cerca de 80h de calendário.\n" +
                     "• A data mostrada na coluna Fim é a data de término visível para o usuário; internamente o cálculo usa o limite final do período de trabalho."),
                    ("% Compl., HH Atual e HH Restante",
                     "• HH Atual é o trabalho já realizado; HH Restante é o trabalho ainda necessário.\n" +
                     "• Dur.(h) é o total de esforço real da atividade: HH Atual + HH Restante.\n" +
                     "• Quando o % Compl. é alterado, o NXProject mantém a Dur.(h) e reparte esse total: HH Atual = Dur.(h) × % Compl.; HH Restante = Dur.(h) - HH Atual.\n" +
                     "• Exemplo: atividade de 8h com 25% concluído fica com HH Atual = 2h e HH Restante = 6h.\n" +
                     "• Com % Compl. = 0, HH Atual fica 0 e HH Restante volta para o esforço original. Com % Compl. = 100, HH Atual recebe o total e HH Restante fica 0.\n" +
                     "• Se uma atividade importada ou aberta de arquivo vier com HH Atual/HH Restante vazios, mas tiver esforço e % Compl. menor que 100%, o NXProject preenche esses campos pela mesma regra.\n" +
                     "• % de alocação não reduz o HH da atividade; ela muda o prazo no calendário. Exemplo: 8h restantes com 10% de alocação em calendário de 8h/dia continuam sendo 8h de trabalho, mas ocupam cerca de 10 dias úteis."),
                    ("Início fixado",
                     "• Ao digitar uma data no campo Início, o Início fica fixado e aparece com o ícone de fixação.\n" +
                     "• Uma atividade com Início fixado não é recuada automaticamente por cascata de recurso ou predecessora virtual.\n" +
                     "• Para remover a fixação do Início, digite 0 no campo Início — o cronograma recalcula automaticamente a data.\n" +
                     "• Se o Início fixado estiver no futuro e a atividade for marcada como 100%, o Fim fica igual ao Início fixado, para evitar Fim antes do Início."),
                    ("Calendário visual para edição do Início",
                     "Um calendário é aberto automaticamente para auxiliar na escolha da data de Início em dois cenários:\n\n" +
                     "• Ctrl + Clique na célula de Início: abre o calendário pré-posicionado na data atual da atividade. Útil para trocar a data sem precisar digitar.\n\n" +
                     "• Data digitada diferente da data calculada: se o valor digitado não coincidir com a data válida do cronograma, o calendário abre pré-selecionado no próximo dia útil mais próximo da data digitada, para confirmar visualmente antes de aplicar.\n\n" +
                     "• Data inválida digitada: se o texto digitado não for uma data reconhecível, o calendário abre pré-posicionado na data calculada atual da atividade.\n\n" +
                     "No calendário:\n" +
                     "• Clique no dia desejado para confirmar imediatamente.\n" +
                     "• Pressione Enter para confirmar a data já selecionada (útil ao digitar uma data válida e apenas conferir).\n" +
                     "• Pressione Escape para cancelar sem alterar a data."),
                    ("Fim fixado",
                     "• Ao editar a coluna Fim ou arrastar a borda direita da barra no Gantt com o botão direito, o Fim fica fixado.\n" +
                     "• Com Fim fixado, alterações de HH, % de conclusão ou % de alocação não recalculam automaticamente a data Fim.\n" +
                     "• Use a fixação de Fim para registrar uma data negociada que pode ser diferente do fim calculado por HH real e alocação.\n" +
                     "• Se houver diferença entre prazo negociado e prazo calculado, o Gantt pode indicar conflito visual."),
                    ("Percentual 0%",
                     "• Ao voltar % Compl. para 0%, o NXProject considera que nenhum trabalho foi realizado.\n" +
                     "• HH Atual fica igual a 0.\n" +
                     "• HH Restante volta para HH Original.\n" +
                     "• A data Fim é recalculada por Início + HH Restante, desde que o Fim não esteja fixado.\n" +
                     "• A cascata pode reposicionar atividades seguintes do mesmo recurso, mas não deve usar Features ou agrupadores como referência de fila."),
                    ("Percentual 100%",
                     "• Ao marcar % Compl. como 100%, o NXProject considera a atividade encerrada.\n" +
                     "• HH Atual recebe o esforço total da atividade.\n" +
                     "• HH Restante fica igual a 0.\n" +
                     "• O Fim calculado é Início + esforço total convertido pela % de alocação. Se esse Fim cair no futuro, o Fim é limitado a hoje, pois não é possível encerrar uma atividade no futuro.\n" +
                     "• Exceção: se o Início estiver fixado em uma data futura, o Fim fica igual ao Início fixado."),
                    ("Cascata por predecessoras e recurso",
                     "• Predecessoras explícitas movem a atividade para o próximo dia útil após o fim visível da predecessora.\n" +
                     "• A cascata usa o padrão de ordenação topológica: uma atividade dependente só é recalculada depois que suas predecessoras já foram processadas.\n" +
                     "• A predecessora virtual organiza atividades do mesmo recurso, mesmo pai e mesmo nível, para evitar sobreposição de trabalho.\n" +
                     "• A referência da predecessora virtual deve ser outra atividade folha, como Story/Task, nunca uma Feature, Epic ou agrupador.\n" +
                     "• Agrupadores continuam sendo recalculados para refletir datas, duração e percentual dos filhos.")
                },
                "Regra prática: edite Início e Dur.(h) para planejar; use % Compl. para registrar progresso. Fixações são exceções conscientes ao cálculo automático."
            ),
            (
                "Gráfico Gantt",
                "O Gantt exibe as barras de cada atividade no tempo, com marcos, setas de dependência, sprints e a linha de hoje.",
                new()
                {
                    ("Navegação e zoom",
                     "• Use o botão de zoom na toolbar para alternar entre Dia, Semana, Sprint, Mês, Trimestre e Semestre.\n" +
                     "• Role horizontalmente para navegar no tempo.\n" +
                     "• Ative o botão de lupa na toolbar e mova o mouse sobre o Gantt para analisar datas, barras e dependências de perto.\n" +
                     "• A linha vermelha vertical indica a data de hoje."),
                    ("Visões de cabeçalho por dia",
                     "O botão de calendário (📅) na toolbar cicla entre três modos:\n" +
                     "• Off: cabeçalho padrão por sprint e mês.\n" +
                     "• Dia 1: destaca segunda-feira com número do dia, quarta e sexta em azul mais vivo.\n" +
                     "• Dia 2: exibe o dígito da unidade de cada dia. Os dias 10, 20 e 30 ficam destacados em azul, laranja e verde respectivamente — facilitando a leitura de datas sem sobrecarregar o cabeçalho."),
                    ("Arrastar barras",
                     "• Botão esquerdo + arrastar: move a data de Início da atividade (somente para atividades que ainda não iniciaram).\n" +
                     "• Botão direito + arrastar (na barra já selecionada): ajusta a data de Fim sem alterar a estimativa de horas. Ao soltar, a data Fim fica fixada (📌).\n" +
                     "• Atividades dependentes se deslocam automaticamente ao mover uma predecessora."),
                    ("Barras e cores",
                     "• Barra azul claro: atividade normal.\n" +
                     "• Barra laranja: atividade selecionada.\n" +
                     "• Faixa escura central: percentual de conclusão, no estilo MS Project.\n" +
                     "• Linha escura discreta na base: HH Atual proporcional ao total de HH Atual + HH Restante.\n" +
                     "• Losango dourado: marco (milestone).\n" +
                     "• Barra cinza-azulada clara: agrupamento (Feature/Epic).\n" +
                     "• Bordas ou realces em vermelho indicam conflito, atraso ou duração negociada diferente da calculada.")
                },
                "Clique em uma barra para selecionar a tarefa na grade. As setas de dependência mostram o caminho crítico visualmente."
            ),
            (
                "Predecessoras",
                "Predecessoras definem que uma atividade só pode iniciar após o término de outra, criando a cadeia de dependências do projeto.",
                new()
                {
                    ("Como cadastrar",
                     "Clique no campo Pred. da atividade que depende de outra. Uma janela de seleção abre com todas as atividades de último nível disponíveis.\n" +
                     "• Use a busca para localizar pelo nome ou código.\n" +
                     "• Marque uma ou mais atividades com o checkbox.\n" +
                     "• O painel superior mostra as predecessoras já marcadas antes de confirmar."),
                    ("Predecessoras fora da lista",
                     "Quando uma atividade importada do DevOps tem predecessoras que apontam para itens fora do escopo importado, elas aparecem em amarelo no seletor com o rótulo 'fora da lista filtrada'.\n" +
                     "• Cada predecessora externa pode ser removida individualmente pelo botão ✕ Remover.\n" +
                     "• Predecessoras dentro da lista são marcadas normalmente por checkbox."),
                    ("Efeito no cronograma",
                     "Ao mover uma atividade no Gantt, todas as atividades que dependem dela (direto ou indiretamente) são deslocadas automaticamente pelo mesmo número de dias.")
                },
                "Para encadear atividades em sequência de uma vez, selecione várias e use Editar → Encadear Atividades."
            ),
            (
                "Recursos",
                "Recursos são as pessoas alocadas nas atividades. O NXProject importa responsáveis do Azure DevOps e permite gerenciar a carga de trabalho por pessoa.",
                new()
                {
                    ("Cadastrar recursos",
                     "Acesse Exibir → Pessoas para gerenciar a lista de recursos do projeto. Cada pessoa pode ter nome e e-mail.\n" +
                     "Ao importar do Azure DevOps, o campo System.AssignedTo é importado automaticamente como recurso."),
                    ("Alocação por Sprint",
                     "Gestão → Alocação por Sprint mostra a carga de trabalho por pessoa em cada período (sprint ou semana), permitindo identificar sobrecargas antes que virem problemas.\n" +
                     "• Células vermelhas indicam sobrecarga (mais de 100% da capacidade diária).\n" +
                     "• Células verdes indicam capacidade disponível.\n" +
                     "• A capacidade considera as horas/dia do calendário, as horas/dia configuradas para o recurso e a % de alocação da atividade.\n" +
                     "• HH Atual e HH Restante continuam sendo horas de trabalho; a % de alocação define em quantos dias essas horas cabem.\n\n" +
                     "O Mapa de Alocação por Projeto (Exibir → Mapa de Alocação) exibe horas por recurso × projeto × mês com as seguintes abas:\n" +
                     "• Horas por Projeto — horas de cada recurso em cada projeto por mês.\n" +
                     "• Distribuição por Pessoa — visão consolidada de todos os projetos por recurso.\n" +
                     "• Stories por Recurso — detalhamento de cada story por recurso e mês.\n" +
                     "• Rateio — % que cada projeto representa do total de horas do recurso naquele mês.\n\n" +
                     "Critério de cálculo das horas por mês:\n" +
                     "As horas de cada atividade são distribuídas proporcionalmente entre os meses cobertos pela faixa Início → Fim da atividade, não pela sprint. Se uma story vai de 10/jan a 20/fev (42 dias), 22 dias ficam em janeiro e 20 dias em fevereiro; as horas são distribuídas nessa proporção (22/42 em jan, 20/42 em fev).\n\n" +
                     "O valor de horas mostrado em cada célula é HH Atual (já trabalhado) + HH Restante (previsto). Na aba Horas por Projeto, use o checkbox 'Apenas HH atual (alocado)' para ver somente as horas já executadas, excluindo a estimativa futura."),
                    ("Filtro por recurso",
                     "O botão 👤 na toolbar permite filtrar o Gantt e a grade para mostrar somente as atividades de uma pessoa específica — útil em reuniões individuais de acompanhamento.")
                },
                "Use o filtro de recurso na toolbar para ver somente as atividades de uma pessoa durante a reunião de status."
            ),
            (
                "Mapa de Alocação",
                "O Mapa de Alocação por Projeto (Exibir → Mapa de Alocação) consolida horas de múltiplos projetos por recurso e mês, permitindo enxergar sobrecargas e planejar capacidade.",
                new()
                {
                    ("Abas disponíveis",
                     "• Horas por Projeto — horas de cada recurso em cada projeto por mês. Clique em uma célula para ver as stories do recurso naquele mês.\n" +
                     "• Distribuição por Pessoa — visão consolidada de todos os projetos por recurso, com total e percentual de capacidade.\n" +
                     "• Stories por Recurso — detalhamento de cada story com HH Total (Atual + Restante), % de conclusão, início e fim.\n" +
                     "• Rateio — mostra o % que cada projeto representa do total de horas do recurso naquele mês.\n" +
                     "• Interno — visão separada dos recursos internos, quando houver."),
                    ("Critério de horas por mês",
                     "As horas de cada atividade são distribuídas proporcionalmente entre os meses cobertos pela faixa Início → Fim da atividade. A sprint não concentra as horas no mês dela; ela apenas identifica a iteração.\n\n" +
                     "Exemplo: uma story de 10/jan a 20/fev tem 22 dias em janeiro e 20 dias em fevereiro; se a story tem 42 horas no total, 22h ficam em janeiro e 20h em fevereiro (proporção 22/42 e 20/42).\n\n" +
                     "O valor exibido no modo normal é HH Atual + HH Restante (duração total prevista). O checkbox 'Apenas HH atual (alocado)' aparece somente na aba Horas por Projeto e mostra apenas as horas já realizadas nessa aba."),
                    ("HH Atual e HH Restante por mês",
                     "O Mapa de Alocação separa o trabalho já realizado do trabalho restante antes de distribuir as horas no calendário:\n\n" +
                     "• HH Atual é distribuído do Início até hoje (ou até o Fim, quando a atividade está 100%).\n" +
                     "• HH Restante é distribuído do ponto seguinte até o Fim da atividade.\n" +
                     "• O modo normal soma as duas partes: HH Atual + HH Restante.\n" +
                     "• O checkbox 'Apenas HH atual (alocado)' é um modo de análise da aba Horas por Projeto; Distribuição por Pessoa, Stories por Recurso, Rateio e Interno usam sempre HH Atual + HH Restante.\n" +
                     "• Quando há mais de um recurso na mesma atividade, o HH Atual é rateado entre os recursos pela proporção do HH Restante de cada assignment; se não houver essa base, usa a proporção da % de alocação.\n\n" +
                     "Essa regra evita jogar HH já realizado em meses futuros ou HH restante em meses passados."),
                    ("Story com Tasks de outra pessoa (decomposição do HH)",
                     "Quando uma Story tem Tasks de recursos diferentes do responsável, o HH da Story é DECOMPOSTO entre as pessoas — o total continua sendo o HH da Story (não infla o projeto):\n\n" +
                     "• Cada Task credita o seu HH estimado para o recurso da Task.\n" +
                     "• O responsável da Story fica com o RESTANTE: HH da Story menos a soma das Tasks. Assim ele não perde tudo por delegar (ainda revisa o que foi feito), mas o total fecha no HH da Story.\n" +
                     "• Trava: nenhuma Task pode passar do HH da Story. Se a soma das Tasks estoura o HH da Story, as Tasks são cortadas proporcionalmente (HH da Story ÷ soma das Tasks) e o responsável fica com 0.\n" +
                     "• Se o responsável não tem Task própria, ele fica com a sobra (sem Tasks de outros, fica com a Story inteira).\n" +
                     "• Se a Story não tem HH estimado, nada é cortado — as Tasks aparecem com o HH delas.\n\n" +
                     "A conta usa o modelo (não a árvore visível), então as Tasks de outro recurso entram mesmo com a Story recolhida no cronograma. Vale para o Mapa de Alocação e para a Alocação por Sprint."),
                    ("Resumo de tasks por recurso (gravado no arquivo)",
                     "As Tasks vivem no DevOps e não são carregadas no cronograma. Para decompor o HH da Story mesmo sem as Tasks abertas, o NXProject grava no arquivo (.nxp) um resumo por recurso:\n\n" +
                     "• Cada entrada tem recurso, horas, quantidade de tasks e estado (Active/Closed/New/Other), agrupada por recurso + estado.\n" +
                     "• Horas por task: se a Task está Closed usa o Completed (HH Atual); senão o Estimate.\n" +
                     "• O resumo é preenchido/atualizado no Sync e também no import do próprio Mapa de Alocação (que lê o DevOps até o nível da task).\n" +
                     "• Ao clicar nas horas na aba Stories por Recurso, a grade de composição mostra Tipo (Story/Task), a Story, a quantidade de tasks (Task = nº de tasks do recurso; Story = 1) e um botão para abrir a grade de tasks (Tech Lead) da story."),
                    ("Filtro: Story com % > 0 e Task Active/Closed",
                     "O Mapa de Alocação e a Alocação por Sprint só consideram o que está em execução:\n\n" +
                     "• Story entra quando tem % de conclusão > 0.\n" +
                     "• Task/resumo entra quando o estado é Active ou Closed (arquivos legados sem estado continuam contando).\n\n" +
                     "Na aba Stories por Recurso, o flag 'Stories sem % (fora do mapa)' mostra em vermelho as Stories excluídas por % = 0 — úteis para revisar o que ainda não começou.\n\n" +
                     "Na Alocação por Sprint, o flag 'Incluir planejado (Story % 0 / Task New)' passa a considerar também as Stories com % = 0 e tasks New, para ver a distribuição da sprint planejada."),
                    ("% de capacidade",
                     "O percentual exibido ao lado das horas nas abas de capacidade é calculado sobre a capacidade mensal do calendário e do recurso: horas/dia úteis × dias úteis do mês, considerando a configuração da pessoa.\n\n" +
                     "Na aba Rateio, o % representa a fatia daquele projeto no total de horas do recurso no mês — não em relação à capacidade total."),
                    ("% de Alocação e data fim",
                     "Ao clicar no % de alocação de uma atividade, a janela permite:\n" +
                     "• Informar HH/dia para calcular o % (ex: 4h/dia = 50%).\n" +
                     "• Informar a data fim desejada: o NXProject calcula automaticamente o % de alocação necessário para completar as horas totais (HH Atual + HH Restante) até aquela data.\n" +
                     "  Fórmula: % = Horas Totais ÷ Horas úteis(Início → Data Fim) × 100.\n" +
                     "  Isso permite descobrir por engenharia reversa quanto o recurso precisou se dedicar para entregar em um prazo específico.\n\n" +
                     "Importante: a % de alocação muda o prazo calculado, não o total de HH da atividade. Uma atividade de 8h continua tendo 8h; com 10% de alocação em calendário de 8h/dia, ela consome cerca de 10 dias úteis.")
                },
                "Filtre os projetos com 'Selecionar Projetos' e ajuste o período de análise — as colunas zeradas são ocultadas automaticamente quando 'Ocultar linhas/colunas zeradas' está marcado."
            ),
            (
                "Sprints",
                "O NXProject suporta sprints do Azure DevOps e permite configurar sprints locais para organizar o cronograma em iterações.",
                new()
                {
                    ("Configurar sprints",
                     "Exibir → Sprint define o número da primeira sprint, duração em dias e modo de numeração (sequencial, par ou ímpar).\n" +
                     "Se o projeto foi importado do Azure DevOps, as sprints são lidas de System.IterationPath e criadas automaticamente."),
                    ("Associar atividades",
                     "A coluna Sprint na grade permite mover Stories e Features entre sprints. Ao alterar a sprint, a data de Início é recalculada para o início daquela sprint.\n" +
                     "• Para remover a associação com sprint e usar data fixa, basta informar uma data no campo Início."),
                    ("Visão no Gantt",
                     "O Gantt exibe as sprints no cabeçalho inferior, com numeração e cores alternadas. A visão de zoom Sprint ou Semana deixa as iterações mais visíveis."),
                    ("Ajustar sprints fora do período",
                     "O botão 🏁 na toolbar (e Gerenciar → Ajustar sprints fora do período) reatribui atividades cuja sprint não corresponde ao período em que a atividade realmente está.\n\n" +
                     "Data de referência (onde a atividade está):\n" +
                     "• 0% de conclusão → data de Início.\n" +
                     "• Em andamento (>0% e <100%) → posição do % concluído: Início + (% × duração útil).\n" +
                     "• 100% → data de Fim.\n\n" +
                     "Escolha da sprint ao ajustar:\n" +
                     "• Se alguma sprint contém a data de referência → usa ela (sai o destaque).\n" +
                     "• Senão → a última sprint que começa em/antes da referência (mais próxima e ANTES) → permanece destacada.\n\n" +
                     "Destaque na coluna Sprint:\n" +
                     "• Laranja: a sprint atribuída não contém a data de referência (ajustável pelo botão).\n" +
                     "• Texto verde itálico: atividade 100% concluída antecipada, em uma sprint de período anterior (não ajusta).\n" +
                     "• Texto azul: sprint em andamento (contém a data de hoje) com o % fora do período (não ajusta).\n\n" +
                     "Quando NÃO sugere (só destaca): a data de referência já alcançou a sprint atual (referência ≥ início da sprint) e a sprint já venceu (fim ≤ hoje) — a atividade passou pela sprint. A célula fica laranja no cronograma, mas a janela de ajuste não a lista.\n\n" +
                     "O hint da célula Sprint no cronograma mostra a data de referência (ritmo) da atividade.\n\n" +
                     "Antes de aplicar, uma janela mostra Epic, Feature, Story, Período da Atividade, Ref. (ritmo), Status, % Concl., Sprint Atual e Sprint Ajustada para revisão; nada muda sem clicar em Aplicar. As datas não são movidas — apenas o rótulo da sprint. Depois, sincronize com o DevOps para gravar.")
                },
                "A coluna Sprint é especialmente útil para replanejar — mova Stories entre sprints e veja o impacto no cronograma imediatamente."
            ),
            (
                "Progresso do Projeto e Curva S",
                "Gestão → Progresso do Projeto e Curva S mostra a evolução do projeto no tempo comparando o PLANEJADO com o REALIZADO — é a Curva S clássica de Gestão de Valor Agregado (EVM), com um ponto por semana.",
                new()
                {
                    ("As duas linhas (fundamento EVM)",
                     "É a Curva S de Earned Value Management (padrão PMI/PMBOK). O eixo Y é o % acumulado de HH; o eixo X é o tempo (um ponto por semana, na segunda-feira).\n\n" +
                     "• HH Original (Planejado) = PV (Planned Value): baseline de quanto deveria estar pronto, distribuído pelas datas Início→Fim de cada Story. Sempre inclui todas as Stories.\n" +
                     "• HH Realizado (concluído) = EV (Earned Value): HH × %conclusão do que foi entregue.\n" +
                     "• A distância entre as linhas é o SV (Schedule Variance): planejado à frente = atrasado; realizado à frente = adiantado."),
                    ("Distribuição por data, não por sprint",
                     "As horas são distribuídas pela faixa Início→Fim de cada Story, não jogadas inteiras numa sprint — a sprint é só a janela de controle. Marcos (duração zero) entram na semana da sua data, sem serem repetidos."),
                    ("Realizado: passado real, futuro projetado pela velocidade",
                     "A linha do realizado é calculada em duas partes, cortadas por HOJE:\n" +
                     "• Passado (até hoje): só o CONCLUÍDO (HH × %). Trabalho não feito nunca aparece no passado.\n" +
                     "• De hoje em diante (só com 'Incluir HH Restante'): o restante é entregue na VELOCIDADE histórica = HH concluído ÷ dias úteis decorridos. Não usa o ritmo do cronograma (otimista) — olha o passado para projetar o futuro.\n\n" +
                     "A conclusão projetada = hoje + (HH restante ÷ velocidade). Se passar do fim planejado, o eixo acrescenta semanas até essa data. É a mesma lógica de forecasting por throughput (Little's Law / burn-up ágil): a diferença entre a projeção real e o planejado forma a 'barriga'."),
                    ("Os checkboxes",
                     "• Incluir HH Restante: liga a projeção do restante pela velocidade (senão a linha mostra só o concluído e estaciona).\n" +
                     "• Incluir planejado (Story % 0 / Task New): traz também as Stories não iniciadas para o realizado — só têm efeito junto com 'Incluir HH Restante' (uma Story a 0% concluído entrega 0). Com as duas, a projeção cobre todo o trabalho restante e o eixo se estende até a conclusão projetada."),
                    ("Pontos semanais e régua de sprints",
                     "A curva tem um ponto por SEMANA (segunda-feira) para deixar a barriga suave. Por cima, uma régua mostra as sprints como marcador de tempo: divisórias no início de cada sprint com o nome no topo — sprints configuradas em azul e sprints PROJETADAS (S8, S9… proj.) em cinza itálico, criadas quando o trabalho passa da última sprint. O resumo mostra 'Sprints: N config. + M proj.', para ver quantas já temos e quantas ainda vamos precisar."),
                    ("Base line (3ª linha, opcional)",
                     "Marque 'Mostrar base line' para comparar com um snapshot salvo do projeto. Se nenhum estiver carregado, aparece o botão 'Abrir baseline…' para escolher um arquivo .nxp.\n\n" +
                     "A 3ª linha (verde tracejada) usa o HH Atual + Restante das Stories do baseline distribuído pelas datas. O HH Original (azul) do projeto atual NÃO muda — é o baseline congelado; a linha verde é a referência do snapshot para comparar planejado × re-planejado."),
                    ("Outras abas",
                     "• Atrasos por Recurso — matriz de atividades atrasadas por pessoa e faixa de atraso.\n" +
                     "• Atividades Atrasadas — lista completa das atrasadas, com justificativa (clique no ID).\n" +
                     "• Em Bloqueio — atividades marcadas como bloqueadas.")
                },
                "Marque 'Incluir HH Restante' para ver a projeção pela sua velocidade real — a barriga mostra o quanto o ritmo atual afasta a entrega do plano."
            ),
            (
                "Planilha de Plan Task",
                "A Planilha de Plan Task é uma grade estilo Excel para planejar as Tasks de cada EPIC em um arquivo .xlsx nativo — editável tanto pelo NXProject quanto pelo Excel — integrada ao cronograma aberto e ao Azure DevOps.",
                new()
                {
                    ("Arquivo (.xlsx nativo)",
                     "O botão da toolbar abre o Task Plan. Novo cria a planilha com as colunas necessárias (EPIC, Feature, Story, Task, ID Devops, Prioridade, Estimado, Status, Descrição, Observações); Abrir carrega qualquer .xlsx (a linha de títulos é reconhecida automaticamente, mesmo com bloco de resumo acima); o Salvar 💾 grava preservando o restante da aba (resumo, fórmulas). Colunas vinculadas ao cronograma são criadas se faltarem e não podem ser excluídas.\n" +
                     "Em ⚙ Configurações: pasta padrão dos arquivos e os campos do SharePoint (Entra ID + Graph, integração futura). O último arquivo é reaberto automaticamente; sem arquivo, a grade é montada do cronograma aberto."),
                    ("Edição estilo Excel",
                     "• Ctrl+Z desfaz as últimas alterações (edição, colar, cores, linhas e colunas — até 10 níveis); também no menu do botão direito → Desfazer.\n" +
                     "• Seleção de células em bloco (Shift/arrastar) com Copiar/Colar por Ctrl+C/Ctrl+V ou pelo menu do botão direito — inclusive de/para o Excel; colar além do fim cria linhas novas.\n" +
                     "• Linhas numeradas como no Excel; botão direito: inserir linha acima/abaixo, excluir linha(s), limpar células e Cor da célula (paleta) — as cores são lidas do Excel (inclusive as de tema) e gravadas de volta.\n" +
                     "• Botão direito no cabeçalho: Filtro... (tela com pesquisa e checkboxes por valor), inserir/renomear/excluir coluna e ajustes de largura; no menu das células, ajuste de altura ao texto e da planilha inteira."),
                    ("Onde guardar o arquivo (OneDrive/SharePoint)",
                     "• Pasta local ou de rede: funciona direto; se o arquivo estiver aberto no Excel, o NXProject avisa na hora de salvar (um editor por vez).\n" +
                     "• SharePoint via OneDrive sincronizado (recomendado): no site do SharePoint clique em \"Sincronizar\" — a biblioteca vira uma pasta local (ex.: C:\\Users\\você\\Empresa\\...) e o Task Plan abre o .xlsx dali normalmente; o OneDrive cuida do envio e do versionamento. Aponte a Pasta padrão (⚙) para ela. Vale a regra de um editor por vez.\n" +
                     "• SharePoint direto (URL https): o Windows não abre esse endereço (WebDAV bloqueado pela autenticação moderna) — o NXProject orienta ao colar a URL. O acesso direto com coautoria exigirá o App registrado no Entra ID (Tenant/Client ID em ⚙; integração em desenvolvimento) — sem client secret, o login é do próprio usuário (MSAL).\n" +
                     "• Sem arquivo nenhum: para apenas revisar, use Novo → \"Do cronograma + Tasks do TFS\" — carrega tudo na grade para conferência e só cria o .xlsx se você salvar."),
                    ("Colunas novas e movidas — o prefixo \"xx#_\"",
                     "Ao salvar, cada coluna volta para a MESMA célula física da planilha, preservando o bloco de resumo e as fórmulas que apontam para colunas fixas. Por isso:\n" +
                     "• Coluna criada na tela é gravada no FIM da aba, e coluna comum movida (arrastando o cabeçalho) permanece na célula física original.\n" +
                     "• Para a tela lembrar onde exibi-las, o cabeçalho delas é gravado como \"posição#_Nome\" (ex.: \"2#_Observações\" = 2ª coluna da visão). Ao reabrir, o prefixo é removido e a coluna volta para a posição certa na grade — no Excel você verá o prefixo no título, e é seguro mantê-lo.\n" +
                     "• As colunas vinculadas ao cronograma (EPIC, Feature, Story, Task, ID Devops, Prioridade, Estimado, Status) têm posição fixa e nome sempre limpo — elas nunca recebem o prefixo; para movê-las de lugar na planilha, faça pelo Excel."),
                    ("Integração com DevOps e cronograma",
                     "• Buscar Task no DevOps: para linhas sem ID, localiza a Story no cronograma e busca as Tasks filhas direto no DevOps, associando o ID no padrão do cronograma ({id}:T; interno {id}:I) com prioridade e estimativa.\n" +
                     "• Merge com Cronograma: busca as Tasks de cada Story no TFS e faz o merge com as linhas (atualiza ID/prioridade/estimado/status e adiciona as que faltam), com barra de progresso, etapas e log copiável. Só traz uma Task Closed nova se ela já estiver na planilha (não recarrega Closed inéditas). Opcionalmente usa a IA (ação \"Merge de Arquivo Externo com Task\" da tela IA Geral) para casar nomes com diferenças de escrita — mostrando o de/para para confirmação antes de aplicar.\n" +
                     "• Load Task: carrega as Tasks do cronograma/TFS como o Merge, perguntando se traz também as Tasks já concluídas (Closed) — o padrão é Não.\n" +
                     "• Aplicar ao Cronograma: cria no cronograma as tasks do plano que não existem (sob a Story correspondente, pela mesma rotina da grid de Tasks; cria a Feature/Story internas que faltarem na cascata). A coluna Estimado HH aceita horas (8) ou dias (2d) e, quando zero/vazia, usa 1h. Story em New/0% pode ter a duração ajustada; iniciada, o período é preservado. Valida antes: EPIC informado precisa existir no cronograma, e a mesma Story não pode ter duas Tasks com o mesmo nome — se houver, nada é aplicado (a sincronização também bloqueia esses casos).\n" +
                     "• Após a sincronização do cronograma, o NX oferece atualizar o ID interno (:I) das Tasks criadas pela planilha para o ID do DevOps (:T) na própria planilha. Se ela estiver aberta no Excel (ou você adiar), um log \"<nome>_Sync_NXProject.xml\" é gravado na pasta do arquivo e aplicado automaticamente na próxima vez que a planilha for aberta no Task Plan — a sincronização conclui normalmente de qualquer forma.\n" +
                     "• Ctrl+clique nas células EPIC/Feature/Story abre a busca no cronograma; na Task, busca as filhas da Story no DevOps. Botão direito → Ver no cronograma foca a atividade no Gantt; Abrir no TFS/DevOps abre o work item pelo ID (:T) da célula.\n" +
                     "• Células de EPIC/Feature/Story/Task encontradas no cronograma ficam verdes; fora do pai correto ficam vermelhas até correção. As colunas ID Feature e ID Story são preenchidas conforme a digitação. O Status é uma combo com os estados do DevOps (a coluna legada \"Concluída (X)\" é migrada automaticamente)."),
                    ("IA no Task Plan (Ativar IA)",
                     "Marcando o checkbox 'Ativar IA', um painel de IA aparece acima da grade com dois botões:\n\n" +
                     "• Incluir tasks: cole a lista de atividades citadas em reunião (cada item com pelo menos a Story e o nome da Task). A IA casa o nome da Story com a Story do cronograma (tolerando abreviações e acentos) e inclui cada task na planilha com Aprovada = False, ID interno (:I), IDs de EPIC/Feature/Story preenchidos e DT_Registro de hoje — criando também a task interna no cronograma, no mesmo padrão do Aplicar. Se já existir task com o mesmo nome na mesma Story (na planilha ou no cronograma), não duplica: reporta 'já existe'.\n" +
                     "• Consultar task: digite/cole a descrição da atividade procurada; a IA localiza as linhas correspondentes e a grade seleciona e rola até elas.\n\n" +
                     "Nos dois casos uma janela de log ao vivo mostra cada passo (contexto, envio, resposta da IA, resultado item a item e o resumo). Antes de executar, os filtros ativos são limpos (senão as linhas ficariam escondidas) e linhas totalmente em branco são removidas.\n\n" +
                     "Os prompts são as ações 'Incluir Tasks na Planilha' e 'Consultar Task na Planilha' da tela IA Geral — podem ser ajustados lá, como as demais ações. Requer cronograma aberto (para incluir), planilha aberta e token de IA configurado.")
                },
                "Fluxo sugerido: monte o plano no Excel ou pelo Novo, use Merge com Cronograma para associar os IDs do DevOps e Aplicar ao Cronograma para criar o que faltar — sempre revisando o de/para quando usar a IA."
            ),
            (
                "Azure DevOps",
                "A integração com o Azure DevOps é o coração do NXProject: o backlog técnico vira cronograma gerenciável sem mudar o fluxo da equipe.",
                new()
                {
                    ("Importar o projeto",
                     "Arquivo → Importar → TFS / Azure DevOps abre a tela de importação. Informe:\n" +
                     "• URL da organização (ex: https://dev.azure.com/suaorg)\n" +
                     "• Nome do projeto (Team Project)\n" +
                     "• Personal Access Token (PAT) com permissão de leitura em Work Items\n" +
                     "• ID do work item raiz (tipo Project) — ou selecione da lista de projetos cadastrada"),
                    ("O que é importado",
                     "• Hierarquia Project → Epic → Feature → Story via links Child.\n" +
                     "• Estimativas: campo HH Estimado → duração em horas.\n" +
                     "• Datas: Data_Inicio e Data_Fim quando preenchidas no DevOps.\n" +
                     "• Responsável: System.AssignedTo → recurso do projeto.\n" +
                     "• Sprint: System.IterationPath → sprint do NXProject.\n" +
                     "• Ordem: Microsoft.VSTS.Common.StackRank.\n" +
                     "• Bloqueios: Tasks com tag Block marcam a Story como bloqueada."),
                    ("Log de importação",
                     "Ao final da importação, se houver avisos, uma janela de log é exibida com:\n" +
                     "• Stories cujo state foi corrigido automaticamente (ex: Closed com Tasks abertas → Active).\n" +
                     "• Predecessoras fora do escopo importado, com identificação se é Story ou outro tipo.\n" +
                     "• Filtros de Info / Aviso / Erro para facilitar a revisão."),
                    ("Abrir work item no DevOps",
                     "Na janela de Vínculo DevOps (clique no ID da tarefa na grade), o botão Abrir no DevOps ↗ abre o work item diretamente no browser. A janela também exibe as Tasks filhas vinculadas com ID, nome e estado."),
                    ("Campos Custom DevOps por Tipo",
                     "O NXProject suporta campos de classificação personalizados por tipo de work item (Epic, Feature, Story ou todos).\n\n" +
                     "Configure em ⚙ → Configuração Azure DevOps → aba 'Campos Custom DevOps':\n" +
                     "• Adicione campos para cada tipo (Epic, Feature, Story ou * para todos os tipos).\n" +
                     "• Cada campo tem um rótulo de exibição e o Reference Name do campo no DevOps (ex: Custom.Type).\n" +
                     "• Na importação, o valor do campo é lido do DevOps e armazenado na atividade.\n" +
                     "• Para editar o valor de uma atividade, clique com o botão direito → 'Campos Custom DevOps...'.\n" +
                     "• Se nenhum campo estiver configurado, um link direto para a janela de configuração é exibido.\n\n" +
                     "Os campos Custom DevOps são apenas de leitura/classificação — não são sincronizados de volta ao DevOps pela sincronização padrão."),
                    ("Tipo do EPIC (EPIC_TYPE) e aprovação de Task",
                     "Tipo do EPIC (EPIC_TYPE):\n" +
                     "• Na janela de Vínculo DevOps (clique no ID), quando o tipo é Epic aparece o painel 'Tipo do EPIC' com DELIVERY/BACKLOG. O valor é salvo no arquivo do projeto e enviado ao DevOps no Exportar/Sincronizar quando mudou.\n" +
                     "• EPIC marcado como BACKLOG fica FORA do total do projeto: não soma no HH do banner, não entra no % concluído nem nas datas início/fim exibidas no título do cronograma.\n" +
                     "• O campo (padrão EPIC_TYPE) vem habilitado por padrão na Configuração TFS/DevOps e pode ser desligado lá.\n\n" +
                     "Aprovação de Task (campo Approved):\n" +
                     "• O campo booleano de aprovação da Task no DevOps (padrão 'Approved' / Custom.Approved) também vem habilitado por padrão.\n" +
                     "• Com valor definido no cronograma/planilha, a sincronização grava o que está aqui — inclusive REMOVENDO a aprovação no DevOps quando o cronograma diz não aprovada. Sem valor definido, mantém o comportamento clássico de apenas oficializar a aprovação.\n" +
                     "• No Task Plan, a coluna Aprovada é enviada ao DevOps ao usar Gravar sel. TFS (grava só quando difere do que está lá).")
                },
                "Os nomes de campos (HH Estimado, Data_Inicio, Data_Fim) podem ser personalizados na área Campos (avançado) da tela de importação."
            ),
            (
                "Lista de Projetos",
                "A lista de projetos DevOps é um arquivo compartilhado entre a equipe com os projetos disponíveis para importação.",
                new()
                {
                    ("Para que serve",
                     "Em vez de cada pessoa lembrar o ID do work item raiz, você mantém um arquivo JSON com os projetos cadastrados (Nome + ID). Todos da equipe apontam para o mesmo arquivo.\n" +
                     "Acesse em Exibir → Projetos DevOps (lista)..."),
                    ("Gerenciar a lista",
                     "• Clique em Abrir / Criar para carregar ou criar um arquivo de lista.\n" +
                     "• Use os botões Adicionar, Editar e Excluir para manter os projetos.\n" +
                     "• O caminho do arquivo fica salvo nas configurações do usuário e recarregado automaticamente."),
                    ("Usar na importação",
                     "Na tela de importação (Arquivo → Importar → TFS / Azure DevOps), um ComboBox exibe os projetos da lista. Selecione o projeto e o campo de ID raiz é preenchido automaticamente.\n" +
                     "Use o botão ⚙ Gerenciar Portfólio... para abrir o cadastro diretamente pela tela de importação."),
                    ("Banner no cronograma",
                     "Após importar, o nome do projeto vinculado aparece em um banner azul claro no topo do cronograma, facilitando a identificação visual de qual projeto está aberto.")
                },
                "Salve o arquivo de lista em um diretório compartilhado (rede, OneDrive, SharePoint) para que toda a equipe use a mesma lista de projetos."
            ),
            (
                "Sincronização",
                "A sincronização envia de volta para o Azure DevOps as alterações feitas no cronograma: datas, horas, estado, sprint, tags e predecessoras.",
                new()
                {
                    ("Como sincronizar",
                     "Arquivo → Exportar → Sincronizar TFS / Azure DevOps... abre a tela de sincronização. Use as mesmas credenciais da importação.\n" +
                     "O processo compara o estado atual do cronograma com o DevOps e envia somente o que mudou."),
                    ("O que é sincronizado",
                     "• Título e descrição da Story/Feature.\n" +
                     "• Horas estimadas (HH Estimado).\n" +
                     "• Datas de início e fim (Data_Inicio, Data_Fim).\n" +
                     "• Estado (New, Active, Resolved, Closed).\n" +
                     "• Tags (inclusive tag Block para bloqueios).\n" +
                     "• Sprint (System.IterationPath).\n" +
                     "• Links de predecessora entre work items."),
                    ("Relatório de sincronização",
                     "Ao finalizar, uma janela exibe o resumo: itens atualizados, criados, sem alteração, avisos e erros. Use os filtros para focar nos problemas e copie o log se precisar registrar.")
                },
                "A sincronização respeita somente os campos configurados. A rastreabilidade de código, pull requests e pipelines do Azure DevOps não são afetados."
            ),
            (
                "Sincronizar com DevOps",
                "Para que o NXProject troque informações com o Azure DevOps, alguns campos personalizados precisam existir nos work items. Esta seção explica quais são, como criá-los e como ajustar os nomes caso a sua organização já use nomes diferentes.",
                new()
                {
                    ("O que é preciso para sincronizar",
                     "Para importar e sincronizar com o Azure DevOps você precisa de:\n\n" +
                     "1. Conexão: URL da organização, Team Project e um PAT (Personal Access Token) com permissão de leitura e escrita de work items.\n" +
                     "2. Um item raiz do projeto (veja 'Item raiz e hierarquia' abaixo).\n" +
                     "3. Os campos personalizados em Story, Feature e Epic (veja 'Campos obrigatórios' abaixo). A Task usa só campos padrão.\n\n" +
                     "Sem os campos personalizados a importação até funciona parcialmente, mas a sincronização de datas, alocação e o controle de concorrência (Sync_version/Sync_Name) não operam corretamente."),
                    ("Item raiz e hierarquia (tipo Project)",
                     "O NXProject monta o cronograma na hierarquia: Project → Epic → Feature → Story → Task.\n\n" +
                     "IMPORTANTE: 'Project' NÃO é um tipo de work item padrão do Azure DevOps (o padrão vai só até Epic). É um tipo personalizado que serve de 'container' acima dos Epics, agrupando o projeto inteiro. Muitas organizações criam esse tipo no processo.\n\n" +
                     "Como o item raiz é usado:\n" +
                     "• Importação manual: você informa o ID do item raiz na tela de importação. O NXProject importa os descendentes (Epic → Feature → Story → Task) desse item. O tipo do raiz NÃO precisa ser exatamente 'Project' — pode ser qualquer work item que seja pai dos Epics (inclusive um Epic, se quiser importar só ele).\n" +
                     "• Discovery (Portfólio → Discovery DevOps): lista automaticamente os work items do tipo 'Project' sem pai no Team Project. Para o Discovery automático funcionar, o tipo personalizado 'Project' precisa existir.\n\n" +
                     "Resumo: se a sua organização não usa um tipo 'Project', você ainda importa apontando o ID raiz para um Epic (ou outro container) — apenas o Discovery automático depende do tipo 'Project'.\n\n" +
                     "Campos no tipo 'Project': como ele fica no topo da hierarquia, crie nele os MESMOS campos personalizados do Epic (HH Estimado, Data_Inicio, Data_Fim, Sync_version, Sync_Name). Na prática o tipo 'Project' costuma ser uma cópia do Epic. O NXProject lê a data de início do projeto (Data_Inicio) direto do item raiz."),
                    ("Campos obrigatórios no Azure DevOps",
                     "O NXProject lê e escreve campos personalizados em Stories, Features e Epics. Os campos precisam existir no processo da organização e ser adicionados a cada tipo de work item que você quer sincronizar.\n\n" +
                     "Campos de planejamento (Story, Feature e Epic):\n" +
                     "• HH Estimado — horas estimadas. Tipo: Inteiro. Usado como duração no cronograma.\n" +
                     "• Data_Inicio — data de início planejada. Tipo: Data e Hora.\n" +
                     "• Data_Fim — data de término planejada. Tipo: Data e Hora.\n\n" +
                     "Campos exclusivos da Story:\n" +
                     "• Perc_Alocacao — percentual do dia útil dedicado a esta Story (afeta a data de término). Tipo: Decimal/Float (1–100, até 2 casas decimais).\n" +
                     "• Perc_Conclusao — percentual de conclusão (lido na importação, gravado na sincronização). Tipo: Inteiro (0–100).\n\n" +
                     "Campos de controle de concorrência (Story, Feature e Epic):\n" +
                     "• Sync_version — contador de versão, gerenciado automaticamente pelo NXProject. Tipo: Inteiro.\n" +
                     "• Sync_Name — usuário que fez a última sincronização, gerenciado automaticamente. Tipo: Texto (linha simples — não use o tipo Identidade).\n\n" +
                     "Campos da Task (nenhum campo personalizado é necessário):\n" +
                     "A Task usa apenas campos PADRÃO do Azure DevOps, que já existem no tipo Task — você não precisa criar nada:\n" +
                     "• HH Estimado → Original Estimate (Microsoft.VSTS.Scheduling.OriginalEstimate).\n" +
                     "• HH Atual → Completed Work (Microsoft.VSTS.Scheduling.CompletedWork).\n" +
                     "• Prioridade → Priority (Microsoft.VSTS.Common.Priority; o DevOps aceita 1–4).\n" +
                     "• Responsável → Assigned To; Estado → State; Categoria → Activity (Microsoft.VSTS.Common.Activity).\n" +
                     "As datas, o percentual de alocação e o Sync_version/Sync_Name NÃO se aplicam à Task — o planejamento (datas/duração) é derivado da Story pai."),
                    ("Controle de concorrência (Sync_version / Sync_Name)",
                     "Quando dois usuários sincronizam ao mesmo tempo, a última gravação poderia sobrescrever a primeira. O NXProject evita isso:\n\n" +
                     "• A cada sincronização que grava alguma alteração, Sync_version é incrementado em 1 e Sync_Name recebe o usuário Windows atual.\n" +
                     "• Ao sincronizar, o NXProject compara a versão lida na importação com a versão atual no DevOps. Se a versão do DevOps for maior, outro usuário salvou mais recentemente — o item é ignorado e marcado em vermelho no cronograma.\n" +
                     "• Itens em vermelho ficam destacados até a próxima reimportação. O log de sincronização indica quais itens tiveram conflito.\n" +
                     "• Clicar no item em vermelho na coluna de estado abre a janela de vínculo DevOps, que exibe um aviso de conflito com o botão ↓ Reimportar.\n\n" +
                     "Os campos Sync_version e Sync_Name devem estar presentes em todos os tipos de work item que você sincroniza: Story, Feature e Epic."),
                    ("Como criar os campos no Azure DevOps",
                     "Acesse: Configurações da Organização → Boards → Processo → selecione seu processo → abra o tipo de work item (Story, Feature ou Epic).\n\n" +
                     "1. Clique em Novo campo.\n" +
                     "2. Informe o nome (ex: 'HH Estimado'), selecione o tipo (Inteiro ou Data e Hora).\n" +
                     "3. Salve e repita para os demais campos.\n" +
                     "4. Adicione os campos ao layout do formulário se quiser que apareçam visíveis na tela de edição.\n\n" +
                     "Dica: crie os campos uma vez no nível do processo e adicione-os a Story, Feature e Epic — todos compartilham a mesma definição de campo."),
                    ("Personalizar os nomes dos campos",
                     "Se sua organização já usa nomes diferentes (ex: 'Estimativa_Horas' em vez de 'HH Estimado'), você pode ajustar os nomes que o NXProject usa sem mexer no Azure DevOps.\n\n" +
                     "Na tela de importação (Arquivo → Importar → TFS / Azure DevOps), expanda a seção Campos (avançado). Lá você encontra os campos configuráveis:\n\n" +
                     "• Nome do campo Horas Estimadas → padrão: 'Esforço Estimado'\n" +
                     "• Nome do campo Data de Início → padrão: 'Data_Inicio'\n" +
                     "• Nome do campo Data de Fim → padrão: 'Data_Fim'\n\n" +
                     "Digite o Reference Name exato do campo como cadastrado no Azure DevOps (não o rótulo de exibição). As configurações são salvas em config_nxproject.json e reusadas nas próximas importações."),
                    ("Verificar o nome de referência de um campo",
                     "Para descobrir o Reference Name de um campo existente no Azure DevOps:\n\n" +
                     "1. Acesse Configurações da Organização → Boards → Campos.\n" +
                     "2. Localize o campo e clique nele.\n" +
                     "3. O Reference Name aparece no detalhe — geralmente no formato 'Custom.NomeDoCampo'.\n\n" +
                     "É esse valor (ex: 'Custom.HHEstimado') que deve ser digitado na seção Campos (avançado) da tela de importação."),
                    ("Processo recomendado para novos projetos",
                     "1. Crie os três campos no processo da organização no Azure DevOps.\n" +
                     "2. No NXProject, abra Arquivo → Importar → TFS / Azure DevOps.\n" +
                     "3. Informe URL da organização, nome do projeto, PAT e ID do work item raiz.\n" +
                     "4. Se os nomes dos campos forem diferentes dos padrões, expanda Campos (avançado) e ajuste.\n" +
                     "5. Clique em Importar — o cronograma é gerado automaticamente.\n" +
                     "6. Planeje no NXProject e use Exportar → Sincronizar para enviar as datas de volta ao DevOps.")
                },
                "Os nomes dos campos são sensíveis a maiúsculas e minúsculas. Use o Reference Name exato do Azure DevOps, não o rótulo de exibição."
            ),
            (
                "Exportação",
                "Exporte o cronograma para outros formatos para compartilhar com stakeholders ou integrar com outras ferramentas.",
                new()
                {
                    ("Formatos disponíveis",
                     "• MS Project XML (.xml): compatível com Microsoft Project.\n" +
                     "• OpenProj (.pod): formato aberto para ferramentas como ProjectLibre.\n" +
                     "• Excel XML (.xml): tabela com todas as atividades, datas e recursos.\n" +
                     "• CSV: formato simples para análise em qualquer planilha."),
                    ("Quando usar cada formato",
                     "• Use MS Project XML para enviar o cronograma a stakeholders que usam MS Project.\n" +
                     "• Use Excel/CSV para relatórios, dashboards ou análises personalizadas.\n" +
                     "• Use OpenProj para ambientes sem licença de MS Project.")
                },
                "O CSV é o formato mais portátil para alimentar dashboards em Power BI, Tableau ou Google Sheets."
            ),
            (
                "Health Check",
                "O Health Check identifica problemas no cronograma que precisam de atenção antes que impactem a entrega.",
                new()
                {
                    ("O que é verificado",
                     "Exibir → Health Check do Projeto analisa todas as atividades e lista:\n" +
                     "• Atividades com data de Fim no passado e percentual menor que 100% (em atraso).\n" +
                     "• Atividades sem responsável alocado.\n" +
                     "• Atividades com predecessoras que criam dependências circulares.\n" +
                     "• Stories marcadas como bloqueadas (tag Block)."),
                    ("Como usar",
                     "• Abra o Health Check regularmente nas reuniões de status para revisar o estado do projeto.\n" +
                     "• Clique em uma atividade na lista para selecioná-la na grade e corrigir o problema.\n" +
                     "• Use como checklist antes de enviar um relatório para a gestão.")
                },
                "Execute o Health Check antes de cada reunião de status — ele revela em segundos o que está atrasado e sem dono."
            ),
            (
                "Assistente IA",
                "O Assistente de IA sugere estruturas de tarefas, decomposição de histórias e organização do cronograma a partir de uma descrição em linguagem natural.",
                new()
                {
                    ("Como acessar",
                     "Clique no botão IA na toolbar ou acesse IA → Assistente de Tarefas...\n" +
                     "Descreva o que precisa ser feito e o assistente sugere uma hierarquia de tarefas com estimativas."),
                    ("Casos de uso",
                     "• Criar a estrutura inicial de um projeto a partir de uma descrição.\n" +
                     "• Decompor uma Story grande em Tasks menores.\n" +
                     "• Gerar uma lista de atividades para um tipo de entrega recorrente (ex: setup de ambiente, testes de regressão).\n" +
                     "• Revisar se a decomposição atual está cobrindo todos os aspectos do escopo."),
                    ("Disponibilidade",
                     "O Assistente IA requer conexão com internet e chave de API configurada. Na edição Community, está disponível em modo limitado. A edição Enterprise inclui integração completa com OpenAI e Claude.")
                },
                "Use o Assistente IA para o primeiro brainstorm de tarefas — depois refine manualmente na grade com os detalhes do seu contexto."
            ),
            (
                "Baseline",
                "Registre um snapshot do cronograma para comparar o planejado original com a execução atual.",
                new()
                {
                    ("O que é o Baseline",
                     "O Baseline é uma fotografia do cronograma em um momento específico — datas de início, fim e horas estimadas de cada atividade.\n\n" +
                     "Ele permite responder: 'O projeto está adiantado ou atrasado em relação ao plano original?'"),
                    ("Como usar",
                     "Gestão → Baseline → Salvar Baseline: grava um arquivo .nxb ao lado do .nxp.\n" +
                     "Gestão → Baseline → Abrir Baseline: carrega e exibe a linha azul no Gantt.\n" +
                     "Gestão → Baseline → Desativar/Ativar Baseline: mostra ou oculta a linha sem apagar o .nxb.\n" +
                     "Gestão → Baseline → Limpar: remove o .nxb e apaga os dados em memória."),
                    ("Linha azul no Gantt",
                     "Quando o baseline está ativo, uma linha azul fina aparece abaixo de cada barra do Gantt indicando a data original de início e fim planejados.\n" +
                     "A diferença entre a barra atual e a linha azul indica avanço (barra à esquerda da linha) ou atraso (barra à direita)."),
                    ("Arquivo separado",
                     "O .nxb é salvo ao lado do .nxp mas NÃO faz parte do cronograma. Isso evita que dados de planejamento inicial polua o arquivo de trabalho.\n" +
                     "Ao compartilhar o .nxp com a equipe, o .nxb pode ser mantido localmente ou compartilhado separadamente."),
                    ("Carregamento automático",
                     "Por padrão, ao abrir um .nxp o NXProject carrega automaticamente o .nxb correspondente (se existir).\n" +
                     "Desative em Gestão → Baseline → Carregar automaticamente ao abrir para controlar isso manualmente.")
                },
                "Salve o Baseline logo após o kick-off do projeto — antes de qualquer ajuste de data. Esse snapshot é a referência para medir atrasos."
            ),
            (
                "Caminho Crítico",
                "Identifica as atividades que, se atrasarem, atrasam todo o projeto.",
                new()
                {
                    ("O que é o Caminho Crítico",
                     "O Caminho Crítico é a sequência de atividades que determina a menor data possível de conclusão do projeto.\n\n" +
                     "Na prática, são atividades com folga zero: se uma delas atrasar, a data final do projeto atrasa junto, a menos que outra atividade seja encurtada ou replanejada.\n\n" +
                     "O NXProject usa o método CPM (Critical Path Method), calculando datas mais cedo e mais tarde para descobrir a folga de cada atividade."),
                    ("Como interpretar",
                     "• Uma atividade crítica não é necessariamente a mais longa nem a mais importante do ponto de vista de negócio.\n" +
                     "• Ela é crítica porque está numa cadeia sem margem de atraso.\n" +
                     "• Uma atividade fora do caminho crítico pode atrasar até o limite da sua folga sem mover o prazo final.\n" +
                     "• Quando você muda duração, predecessoras, recurso ou % de alocação, o caminho crítico pode mudar."),
                    ("Como habilitar",
                     "Gestão → Caminho Crítico (checkbox): liga e desliga o destaque visual.\n" +
                     "Gestão → Caminho Crítico → Ver lista de atividades críticas: abre a janela com a grade completa.\n" +
                     "O estado (ligado/desligado) é salvo no arquivo .nxp."),
                    ("Borda vermelha no Gantt",
                     "Quando ativado, as atividades no caminho crítico exibem uma borda vermelha ao redor da barra no Gantt.\n" +
                     "A cor de fundo da barra não muda — só a borda é destacada para não confundir com outros alertas visuais."),
                    ("Janela de atividades críticas",
                     "Exibe uma grade com:\n" +
                     "• ID (xxx:T para TFS, xxx:I para interno)\n" +
                     "• Tipo, Nome, Início, Fim, Duração\n" +
                     "• Folga: 'Crítica' (vermelho) ou número de dias de folga (verde)\n" +
                     "• Predecessoras\n\n" +
                     "Filtros disponíveis: por nome, por recurso e checkbox 'Só críticas'."),
                    ("Interpretação da folga",
                     "Folga = dias que a atividade pode atrasar sem comprometer o prazo final.\n" +
                     "Folga 0 = crítica. Folga 5d = pode atrasar até 5 dias úteis sem impacto no projeto.")
                },
                "Foque primeiro nas atividades críticas ao replanejar. Um atraso de 1 dia em uma atividade com folga 0 equivale a atrasar o projeto inteiro."
            ),
            (
                "Diagrama de Atividades",
                "Visualize a hierarquia do projeto em forma de diagrama horizontal com dependências.",
                new()
                {
                    ("Como abrir",
                     "Gestão → Diagrama de Atividades. O diagrama é gerado automaticamente a partir da hierarquia do cronograma."),
                    ("Níveis e expansão",
                     "O diagrama exibe a hierarquia em colunas horizontais: Épico → Feature → Story → Task.\n" +
                     "Use os checkboxes no topo (Épico, Feature, Story, Task) para expandir ou recolher níveis.\n" +
                     "Clique em um nó para expandir/recolher seus filhos individualmente."),
                    ("Cores dos nós",
                     "• Azul escuro: Épico\n• Azul: Feature\n• Verde: Story\n• Roxo: Task\n" +
                     "• Marrom/laranja: atividade interna (xxx:I) — criada localmente, ainda não sincronizada com o DevOps."),
                    ("Identificação :T / :I",
                     "Cada nó exibe um badge no canto inferior direito:\n" +
                     "• 1234:T = work item do Azure DevOps com ID 1234\n" +
                     "• 45:I = atividade interna com ID sequencial local\n" +
                     "Após sincronizar com o DevOps, o :I é automaticamente promovido a :T."),
                    ("Tooltip ao passar o mouse",
                     "Passe o mouse sobre qualquer nó para ver: ID, Tipo, Estado, Início, Fim, HH Estimadas, % Concluído, Recurso e Sprint."),
                    ("Zoom e redimensionamento",
                     "• Ctrl + scroll: aumenta ou diminui o zoom (30% a 300%).\n" +
                     "• Arraste a borda direita de qualquer nó para redimensionar todas as caixas do mesmo nível.\n" +
                     "• Clique em '↺ Resetar zoom' para voltar a 100%.\n" +
                     "• '💾 Salvar preferências' grava as larguras e nível de expansão no .nxp para a próxima abertura.")
                },
                "Use o Diagrama de Atividades para comunicar o escopo do projeto para stakeholders — é mais legível que o Gantt para quem não tem familiaridade com cronogramas."
            ),
            (
                "Custo por Recurso",
                "Calcule o custo do projeto por recurso com suporte a modelo por hora ou por mês.",
                new()
                {
                    ("Configurar custo na tela Pessoas",
                     "Gestão → Custo — Pessoas abre a tela de Pessoas já com as colunas e o painel de custo habilitados.\n\n" +
                     "Colunas de custo:\n" +
                     "• Custo: 'Hourly' (por hora) ou 'Monthly' (por mês)\n" +
                     "• R$/hora: valor cobrado por hora trabalhada\n" +
                     "• R$/mês: valor mensal quando o recurso tem custo fixo mensal\n\n" +
                     "Recursos marcados como Internal não entram no custo do projeto."),
                    ("Base de horas usada no custo",
                     "O custo usa as horas de trabalho do recurso na atividade: HH Atual + HH Restante.\n" +
                     "A % de alocação não reduz o custo. Ela muda o prazo/calendário necessário para executar as horas.\n\n" +
                     "Exemplo: uma atividade de 8h com recurso a 10% continua tendo 8h de custo; a diferença é que ela ocupa mais dias no cronograma."),
                    ("Modelo por hora (Hourly)",
                     "Custo = horas do recurso na atividade × R$/hora.\n" +
                     "As horas são distribuídas nos meses conforme o período da atividade e a regra de HH Atual/HH Restante.\n" +
                     "Ideal para freelancers, consultores ou qualquer profissional contratado por demanda."),
                    ("Modelo por mês (Monthly)",
                     "O valor mensal informado para o recurso é rateado proporcionalmente pelas horas dele no projeto:\n\n" +
                     "Custo da atividade = (HH do recurso na atividade ÷ total de HH do recurso) × R$/mês\n\n" +
                     "Depois, esse custo é distribuído entre os meses conforme as horas da atividade em cada mês.\n" +
                     "Ideal para funcionários CLT, estagiários ou qualquer profissional com remuneração fixa mensal."),
                    ("Tela Custo por Recurso",
                     "Gestão → Custo por Recurso exibe a grade detalhada por Recurso → Épico → Feature.\n\n" +
                     "A grade mostra, para cada mês:\n" +
                     "• coluna CAPEX: custo das atividades classificadas como CAPEX\n" +
                     "• coluna OPEX: custo das atividades classificadas como OPEX\n" +
                     "• TOTAL, CAPEX tot. e OPEX tot. no fim da linha\n\n" +
                     "Os totais do recurso e o TOTAL GERAL somam todos os meses visíveis."),
                    ("CAPEX/OPEX e detalhamento",
                     "A classificação CAPEX/OPEX vem do Tipo Centro de Custo do Épico quando configurado; quando não há definição específica, usa a regra padrão do projeto.\n\n" +
                     "Clique em uma célula de custo para abrir o detalhamento. O detalhe mostra as Stories/atividades que formam aquele valor, com HH e custo calculado."),
                    ("Filtros e exportação",
                     "Use os filtros da lateral para selecionar recursos, Features ou mostrar apenas linhas com custo.\n" +
                     "A exportação leva a mesma visão da tela para Excel XML, mantendo os meses, CAPEX/OPEX e totais."),
                    ("Arquivo de custo (.nxcost) — criptografado",
                     "Os dados de custo NÃO são gravados no .nxp para preservar o sigilo salarial.\n\n" +
                     "Na tela Pessoas:\n" +
                     "• '💰 Salvar config de custo': escolha um local, defina uma senha → gera arquivo .nxcost criptografado.\n" +
                     "• '📂 Carregar config de custo': escolha o arquivo, informe a senha → os valores são aplicados aos recursos.\n\n" +
                     "O arquivo usa AES-256-GCM com PBKDF2-SHA256 (100.000 iterações). Sem a senha, o arquivo é indecifrável — guarde-a com segurança.")
                },
                "Use o modelo Monthly para funcionários fixos e Hourly para prestadores. O arquivo .nxcost pode ser mantido restrito ao gestor — não precisa acompanhar o .nxp no compartilhamento com a equipe."
            ),
            (
                "Configurações",
                "Personalize o comportamento do NXProject para o seu projeto e equipe.",
                new()
                {
                    ("Calendário de trabalho",
                     "Exibir → Calendário permite configurar:\n" +
                     "• Horas úteis por dia (padrão: 8h).\n" +
                     "• Dias da semana considerados úteis.\n" +
                     "• Feriados: adicione datas específicas que serão ignoradas no cálculo de prazo.\n" +
                     "O calendário é salvo localmente em %LocalAppData%\\NXProject.Community\\nxproject_calender.json."),
                    ("SPF — Story Points de Função",
                     "Exibir → SPF configura a tabela de conversão entre pontos de função e horas estimadas, usada para calcular duração a partir de métricas de complexidade."),
                    ("Configurações de conexão DevOps",
                     "As credenciais de conexão (URL da organização, Team Project, PAT) são salvas de forma segura usando DPAPI (criptografia do Windows ligada ao usuário). Marque Lembrar o token para não precisar digitar a cada importação.\n" +
                     "O caminho do arquivo de Lista de Projetos DevOps também é salvo nas configurações do usuário."),
                    ("Zoom padrão",
                     "O último zoom selecionado é salvo no arquivo .nxp e restaurado ao reabrir o projeto.")
                },
                "O calendário é o coração do cálculo de prazos — configure os feriados do seu país e da empresa antes de começar o planejamento."
            )
        };

        private static List<(string, string, List<(string, string)>, string?)> BuildTopicsEn() => new()
        {
            (
                "Overview",
                "NXProject is an IT project management tool that combines Azure DevOps rigor with the schedule view that managers and leaders need to make decisions.",
                new()
                {
                    ("Planning philosophy",
                     "NXProject plans down to the Story level, allowing Developers to freely detail and create tasks during execution.\n\n" +
                     "Inspired by the mathematical concept of degrees of freedom — used to model complex systems — NXProject applies the same principle to planning: it structures the complexity of technology without constraining the development process.\n\n" +
                     "Just as in a physical system where degrees of freedom define the space of possible movement, NXProject defines the boundaries (dates, resources, dependencies) and preserves the space the technical team needs to navigate autonomously within them."),
                    ("What NXProject does",
                     "NXProject imports the Azure DevOps hierarchy (Project → Epic → Feature → Story) and transforms that data into a schedule with dates, dependencies, resource allocation and a Gantt chart.\n" +
                     "The technical team stays in Azure DevOps as usual. NXProject is a reading and planning layer on top of that data.\n" +
                     "Nexus Xdata's goal is transparency: making it clear why each date, duration, percentage and alert appears in the schedule."),
                    ("Who uses it and for what",
                     "• Project Manager: schedule integrated with the backlog, delay alerts, dependency overview.\n" +
                     "• Scrum Master / RTE: sprint capacity, allocation conflicts, impact of date changes.\n" +
                     "• Tech Lead: view of Features and Stories with predecessors and hour estimates.\n" +
                     "• PMO: export to MS Project / Excel, consolidated project view."),
                    ("Project file (.nxp)",
                     "The schedule is saved in an .nxp file that can be shared. It stores all tasks, dates, dependencies, resources, sprint settings and the Azure DevOps link.")
                },
                "Use File → Import → TFS / Azure DevOps to create the schedule from your existing backlog."
            ),
            (
                "DevOps Hierarchy",
                "Understand the role of each Azure DevOps hierarchy level in NXProject and the rules governing fields, dates and sync.",
                new()
                {
                    ("Project (root item)",
                     "The Project is the schedule's root item: the work item that groups all the project's Epics.\n\n" +
                     "NOTE: 'Project' is NOT a standard Azure DevOps work item type — the standard tops out at Epic. It's a CUSTOM type the organization creates in its process to act as a container above the Epics.\n\n" +
                     "In NXProject:\n" +
                     "• It is not a schedule row nor a Gantt bar — it IS the open project.\n" +
                     "• The root item's title becomes the project name in NXProject.\n" +
                     "• The project start date is read from the root item's Data_Inicio field (when there is no sprint to anchor to).\n" +
                     "• The root item's Assigned To becomes the project owner.\n" +
                     "• The root item's children are imported following Epic → Feature → Story → Task.\n\n" +
                     "Fields: create on the 'Project' type the SAME custom fields as the Epic (Estimated HH, Data_Inicio, Data_Fim, Sync_version, Sync_Name) — in practice it is usually a copy of the Epic.\n\n" +
                     "On import: enter the root work item ID. Its type does not have to be exactly 'Project' — it can be any work item that parents the Epics (even an Epic, if you only want to import that one). Discovery (Portfolio → Discovery DevOps), however, automatically looks for work items of type 'Project', so that custom type must exist for it to work."),
                    ("Epic",
                     "An Epic represents a large initiative or strategic objective, typically spanning months.\n\n" +
                     "In NXProject:\n" +
                     "• It groups Features — its dates are derived from child Feature dates.\n" +
                     "• Has no own Estimated HH; duration is derived from children.\n" +
                     "• Can have predecessors to sequence large blocks of work.\n" +
                     "• Syncs with DevOps: State, title and dates (if configured).\n" +
                     "• Appears in the Gantt as a grouping bar (blue-grey color)."),
                    ("Feature",
                     "A Feature represents a deliverable business capability, typically grouping several Stories.\n\n" +
                     "In NXProject:\n" +
                     "• Groups Stories — dates and % complete are derived from children.\n" +
                     "• Can have predecessors between Features (delivery dependencies).\n" +
                     "• Estimated HH: computed as the sum of child Story hours.\n" +
                     "• Sprint alert is shown when the Feature spans more than one sprint without being complete.\n" +
                     "• Syncs State and dates with DevOps."),
                    ("Story (User Story / PBI)",
                     "The Story is the central planning unit in NXProject. It represents a unit of user value.\n\n" +
                     "In NXProject:\n" +
                     "• Has Estimated HH, Start Date, Finish Date, Sprint and an allocated Resource.\n" +
                     "• Dates are calculated by resource queue and HH duration.\n" +
                     "• % complete comes from the configured DevOps field (e.g. Perc_Conclusao).\n" +
                     "• Block: if the Story has the 'Block' tag in DevOps, it shows ⛔ in the schedule.\n" +
                     "• Child Tasks: can be fetched/expanded in the schedule via context menu.\n" +
                     "• Syncs: Estimated HH, dates, state, % complete, allocation and predecessors."),
                    ("Task",
                     "A Task represents a technical activity inside a Story, executed by a developer.\n\n" +
                     "In NXProject:\n" +
                     "• Main fields: Estimated HH (Original Estimate), Current HH (Completed Work), Priority, Assignee, State and Activity.\n" +
                     "• Estimated HH = 0 and Current HH = 0: proportional split of Story duration when added to schedule.\n" +
                     "• Priority determines execution order within the Story.\n" +
                     "• Task Grid: accessible via Story context menu → 'Task Grid (DevOps)'. Allows editing, reordering by drag-drop and syncing with DevOps.\n" +
                     "• Syncs: Title, Original Estimate, Completed Work, Priority, AssignedTo, State and Activity.")
                },
                "The Project → Epic → Feature → Story → Task hierarchy mirrors the Azure DevOps backlog ('Project' being a custom type at the top). NXProject plans down to the Story and provides Task visibility without constraining them."
            ),
            (
                "Tech Lead",
                "The Tech Lead window is the control point for technical Tasks: it fetches Tasks from DevOps per Story, lets you create, edit and sync Tasks without leaving NXProject.",
                new()
                {
                    ("Open from toolbar button",
                     "Click the 👷 Tech Lead icon in the toolbar to open the window in cascade mode:\n\n" +
                     "1. Select an Epic → the Features of that Epic are loaded automatically.\n" +
                     "2. Select a Feature → the Stories of that Feature are loaded.\n" +
                     "3. Select the desired Story (or '(All)' to see all Stories in the Feature) → click 🔍 Fetch Tasks.\n\n" +
                     "Tasks are fetched directly from Azure DevOps at that moment."),
                    ("Open from Story context menu",
                     "Right-click a Story in the grid → 'Task Grid (DevOps)...'.\n\n" +
                     "The window opens with the Story already pre-selected and Tasks are loaded automatically — no need to use the Epic and Feature combos."),
                    ("What Tech Lead can do",
                     "• View all Tasks for one or more Stories (ID, title, state, HH, priority, assignee).\n" +
                     "• Edit estimates (Estimated HH, Current HH), priority, assignee, state and activity type.\n" +
                     "• Create new pending Tasks — they will be created in Azure DevOps on the next sync.\n" +
                     "• Add Tasks to the schedule: DevOps Tasks not yet in the schedule can be added with one click.\n" +
                     "• Sync changes back to Azure DevOps ('Save Changes' button)."),
                    ("TKs column",
                     "When fetching Tasks in Tech Lead, the TKs column in the schedule is automatically updated with the count of Tasks found per Story.\n\n" +
                     "This lets you see at a glance which Stories already have technical Tasks created in DevOps, and which do not (value 0 shown in red).")
                },
                "Use cascade mode (toolbar) to plan Tasks for an entire Feature at once. Use the Story context menu for quick access to a specific Story during execution."
            ),
            (
                "Schedule",
                "The task grid is where you view and edit the project structure: hierarchy, dates, duration, resources, percent complete and dependencies.",
                new()
                {
                    ("Task hierarchy",
                     "The project is organized in levels: Feature → Story → Task or any grouping that makes sense. Child tasks are indented below the parent.\n" +
                     "• Use Edit → Create Subtask to indent a task.\n" +
                     "• Use Edit → Promote Task to move up one level.\n" +
                     "• Summary tasks (with children) calculate dates and duration automatically from their children.\n" +
                     "• Task rows are shown in a subtle gray when not selected, to distinguish them from EPIC/Feature/Story."),
                    ("Expand and collapse",
                     "• The Expand hierarchy button opens ONE LEVEL at a time: EPIC → Feature → Story → Task (Tasks already loaded in the schedule). Each click reveals the next level and collapses deeper ones.\n" +
                     "• Expand selected level opens the level of the selected item's siblings; Collapse all closes the whole hierarchy."),
                    ("Load Task ToDo",
                     "The Load Task ToDo toolbar icon loads from DevOps the Tasks of Stories below 100% completion (still to do) and applies them to the schedule. It brings ALL of the Story's Tasks, including already completed (Closed) ones, so duration and HH totals stay correct. It does not duplicate Tasks already in the schedule.\n" +
                     "• Ctrl + Click on the icon asks whether Stories already 100% complete should be included as well. Answering Yes loads the Tasks of EVERY Story linked to DevOps — useful to visually review the whole project on the Gantt. Answering No keeps the default behavior (below 100% only)."),
                    ("Duration and dates",
                     "• Dur.(h) column: enter in hours (e.g. 8) or in working days with d (e.g. 2d = 2 working days).\n" +
                     "• The Finish date is calculated automatically: Start + Dur.(h) respecting the work calendar.\n" +
                     "• To fix the Start date, type the date in the field — it is marked with 📌. If the typed date differs from the calculated one, a calendar opens for visual confirmation.\n" +
                     "• Use Ctrl + Click on the Start cell to open the calendar directly without typing.\n" +
                     "• To fix the Finish date, enter a date in the Finish field or drag the right edge of the Gantt bar with the right mouse button (on an already selected bar).\n" +
                     "• To remove the Start fix, type 0 in the Start field — the schedule recalculates the date automatically."),
                    ("Percent complete",
                     "• The % Compl. field records task progress (0 to 100).\n" +
                     "• In the grid, low percentages use dark text on a light background; higher percentages use white text over the filled area.\n" +
                     "• Summary tasks calculate percent as a weighted average of children's hours.\n" +
                     "• If the Finish date is in the past and the percentage is less than 100, the system alerts automatically in Health Check."),
                    ("Creating an activity",
                     "When adding a new activity (+ button or Edit → Add Task):\n" +
                     "• Type, Resource and Sprint are automatically copied from the selected activity at the time of the click.\n" +
                     "• The DevOps ID is set to 0, indicating the activity will be created in Azure DevOps on the next sync (Export → Sync).\n" +
                     "• Activities with Type = 'No DevOps' are never sent to Azure DevOps — they exist only for local schedule control.\n" +
                     "• Activities without a defined Type are automatically classified as 'No DevOps' to prevent accidental creation in DevOps."),
                    ("Updating an activity in DevOps",
                     "• Activities with DevOps ID > 0 are updated in Azure DevOps when running Export → Sync.\n" +
                     "• Activities with DevOps ID = 0 (and Type other than 'No DevOps') are created as new work items in Azure DevOps, and the returned ID is saved in the schedule.\n" +
                     "• Activities with Type 'No DevOps' are ignored by sync even if their ID = 0.\n" +
                     "• On Import: if an Azure DevOps work item has the same name as a local 'No DevOps' activity, NXProject automatically links the local activity to the imported item, updating its Type to match the DevOps type."),
                    ("Block tag",
                     "NXProject distinguishes two types of blocking visible in the Name column:\n" +
                     "• ⛔ BLOCK (red) — the Story/activity itself has the 'Block' tag. When both exist, only this icon is shown.\n" +
                     "• 🔴 BLOCK (yellow) — blocking inherited from a child Task in DevOps that has the 'Block' tag.\n\n" +
                     "To add or remove the Block on the Story, right-click the activity name and use the context menu.\n\n" +
                     "Block tag sync:\n" +
                     "• If the Story in NXProject has Block and DevOps does not → the tag is added in DevOps on sync.\n" +
                     "• If the Story in NXProject does not have Block and DevOps does → the tag is removed from DevOps on sync.\n\n" +
                     "On import, NXProject reads the Block tag from the Story itself and from child Tasks (reflected as inherited blocking)."),
                    ("Editing the activity name",
                     "The Name column requires a double-click to enter edit mode, preventing accidental edits when navigating through cells.\n\n" +
                     "All other columns (Start, Finish, Dur.(h), % Compl., etc.) still activate editing with a single click."),
                    ("TKs column",
                     "The TKs column (visible only in expanded mode) shows the count of child Tasks each Story has in Azure DevOps.\n\n" +
                     "• Numeric value: number of Tasks found in DevOps for this Story.\n" +
                     "• 0 in red: Story with no technical Tasks created in DevOps.\n" +
                     "• Empty cell: count not yet calculated (Story not imported from DevOps or not queried via Tech Lead).\n\n" +
                     "The value is updated automatically:\n" +
                     "• On Azure DevOps import.\n" +
                     "• When fetching Tasks in the Tech Lead window.\n" +
                     "• When adding Tasks to the schedule or creating Tasks in the Task Grid.")
                },
                "Enter Start and Dur.(h) — Finish is calculated from the calendar. For dependencies, use the Pred. column."
            ),
            (
                "Activity Dates",
                "An activity's dates are calculated from Start, duration in hours, work calendar, percent complete and cascade rules. In line with Nexus Xdata's transparency goal, this section details the rules used by the schedule.",
                new()
                {
                    ("Start, duration and finish",
                     "• Start is the date the activity begins in the schedule.\n" +
                     "• Dur.(h) is the total work duration: Current HH + Remaining HH.\n" +
                     "• Dur.(h), Current HH and Remaining HH are work hours, not calendar days.\n" +
                     "• Finish is calculated from Start + work hours, respecting working days, holidays, daily calendar hours and the resource allocation %.\n" +
                     "• The date shown in the Finish column is the visible end date; internally the calculation uses the end of the working period."),
                    ("% Complete, Current HH and Remaining HH",
                     "• Current HH is work already done; Remaining HH is work still needed.\n" +
                     "• Dur.(h) is the activity's total effort: Current HH + Remaining HH.\n" +
                     "• When % Complete changes, NXProject keeps Dur.(h) and splits that total: Current HH = Dur.(h) × % Complete; Remaining HH = Dur.(h) - Current HH.\n" +
                     "• Example: an 8h activity at 25% complete has Current HH = 2h and Remaining HH = 6h.\n" +
                     "• At % Complete = 0, Current HH becomes 0 and Remaining HH reverts to duration/original. At % Complete = 100, Current HH receives the total and Remaining HH becomes 0.\n" +
                     "• If an imported activity or an opened file has empty Current/Remaining HH but has duration and % Complete below 100%, NXProject fills those fields with the same rule.\n" +
                     "• Allocation % does not reduce the activity HH; it changes the calendar lead time. Example: 8h remaining at 10% allocation on an 8h/day calendar is still 8h of work, but spans about 10 working days."),
                    ("Fixed start",
                     "• When you type a date in the Start field, the Start is fixed and shown with the pin icon.\n" +
                     "• An activity with a fixed Start is not automatically shifted back by resource or virtual predecessor cascade.\n" +
                     "• To remove the Start fix, type 0 in the Start field — the schedule recalculates automatically.\n" +
                     "• If the fixed Start is in the future and the activity is marked 100%, Finish equals the fixed Start to avoid Finish before Start."),
                    ("Visual calendar for Start editing",
                     "A calendar opens automatically in two scenarios:\n\n" +
                     "• Ctrl + Click on the Start cell: opens the calendar positioned on the current activity date. Useful for changing the date without typing.\n\n" +
                     "• Typed date differs from calculated date: if the entered value doesn't match the valid schedule date, the calendar opens pre-selected on the nearest working day, for visual confirmation before applying.\n\n" +
                     "• Invalid date typed: if the text is not a recognizable date, the calendar opens positioned on the current calculated date.\n\n" +
                     "In the calendar:\n" +
                     "• Click the desired day to confirm immediately.\n" +
                     "• Press Enter to confirm the already-selected date.\n" +
                     "• Press Escape to cancel without changing the date."),
                    ("Fixed finish",
                     "• When editing the Finish column or dragging the right edge of the Gantt bar with the right button, Finish is fixed.\n" +
                     "• With fixed Finish, changes to duration or percent do not automatically recalculate the Finish date.\n" +
                     "• Use fixed Finish to record a negotiated date that may differ from the calculated duration.\n" +
                     "• If there is a difference between negotiated and calculated duration, the Gantt may indicate a visual conflict."),
                    ("0% complete",
                     "• When resetting % Compl. to 0%, NXProject considers no work has been done.\n" +
                     "• Current HH becomes 0.\n" +
                     "• Remaining HH reverts to Original HH.\n" +
                     "• Finish is recalculated as Start + Remaining HH, unless Finish is fixed.\n" +
                     "• Cascade may reposition following activities of the same resource, but should not use Features or summary tasks as queue references."),
                    ("100% complete",
                     "• When marking % Compl. as 100%, NXProject considers the activity closed.\n" +
                     "• Current HH receives the total activity duration.\n" +
                     "• Remaining HH becomes 0.\n" +
                     "• Calculated Finish is Start + total duration. If this Finish falls in the future, it is capped to today, since an activity cannot close in the future.\n" +
                     "• Exception: if Start is fixed to a future date, Finish equals the fixed Start."),
                    ("Predecessor and resource cascade",
                     "• Explicit predecessors move the activity to the next working day after the predecessor's visible end.\n" +
                     "• Cascade uses topological sort: a dependent activity is only recalculated after its predecessors are processed.\n" +
                     "• The virtual predecessor organizes activities of the same resource, parent and level to avoid work overlap.\n" +
                     "• Virtual predecessor reference must be another leaf activity (Story/Task), never a Feature, Epic or summary task.\n" +
                     "• Summary tasks are always recalculated to reflect children's dates, duration and percent.")
                },
                "Practical rule: edit Start and Dur.(h) to plan; use % Compl. to record progress. Fixes are deliberate exceptions to automatic calculation."
            ),
            (
                "Gantt Chart",
                "The Gantt displays bars for each activity in time, with milestones, dependency arrows, sprints and today's line.",
                new()
                {
                    ("Navigation and zoom",
                     "• Use the zoom button in the toolbar to switch between Day, Week, Sprint, Month, Quarter and Semester.\n" +
                     "• Scroll horizontally to navigate in time.\n" +
                     "• Enable the magnifier button in the toolbar and move the mouse over the Gantt to inspect dates, bars and dependencies up close.\n" +
                     "• The vertical red line indicates today."),
                    ("Day header modes",
                     "The calendar button (📅) in the toolbar cycles between three modes:\n" +
                     "• Off: default header by sprint and month.\n" +
                     "• Day 1: highlights Monday with the day number; Wednesday and Friday in brighter blue.\n" +
                     "• Day 2: shows the unit digit of each day. Days 10, 20 and 30 are highlighted in blue, orange and green respectively — making it easier to read dates without cluttering the header."),
                    ("Dragging bars",
                     "• Left button + drag: moves the activity's Start date (only for activities not yet started).\n" +
                     "• Right button + drag (on the already-selected bar): adjusts the Finish date without changing the hour estimate. On release, Finish is fixed (📌).\n" +
                     "• Dependent activities shift automatically when a predecessor is moved."),
                    ("Bars and colors",
                     "• Light blue bar: normal activity.\n" +
                     "• Orange bar: selected activity.\n" +
                     "• Dark central strip: percent complete, MS Project style.\n" +
                     "• Subtle dark line at the base: Current HH proportional to total Current + Remaining HH.\n" +
                     "• Golden diamond: milestone.\n" +
                     "• Light blue-grey bar: summary (Feature/Epic).\n" +
                     "• Red borders or highlights indicate conflict, delay or negotiated duration differing from calculated.")
                },
                "Click a bar to select the task in the grid. Dependency arrows show the critical path visually."
            ),
            (
                "Predecessors",
                "Predecessors define that an activity can only start after another finishes, creating the dependency chain of the project.",
                new()
                {
                    ("How to set up",
                     "Click the Pred. field of the activity that depends on another. A selection window opens with all available leaf activities.\n" +
                     "• Use search to find by name or code.\n" +
                     "• Check one or more activities with the checkbox.\n" +
                     "• The top panel shows already-checked predecessors before confirming."),
                    ("Predecessors outside the list",
                     "When an activity imported from DevOps has predecessors pointing to items outside the imported scope, they appear in yellow in the selector labeled 'outside filtered list'.\n" +
                     "• Each external predecessor can be removed individually with the ✕ Remove button.\n" +
                     "• Predecessors inside the list are checked normally via checkbox."),
                    ("Effect on the schedule",
                     "When you move an activity in the Gantt, all activities that depend on it (directly or indirectly) shift automatically by the same number of days.")
                },
                "To chain activities in sequence at once, select several and use Edit → Link Tasks Sequentially."
            ),
            (
                "Resources",
                "Resources are the people allocated to activities. NXProject imports assignees from Azure DevOps and lets you manage workload per person.",
                new()
                {
                    ("Register resources",
                     "Go to View → People to manage the project's resource list. Each person can have a name and email.\n" +
                     "When importing from Azure DevOps, the System.AssignedTo field is automatically imported as a resource."),
                    ("Allocation by Sprint",
                     "Manage → Allocation by Sprint shows the workload per person in each period (sprint or week), allowing you to identify overloads before they become problems.\n" +
                     "• Red cells indicate overload (more than 100% of daily capacity).\n" +
                     "• Green cells indicate available capacity.\n" +
                     "• Capacity considers calendar hours/day, the resource's configured hours/day and the activity allocation %.\n" +
                     "• Current HH and Remaining HH remain work hours; allocation % defines how many days are needed to fit those hours.\n\n" +
                     "The Allocation Map (View → Allocation Map) shows hours per resource × project × month with the following tabs:\n" +
                     "• Hours by Project — hours per resource per project per month.\n" +
                     "• Distribution by Person — consolidated view across all projects per resource.\n" +
                     "• Stories by Resource — breakdown of each story per resource and month.\n" +
                     "• Rateio (Apportionment) — % that each project represents of the resource's total hours in that month.\n\n" +
                     "How hours are calculated per month:\n" +
                     "Each activity's hours are distributed proportionally across the months it spans. If a story runs from Jan 10 to Feb 20 (42 days), 22 days fall in January and 20 in February; hours are split in that ratio (22/42 in Jan, 20/42 in Feb).\n\n" +
                     "The hours shown in each cell are Current HH (already worked) + Remaining HH (forecast). In the Hours by Project tab, use the 'Only current HH (allocated)' checkbox to see only hours already executed, excluding the future estimate."),
                    ("Resource filter",
                     "The 👤 button in the toolbar filters the Gantt and the grid to show only the activities of a specific person — useful in individual status meetings.")
                },
                "Use the resource filter in the toolbar to show only one person's activities during a status meeting."
            ),
            (
                "Allocation Map",
                "The Allocation Map (View → Allocation Map) consolidates hours from multiple projects by resource and month, helping you spot overloads and plan capacity.",
                new()
                {
                    ("Available tabs",
                     "• Hours by Project — hours per resource per project per month. Click a cell to see the stories for that resource in that month.\n" +
                     "• Distribution by Person — consolidated view across all projects per resource, with totals and capacity percentage.\n" +
                     "• Stories by Resource — details each story with Total HH (Current + Remaining), % completion, start and finish.\n" +
                     "• Rateio (Apportionment) — shows what % each project represents of the resource's total hours in that month.\n" +
                     "• Internal — separate view for internal resources, when present."),
                    ("Hours per month criterion",
                     "Each activity's hours are distributed proportionally across the months it spans.\n\n" +
                     "Example: a story from Jan 10 to Feb 20 has 22 days in January and 20 days in February; if the story has 42 total hours, 22h go to January and 20h to February (ratios 22/42 and 20/42).\n\n" +
                     "Normal mode shows Current HH + Remaining HH (total planned duration). The 'Only current HH (allocated)' checkbox appears only in the Hours by Project tab and shows only realized hours in that tab."),
                    ("Current HH and Remaining HH by month",
                     "The Allocation Map separates work already done from work still remaining before distributing hours on the calendar:\n\n" +
                     "• Current HH is distributed from Start to today (or to Finish when the activity is 100%).\n" +
                     "• Remaining HH is distributed from the next period through the activity Finish.\n" +
                     "• Normal mode adds both parts: Current HH + Remaining HH.\n" +
                     "• The 'Only current HH (allocated)' checkbox is an analysis mode for the Hours by Project tab; Distribution by Person, Stories by Resource, Rateio and Internal always use Current HH + Remaining HH.\n" +
                     "• When multiple resources are assigned to the same activity, Current HH is split by each assignment's Remaining HH proportion; if that base is missing, allocation % is used.\n\n" +
                     "This prevents work already done from being pushed into future months, and remaining work from being counted in past months."),
                    ("Story with Tasks from another person (HH decomposition)",
                     "When a Story has Tasks from resources other than the responsible, the Story's HH is DECOMPOSED among the people — the total stays equal to the Story HH (it does not inflate the project):\n\n" +
                     "• Each Task credits its estimated HH to the Task's resource.\n" +
                     "• The Story responsible keeps the REMAINDER: Story HH minus the sum of the Tasks. So they don't lose everything by delegating (they still review the work), while the total closes at the Story HH.\n" +
                     "• Cap: no Task can exceed the Story HH. If the Tasks sum exceeds the Story HH, Tasks are cut proportionally (Story HH ÷ sum of Tasks) and the responsible gets 0.\n" +
                     "• If the responsible has no Task of their own, they keep the remainder (with no other Tasks, they keep the whole Story).\n" +
                     "• If the Story has no estimated HH, nothing is cut — Tasks show their own HH.\n\n" +
                     "The math uses the model (not the visible tree), so Tasks from another resource count even when the Story is collapsed in the schedule. Applies to the Allocation Map and to Allocation by Sprint."),
                    ("Task summary per resource (stored in the file)",
                     "Tasks live in DevOps and are not loaded into the schedule. To decompose the Story HH even without the Tasks loaded, NXProject stores a per-resource summary in the file (.nxp):\n\n" +
                     "• Each entry has resource, hours, task count and state (Active/Closed/New/Other), grouped by resource + state.\n" +
                     "• Hours per task: if the Task is Closed it uses Completed (current HH); otherwise Estimate.\n" +
                     "• The summary is filled/updated on Sync and also on the Allocation Map import (which reads DevOps down to the task level).\n" +
                     "• Clicking the hours on the Stories by Resource tab shows a composition grid with Type (Story/Task), the Story, the task count (Task = resource's task count; Story = 1) and a button to open the story's task grid (Tech Lead)."),
                    ("Filter: Story % > 0 and Task Active/Closed",
                     "The Allocation Map and Allocation by Sprint only consider work in progress:\n\n" +
                     "• A Story is included when its completion % > 0.\n" +
                     "• A Task/summary is included when its state is Active or Closed (legacy files with no state still count).\n\n" +
                     "On the Stories by Resource tab, the 'Stories with no % (off the map)' flag shows in red the Stories excluded by % = 0 — useful to review what hasn't started.\n\n" +
                     "On Allocation by Sprint, the 'Include planned (Story % 0 / Task New)' flag also considers Stories at % = 0 and New tasks, to see the planned sprint distribution."),
                    ("Capacity percentage",
                     "The percentage shown beside hours in capacity tabs is calculated against the monthly calendar and resource capacity: working hours/day × working days in the month, considering the person's configuration.\n\n" +
                     "In the Rateio tab, the % represents that project's share of the resource's total hours in the month — not relative to full capacity."),
                    ("Allocation % and finish date",
                     "Clicking a task's allocation % opens a dialog that lets you:\n" +
                     "• Enter HH/day to calculate the % (e.g. 4h/day = 50%).\n" +
                     "• Enter a desired finish date: NXProject automatically calculates the allocation % needed to complete the total hours (Current + Remaining) by that date.\n" +
                     "  Formula: % = Total Hours ÷ Working hours(Start → Finish) × 100.\n" +
                     "  This lets you reverse-engineer how much dedication the resource needs to meet a specific deadline.\n\n" +
                     "Important: allocation % changes the calculated finish date, not the total HH of the activity. An 8h activity still has 8h; at 10% allocation on an 8h/day calendar, it takes about 10 working days.")
                },
                "Filter projects with 'Select Projects' and adjust the analysis period — zero columns are automatically hidden when 'Hide zero rows/columns' is checked."
            ),
            (
                "Sprints",
                "NXProject supports Azure DevOps sprints and allows you to configure local sprints to organize the schedule into iterations.",
                new()
                {
                    ("Configure sprints",
                     "View → Sprint sets the first sprint number, duration in days and numbering mode (sequential, even or odd).\n" +
                     "If the project was imported from Azure DevOps, sprints are read from System.IterationPath and created automatically."),
                    ("Assign activities",
                     "The Sprint column in the grid lets you move Stories and Features between sprints. When you change the sprint, the Start date is recalculated to the start of that sprint.\n" +
                     "• To remove the sprint association and use a fixed date, just enter a date in the Start field."),
                    ("View in Gantt",
                     "The Gantt shows sprints in the bottom header, with numbering and alternating colors. Sprint or Week zoom makes iterations more visible."),
                    ("Fix sprints out of period",
                     "The 🏁 button on the toolbar (and Manage → Fix sprints out of period) reassigns activities whose sprint does not match the period the activity is actually in.\n\n" +
                     "Reference date (where the activity is):\n" +
                     "• 0% complete → Start date.\n" +
                     "• In progress (>0% and <100%) → % position: Start + (% × working duration).\n" +
                     "• 100% → Finish date.\n\n" +
                     "Sprint choice when fixing:\n" +
                     "• If a sprint contains the reference date → use it (highlight clears).\n" +
                     "• Otherwise → the last sprint starting on/before the reference (closest and BEFORE) → stays highlighted.\n\n" +
                     "Highlight in the Sprint column:\n" +
                     "• Orange: the assigned sprint does not contain the reference date (adjustable by the button).\n" +
                     "• Italic green text: 100% activity delivered early, in an earlier-period sprint (not adjusted).\n" +
                     "• Blue text: sprint in progress (contains today) with the % out of period (not adjusted).\n\n" +
                     "When it does NOT suggest (highlight only): the reference date has already reached the current sprint (reference ≥ sprint start) and the sprint has already ended (end ≤ today) — the activity passed through the sprint. The cell stays orange in the schedule, but the adjust window does not list it.\n\n" +
                     "The Sprint cell hint in the schedule shows the activity's reference date (pace).\n\n" +
                     "Before applying, a window shows Epic, Feature, Story, Activity Period, Ref. (pace), Status, % Compl., Current Sprint and Adjusted Sprint for review; nothing changes until you click Apply. Dates are not moved — only the sprint label. Then sync with DevOps to persist.")
                },
                "The Sprint column is especially useful for replanning — move Stories between sprints and see the schedule impact immediately."
            ),
            (
                "Project Progress and S-Curve",
                "Manage → Project Progress and S-Curve shows the project's evolution over time comparing PLANNED vs ACTUAL — the classic Earned Value Management (EVM) S-Curve, with one point per week.",
                new()
                {
                    ("The two lines (EVM foundation)",
                     "This is the Earned Value Management S-Curve (PMI/PMBOK standard). Y axis = cumulative % of HH; X axis = time (one point per week, on Monday).\n\n" +
                     "• Original HH (Planned) = PV (Planned Value): baseline of how much should be done, distributed over each Story's Start→Finish dates. Always includes all Stories.\n" +
                     "• Actual HH (completed) = EV (Earned Value): HH × completion % of what was delivered.\n" +
                     "• The distance between the lines is the SV (Schedule Variance): planned ahead = behind; actual ahead = ahead of schedule."),
                    ("Distribution by date, not by sprint",
                     "Hours are distributed over each Story's Start→Finish range, not dropped whole into a sprint — the sprint is just the control window. Milestones (zero duration) land in the week of their date, never repeated."),
                    ("Actual: real past, future projected by velocity",
                     "The actual line is computed in two parts, split at TODAY:\n" +
                     "• Past (up to today): only COMPLETED work (HH × %). Undone work never appears in the past.\n" +
                     "• From today on (only with 'Include Remaining HH'): the remainder is delivered at the historical VELOCITY = completed HH ÷ elapsed working days. It does not use the (optimistic) schedule pace — it looks at the past to project the future.\n\n" +
                     "Projected completion = today + (remaining HH ÷ velocity). If it goes past the planned end, the axis adds weeks up to that date. Same logic as throughput forecasting (Little's Law / agile burn-up): the gap between the real projection and the plan forms the 'belly'."),
                    ("The checkboxes",
                     "• Include Remaining HH: turns on the velocity projection of the remainder (otherwise the line shows only completed work and plateaus).\n" +
                     "• Include planned (Story % 0 / Task New): also brings not-started Stories into the actual — only meaningful together with 'Include Remaining HH' (a 0%-complete Story delivers 0). With both, the projection covers all remaining work and the axis extends to the projected completion."),
                    ("Weekly points and sprint ruler",
                     "The curve has one point per WEEK (Monday) to keep the belly smooth. On top, a ruler shows sprints as a time marker: dividers at each sprint start with the name on top — configured sprints in blue and PROJECTED sprints (S8, S9… proj.) in gray italic, created when the work runs past the last sprint. The summary shows 'Sprints: N config. + M proj.', so you can see how many you already have and how many more you'll need."),
                    ("Base line (3rd line, optional)",
                     "Check 'Show base line' to compare against a saved project snapshot. If none is loaded, the 'Open baseline…' button appears to pick a .nxp file.\n\n" +
                     "The 3rd line (dashed green) uses the baseline Stories' Current + Remaining HH distributed over their dates. The current project's Original HH (blue) does NOT change — it is the frozen baseline; the green line is the snapshot reference to compare planned vs re-planned."),
                    ("Other tabs",
                     "• Delays by Resource — matrix of delayed activities by person and delay range.\n" +
                     "• Delayed Activities — full list of the delayed ones, with justification (click the ID).\n" +
                     "• Blocked — activities flagged as blocked.")
                },
                "Check 'Include Remaining HH' to see the projection at your real velocity — the belly shows how much the current pace pushes delivery away from the plan."
            ),
            (
                "Plan Task Sheet",
                "The Plan Task Sheet is an Excel-style grid for planning each EPIC's Tasks in a native .xlsx file — editable by both NXProject and Excel — integrated with the open schedule and Azure DevOps.",
                new()
                {
                    ("File (native .xlsx)",
                     "The toolbar button opens the Plan Task Sheet. New creates the sheet with the required columns (EPIC, Feature, Story, Task, ID Devops, Priority, Estimated, Status, Description, Notes) — optionally pre-filled from the current schedule or the schedule + TFS Tasks; Open loads any .xlsx (the title row is recognized automatically, even below a summary block); Save 💾 writes back preserving the rest of the sheet (summary, formulas). Schedule-linked columns are created if missing and cannot be deleted.\n" +
                     "In ⚙ Settings: default files folder and the SharePoint fields (Entra ID + Graph, future integration). The last file reopens automatically; without a file, the grid is built from the open schedule."),
                    ("Excel-style editing",
                     "• Ctrl+Z undoes the latest changes (editing, paste, colors, rows and columns — up to 10 levels); also on the right-click menu → Undo.\n" +
                     "• Block cell selection (Shift/drag) with Copy/Paste via Ctrl+C/Ctrl+V or the right-click menu — including to/from Excel; pasting past the end creates new rows.\n" +
                     "• Rows numbered like Excel; right-click: insert row above/below, delete row(s), clear cells and Cell color (palette) — colors are read from Excel (including theme colors) and written back.\n" +
                     "• Right-click the header: Filter... (dialog with search and per-value checkboxes), insert/rename/delete column and width adjustments; in the cell menu, fit row height to text and fit the whole sheet."),
                    ("Where to keep the file (OneDrive/SharePoint)",
                     "• Local or network folder: works directly; if the file is open in Excel, NXProject warns on save (one editor at a time).\n" +
                     "• SharePoint via synced OneDrive (recommended): on the SharePoint site click \"Sync\" — the library becomes a local folder and Task Plan opens the .xlsx from there normally; OneDrive handles upload and versioning. Point the Default folder (⚙) to it. The one-editor-at-a-time rule applies.\n" +
                     "• SharePoint directly (https URL): Windows cannot open that address (WebDAV blocked by modern authentication) — NXProject guides you when the URL is pasted. Direct access with co-authoring will require the App registered in Entra ID (Tenant/Client ID in ⚙; integration under development) — no client secret, the user signs in via MSAL.\n" +
                     "• No file at all: to just review, use New → \"From the schedule + TFS Tasks\" — everything loads into the grid for review and the .xlsx is only created if you save."),
                    ("New and moved columns — the \"xx#_\" prefix",
                     "On save, each column goes back to the SAME physical cell in the sheet, preserving the summary block and formulas that point to fixed columns. Therefore:\n" +
                     "• A column created on screen is saved at the END of the sheet, and a regular column that was moved (dragging its header) stays in its original physical cell.\n" +
                     "• So the screen remembers where to display them, their header is saved as \"position#_Name\" (e.g. \"2#_Notes\" = 2nd column in the view). On reopen, the prefix is stripped and the column returns to the right position in the grid — in Excel you will see the prefix in the title, and it is safe to keep it.\n" +
                     "• Schedule-linked columns (EPIC, Feature, Story, Task, ID Devops, Priority, Estimated, Status) have a fixed position and always a clean name — they never get the prefix; to move them in the sheet, do it in Excel."),
                    ("DevOps and schedule integration",
                     "• Find Task in DevOps: for rows without an ID, finds the Story in the schedule and fetches its child Tasks directly from DevOps, assigning the schedule ID pattern ({id}:T; internal {id}:I) with priority and estimate.\n" +
                     "• Merge with Schedule: fetches each Story's Tasks from TFS and merges them with the rows (updates ID/priority/estimated/status and adds missing ones), with a progress bar, steps and a copyable log. A new Closed task is only added if it's already in the sheet (Closed tasks aren't reloaded). Optionally uses AI (the \"Merge de Arquivo Externo com Task\" action from the General AI screen) to match names with writing differences — showing the from/to list for confirmation before applying.\n" +
                     "• Load Task: loads the schedule/TFS Tasks like Merge, asking whether to also bring the already completed (Closed) tasks — default No.\n" +
                     "• Apply to Schedule: creates in the schedule the plan tasks that don't exist (under the matching Story, via the same routine as the Task grid; creates the missing internal Feature/Story in the cascade). The Estimated HH column accepts hours (8) or days (2d), and when zero/empty uses 1h. A Story in New/0% may have its duration adjusted; once started, its period is preserved. Validates first: an informed EPIC must exist in the schedule, and the same Story cannot have two Tasks with the same name — if so, nothing is applied (sync blocks these cases too).\n" +
                     "• After the schedule sync, NX offers to update the internal ID (:I) of the Tasks created from the sheet to the DevOps ID (:T) in the sheet itself. If it is open in Excel (or you defer), a log \"<name>_Sync_NXProject.xml\" is saved in the file's folder and applied automatically the next time the sheet is opened in Task Plan — sync completes normally regardless.\n" +
                     "• Ctrl+click on EPIC/Feature/Story cells opens the schedule search; on Task, it searches the Story's children in DevOps. Right-click → View in schedule focuses the activity in the Gantt; Open in TFS/DevOps opens the work item by the cell's ID (:T).\n" +
                     "• EPIC/Feature/Story/Task cells found in the schedule turn green; outside the correct parent they turn red until fixed. The ID Feature and ID Story columns are filled as you type. Status is a combo with the DevOps states (the legacy \"Concluída (X)\" column is migrated automatically)."),
                    ("AI in Task Plan (Enable AI)",
                     "Checking the 'Enable AI' checkbox shows an AI panel above the grid with two buttons:\n\n" +
                     "• Include tasks: paste the list of activities from a meeting (each item with at least the Story and the Task name). The AI matches the Story name against the schedule Stories (tolerating abbreviations and accents) and adds each task to the sheet with Approved = False, an internal ID (:I), EPIC/Feature/Story IDs filled and today's DT_Registro — also creating the internal task in the schedule, using the same pattern as Apply. If a task with the same name already exists in the same Story (in the sheet or the schedule), it is not duplicated: it is reported as 'already exists'.\n" +
                     "• Find task: type/paste the description of the activity; the AI locates the matching rows and the grid selects and scrolls to them.\n\n" +
                     "In both cases a live log window shows every step (context, request, AI response, per-item result and the summary). Before running, active filters are cleared (otherwise the rows would be hidden) and fully blank rows are removed.\n\n" +
                     "The prompts are the 'Incluir Tasks na Planilha' and 'Consultar Task na Planilha' actions of the General AI screen — adjustable there like the other actions. Requires an open schedule (to include), an open sheet and an AI token configured.")
                },
                "Suggested flow: build the plan in Excel or via New, use Merge with Schedule to link the DevOps IDs and Apply to Schedule to create what's missing — always reviewing the from/to list when using AI."
            ),
            (
                "Azure DevOps",
                "The Azure DevOps integration is the heart of NXProject: the technical backlog becomes a manageable schedule without changing the team's workflow.",
                new()
                {
                    ("Importing the project",
                     "File → Import → TFS / Azure DevOps opens the import screen. Enter:\n" +
                     "• Organization URL (e.g. https://dev.azure.com/yourorg)\n" +
                     "• Project name (Team Project)\n" +
                     "• Personal Access Token (PAT) with Work Items read permission\n" +
                     "• ID of the root work item (Project type) — or select from the saved project list"),
                    ("What is imported",
                     "• Hierarchy Project → Epic → Feature → Story via Child links.\n" +
                     "• Estimates: Estimated HH field → duration in hours.\n" +
                     "• Dates: Data_Inicio and Data_Fim when filled in DevOps.\n" +
                     "• Assignee: System.AssignedTo → project resource.\n" +
                     "• Sprint: System.IterationPath → NXProject sprint.\n" +
                     "• Order: Microsoft.VSTS.Common.StackRank.\n" +
                     "• Blocks: Tasks with the Block tag mark the Story as blocked."),
                    ("Import log",
                     "At the end of import, if there are warnings, a log window is shown with:\n" +
                     "• Stories whose state was automatically corrected (e.g. Closed with open Tasks → Active).\n" +
                     "• Predecessors outside the imported scope, identified whether they are Stories or other types.\n" +
                     "• Info / Warning / Error filters to ease review."),
                    ("Open work item in DevOps",
                     "In the DevOps Link window (click the task ID in the grid), the Open in DevOps ↗ button opens the work item directly in the browser. The window also shows child Tasks linked with ID, name and state."),
                    ("Custom DevOps Fields by Type",
                     "NXProject supports custom classification fields per DevOps work item type (Epic, Feature, Story or all types).\n\n" +
                     "Configure under ⚙ → Azure DevOps Configuration → 'Custom DevOps Fields' tab:\n" +
                     "• Add one or more fields for each type (Epic, Feature, Story or * for all types).\n" +
                     "• Each field has a display label and the DevOps Reference Name (e.g. Custom.Type).\n" +
                     "• On import, the field value is read from DevOps and stored on the activity.\n" +
                     "• To edit a value, right-click the activity → 'Custom DevOps Fields...'.\n" +
                     "• If no fields are configured, a direct link to the configuration window is shown.\n\n" +
                     "Custom DevOps Fields are read-only / classification-only — they are not written back to DevOps by the standard sync."),
                    ("EPIC type (EPIC_TYPE) and Task approval",
                     "EPIC type (EPIC_TYPE):\n" +
                     "• In the DevOps Link window (click the ID), when the type is Epic the 'EPIC type' panel appears with DELIVERY/BACKLOG. The value is saved in the project file and pushed to DevOps on Export/Sync when changed.\n" +
                     "• An EPIC marked as BACKLOG stays OUT of the project totals: it does not add to the banner HH, nor to the completed % or the start/finish dates shown in the schedule title.\n" +
                     "• The field (default EPIC_TYPE) comes enabled by default in the TFS/DevOps Configuration and can be turned off there.\n\n" +
                     "Task approval (Approved field):\n" +
                     "• The Task approval boolean field in DevOps (default 'Approved' / Custom.Approved) also comes enabled by default.\n" +
                     "• With a value set in the schedule/sheet, sync writes what is here — including REMOVING the approval in DevOps when the schedule says not approved. Without a value, the classic behavior of only officializing the approval is kept.\n" +
                     "• In Task Plan, the Approved column is sent to DevOps when using Save sel. TFS (written only when it differs from what is there).")
                },
                "Field names (Estimated HH, Data_Inicio, Data_Fim) can be customized in the Fields (advanced) section of the import screen."
            ),
            (
                "Project List",
                "The DevOps project list is a file shared among the team with the projects available for import.",
                new()
                {
                    ("Purpose",
                     "Instead of everyone remembering the root work item ID, you maintain a JSON file with registered projects (Name + ID). Everyone on the team points to the same file.\n" +
                     "Access it at View → DevOps Projects (list)..."),
                    ("Managing the list",
                     "• Click Open / Create to load or create a list file.\n" +
                     "• Use the Add, Edit and Delete buttons to maintain projects.\n" +
                     "• The file path is saved in user settings and reloaded automatically."),
                    ("Using in import",
                     "On the import screen (File → Import → TFS / Azure DevOps), a ComboBox shows the projects from the list. Select the project and the root ID field is filled automatically.\n" +
                     "Use the ⚙ Manage List... button to open the CRUD directly from the import screen."),
                    ("Banner in the schedule",
                     "After importing, the linked project name appears in a light blue banner at the top of the schedule, making it easy to identify which project is open.")
                },
                "Save the list file in a shared directory (network, OneDrive, SharePoint) so the whole team uses the same project list."
            ),
            (
                "Sync",
                "Sync sends back to Azure DevOps the changes made in the schedule: dates, hours, state, sprint, tags and predecessors.",
                new()
                {
                    ("How to sync",
                     "File → Export → Sync TFS / Azure DevOps... opens the sync screen. Use the same credentials as import.\n" +
                     "The process compares the current schedule state with DevOps and sends only what changed."),
                    ("What is synced",
                     "• Story/Feature title and description.\n" +
                     "• Estimated hours (Estimated HH).\n" +
                     "• Start and finish dates (Data_Inicio, Data_Fim).\n" +
                     "• State (New, Active, Resolved, Closed).\n" +
                     "• Tags (including Block tag for blocking).\n" +
                     "• Sprint (System.IterationPath).\n" +
                     "• Predecessor links between work items."),
                    ("Sync report",
                     "When done, a window shows the summary: updated, created, unchanged, warnings and errors. Use filters to focus on issues and copy the log if you need to record it.")
                },
                "Sync respects only the configured fields. Azure DevOps code traceability, pull requests and pipelines are not affected."
            ),
            (
                "Sync with DevOps",
                "For NXProject to exchange data with Azure DevOps, a few custom fields must exist on the work items. This section explains which ones, how to create them, and how to adjust their names if your organization already uses different names.",
                new()
                {
                    ("What you need to sync",
                     "To import and sync with Azure DevOps you need:\n\n" +
                     "1. Connection: organization URL, Team Project and a PAT (Personal Access Token) with work item read and write permission.\n" +
                     "2. A project root work item (see 'Root work item and hierarchy' below).\n" +
                     "3. The custom fields on Story, Feature and Epic (see 'Required fields' below). Tasks use standard fields only.\n\n" +
                     "Without the custom fields the import partially works, but date/allocation sync and the concurrency control (Sync_version/Sync_Name) won't operate correctly."),
                    ("Root work item and hierarchy (Project type)",
                     "NXProject builds the schedule on the hierarchy: Project → Epic → Feature → Story → Task.\n\n" +
                     "IMPORTANT: 'Project' is NOT a standard Azure DevOps work item type (the standard tops out at Epic). It's a custom type that acts as a 'container' above Epics, grouping the whole project. Many organizations create this type in their process.\n\n" +
                     "How the root is used:\n" +
                     "• Manual import: you enter the root work item ID in the import screen. NXProject imports the descendants (Epic → Feature → Story → Task) of that item. The root type does NOT have to be exactly 'Project' — it can be any work item that is the parent of the Epics (even an Epic, if you only want to import that one).\n" +
                     "• Discovery (Portfolio → Discovery DevOps): automatically lists work items of type 'Project' with no parent in the Team Project. For automatic Discovery to work, the custom 'Project' type must exist.\n\n" +
                     "Summary: if your organization doesn't use a 'Project' type, you can still import by pointing the root ID at an Epic (or other container) — only automatic Discovery depends on the 'Project' type.\n\n" +
                     "Fields on the 'Project' type: since it sits at the top of the hierarchy, create the SAME custom fields on it as on the Epic (Estimated HH, Data_Inicio, Data_Fim, Sync_version, Sync_Name). In practice the 'Project' type is usually a copy of the Epic. NXProject reads the project start date (Data_Inicio) directly from the root item."),
                    ("Required fields in Azure DevOps",
                     "NXProject reads and writes custom fields on Stories, Features and Epics. The fields must exist in the organization process and be added to each work item type you want to sync.\n\n" +
                     "Planning fields (Story, Feature and Epic):\n" +
                     "• Estimated HH — estimated hours. Type: Integer. Used as duration in the schedule.\n" +
                     "• Data_Inicio — planned start date. Type: Date and Time.\n" +
                     "• Data_Fim — planned finish date. Type: Date and Time.\n\n" +
                     "Story-only fields:\n" +
                     "• Perc_Alocacao — % of the person's working day dedicated to this Story (affects finish date). Type: Decimal/Float (1–100, up to 2 decimal places).\n" +
                     "• Perc_Conclusao — % completion (read on import, written on sync). Type: Integer (0–100).\n\n" +
                     "Concurrency control fields (Story, Feature and Epic):\n" +
                     "• Sync_version — version counter, auto-managed by NXProject. Type: Integer.\n" +
                     "• Sync_Name — user who last synced, auto-managed. Type: Text (single line — do NOT use the Identity type).\n\n" +
                     "Task fields (no custom fields required):\n" +
                     "Tasks use only STANDARD Azure DevOps fields, which already exist on the Task type — you don't need to create anything:\n" +
                     "• Estimated HH → Original Estimate (Microsoft.VSTS.Scheduling.OriginalEstimate).\n" +
                     "• Current HH → Completed Work (Microsoft.VSTS.Scheduling.CompletedWork).\n" +
                     "• Priority → Priority (Microsoft.VSTS.Common.Priority; DevOps accepts 1–4).\n" +
                     "• Assigned To; State; Activity (Microsoft.VSTS.Common.Activity).\n" +
                     "Dates, allocation percentage and Sync_version/Sync_Name do NOT apply to Tasks — planning (dates/duration) is derived from the parent Story."),
                    ("Concurrency control (Sync_version / Sync_Name)",
                     "When two users sync at the same time, the last write could overwrite the first. NXProject prevents this:\n\n" +
                     "• On every sync that writes at least one change, Sync_version is incremented by 1 and Sync_Name is set to the current Windows user.\n" +
                     "• When you sync, NXProject compares the version it read during import with the current version in DevOps. If the DevOps version is higher, someone else saved more recently — the item is skipped and marked red in the schedule.\n" +
                     "• Red items remain highlighted until you re-import the project. The sync log shows which items had conflicts.\n" +
                     "• Clicking a red item in the state column opens the DevOps link window, which shows a conflict warning with a ↓ Re-import button.\n\n" +
                     "Sync_version and Sync_Name must be present on all work item types you sync: Story, Feature and Epic."),
                    ("How to create the fields in Azure DevOps",
                     "Go to: Organization Settings → Boards → Process → select your process → open the work item type (Story, Feature or Epic).\n\n" +
                     "1. Click New field.\n" +
                     "2. Enter the name (e.g. 'Estimated HH'), select the type (Integer or Date and Time).\n" +
                     "3. Save and repeat for the remaining fields.\n" +
                     "4. Add the fields to the form layout if you want them visible when editing a work item.\n\n" +
                     "Tip: create the fields once at the process level and add them to Story, Feature and Epic — they share the same field definition across types."),
                    ("Customizing field names",
                     "If your organization already uses different names (e.g. 'Est_Hours' instead of 'Estimated HH'), you can adjust the names NXProject uses without changing Azure DevOps.\n\n" +
                     "On the import screen (File → Import → TFS / Azure DevOps), expand the Fields (advanced) section. There you will find the configurable fields:\n\n" +
                     "• Estimated Hours field name → default: 'Esforço Estimado'\n" +
                     "• Start Date field name → default: 'Data_Inicio'\n" +
                     "• Finish Date field name → default: 'Data_Fim'\n\n" +
                     "Enter the exact Reference Name as registered in Azure DevOps (not the display label). Settings are saved to config_nxproject.json and reused on future imports."),
                    ("Finding a field's Reference Name",
                     "To discover the Reference Name of an existing field in Azure DevOps:\n\n" +
                     "1. Go to Organization Settings → Boards → Fields.\n" +
                     "2. Locate the field and click on it.\n" +
                     "3. The Reference Name appears in the detail panel — usually in the format 'Custom.FieldName'.\n\n" +
                     "That value (e.g. 'Custom.EstimatedHH') is what you enter in the Fields (advanced) section of the import screen."),
                    ("Recommended setup for new projects",
                     "1. Create the three fields in the organization process in Azure DevOps.\n" +
                     "2. In NXProject, open File → Import → TFS / Azure DevOps.\n" +
                     "3. Enter the organization URL, project name, PAT and root work item ID.\n" +
                     "4. If the field names differ from the defaults, expand Fields (advanced) and adjust them.\n" +
                     "5. Click Import — the schedule is generated automatically.\n" +
                     "6. Plan in NXProject and use Export → Sync to send dates back to DevOps.")
                },
                "Field names are case-sensitive. Use the exact Reference Name from Azure DevOps, not the display label."
            ),
            (
                "Export",
                "Export the schedule to other formats to share with stakeholders or integrate with other tools.",
                new()
                {
                    ("Available formats",
                     "• MS Project XML (.xml): compatible with Microsoft Project.\n" +
                     "• OpenProj (.pod): open format for tools like ProjectLibre.\n" +
                     "• Excel XML (.xml): table with all activities, dates and resources.\n" +
                     "• CSV: simple format for analysis in any spreadsheet."),
                    ("When to use each format",
                     "• Use MS Project XML to send the schedule to stakeholders who use MS Project.\n" +
                     "• Use Excel/CSV for reports, dashboards or custom analyses.\n" +
                     "• Use OpenProj in environments without an MS Project license.")
                },
                "CSV is the most portable format for feeding dashboards in Power BI, Tableau or Google Sheets."
            ),
            (
                "Health Check",
                "Health Check identifies schedule issues that need attention before they impact delivery.",
                new()
                {
                    ("What is checked",
                     "View → Project Health Check analyzes all activities and lists:\n" +
                     "• Activities with Finish in the past and percent less than 100% (delayed).\n" +
                     "• Activities without an assigned resource.\n" +
                     "• Activities with predecessors that create circular dependencies.\n" +
                     "• Stories marked as blocked (Block tag)."),
                    ("How to use",
                     "• Open Health Check regularly in status meetings to review the project state.\n" +
                     "• Click an activity in the list to select it in the grid and fix the issue.\n" +
                     "• Use it as a checklist before sending a report to management.")
                },
                "Run Health Check before each status meeting — it reveals in seconds what is delayed and unassigned."
            ),
            (
                "AI Assistant",
                "The AI Assistant suggests task structures, story decomposition and schedule organization from a natural language description.",
                new()
                {
                    ("How to access",
                     "Click the AI button in the toolbar or go to AI → Task Assistant...\n" +
                     "Describe what needs to be done and the assistant suggests a task hierarchy with estimates."),
                    ("Use cases",
                     "• Create the initial project structure from a description.\n" +
                     "• Decompose a large Story into smaller Tasks.\n" +
                     "• Generate an activity list for a recurring delivery type (e.g. environment setup, regression tests).\n" +
                     "• Review whether the current decomposition covers all scope aspects."),
                    ("Availability",
                     "The AI Assistant requires an internet connection and a configured API key. In the Community edition it is available in limited mode. The Enterprise edition includes full integration with OpenAI and Claude.")
                },
                "Use the AI Assistant for the initial task brainstorm — then manually refine in the grid with your specific context."
            ),
            (
                "Baseline",
                "Record a schedule snapshot to compare original planning against actual execution.",
                new()
                {
                    ("What is a Baseline",
                     "A Baseline is a snapshot of the schedule at a specific moment — start dates, end dates, and estimated hours for each activity.\n\n" +
                     "It answers: 'Is the project ahead of or behind the original plan?'"),
                    ("How to use",
                     "Management → Baseline → Save Baseline: creates a .nxb file alongside the .nxp.\n" +
                     "Management → Baseline → Open Baseline: loads and displays the blue bar in the Gantt.\n" +
                     "Management → Baseline → Disable/Enable Baseline: shows or hides the bar without deleting the .nxb.\n" +
                     "Management → Baseline → Clear: removes the .nxb and clears in-memory data."),
                    ("Blue bar in the Gantt",
                     "When the baseline is active, a thin blue bar appears below each Gantt bar showing the originally planned start and end dates.\n" +
                     "The gap between the current bar and the blue bar indicates advance (bar left of the line) or delay (bar right of the line)."),
                    ("Separate file",
                     "The .nxb is saved beside the .nxp but is NOT part of the schedule. This prevents initial planning data from polluting the working file.\n" +
                     "When sharing the .nxp with the team, the .nxb can be kept locally or shared separately."),
                    ("Auto-load",
                     "By default, when opening a .nxp, NXProject automatically loads the corresponding .nxb (if it exists).\n" +
                     "Disable this in Management → Baseline → Load automatically on open to control it manually.")
                },
                "Save the Baseline right after the project kick-off — before any date adjustments. That snapshot is the reference for measuring delays."
            ),
            (
                "Critical Path",
                "Identifies the activities that, if delayed, delay the entire project.",
                new()
                {
                    ("What is the Critical Path",
                     "The Critical Path is the sequence of activities that determines the earliest possible project finish date.\n\n" +
                     "In practice, these are zero-float activities: if one of them slips, the project finish date slips too, unless another activity is shortened or replanned.\n\n" +
                     "NXProject uses CPM (Critical Path Method), calculating early and late dates to find each activity's float."),
                    ("How to interpret it",
                     "• A critical activity is not necessarily the longest or the most important from a business perspective.\n" +
                     "• It is critical because it belongs to a chain with no delay margin.\n" +
                     "• A non-critical activity can slip up to its float limit without moving the final deadline.\n" +
                     "• When you change duration, predecessors, resource or allocation %, the critical path may change."),
                    ("How to enable",
                     "Management → Critical Path (checkbox): toggles the visual highlight on/off.\n" +
                     "Management → Critical Path → View critical activity list: opens the window with the full grid.\n" +
                     "The state (on/off) is saved in the .nxp file."),
                    ("Red border in the Gantt",
                     "When enabled, activities on the critical path display a red border around their Gantt bar.\n" +
                     "The bar background color does not change — only the border is highlighted to avoid confusion with other visual alerts."),
                    ("Critical activities window",
                     "Displays a grid with:\n" +
                     "• ID (xxx:T for DevOps, xxx:I for internal)\n" +
                     "• Type, Name, Start, End, Duration\n" +
                     "• Float: 'Critical' (red) or number of days of float (green)\n" +
                     "• Predecessors\n\n" +
                     "Available filters: by name, by resource, and 'Critical only' checkbox."),
                    ("Interpreting float",
                     "Float = days the activity can be delayed without impacting the final deadline.\n" +
                     "Float 0 = critical. Float 5d = can be delayed up to 5 working days with no project impact.")
                },
                "Focus on critical activities first when replanning. A 1-day delay in a zero-float activity equals a full project delay."
            ),
            (
                "Activity Diagram",
                "Visualize the project hierarchy as a horizontal diagram with dependencies.",
                new()
                {
                    ("How to open",
                     "Management → Activity Diagram. The diagram is generated automatically from the schedule hierarchy."),
                    ("Levels and expansion",
                     "The diagram displays the hierarchy in horizontal columns: Epic → Feature → Story → Task.\n" +
                     "Use the checkboxes at the top (Epic, Feature, Story, Task) to expand or collapse levels.\n" +
                     "Click a node to expand/collapse its children individually."),
                    ("Node colors",
                     "• Dark blue: Epic\n• Blue: Feature\n• Green: Story\n• Purple: Task\n" +
                     "• Brown/orange: internal activity (xxx:I) — created locally, not yet synced with DevOps."),
                    (":T / :I badge",
                     "Each node displays a badge in the bottom-right corner:\n" +
                     "• 1234:T = Azure DevOps work item with ID 1234\n" +
                     "• 45:I = internal activity with local sequential ID\n" +
                     "After syncing with DevOps, :I is automatically promoted to :T."),
                    ("Tooltip on hover",
                     "Hover over any node to see: ID, Type, State, Start, End, Estimated Hours, % Complete, Resource and Sprint."),
                    ("Zoom and resize",
                     "• Ctrl + scroll: zoom in or out (30% to 300%).\n" +
                     "• Drag the right edge of any node to resize all nodes at the same level.\n" +
                     "• Click '↺ Reset zoom' to return to 100%.\n" +
                     "• '💾 Save preferences' stores widths and expansion state in the .nxp for next opening.")
                },
                "Use the Activity Diagram to communicate project scope to stakeholders — it is more readable than the Gantt for those unfamiliar with schedules."
            ),
            (
                "Resource Cost",
                "Calculate project cost per resource with support for hourly or monthly billing models.",
                new()
                {
                    ("Configure cost in the People screen",
                     "Management → Cost — People opens the People screen with cost columns and the cost side panel enabled.\n\n" +
                     "Cost columns:\n" +
                     "• Cost: 'Hourly' (per hour) or 'Monthly' (per month)\n" +
                     "• $/hour: rate charged per hour worked\n" +
                     "• $/month: monthly rate when the resource has a fixed monthly cost\n\n" +
                     "Resources marked as Internal are excluded from project cost."),
                    ("Hours used by cost",
                     "Cost uses the resource work hours on the activity: Current HH + Remaining HH.\n" +
                     "Allocation % does not reduce cost. It changes the schedule/calendar time required to execute those hours.\n\n" +
                     "Example: an 8h activity with a resource allocated at 10% still has 8h of cost; it simply spans more calendar days."),
                    ("Hourly model",
                     "Cost = resource hours on the activity × $/hour.\n" +
                     "Hours are distributed across months according to the activity period and the Current HH / Remaining HH rule.\n" +
                     "Ideal for freelancers, consultants, or any professional hired on demand."),
                    ("Monthly model",
                     "The resource monthly rate is allocated proportionally by that resource's hours in the project:\n\n" +
                     "Activity cost = (resource HH on activity ÷ total resource HH) × $/month\n\n" +
                     "Then that cost is distributed across months according to the activity hours in each month.\n" +
                     "Ideal for salaried employees, interns, or any professional with fixed monthly compensation."),
                    ("Resource Cost screen",
                     "Management → Resource Cost displays the detailed grid by Resource → Epic → Feature.\n\n" +
                     "For each month the grid shows:\n" +
                     "• CAPEX column: cost from CAPEX-classified activities\n" +
                     "• OPEX column: cost from OPEX-classified activities\n" +
                     "• TOTAL, CAPEX total and OPEX total at the end of the row\n\n" +
                     "Resource totals and GRAND TOTAL sum all visible months."),
                    ("CAPEX/OPEX and drill-down",
                     "CAPEX/OPEX classification comes from the Epic cost-center type when configured; when there is no specific definition, the project default rule is used.\n\n" +
                     "Click a cost cell to open the drill-down. The detail shows the Stories/activities behind that value, including HH and calculated cost."),
                    ("Filters and export",
                     "Use the side filters to select resources, Features, or show only rows with cost.\n" +
                     "Export sends the same screen view to Excel XML, preserving months, CAPEX/OPEX and totals."),
                    ("Cost file (.nxcost) — encrypted",
                     "Cost data is NOT stored in the .nxp to preserve salary confidentiality.\n\n" +
                     "In the People screen:\n" +
                     "• '💰 Save cost config': choose a location, set a password → generates an encrypted .nxcost file.\n" +
                     "• '📂 Load cost config': choose the file, enter the password → values are applied to resources.\n\n" +
                     "The file uses AES-256-GCM with PBKDF2-SHA256 (100,000 iterations). Without the password, the file is unreadable — keep it safe.")
                },
                "Use the Monthly model for salaried staff and Hourly for contractors. The .nxcost file can be kept restricted to the project manager — it does not need to accompany the .nxp when sharing with the team."
            ),
            (
                "Settings",
                "Customize NXProject behavior for your project and team.",
                new()
                {
                    ("Work calendar",
                     "View → Calendar lets you configure:\n" +
                     "• Working hours per day (default: 8h).\n" +
                     "• Days of the week considered working days.\n" +
                     "• Holidays: add specific dates that will be ignored in deadline calculations.\n" +
                     "The calendar is saved locally at %LocalAppData%\\NXProject.Community\\nxproject_calender.json."),
                    ("SPF — Story Function Points",
                     "View → SPF configures the conversion table between function points and estimated hours, used to calculate duration from complexity metrics."),
                    ("DevOps connection settings",
                     "Connection credentials (organization URL, Team Project, PAT) are saved securely using DPAPI (Windows encryption tied to the user). Check Remember token to avoid typing it on each import.\n" +
                     "The DevOps Project List file path is also saved in user settings."),
                    ("Default zoom",
                     "The last selected zoom is saved in the .nxp file and restored when reopening the project.")
                },
                "The calendar is the heart of deadline calculation — configure your country and company holidays before starting planning."
            )
        };
    }
}
