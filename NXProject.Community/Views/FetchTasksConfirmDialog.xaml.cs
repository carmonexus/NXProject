using System.Windows;
using NXProject.Services;

namespace NXProject.Views
{
    public enum FetchTasksAction { Include, Release, Cancel }

    public partial class FetchTasksConfirmDialog : Window
    {
        public FetchTasksAction Result { get; private set; } = FetchTasksAction.Cancel;

        public FetchTasksConfirmDialog(int totalFound, int newCount)
        {
            InitializeComponent();
            SummaryText.Text = AppStrings.Get("Fetch_Summary", totalFound, newCount);

            // Se há Tasks novas ou alteradas, avisa que serão suprimidas ao liberar
            if (newCount > 0)
            {
                WarningText.Text = AppStrings.Get("Fetch_Warning", newCount);
                WarningText.Visibility = Visibility.Visible;
            }
        }

        private void OnIncludeClick(object sender, RoutedEventArgs e)
        {
            Result = FetchTasksAction.Include;
            Close();
        }

        private void OnReleaseClick(object sender, RoutedEventArgs e)
        {
            Result = FetchTasksAction.Release;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Result = FetchTasksAction.Cancel;
            Close();
        }
    }
}
