# 可观测性集成测试

[English](README.md) | [简体中文](README.zh-CN.md)

这些 .NET 10 xUnit 测试通过真实 HTTP、PostgreSQL、OTLP 导出、Tempo 和 Prometheus 验证运行中的
[可观测性示例](../../samples/RequestBatcher.Observability/README.zh-CN.md)。参考
[EventHorizon.RocketMQ.EventBus 集成测试](https://github.com/eventhorizon-cli/EventHorizon.RocketMQ.EventBus/tree/main/tests/it)
的组织方式，集成测试与单元测试分开，并显式声明环境要求。启用 live 测试后，Testcontainers fixture 会创建
隔离网络，构建 Cart 和 Pricing 示例镜像，启动 PostgreSQL 17.6 与 OTEL-LGTM 0.29.0，并随机分配宿主机端口。
测试结束后会清理容器、镜像和网络，包括启动失败时的部分资源；Testcontainers 的 resource reaper 也会作为额外
清理保障。普通单元测试不依赖外部服务。

显式启用并运行集成测试：

```bash
REQUESTBATCHER_RUN_INTEGRATION_TESTS=1 dotnet test \
  tests/it/RequestBatcher.Observability.IntegrationTests/RequestBatcher.Observability.IntegrationTests.csproj \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/it/RequestBatcher.Observability.IntegrationTests/coverage.runsettings \
  --results-directory TestResults/Integration \
  --logger 'console;verbosity=detailed'
```

覆盖率配置只统计 `RequestBatcher` 和 `RequestBatcher.OpenTelemetry` 生产程序集，示例代码和测试程序集不会计入结果。
覆盖数据来自进程内压力测试；测试宿主的 collector 不会插桩示例容器内的代码，容器测试单独验证实际导出的行为。
CI 将该报告标记为 `integration` 上传，单元测试报告标记为 `unit`。

同一个项目还包含不依赖 Docker 和环境开关的进程内测试，它们使用公共 API 和真实 OpenTelemetry SDK：
`ExecuteAsync_ConcurrentWavesAtEachSamplingRate_ProcessesExactlyOnceAndLinksActualBatches` 在 0% 和 100% 采样下分别运行
32 轮、每轮 512 个请求（每种采样率共 16,384 个请求），混合单请求和 16 请求批量提交，handler 异步等待 1 毫秒，并在每轮
校验后清理 telemetry。这些基线轮次注册 `AddRequestBatcherInstrumentation()`，在应用 sampler 配置下验证 linked-batch 策略。
`ExecuteAsync_Instrumentation_MixedSamplingLinksAllParticipatingContexts` 在保留应用 sampler 配置时，验证混合批次指向所有参与的
Execute 上下文，包括未采样的上下文；Handle 是否导出则根据实际批次是否包含已采样 Execute 判断。
`ExecuteAsync_Instrumentation_SamplesBatchWithZeroRootRatio` 注册无参数的
`AddRequestBatcherInstrumentation()` 扩展，验证即使根采样率为 0%，已采样 Execute 对应的 Handler 批次仍被采样。
确定性单元测试覆盖扩展在所有 Execute 都未采样时丢弃批次，以及直接注册 source 时保留应用采样规则。
`DisposeAsync_QueuedCancellationAndHandlerFailure_DrainsAcceptedWorkAndRejectsLateSubmissions` 覆盖取消、
排空、失败和停止接收新提交。不设置 `REQUESTBATCHER_RUN_INTEGRATION_TESTS` 运行项目即可执行这些测试，且不会启动 live fixture。

未设置 `REQUESTBATCHER_RUN_INTEGRATION_TESTS=1` 时，容器测试报告为跳过，进程内测试仍会运行。项目已加入 `RequestBatcher.slnx`，
也可以在 Rider 中构建、运行。fixture 会向测试提供本次运行生成的 Cart 和 Grafana 地址，不需要固定端口环境变量。
如果需要手动查看示例，请使用示例 README 中独立的 Compose 步骤，并在结束后停止 Compose 环境。

测试分两轮发送 32 个并发 Cart 请求，每个请求使用独立的 W3C Trace 上下文，包含 2–10 个商品项，
并特意包含重复商品。检查内容包括：

- 响应顺序、数量、种子数据价格、重复商品回填和总价。
- 每个 Cart 请求只有一个 `RequestBatcher.Execute pricing-queries`，父节点是对应的 HTTP 服务端 span。
- `RequestBatcher.Handle pricing-queries` 是独立 Trace 的根节点，通过去重的 Links 指向调用方 `Execute`；每个 `Execute` 也有指向处理其请求的
  `Handle` 的 link；
  所有测试调用方都有对应 Link，且观察到至少一个包含多个调用方的批次。
- 批大小为 1–20 个**商品请求项**。一次 Cart 提交可以拆到多个批次，批次不必填满。
- Cart HTTP 客户端 → Pricing HTTP 服务端 → Npgsql 查询的父子关系，HTTP 成功状态，以及 Pricing 中没有 Batcher。
- 第二轮使用新的上下文，防止早先调用方污染后续 Trace。
- 五项 RequestBatcher 指标已导出，请求类型标签为短名 `Int32`，计数器与直方图 count/sum 持续增长。

示例新写入的 telemetry 使用 `service.name=cart` 和 `service.name=pricing`。dashboard 的 `Cart calls · Execute` 与
`Handler execution · Handle` 表格明确展示两种方向：`Handle` 指向调用方 `Execute`，`Execute` 指向处理其请求的 `Handle`。
可以从调用方找到批次，也可以从批次回到调用方。改名之前写入的
数据会继续使用旧服务名，直到后端保留期清理。

OTLP 导出、Tempo 索引和 Prometheus 抓取是异步的，所以每个验证阶段最多轮询 120 秒。
失败时会报告缺失的 Trace、Link 或指标证据；成功输出包含调用方和批次的 Trace ID，方便在 Grafana 查看。
链路检索仅匹配本次测试生成的 Trace ID，允许其他示例流量同时存在。指标是具名 Batcher 的累计值，
流量生成器也可能贡献其增长。测试使用示例中 ID 为 1–100、价格为 `id × 1.25` 的种子数据，不修改数据库内容。

| 环境变量 | 默认值 | 用途 |
| --- | --- | --- |
| `REQUESTBATCHER_RUN_INTEGRATION_TESTS` | 未设置 | 设为 `1` 启用真实环境测试。 |
fixture 使用示例默认的 Grafana 凭据（`admin` / `admin`），并为每次运行生成 Cart 和 Grafana 的宿主机地址。

测试通过 Grafana 的认证代理访问预置的 `requestbatcher-tempo` 和 `requestbatcher-prometheus` 数据源，
不需要暴露额外的后端端口，也不需要给生产代码增加测试专用接口。
