// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Threading.Tasks;
using System.Windows;
using NXProject.Services;

namespace NXProject.Views;

public partial class UpdateProgressWindow : Window
{
    private readonly string _downloadUrl;
    private string? _extractedDir;

    public UpdateProgressWindow(string downloadUrl)
    {
        InitializeComponent();
        _downloadUrl = downloadUrl;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await RunDownloadAsync();
    }

    private async Task RunDownloadAsync()
    {
        try
        {
            var progress = new Progress<int>(p =>
            {
                DownloadProgress.Value = p;
                PercentText.Text = $"{p}%";
            });
            // MB baixados/total: em pacotes grandes o percentual sozinho parece parado.
            var bytes = new Progress<(long Downloaded, long Total)>(b =>
                PercentText.Text = b.Total > 0
                    ? $"{b.Downloaded / 1024d / 1024d:0.0} MB de {b.Total / 1024d / 1024d:0.0} MB ({b.Downloaded * 100 / b.Total}%)"
                    : $"{b.Downloaded / 1024d / 1024d:0.0} MB");

            _extractedDir = await UpdateService.DownloadAndExtractAsync(_downloadUrl, progress, bytesProgress: bytes);

            StatusText.Text = AppStrings.Get("Upd_Applying");
            await Task.Delay(600);

            UpdateService.LaunchUpdaterAndExit(_extractedDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                AppStrings.Get("Upd_Failed", ex.Message),
                AppStrings.Get("Upd_MsgTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }
}
