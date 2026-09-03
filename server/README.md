# cadmcp-server

CADMCP 的通用 stdio MCP 服务器。它读取本机 AutoCAD 2022 插件生成的临时会话文件，通过仅绑定 `127.0.0.1` 的认证 TCP 连接调用当前活动 DWG。

使用前必须在 AutoCAD 的 `CADMCP` Ribbon 手动开启服务。动态 C# 拥有 AutoCAD 进程的完整权限，只应在个人受信本地环境使用。完整说明见仓库根目录 README。

固定创建/复制/变换采用严格的一次撤销机制，无法满足完整 UNDO 条件时写入前拒绝。`send_code_to_cad` 能力优先：auto 提供事务、none 自管，两者都不承诺严格撤销（undoGuaranteed=false 不代表调用失败或不能撤销），也不因 UNDO 关闭/One/已有组而被框架禁止执行。成功提交后的 warning 不应触发自动重试。
