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
