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
