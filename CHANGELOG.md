# 更新记录

本文件记录已交付到仓库的功能与限制，不表示每个版本都已发布 GitHub Release 或 npm。唯一版本配置为根目录 `version.json`。

## 2.1.1 — 2026-09-07

- 修复框架在 AutoCAD 应用上下文直接调用 `Editor.SetImpliedSelection`，最终进入 `acedSSSetFirst` 并可能触发 `AccessViolationException`、导致 AutoCAD 2022 进程终止的问题。两次实际崩溃发生于2.1.0相同调用栈。
- 新增统一 `SelectionCommandBridge`：应用上下文只校验并排队，实际选择写入由带 `UsePickSet | Redraw | NoUndoMarker` 等标志的非 Session 文档命令完成。命令排队前后复核 Document、活动空间、PICKFIRST、原选择和实体有效性，避免覆盖用户在间隙作出的选择。
- set_selection、clone_entities(selectCreated=true)、固定写入后的选择恢复和最终兜底恢复全部改用选择桥；不再从这些应用上下文路径直接进入原生选择 API。选择失败不覆盖已经提交的复制结果。
- 新增背景纸空间视口拒绝、选择状态变化及 PICKFIRST关闭的结构化失败；插件不会擅自改变用户系统变量、图层或图纸。
- 保留 send_code_to_cad 的能力优先策略和完整 AutoCAD API 权限。框架桥接不禁止动态代码自行管理选择，动态撤销和事务语义不变。
- 增加真实宿主回归场景：非空旧选择替换为四个刚提交的新实体，并检查选择命令不增加撤销步。源码编译和自动检查不能替代安装2.1.1后的真实 AutoCAD 2022崩溃回归。
- 2026-09-07 安装包真实 AutoCAD 2022 回归已通过核心场景：空/非空选择替换、非空旧选择替换为四个刚提交实体、clone 自动选中新副本、两次固定写入对应两次 U，以及非法目标失败后保留原选择。范围与未覆盖项见 `docs/test-reports/CADMCP-2.1.1_SELECTION_BRIDGE_LIVE_2026-09-07.md`。
- 产品统一升级到2.1.1；TCP协议仍为2，设置Schema仍为1。

## 2.1.0 — 2026-09-03

范围：从第一批固定工具提交 `4048b97` 之后，汇总第二批开发、后续执行/撤销修复及已知事项。Plugin、CommandSet、Bundle、npm Server 统一升级为 2.1.0；DLL AssemblyVersion/FileVersion 为 2.1.0.0，构建标识继续附 Git SHA。TCP 协议 2、设置 Schema 1 不变。

### 新增复制与几何变换

- 新增 `clone_entities` 和 `transform_entities`，MCP 工具总数由 12 增至 14；设置中文说明、中英工具描述、Zod/真实 MCP Schema、默认启用及旧设置偏好保留同步集成。
- 目标支持显式 Handle 或调用开始时完整选择快照，要求 documentToken、activeSpaceHandle、expectedCount；规范化并去重，限定当前 DWG/活动空间，1–2000 个顶层目标，超限整批拒绝、不截断。
- 复制使用 DeepCloneObjects / IdMapping 保留块属性、动态块状态及数据库关系，复用资源和共享块定义，只位移顶层副本；返回完整源→副本 Handle 映射，支持 selectCreated。
- 变换支持移动、绕输入坐标系 Z 轴旋转、正数等比缩放、关于指定轴线平面的镜像，保留原 Handle；毫米输入、弧度角度，支持 UCS/WCS，向量变换不叠加 UCS 原点，镜像保留输入坐标系高度。
- 两工具均支持 dryRun，只读预检并返回保守预计包围盒；不在活动数据库先写后回滚，不生成新 Handle，也不锁定未来执行的目标集合。
- 批量预检支持范围、图层/资源、关联、文档与空间身份；拒绝不支持对象，不静默跳过。单事务原子提交，校验深克隆映射及意外新增顶层对象，提交前物化 JSON 并限制响应为 7 MiB。

### 修正文档路径和文字镜像

- `get_current_document_info.fileName` 改为已命名活动 Document 的原图路径；未命名时为空字符串。新增 isNamedDrawing、databaseFileName、dbmod，保留 name/isModified 行为。
- 自动保存后 databaseFileName 可以为 `.sv$`，但 fileName 和同一加载文档的 token 不随临时路径变化；另存为不缓存旧路径。文件时间检查应使用原图路径。
- 根据用户设计习惯，取消早期“所有文字严格几何反字”的要求，镜像沿用原生 TransformBy 与当前 MIRRTEXT，不切换系统变量、不修改共享块定义。MIRRTEXT=0 下常用文字保持正向是预期行为；块内文字、属性、尺寸遵循各自原生规则，不能一概而论。

### 修复执行、选择与连续撤销

- 所有命令显式声明 ExecutionKind。固定读工具及已验证的 dryRun 在主线程应用上下文、只读锁下执行，不进入普通命令上下文，不重设选择、不再产生读取引起的多余撤销步骤。
- set_selection 使用独立选择路径，先完整校验、后应用选择意图；校验失败保留原选择。正式写入与任意动态代码继续走命令上下文；所有路径共用执行槽、忙碌和身份检查。
- 固定创建、复制和变换使用调度器拥有的原生命令撤销单元，移除 EXECUTEFUNCTION 内额外的框架 ActiveX 撤销组，修复连续两次写入需要多次 U 的空步问题。
- 新增 FixedWrite 分类用于四个创建工具，正式写入验证边界的文档/空间/线程/生命期及完整 UNDO 条件；边界不可用时在写入前拒绝，不通过关闭 UNDO 或清空用户历史绕过。
- 数据库提交与命令结束分开确认，只有原生命令正常结束才确认 undoGuaranteed。提交后命令收尾或选择失败保留成功事实、Handle 和映射，并单独 warning，避免 AI 重复创建。
- `send_code_to_cad` 保持能力优先：auto 提供数据库事务，none 自管；两者完整权限、CommandContext，不受固定实体白名单或 UNDO 关闭/One/外层组限制。用户代码仍可自行使用命令、事务和撤销标记。
- 动态 auto 不再额外套框架 ActiveX 撤销组，始终返回 undoGuaranteed=false 并附说明。普通动态绘图仍尽力支持逐次撤销；false 不代表执行失败或不能撤销。auto 提交后收尾错误不再误报回滚。

