# PostgreSQL Web API Sample

This is a small RequestBatcher example. Concurrent `POST /prices` calls are collected into handler batches. The write
handler keeps the highest version for each product and writes its batch with one Dapper PostgreSQL upsert.

Concurrent `GET /prices/{productId}` calls are also batched. The query handler deduplicates product IDs, performs one
PostgreSQL query for the distinct IDs, and distributes each returned price to all matching callers.

`UsePartitionKey(update => update.ProductId)` keeps updates for one product in one processing partition. The database
only updates a row when its incoming version is newer, so stale updates are also rejected across handler batches.

## Run

Start PostgreSQL from the repository root:

```bash
docker compose -f samples/RequestBatcher.Deduplication/compose.yaml up -d
```

Run the API:

```bash
dotnet run --project samples/RequestBatcher.Deduplication/RequestBatcher.Deduplication.csproj
```

The API creates the `product_prices` table at startup. RequestBatcher options stay directly in `Program.cs`; the
PostgreSQL connection string and ASP.NET Core logging settings remain in `appsettings.json`.

## Endpoints

Submit one update:

```bash
curl --request POST http://localhost:5080/prices \
  --header "Content-Type: application/json" \
  --data '{"productId":101,"version":3,"price":17.90}'
```

Read the persisted price:

```bash
curl http://localhost:5080/prices/101
```

The API returns `204 No Content` only after the applicable handler batch has finished. Database failures propagate to
the HTTP request as a server error; RequestBatcher does not retry them automatically.
