using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CADMCP.Plugin;

public sealed class SettingsWindow : Window
{
    private readonly TextBox _port = new(); private readonly ComboBox _unit = new(); private readonly TextBox _threshold = new();
    private readonly CheckBox _logCode = new(); private readonly TextBox _retention = new(); private readonly Dictionary<string, CheckBox> _tools = new(); private readonly bool _running;
    public CadMcpSettings Result { get; private set; }
    public SettingsWindow(bool running)
    {
        _running = running; Result = SettingsStore.Current; Title = "CADMCP 设置"; Width = 620; Height = 570; WindowStartupLocation = WindowStartupLocation.CenterScreen; ResizeMode = ResizeMode.NoResize;
        var tabs = new TabControl { Margin = new Thickness(12) }; tabs.Items.Add(Tab("服务", ServicePage())); tabs.Items.Add(Tab("功能", FeaturePage())); tabs.Items.Add(Tab("日志", LogPage())); tabs.Items.Add(Tab("关于", AboutPage()));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var ok = new Button { Content = "保存", Width = 90, Margin = new Thickness(6) }; ok.Click += Save; var cancel = new Button { Content = "取消", Width = 90, Margin = new Thickness(6) }; cancel.Click += (_, __) => Close(); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons); root.Children.Add(tabs); Content = root;
    }
    private UIElement ServicePage()
    {
        _port.Text = Result.Port.ToString(); _port.IsEnabled = !_running; _unit.ItemsSource = new[] { "Millimeters", "Meters", "Inches" }; _unit.SelectedItem = Result.UnitlessDefaultUnit; _threshold.Text = Result.DynamicAssemblyWarningThreshold.ToString();
        var panel = Stack(); AddRow(panel, "TCP 端口（服务关闭时可改）", _port); AddRow(panel, "Unitless 默认单位", _unit); AddRow(panel, "动态程序集提醒阈值", _threshold); return panel;
    }
    private UIElement FeaturePage()
    {
        var descriptions = new Dictionary<string, string> { ["say_hello"]="测试连接", ["get_current_document_info"]="当前 DWG 信息", ["get_selected_entities"]="读取预选实体", ["query_entities"]="查询活动空间实体", ["send_code_to_cad"]="动态编译执行 C#（完整权限）", ["get_execution_status"]="查询超时调用状态", ["create_line"]="批量创建直线", ["create_polyline"]="批量创建二维多段线", ["create_circle"]="批量创建圆", ["create_text"]="批量创建 MText" };
        descriptions["get_entity_details"] = "按句柄读取实体详情";
        descriptions["set_selection"] = "精确设置当前选择集（不修改图形）";
        descriptions["clone_entities"] = "原样复制实体（支持只读预览）";
        descriptions["transform_entities"] = "移动、旋转、等比缩放、几何镜像";
        var panel = Stack(); foreach (var name in CadMcpSettings.ToolNames) { var box = new CheckBox { Content = name + " — " + descriptions[name], IsChecked = Result.IsEnabled(name), Margin = new Thickness(4) }; _tools[name] = box; panel.Children.Add(box); } return new ScrollViewer { Content = panel };
    }
    private UIElement LogPage() { _logCode.Content = "记录完整代码和参数（默认开启）"; _logCode.IsChecked = Result.LogSourceAndParameters; _retention.Text = Result.LogRetentionDays.ToString(); var panel = Stack(); panel.Children.Add(_logCode); AddRow(panel, "日志保留天数", _retention); return panel; }
    private UIElement AboutPage()
    {
        var panel = Stack(); panel.Children.Add(new TextBlock { Text = $"CADMCP {CadMcpVersion.BuildVersion}\n协议版本 {CadMcpVersion.ProtocolVersion} / 设置 Schema {CadMcpVersion.SettingsSchemaVersion}\nAutoCAD 2022 / .NET Framework 4.8\n\n风险提示：send_code_to_cad 动态代码与插件具有相同的文件、网络、进程和 AutoCAD API 完整权限。仅在个人受信本地环境使用。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
        foreach (var item in new[] { ("打开插件目录", RuntimePaths.PluginDirectory), ("打开设置目录", RuntimePaths.SettingsDirectory), ("打开日志目录", RuntimePaths.LogDirectory) }) { var button = new Button { Content = item.Item1, Margin = new Thickness(4), Width = 140 }; button.Click += (_, __) => Open(item.Item2); panel.Children.Add(button); } return panel;
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(_port.Text, out var port) || port < 1024 || port > 65535 || !int.TryParse(_threshold.Text, out var threshold) || threshold < 1 || !int.TryParse(_retention.Text, out var days) || days < 1) { MessageBox.Show("请检查端口、提醒阈值和日志天数。", "CADMCP"); return; }
        var value = new CadMcpSettings { Port = _running ? Result.Port : port, UnitlessDefaultUnit = Convert.ToString(_unit.SelectedItem) ?? "Millimeters", DynamicAssemblyWarningThreshold = threshold, LogSourceAndParameters = _logCode.IsChecked == true, LogRetentionDays = days };
        foreach (var pair in _tools) value.EnabledTools[pair.Key] = pair.Value.IsChecked == true; Result = value; DialogResult = true;
    }
    private static TabItem Tab(string title, UIElement content) => new() { Header = title, Content = content };
    private static StackPanel Stack() => new() { Margin = new Thickness(14) };
    private static void AddRow(Panel panel, string label, Control control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 12, 4, 3) }); control.Margin = new Thickness(4); control.Width = 260; control.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(control); }
    private static void Open(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
}
