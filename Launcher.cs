using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("ChatGPT Desktop MSIX Updater")]
[assembly: AssemblyVersion("1.1.0.0")]

internal static class Launcher
{
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            FreeConsole();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new UpdaterForm())
                Application.Run(form);
            return 0;
        }

        try
        {
            bool hiddenWorker = args[0] == "--worker";
            if (hiddenWorker)
            {
                FreeConsole();
                var workerArgs = new string[args.Length - 1];
                Array.Copy(args, 1, workerArgs, 0, workerArgs.Length);
                args = workerArgs;
            }
            using (var worker = new PowerShellWorker())
                return worker.RunConsole(args, hiddenWorker);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

internal sealed class PowerShellWorker : IDisposable
{
    private const string ScriptName = "Install-ChatGPT.ps1";
    internal static readonly string Executable = Assembly.GetExecutingAssembly().Location;
    private readonly string script;

    internal PowerShellWorker()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ChatGPT-Updater-" + Guid.NewGuid().ToString("N"));
        script = Path.Combine(directory, ScriptName);
        Directory.CreateDirectory(directory);
        try
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptName))
            using (FileStream file = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(file);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal int RunConsole(string[] arguments, bool hidden)
    {
        using (Process process = Process.Start(CreateStartInfo(arguments, hidden)))
        {
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    internal static Task<WorkerEvent> RunAsync(string[] arguments, bool elevate, IProgress<WorkerEvent> progress)
    {
        return Task.Run(() => RunUi(arguments, elevate, progress));
    }

    private static WorkerEvent RunUi(string[] arguments, bool elevate, IProgress<WorkerEvent> progress)
    {
        string name = "ChatGPT-Updater-" + Guid.NewGuid().ToString("N");
        SecurityIdentifier sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            sid = identity.User;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.Write, AccessControlType.Allow));

        var args = new List<string>(arguments) { "-UiPipe", name, "-CallerSid", sid.Value };
        // The elevated process extracts its own embedded script.
        using (var worker = elevate ? null : new PowerShellWorker())
        using (var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 0, security))
        {
            ProcessStartInfo start;
            if (elevate)
            {
                args.Insert(0, "--worker");
                start = new ProcessStartInfo(Executable, string.Join(" ", args.ConvertAll(QuoteArgument)))
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                };
            }
            else
            {
                start = worker.CreateStartInfo(args, true);
                start.RedirectStandardError = true;
            }
            return ReadWorkerEvents(pipe, start, progress);
        }
    }

    private static WorkerEvent ReadWorkerEvents(NamedPipeServerStream pipe, ProcessStartInfo start,
        IProgress<WorkerEvent> progress)
    {
        IAsyncResult connected = pipe.BeginWaitForConnection(null, null);
        using (Process process = Process.Start(start))
        {
            Task<string> stderr = start.RedirectStandardError ? process.StandardError.ReadToEndAsync() : null;
            while (!connected.IsCompleted && !process.WaitForExit(100)) { }
            if (!connected.IsCompleted)
            {
                string detail = stderr == null ? null : stderr.GetAwaiter().GetResult().Trim();
                throw new WorkerException(process.ExitCode, detail);
            }
            pipe.EndWaitForConnection(connected);

            var json = new JavaScriptSerializer();
            WorkerEvent result = null;
            using (var reader = new StreamReader(pipe, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var message = json.Deserialize<WorkerEvent>(line);
                    switch (message.Kind)
                    {
                        case "Download":
                        case "Verify":
                        case "Install":
                            if (progress != null) progress.Report(message);
                            break;
                        default:
                            result = message;
                            break;
                    }
                }
            }
            process.WaitForExit();
            if (process.ExitCode != 0 || result == null || result.Kind == "Error")
                throw new WorkerException(process.ExitCode, result == null ? null : result.Message);
            return result;
        }
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments, bool hidden)
    {
        string systemDirectory = Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem
            ? "System32" : "Sysnative";
        string powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var args = new List<string> { "-NoProfile" };
        if (hidden) args.Add("-NonInteractive");
        args.AddRange(new[] { "-File", script, "-LauncherPath", Executable });
        args.AddRange(arguments);
        return new ProcessStartInfo(powerShell, string.Join(" ", args.ConvertAll(QuoteArgument)))
        {
            UseShellExecute = false, CreateNoWindow = hidden
        };
    }

    private static string QuoteArgument(string value)
    {
        // Escape quotes and any backslashes that immediately precede a quote.
        return "\"" + Regex.Replace(value, "(\\\\*)(\"|$)", "$1$1$2")
            .Replace("\"", "\\\"") + "\"";
    }

    public void Dispose()
    {
        try
        {
            File.Delete(script);
            Directory.Delete(Path.GetDirectoryName(script));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class WorkerEvent
{
    public string Kind { get; set; }
    public string Message { get; set; }
    public string Installed { get; set; }
    public string Available { get; set; }
    public bool NeedsUpdate { get; set; }
    public bool Updated { get; set; }
    public string AppId { get; set; }
    public int Percent { get; set; }
    public bool AtLogon { get; set; }
    public string DailyAt { get; set; }
}

internal sealed class WorkerException : Exception
{
    internal readonly int ExitCode;
    internal readonly string Detail;

    internal WorkerException(int exitCode, string detail) : base(detail)
    {
        ExitCode = exitCode;
        Detail = detail;
    }
}
