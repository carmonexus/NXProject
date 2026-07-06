using System;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NXProject.Models;
using NXProject.Services;

namespace NXProject.Views
{
    public partial class TaskDescriptionEditWindow : Window
    {
        private readonly ProjectTask _task;
        private bool _webViewReady;
        private bool _pendingPreview;
        private string _html = string.Empty;
        private bool _editingInWebView;

        public TaskDescriptionEditWindow(ProjectTask task)
        {
            InitializeComponent();
            _task = task;
            TitleText.Text = AppStrings.Get("Desc_TitleFormat", task.Name);
            _html = task.Description ?? string.Empty;

            if (task.TfsId is not > 0)
                FetchBtn.IsEnabled = false;

            if (!string.IsNullOrWhiteSpace(_html))
                _pendingPreview = true;

            InitWebViewAsync();
        }

        private async void InitWebViewAsync()
        {
            try
            {
                await WebView.EnsureCoreWebView2Async();
                _webViewReady = true;
                SetupWebViewAuth();

                if (_pendingPreview)
                    ShowPreview();
                else
                    ShowEditWysiwyg();
            }
            catch
            {
                PreviewModeBtn.IsEnabled = false;
                EditModeBtn.IsEnabled = false;
            }
        }

        private void SetupWebViewAuth()
        {
            if (!_webViewReady) return;

            try
            {
                var options = TfsConnectionStore.Load("NXProject.Community");
                if (string.IsNullOrWhiteSpace(options.PersonalAccessToken)) return;

                var authValue = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes(":" + options.PersonalAccessToken));

                WebView.CoreWebView2.AddWebResourceRequestedFilter(
                    "https://*.visualstudio.com/*", CoreWebView2WebResourceContext.All);
                WebView.CoreWebView2.AddWebResourceRequestedFilter(
                    "https://dev.azure.com/*", CoreWebView2WebResourceContext.All);

                WebView.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    e.Request.Headers.SetHeader("Authorization", "Basic " + authValue);
                };
            }
            catch { }
        }

        private async void ShowPreview()
        {
            if (_editingInWebView && _webViewReady)
            {
                var result = await WebView.ExecuteScriptAsync("document.body.innerHTML");
                _html = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? _html;
            }

            _editingInWebView = false;
            PreviewModeBtn.FontWeight = FontWeights.Bold;
            EditModeBtn.FontWeight = FontWeights.Normal;

            if (_webViewReady)
                LoadHtmlInWebView(_html);
        }

        private void ShowEditWysiwyg()
        {
            _editingInWebView = true;
            EditModeBtn.FontWeight = FontWeights.Bold;
            PreviewModeBtn.FontWeight = FontWeights.Normal;

            if (_webViewReady)
                LoadHtmlInWebViewEditable(_html);
        }

        private static string BuildCss(bool editable = false) =>
            $"body{{font-family:Segoe UI,sans-serif;font-size:13px;color:#1f1f1f;background:#ffffff;padding:16px;margin:0;line-height:1.5{(editable ? ";outline:none" : "")}}}" +
            "img{max-width:100%;height:auto}" +
            "table{border-collapse:collapse}" +
            "td,th{border:1px solid #ccc;padding:4px 8px}" +
            "th{background:#f0f0f0}" +
            "code{background:#f4f4f4;padding:1px 4px;border-radius:3px}" +
            "p{margin:0 0 8px 0}";

        private void LoadHtmlInWebView(string html)
        {
            var page = string.IsNullOrWhiteSpace(html)
                ? "<html><body style='font-family:Segoe UI,sans-serif;color:#666;background:#ffffff;padding:16px'><i>" + AppStrings.Get("Desc_NoDescription") + "</i></body></html>"
                : $"<html><head><meta charset='utf-8'/><style>{BuildCss()}</style></head><body>{html}</body></html>";

            WebView.CoreWebView2.NavigateToString(page);
        }

        private void LoadHtmlInWebViewEditable(string html)
        {
            var body = string.IsNullOrWhiteSpace(html) ? "" : html;
            var page = $"<html><head><meta charset='utf-8'/><style>{BuildCss(editable: true)}</style></head>" +
                       $"<body contenteditable='true'>{body}</body></html>";

            WebView.CoreWebView2.NavigateToString(page);
        }

        private void OnPreviewMode(object sender, RoutedEventArgs e) => ShowPreview();
        private void OnEditMode(object sender, RoutedEventArgs e) => ShowEditWysiwyg();

        private async void OnFetchFromDevOpsClick(object sender, RoutedEventArgs e)
        {
            FetchBtn.IsEnabled = false;
            FetchStatus.Text = AppStrings.Get("Desc_Fetching");
            try
            {
                var options = TfsConnectionStore.Load("NXProject.Community");
                var html = await TfsImportService.LoadWorkItemDescriptionHtmlAsync(
                    options, _task.TfsId!.Value);
                _html = html ?? string.Empty;
                FetchStatus.Text = string.IsNullOrWhiteSpace(_html)
                    ? AppStrings.Get("Desc_Empty")
                    : AppStrings.Get("Desc_Loaded");

                if (_webViewReady)
                {
                    if (_editingInWebView)
                        LoadHtmlInWebViewEditable(_html);
                    else
                        LoadHtmlInWebView(_html);
                }
            }
            catch (Exception ex)
            {
                FetchStatus.Text = AppStrings.Get("Desc_FetchError", ex.Message);
            }
            finally
            {
                FetchBtn.IsEnabled = _task.TfsId is > 0;
            }
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_editingInWebView && _webViewReady)
            {
                var result = await WebView.ExecuteScriptAsync("document.body.innerHTML");
                _html = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? _html;
            }

            _task.Description = _html.Trim();
            DialogResult = true;
            Close();
        }
    }
}
