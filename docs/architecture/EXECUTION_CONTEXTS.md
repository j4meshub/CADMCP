# ADR-001：分离固定只读、选择与写入执行路径

状态：已采纳并实现；真实宿主完整回归须按各构建的测试报告确认。
日期：2026-09-03。适用：AutoCAD 2022、当前协议2；这是内部框架设计，不是新增MCP输入参数。

## 为什么这样设计

旧调度器把所有需要CAD的请求都放进 `ExecuteInCommandContextAsync`。为处理命令进入导致的预选清空，又在命令前后调用 SetImpliedSelection 恢复选择。2026-09-02 实测出现：变换后立即U能撤销，但插入文档信息或实体详情读取后，一次U不能撤回变换。事务回滚仍正常。这说明“实体以ForRead打开”不足以证明整个调用不会干扰撤销历史。

2026-09-03 隔离探针在应用上下文直接查询、加只读锁查询、单独恢复选择后，均能一次U恢复测试移动。这支持让固定只读调用避开普通命令生命周期；不能把它误读为每一种锁/每个API在所有环境都无副作用。旧实测记录见本地 artifacts/SECOND_BATCH_LIVE_2026-09-02.md（产物目录不提交；关键结论已记录于本ADR）。

同日进一步对照：用相同的旧 CadDispatcher 执行测试移动，随后插入旧调度器的文档信息调用时一次U未恢复；替换为应用上下文+Read锁直接读取同一信息时一次U恢复。由此确认旧通用读取调用链是复现条件，应用上下文读路径是已验证的隔离方案；新包的所有查询/选择/预览组合仍须独立回归。

**不变量：一次成功固定写入是一个撤销单元；之后仅有CADMCP固定读取、预览或选择操作时，不应多消耗撤销步骤。** 用户自己的绘图、视图命令或其他插件动作不在此保证范围内。

## 两条执行路径与五种声明

`ICadCommand.ExecutionKind` 是必填的内部枚举，不是模型可操纵的参数。实现接口而漏写类别时，插件构建应失败。

| 声明 | 工具 | 调度/锁/选择行为 |
| --- | --- | --- |
| ReadOnly | say_hello、get_current_document_info、get_selected_entities、query_entities、get_entity_details | 主线程应用上下文；读锁；不设选择、不进命令上下文、不创建撤销边界 |
| Selection | set_selection | 应用上下文读锁校验；由带 Redraw/NoUndoMarker 的内部文档命令应用并核对选择；失败恢复原选择 |
| PreviewableWrite | clone_entities、transform_entities | 仅真正布尔dryRun=true走只读；缺省/false走写入，字符串等非法值先拒绝 |
| FixedWrite | 四个创建工具 | 始终命令上下文；严格原生撤销、写锁、单事务；保留初始选择；不提供 dryRun |
| CommandContext | send_code_to_cad | 命令上下文；auto 框架事务、none 自管；能力优先，撤销尽力而为；保留代码的选择效果 |

`get_execution_status` 不访问CAD，在原有状态存储快速路径返回。未知枚举值保守归入CommandContext，不得自动获得只读待遇。新执行类别必须补策略测试并修改本表。

所有需要CAD的类别共用同一个执行槽、忙碌检查、文档/空间校验、错误及状态记录。应用上下文仍是AutoCAD主线程，**不是Task.Run或TCP线程**。

## 固定读取与预览的约束

- 主线程一次同步回调中获取活动文档、空间和完整选择快照，校验并物化返回值；不跨await持有DBObject/Transaction/DocumentLock。
- 外层使用显式 `DocumentLockMode.Read`，不得无意换成默认 `LockDocument()` 或Write。内部查询不再重复获取默认写锁；直接调用这些内部命令的测试/代码要自行满足线程和锁前提。
- 只允许ForRead访问和无修改的计算；不调用Editor.Command、SetImpliedSelection、Regen、保存、修改系统变量、StartUndoMark或写事务。
- 预览不能“先真实修改再回滚”。AtomicModifyCommand的dryRun分支只读锁、读事务、估算范围并返回，不能掉入深克隆/变换的写入分支。
- 读取正常结束不得调用选择恢复。正常查询从未清空选择，不需要“恢复”；旧命令路径的补偿逻辑不能复制过来。
- 只读声明是开发契约，不是任意C#的安全沙箱；不能把它当作隔离恶意代码的机制。

