# 第二批固定工具：复制、变换与原图路径

本批新增 `clone_entities`、`transform_entities`，共 14 个 MCP 工具。开发和 bugfix 验收最初使用产品 2.0.0；现将本批及后续修复统一归入产品 2.1.0，协议 2、设置 Schema 1 不变。当前版本以根目录 `version.json` 为准，更新汇总见 [更新记录](../CHANGELOG.md)。不会自动保存、执行改号、删除或跨 DWG 复制。

## 文档路径修复

`get_current_document_info` 返回以下路径/状态字段：

| 字段 | 语义 |
| --- | --- |
| name | 当前 Document.Name，兼容保留 |
| fileName | 已命名图纸的 Document.Name；DWGTITLED=0 时为空字符串 |
| isNamedDrawing | DWGTITLED 是否非零，不等同于“没有未保存修改” |
| databaseFileName | 数据库当前文件路径，仅供诊断，可能指向临时 .sv$ |
| dbmod | AutoCAD 原始 DBMOD 整数 |
| isModified | dbmod != 0，兼容保留 |

自动保存不会使原图路径字段改为临时文件。另存为后不缓存旧路径；同一 Document 的 documentToken 保持，关闭重开后失效。不要用 databaseFileName 做“原图是否被保存”的时间检查，也不要仅凭 DBMOD 或实体数量推断所有实体是否变化。

## 公共参数

先通过文档信息、选择或查询取得 `documentToken` 与 `activeSpaceHandle`，传给两个新工具。操作当前活动空间内 1–2000 个顶层实体；不展开块内实体或属性引用作为独立目标。

- `source: {kind: "handles", handles: ["A", "B"]}`：显式句柄，规范化大小写和前导零，按首次出现顺序去重。
- `source: {kind: "selection"}`：使用调用开始时的完整选择快照；不接受同时传 handles，不接受 query，超限不截断。
- `expectedCount`：必填 1–2000，校验去重后的顶层目标数，块属性不单独计数。与 set_selection 的“最终选择数”语义不同。
- `coordinateSystem`：`ucs`（默认）或 `wcs`。位置、距离以毫米输入，角度为弧度。
- `dryRun`：默认 false，直接执行；true 只校验并估算范围，不写入数据库、不创建新 Handle、不改变选择。

正式执行和预览都完整验证目标。任一目标失效、类型不支持、归属不符或资源不可用均整批拒绝，不静默跳过。预览不锁定之后的目标，不保证实际克隆或变换必然成功；需要同一集合时，用返回的 sourceHandles 再正式调用。

## clone_entities

```json
{
  "documentToken": "从最近读取结果取得的 UUID",
  "activeSpaceHandle": "2",
  "source": {"kind": "selection"},
  "expectedCount": 186,
  "displacement": {"x": 420, "y": 0, "z": 0},
  "coordinateSystem": "wcs",
  "dryRun": false,
  "selectCreated": false
}
```

每次将整批集合复制一份并位移，使用数据库 DeepCloneObjects / IdMapping，不根据几何重绘。复用现有图层、线型、文字样式和块定义；保留块属性、动态块状态，不修改共享块定义。只对副本顶层对象施加一次变换，块属性不能再次重复位移。

正式结果包含 sourceHandles、createdHandles、handleMapping（sourceHandle/createdHandle）、count、items（副本摘要及前后包围盒）、selectionRequested、selectionApplied，以及文档/空间标识。

`selectCreated` 默认 false，保留原选择。为 true 时，在提交之后选中新副本并核对实际选择。如果这一步失败，复制仍然成功：success/committed 保持 true，createdHandles/handleMapping 保留，selectionApplied=false，并返回 post_commit_selection_failed 警告。**不得因这个警告重试复制**；可单独调用 set_selection。

## transform_entities

```json
{
  "documentToken": "从最近读取结果取得的 UUID",
  "activeSpaceHandle": "2",
  "source": {"kind": "handles", "handles": ["A", "B"]},
  "expectedCount": 2,
  "operation": {"type": "move", "displacement": {"x": 0, "y": -300}},
  "coordinateSystem": "wcs",
  "dryRun": false
}
```

| operation.type | 参数 | 行为 |
| --- | --- | --- |
| move | displacement | 原地平移 |
| rotate | basePoint、angle | 绕输入坐标系 Z 轴旋转，弧度 |
| scale | basePoint、factor | 正数等比缩放，无非均匀缩放 |
| mirror | axisStart、axisEnd | 镜像原实体，不创建副本；文字沿用 CAD 当前设置 |

UCS 位移按向量转换，不叠加 UCS 原点。镜像轴要求输入 Z 相同、XY 不重合；镜像平面包含轴线并平行输入坐标系 Z 轴，保留该坐标系的高度。只接受有限数值，矩阵算术溢出也拒绝。

文字镜像沿用 AutoCAD 原生行为及当前 `MIRRTEXT`。`MIRRTEXT=0` 时常用文字保持正向是预期行为，不再要求强制几何反字；`MIRRTEXT=1` 使用宿主的反字行为。块内文字、属性和尺寸依实体原生规则处理，不保证所有内部对象表现完全相同。插件不临时切换该设置，不额外反转文字，也不修改共享块定义。镜像响应返回 `native_text_mirror` 提示（替代旧 `geometric_mirror`）。镜像不等于“生成镜像副本”，需要副本时先 clone_entities。

这是 2026-09-03 按用户设计习惯修订的产品契约；旧实测报告中的“文字未严格反字”按旧契约记录，不应作为新契约的失败判据。几何镜像平面、坐标、单位及非零高度要求不变。

