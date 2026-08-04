FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj Server/OnlyMyGame.Api/
COPY Assets/OnlyMyGame/Core/RuleCore.cs Assets/OnlyMyGame/Core/
RUN dotnet restore Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj
COPY Server/OnlyMyGame.Api/ Server/OnlyMyGame.Api/
RUN dotnet publish Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "OnlyMyGame.Api.dll"]
