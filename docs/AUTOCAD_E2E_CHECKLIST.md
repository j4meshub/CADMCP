# AutoCAD 2022 真实端到端验收清单

> 本清单必须在安装 AutoCAD 2022 的 Windows 开发机执行。CI 不模拟 AutoCAD 宿主。

- 将 `artifacts/CADMCP.bundle` 复制到 `%AppData%\Autodesk\ApplicationPlugins\CADMCP.bundle`，确认插件随 AutoCAD 加载，但服务保持关闭。
- 点击“开启服务”，确认 `%LocalAppData%\CADMCP\runtime\session.json` 生成；关闭服务后文件删除。
- 使用 `cadmcp-server` 依次验证当前文档、预选实体、实体查询和 4 个批量绘图工具。
- 在默认 UCS、旋转 UCS、显式 WCS 下校验实体位置及响应的 WCS 包围盒。
- 验证任一非法图层/线型使整批绘图回滚；成功批次一次 `UNDO` 撤销。
- 用 `send_code_to_cad` 查询、创建、修改、删除实体并返回匿名对象；确认编译诊断映射到 `AI_CODE` 行号。
- 验证 `auto` 运行异常回滚，`none` 的 `transaction` 为 `null` 且不声称自动撤销。
- CAD 正在执行其他命令时确认立即返回 `cad_busy`。
- 临时缩短 Node 超时，确认得到 `timeout_unknown/callId`，随后用 `get_execution_status` 得到最终结果。
- 验证框架不自动保存 DWG，动态代码显式保存仍能执行。
- 验证错误令牌、陈旧 session 文件、第二 AutoCAD 实例均不能执行工具。
- 验证设置跨 AutoCAD 重启保留，服务每次仍默认关闭。

## 第一批：实体详情与精确选择（须人工记录实际结果）

- 安装后刷新 MCP，确认 12 个工具，设置页出现两个新工具；原工具禁用偏好保留，新工具默认开启。禁用新工具后仍能在 MCP 列表看到，但调用返回 `tool_disabled`。
- 在一份测试 DWG 选择圆弧、文字、引线和带属性的图框块。读取选择及文档标识，再调用 `get_entity_details`；核对 ARC 圆心/半径/端点、TEXT/MTEXT 原文/纯文本、有效块名、可见和不可见属性。比较连续读取结果。
- 用 R4 实际样图人工标出正确圆弧，验证实体事实能支持 AI 判断，然后用 `set_selection` 选中对应圆弧。首批不自动推断 R4 目标。
- 分别检查 Leader、含多条分支的 MLeader、径向/直径/对齐/转角线性尺寸；不支持的类型必须有 warning。
- 在毫米、米、英寸、Unitless DWG 及旋转/倾斜 UCS 中核对相同实体的 WCS 毫米输出；角度 OCS 标记、方向/法向量不得错误乘长度系数。
- 对有平移/旋转/缩放的块，核对 blockTransform 与插入点；属性位置不重复套用块矩阵。
- 依次 replace/add/remove/clear，核对 CAD 高亮与 selectedHandles 一致；测试大小写/前导零重复句柄、expectedCount 的最终数量语义。
- 混入一个无效或已删除句柄、非实体句柄、其他布局实体、属性引用句柄：整个调用失败，无部分结果或选择变更。
- 对最终要选入的不可见实体、关闭/冻结图层实体，返回 `entity_not_selectable`，不改图层。
- 在 A 图读取身份和句柄后切换 B 图（即使存在相同句柄），以及在同一 DWG 切换布局：旧身份拒绝执行；关闭重新打开 A 图后旧 documentToken 也失效。
- 记录调用前后的 DBMOD、实体数及文件时间；读取和选中不得修改/保存图纸。不要把“新建文档本来已修改”的状态当作本工具修改。
- 用 send_code_to_cad 的 initialSelection/selectedHandles 读取，再动态调用 editor.SetImpliedSelection 选择目标：最终选择应保留；随后固定读取仍返回该选择。
- 详情开关关闭时不读取对应详情；200/1000/2000 个实体分批测试，超限明确报错。查询/选择已有 truncated 行为保留，不把截断结果当全图。
- 测试验证失败保留原选择；若 AutoCAD 自身拒绝恢复，响应必须提示 selection_restore_failed，不能声称成功恢复。
- 2.1.1 选择桥专项：在独立测试进程依次验证空→已有、非空A→相同A、非空A→不同B，以及非空旧选择→四个刚提交的新实体；每种都核对实际高亮和 Handle，反复执行时 Windows 应用日志不得再出现 acedSSSetFirst/AccessViolationException。
- 验证内部选择命令带 Redraw 且不带 Session；选择后应保持夹点/高亮。写入→set_selection→一次U只撤销写入，选择桥不得增加空撤销步或进入重复命令历史。
- 桥排队期间切图、切空间或改变当前选择应安全返回 mismatch/selection_state_changed，不覆盖新选择。PICKFIRST=0 返回 selection_unavailable 且不擅自改系统变量；纸空间背景视口、失效对象整批拒绝。
- `clone_entities(selectCreated=true)` 也必须走选择桥；选择失败仍保留 success/committed/createdHandles 和 post_commit_selection_failed。框架所有恢复路径不得在应用上下文直接调用 SetImpliedSelection。

