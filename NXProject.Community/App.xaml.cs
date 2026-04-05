using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace NXProject
{
    public partial class CommunityApp : Application
    {
        public CommunityApp()
        {
            var culture = CultureInfo.CurrentCulture;

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
    }
}
