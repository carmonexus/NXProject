using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace NXProject.Controls
{
    public partial class SprintSettingsControl : UserControl
    {
        public SprintSettingsControl()
        {
            InitializeComponent();
        }

        private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
    }
}
