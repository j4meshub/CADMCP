# 第一批固定工具：读取与选择

本批新增 `get_entity_details`、`set_selection`。不包含复制、改号、删除、图框识别或自动标注目标推断，也不改变产品、协议、设置 Schema 版本。

## 使用顺序与文档身份

1. 调用 `get_current_document_info`、`get_selected_entities` 或 `query_entities`，取得结果中的 `documentToken` 和 `activeSpaceHandle`。
2. 将这两个值原样传给新工具，同时传入所需实体的 Handle。
3. 如果得到 `document_mismatch` 或 `active_space_mismatch`，重新读取，不要绕过检查。不同 DWG 可存在相同 Handle。

`documentToken` 是加载中的 Document 实例标识，不是文件路径。另存为不主动改变该实例标识；关闭后重新打开或重启 AutoCAD 必须重新读取。只支持当前空间的顶层实体；块属性作为父块的子数据读取，其句柄不能直接用于本批工具的顶层选择或详情请求。

## get_entity_details

```json
{
  "documentToken": "从最近读取结果取得的 UUID",
  "activeSpaceHandle": "1F",
  "handles": ["1A2", "1A3"],
  "includeGeometry": true,
  "includeAttributes": true,
  "includeStyle": true
}
```

三个详情开关默认开启。句柄输入为 1–2000 项，大小写和前导零统一规范化，重复项按首次出现顺序去重。任一无效、已删除、非 Entity、非当前空间顶层句柄会使整个请求失败，不返回不完整的成功列表。

实体结果包含 Handle、类型、图层、颜色、线型、可见性、WCS 包围盒、几何/属性/样式和逐实体 `warnings`。兼容保留的 `objectId` 标注为 `runtimeDiagnosticOnly`，不能用于后续工具定位。

### 本批几何支持表

| 实体类型 | 字段范围 |
| --- | --- |
| LINE | WCS 起终点 |
| ARC | 圆心、半径、WCS 起终点、法向量、OCS 起止角 |
| CIRCLE | 圆心、半径、WCS 法向量 |
| LWPOLYLINE | WCS 顶点、bulge、起终宽、闭合状态和法向量 |
| SOLID | 四个定义顶点和法向量；不代表三维实体 3DSOLID |
| TEXT | 原值、位置、对齐点、旋转、高度、宽度因子、倾斜、镜像和对齐模式 |
| MTEXT | Contents、纯文本、位置、高度、宽度、旋转、WCS 方向与法向量、对齐 |
| INSERT | 插入点、实际块名/有效块名、动态块/Xref 标识、比例、旋转、块变换矩阵 |
| LEADER | 全部顺序顶点、箭头标志、箭头端点、关联注释句柄和法向量 |
| MLEADER | leader → line → vertices 层级、每条线头点、MText 内容；块内容只给警告，不递归展开 |
| DIMENSION | RadialDimension、DiametricDimension、AlignedDimension、RotatedDimension 的定义点、文字覆盖值、文字位置、测量值 |

老式 POLYLINE、角度/坐标/弧长/折弯半径尺寸、代理对象等尚未提供专门几何详情，返回 `geometry: null` 与 `unsupported_entity_type`。包围盒或个别详情读取失败也明确写入 warning，不把缺失数据伪装成完整几何。

块的 `attributes` 仅包含现有 AttributeReference（包含不可见引用），返回父块 Handle、属性 Handle、tag、文本、位置、常量标志及多行属性信息。`attributeScope: referencesOnly` 表示不枚举块定义中的常量 AttributeDefinition，不展开嵌套块或 Xref 内部对象。样式字段覆盖通用线宽/线型比例，以及 TEXT/MTEXT 文字样式和受支持尺寸/Leader 的尺寸样式标识。

所有位置和长度为 WCS/毫米；方向、法向量和缩放系数无量纲。角度用弧度，明确标记其 OCS/属性参考系；不要把圆弧的 OCS 角度误当作 WCS 方向角。MText 的 WCS `directionWcs` 可用于方向判断。块矩阵是 row-major 4×4，映射“块局部毫米坐标 → WCS 毫米坐标”，只有平移分量做长度换算。

尺寸 `measurement` 是几何测量长度，不是已渲染的标注字符串；`textOverride` 保留原始覆盖值，未对 DIMLFAC、格式控制码或字段进行语义推断。

单实体最多读取 20000 个顶点/属性；详情响应容量预算 7 MiB，为 8 MiB TCP 帧留出封装空间。超限返回 `result_too_large`，请分批读取或关闭对应详情；不静默截断。已有选择/查询的 `includeGeometry` 复用相同序列化，并有响应容量检查。

## set_selection

```json
{
  "documentToken": "从最近读取结果取得的 UUID",
  "activeSpaceHandle": "1F",
  "mode": "replace",
  "handles": ["1A2", "1A3"],
  "expectedCount": 2
}
```

- `replace`（默认）：替换为所给实体。
- `add`：在初始选择中追加，去重。
- `remove`：从初始选择中移除；所给句柄仍必须是有效实体，即使不在初始选择中。
- `clear`：清空；`handles` 必须省略或为空。
- 其他模式要求非空 handles，输入和最终结果最多 2000 个。
- `expectedCount` 校验**最终去重后的选择数量**，不是传入句柄数量。例如原选 2 个，追加 1 个新对象，应填 3。

结果返回 `previousHandles`、`selectedHandles`、`count`、文档和空间标识。先验证所有目标，再在 AutoCAD 应用上下文应用最终选择，并核对实际集合。失败时恢复原选择；若 AutoCAD 无法恢复，明确返回警告。关闭/冻结图层或不可见实体不能加入最终选择；不会自动开图层、改变 PICKFIRST、解锁或修改实体。

本工具仅使用只读事务验证实体，不创建数据库写入事务或 UNDO 单元，成功时 `committed`、`rolledBack`、`undoGuaranteed` 均为 false（它们描述数据库行为，不代表选择工具失败）。不保存 DWG。

常见错误：`invalid_parameters`、`document_mismatch`、`active_space_mismatch`、`entity_not_found`、`wrong_entity_type`、`entity_outside_active_space`、`entity_not_selectable`、`count_mismatch`、`selection_failed`。仍保留 `cad_busy`、`tool_disabled`、超时与状态查询行为。

## 测试与升级

- Node 自动测试覆盖真实 MCP 工具注册与参数导出、模式校验、TCP 转发、禁用和错误返回。
- `dotnet run --project tests/CADMCP.Core.Tests -c Release` 运行与插件共用源码的句柄/选择规则和设置归一化测试，无 AutoCAD 依赖。
- 真正的实体读数、CAD 高亮选择、DBMOD、UCS 和动态代码回归仍须按 [宿主验收清单](AUTOCAD_E2E_CHECKLIST.md) 在 AutoCAD 2022 中验证。
- 成套更新 Bundle 和本地 Server，重启 AutoCAD 和 MCP 连接，刷新工具列表至 12 项。设置文件保留原工具禁用偏好，新工具默认启用。
