using System;
using System.IO;
using System.Windows.Input;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(CADMCP.Plugin.CadMcpApplication))]
[assembly: CommandClass(typeof(CADMCP.Plugin.CadMcpApplication))]

namespace CADMCP.Plugin;

public sealed class CadMcpApplication : IExtensionApplication
{
    private static TcpService? _service;
    private static RibbonButton? _toggleButton;
    private static StatusWindow? _statusWindow;
    private static PrivateAssemblyResolver? _assemblyResolver;

    public void Initialize()
    {
        try
        {
            SettingsStore.Load();
            _assemblyResolver = new PrivateAssemblyResolver(Path.Combine(RuntimePaths.PluginDirectory, "Commands"));
            _assemblyResolver.Install();
            System.Exception? compilerInitializationError = null;
            try { _assemblyResolver.Preload(); }
            catch (System.Exception error)
            {
                compilerInitializationError = error;
                CompilerRuntimeStatus.MarkFailed("Roslyn 私有依赖预加载失败，send_code_to_cad 暂不可用", error);
            }

            var registry = new CommandRegistry();
            registry.Load();
            if (compilerInitializationError == null)
            {
                try { CompilerRuntimeStatus.MarkReady(_assemblyResolver.RunRoslynSelfTest()); }
                catch (System.Exception compilerError)
                {
                    CompilerRuntimeStatus.MarkFailed("Roslyn 初始化自检失败，send_code_to_cad 暂不可用", compilerError);
                }
            }

            _service = new TcpService(new CadDispatcher(registry));
            ComponentManager.ItemInitialized += RibbonInitialized;
            CreateRibbon();
        }
        catch (System.Exception error)
        {
            CompilerRuntimeStatus.MarkFailed("CADMCP 私有依赖初始化失败", error);
            AcApp.ShowAlertDialog("CADMCP 初始化失败：\n" + error.Message);
        }
    }

    public void Terminate()
    {
        ComponentManager.ItemInitialized -= RibbonInitialized;
        _statusWindow?.Close();
        _statusWindow = null;
        _service?.Dispose();
        _service = null;
        _assemblyResolver?.Dispose();
        _assemblyResolver = null;
    }

    private static void RibbonInitialized(object sender, RibbonItemEventArgs e) => CreateRibbon();

    private static void CreateRibbon()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null) return;
        foreach (RibbonTab existing in ribbon.Tabs) if (existing.Id == "CADMCP_TAB") return;
        var tab = new RibbonTab { Id = "CADMCP_TAB", Title = "CADMCP" };
        ribbon.Tabs.Add(tab);
        var source = new RibbonPanelSource { Title = "AI 控制" };
        tab.Panels.Add(new RibbonPanel { Source = source });
        _toggleButton = Button("开启服务", "CADMCP_TOGGLE");
        source.Items.Add(_toggleButton);
        source.Items.Add(Button("设置", "CADMCP_SETTINGS"));
        source.Items.Add(Button("状态", "CADMCP_STATUS"));
    }

    private static RibbonButton Button(string text, string command) => new()
    {
        Text = text, ShowText = true, Size = RibbonItemSize.Large,
        Orientation = System.Windows.Controls.Orientation.Vertical, CommandHandler = new RibbonCommand(command)
    };

    [CommandMethod("CADMCP_TOGGLE", CommandFlags.Session)]
    public void ToggleService()
    {
        try
        {
            if (_service == null) throw new InvalidOperationException("服务尚未初始化");
            if (_service.IsRunning) _service.Stop(); else _service.Start();
            UpdateToggle();
            AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(_service.IsRunning ? "\nCADMCP 服务已开启。" : "\nCADMCP 服务已关闭。");
        }
        catch (System.Exception error) { AcApp.ShowAlertDialog("CADMCP 服务切换失败：\n" + error.Message); }
    }

    [CommandMethod("CADMCP_SETTINGS", CommandFlags.Session)]
    public void OpenSettings()
    {
        var window = new SettingsWindow(_service?.IsRunning == true);
        if (window.ShowDialog() == true) SettingsStore.Save(window.Result);
        UpdateToggle();
    }

    [CommandMethod("CADMCP_STATUS", CommandFlags.Session)]
    public void ShowStatus()
    {
        if (_statusWindow != null) { _statusWindow.Activate(); return; }
        _statusWindow = new StatusWindow(() => _service?.IsRunning == true);
        _statusWindow.Closed += (_, __) => _statusWindow = null;
        AcApp.ShowModelessWindow(_statusWindow);
    }

    private static void UpdateToggle()
    {
        if (_toggleButton != null) _toggleButton.Text = _service?.IsRunning == true ? "关闭服务" : "开启服务";
    }

    private sealed class RibbonCommand : ICommand
    {
        private readonly string _command;
        public RibbonCommand(string command) => _command = command;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(_command + " ", true, false, false);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