正式结果包含 handles、sourceHandles、count、items 的 beforeBoundingBoxWcs/afterBoundingBoxWcs、operation、inputCoordinateSystem、文档/空间标识。Handle 保持不变，默认保持原选择。

## 预览和范围

预览返回 dryRun=true、sourceHandles、count、operation、items 的 beforeBoundingBoxWcs/predictedBoundingBoxWcs 与 predictionKind=conservative，不返回新实体 Handle。预计包围盒是原 WCS 包围盒八角点变换后的外包范围，旋转圆弧等情况下可能大于真实实体范围。

预览 committed/rolledBack/undoGuaranteed 均为 false，transactionMode=null。不采用“先克隆到活动数据库再回滚”的预览方式。包围盒不可用则明确拒绝，不能提供假精确范围。所有结果长度、坐标以 WCS/毫米输出。

固定读取与预览在 AutoCAD 主线程的应用上下文中执行，不进入普通命令上下文，不使用默认写锁、不重设选择集。它们仍受单执行槽、忙碌检查、文档/空间校验和只读锁保护。正式修改继续使用命令上下文、写锁、事务和撤销边界。`send_code_to_cad` 的 `none` 模式不等于只读；不改变动态代码的完整能力。维护约束见 [执行上下文设计](architecture/EXECUTION_CONTEXTS.md)。

## 支持类型与保守限制

支持原生 LINE、ARC、CIRCLE、LWPOLYLINE、二维 SOLID、TEXT、MTEXT、普通/动态 INSERT、LEADER、MLEADER、POINT、非关联 HATCH，以及 RadialDimension、DiametricDimension、AlignedDimension、RotatedDimension。

- 仅接受列出的实际原生类型；代理对象、自定义派生类型、三维实体、老式 POLYLINE、独立 ATTDEF/ATTRIB 和其他未列类型拒绝。
- 不支持外部参照、注释性对象、关联填充、具有外部注释对象的 Leader、关联尺寸，以及具有关联依赖或持久反应器的对象。
- 组和第三方关联可能通过持久反应器出现，会保守返回 dependent_entity；不自动解除组/约束/关联。
- 关闭/冻结/锁定图层、不可见实体拒绝；不自动开图层、解锁或改变用户设置。
- 块内部保持封装，只操作顶层引用和随父块拥有的属性，不递归编辑内部实体；动态块的定义/参数状态由原生克隆机制保留。

常见错误：invalid_parameters、count_mismatch、document_mismatch、active_space_mismatch、entity_not_found、entity_outside_active_space、unsupported_entity_type、dependent_entity、entity_not_selectable、layer_locked、missing_resource、geometry_unavailable、undo_unavailable、clone_incomplete、unexpected_created_entities、result_too_large、modify_failed。

## 事务、选择与超时

正式执行使用调度器实际进入的原生命令作为撤销边界，不在命令内另开 ActiveX 撤销组。校验框架所有权与文档/空间，完整预检后再次检查边界，再进行单一数据库事务。UNDO 关闭、仅一次撤销、已有撤销组或缺少本次调度器边界时返回 undo_unavailable，不开始写入。任何处理中异常均整批回滚；映射不完整、意外新增顶层实体、最终结果超出 7 MiB 时也在提交前失败。

数据库提交且外层命令正常结束后，成功返回 committed=true、undoGuaranteed=true。若提交后命令收尾异常，保留成功结果和 Handle，但标记 undoGuaranteed=false 并附 undo_completion_failed；不要重试写入。提交后仅选择失败不降低已经确认的撤销保证。预检失败不声称已回滚，实际写入开始后失败才返回 rolledBack=true。

超时仍返回 timeout_unknown 和 callId，先 get_execution_status 查询，不自动重试写调用。无需逐实体刷新视图；四个旧创建工具及动态代码的事务契约不随本批重写。

## 自动测试与独立宿主验收

```powershell
cd server
npm test
cd ..
dotnet run --project tests/CADMCP.Core.Tests -c Release
dotnet build tests/CADMCP.AutoCAD.Tests -c Release
```

宿主验收 DLL 不随 Bundle 发布。安装同一构建的 Bundle 后，在 AutoCAD 2022 中 NETLOAD `tests/CADMCP.AutoCAD.Tests/bin/Release/net48/CADMCP.AutoCAD.Tests.dll`，运行 `CADMCP_TEST_SECOND_BATCH`。执行时不要同时操作 CAD 或发起 MCP 调用。保持新工具开启，CAD 安装需能找到 acadiso.dwt。

入口新建独立未保存图纸，明确激活并等待空闲/核对文档身份后创建 2000 条测试线；不在原业务图中绘图，不保存或关闭业务图。通过真实 CadDispatcher 运行预览、数量保护、中途故障整批回滚、复制映射、四种变换、一次 UNDO、提交后选择失败和批量检查；结果打印到 CAD 命令行，遇到首个失败停止，保留测试图供检查后手动关闭。撤销测试等待原生 U 完成事件，绝不以额外 EXECUTEFUNCTION 包装 U 或在未知超时后自动重试。测试回调内部捕获异常，另有边界误用及异常安全门检查。

此入口不代表全部人工验收：带属性/动态块、引线/尺寸、多单位/UCS、实际自动保存和另存为等仍须按 [宿主验收清单](AUTOCAD_E2E_CHECKLIST.md) 检查。构建通过也不代表真实 CAD 测试通过。

安装成套更新 Bundle 和本地 Server，重启 AutoCAD/MCP，确认工具列表为 14 项。设置新增两项默认启用，原偏好保留。产品版本必须严格匹配：2.1.0 Server 不能连接仍运行 2.0.0 的插件，不能仅重建 Server 而继续使用旧 Bundle。版本升级与 Git 提交不代表已发布 npm 或 GitHub Release。
