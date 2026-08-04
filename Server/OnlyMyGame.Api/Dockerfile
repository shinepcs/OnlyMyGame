FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj Server/OnlyMyGame.Api/
COPY Assets/OnlyMyGame/Core/RuleCore.cs Assets/OnlyMyGame/Core/
COPY Assets/OnlyMyGame/Core/RuleExpressions.cs Assets/OnlyMyGame/Core/
COPY Assets/OnlyMyGame/Core/DynamicActionTargeting.cs Assets/OnlyMyGame/Core/
RUN dotnet restore Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj
COPY Server/OnlyMyGame.Api/ Server/OnlyMyGame.Api/
RUN dotnet publish Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends sqlite3 \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data \
    && chown "$APP_UID:0" /data \
    && chmod 0770 /data
EXPOSE 8080
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["dotnet", "OnlyMyGame.Api.dll"]