## 第二批：复制、变换与路径（未执行的项目不得勾选通过）

使用独立验收 DWG。可先运行 [第二批工具说明](SECOND_BATCH_TOOLS.md) 中的 CADMCP_TEST_SECOND_BATCH；不要在用户业务图中注入测试故障或进行测试变换。

- 对比已命名 DWG、全新未保存图、首次保存、另存为、真实自动保存前后的 name/fileName/isNamedDrawing/databaseFileName/dbmod；原图路径不变为 .sv$。自动保存/另存为同一 Document token 不变，关闭重开 token 失效。
- 预览前后对比 DBMOD、实体数、Handle 集合、原图文件时间与内容快照，选择不变；正式调用重新校验，不把预览估计范围当成精确结果。
- 克隆 LINE/ARC/CIRCLE/Polyline/TEXT/MTEXT/SOLID/POINT/非关联 HATCH；复制带可见/不可见/多行属性的图框和动态块，核对样式、属性、参数、可见性、间距及映射。属性位置只变换一次。
- 检查四种非关联尺寸、独立 Leader 和 MLeader；关联尺寸/引线、注释性对象、组/持久反应器、代理对象、外部参照必须明确拒绝，不静默跳过。
- 两种 source、空选择、超 2000 选择、重复句柄、wrong document/space、缺失/错误 expectedCount 均按契约处理；不支持 query 来源。
- 关闭/冻结/锁定图层、不可见或已删除目标导致整批失败，图层状态不被改变。
- 一次 UNDO 撤销完整克隆/变换；注入第二个实体处理后异常，确认没有副本残留或部分变换，rolledBack=true。关闭 UNDO/已有撤销组时不开始修改。
- selectCreated=false 保留原选择，true 选中新副本；模拟提交后选择失败，success/committed 和副本映射必须保留，仅选择状态及 warning 表示失败。
- 移动/旋转/缩放/镜像核对精确坐标，原 Handle 不变；毫米、米、英寸、Unitless 及带原点平移的旋转/倾斜 UCS 均覆盖。
- 镜像测试非零高度、斜轴、文字和块属性；保留输入坐标系高度，MIRRTEXT=0 时常用文字保持正向，=1 时使用宿主反字行为。调用前后 MIRRTEXT 不改变，不额外反转文字或修改共享块定义。退化轴、不同 Z、非有限值、非法比例全部拒绝。

## 读写调度回归（2026-09-03）

