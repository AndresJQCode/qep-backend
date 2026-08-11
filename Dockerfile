# syntax=docker/dockerfile:1

# El tag va fijo a la banda que pide global.json, no al flotante 10.0. global.json pide
# 10.0.301 con rollForward "latestPatch", que sólo rueda dentro de la misma banda de feature:
# de 10.0.3xx a 10.0.3xx sí, de 3xx a 4xx no. Cuando el tag 10.0 se movió a 10.0.400 el build
# empezó a fallar con "SDK not found" y exit 155, sin que nadie tocara este repositorio.
# Se fija la imagen en vez de aflojar global.json a propósito: global.json existe para que
# todos compilen con el mismo SDK, y la máquina del developer tiene 10.0.301. Al subir de
# banda se cambian los dos, juntos.
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

# .editorconfig is build input: it marks EF migrations as generated code, so the
# analyzers stay off them under TreatWarningsAsErrors.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/ src/

RUN dotnet restore src/Api/Api.csproj --locked-mode
RUN dotnet publish src/Api/Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Kubernetes deployment/service target containerPort 8080 (k8s/prod-deployment.yaml).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# "app" is the non-root user pre-created in the official ASP.NET Core image.
USER app
ENTRYPOINT ["dotnet", "Api.dll"]
