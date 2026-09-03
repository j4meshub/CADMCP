# 分组真实宿主验收（仅开发使用）

此项目不随 Bundle 发布，不加入普通 CI，不重编译生产 Plugin/CommandSet。它引用本机已有的 Release DLL，必须先确认这些 DLL 与正在运行的 CADMCP 一致。

## 安全约束

- 运行前完整阅读仓库 `CONTRIBUTING.md` 和 `docs/architecture/EXECUTION_CONTEXTS.md`。
- 必须由用户授权真实 CAD 测试；用户在测试期间不要操作 CAD。
- 从当前活动文档捕获并校验 `documentToken`，每组自动创建、显式激活并验证独立的未保存测试图。
- 所有 CAD 对象访问通过应用线程或安全命令回调。异常在 native callback 内捕获。
- 每次 U 为一个原生命令，等待完成事件；不把 U 放进另一个命令回调，不重试结果未知的写入或 U。
- 如果连续撤销失败，保留失败断言，不追加 U 将其冒充通过。随后只清理已知的测试对象。
- 每组结束恢复原活动图，核对 token、DBMOD、单位、UCS、实体数和选择。切图可能清空预选；保留恢复前证据，再通过真实 set_selection 路径恢复原选择并记录结果。测试图保留未保存状态，便于检查。
- 不保存原图，不改变插件设置，不修改生产注册表；故障注入只替换本测试实例的注册表。

## 分组

- `bulk`：200/1000/2000 目标复制和移动、预览、单次撤销、处理中途失败回滚、2001 选择目标拒绝。
- `complex`：16 类合成实体、普通块属性、真实动态块导入和复制/移动、不可编辑目标拒绝、MIRRTEXT 0/1。
- `environment`：四种单位 × 三种 UCS、四个创建工具、复制和四种变换、文档/空间切换、旧工具连续撤销、Roslyn 回归、执行槽 busy。
- `environment_followup`：只运行文档/空间与旧工具/Roslyn 段。用于继续已经完成前面单位矩阵的验收，不覆盖或改写早期失败报告。
- `undo_policy`：安装 bugfix3 后运行四个创建工具的同类/混合连续撤销、读取/预览/选择干扰、资源/中途异常、提交后选择失败、动态 auto/none 与3D/原生命令/选择/DLL 能力。
- `undo_conditions`：仅在本组新建并验证所有权的测试图中切换 UNDO None/One/Begin。四个固定创建工具均须在写入前拒绝；动态 auto/none 实际创建实体且不改写撤销设置。另验证用户代码自行调用 ActiveX 撤销标记并管理事务。该 C# dynamic 样例显式引用运行时 Microsoft.CSharp.dll，不依赖 CAD 先前是否加载了运行时绑定器。每个原生命令等待完成事件，结果未知时停止且不重试；正常结束恢复完整撤销，不改变原图撤销设置。

`complex` 当前使用此次已授权测试图中的动态块 Handle `2AF3FD` 和 `2AF433`。其他图纸不得直接照搬；需先只读确认测试数据并调整测试夹具。这是测试样本，不是生产功能约束。

## 构建和调度

为每次重新编译使用新的程序集名称和输出目录；已加载 DLL 不能覆盖，也不能假定同名新文件会在 AutoCAD 内重新加载。

```powershell
dotnet build tests/CADMCP.Acceptance -c Release --no-restore -p:AssemblyName=CADMCP.Acceptance.Run001 --output artifacts/acceptance-run001
```

通过 `send_code_to_cad(transactionMode="none")` 在活动文档启动器中加载该 DLL，再反射调用 `new CADMCP.Acceptance.AcceptanceRun().Start(expectedDocumentToken, group)`。Start 立即返回报告路径，实际运行等待启动命令结束后开始。不要把启动器返回成功当作测试通过。

运行期间只读取磁盘报告，不同时调用其他 CAD 工具。每个报告有 `status`、`phase`、`checks`、实际统一响应、命令事件、原图前后状态、`finishedAtUtc`；`failed` 包括断言失败和测试夹具异常，需区分原因。

布局名从 LayoutDictionary 读取，不硬编码英文 `Layout1`；使用 Autodesk 字典的强类型 foreach，不通过 LINQ Cast 假设其非泛型接口返回值类型。

本次证据及遗留撤销失败见 `artifacts/SECOND_BATCH_THREE_GROUP_ACCEPTANCE_2026-09-03.md`。artifacts 是本地忽略目录，报告不会随普通 git add 自动上传。

bugfix3 重启后的跟进实测见 `artifacts/SECOND_BATCH_BUGFIX3_LIVE_2026-09-03.md`：undo_policy 87 项、undo_conditions 51 项通过。早期失败报告保留，包括测试样例写法错误和缺少显式绑定器引用的编译失败；不得将测试夹具失败或较早版本失败改写为已通过。

bugfix3 起，Write 辅助断言对动态 auto 检查 committed=true/undoGuaranteed=false，而固定写入检查两者为true。这是用户确认的能力优先契约；真实几何和原生 U 的断言仍保留，不能用降低保证字段掩盖普通动态绘图的空撤销步。旧 JSON 报告不改写。新增用例编译通过不等于已经在新包宿主执行。
