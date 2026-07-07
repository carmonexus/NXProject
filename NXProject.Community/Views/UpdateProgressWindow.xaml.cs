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

            _extractedDir = await UpdateService.DownloadAndExtractAsync(_downloadUrl, progress);

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
