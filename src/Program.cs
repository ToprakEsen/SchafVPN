namespace SchafVPN;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Aynı anda tek kopya çalışsın (iki TUN arayüzü çakışır)
        using var mutex = new Mutex(true, @"Global\SchafVPN_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("SchafVPN zaten çalışıyor.", "SchafVPN",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
