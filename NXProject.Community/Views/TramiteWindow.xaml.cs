// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NXProject.Community.Services;
using NXProject.Services;

namespace NXProject.Views
{
    /// <summary>
    /// Tela de trâmite (discussão) de um work item: mostra o HISTÓRICO dos comentários (autor/data,
    /// com imagens) e permite registrar um novo trâmite rico (também com imagem). O novo trâmite fica
    /// em <see cref="NewComment"/> (HTML) para o TaskBoard gravar no Salvar TFS.
    /// </summary>
    public partial class TramiteWindow : Window
    {
        private readonly TfsConnectionOptions _options;
        private readonly int _workItemId;
        private bool _historyReady, _editorReady;

        /// <summary>HTML do novo trâmite (vazio = nenhum).</summary>
        public string NewComment { get; private set; } = string.Empty;

        public TramiteWindow(int workItemId, string title, string? draftHtml = null)
        {
            InitializeComponent();
            _options = TfsConnectionStore.Load("NXProject.Community");
            _workItemId = workItemId;
            NewComment = draftHtml ?? string.Empty;
            TitleText.Text = title;
            InitAsync();
        }

        private async void InitAsync()
        {
            try
            {
                await HistoryView.EnsureCoreWebView2Async();
                _historyReady = true;
                SetupAuth(HistoryView);
                await EditorView.EnsureCoreWebView2Async();
                _editorReady = true;
                SetupAuth(EditorView);
                LoadEditor(NewComment);
                await LoadHistoryAsync();
            }
            catch { StatusText.Text = AppStrings.Get("Tramite_NoWebView"); }
        }

        // Envia o PAT nas requisições do WebView para carregar imagens do DevOps.
        private void SetupAuth(Microsoft.Web.WebView2.Wpf.WebView2 view)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken)) return;
                var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + _options.PersonalAccessToken));
                view.CoreWebView2.AddWebResourceRequestedFilter("https://*.visualstudio.com/*", CoreWebView2WebResourceContext.All);
                view.CoreWebView2.AddWebResourceRequestedFilter("https://dev.azure.com/*", CoreWebView2WebResourceContext.All);
                view.CoreWebView2.WebResourceRequested += (_, e) => e.Request.Headers.SetHeader("Authorization", "Basic " + authValue);
            }
            catch { }
        }

        private static string Css =>
            "body{font-family:Segoe UI,sans-serif;font-size:13px;color:#1f1f1f;background:#fff;padding:12px;margin:0;line-height:1.5}" +
            "img{max-width:100%;height:auto}.c{border-bottom:1px solid #eee;padding:8px 0}.h{color:#2B579A;font-size:11px;font-weight:600;margin-bottom:4px}";

        private async System.Threading.Tasks.Task LoadHistoryAsync()
        {
            StatusText.Text = AppStrings.Get("Tramite_Loading");
            try
            {
                var comments = await TfsImportService.GetWorkItemCommentsAsync(_options, _workItemId);
                var sb = new StringBuilder();
                if (comments.Count == 0)
                    sb.Append("<i style='color:#888'>").Append(WebUtility.HtmlEncode(AppStrings.Get("Tramite_Empty"))).Append("</i>");
                foreach (var c in comments)
                {
                    var when = c.Date is { } d ? d.ToString("dd/MM/yyyy HH:mm") : "";
                    sb.Append("<div class='c'><div class='h'>")
                      .Append(WebUtility.HtmlEncode(c.Author)).Append("  ·  ").Append(when)
                      .Append("</div>").Append(c.Html).Append("</div>");
                }
                if (_historyReady)
                    HistoryView.CoreWebView2.NavigateToString($"<html><head><meta charset='utf-8'/><style>{Css}</style></head><body>{sb}</body></html>");
                StatusText.Text = AppStrings.Get("Query_Count", comments.Count.ToString());
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        private void LoadEditor(string html)
        {
            if (!_editorReady) return;
            EditorView.CoreWebView2.NavigateToString(
                $"<html><head><meta charset='utf-8'/><style>{Css}body{{outline:none}}</style></head><body contenteditable='true'>{html}</body></html>");
        }

        private async void OnRegisterClick(object sender, RoutedEventArgs e)
        {
            if (_editorReady)
            {
                var result = await EditorView.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
                NewComment = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? string.Empty;
            }
            DialogResult = true;
            Close();
        }
    }
}
