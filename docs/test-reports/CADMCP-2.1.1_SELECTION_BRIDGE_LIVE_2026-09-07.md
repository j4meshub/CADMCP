# CADMCP 2.1.1 选择桥真实宿主回归（2026-09-07）

## 环境与范围

- AutoCAD 2022，用户安装 `CADMCP-2.1.1-selection-fix.bundle.zip` 后完全重启并手动开启服务。
- 活动测试图：`C:\Users\admin\Desktop\暂存\CAD AI测试\测试图.dwg`；模型空间。
- 初始实体数 11307，初始 DBMOD 17，初始预选为 CIRCLE `4C005D`、ARC `4C005B`、TEXT `4BFC4F`。
- 本轮目标是验证 2.1.0 中可重复触发 `acedSSSetFirst` / `AccessViolationException` 的框架选择路径，以及选择桥是否污染固定写入的撤销顺序。

## 已通过

1. `set_selection` 完成空选择到非空选择、非空 A 到相同 A、非空 A 到不同 B；随后 `get_selected_entities` 返回的 Handle 与请求集合一致，AutoCAD 未崩溃、服务连接保持正常。
2. 单次 `create_line` 原子创建四条测试线 `4C1FFE`–`4C2001` 后，将原有三个非空选择替换为这四个刚提交的实体；实际选择及几何复核一致。
3. 对这四条线执行 `clone_entities(selectCreated=true)`，生成 `4C2002`–`4C2005`；响应为 `committed=true`、`undoGuaranteed=true`、`selectionApplied=true`，实际预选完整切换到四个副本。
4. 第一次人工在 CAD 命令行执行 `U`，命令行显示 `EXECUTEFUNCTION`：实体数从 11315 降至 11311，四个副本 Handle 全部失效，四个源 Handle 及几何仍存在。
5. 第二次人工执行 `U`，命令行再次显示 `EXECUTEFUNCTION`：实体数从 11311 回到 11307，四个源测试线 Handle 全部失效。选择桥没有插入额外撤销步。
6. 失败原子性：恢复初始三个选择后，请求替换为已撤销的 `4C1FFE`，正确返回 `entity_not_found`；随后读取仍得到完整的三个原选择。
7. 测试结束时实体数和 DBMOD 均与本轮开始值相同，初始三个对象保持选中。

## 本轮未覆盖

- 未主动把 `PICKFIRST` 改为 0，未制造桥排队瞬间切换 Document/空间/用户选择的竞态。
- 未测试纸空间背景 Viewport、超过 2000 个选择目标或宿主拒绝恢复选择的异常分支。
- 未做长时间高频循环，也未在测试后单独读取 Windows 应用事件日志；本报告只陈述本轮进程未崩溃且 MCP 会话持续可用。
- `send_code_to_cad` 的动态选择能力不属于本次修复范围，本轮没有重新测试。

## 结论

2.1.1 的专用文档命令选择桥通过本轮关键真实宿主回归：此前的“已有非空选择 → 多个刚提交实体”崩溃场景未复现，`clone_entities(selectCreated=true)` 正常，且选择操作没有改变两次固定写入对应两次 `U` 的撤销顺序。未覆盖的竞态和异常注入项仍保留在完整 E2E 清单中，不因本次通过而视为已验证。
