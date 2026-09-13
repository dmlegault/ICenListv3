using System.Windows;
using System.Windows.Controls;

namespace Enlist.Installer.Ba
{
    /// <summary>
    /// The shell. Everything it shows comes from WizardViewModel, and everything that view model
    /// decides comes from InstallPlan - so this file has almost nothing in it, which is the point.
    ///
    /// The exception is the three passwords, and it is not an oversight. PasswordBox.Password is
    /// deliberately NOT a dependency property: WPF will not let a password sit in the binding system
    /// where it would be reachable from a snapshot of the visual tree. So it is passed across by hand
    /// here, on change, and nowhere else.
    /// </summary>
    public partial class WizardWindow : Window
    {
        public WizardWindow()
        {
            InitializeComponent();
        }

        private WizardViewModel? Model => DataContext as WizardViewModel;

        private void ControlPlanePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Model != null) { Model.ControlPlanePassword = ((PasswordBox)sender).Password; }
        }

        private void PortalPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Model != null) { Model.PortalPassword = ((PasswordBox)sender).Password; }
        }

        private void DatabasePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Model != null) { Model.DatabasePassword = ((PasswordBox)sender).Password; }
        }
    }
}