- 安装匹配的 Plugin/CommandSet，运行 CADMCP_TEST_SECOND_BATCH；新增 ExecutionRegressionTests 覆盖分类、各查询/预览/选择插入写入后的单次U、连续两次编辑逐次U及文字镜像设置0/1。
- 必须补做人工 Ctrl+Z：移动或复制→多次 get_current_document_info/get_entity_details/query_entities/get_selected_entities→一次 Ctrl+Z，整批恢复。
- 同样覆盖两个 dryRun、无效预览、set_selection 四个模式，确认只读不改变选择、不制造撤销步骤。测试器自身的快照读取也不得进入命令上下文。
- 写入→读取→写入→读取，第一次 Ctrl+Z 只撤第二次写入，第二次撤第一次，不跨越用户更早历史。
- 查询/预览仍返回 cad_busy、不跨文档/空间执行；动态代码 none 仍可写入和操作选择，不被归入只读。
- 只读锁下的嵌套预览、返回容量检查和失败路径不能升级默认写锁；实际宿主回归未执行的项目必须在报告中标明。
- 200/1000/2000 实体批量检查，记录宿主时间；结果超限必须提交前失败，超时通过状态查询确认结果，不能直接重试复制。
- 回归第一批选择读写、动态代码改选及原四个创建工具；所有测试记录区分源码测试、自动宿主测试与人工观察。

## 原生命令撤销边界修复补验（second-batch-bugfix2）

- 整套更新 Plugin/CommandSet，重启 CAD。确认同源候选的历史通过不被当作本次正式二进制通过。
- 连续两次移动（有/无读取），两次 U 分别恢复第二次、第一次精确坐标；连续两批复制，两次 U 分别移除相应副本 Handle，源对象保持。
- 在写入之间插入重复查询、两个预览、set_selection 四种模式；检查选择及几何，不只看 DBMOD/count。再补做三次连续写入逐次 U。
- 第二实体注入异常→全部回滚→再次正常写入→一次 U 撤回；selectCreated 失败仍保留 committed/映射和已经确认的 undoGuaranteed。
- UNDO 关闭/One/已有组与未获得调度器所有权的直接调用，写入前拒绝；测试后恢复原设置。不用 NoUndoMarker、禁用撤销或多次 U 兜底。
- 验证 get_execution_status 在外层命令完成前仍 running；命令收尾异常但已提交时只降级撤销保证、附 undo_completion_failed，不改为回滚结果。收尾故障的纯逻辑测试不能冒充真实宿主故障注入通过。
- 自动验收程序先明确激活新图，遇到文档改变立即停止；内部异常安全返回。原生 U 要有对应完成事件；无事件则停止，不补发。
- 不改 MIRRTEXT 策略；四个旧创建/动态 auto/none 的撤销策略本次未替换，仍需独立回归。

## 创建/动态撤销分流补验（second-batch-bugfix3，待真实宿主执行）

- 四个 create_* 分别连续两次，以及 line→circle→polyline→text 混合执行。间隔插入读取、两个 dryRun、set_selection；每次原生 U 或人工 Ctrl+Z 只撤回最后一批，核对 Handle、精确几何、选择与 DBMOD。
- 缺失图层/线型和第二项创建异常：没有部分实体残留；失败后正常创建仍可一次 U。响应超过7 MiB时提交前失败。
- 提交后的选择失败：保留 success/committed/handles；不能因 warning 重复创建。原生命令收尾失败不得虚报严格保证。
- 只在独立未保存图中设置 UNDO None/One/Begin；四个创建工具应返回 undo_unavailable 且不写入；动态 auto/none 均应能执行且 undoGuaranteed=false，不擅自改变 UNDO 状态。结束恢复该测试图设置，不改原图。注意 None 会丢弃当前测试图历史，见 [Autodesk UNDO 说明](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-Core/files/GUID-2729A466-B199-4840-B92B-4D8A38A8ADB8.htm)。
- 动态普通 auto 两次创建线，观察两次 U 是否逐批恢复；这是典型代码的尽力撤销回归，不提升为任意 C# 严格撤销承诺。
- 动态能力回归：嵌套参数/using/DLL、Solid3d、非交互 Editor.Command、用户自管事务/撤销、选择效果、编译诊断、auto 运行异常回滚、none transaction=null。不得以禁止这些 API 的方式让测试通过。
- 确认 MCP 描述和调用响应解释动态 undoGuaranteed=false；get_execution_status 保留提交结果。本批不做旧包对照、不改版本、不自动保存图纸。
