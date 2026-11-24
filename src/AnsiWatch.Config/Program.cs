using AnsiWatch.Core.Text;

namespace AnsiWatch.Config;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        CodePageSupport.EnsureRegistered();
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "AnsiWatch – onverwachte fout", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}
