FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Restaurar primero (cachea las dependencias entre builds)
COPY ["BirvennApi.csproj", "./"]
RUN dotnet restore "BirvennApi.csproj"

COPY . .
RUN dotnet publish "BirvennApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /app/publish .

# PORT (Render, Cloud Run) tiene prioridad sobre ASPNETCORE_HTTP_PORTS; ver Program.cs.
# TrustProxy: estas plataformas ponen un proxy delante y mandan la IP real en X-Forwarded-For.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Network__TrustProxy=true

EXPOSE 8080

# La imagen trae un usuario sin privilegios (APP_UID)
USER $APP_UID

ENTRYPOINT ["dotnet", "BirvennApi.dll"]
