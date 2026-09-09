using System.Windows;
using System.Windows.Input;
using MozartBrowser.Interop;

namespace MozartBrowser.Dialogs
{
    /// <summary>
    /// Blocking (ShowDialog) prompt for the current Windows account's
    /// password, used to gate revealing a saved password as plaintext.
    /// DialogResult == true only after WindowsCredentialVerifier actually
    /// confirms the password — never just because Confirm was clicked.
    /// </summary>
    public partial class ReauthenticateDialog : Window
    {
        public ReauthenticateDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => PasswordInput.Focus();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ConfirmButton_Click(sender, e);
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowsCredentialVerifier.VerifyCurrentUserPassword(PasswordInput.Password))
            {
                DialogResult = true;
                return;
            }

            ErrorText.Visibility = Visibility.Visible;
            PasswordInput.SelectAll();
            PasswordInput.Focus();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