## 选择与写入约束

- `set_selection` 先验证全部目标，再返回 `context.RequestSelection` 意图。框架不得在应用上下文直接调用 `Editor.SetImpliedSelection`：该托管方法进入原生 `acedSSSetFirst`，2.1.0 已出现可重复的宿主访问冲突。选择意图必须由统一的内部文档命令桥应用并核对。
- 选择桥使用 `UsePickSet | Redraw | NoUndoMarker | NoHistory | NoActionRecording | NoMultiple`，不使用 `Session`。排队前和命令执行时均核对活动 Document、空间、PICKFIRST、当前选择及目标；用户在间隙改变选择时拒绝覆盖。背景纸空间视口不得进入目标。命令回调内部捕获普通异常；不能尝试捕获并继续运行原生内存损坏。
- 正式固定修改保留初始选择快照。`selectCreated=true` 在提交后也通过同一选择桥应用；失败不能覆盖数据库成功事实，必须保留committed和副本Handle，单独warning。创建/修改后的框架选择恢复和最终兜底恢复同样不得绕过选择桥。
- 数据库修改必须仍有文档锁、单事务与撤销边界，提交前物化JSON并检查容量。不要为了消除空撤销记录而关闭全局UNDO、清空历史或自动多次U。
- `send_code_to_cad` 无论auto/none都可能写库、调用命令、操作选择；none只表示不提供框架事务，绝不表示只读。不扫描C#文本猜测副作用，不新增可绕过写入路径的客户端readOnly开关。

## ADR-002：固定修改使用调度器拥有的原生命令撤销单元

2026-09-03 的后续隔离实验发现另一独立问题：在 `EXECUTEFUNCTION` 内调用 ActiveX `StartUndoMark/EndUndoMark`，连续两次修改需要三次 U 才回到基线；仅移除内层手工组而保留命令上下文、Write 锁、事务、选择恢复与读取，两次 U 可以逐次撤回两次修改。把手工组放到锁内/锁外/锁前均未解决；绕过选择恢复的原实现仍失败。此结论针对当前 AutoCAD 2022 实测，不声称解释全部宿主内部实现。

历史记录：artifacts/UNDO_ROOT_CAUSE_AND_CANDIDATE_2026-09-03.md 及 UNDO_PROBE_*.json。记录中的旧包失败与候选通过保留，不改写为新包通过；源码落地后的整套 Bundle 仍须独立验收。这不是撤回 ADR-001，读/预览路径分离继续保留。

正式 `clone_entities` / `transform_entities` 的执行顺序：

1. 调度器捕获文档/空间/选择，实际进入原生 `EXECUTEFUNCTION` 回调后创建 `NativeCommandUndoScope`。
2. 该内部对象绑定本次 Document、空间、线程和回调生命期；ADR-002 初始只交给正式 PreviewableWrite，ADR-003 扩展到 FixedWrite。公开构造 CadCommandContext 不授予该能力，读取/预览/动态代码也不授予。
3. `AtomicModifyCommand` 在访问 CAD 前及完整预检后的写入前，通过 `StrictUndoBoundary.Require(context)` 校验所有权、活动身份、命令名以及 UNDOCTL（完整开启、非 One、无其他组）。此检查不改变数据库或系统变量。
4. 保留 Write 锁、单事务、深克隆映射/支持范围检查、提交前 JSON 物化与 7 MiB 容量上限。不再开启或关闭额外的 ActiveX 撤销组。外层命令本身就是撤销边界，不是“取消撤销保护”。
5. 提交时仅记录 committed，内部暂不确认 undoGuaranteed。回调结束即撤销能力，待调度器 await 原生命令正常结束后才确认 undoGuaranteed=true；状态查询在此之前仍是 running。
6. 命令收尾异常时保留 success/committed/Handle/映射，但 undoGuaranteed=false 并附 `undo_completion_failed`。随后独立应用选择，选择失败不降低已经确认的撤销保证，也不覆盖数据库成功结果。

