# 委托 Handler PostgreSQL 示例

[English](README.md) | **简体中文**

委托 Handler 适合把简短的批处理逻辑放在提交请求的代码附近。本示例在 Endpoint 的静态 Lambda 中完成批次去重和结果
回填，数据库操作放在 `PriceStore` 中。需要复用或独立测试的复杂逻辑可以改用单独的
`IRequestBatchHandler` 类，参考[类型化 Handler 示例](../RequestBatcher.Deduplication)。

本示例包含两条委托管线：

- `POST /prices` 通过未命名的 request-only 管线写入结构化 `PriceUpdate`。
- `POST /prices/query` 通过命名的 `price` response 管线读取 `int` 商品 ID，并按输入顺序返回 nullable 价格。

因为简单请求类型可能用于多个业务操作，原始 `int` 管线命名为 `price`。相同请求类型的其他操作应使用不同的
`batcherName`；省略名称表示默认管线。`BatchSize` 是上限，一次列表提交可以拆成多个 Handler 批次；去重只在当前批次内生效。
Partition 路由保证相同 Key 进入同一个 Partition，但不保证不同批次串行执行或 Handler 顺序。

## 注册

委托 Handler 始终是 Singleton，不需要 Handler 生命周期参数。每个实际批次会在独立的 DI Scope 中解析 Keyed `PriceStore`，
批次完成前释放该 Scope；数据库 DataSource 是应用 Singleton。

```csharp
services.AddSingleton<NpgsqlDataSource>(
    _ => new NpgsqlDataSourceBuilder(connectionString).Build());
services.AddKeyedScoped<PriceStore>("catalog");

services.AddDelegateRequestBatcher<PriceUpdate>(options =>
{
    options.BatchSize = 100;
    options.MaxConcurrency = 4;
    options.MaxPendingRequests = 10_000;
    options.UsePartitionKey(update => update.ProductId, partitionCount: 8);
});
services.AddDelegateRequestBatcher<int, decimal?>(
    batcherName: "price",
    configure: options =>
    {
        options.BatchSize = 100;
        options.MaxConcurrency = 4;
        options.MaxPendingRequests = 10_000;
        options.UsePartitionKey(id => id, partitionCount: 8);
    });
```

Handler 必须使用不捕获局部变量或实例状态的静态实现；依赖通过注入参数获取。内置 Analyzer 会检查已知的委托目标和签名。

## 运行与边界

从仓库根目录启动数据库，再启动本示例：

```bash
docker compose -f samples/RequestBatcher.Delegates/compose.yaml up -d
dotnet run --project samples/RequestBatcher.Delegates/RequestBatcher.Delegates.csproj
```

提交写入和查询：

```bash
curl --request POST http://localhost:5081/prices \
  --header "Content-Type: application/json" \
  --data '{"productId":101,"version":3,"price":17.90}'
curl --request POST http://localhost:5081/prices/query \
  --header "Content-Type: application/json" \
  --data '[101,102,101,999]'
```

最高版本在每个写入批次内选择，数据库版本条件保护并发批次的正确性。查询 lambda 只在当前批次内去重；缺失商品返回
`null`，重复 ID 保持重复。RequestBatcher 在进程内运行，进程故障后不会恢复已接收请求；数据库错误会传播到 HTTP 请求。

## 可观测性

安装 [`RequestBatcher.OpenTelemetry`](https://www.nuget.org/packages/RequestBatcher.OpenTelemetry) 及所需的
OpenTelemetry Hosting 和 Exporter 包后，在应用的 Tracing Provider 中调用 `AddRequestBatcherInstrumentation()`。
`RequestBatcher.Execute` 记录调用方完整等待，`RequestBatcher.Handle` 是独立的 Consumer Span，通过 Link 关联参与调用。
更多说明见[可观测性设计](../../docs/observability-design.md)。

如需对可观测性示例执行端到端验证，请查看[集成测试说明](../../tests/it/README.zh-CN.md)
（[English](../../tests/it/README.md)）。启用集成测试开关并确保 Docker 可用后，测试夹具会通过 Testcontainers
创建隔离环境，并在测试结束后自动清理。测试验证每个 Cart HTTP 请求只有一个 `Execute`、跨 Trace 的批次 Link、
Pricing/数据库父子 Span 和 Metrics。
