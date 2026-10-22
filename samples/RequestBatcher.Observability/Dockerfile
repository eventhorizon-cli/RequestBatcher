FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS source
WORKDIR /src
COPY . .

FROM source AS pricing-build
RUN dotnet restore samples/RequestBatcher.Observability/Pricing/RequestBatcher.Observability.Pricing.csproj
RUN dotnet publish samples/RequestBatcher.Observability/Pricing/RequestBatcher.Observability.Pricing.csproj --configuration Release --framework net10.0 --no-restore -o /app/publish /p:UseAppHost=false

FROM source AS cart-build
RUN dotnet restore samples/RequestBatcher.Observability/Cart/RequestBatcher.Observability.Cart.csproj
RUN dotnet publish samples/RequestBatcher.Observability/Cart/RequestBatcher.Observability.Cart.csproj --configuration Release --framework net10.0 --no-restore -o /app/publish /p:UseAppHost=false

FROM source AS healthcheck-build
RUN dotnet publish samples/RequestBatcher.Observability/CheckHealth.cs --configuration Release -o /app/healthcheck /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS pricing
WORKDIR /app
COPY --from=pricing-build /app/publish .
COPY --from=healthcheck-build /app/healthcheck /healthcheck
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RequestBatcher.Observability.Pricing.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS cart
WORKDIR /app
COPY --from=cart-build /app/publish .
COPY --from=healthcheck-build /app/healthcheck /healthcheck
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RequestBatcher.Observability.Cart.dll"]

FROM source AS traffic-generator-build
RUN dotnet restore samples/RequestBatcher.Observability/TrafficGenerator/RequestBatcher.Observability.TrafficGenerator.csproj
RUN dotnet publish samples/RequestBatcher.Observability/TrafficGenerator/RequestBatcher.Observability.TrafficGenerator.csproj --configuration Release --framework net10.0 --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS traffic-generator
WORKDIR /app
COPY --from=traffic-generator-build /app/publish .
ENTRYPOINT ["dotnet", "RequestBatcher.Observability.TrafficGenerator.dll"]
