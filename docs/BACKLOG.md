# 后续待完善事项

本文件记录已知限制与候选改进，不表示已经安排开发或验收通过。实施前重新核对现有代码，并遵守仓库开发约束。

## DEP-001：为动态 C# 自动准备 Microsoft.CSharp 基础引用

- 记录日期：2026-09-03。
- 状态：暂缓开发，按用户决定记录为后续待完善项。
- 发现版本：产品 2.0.0，second-batch-bugfix3 测试包。
- 本次处置：仅记录，不修改生产代码、版本、协议、设置或安装包。

### 现象、原因与影响范围

`send_code_to_cad` 支持 Roslyn 动态编译，但 C# 的 `dynamic` 成员访问另需 Microsoft.CSharp 运行时绑定器。当前引用收集器固定加入少量基础程序集，再收集宿主已加载程序集及请求的 `references`；没有保证绑定器总在默认编译引用中。

冷启动后，如果绑定器尚未加载，使用 `dynamic` 的代码可能返回 `compilation_failed`，诊断例如 CS0656：缺少 Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create。本次实际失败的编译引用清单有 98 项，没有 Microsoft.CSharp；系统 DLL 则确实存在，并非发现安装包漏带私有依赖。

- 已通过验收的固定工具不会因这项缺口停止工作。
- 不需要该绑定器的普通 C# 代码不受这项引用缺口影响；不能因此将全部动态代码称为不可用。
- 使用 `dynamic` 的任务可能中断，需 AI 或开发者明确补充引用；因此不能称为“完全不影响使用”。
- 插件及 Node Server **不会在报错后自动查找、补齐 DLL 并重新执行请求**。上次是测试 AI 读取诊断，修改测试请求后再次调用成功；这不保证其他 AI 客户端也会主动完成补救。
- 补充引用会加载程序集，同一 CAD 会话后续通常能通过已加载程序集扫描取得它；重启后不保证仍然可用。
- 旧撤销类自身曾使用 dynamic，执行它可能顺带加载绑定器；移除旧撤销路径后，潜在引用缺口更易暴露。没有做旧包同场景对照，不把这项推断当作历史全场景实测结论。

### 当前临时处理方法

可事先在需要该绑定器的请求中显式传 `references`，无需故意先触发一次错误。本机已经验证的路径示例：

```json
{
  "references": [
    "C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\Microsoft.CSharp.dll"
  ]
}
```

这只是请求的一部分，不替代 code、parameters 等原有字段；换机器时先确认本地运行时 DLL 路径。不要下载未知来源 DLL、把系统 DLL 随意复制进 Bundle，或重新引入旧 ActiveX 撤销边界来间接加载它。

如果已经引用了该程序集，不盲目再传另一路径：当前规则对同名程序集的不同路径也可能报 reference_conflict，应结合返回的 reference_manifest 检查实际已选路径。

仅针对明确处于编译阶段的本次缺引用失败，用户代码尚未进入执行，可以补充引用后重新提交。不得把这一补救泛化为对 timeout_unknown、runtime_exception 或 committed=true 请求的自动重试。

### 拟议完善方向（尚未实施）

1. 通过 `typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly` 定位当前运行环境绑定的程序集，显式加入默认基础引用；不硬编码电脑路径，不打包新的系统库副本。
2. 保持 Json、Roslyn、Unsafe 私有依赖及版本冲突策略；识别同一系统绑定器的重复引用/路径别名，不放松其他 DLL 的冲突规则。
3. 增加真正编译、加载、执行 dynamic 方法调用和属性访问的纯内存自检；不触碰图纸、选择、事务或撤销。
4. 区分核心 Roslyn 失败与仅绑定器能力异常。按能力优先原则，后者不应无故禁止仍能运行的普通 C#；需要相应状态、诊断和故障测试，不能只把新自检塞入现有总失败开关。
5. 不改变 auto/none、完整 API 权限、原生调度和撤销策略；实施时更新相关文档和测试，不因本事项自行修改产品版本或协议。

### 后续验收重点

- 新 CAD 会话不先调用任何工具，首条含 dynamic 的请求不传 references 就可成功；覆盖 auto/none。
- 纯内存方法/属性调用、原先 COM 调用样例、普通 C#、Json 参数和附加 DLL 均回归。
- 同一绑定器重复引用、不同路径别名和不兼容程序集冲突处理。
- 模拟绑定器不可用，核对普通 C# 能力和状态提示；不得操作系统 DLL 来制造故障。
- 连续绘图撤销、选择保持回归；不得用已手工补过绑定器的会话冒充冷启动验收。

### 代码与证据入口

- [编译引用收集器](../commandset/DynamicMetadataReferenceResolver.cs)
- [动态编译命令及现有自检](../commandset/DynamicCodeCommand.cs)
- [MCP 调用转发](../server/src/tools/register.ts)
- [临时引用验收样例](../tests/CADMCP.Acceptance/UndoConditions.cs)
- 本地历史实测：`artifacts/SECOND_BATCH_BUGFIX3_LIVE_2026-09-03.md` 及其引用的失败/成功 JSON。artifacts 默认被 Git 忽略；本文保留原因、影响和补救步骤，不依赖其他开发者能读到本地报告。
