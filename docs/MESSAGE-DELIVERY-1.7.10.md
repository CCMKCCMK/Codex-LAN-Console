# 消息秒失败修复记录

部署：2026-09-04，Bridge 1.7.10；本机 app-server：codex-cli 0.153.1。

## 确认的原因

1. `thread/start` 返回的新线程，在首条用户消息开始前尚未落盘。
   桥在发送前查询 `thread/turns/list`，收到 `not materialized yet` 后把
   outbox 判为 failed，根本没有发送 `turn/start`。
2. 建线程后五秒自动 unsubscribe，会丢弃尚未落盘的空线程；后续 resume
   返回 `no rollout found`。现在空线程保留到第一轮启动，完成后仍自动释放。
3. 初次 `turn/start` 尚未确认时，Codex 短暂返回
   `-32601: paginated_threads is not supported yet`。只在已知新线程的短暂
   初始化窗口使用实时详情过渡，避免误报必须升级。

普通 RPC 错误以前没有写入 transport/API 异常日志。无新增日志不能证明没有 RPC；
outbox 中的 `lastError` 和 `requestWrittenAt` 是本次诊断的直接证据。
现在诊断包含 RPC method、request ID、command ID 和脱敏错误，手机可展开查看失败原因。

## 实测

最终 1.7.10 均达到 `delivered`、`completed` 并收到 `ok`：

| 测试 | 任务 ID | 总耗时 |
| --- | --- | --- |
| 新线程立即发送，最小参数 | 01a06e85-6bd0-7e72-ac5a-3ce60f228119 | 4.4 秒 |
| 完成并确认释放后再次发送 | 同上 | 4.8 秒 |
| 新线程等待七秒，全权限及 gpt-6-astra 覆盖 | 01a06e85-ad49-70a2-9b3b-47a0ebd5374f | 4.3 秒 |

启动期间并行读取手机详情成功。后端 224 项断言、相关前端 13 项测试通过。

浏览器独立实测任务 `01a06e81-3d3f-74c2-935b-d8846c0afa75` 已通过桥调用
`cua_repl/js` 打开 Chrome 的 example.com，返回标题和正文标题 `Example Domain`。
该浏览器实测在 1.7.9 上完成；1.7.10 追加了详情页初始化处理。

## item/tool/call 的边界

9 月 3 日的动态工具错误确实存在，但不是本次第一条消息秒失败的直接原因。
继承自桌面线程的动态工具可能需要桌面客户端自己的执行器。桥现在返回协议规定的
`success: false` 工具结果和明确说明，不再把它当作未实现的 JSON-RPC 方法或待审批。
这不等于为独立桥实现了所有桌面专属工具。实际 Chrome 测试走的是可用的 MCP 浏览器路径。
参考：[官方 app-server 动态工具协议](https://learn.chatgpt.com/docs/app-server)。

## 使用与复验

手机刷新到 1.7.10 后新建任务即可发送。此前已经释放、没有生成 rollout 的空线程
无法用原 ID 恢复，需新建任务；其失败指令仍在 outbox 保存，没有自动重放。

`scripts/Test-LiveMessageDelivery.ps1 -Live` 可复验三种发送及开始阶段的详情读取。
它创建两个测试任务并消耗三次短回复，需要有效的本机临时配对码；不会打印或保存 token。
