# cadmcp-server

CADMCP 的通用 stdio MCP 服务器。它读取本机 AutoCAD 2022 插件生成的临时会话文件，通过仅绑定 `127.0.0.1` 的认证 TCP 连接调用当前活动 DWG。

使用前必须在 AutoCAD 的 `CADMCP` Ribbon 手动开启服务。动态 C# 拥有 AutoCAD 进程的完整权限，只应在个人受信本地环境使用。完整说明见仓库根目录 README。
