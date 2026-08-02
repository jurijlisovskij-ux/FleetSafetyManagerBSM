using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("Fleet Safety Manager Launcher")]
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
    private static extern int MessageBox(IntPtr handle, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        string installDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FleetSafetyManagerLauncher.log");

        try
        {
            string applicationDirectory = ResolveApplicationDirectory(installDirectory);
            string edition = ResolveEdition(installDirectory, applicationDirectory);
            string executable = Path.Combine(applicationDirectory, "FSM.Api.exe");

            if (!File.Exists(executable))
                throw new FileNotFoundException("The Fleet Safety Manager executable is missing: " + executable, executable);

            if (args.Length == 1 && args[0].Equals("--validate-only", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (!SiteIsReady())
                StartService(executable);

            for (int attempt = 0; attempt < ReadyAttempts; attempt++)
            {
                if (SiteIsReady())
                {
                    Process.Start(new ProcessStartInfo { FileName = ApplicationUrl, UseShellExecute = true });
                    return 0;
                }
                Thread.Sleep(1000);
            }

            throw new InvalidOperationException("Fleet Safety Manager " + edition +
                " did not become ready at " + ApplicationUrl + " after 30 seconds.");
        }
        catch (Exception exception)
        {
            string detail = GetErrorMessage(exception);
            LogFailure(logPath, installDirectory, detail, exception);
            MessageBox(IntPtr.Zero, detail + "\r\n\r\nDiagnostic log: " + logPath,
                "Fleet Safety Manager", 0x10);
            return 1;
        }
    }

    private static string ResolveApplicationDirectory(string installDirectory)
    {
        string currentPath = Path.Combine(installDirectory, "current.txt");
        if (!File.Exists(currentPath))
            File.WriteAllText(currentPath, "." + Environment.NewLine, Encoding.UTF8);

        string configuredPath = File.ReadAllText(currentPath).Trim();
        if (configuredPath.Length == 0)
            throw new InvalidDataException("current.txt is empty: " + currentPath);

        string resolvedPath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(installDirectory, configuredPath));

        if (!Directory.Exists(resolvedPath))
            throw new DirectoryNotFoundException("current.txt points to a directory that does not exist: " + resolvedPath);

        return resolvedPath;
    }

    private static string ResolveEdition(string installDirectory, string applicationDirectory)
    {
        string editionPath = Path.Combine(installDirectory, "edition.txt");
        if (!File.Exists(editionPath))
        {
            string detectedEdition = DetectEdition(Path.Combine(applicationDirectory, "appsettings.json"));
            File.WriteAllText(editionPath, detectedEdition + Environment.NewLine, Encoding.UTF8);
        }

        string edition = File.ReadAllText(editionPath).Trim();
        if (!edition.Equals("Office", StringComparison.OrdinalIgnoreCase) &&
            !edition.Equals("Ship", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("edition.txt must contain Office or Ship: " + editionPath);
        return edition;
    }

    private static string DetectEdition(string settingsPath)
    {
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("Cannot recreate edition.txt because appsettings.json is missing: " + settingsPath, settingsPath);

        string settings = File.ReadAllText(settingsPath);
        if (settings.IndexOf("\"Edition\": \"Ship\"", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Ship";
        if (settings.IndexOf("\"Edition\": \"Office\"", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Office";
        throw new InvalidDataException("Cannot recreate edition.txt because appsettings.json has no valid Edition value: " + settingsPath);
    }

    private static void StartService(string expectedExecutable)
    {
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = "start \"" + ServiceName + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using (Process process = Process.Start(info))
        {
            if (process == null)
                throw new InvalidOperationException("Windows could not start the " + ServiceName + " service process.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            if (process.ExitCode != 0 && process.ExitCode != 1056)
                throw new InvalidOperationException("Windows service startup failed for " + expectedExecutable +
                    " (sc.exe exit " + process.ExitCode + "): " + (error + " " + output).Trim());
        }
    }

    private static bool SiteIsReady()
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApplicationUrl);
            request.Method = "GET";
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                return (int)response.StatusCode >= 200 && (int)response.StatusCode < 500;
        }
        catch { return false; }
    }

    private static string GetErrorMessage(Exception exception)
    {
        Exception error = exception;
        while (error.InnerException != null) error = error.InnerException;
        return error.Message;
    }

    private static void LogFailure(string logPath, string installDirectory, string detail, Exception exception)
    {
        try
        {
            File.AppendAllText(logPath, DateTime.Now.ToString("O") + "  STARTUP FAILURE\r\n" +
                "Install directory: " + installDirectory + "\r\n" +
                "Working directory: " + Environment.CurrentDirectory + "\r\n" +
                "Error: " + detail + "\r\n" + exception + "\r\n\r\n");
        }
        catch { }
    }
}
