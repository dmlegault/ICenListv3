using System.Windows;

namespace Enlist.Installer.Ba
{
    /// <summary>
    /// The shell. Everything it shows comes from WizardViewModel, and everything that view model
    /// decides comes from InstallPlan - so this file has nothing in it, which is the point.
    /// </summary>
    public partial class WizardWindow : Window
    {
        public WizardWindow()
        {
            InitializeComponent();
        }
    }
}
