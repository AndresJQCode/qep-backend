# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
