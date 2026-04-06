namespace BHelper;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var trayApp = new TrayApplicationContext();
        Application.Run(trayApp);
    }
}
