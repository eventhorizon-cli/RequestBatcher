# PostgreSQL Web API 示例

[English](README.md) | **简体中文**

本示例通过 RequestBatcher 使用 [BufferQueue](https://github.com/eventhorizon-cli/BufferQueue)。Push 通知会唤醒消费流程；
消费流程在取得执行名额后拉取当前已排队的请求，没有固定的攒批时间窗口。`BatchSize` 只是每个 Handler 批次的上限，
`MaxConcurrency` 限制进程内并发批次数，`partitionCount` 控制按键路由。

这是一个小型的 RequestBatcher 示例。并发 `POST /prices` 请求会合并到 Handler 批次中；写入 Handler 在每个批次内按
商品选择最高版本，并使用一次 Dapper PostgreSQL upsert。并发 `GET /prices/{productId}` 请求通过命名的 `price` 管线批处理，
查询 Handler 接收 `int` 商品 ID 并返回 `decimal?` 价格。`POST /prices/query` 接收 JSON `int[]`，按输入顺序返回 nullable 价格。

查询 Handler 使用 `Distinct()` 在当前批次内去重，再通过 `entries.SetResponses` 为重复请求回填同一结果。不同 Handler 批次
可能再次查询同一商品；一次列表提交也可能拆分为多个批次。缺失商品返回 `null`，数据库行顺序不影响结果。

`UsePartitionKey(update => update.ProductId, partitionCount: 8)` 会把相同商品 ID 路由到同一个 Partition，同时允许不同 ID 独立推进。
路由不保证 Handler 顺序，也不会把相同 Key 的不同批次串行化。写入数据库时的版本条件负责跨批次避免旧版本覆盖新版本。

查询管线通过 `batcherName: "price"` 注册，与未命名的结构化 `PriceUpdate` 写入管线相互独立：

```csharp
services.AddRequestBatcher<int, decimal?, PriceQueryHandler>(
    batcherName: "price",
    handlerLifetime: ServiceLifetime.Scoped,
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.UsePartitionKey(id => id, partitionCount: 8);
    });

var batcher = serviceProvider
    .GetRequiredKeyedService<IRequestBatcher<int, decimal?>>("price");
var prices = await batcher.ExecuteAsync(new[] { 101, 102, 101 });
```

## 运行

从仓库根目录启动 PostgreSQL，再启动 API：

```bash
docker compose -f samples/RequestBatcher.Deduplication/compose.yaml up -d
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```

API 启动时会创建 `product_prices` 表。连接字符串和日志配置在 `appsettings.json` 中，RequestBatcher 配置在 `Program.cs` 中。

提交写入并查询：

```bash
curl --request POST http://localhost:5080/prices \
  --header "Content-Type: application/json" \
  --data '{"productId":101,"version":3,"price":17.90}'
curl http://localhost:5080/prices/101
curl --request POST http://localhost:5080/prices/query \
  --header "Content-Type: application/json" \
  --data '[101,102,101,999]'
```

仅商品 `101` 存在时，列表响应为 `[17.90,null,17.90,null]`。成功写入返回 `204 No Content`；查询请求会等待对应 Handler
批次完成。数据库错误会传播为 HTTP 服务错误，RequestBatcher 不会自动重试，进程故障后也不会恢复内存中的请求。

## 可观测性

应用安装 [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) 及所需的
OpenTelemetry Hosting 和 Exporter 包后，可以在 `WithTracing` 中调用
`AddRequestBatcherInstrumentation()`。`RequestBatcher.Execute` 记录调用方完整等待时间；每个实际批次的
`RequestBatcher.Handle` 是独立的 Consumer Span，通过 ActivityLink 关联进入该批次的调用。详见
[可观测性设计](../../docs/observability-design.md) 和[关联图](../../docs/diagrams/observability.zh-CN.svg)。

如需对可观测性示例执行端到端验证，请查看[集成测试说明](../../tests/it/README.zh-CN.md)
（[English](../../tests/it/README.md)）。启用集成测试开关并确保 Docker 可用后，测试夹具会通过 Testcontainers
创建隔离环境，并在测试结束后自动清理。测试验证每个 Cart HTTP 请求只有一个 `Execute`、跨 Trace 的批次 Link、
Pricing/数据库父子 Span 和 Metrics。
