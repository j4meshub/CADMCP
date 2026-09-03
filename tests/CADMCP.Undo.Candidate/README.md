# 同源撤销边界候选（仅诊断）

本项目直接链接原 commandset 修改代码，独立实现候选 StrictUndoBoundary：只接受已验证的命令上下文，并保留 UNDOCTL 检查，不在命令内额外开启 ActiveX 撤销组。用于比较机制，不是正式插件构建，不随Bundle发布。

编译符号 UNDO_CANDIDATE 使诊断器只替换自己新建注册表中的 clone/transform；不修改运行中的TCP服务或已安装DLL。RunCurrent 前必须先执行 safety_mismatch；旧 Run 仍禁用。

2026-09-03 已通过：无读取/含读取预览的连续两次移动、两批复制逐次U、注入第二实体异常回滚、selectCreated和preview。正式框架边界所有权与完整宿主回归仍待实施，不能直接把此DLL复制到插件目录。

后续正式修复已加入框架边界所有权。本项目链接的是工作区源码，重新构建后会使用新所有权检查，不再等同于上面历史实测的候选二进制。历史结论与原始 JSON 保持不变；新包验收使用 CADMCP.AutoCAD.Tests，不应把重编译候选当成新的 A/B 实验证据。
