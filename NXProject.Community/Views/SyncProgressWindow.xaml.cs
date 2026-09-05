// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Tela modal de andamento da sincronização com o TFS/DevOps. Mostra a etapa atual e,
    /// na etapa de itens, qual Epic/Feature/Story/Task está sendo sincronizado.
    /// A sincronização não é cancelável: a janela só fecha quando termina.
    /// </summary>
    public partial class SyncProgressWindow : Window
    {
        private bool _canClose;

        public SyncProgressWindow()
        {
            InitializeComponent();
            PhaseText.Text = AppStrings.Get("SyncProg_Starting");
            Bar.IsIndeterminate = true;
            // Fechar pelo X só quando a sincronização terminar.
            Closing += (_, e) => e.Cancel = !_canClose;
        }

        /// <summary>Atualiza a tela com o andamento (chamado pela thread de UI).</summary>
        public void Report(Services.TfsImportService.SyncProgress p)
        {
            PhaseText.Text = p.Phase;
            ItemText.Text = p.Item ?? string.Empty;
            if (p.Total > 0)
            {
                Bar.IsIndeterminate = false;
                Bar.Value = p.Current * 100.0 / p.Total;
                CountText.Text = AppStrings.Get("SyncProg_Count", p.Current, p.Total);
            }
            else
            {
                Bar.IsIndeterminate = true;
                CountText.Text = string.Empty;
            }
        }

        /// <summary>Libera o fechamento e fecha a janela. Idempotente (catch + finally).</summary>
        public void Done()
        {
            if (_canClose) return;
            _canClose = true;
            Close();
        }
    }
}
