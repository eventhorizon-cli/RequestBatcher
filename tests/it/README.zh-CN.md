# 可观测性集成测试

[English](README.md) | [简体中文](README.zh-CN.md)

这些 .NET 10 xUnit 测试通过真实 HTTP、PostgreSQL、OTLP 导出、Tempo 和 Prometheus 验证运行中的
[可观测性示例](../../samples/RequestBatcher.Observability/README.zh-CN.md)。参考
[EventHorizon.RocketMQ 集成测试](https://github.com/eventhorizon-cli/EventHorizon.RocketMQ/tree/main/tests/it)
的组织方式，集成测试与单元测试分开，并显式声明环境要求。它们复用示例的 Compose 环境，不自行启停或重置容器。
普通单元测试不依赖外部服务。

在仓库根目录执行以下命令，构建示例镜像、启动环境并等待健康检查通过：

```bash
docker compose -f samples/RequestBatcher.Observability/compose.yaml up --build -d --wait
```

显式启用并运行集成测试：

```bash
REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release --logger 'console;verbosity=detailed'
```

未设置 `REQUESTBATCHER_RUN_INTEGRATION_TESTS=1` 时，测试报告为跳过。项目已加入 `RequestBatcher.slnx`，
也可以在 Rider 中构建、运行。示例的流量生成器可以保持运行。

测试分两轮发送 32 个并发 Cart 请求，每个请求使用独立的 W3C Trace 上下文，包含 2–10 个商品项，
并特意包含重复商品。检查内容包括：

- 响应顺序、数量、种子数据价格、重复商品回填和总价。
- 每个 Cart 请求只有一个 `RequestBatcher.Execute pricing-queries`，父节点是对应的 HTTP 服务端 span。
- `RequestBatcher.ProcessBatch pricing-queries` 是独立 Trace 的根节点，通过去重的 Links 关联调用方 Execute；
  所有测试调用方都有对应 Link，且观察到至少一个包含多个调用方的批次。
- 批大小为 1–20 个**商品请求项**。一次 Cart 提交可以拆到多个批次，批次不必填满。
- Cart HTTP 客户端 → Pricing HTTP 服务端 → Npgsql 查询的父子关系，HTTP 成功状态，以及 Pricing 中没有 Batcher。
- 第二轮使用新的上下文，防止早先调用方污染后续 Trace。
- 五项 RequestBatcher 指标已导出，计数器与直方图 count/sum 持续增长。

OTLP 导出、Tempo 索引和 Prometheus 抓取是异步的，所以每个验证阶段最多轮询 120 秒。
失败时会报告缺失的 Trace、Link 或指标证据；成功输出包含调用方和批次的 Trace ID，方便在 Grafana 查看。
链路检索仅匹配本次测试生成的 Trace ID，允许其他示例流量同时存在。指标是具名 Batcher 的累计值，
流量生成器也可能贡献其增长。测试使用示例中 ID 为 1–100、价格为 `id × 1.25` 的种子数据，不修改数据库内容。

| 环境变量 | 默认值 | 用途 |
| --- | --- | --- |
| `REQUESTBATCHER_RUN_INTEGRATION_TESTS` | 未设置 | 设为 `1` 启用真实环境测试。 |
| `REQUESTBATCHER_CART_URL` | `http://127.0.0.1:5081` | Cart 地址，与 Compose 的 `CART_PORT` 覆盖值对应。 |
| `REQUESTBATCHER_GRAFANA_URL` | `http://127.0.0.1:3000` | Grafana 地址，与 `GRAFANA_PORT` 覆盖值对应。 |
| `REQUESTBATCHER_GRAFANA_USER` | `admin` | 本地 Grafana 用户名。 |
| `REQUESTBATCHER_GRAFANA_PASSWORD` | `admin` | 本地 Grafana 密码。 |

测试通过 Grafana 的认证代理访问预置的 `requestbatcher-tempo` 和 `requestbatcher-prometheus` 数据源，
不需要暴露额外的后端端口，也不需要给生产代码增加测试专用接口。