新增固定编辑应复用该框架，不得直接 new CadCommandContext 后调用写入 Execute，不得绕过所有权检查，也不得在外层命令里再套 StartUndoMark。内部能力是防止误用的开发契约，不是对有完整权限动态 C# 的安全隔离（反射也不受此沙箱限制）。固定编辑不能把 context 保留到线程池或另一个命令继续使用。

ADR-002 实施时，四个旧创建工具和 `send_code_to_cad` 暂保留 `UndoBoundary` 原策略，当时未用候选数据证明其全部连续撤销场景。后续实测结果和当前修订见 ADR-003；不将先前未覆盖项改写为已通过。

测试器也属于正确性边界：不能把异常抛出 AutoCAD 拥有的原生回调；必须在回调内部捕获，退出后再向托管 Task 报错。新图必须明确激活、等待空闲并核对身份；原生 U 只发送一次，等待完成事件，不在 U 外再包装 EXECUTEFUNCTION。超时停止，不自动补发 U，不用多次撤销掩盖失败。

## ADR-003：固定创建严格撤销，动态代码能力优先

用户明确：不做旧包对照；按旧问题处理，不能为了任意代码严格撤销而削弱 `send_code_to_cad`。三组宿主验收已复现创建直线→创建圆、动态 auto→动态 auto 的第二次 U 空步，原始失败保留在 artifacts/SECOND_BATCH_THREE_GROUP_ACCEPTANCE_2026-09-03.md。

- 四个创建工具声明新增的 FixedWrite。Resolve 始终走命令上下文，不能通过 dryRun/transactionMode 参数获得只读或绕过严格撤销；MCP 输入契约不增加这些参数。
- 调度器为 FixedWrite 和正式 PreviewableWrite 创建原生撤销能力；创建基类在访问 CAD 前以及写入前 Require，缺少能力或 UNDO 关闭/One/已有组时写入前拒绝。保留单事务、资源校验、毫米/UCS、批量语义，提交前物化及检查 7 MiB 响应。
- 固定创建的选择恢复与修改工具一致；提交和原生命令结束分开确认。提交后收尾/选择失败保留 committed、Handle 和成功结果，并附 warning。正常命令结束后才设置 undoGuaranteed=true。
- send_code_to_cad 继续声明 CommandContext。auto 仍创建框架事务；none 的 transaction 仍为 null。两者不领取严格撤销能力，不检查 UNDOCTL 来拒绝执行，不扫描或限制 C#、DLL、API、实体类型或用户自行组织的撤销组。
- 框架不再对动态 auto 添加额外的 ActiveX 手工组；依靠正常原生命令行为尽力支持普通绘图的撤销。代码自己使用 StartUndoMark、嵌套非交互命令或自管事务仍被允许。这不是沙箱，也不能承诺外部文件/网络副作用由 DWG 回滚或撤销。
- 动态响应的 undoGuaranteed 始终为 false，成功执行附 undo_not_guaranteed 说明。false 不代表不能撤销、没有修改或执行失败；auto 成功时 committed=true；none 的 committed=false 只说明没有框架事务提交，不证明代码没写入。
- auto 已提交之后的序列化/锁释放异常保留 committed=true，返回 post_commit_warning，不能误报 rolledBack 或诱导自动重试。auto 异常回滚仅涵盖框架事务内尚未提交的修改。
- 本 ADR 采纳时输入及版本不变：产品2.0.0、协议2、Schema1；后续统一产品版本以根目录 version.json 为准，历史验收包版本不追改。移除无调用者的旧框架 UndoBoundary，不能通过把动态代码改成 FixedWrite/PreviewableWrite 来统一实现。

验证要求：四个创建工具分别连续两次和混合连续写入，插入读取/预览/选择后逐次 U；资源失败与中途异常整批回滚；提交后选择失败保留结果；固定工具在 UNDO 关闭/One/已有组时拒绝，动态 auto/none 仍能运行且不自行改变该状态。动态普通两次写入的真实 U 作为尽力撤销回归，同时验证3D、原生命令、自管事务和选择能力。异常状态测试只在独立未保存测试图进行，不能清空原图历史。构建/纯测试通过不代表这些宿主用例已通过。

