#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";

// 创建服务器实例
const server = new McpServer({
  name: "sinotech-mcp-server",
  version: "1.0.0",
});

// 启动服务器
async function main() {
  // 注册工具
  await registerTools(server);

  // 连接到传输层
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Sinotech MCP Server start success");
}

main().catch((error) => {
  console.error("Error starting Sinotech MCP Server:", error);
  process.exit(1);
});
