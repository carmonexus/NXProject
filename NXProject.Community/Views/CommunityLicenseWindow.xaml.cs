using System.Windows;

namespace NXProject.Views
{
    public partial class CommunityLicenseWindow : Window
    {
        public CommunityLicenseWindow()
        {
            InitializeComponent();
        }

        private void OnAcceptClick(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnDeclineClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