### 开发约束、测试与构建

- 新增 CONTRIBUTING、执行上下文 ADR-001/002/003、第二批工具契约、宿主验收清单与已知事项入口；明确后续开发不能合并读写路径，也不能将 none 当作只读。
- 增加纯逻辑矩阵/身份/预览/撤销生命期测试、真实 MCP Schema 和错误转发测试；CI 继续在 Node 24 下执行 Server 与不依赖 AutoCAD 的核心检查。
- 新增真实宿主回归/分组验收代码，以及带隔离说明的历史撤销探针与候选代码。探针曾因异常越过原生回调导致 CAD 崩溃，旧 Run 入口已隔离禁用；新测试要求回调内部捕获异常、新图显式激活、原生 U 等待完成且不重试未知结果。这些测试 DLL 均不随 Bundle 发布。
- 修复构建时 NuGet 缓存根路径没有末尾分隔符造成的 Unsafe 兼容 DLL 复制失败，使用 Path.Combine；不改变依赖版本或删减 Roslyn 多语言资源。
- 2.1.0 版本同步后：版本一致性、Node 19 项测试、.NET 124 项纯检查、Plugin/CommandSet Release 构建及正式宿主验收项目/分组验收项目编译通过。编译成功不等于已经安装 2.1.0 完成新的真实 CAD 验收。

### 真实宿主证据与覆盖边界

- 开发期产品版本为 2.0.0 的 bugfix2 已验证：200/1000/2000 对象复制/移动、映射、预览和中途失败回滚；16 类合成实体、带属性普通块及两个真实动态块；毫米/米/英寸/Unitless × 默认/旋转/倾斜 UCS；新工具连续原生 U，另有用户两次单独 U 验证。该阶段仍发现旧创建和动态 auto 的连续撤销问题，历史失败保留。
- 后续 bugfix3 专项：undo_policy 87/87、undo_conditions 51/51，共 138 条断言通过；两个成功运行报告含 230 次内部真实调度器调用、23 次原生 U 完成。覆盖四个创建工具连续/混合撤销、读/预览/选择穿插、回滚、提交后选择失败、动态 3D/原生命令/自管事务与撤销标记，以及 UNDO None/One/Begin 的两种策略。
- 这些内部调用不逐次经过完整 stdio/TCP 链路；MCP 启动/末尾核对、Server 自动测试与真实调度器回归应分别理解。不能把不同历史包的覆盖合并声称为 2.1.0 全量真实验收通过。
- 实际自动保存验证了原图路径/临时路径区分；原选中实体详情与磁盘时间核对无变化。测试期间原图 DBMOD 曾由 17 变为 21，未做触发源事件级定位，不宣称整场 DBMOD 不变或仅凭实体数认定全图零修改。
- 尚未穷尽：本次版本化后人工 Ctrl+Z、真实 7 MiB 边界、提交后原生命令收尾故障、全部超时/保存周期、所有第三方复杂块/外部依赖场景。过去的失败 JSON 和详细日志保留在本地 artifacts，不默认上传用户图纸、会话文件或原始审计内容。

### 升级与使用注意事项

1. 仍仅支持 Windows AutoCAD 2022 / .NET Framework 4.8，Server 最低 Node 24。完整更新 CADMCP.bundle 和 Server，重启 AutoCAD 与 MCP 客户端后手动开启服务；2.1.0 Server 会拒绝仍运行 2.0.0 的插件，即使协议都为 2。
2. 固定创建/复制/变换需要完整 UNDO，关闭、One、其他外层撤销组等条件会返回 undo_unavailable；这不影响动态代码按能力优先策略运行。
3. 动态 `undoGuaranteed=false` 不等于失败；none 的 `committed=false` 仅表示框架没有提交事务，不证明用户代码没有写入。auto 回滚不涵盖文件、网络等外部副作用。
4. timeout_unknown 要按 callId 查询最终状态。committed=true 后的收尾/选择 warning 不得触发重复写调用；不得自动重试结果未知的写入或 U。
5. 固定修改保守拒绝代理/三维/Xref/注释性/关联或持久反应器依赖目标，以及锁定/关闭/冻结图层和不可见对象；组或第三方关系也可能被拒绝。需要额外能力时可使用完整权限动态代码，但须自行考虑副作用与事务。
6. **DEP-001 暂缓，未修复**：冷启动时使用 C# dynamic 可能因未引用系统 Microsoft.CSharp.dll 而编译失败。可显式传本地 references；插件/Server 不会自动补依赖并重试，其他 AI 是否会主动补救不能保证。不是本次安装包漏带文件，不影响固定工具和不需要该绑定器的普通 C#。原因、临时办法和后续方案见 [待完善记录](docs/BACKLOG.md)。
7. 本次版本升级和推送只发布源代码提交，不创建 GitHub Release、不发布 npm、不自动安装 Bundle。保留旧测试包、Roslyn 资源及历史失败记录。
