# CADMCP 开发入口

本文面向人工开发者和参与后续开发的 AI。不要仅模仿附近的工具实现：CAD 命令的执行环境会影响撤销、选择和锁，即使工具没有写入实体。

## 修改前必读

1. 根目录 `AGENTS.md`：版本与开发约束。
2. [执行上下文设计](docs/architecture/EXECUTION_CONTEXTS.md)：类别、原因、禁区与验收要求。
3. 对应工具契约：[第一批](docs/FIRST_BATCH_TOOLS.md)、[第二批](docs/SECOND_BATCH_TOOLS.md)。用户最新确认的需求优先于旧参考计划。

## 新增固定工具清单

- 明确是只读、选择、可预览写入还是一般命令，显式实现 `ICadCommand.ExecutionKind`。不要通过工具名称前缀、用户传入 readOnly 或代码内容猜测。
- 在 CommandSet 实现命令；共享逻辑放在相应框架中。不要把读路径与写路径重新合并。
- 更新 Server 的中英描述/Zod Schema，以及插件 `CadMcpSettings.ToolNames` 和设置中文说明。旧偏好需保留，新工具默认启用。
- 修改 Schema 时用真实 MCP `tools/list` 验证导出，不能只验证 Zod.parse。
- 保持文档/空间标识、完整选择快照、Handle、单位与坐标系约定。写入要预检、原子事务、提交前响应物化和容量检查；超时未知结果不得自动重试。
- 固定创建（FixedWrite）和固定修改使用调度器拥有的原生命令撤销单元，不叠加框架 ActiveX StartUndoMark/EndUndoMark；不要绕过调度器直接调用写入 Execute。创建工具没有 dryRun 契约，不要误标为 PreviewableWrite。
- 动态代码能力优先：auto 保留事务、none 自管，两者都走 CommandContext、undoGuaranteed=false；不以 UNDO 关闭/One/已有组为动态执行门槛，不添加实体白名单，不限制用户代码自管撤销，详见 ADR-003。不能因为撤销不保证就自动重试已经成功的调用。
- 增加纯逻辑测试和真实宿主回归。只读工具必须插入到已有写操作与一次撤销之间测试，不能仅检查“能返回数据”。
- 更新工具契约和人工验收清单；有新的执行类别或架构例外时，同步修订架构决策文档，写明原因。

## 检查命令

```powershell
npm --prefix server test
dotnet run --project tests/CADMCP.Core.Tests -c Release
dotnet build tests/CADMCP.AutoCAD.Tests -c Release
./scripts/test-version-consistency.ps1
git diff --check
```

AutoCAD 2022 的宿主验收只能在真实 CAD 内进行。安装匹配的整套 Bundle 后 NETLOAD 验收 DLL，运行 `CADMCP_TEST_SECOND_BATCH`；该入口新建并明确激活独立未保存图纸，核对身份与空闲状态后才创建测试实体，不操作业务原图。不要并发操作 CAD；激活失败/切换文档立即停止，原生回调中的异常须内部捕获，不能让异常越过非托管边界。完整清单见 [AUTOCAD_E2E_CHECKLIST](docs/AUTOCAD_E2E_CHECKLIST.md)。补做人工 Ctrl+Z，避免自动调用链掩盖撤销问题。

版本号只通过 `version.json` 和 `scripts/set-version.ps1` 维护，必须先得到用户明确目标版本。未给新版本时不要自行升级。不要只替换主 DLL：插件与内部 CommandSet 接口需匹配。

## 对 AI 开发者的要求

仓库代码能说明“怎么做”，架构文档说明“为什么不能换另一种做法”，测试检查“是否又破坏了保证”。请在任务交付中明确实际运行了哪些检查、哪些真实 CAD 场景尚未验证；不要将编译成功写成全部验收通过。
