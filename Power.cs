using System.Diagnostics;

namespace Isle
{
    internal static class Power
    {
        public static void Lock() => Win32.LockWorkStation();

        public static void Sleep() => Win32.SetSuspendState(false, false, false);

        public static void Restart()
        {
            if (DarkDialog.Confirm("Neu starten?", "Offene Programme werden geschlossen. Nicht gespeicherte Arbeit kann verloren gehen.", "Neu starten"))
                Run("shutdown", "/r /t 0");
        }

        public static void Shutdown()
        {
            if (DarkDialog.Confirm("Herunterfahren?", "Offene Programme werden geschlossen. Nicht gespeicherte Arbeit kann verloren gehen.", "Herunterfahren"))
                Run("shutdown", "/s /t 0");
        }

        public static void SignOut()
        {
            if (DarkDialog.Confirm("Abmelden?", "Offene Programme werden geschlossen.", "Abmelden"))
                Run("shutdown", "/l");
        }

        private static void Run(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }
    }
}
