using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Workspace.VSIntegration.Contracts;
using Pty.Net;
using StreamJsonRpc;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.Terminal.VsService
{
    internal sealed class EmbeddedTerminal : TerminalRenderer
    {
        private readonly EmbeddedTerminalOptions options;
        private readonly AsyncLazy<SolutionUtils> solutionUtils;
        private IPtyConnection pty;
        private bool isRpcDisconnected;

        public EmbeddedTerminal(TermWindowPackage package, TermWindowPane pane, EmbeddedTerminalOptions options) : base(package, pane)
        {
            this.options = options;

            if (options.WorkingDirectory == null)
            {
                // Use solution directory
                this.solutionUtils = new AsyncLazy<SolutionUtils>(GetSolutionUtilsAsync, package.JoinableTaskFactory);

                // Start getting solution utils but don't block on the result.
                // Solution utils need MEF and sometimes it takes long time to initialize.
                this.solutionUtils.GetValueAsync().FileAndForget("WhackWhackTerminal/GetSolutionUtils");
            }
        }

        protected internal override string SolutionDirectory =>
            this.options.WorkingDirectory ?? this.package.JoinableTaskFactory.Run(() => GetSolutionDirectoryAsync(this.package.DisposalToken));

        internal protected override void OnTerminalResized(int cols, int rows)
        {
            base.OnTerminalResized(cols, rows);
            ResizeTerm(cols, rows);
        }

        internal protected override void OnTerminalDataRecieved(string data)
        {
            base.OnTerminalDataRecieved(data);
            SendTermDataAsync(data).FileAndForget("WhackWhackTerminal/TermData");
        }

        internal protected override void OnTerminalClosed()
        {
            base.OnTerminalClosed();
            CloseTermAsync().FileAndForget("WhackWhackTerminal/closeTerm");
        }

        protected override void OnClosed()
        {
            base.OnClosed();
        }

        internal protected override void OnTerminalInit(object sender, TermInitEventArgs e)
        {
            base.OnTerminalInit(sender, e);
            InitTermAsync(e).FileAndForget("WhackWhackTerminal/InitPty");
        }

        private void ResizeTerm(int cols, int rows)
        {
            this.pty?.Resize(cols, rows);
        }

        private async Task SendTermDataAsync(string data)
        {
            if (this.pty != null)
            {
                await this.pty.WriteAsync(data);
            }
        }

        private async Task CloseTermAsync()
        {
            // TODO
        }

        private async Task InitTermAsync(TermInitEventArgs e)
        {
            var path = this.options.ShellPath ??
                (this.package.OptionTerminal == DefaultTerminal.Other ? this.package.OptionShellPath : this.package.OptionTerminal.ToString());
            var args = ((object)this.options.Args) ?? this.package.OptionStartupArgument;
            try
            {
                this.pty = PtyProvider.Spawn(@"powershell.exe", e.Cols, e.Rows, e.Directory, BackendOptions.ConPty);
            }
            catch (Exception err)
            {

            }
            this.pty.PtyData += (_, data) => this.PtyDataAsync(data, CancellationToken.None).FileAndForget("WhackWhackTerminal/InitPty");
            this.pty.PtyDisconnected += (_) => this.PtyExitedAsync(null, CancellationToken.None).FileAndForget("WhackWhackTerminal/Disconnected");
        }

        private async Task<SolutionUtils> GetSolutionUtilsAsync()
        {
            await this.package.JoinableTaskFactory.SwitchToMainThreadAsync(this.package.DisposalToken);
            var solutionService = (IVsSolution)await this.package.GetServiceAsync(typeof(SVsSolution));
            var componentModel = (IComponentModel)await this.package.GetServiceAsync(typeof(SComponentModel));
            var workspaceService = componentModel.GetService<IVsFolderWorkspaceService>();
            var result = new SolutionUtils(solutionService, workspaceService, this.package.JoinableTaskFactory);
            result.SolutionChanged += (sender, solutionDir) =>
            {
                if (package.OptionChangeDirectory)
                {
                    this.ChangeWorkingDirectoryAsync(solutionDir).FileAndForget("WhackWhackTerminal/changeWorkingDirectory");
                }
            };

            return result;
        }

        private async Task<string> GetSolutionDirectoryAsync(CancellationToken cancellationToken)
        {
            var solutionUtils = await this.solutionUtils.GetValueAsync(cancellationToken);
            return await solutionUtils.GetSolutionDirAsync(cancellationToken) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }
}
