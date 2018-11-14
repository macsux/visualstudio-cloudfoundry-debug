using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Extensibility;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CFDebug
{
    public class Connect : IDTExtensibility2, IDTCommandTarget
    {
        private DTE2 applicationObject;
        private AddIn addInInstance;

        public Connect() { }

        private void AttachToRemoteProcess(string machineName, string processName)
        {
            IVsDebugger3 vsDebugger = Package.GetGlobalService(typeof(IVsDebugger)) as IVsDebugger3;
            VsDebugTargetInfo3[] arrDebugTargetInfo = new VsDebugTargetInfo3[1];
            VsDebugTargetProcessInfo[] arrTargetProcessInfo = new VsDebugTargetProcessInfo[1];

            arrDebugTargetInfo[0].bstrExe = processName;
            arrDebugTargetInfo[0].bstrRemoteMachine = machineName;
            
            arrDebugTargetInfo[0].dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_AlreadyRunning;
            arrDebugTargetInfo[0].guidLaunchDebugEngine = Guid.Empty;
            arrDebugTargetInfo[0].dwDebugEngineCount = 1;

            Guid guidDbgEngine = VSConstants.DebugEnginesGuids.ManagedAndNative_guid;
            IntPtr pGuids = Marshal.AllocCoTaskMem(Marshal.SizeOf(guidDbgEngine));
            Marshal.StructureToPtr(guidDbgEngine, pGuids, false);
            arrDebugTargetInfo[0].pDebugEngines = pGuids;
            int hr = vsDebugger.LaunchDebugTargets3(1, arrDebugTargetInfo, arrTargetProcessInfo);

            // cleanup
            if (pGuids != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pGuids);
        }

        public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
        {
            applicationObject = (DTE2)application;
            addInInstance = (AddIn)addInInst;

            if (connectMode == ext_ConnectMode.ext_cm_UISetup)
            {
                object[] contextGUIDS = new object[] { };
                Commands2 commands = (Commands2)applicationObject.Commands;
                Microsoft.VisualStudio.CommandBars.CommandBar menuBarCommandBar = ((Microsoft.VisualStudio.CommandBars.CommandBars)applicationObject.CommandBars)["MenuBar"];
                CommandBarControl toolsControl = menuBarCommandBar.Controls["Tools"];
                CommandBarPopup toolsPopup = (CommandBarPopup)toolsControl;

                try
                {
                    Command command = commands.AddNamedCommand2(addInInstance, "AutoAttachAddin", "Auto Attach", "Attach to remote process", true, 59, ref contextGUIDS, (int)vsCommandStatus.vsCommandStatusSupported + (int)vsCommandStatus.vsCommandStatusEnabled, (int)vsCommandStyle.vsCommandStylePictAndText, vsCommandControlType.vsCommandControlTypeButton);
                    if ((command != null) && (toolsPopup != null))
                        command.AddControl(toolsPopup.CommandBar, 1);
                }
                catch (System.ArgumentException) { }
            }
        }

        public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom) { }
        public void OnAddInsUpdate(ref Array custom) { }
        public void OnStartupComplete(ref Array custom) { }
        public void OnBeginShutdown(ref Array custom) { }

        public void QueryStatus(string commandName, vsCommandStatusTextWanted neededText, ref vsCommandStatus status, ref object commandText)
        {
            if (neededText == vsCommandStatusTextWanted.vsCommandStatusTextWantedNone)
            {
                if (commandName == "AutoAttachAddin.Connect.AutoAttachAddin")
                {
                    status = (vsCommandStatus)vsCommandStatus.vsCommandStatusSupported | vsCommandStatus.vsCommandStatusEnabled;
                    return;
                }
            }
        }

        public void Exec(string commandName, vsCommandExecOption executeOption, ref object varIn, ref object varOut, ref bool handled)
        {
            handled = false;
            if (executeOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
            {
                if (commandName == "AutoAttachAddin.Connect.AutoAttachAddin")
                {
                    AttachToRemoteProcess("EDDO-380", "DebugMe.exe");
                    handled = true;
                    return;
                }
            }
        }
    }
}

