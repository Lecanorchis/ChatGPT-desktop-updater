using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

[assembly: AssemblyTitle("ChatGPT Desktop MSIX Updater")]
[assembly: AssemblyVersion("1.0.0.0")]

internal static class Launcher
{
    private const string ScriptName = "Install-ChatGPT.ps1";

    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ChatGPT-Updater-" + Guid.NewGuid().ToString("N"));
        string script = Path.Combine(directory, ScriptName);
        try
        {
            Directory.CreateDirectory(directory);
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptName))
            using (FileStream file = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(file);
            }

            string systemDirectory = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
                ? "System32" : "Sysnative";
            string powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

            var arguments = new List<string>
            {
                "-NoProfile", "-File", script, "-LauncherPath", Assembly.GetExecutingAssembly().Location
            };
            arguments.AddRange(args.Length == 0 ? new[] { "-Pause" } : args);
            var startInfo = new ProcessStartInfo(powerShell, string.Join(" ", arguments.ConvertAll(QuoteArgument)))
            {
                UseShellExecute = false
            };
            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (args.Length == 0 && !Console.IsInputRedirected)
            {
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
            return 1;
        }
        finally
        {
            try
            {
                File.Delete(script);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string QuoteArgument(string value)
    {
        // Escape quotes and any backslashes that immediately precede a quote.
        return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", "$1$1$2")
            .Replace("\"", "\\\"") + "\"";
    }
}
