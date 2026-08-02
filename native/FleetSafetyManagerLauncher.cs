using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

[assembly: AssemblyTitle("Fleet Safety Manager")]
[assembly: AssemblyDescription("Fleet Safety Manager desktop launcher")]
[assembly: AssemblyProduct("Fleet Safety Manager")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    private const string ServiceName = "FleetSafetyManager";
    private const string ApplicationUrl = "http://127.0.0.1:8080/";
    private const int ReadyAttempts = 30;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr windowHandle,
        string text,
        string caption,
        uint type);

    [STAThread]
    private static int Main()
    {
        string logPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "FleetSafetyManagerLauncher.log");

        try
        {
            Log(logPath, "Launcher started.");

            if (!SiteIsReady())
            {
                Log(logPath, "Starting Windows service.");
                StartService();
            }

            for (int attempt = 0; attempt < ReadyAttempts; attempt++)
            {
                if (SiteIsReady())
                {
                    Log(logPath, "Application is ready.");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ApplicationUrl,
                        UseShellExecute = true
                    });
                    return 0;
                }

                Thread.Sleep(1000);
            }

            throw new InvalidOperationException(
                "The application did not become ready.");
        }
        catch (Exception exception)
        {
            Log(logPath, exception.ToString());
            MessageBox(
                IntPtr.Zero,
                "Fleet Safety Manager could not start.\r\n" +
                "Restart Windows and try again.\r\n" +
                "Diagnostic log: " + logPath,
                "Fleet Safety Manager",
                0x30);
            return 1;
        }
    }

    private static void StartService()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = "start \"" + ServiceName + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using (Process process = Process.Start(startInfo))
        {
            if (process != null)
                process.WaitForExit(10000);
        }
    }

    private static bool SiteIsReady()
    {
        try
        {
            HttpWebRequest request =
                (HttpWebRequest)WebRequest.Create(ApplicationUrl);
            request.Method = "GET";
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            request.AllowAutoRedirect = true;

            using (HttpWebResponse response =
                (HttpWebResponse)request.GetResponse())
            {
                int statusCode = (int)response.StatusCode;
                return statusCode >= 200 && statusCode < 500;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string logPath, string message)
    {
        try
        {
            File.AppendAllText(
                logPath,
                DateTime.Now + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Logging must never prevent the launcher from opening the app.
        }
    }
}
