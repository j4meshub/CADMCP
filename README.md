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

TCP 使用 4 字节大端长度前缀和 UTF-8 JSON，单帧最大 8 MiB。插件每次手动开启服务时生成临时 256-bit 令牌并写入 `%LocalAppData%\CADMCP\runtime\session.json`；Node 会校验协议版本、严格一致的产品版本和 AutoCAD 进程是否仍存活。Git SHA 仅作为构建诊断信息，不参与兼容性判定。

## MCP 工具

`say_hello`、`get_current_document_info`、`get_selected_entities`、`query_entities`、`get_entity_details`、`set_selection`、`send_code_to_cad`、`get_execution_status`、`create_line`、`create_polyline`、`create_circle`、`create_text`，共 12 个工具。

全部工具默认启用，可在 Ribbon 的“设置”中逐项关闭。禁用项仍在 MCP 工具列表中，调用时返回 `tool_disabled`。

固定绘图工具的坐标和长度以毫米输入，支持 `ucs`（默认）和 `wcs`。批量 `items` 在单一事务中原子创建；指定的图层或线型不存在时整批失败，不隐式创建资源。

`get_entity_details` 按句柄读取几何、块属性和样式；`set_selection` 支持替换、追加、移除和清空预选集，不修改 DWG 实体。两个新工具均要求传入最近读取结果的 `documentToken`、`activeSpaceHandle`，防止切换图纸或空间后误用句柄。详见 [第一批工具说明](docs/FIRST_BATCH_TOOLS.md)。

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

要求 Node.js 24+、.NET SDK，以及本机 AutoCAD 2022。默认从 `C:\Program Files\Autodesk\AutoCAD 2022` 引用 `AcCoreMgd.dll`、`AcDbMgd.dll`、`AcMgd.dll`；也可设置 `ACAD2022_DIR`。这些宿主程序集均不复制到 Bundle。

```powershell
cd server
npm install
npm test

cd ..
$env:ACAD2022_DIR = 'C:\Program Files\Autodesk\AutoCAD 2022'
dotnet build CADMCP.sln -c Release
# 不需要 AutoCAD 的纯规则与设置迁移测试（.NET 8）
dotnet run --project tests/CADMCP.Core.Tests -c Release
```

本地发布：

```powershell
.\scripts\build-release.ps1
# 可选测试文件名后缀，不改变包内产品版本
.\scripts\build-release.ps1 -Label bugfix7
```

脚本从根目录 `version.json` 读取产品版本，生成 `artifacts\CADMCP-<版本>.bundle.zip`。npm 包位于 `server`，发布前应实时确认 `cadmcp-server` 名称可用。

## 版本管理

`version.json` 是产品版本、TCP 协议版本和设置 Schema 的唯一人工维护来源。产品版本严格统一应用于插件、CommandSet、Bundle 和 npm Server；协议与设置 Schema 只在各自契约变化时独立提升。版本修改必须使用：

```powershell
.\scripts\set-version.ps1 -ProductVersion 2.1.0
.\scripts\set-version.ps1 -ProtocolVersion 3
.\scripts\set-version.ps1 -SettingsSchemaVersion 2
.\scripts\test-version-consistency.ps1
```

两个 DLL 的程序集版本和文件版本跟随产品版本；信息版本及 Node 运行时标识采用 `<产品版本>+<12位Git SHA>`，有未提交修改时追加 `.dirty`。`PackageContents.xml` 的 `SchemaVersion="1.0"` 是 Autodesk Bundle 格式版本，不属于 CADMCP 产品版本。

## 安装与使用

1. 将构建出的 `CADMCP.bundle` 放入 `%AppData%\Autodesk\ApplicationPlugins\`。
2. 启动 AutoCAD 2022，在 `CADMCP` Ribbon 点击“开启服务”。服务状态不会跨启动持久化。
3. MCP 客户端以 `npx cadmcp-server` 或本仓库的 `node server/build/index.js` 作为 stdio 服务命令。

插件未签名。若 AutoCAD 阻止加载，请根据组织安全策略配置 `SECURELOAD` 与 `TRUSTEDPATHS`，只信任实际 Bundle 目录，不要关闭全局安全检查。

设置保存在 `%AppData%\CADMCP\settings.json`；日志和临时会话文件位于 `%LocalAppData%\CADMCP`。审计日志默认包含代码和参数并保留 30 天，可在设置中关闭源码/参数落盘。

真实宿主验收见 [AutoCAD 2022 验收清单](docs/AUTOCAD_E2E_CHECKLIST.md)。

## 许可证

MIT
