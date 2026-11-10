namespace SchafVPN;

internal static class Program
{
    /// <summary>İkinci kopya açılınca çalışan kopyaya "pencereni göster" sinyali.</summary>
    internal const string ShowEventName = @"Local\SchafVPN_Show";

    [STAThread]
    private static void Main(string[] args)
    {
        // Aynı anda tek kopya çalışsın (iki TUN arayüzü çakışır)
        using var mutex = new Mutex(true, @"Global\SchafVPN_SingleInstance", out bool isNew);
        if (!isNew)
        {
            // Zaten çalışıyor (muhtemelen tepside): onu öne getir
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
                MessageBox.Show("SchafVPN zaten çalışıyor.", "SchafVPN",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(justUpdated: args.Contains("--updated"), startHidden: args.Contains("--tray")));
    }
}
