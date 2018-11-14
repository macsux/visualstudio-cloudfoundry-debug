using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Process = System.Diagnostics.Process;
using Task = System.Threading.Tasks.Task;
using Thread = EnvDTE.Thread;

namespace CFDebug
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class DebugOnCF : IDebugEventCallback2
    {
        private readonly OleMenuCommandService _commandService;

        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("17006366-2bd4-49e0-ade9-4f4384f43adb");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        private static DTE _dte;
        private IVsDebugger3 _debugger;
        private Process _cfssh;
        private TaskCompletionSource<bool> _sshEstablished;
        private MemoryStream _textStream;

        /// <summary>
        /// Initializes a new instance of the <see cref="DebugOnCF"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private DebugOnCF(AsyncPackage package, OleMenuCommandService commandService)
        {
            var debugger = (IVsDebugger)package.GetServiceAsync(typeof(IVsDebugger)).Result;
            _debugger = (IVsDebugger3)debugger;
            if (debugger != null)
                debugger.AdviseDebugEventCallback(this);
            _commandService = commandService;
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static DebugOnCF Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in DebugOnCF's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            
            Instance = new DebugOnCF(package, commandService);
            _dte = (DTE) (await package.GetServiceAsync(typeof(DTE)));
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Show a message box to prove we were here
            //            VsShellUtilities.ShowMessageBox(
            //                this.package,
            //                message,
            //                title,
            //                OLEMSGICON.OLEMSGICON_INFO,
            //                OLEMSGBUTTON.OLEMSGBUTTON_OK,
            //                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);


            
            //_dte.ExecuteCommand("DebugAdapterHost.Launch", "/LaunchJson:launch.json");
            var commands = _dte.Commands.Cast<Command>().Select(x => x.Name).ToList();
            var projects = _dte.Solution.Projects;
            var startupProjectNames = ((object[])_dte.Solution.SolutionBuild.StartupProjects).Cast<string>().ToList();
            // we only gonna debug if single project is selected
            if (startupProjectNames.Count != 1)
            {
                VsShellUtilities.ShowMessageBox(
                    this.package,
                    "Please select a single startup project to debug",
                    "Error",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }
            
            

            var project = projects.Cast<Project>().First(x => Path.GetFileName(x.FileName) == Path.GetFileName(startupProjectNames[0]));
            var fullPath = (string)project.Properties.Cast<Property>().First(x => x.Name == "FullPath").Value;
//            var fullPath = (string)properties["FullPath"];
            var manifestPath = Path.Combine(fullPath, "manifest.yml");
            if (!File.Exists(manifestPath))
            {
                VsShellUtilities.ShowMessageBox(
                    this.package,
                    "Startup project must hava a manifest.yml defining only single app that will be debugged",
                    "Error",
                    OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var manifest = GetManifestInfo(manifestPath);
            if (manifest.Stack.IsLinux())
            {
                EnsureCoreDebuggingToolsInstalled(manifest.AppName);
                string launchPath = CreateLaunchJson(manifest.AppName);
                _dte.ExecuteCommand("DebugAdapterHost.Launch", $"/LaunchJson:{launchPath}");
            }
            else
            {

//                AttachToRemoteProcess("localhost", "hwc.exe");
                CreateDebugSession();
                AttachToRemoteProcess2();
            }


        }

        private void CreateDebugSession()
        {
            //powershell (new-object net.webclient).DownloadFile('http://bit.do/eAJtN','debugger.ps1');./debugger.ps1
            _sshEstablished = new TaskCompletionSource<bool>();
            var sb = new StringBuilder();
            _cfssh = new System.Diagnostics.Process();
            _cfssh.StartInfo.CreateNoWindow = true;
            _cfssh.StartInfo.FileName = "cf.exe";
            _cfssh.StartInfo.Arguments = "ssh funnyquotes -L 4020:localhost:4020";
            _cfssh.StartInfo.UseShellExecute = false;
            _cfssh.StartInfo.RedirectStandardOutput = true;
            _cfssh.StartInfo.RedirectStandardError = true;
            _cfssh.StartInfo.RedirectStandardInput = true;
//            _textStream = new MemoryStream();
////            _cfssh.OutputDataReceived += OnSshSessionEstablished;
            _cfssh.OutputDataReceived += (sender, args) =>
            {
                if (args.Data == null)
                    return;
                if(args.Data.ToLower().StartsWith("fail"))
                    _sshEstablished.SetResult(false);
                if (args.Data == "(c) 2018 Microsoft Corporation. All rights reserved.")
                {
                    _cfssh.StandardInput.WriteLine("powershell (new-object net.webclient).DownloadFile('http://bit.do/eAJtN','debugger.ps1');./debugger.ps1\n");
                }
                if (args.Data == @"C:\Users\vcap>")
                {
                    _sshEstablished.TrySetResult(true);
                }
            };
//            
            
            _cfssh.Start();
            _cfssh.StandardInput.AutoFlush = true;
            var job = new Job();
            job.AddProcess(_cfssh.Handle);
            _cfssh.BeginOutputReadLine();
            
//            var connectResponse = ReadBlock(_cfssh.StandardOutput);
//            _cfssh.StandardInput.WriteLine("powershell (new-object net.webclient).DownloadFile('http://bit.do/eAJtN','debugger.ps1');./debugger.ps1\n");
//            _cfssh.StandardInput.Flush();
//            _cfssh.StandardError.ReadLine();
//            System.Threading.Thread.Sleep(100);
//            var installResponse = ReadBlock(_cfssh.StandardOutput);
            _sshEstablished.Task.Wait();
        }

        private string ReadBlock(StreamReader sr)
        {
            var sb = new StringBuilder();
            DateTime lastDataReceived = DateTime.MaxValue.AddDays(-1);
            
            var buffer = new char[1024];
            sr.BaseStream.ReadTimeout = 1000;
            while (true)
            {
                //buffer,0, buffer.Length

                var line = sr.ReadLine();
                if (line != null)
                {
                    lastDataReceived = DateTime.Now;
                    sb.AppendLine(line);
                }

                if (lastDataReceived.AddSeconds(1) < DateTime.Now)
                    break;
                
                
                System.Threading.Thread.Sleep(10);

            }
            return sb.ToString();
        }

        private void OnSshSessionEstablished(object sender, DataReceivedEventArgs e)
        {
            _cfssh.OutputDataReceived -= OnSshSessionEstablished;
            System.Threading.Thread.Sleep(2000);
            System.Diagnostics.Debugger.Log(1, "Test", e.Data);
            _cfssh.StandardInput.WriteLine("powershell (new-object net.webclient).DownloadFile('http://bit.do/eAJtN','debugger.ps1');./debugger.ps1");
            _cfssh.OutputDataReceived += OnDebuggingToolsInitialized;
        }
        private void OnDebuggingToolsInitialized(object sender, DataReceivedEventArgs e)
        {
            System.Threading.Thread.Sleep(500);
            _cfssh.OutputDataReceived -= OnDebuggingToolsInitialized;
            _sshEstablished.SetResult(true);
        }


        private void AttachToRemoteProcess2()
        {
            _dte.Events.DebuggerEvents.OnContextChanged += (newProcess, program, thread, frame) => System.Diagnostics.Debugger.Break();
            var debugger = _dte.Debugger as EnvDTE80.Debugger2;
            
            var transport = debugger.Transports.Item("Remote");
//            var t = debugger.Transports.Item("default");
//            foreach(Engine e in t.Engines)
//            {
//                Console.WriteLine(e.Name);
//            }
            //
            //            var engines = transport.Engines.Cast<Engine>().Select(x => x.Name).ToList();
            var processList = debugger.GetProcesses(transport, "localhost:4020");
            var process = processList.Item("hwc.exe") as EnvDTE80.Process2;
            var engine = transport.Engines.Item("Managed (v4.6, v4.5, v4.0)");
            process.Attach2(engine);
        }
        
        private CFManifestInfo GetManifestInfo(string manifest)
        {
            var manifestContent = File.ReadAllText(manifest);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(new CamelCaseNamingConvention())
                .Build();
            var manifestDynamic = deserializer.Deserialize<dynamic>(new StringReader(manifestContent));
            var appManifestProperties = (Dictionary<object, object>) manifestDynamic["applications"][0];
            var name = (string)appManifestProperties["name"];
            if (!appManifestProperties.TryGetValue("stack", out object stackName) || !Enum.TryParse((string)stackName, true, out Stack stack))
            {
                stack = Stack.Cflinuxfs2;
            }
//            var stackName = appManifestProperties["stack"];
//            if (!Enum.TryParse(stackName, true, out Stack stack))
//            {
//                stack = Stack.Cflinuxfs2;
//            }
            return new CFManifestInfo{ AppName = name, Stack = stack };
        }
        private string CreateLaunchJson(string cfAppName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "CFDebug.launch.json";
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream))
            {
                string result = reader.ReadToEnd();
                result = result.Replace("APP_NAME", cfAppName);
                var filename = Path.GetTempFileName();
                File.WriteAllText(filename, result);
                return filename;
            }
        }

        public void EnsureCoreDebuggingToolsInstalled(string appName)
        {
            ExecuteProcess("cf", $"ssh {appName} -c \"curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/app/vsdbg\"");
        }
        

        
        public string ExecuteProcess(string app, string arguments)
        {
            var sb = new StringBuilder();
            var process = new System.Diagnostics.Process();
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.FileName = app;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.OutputDataReceived += (sender, args) => sb.Append(args.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.WaitForExit();
            return sb.ToString();
        }

        public System.Diagnostics.Process LaunchProcess(string app, string arguments)
        {
            var sb = new StringBuilder();
            var process = new System.Diagnostics.Process();
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.FileName = app;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.OutputDataReceived += (sender, args) => sb.Append(args.Data);
            process.Start();
            process.BeginOutputReadLine();
            return process;
        }

        public int Event(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent, ref Guid riidEvent, uint dwAttrib)
        {
            IVsDebugger debugger = (IVsDebugger)_debugger;
            if (pEvent is IDebugProgramDestroyEvent2)
            {
                // The process has exited

                uint exitCode;
                if (((IDebugProgramDestroyEvent2)pEvent).GetExitCode(out exitCode) == VSConstants.S_OK)
                {
                    _cfssh?.Kill();
                }

                // Stop listening for future exit events
                //debugger.UnadviseDebugEventCallback(this);
            }
            return VSConstants.S_OK;
        }
    }
    public struct CFManifestInfo
    {
        public string AppName { get; set; }
        public Stack Stack { get; set; }
    }

    public enum Stack
    {
        Windows2016,
        Windows2012R2,
        Cflinuxfs2,
        Cflinuxfs3,
    }

    public static class StackExtensions
    {
        public static bool IsLinux(this Stack stack) => stack == Stack.Cflinuxfs2 || stack == Stack.Cflinuxfs3;
    }
}
