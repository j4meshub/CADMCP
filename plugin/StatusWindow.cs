using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CADMCP.Plugin;

public sealed class StatusWindow : Window
{
    private readonly Func<bool> _isRunning;
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _timer;

    public StatusWindow(Func<bool> isRunning)
    {
        _isRunning = isRunning;
        Title = "CADMCP 状态"; Width = 560; Height = 350; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _status.Margin = new Thickness(18); _status.TextWrapping = TextWrapping.Wrap; _status.FontSize = 14;
        var close = new Button { Content = "关闭", Width = 90, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, __) => Close();
        var root = new DockPanel(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close); root.Children.Add(_status); Content = root;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, __) => Refresh(), Dispatcher);
        Closed += (_, __) => _timer.Stop(); Refresh(); _timer.Start();
    }

    private void Refresh()
    {
        var process = Process.GetCurrentProcess();
        var settings = SettingsStore.Current;
        var warning = RuntimeMetrics.DynamicCompilationCount >= settings.DynamicAssemblyWarningThreshold
            ? "\n提醒：动态程序集已达到提醒阈值，建议在合适时重启 AutoCAD。" : string.Empty;
        _status.Text = $"服务：{(_isRunning() ? "已开启" : "已关闭")}\n" +
            $"端口：{settings.Port}\n协议版本：1\n" +
            $"Roslyn：{(CompilerRuntimeStatus.IsReady ? "可用" : "不可用")} - {CompilerRuntimeStatus.Message}\n" +
            $"动态编译次数：{RuntimeMetrics.DynamicCompilationCount} / 提醒阈值 {settings.DynamicAssemblyWarningThreshold}\n" +
            $"AutoCAD 私有内存：{process.PrivateMemorySize64 / 1024 / 1024:N0} MiB\n" +
            $"AutoCAD 工作集：{process.WorkingSet64 / 1024 / 1024:N0} MiB\n" +
            $"会话文件：{RuntimePaths.SessionFile}{warning}";
    }
}