## 文字镜像（修订原第二批契约）

用户确认MIRRTEXT=0保持文字正向符合设计习惯。本批保留AutoCAD原生TransformBy及当前设置的文字处理，不额外强制反字、不修改MIRRTEXT、不编辑共享块定义。镜像仍使用包含输入轴线且平行输入Z的平面，避免翻转高度。说明/提示使用native_text_mirror，而非旧严格几何反字保证。

不同类型（DBText、MText、AttributeReference、块内常量文字、尺寸）遵循宿主各自规则，不把独立TEXT测试结果扩大为所有嵌套对象的保证。回归需分别覆盖MIRRTEXT=0/1，确认调用不改设置。

## 为什么没有统一使用NoUndoMarker

NoUndoMarker是AutoCAD注册命令的标志，不是给普通C# Execute方法加属性就能生效。只有确需命令上下文的固定只读API才考虑内部命令桥接，并验证外层不会另加标记。当前固定读取不需要该层；动态代码和真实写入绝不能使用它掩盖撤销。

官方背景：[命令标志](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-DevGuide-Managed/files/GUID-F77E8FE0-8034-4704-93BD-F717608F8223.htm)、[文档锁类别](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_ApplicationServices_DocumentLockMode.html)。这些API说明不能替代本项目的真实AutoCAD回归。

## ADR-004：框架选择写入使用带命令标志的文档命令桥

2026-09-07，2.1.0 在两次 `set_selection` 调用中以同一调用栈终止 AutoCAD 2022：`Editor.SetImpliedSelection` → `acedSSSetFirst` → `AccessViolationException`。当时固定选择路径虽在主线程应用上下文完成只读校验，却在释放读锁后直接写入原生 PickFirst/Grip 状态。普通托管 catch 无法把已经损坏的宿主恢复为安全状态。

2.1.1 将框架拥有的选择写入集中到 `SelectionCommandBridge`。应用上下文只准备和排队意图；实际 `SetImpliedSelection` 在注册的非 Session 文档命令中执行。`Redraw` 使 PickFirst/Grip 集在命令完成后保留并刷新，`NoUndoMarker` 避免选择操作新增撤销单元。桥接命令排队前与执行时比较选择快照，防止异步间隙覆盖用户选择；所有目标在执行时重新打开验证。

这项隔离只约束框架内部的 set_selection、selectCreated 与选择恢复。`send_code_to_cad` 仍是完整权限的 CommandContext，用户代码可以自行调用选择 API；不将动态代码改为固定选择桥，不扫描或禁止相关源码。真实宿主验收必须覆盖非空旧选择→四个刚提交的新实体，以及写入→选择→一次U。

## 必须防止的回归

1. 写入→单个及重复组合的各只读工具→一次U和人工Ctrl+Z，核对精确几何/原Handle，不只看count。
2. 写入→dryRun（两个工具、合法/非法请求）→一次U；预览不消耗步骤、不产生新Handle、选择不变。
3. 写入→set_selection replace/add/remove/clear→一次U；选择错误不丢失原选择。
4. 连续两次写入，中间插入读取；两次U分别撤回第二批和第一批，不能越过用户更早操作。
5. 文档/空间切换、cad_busy、选择完整快照与超时状态保持；查询不保存，DBMOD/文件时间/几何分开核对。
6. 动态代码auto/none及其选择效果、编译错误、运行回滚；事务/预览类别不能依据模型声明变更。

纯测试入口：tests/CADMCP.Core.Tests/Program.cs 的 ExecutionRoutes、NativeUndoLifecycle。覆盖所有权生命期、线程/身份/UNDOCTL、提交与收尾、选择失败保留结果。宿主入口：tests/CADMCP.AutoCAD.Tests，安装匹配Bundle后运行CADMCP_TEST_SECOND_BATCH。任何未执行项在报告中注明，不将“新增测试代码”写作“测试通过”。
