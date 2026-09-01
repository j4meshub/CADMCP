# CADMCP

CADMCP 是一个面向个人受信本地环境的 AutoCAD 2022 插件。它通过通用 stdio MCP 让 Claude、Codex、Cline 等客户端控制当前活动 DWG，并保留最重要的能力：AI 可以通过 `send_code_to_cad` 在 AutoCAD 进程内动态编译并执行 C#。

## 支持边界

- Windows + AutoCAD 2022，插件目标为 .NET Framework 4.8。
- 固定工具只承诺二维绘图；其他二维、三维和自动化能力由动态 C# 调用完整 AutoCAD .NET API 实现。
- 仅操作当前 AutoCAD 进程中的当前活动 DWG 和活动空间，不维护 DWG 实体缓存数据库。
- 动态代码拥有插件同等的文件、网络、进程与 AutoCAD API 完整权限，不提供沙箱。只应在个人受信环境使用。
- 动态执行不支持长期等待 `Editor.GetPoint`、`GetSelection` 等用户交互。

## 架构

```text
MCP 客户端 ↔ stdio ↔ cadmcp-server ↔ 127.0.0.1 TCP/JSON-RPC ↔ CADMCP 插件 ↔ 当前 DWG
```

TCP 使用 4 字节大端长度前缀和 UTF-8 JSON，单帧最大 8 MiB。插件每次手动开启服务时生成临时 256-bit 令牌并写入 `%LocalAppData%\CADMCP\runtime\session.json`；Node 会校验协议版本和 AutoCAD 进程是否仍存活。

## MCP 工具

`say_hello`、`get_current_document_info`、`get_selected_entities`、`query_entities`、`send_code_to_cad`、`get_execution_status`、`create_line`、`create_polyline`、`create_circle`、`create_text`。

全部工具默认启用，可在 Ribbon 的“设置”中逐项关闭。禁用项仍在 MCP 工具列表中，调用时返回 `tool_disabled`。

固定绘图工具的坐标和长度以毫米输入，支持 `ucs`（默认）和 `wcs`。批量 `items` 在单一事务中原子创建；指定的图层或线型不存在时整批失败，不隐式创建资源。

## 动态 C#

`send_code_to_cad` 接收 C# 方法体、具名 JSON 参数、可选 using、本地 DLL 绝对路径和 `auto | none` 事务模式。包装器提供：

```csharp
document
database
editor
transaction
units
```

`auto` 会锁定文档并创建单一数据库事务；成功提交，异常回滚。`none` 仍锁定文档，但 `transaction` 为 `null`，副作用和撤销由代码负责。框架从不自动保存 DWG。

Node 最长等待 5 分钟。超时不会强杀 AutoCAD 线程，而会返回 `timeout_unknown` 和 `callId`，之后可用 `get_execution_status` 查询。动态程序集在进程内不可卸载；默认编译 100 次后提示重启，但不停止核心能力。

## 构建

要求 Node.js 20+、.NET SDK，以及本机 AutoCAD 2022。默认从 `C:\Program Files\Autodesk\AutoCAD 2022` 引用 `AcCoreMgd.dll`、`AcDbMgd.dll`、`AcMgd.dll`；也可设置 `ACAD2022_DIR`。这些宿主程序集均不复制到 Bundle。

```powershell
cd server
npm install
npm test

cd ..
$env:ACAD2022_DIR = 'C:\Program Files\Autodesk\AutoCAD 2022'
dotnet build CADMCP.sln -c Release
```

本地发布：

```powershell
.\scripts\build-release.ps1 -Version 1.0.0
```

生成 `artifacts\CADMCP-1.0.0.bundle.zip`。npm 包位于 `server`，发布前应实时确认 `cadmcp-server` 名称可用。

## 安装与使用

1. 将构建出的 `CADMCP.bundle` 放入 `%AppData%\Autodesk\ApplicationPlugins\`。
2. 启动 AutoCAD 2022，在 `CADMCP` Ribbon 点击“开启服务”。服务状态不会跨启动持久化。
3. MCP 客户端以 `npx cadmcp-server` 或本仓库的 `node server/build/index.js` 作为 stdio 服务命令。

插件 v1 未签名。若 AutoCAD 阻止加载，请根据组织安全策略配置 `SECURELOAD` 与 `TRUSTEDPATHS`，只信任实际 Bundle 目录，不要关闭全局安全检查。

设置保存在 `%AppData%\CADMCP\settings.json`；日志和临时会话文件位于 `%LocalAppData%\CADMCP`。审计日志默认包含代码和参数并保留 30 天，可在设置中关闭源码/参数落盘。

真实宿主验收见 [AutoCAD 2022 验收清单](docs/AUTOCAD_E2E_CHECKLIST.md)。

## 许可证

MIT
